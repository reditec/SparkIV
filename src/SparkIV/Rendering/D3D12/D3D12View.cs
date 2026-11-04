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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SparkIV.WinUI.Viewers;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The native renderer in a <see cref="SwapChainPanel"/> (#112 step 1): device, swap chain and renderer, drawn on a
  /// thread of its own that waits on the swap chain's waitable object, so frames go out at the screen's refresh rate
  /// and the UI thread stays free (loading the cast there doesn't stall the view). Size and DPI changes come from the
  /// UI thread and are taken at the next frame. Once a second the frames per second and the mean and longest frame
  /// time go to <c>fpsChanged</c> (on the UI thread) and to SparkIV-d3d12.log.
  /// </summary>
  public sealed class D3D12View : IDisposable
  {
    /// <summary>The switch for now: SPARKIV_D3D12=1 draws the cutscene view natively, else the WebView2 page as before.</summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SPARKIV_D3D12") == "1";

    private readonly SwapChainPanel _panel;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _fpsChanged;
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
    private ModelScene? _model;

    /// <summary>Starts on the UI thread, once the panel is loaded.</summary>
    public D3D12View(SwapChainPanel panel, Action<string> fpsChanged)
    {
      _panel = panel;
      _dispatcher = DispatcherQueue.GetForCurrentThread();
      _fpsChanged = fpsChanged;
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

    /// <summary>The model to draw (put onto the GPU by the render thread).</summary>
    public void SetModel(ModelScene scene)
    {
      lock (_lock)
      {
        _model = scene;
      }
    }

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

          ModelScene? model;
          lock (_lock)
          {
            model = _model;
            _model = null;
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
          if (model != null)
          {
            var load = Stopwatch.StartNew();
            _renderer.Clear();
            int number = _renderer.AddModels(new[] { model })[0];
            _renderer.AddInstance(number, Matrix4x4.Identity);
            if (_renderer.Bounds(number) is var (min, max))
            {
              _center = (min + max) * 0.5f;
              _radius = Math.Max(0.1f, (max - min).Length() * 0.5f);
            }
            D3D12Device.Log($"model: {model.Meshes.Count} meshes, {_renderer.Triangles} triangles, on the GPU in {load.Elapsed.TotalMilliseconds:0.0} ms");
          }

          double now = clock.Elapsed.TotalSeconds;
          var list = _swapChain.Begin();
          _renderer.Draw(list, _swapChain.RenderTargetView, _swapChain.DepthView, _swapChain.Width, _swapChain.Height,
                         OrbitViewProjection((float)(now * 0.4)), new Vector3(0.4f, -0.5f, 0.8f), 0.35f);
          _swapChain.End();

          // Frame times: from present to present.
          if (last > 0)
          {
            double frame = now - last;
            sum += frame;
            longest = Math.Max(longest, frame);
            frames++;
          }
          last = now;
          if (now - secondStart >= 1 && frames > 0)
          {
            double fps = frames / (now - secondStart);
            string text = string.Format(CultureInfo.InvariantCulture, "{0:0} fps (D3D12) · {1:0.0} ms, max {2:0.0} ms",
                                        fps, sum / frames * 1000, longest * 1000);
            D3D12Device.Log(text + $" · {_swapChain.Width}x{_swapChain.Height}");
            _device.FlushMessages();
            _dispatcher.TryEnqueue(() => _fpsChanged(text));
            secondStart = now;
            sum = longest = 0;
            frames = 0;
          }
        }
      }
      catch (Exception ex)
      {
        _device.FlushMessages();
        D3D12Device.Log("render thread stopped: " + ex);
        _dispatcher.TryEnqueue(() => _fpsChanged("D3D12 error: " + ex.Message));
      }
    }

    // The prototype's camera: going slowly round the model (Z up, as in GTA).
    private Vector3 _center;
    private float _radius = 1;

    private Matrix4x4 OrbitViewProjection(float angle)
    {
      float distance = _radius * 2.6f;
      var eye = _center + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, _radius * 0.6f);
      var view = Matrix4x4.CreateLookAt(eye, _center, Vector3.UnitZ);
      var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, _swapChain.Width / (float)_swapChain.Height,
                                                              Math.Max(0.01f, distance - _radius * 2), distance + _radius * 2);
      return view * projection;
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
      _thread.Join(2000);
      PanelSwapChain.ClearPanel(_panel);
      CloseHandle(_swapChain.WaitableObject);
      _renderer.Dispose();
      _swapChain.Dispose();
      _device.Dispose();
    }
  }
}
