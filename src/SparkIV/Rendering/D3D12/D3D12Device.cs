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
      // (Only with SPARKIV_D3D12_DEBUG=1: it checks every call, a cutscene frame's ~20,000 draws then take ~370 ms
      // instead of ~15 - a Debug build started with F5 would play at a few frames a second.)
      if (Environment.GetEnvironmentVariable("SPARKIV_D3D12_DEBUG") == "1" &&
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
      CopyQueue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Copy));
      _copyFence = Device.CreateFence(0);

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

    /// <summary>
    /// The copy queue (the GPU's copy engine): uploads sent there run beside the frames on the direct queue instead of
    /// between their command lists. Its lists are of CommandListType.Copy; what they copy into is left in COMMON when they
    /// are done, which the direct queue takes as any read state (implicit promotion), so no barriers to read states there.
    /// </summary>
    public ID3D12CommandQueue CopyQueue { get; }

    private readonly ID3D12Fence _copyFence;
    private readonly object _copyLock = new object();
    private ulong _copyValue;

    /// <summary>A copy list (closed) sent to the copy queue and waited for (any thread; a loading one).</summary>
    public void CopyAndWait(ID3D12GraphicsCommandList list)
    {
      ulong value;
      // (A lock of its own: the render thread's Signal never waits behind a big upload being sent.)
      lock (_copyLock)
      {
        CopyQueue.ExecuteCommandList(list);
        value = ++_copyValue;
        CopyQueue.Signal(_copyFence, value);
      }
      if (_copyFence.CompletedValue >= value)
      {
        return;
      }
      var done = _fenceEvents.Value!;
      _copyFence.SetEventOnCompletion(value, done.SafeWaitHandle.DangerousGetHandle());
      done.WaitOne();
    }

    // What waits to be freed until the GPU is past the frames that may still use it.
    private readonly System.Collections.Generic.List<(ulong Mark, Action Release)> _retired = new System.Collections.Generic.List<(ulong, Action)>();

    /// <summary>
    /// Frees something the GPU may still read (buffers, textures, descriptor slots) once it is past everything sent so
    /// far, without waiting here (<see cref="ReleaseRetired"/>, at the start of each frame, does it). From any thread.
    /// </summary>
    public void Retire(Action release)
    {
      ulong mark = Signal();
      lock (_retired)
      {
        _retired.Add((mark, release));
      }
    }

    // The freeing runs on another thread, one batch after the other (releasing thousands of a location's buffers and
    // textures took the render thread 70-260 ms at a section change).
    private System.Threading.Tasks.Task _releasing = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>
    /// What <see cref="Retire"/> was handed and the GPU is done with, freed on another thread (the render thread asks
    /// each frame; nothing waited for).
    /// </summary>
    public void ReleaseRetired()
    {
      var due = TakeDue();
      if (due == null)
      {
        return;
      }
      lock (_retired)
      {
        _releasing = _releasing.ContinueWith(_ =>
        {
          var thread = Thread.CurrentThread;
          var priority = thread.Priority;
          thread.Priority = ThreadPriority.BelowNormal;
          try
          {
            due.ForEach(release => release());
          }
          catch (Exception ex)
          {
            Log("freeing failed: " + ex.Message);
          }
          finally
          {
            thread.Priority = priority;
          }
        }, System.Threading.Tasks.TaskScheduler.Default);
      }
    }

    /// <summary>Everything retired freed now, the GPU waited for (before the renderer's own things go).</summary>
    public void ReleaseRetiredNow()
    {
      WaitIdle();
      System.Threading.Tasks.Task releasing;
      lock (_retired)
      {
        releasing = _releasing;
      }
      releasing.Wait();
      TakeDue()?.ForEach(release => release());
    }

    // The retired things the GPU is done with, in the order retired.
    private System.Collections.Generic.List<Action>? TakeDue()
    {
      System.Collections.Generic.List<Action>? due = null;
      lock (_retired)
      {
        ulong done = _fence.CompletedValue;
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
          if (_retired[i].Mark <= done)
          {
            (due ??= new System.Collections.Generic.List<Action>()).Add(_retired[i].Release);
            _retired.RemoveAt(i);
          }
        }
      }
      due?.Reverse();
      return due;
    }

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
      ReleaseRetiredNow();
      FlushMessages();
      Profiler?.Dispose();
      _copyFence.Dispose();
      CopyQueue.Dispose();
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
