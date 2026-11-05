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
using System.IO;
using System.Text;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The Direct3D 12 device of the native renderer (#112): the DXGI factory, the fastest adapter, the device and
  /// its direct queue with a fence to wait on. In a Debug build also the debug layer, its messages written to
  /// SparkIV-d3d12.log beside SparkIV.exe.
  /// </summary>
  public sealed class D3D12Device : IDisposable
  {
    private readonly ID3D12Fence _fence;
    private ulong _fenceValue;
    private readonly ID3D12InfoQueue? _infoQueue;

    public D3D12Device()
    {
      bool debug = false;
#if DEBUG
      // (SPARKIV_D3D12_DEBUG=0 leaves it off in a Debug build too: it checks every call, a harbour frame's thousands of
      // draws take far longer then - for measuring.)
      if (Environment.GetEnvironmentVariable("SPARKIV_D3D12_DEBUG") != "0" &&
          Vortice.Direct3D12.D3D12.D3D12GetDebugInterface(out ID3D12Debug? debugInterface).Success && debugInterface != null)
      {
        debugInterface.EnableDebugLayer();
        debugInterface.Dispose();
        debug = true;
      }
#endif
      Factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug);

      ID3D12Device? device = null;
      using (var factory6 = Factory.QueryInterfaceOrNull<IDXGIFactory6>())
      {
        for (uint i = 0; device == null; i++)
        {
          IDXGIAdapter1? adapter = null;
          if (factory6 != null)
          {
            if (factory6.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out adapter).Failure)
            {
              break;
            }
          }
          else if (Factory.EnumAdapters1(i, out adapter).Failure)
          {
            break;
          }
          using (adapter)
          {
            if ((adapter!.Description1.Flags & AdapterFlags.Software) != 0)
            {
              continue;
            }
            if (Vortice.Direct3D12.D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? made).Success)
            {
              device = made;
              AdapterName = adapter.Description1.Description;
            }
          }
        }
      }
      Device = device ?? throw new InvalidOperationException("No Direct3D 12 adapter (feature level 11.0) found.");

      Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
      _fence = Device.CreateFence(0);

      if (debug)
      {
        _infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
      }
      Log($"device: {AdapterName}, debug layer {(debug ? "on" : "off")}");
    }

    public IDXGIFactory4 Factory { get; }
    public ID3D12Device Device { get; }
    public ID3D12CommandQueue Queue { get; }
    public string AdapterName { get; } = "";

    /// <summary>
    /// The per-pass profiler when profiling is on (the app: SPARKIV_D3D12_PROFILE=1; cutgen's bench: D3D12PROFILE=1),
    /// else null: the passes' marks then record nothing.
    /// </summary>
    public GpuProfiler? Profiler { get; private set; }

    /// <summary>Profiling on (once; the render thread uses it).</summary>
    public GpuProfiler StartProfiler() => Profiler ??= new GpuProfiler(this);

    private readonly object _signalLock = new object();

    /// <summary>
    /// A mark on the queue: the value the fence takes once the GPU has done everything sent so far. From any thread
    /// (the render thread and a loading thread send to the same queue).
    /// </summary>
    public ulong Signal()
    {
      lock (_signalLock)
      {
        ulong value = ++_fenceValue;
        Queue.Signal(_fence, value);
        return value;
      }
    }

    /// <summary>Whether the GPU has reached a mark of <see cref="Signal"/> (without waiting).</summary>
    public bool IsDone(ulong value) => _fence.CompletedValue >= value;

    /// <summary>Waits until the GPU has reached a mark of <see cref="Signal"/> (from any thread).</summary>
    public void Wait(ulong value)
    {
      if (_fence.CompletedValue >= value)
      {
        return;
      }
      var done = _fenceEvents.Value!;
      _fence.SetEventOnCompletion(value, done.SafeWaitHandle.DangerousGetHandle());
      done.WaitOne();
    }

    // An event per thread to wait on (the render thread and a loading thread wait at the same time).
    private readonly ThreadLocal<AutoResetEvent> _fenceEvents = new ThreadLocal<AutoResetEvent>(() => new AutoResetEvent(false), trackAllValues: true);

    /// <summary>Waits until the GPU has done everything sent so far.</summary>
    public void WaitIdle() => Wait(Signal());

    /// <summary>Debug build: the debug layer's messages since the last call, into the log.</summary>
    public void FlushMessages()
    {
      if (_infoQueue == null)
      {
        return;
      }
      ulong count = _infoQueue.NumStoredMessages;
      if (count == 0)
      {
        return;
      }
      var text = new StringBuilder();
      for (ulong i = 0; i < count; i++)
      {
        var message = _infoQueue.GetMessage(i);
        text.Append(message.Severity).Append(": ").AppendLine(message.Description);
      }
      _infoQueue.ClearStoredMessages();
      Log(text.ToString().TrimEnd());
    }

    /// <summary>A line in SparkIV-d3d12.log beside SparkIV.exe.</summary>
    public static void Log(string line)
    {
      try
      {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SparkIV-d3d12.log"),
                           DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine);
      }
      catch (Exception)
      {
        // The log is only a help.
      }
    }

    public void Dispose()
    {
      try
      {
        WaitIdle();
      }
      catch (Exception)
      {
        // The device may be gone.
      }
      FlushMessages();
      Profiler?.Dispose();
      Profiler = null;
      _infoQueue?.Dispose();
      _fence.Dispose();
      foreach (var e in _fenceEvents.Values)
      {
        e.Dispose();
      }
      _fenceEvents.Dispose();
      Queue.Dispose();
      Device.Dispose();
      Factory.Dispose();
    }
  }
}
