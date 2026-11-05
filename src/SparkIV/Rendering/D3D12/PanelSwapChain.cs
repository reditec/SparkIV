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
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// A composition swap chain shown by a WinUI <see cref="SwapChainPanel"/> (flip-discard, three B8G8R8A8 buffers,
  /// a waitable object for the frame latency), with a render target view per buffer, a D32 depth buffer and per
  /// buffer a command allocator and the fence mark of the frame last drawn into it.
  /// </summary>
  public sealed class PanelSwapChain : IDisposable
  {
    public const int BufferCount = 3;
    public const Format BackBufferFormat = PanelSwapChainFormats.Color;
    public const Format DepthFormat = PanelSwapChainFormats.Depth;
    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    private readonly D3D12Device _device;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly uint _rtvSize;
    private readonly ID3D12Resource?[] _buffers = new ID3D12Resource?[BufferCount];
    private ID3D12Resource? _depth;
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[BufferCount];
    private readonly ulong[] _frameMarks = new ulong[BufferCount];

    /// <summary>Must be made on the UI thread (the panel takes the swap chain there).</summary>
    public PanelSwapChain(D3D12Device device, SwapChainPanel panel, int width, int height, float scaleX, float scaleY)
    {
      _device = device;
      Width = Math.Max(1, width);
      Height = Math.Max(1, height);
      var description = new SwapChainDescription1
      {
        Width = (uint)Width,
        Height = (uint)Height,
        Format = BackBufferFormat,
        BufferCount = BufferCount,
        BufferUsage = Usage.RenderTargetOutput,
        SampleDescription = new SampleDescription(1, 0),
        Scaling = Scaling.Stretch,
        SwapEffect = SwapEffect.FlipDiscard,
        AlphaMode = AlphaMode.Ignore,
        Flags = Flags,
      };
      using (var swapChain1 = device.Factory.CreateSwapChainForComposition(device.Queue, description))
      {
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
      }
      // One frame queued at most (the mouse's turn on screen a frame sooner than with two); SPARKIV_D3D12_LATENCY=2 for
      // the earlier two.
      MaximumFrameLatency = Environment.GetEnvironmentVariable("SPARKIV_D3D12_LATENCY") is string latency && uint.TryParse(latency, out uint frames) && frames is >= 1 and <= 16 ? frames : 1;
      _swapChain.MaximumFrameLatency = MaximumFrameLatency;
      WaitableObject = _swapChain.FrameLatencyWaitableObject;
      SetScale(scaleX, scaleY);
      SetOnPanel(panel, _swapChain.NativePointer);

      var d3d = device.Device;
      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, BufferCount));
      _dsvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
      _rtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      for (int i = 0; i < BufferCount; i++)
      {
        _allocators[i] = d3d.CreateCommandAllocator(CommandListType.Direct);
      }
      CommandList = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocators[0], null);
      CommandList.Close();
      MakeTargets();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D12GraphicsCommandList CommandList { get; }

    /// <summary>Signalled when the swap chain takes the next frame (wait on it before drawing one).</summary>
    public IntPtr WaitableObject { get; }

    /// <summary>How many frames may be queued at most (1 unless SPARKIV_D3D12_LATENCY says otherwise).</summary>
    public uint MaximumFrameLatency { get; }

    public int BufferIndex => (int)_swapChain.CurrentBackBufferIndex;
    public ID3D12Resource BackBuffer => _buffers[BufferIndex]!;
    public CpuDescriptorHandle RenderTargetView => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + BufferIndex * (int)_rtvSize;
    public CpuDescriptorHandle DepthView => _dsvHeap.GetCPUDescriptorHandleForHeapStart();
    /// <summary>The depth buffer itself (in DepthWrite between draws; the post effects read it).</summary>
    public ID3D12Resource? DepthBuffer => _depth;

    /// <summary>The panel's composition scale (DPI): the buffers have the size in pixels, shown at 1 / scale.</summary>
    public void SetScale(float scaleX, float scaleY)
    {
      var transform = new Matrix3x2(1f / Math.Max(scaleX, 0.01f), 0, 0, 1f / Math.Max(scaleY, 0.01f), 0, 0);
      using var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
      swapChain2.MatrixTransform = transform;
    }

    /// <summary>A new size in pixels: the GPU done, the buffers made again.</summary>
    public void Resize(int width, int height)
    {
      width = Math.Max(1, width);
      height = Math.Max(1, height);
      if (width == Width && height == Height)
      {
        return;
      }
      _device.WaitIdle();
      ReleaseTargets();
      _swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, Format.Unknown, Flags).CheckError();
      Width = width;
      Height = height;
      MakeTargets();
    }

    /// <summary>Starts a frame: waits until the GPU is done with this buffer's last frame, opens the command list.</summary>
    public ID3D12GraphicsCommandList Begin()
    {
      int index = BufferIndex;
      var profiler = _device.Profiler;
      long start = profiler != null ? GpuProfiler.Now : 0;
      _device.Wait(_frameMarks[index]);
      profiler?.CpuSince("wait for the buffer's fence", start);
      _allocators[index].Reset();
      CommandList.Reset(_allocators[index], null);
      profiler?.BeginFrame(CommandList);
      CommandList.ResourceBarrierTransition(BackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
      return CommandList;
    }

    /// <summary>Ends a frame: closes and sends the command list and shows the buffer (with vsync); the frame's fence mark.</summary>
    public ulong End()
    {
      int index = BufferIndex;
      CommandList.ResourceBarrierTransition(BackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
      // (Profiling: the frame's last timestamps resolved, then closing and sending, and Present, timed apart.)
      var profiler = _device.Profiler;
      profiler?.EndFrame(CommandList);
      long start = profiler != null ? GpuProfiler.Now : 0;
      CommandList.Close();
      _device.Queue.ExecuteCommandList(CommandList);
      if (profiler != null)
      {
        profiler.CpuSince("close + execute", start);
        start = GpuProfiler.Now;
      }
      _swapChain.Present(1, PresentFlags.None).CheckError();
      profiler?.CpuSince("present", start);
      return _frameMarks[index] = _device.Signal();
    }

    private void MakeTargets()
    {
      var d3d = _device.Device;
      var rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
      for (int i = 0; i < BufferCount; i++)
      {
        _buffers[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
        d3d.CreateRenderTargetView(_buffers[i], null, rtv + i * (int)_rtvSize);
      }
      _depth = d3d.CreateCommittedResource(
        new HeapProperties(HeapType.Default),
        HeapFlags.None,
        ResourceDescription.Texture2D(DepthFormat, (uint)Width, (uint)Height, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.DepthWrite,
        new ClearValue(DepthFormat, 1.0f, 0));
      d3d.CreateDepthStencilView(_depth, null, DepthView);
    }

    private void ReleaseTargets()
    {
      for (int i = 0; i < BufferCount; i++)
      {
        _buffers[i]?.Dispose();
        _buffers[i] = null;
        _frameMarks[i] = 0;
      }
      _depth?.Dispose();
      _depth = null;
    }

    // ISwapChainPanelNative of WinUI 3 (microsoft.ui.xaml.media.dxinterop.h): IUnknown, then SetSwapChain.
    private static readonly Guid IID_ISwapChainPanelNative = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");

    private static unsafe void SetOnPanel(SwapChainPanel panel, IntPtr swapChain)
    {
      IntPtr unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
      Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_ISwapChainPanelNative, out IntPtr native));
      try
      {
        var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)native)[3];
        Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
      }
      finally
      {
        Marshal.Release(native);
      }
    }

    /// <summary>Takes the swap chain off the panel (on the UI thread, before disposing).</summary>
    public static void ClearPanel(SwapChainPanel panel)
    {
      try
      {
        SetOnPanel(panel, IntPtr.Zero);
      }
      catch (Exception)
      {
        // The panel is gone already.
      }
    }

    public void Dispose()
    {
      _device.WaitIdle();
      ReleaseTargets();
      CommandList.Dispose();
      foreach (var allocator in _allocators)
      {
        allocator.Dispose();
      }
      _rtvHeap.Dispose();
      _dsvHeap.Dispose();
      _swapChain.Dispose();
    }
  }
}
