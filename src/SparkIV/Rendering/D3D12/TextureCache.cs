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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The native renderer's textures (#112 step 3): each texture of a <see cref="SparkIV.WinUI.Viewers.ModelScene"/>
  /// (its <c>Textures</c> blobs) made once on the GPU, with a shader resource view in one shader-visible heap. Blobs
  /// as the game has them ("SXT1", see ModelScene.Raw: DXT1/3/5 straight as BC1/2/3, A8R8G8B8 as B8G8R8A8, L8 as R8
  /// read as grey) keep their mip levels as far as the data holds them, as the page's uploadRaw; PNG blobs (a cast's)
  /// are decoded, without mip levels. Number 0 is plain white (no texture, or one that can't be made). No WinUI here.
  /// </summary>
  public sealed class TextureCache : IDisposable
  {
    public const int Capacity = 65536;

    // D3D12_ENCODE_SHADER_4_COMPONENT_MAPPING: r, g, b, a as they are; L8: its red as r, g, b and alpha 1.
    private const int MappingDefault = 0 | (1 << 3) | (2 << 6) | (3 << 9) | (1 << 12);
    private const int MappingGrey = 0 | (0 << 3) | (0 << 6) | (5 << 9) | (1 << 12);

    // Uploads are sent and waited for after about this many bytes (the location's textures: hundreds of MB).
    private const long FlushBytes = 256L << 20;

    private readonly D3D12Device _device;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly uint _descriptorSize;
    private readonly List<ID3D12Resource> _textures = new List<ID3D12Resource>();
    private readonly Dictionary<byte[], int> _known = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
    private readonly List<ID3D12Resource> _pendingUploads = new List<ID3D12Resource>();
    private long _pendingBytes;
    private ID3D12CommandAllocator? _allocator;
    private ID3D12GraphicsCommandList? _list;

    public TextureCache(D3D12Device device)
    {
      _device = device;
      _heap = device.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                                                                               Capacity, DescriptorHeapFlags.ShaderVisible));
      _descriptorSize = device.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      Reset();
    }

    public ID3D12DescriptorHeap Heap => _heap;

    /// <summary>Textures made, textures that couldn't be (drawn white), and their bytes on the GPU.</summary>
    public int Count => _textures.Count;
    public int Failed { get; private set; }
    public long Bytes { get; private set; }

    public GpuDescriptorHandle Handle(int number) => _heap.GetGPUDescriptorHandleForHeapStart() + number * (int)_descriptorSize;

    /// <summary>All textures gone but white (the GPU done with them first).</summary>
    public void Reset()
    {
      _device.WaitIdle();
      foreach (var texture in _textures)
      {
        texture.Dispose();
      }
      _textures.Clear();
      _known.Clear();
      Failed = 0;
      Bytes = 0;
      var white = new byte[] { 255, 255, 255, 255 };
      Make(Format.B8G8R8A8_UNorm, 1, 1, new[] { (white, 4, 1) }, MappingDefault);
      Flush();
    }

    /// <summary>
    /// A texture's number for a blob (made once per blob, the upload recorded; <see cref="Flush"/> sends it), or 0
    /// for none.
    /// </summary>
    public int Get(byte[]? blob)
    {
      if (blob == null || blob.Length < 4)
      {
        return 0;
      }
      if (_known.TryGetValue(blob, out int number))
      {
        return number;
      }
      number = 0;
      try
      {
        number = blob[0] == 'S' && blob[1] == 'X' && blob[2] == 'T' && blob[3] == '1' ? MakeRaw(blob) : MakeImage(blob);
      }
      catch (Exception ex)
      {
        D3D12Device.Log("texture not made: " + ex.Message);
      }
      if (number == 0)
      {
        Failed++;
      }
      _known[blob] = number;
      if (_pendingBytes >= FlushBytes)
      {
        Flush();
      }
      return number;
    }

    private int MakeRaw(byte[] blob)
    {
      int format = BitConverter.ToInt32(blob, 4);
      int width = BitConverter.ToInt32(blob, 8);
      int height = BitConverter.ToInt32(blob, 12);
      int levels = Math.Max(1, BitConverter.ToInt32(blob, 16));
      bool dxt = format <= 2;
      if (format > 4 || width <= 0 || height <= 0 || (dxt && (width % 4 != 0 || height % 4 != 0)))
      {
        return 0;
      }
      int blockBytes = format == 0 ? 8 : 16;
      var data = new List<(byte[] Bytes, int RowBytes, int Rows)>();
      int offset = 20;
      for (int level = 0; level < levels; level++)
      {
        int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
        int rowBytes = dxt ? (w + 3) / 4 * blockBytes : w * (format == 3 ? 4 : 1);
        int rows = dxt ? (h + 3) / 4 : h;
        int size = rowBytes * rows;
        if (offset + size > blob.Length)
        {
          break;
        }
        var bytes = new byte[size];
        Buffer.BlockCopy(blob, offset, bytes, 0, size);
        offset += size;
        data.Add((bytes, rowBytes, rows));
      }
      if (data.Count == 0)
      {
        return 0;
      }
      var dxgi = format switch
      {
        0 => Format.BC1_UNorm,
        1 => Format.BC2_UNorm,
        2 => Format.BC3_UNorm,
        3 => Format.B8G8R8A8_UNorm,
        _ => Format.R8_UNorm,
      };
      return Make(dxgi, width, height, data.ToArray(), format == 4 ? MappingGrey : MappingDefault);
    }

    private unsafe int MakeImage(byte[] blob)
    {
      using var image = new Bitmap(new MemoryStream(blob));
      int width = image.Width, height = image.Height;
      var bytes = new byte[width * height * 4];
      var locked = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      for (int y = 0; y < height; y++)
      {
        System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * locked.Stride, bytes, y * width * 4, width * 4);
      }
      image.UnlockBits(locked);
      return Make(Format.B8G8R8A8_UNorm, width, height, new[] { (bytes, width * 4, height) }, MappingDefault);
    }

    /// <summary>A texture with its levels' data (rows of bytes each), copied over an upload buffer; its number.</summary>
    private unsafe int Make(Format format, int width, int height, (byte[] Bytes, int RowBytes, int Rows)[] levels, int mapping)
    {
      if (_textures.Count >= Capacity)
      {
        return 0;
      }
      var d3d = _device.Device;
      var texture = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, (ushort)levels.Length), ResourceStates.CopyDest);

      // Each level's rows every 256 bytes, each level at a multiple of 512 bytes in the upload buffer.
      var offsets = new long[levels.Length];
      var pitches = new int[levels.Length];
      long total = 0;
      for (int l = 0; l < levels.Length; l++)
      {
        total = (total + 511) & ~511L;
        offsets[l] = total;
        pitches[l] = (levels[l].RowBytes + 255) & ~255;
        total += (long)pitches[l] * levels[l].Rows;
      }
      var upload = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                               ResourceDescription.Buffer((ulong)total), ResourceStates.GenericRead);
      void* mapped;
      upload.Map(0, null, &mapped).CheckError();
      for (int l = 0; l < levels.Length; l++)
      {
        fixed (byte* source = levels[l].Bytes)
        {
          for (int r = 0; r < levels[l].Rows; r++)
          {
            Buffer.MemoryCopy(source + (long)r * levels[l].RowBytes, (byte*)mapped + offsets[l] + (long)r * pitches[l], pitches[l], levels[l].RowBytes);
          }
        }
      }
      upload.Unmap(0);

      var list = List();
      for (int l = 0; l < levels.Length; l++)
      {
        int w = Math.Max(1, width >> l), h = Math.Max(1, height >> l);
        var footprint = new PlacedSubresourceFootPrint
        {
          Offset = (ulong)offsets[l],
          Footprint = new SubresourceFootPrint(format, (uint)w, (uint)h, 1, (uint)pitches[l]),
        };
        list.CopyTextureRegion(new TextureCopyLocation(texture, (uint)l), 0, 0, 0, new TextureCopyLocation(upload, footprint), null);
      }
      list.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
      _pendingUploads.Add(upload);
      _pendingBytes += total;
      Bytes += total;

      int number = _textures.Count;
      _textures.Add(texture);
      var view = new ShaderResourceViewDescription
      {
        Format = format,
        ViewDimension = ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = (uint)mapping,
        Texture2D = new Texture2DShaderResourceView { MipLevels = (uint)levels.Length },
      };
      d3d.CreateShaderResourceView(texture, view, _heap.GetCPUDescriptorHandleForHeapStart() + number * (int)_descriptorSize);
      return number;
    }

    private ID3D12GraphicsCommandList List()
    {
      if (_list == null)
      {
        _allocator = _device.Device.CreateCommandAllocator(CommandListType.Direct);
        _list = _device.Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, null);
      }
      return _list;
    }

    /// <summary>Sends the recorded uploads and waits for the GPU (then the upload buffers are freed).</summary>
    public void Flush()
    {
      if (_list != null)
      {
        _list.Close();
        _device.Queue.ExecuteCommandList(_list);
        _device.WaitIdle();
        _list.Dispose();
        _allocator!.Dispose();
        _list = null;
        _allocator = null;
      }
      foreach (var upload in _pendingUploads)
      {
        upload.Dispose();
      }
      _pendingUploads.Clear();
      _pendingBytes = 0;
    }

    public void Dispose()
    {
      Flush();
      _device.WaitIdle();
      foreach (var texture in _textures)
      {
        texture.Dispose();
      }
      _textures.Clear();
      _heap.Dispose();
    }
  }
}
