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
  /// What the native view draws, asked on its render thread once a frame: it brings the renderer up to date (models,
  /// instances, bones) and says through which camera and light the frame is seen.
  /// </summary>
  public interface IRenderScene
  {
    /// <summary>
    /// Before a frame (render thread): <paramref name="now"/> the view's clock in seconds, the picture's size in pixels.
    /// </summary>
    (Matrix4x4 ViewProjection, Vector3 ToLight, float Ambient) Update(MeshRenderer renderer, double now, int width, int height);
  }

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
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly object _lock = new object();
    // Taken by the render thread at the next frame.
    private int _width, _height;
    private float _scaleX, _scaleY;
    private bool _sizeChanged, _scaleChanged;

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
      _renderer = new MeshRenderer(_device, PanelSwapChain.BackBufferFormat);

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
        while (!_stop)
        {
          // At most two frames queued: wait until the swap chain takes the next one.
          WaitForSingleObjectEx(_swapChain.WaitableObject, 1000, true);
          if (_stop)
          {
            break;
          }
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
          var (viewProjection, toLight, ambient) = _scene.Update(_renderer, now, _swapChain.Width, _swapChain.Height);
          var list = _swapChain.Begin();
          _renderer.Draw(list, _swapChain.RenderTargetView, _swapChain.DepthView, _swapChain.Width, _swapChain.Height, viewProjection, toLight, ambient);
          _swapChain.End();
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
            D3D12Device.Log(string.Format(CultureInfo.InvariantCulture, "{0:0} fps · {1:0.0} ms, max {2:0.0} ms · {3} draws · {4}x{5}",
                                          frames / (after - secondStart), sum / frames * 1000, longest * 1000, draws / frames,
                                          _swapChain.Width, _swapChain.Height));
            _device.FlushMessages();
            secondStart = after;
            sum = longest = 0;
            draws = 0;
            frames = 0;
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
      _renderer.Dispose();
      _swapChain.Dispose();
      _device.Dispose();
    }
  }
}
