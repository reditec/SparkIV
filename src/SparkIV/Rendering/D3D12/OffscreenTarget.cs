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
using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// Drawing without a window (#112 step 2, the visual tests): a colour texture (B8G8R8A8) and a D32 depth buffer of a
  /// given size, and a readback buffer the colour is copied into after a frame, saved as PNG. No WinUI here.
  /// </summary>
  public sealed class OffscreenTarget : IDisposable
  {
    private readonly D3D12Device _device;
    private readonly ID3D12Resource _color;
    private readonly ID3D12Resource _depth;
    private readonly ID3D12Resource _readback;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly uint _rowPitch;

    public OffscreenTarget(D3D12Device device, int width, int height)
    {
      _device = device;
      Width = width;
      Height = height;
      var d3d = device.Device;
      _color = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(PanelSwapChainFormats.Color, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.RenderTarget, new ClearValue(PanelSwapChainFormats.Color, new Vortice.Mathematics.Color4(0.16f, 0.17f, 0.19f, 1f)));
      _depth = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(PanelSwapChainFormats.Depth, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.DepthWrite, new ClearValue(PanelSwapChainFormats.Depth, 1.0f, 0));
      // Rows of a texture copied into a buffer start every 256 bytes.
      _rowPitch = (uint)((width * 4 + 255) & ~255);
      _readback = d3d.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
        ResourceDescription.Buffer((ulong)_rowPitch * (ulong)height), ResourceStates.CopyDest);

      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
      _dsvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
      d3d.CreateRenderTargetView(_color, null, RenderTargetView);
      d3d.CreateDepthStencilView(_depth, null, DepthView);
      _allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      _allocator2 = d3d.CreateCommandAllocator(CommandListType.Direct);
      CommandList = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, null);
      CommandList.Close();
    }

    public int Width { get; }
    public int Height { get; }
    public ID3D12GraphicsCommandList CommandList { get; }
    public CpuDescriptorHandle RenderTargetView => _rtvHeap.GetCPUDescriptorHandleForHeapStart();
    public CpuDescriptorHandle DepthView => _dsvHeap.GetCPUDescriptorHandleForHeapStart();
    /// <summary>The depth buffer itself (in DepthWrite between draws; the post effects read it).</summary>
    public ID3D12Resource DepthBuffer => _depth;

    /// <summary>
    /// Opens the command list for a frame (its allocator one of two, used again once the GPU is done with the frame
    /// before last: at most one frame on the GPU while this one is recorded, as in the app's loop).
    /// </summary>
    public ID3D12GraphicsCommandList Begin()
    {
      _slot = (_slot + 1) % 2;
      _device.Wait(_marks[_slot]);
      _device.ReleaseRetired();
      var allocator = _slot == 0 ? _allocator : _allocator2;
      allocator.Reset();
      CommandList.Reset(allocator, null);
      return CommandList;
    }

    /// <summary>Ends the frame without reading it back: sent, the GPU done (for timing).</summary>
    public void End()
    {
      Submit();
      _device.WaitIdle();
    }

    /// <summary>Ends the frame without waiting: sent, the GPU going on with it while the next is recorded; its fence mark.</summary>
    public ulong Submit()
    {
      CommandList.Close();
      _device.Queue.ExecuteCommandList(CommandList);
      return _marks[_slot] = _device.Signal();
    }

    private readonly ID3D12CommandAllocator _allocator2;
    private readonly ulong[] _marks = new ulong[2];
    private int _slot;

    /// <summary>Ends the frame: the colour copied into the readback buffer, the GPU done, the picture saved as PNG.</summary>
    public unsafe void EndAndSave(string path)
    {
      var list = CommandList;
      list.ResourceBarrierTransition(_color, ResourceStates.RenderTarget, ResourceStates.CopySource);
      var footprint = new PlacedSubresourceFootPrint
      {
        Offset = 0,
        Footprint = new SubresourceFootPrint(PanelSwapChainFormats.Color, (uint)Width, (uint)Height, 1, _rowPitch),
      };
      list.CopyTextureRegion(new TextureCopyLocation(_readback, footprint), 0, 0, 0, new TextureCopyLocation(_color, 0), null);
      list.ResourceBarrierTransition(_color, ResourceStates.CopySource, ResourceStates.RenderTarget);
      list.Close();
      _device.Queue.ExecuteCommandList(list);
      _device.WaitIdle();

      void* mapped;
      _readback.Map(0, null, &mapped).CheckError();
      try
      {
        // B8G8R8A8 in memory is what a 32-bit GDI+ bitmap holds; alpha is set opaque by the clear and the shader.
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
        {
          Buffer.MemoryCopy((byte*)mapped + (long)y * _rowPitch, (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, Width * 4);
        }
        bitmap.UnlockBits(data);
        bitmap.Save(path, ImageFormat.Png);
      }
      finally
      {
        _readback.Unmap(0);
      }
    }

    public void Dispose()
    {
      _device.WaitIdle();
      CommandList.Dispose();
      _allocator.Dispose();
      _allocator2.Dispose();
      _rtvHeap.Dispose();
      _dsvHeap.Dispose();
      _readback.Dispose();
      _depth.Dispose();
      _color.Dispose();
    }
  }
}
