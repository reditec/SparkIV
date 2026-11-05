/**********************************************************************\

 SparkIV
 Copyright (C) 2026  reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

 You should have received a copy of the GNU General Public License
 along with this program.  If not, see <http://www.gnu.org/licenses/>.

\**********************************************************************/

using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The native renderer in a <see cref="SwapChainPanel"/> (#112): device, swap chain and renderer, drawing an
  /// <see cref="IRenderScene"/> on a thread of its own that waits on the swap chain's waitable object, so frames go out
  /// at the screen's refresh rate and the UI thread stays free. Size and DPI changes come from the UI thread and are
  /// taken at the next frame. Once a second the frames per second, the mean and longest frame time and the draw calls
  /// go to SparkIV-d3d12.log.
  /// </summary>
  public sealed class D3D12View : IDisposable
  {
    /// <summary>The switch for now: SPARKIV_D3D12=1 draws the cutscene view natively, else the WebView2 page as before.</summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SPARKIV_D3D12") == "1";

    private readonly SwapChainPanel _panel;
    private readonly IRenderScene _scene;
    private readonly Action<string> _failed;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly D3D12Device _device;
    private readonly PanelSwapChain _swapChain;
    private readonly MeshRenderer _renderer;
    private readonly ToneMapper _toneMapper;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly object _lock = new object();
    // Taken by the render thread at the next frame.
    private int _width, _height;
    private float _scaleX, _scaleY;
    private bool _sizeChanged, _scaleChanged;
    private double _lastMeasure = double.NegativeInfinity;
    private readonly FramePacer _pacer = new FramePacer();
    private readonly System.Runtime.GCLatencyMode _latencyMode;

    /// <summary>Starts on the UI thread, once the panel is loaded; <paramref name="failed"/> hears (on the UI thread) why drawing stopped.</summary>
    public D3D12View(SwapChainPanel panel, IRenderScene scene, Action<string> failed)
    {
      _panel = panel;
      _scene = scene;
      _failed = failed;
      _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
      _scaleX = panel.CompositionScaleX;
      _scaleY = panel.CompositionScaleY;
      (_width, _height) = Pixels();

      _device = new D3D12Device();
      _swapChain = new PanelSwapChain(_device, panel, _width, _height, _scaleX, _scaleY);
      D3D12Device.Log("swap chain: at most " + _swapChain.MaximumFrameLatency + " frame(s) queued");
      _renderer = new MeshRenderer(_device, ToneMapper.LightFormat);
      _toneMapper = new ToneMapper(_device, PanelSwapChain.BackBufferFormat);
      // SPARKIV_D3D12_PROFILE=1: per pass GPU and CPU times, once a second into the log (see GpuProfiler).
      if (GpuProfiler.EnabledInApp)
      {
        _device.StartProfiler();
        D3D12Device.Log("profiling on (SPARKIV_D3D12_PROFILE=1)");
      }

      // No blocking full garbage collections while the view draws where they can be avoided (a location's load allocates
      // gigabytes on other threads; a blocking gen-2 collection stops the render thread too). Restored on Dispose.
      _latencyMode = System.Runtime.GCSettings.LatencyMode;
      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
      panel.SizeChanged += Panel_SizeChanged;
      panel.CompositionScaleChanged += Panel_CompositionScaleChanged;
      _thread = new Thread(RenderLoop) { IsBackground = true, Name = "SparkIV D3D12" };
      _thread.Start();
    }

    public string AdapterName => _device.AdapterName;

    private (int Width, int Height) Pixels() =>
      ((int)Math.Round(_panel.ActualWidth * _scaleX), (int)Math.Round(_panel.ActualHeight * _scaleY));

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
      lock (_lock)
      {
        (_width, _height) = Pixels();
        _sizeChanged = true;
      }
    }

    private void Panel_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
      lock (_lock)
      {
        _scaleX = sender.CompositionScaleX;
        _scaleY = sender.CompositionScaleY;
        (_width, _height) = Pixels();
        _scaleChanged = _sizeChanged = true;
      }
    }

    private void RenderLoop()
    {
      var clock = Stopwatch.StartNew();
      double last = 0, secondStart = 0, longest = 0, sum = 0;
      long draws = 0;
      int frames = 0;
      try
      {
        var profiler = _device.Profiler;
        int pipelinedFrames = 0;
        double cpuSum = 0, gpuSum = 0;
        while (!_stop)
        {
          // At most MaximumFrameLatency frames queued (1): wait until the swap chain takes the next one - serial: here,
          // before the frame is updated (the input read as late as possible, shown at the next refresh); pipelined:
          // before Present, so this frame is updated and recorded while the GPU still draws the last (FramePacer).
          bool pipelined = _pacer.Pipelined;
          long stage = profiler != null ? GpuProfiler.Now : 0;
          if (!pipelined)
          {
            WaitForSingleObjectEx(_swapChain.WaitableObject, 1000, true);
            profiler?.CpuSince("wait (frame latency, VSync)", stage);
          }
          if (_stop)
          {
            break;
          }
          long cpuStart = Stopwatch.GetTimestamp();
          lock (_lock)
          {
            if (_scaleChanged)
            {
              _swapChain.SetScale(_scaleX, _scaleY);
              _scaleChanged = false;
            }
            if (_sizeChanged)
            {
              _swapChain.Resize(_width, _height);
              _sizeChanged = false;
            }
          }

          double now = clock.Elapsed.TotalSeconds;
          if (profiler != null)
          {
            stage = GpuProfiler.Now;
          }
          var view = _scene.Update(_renderer, now, _swapChain.Width, _swapChain.Height);
          profiler?.CpuSince("scene update", stage);
          _toneMapper.Resize(_swapChain.Width, _swapChain.Height);
          // The eye adapts to the last measure read (a few frames old): gently while playing, at once when stopped.
          _toneMapper.ReadMeasure(wait: false);
          _toneMapper.Adapt(view.Tone, view.Playing, last > 0 ? now - last : 0);
          var list = _swapChain.Begin();
          if (profiler != null)
          {
            stage = GpuProfiler.Now;
          }
          _toneMapper.BeginLight(list);
          _scene.DrawBackground(_device, list, _toneMapper.LightView, _swapChain.Width, _swapChain.Height, view);
          _renderer.Draw(list, _toneMapper.LightView, _swapChain.DepthView, _swapChain.Width, _swapChain.Height, view.ViewProjection, view.Eye, view.Light,
                         l => _scene.DrawAfterSolid(_device, l, _toneMapper.LightView, _swapChain.DepthView, _swapChain.Width, _swapChain.Height, view, _swapChain.DepthBuffer),
                         view.Lights, _toneMapper.LightTexture, view.Coronas, _swapChain.DepthBuffer, view.Volumes,
                         view.ParabolicHaze != null ? (l, t, n) => _scene.DrawParabolicSky(_device, l, t, n, view) : null, view.ParabolicHaze ?? 0);
          _toneMapper.EndLight(list);
          // Measured at most every 200 ms (as the page), else when the last measure is read.
          bool measured = now - _lastMeasure >= 0.2 && _toneMapper.Measure(list);
          if (measured)
          {
            _lastMeasure = now;
          }
          _toneMapper.Composite(list, _swapChain.RenderTargetView, _swapChain.Width, _swapChain.Height, view.Tone, view.Post, _swapChain.DepthBuffer);
          profiler?.CpuSince("recording", stage);
          ulong mark = _swapChain.Submit();
          double cpuMs = (Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency;
          if (pipelined)
          {
            stage = profiler != null ? GpuProfiler.Now : 0;
            WaitForSingleObjectEx(_swapChain.WaitableObject, 1000, true);
            profiler?.CpuSince("wait (frame latency, VSync)", stage);
          }
          _swapChain.Present();
          _pacer.Record(cpuMs, _swapChain.LastGpuMs, clock.Elapsed.TotalSeconds);
          pipelinedFrames += pipelined ? 1 : 0;
          cpuSum += cpuMs;
          gpuSum += _swapChain.LastGpuMs;
          if (profiler != null)
          {
            profiler.Count("draws (main view and maps)", _renderer.DrawCalls);
            profiler.Count("draws (water's mirror)", _renderer.MirrorDrawCalls);
            profiler.FrameSent(mark);
          }
          if (measured)
          {
            _toneMapper.MeasureSent(mark);
          }
          draws += _renderer.DrawCalls;

          // Frame times: from present to present (the scene's updates, which may load, count too).
          double after = clock.Elapsed.TotalSeconds;
          if (last > 0)
          {
            double frame = after - last;
            sum += frame;
            longest = Math.Max(longest, frame);
            frames++;
          }
          last = after;
          if (after - secondStart >= 1 && frames > 0)
          {
            D3D12Device.Log(string.Format(CultureInfo.InvariantCulture, "{0:0} fps · {1:0.0} ms, max {2:0.0} ms · {3} draws · {4}x{5} · {6}",
                                          frames / (after - secondStart), sum / frames * 1000, longest * 1000, draws / frames,
                                          _swapChain.Width, _swapChain.Height, pipelinedFrames * 2 >= frames ? "pipelined" : "serial"));
            if (profiler != null)
            {
              // How much the CPU's and the GPU's parts overlapped: their sum a frame against the frame's time.
              double cpu = cpuSum / frames, gpu = gpuSum / frames, frameMs = sum / frames * 1000;
              D3D12Device.Log(string.Format(CultureInfo.InvariantCulture,
                "pacing: {0} of {1} frames pipelined, refresh {2:0.00} ms; CPU (update to submit) {3:0.00} ms + GPU {4:0.00} ms = {5:0.00} ms serial, frame {6:0.00} ms, overlapped {7:0.00} ms; costliest CPU + GPU of the last {8}: {9:0.00} ms",
                pipelinedFrames, frames, _pacer.PeriodMs, cpu, gpu, cpu + gpu, frameMs, Math.Max(0, cpu + gpu - frameMs), FramePacer.Window, _pacer.SerialCostMs));
              D3D12Device.Log(profiler.Report(p95: false));
              profiler.Reset();
            }
            _device.FlushMessages();
            secondStart = after;
            sum = longest = 0;
            draws = 0;
            frames = 0;
            pipelinedFrames = 0;
            cpuSum = gpuSum = 0;
          }
        }
      }
      catch (Exception ex)
      {
        _device.FlushMessages();
        D3D12Device.Log("render thread stopped: " + ex);
        _dispatcher.TryEnqueue(() => _failed("D3D12 error: " + ex.Message));
      }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, bool alertable);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>On the UI thread: stops the render thread, the GPU done, then everything released.</summary>
    public void Dispose()
    {
      _panel.SizeChanged -= Panel_SizeChanged;
      _panel.CompositionScaleChanged -= Panel_CompositionScaleChanged;
      _stop = true;
      _thread.Join(5000);
      PanelSwapChain.ClearPanel(_panel);
      CloseHandle(_swapChain.WaitableObject);
      _device.WaitIdle();
      (_scene as IDisposable)?.Dispose();
      _renderer.Dispose();
      _toneMapper.Dispose();
      _swapChain.Dispose();
      _device.Dispose();
      System.Runtime.GCSettings.LatencyMode = _latencyMode;
    }
  }
}
