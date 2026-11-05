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
  /// The native renderer's textures (#112): each texture of a <see cref="SparkIV.WinUI.Viewers.ModelScene"/> (its
  /// <c>Textures</c> blobs) made once on the GPU, with a shader resource view in one shader-visible heap, counted by
  /// the models using it and freed when the last lets go (its view's slot used again). Blobs as the game has them
  /// ("SXT1", see ModelScene.Raw: DXT1/3/5 straight as BC1/2/3, A8R8G8B8 as B8G8R8A8, L8 as R8 read as grey) keep their
  /// mip levels as far as the data holds them, as the page's uploadRaw; PNG blobs (a cast's) are decoded, without mip
  /// levels. Number 0 is plain white (no texture, or one that can't be made). Safe from several threads: the copies go
  /// into the caller's command list (a loading thread's). No WinUI here.
  /// </summary>
  public sealed class TextureCache : IDisposable
  {
    public const int Capacity = 65536;

    // D3D12_ENCODE_SHADER_4_COMPONENT_MAPPING: r, g, b, a as they are; L8: its red as r, g, b and alpha 1.
    private const int MappingDefault = 0 | (1 << 3) | (2 << 6) | (3 << 9) | (1 << 12);
    private const int MappingGrey = 0 | (0 << 3) | (0 << 6) | (5 << 9) | (1 << 12);

    private readonly object _sync = new object();
    private readonly D3D12Device _device;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly uint _descriptorSize;
    private readonly ID3D12Resource?[] _textures = new ID3D12Resource?[Capacity];
    private readonly long[] _bytes = new long[Capacity];
    private readonly int[] _uses = new int[Capacity];
    private readonly byte[]?[] _blobOf = new byte[]?[Capacity];
    private readonly Dictionary<byte[], int> _known = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
    private readonly Stack<int> _free = new Stack<int>();
    private int _next;

    public TextureCache(D3D12Device device)
    {
      _device = device;
      _heap = device.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                                                                               Capacity, DescriptorHeapFlags.ShaderVisible));
      _descriptorSize = device.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      // Number 0: white, never freed.
      using var allocator = device.Device.CreateCommandAllocator(CommandListType.Direct);
      using var list = device.Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      var uploads = new List<ID3D12Resource>();
      lock (_sync)
      {
        int white = _next++;
        Make(white, Format.B8G8R8A8_UNorm, 1, 1, new[] { (new byte[] { 255, 255, 255, 255 }, 4, 1) }, MappingDefault, list, uploads, out long bytes);
        _uses[white] = 1;
      }
      list.Close();
      device.Queue.ExecuteCommandList(list);
      device.WaitIdle();
      foreach (var upload in uploads)
      {
        upload.Dispose();
      }
    }

    public ID3D12DescriptorHeap Heap => _heap;

    /// <summary>Textures on the GPU (white not counted), those that couldn't be made (drawn white), their bytes.</summary>
    public int Count { get { lock (_sync) { return _known.Count; } } }
    public int Failed { get; private set; }
    public long Bytes { get; private set; }

    public GpuDescriptorHandle Handle(int number) => _heap.GetGPUDescriptorHandleForHeapStart() + number * (int)_descriptorSize;

    /// <summary>A view's slot in the heap where <see cref="Reserve"/>'s owner writes its own view.</summary>
    public CpuDescriptorHandle CpuHandle(int number) => _heap.GetCPUDescriptorHandleForHeapStart() + number * (int)_descriptorSize;

    /// <summary>
    /// A slot of the heap kept for the caller's own view (a target read by the shaders by number, as the textures are);
    /// never given to a texture. 0 when the heap is full.
    /// </summary>
    public int Reserve()
    {
      lock (_sync)
      {
        int number = _free.Count > 0 ? _free.Pop() : _next < Capacity ? _next++ : 0;
        if (number > 0)
        {
          _uses[number] = 1;
        }
        return number;
      }
    }

    /// <summary>
    /// A texture's number for a blob, counted as used once more (made on first use: its copies recorded into
    /// <paramref name="list"/>, the upload buffers added to <paramref name="uploads"/> for the caller to free once the
    /// list is done); 0 (white) for none. <paramref name="bytes"/>: the upload bytes it added.
    /// </summary>
    public int Acquire(byte[]? blob, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, out long bytes)
    {
      bytes = 0;
      if (blob == null || blob.Length < 4)
      {
        return 0;
      }
      bool raw = blob[0] == 'S' && blob[1] == 'X' && blob[2] == 'T' && blob[3] == '1';
      int number;
      lock (_sync)
      {
        if (_known.TryGetValue(blob, out number))
        {
          if (number != 0)
          {
            _uses[number]++;
          }
          return number;
        }
        // (A raw texture that can't be made is known by its header: white, as before, without a slot.)
        if (raw && !RawUsable(blob))
        {
          Failed++;
          return 0;
        }
        number = _free.Count > 0 ? _free.Pop() : _next < Capacity ? _next++ : 0;
        if (number == 0)
        {
          Failed++;
          return 0;
        }
        // The slot is the blob's from now on (another thread asking for it gets it at once; the copies are all done
        // before any model using it is drawn: Prepare waits for every list).
        _known[blob] = number;
        _blobOf[number] = blob;
        _uses[number] = 1;
      }
      // Made outside the lock, so several loading threads make textures at once.
      try
      {
        if (raw)
        {
          MakeRaw(number, blob, list, uploads, out bytes);
        }
        else
        {
          MakeImage(number, blob, list, uploads, out bytes);
        }
      }
      catch (Exception ex)
      {
        // (Rare - an image that doesn't decode: its slot shows the white texture, as number 0 does.)
        D3D12Device.Log("texture not made: " + ex.Message);
        lock (_sync)
        {
          Failed++;
        }
        _device.Device.CopyDescriptorsSimple(1, _heap.GetCPUDescriptorHandleForHeapStart() + number * (int)_descriptorSize,
                                             _heap.GetCPUDescriptorHandleForHeapStart(), DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      }
      return number;
    }

    // Whether a raw blob's header describes a texture MakeRaw can make (its first level there).
    private static bool RawUsable(byte[] blob)
    {
      if (blob.Length < 20)
      {
        return false;
      }
      int format = BitConverter.ToInt32(blob, 4);
      int width = BitConverter.ToInt32(blob, 8);
      int height = BitConverter.ToInt32(blob, 12);
      bool dxt = format <= 2;
      int blockBytes = format == 0 ? 8 : 16;
      long rowBytes = dxt ? (width + 3) / 4 * blockBytes : (long)width * (format == 3 ? 4 : 1);
      long rows = dxt ? (height + 3) / 4 : height;
      return format <= 4 && width > 0 && height > 0 && !(dxt && (width % 4 != 0 || height % 4 != 0)) &&
             20 + rowBytes * rows <= blob.Length;
    }
    /// <summary>A use of a texture ended; the last frees it (only once the GPU is done with it: the caller waits first).</summary>
    public void Release(int number)
    {
      if (number <= 0)
      {
        return;
      }
      lock (_sync)
      {
        if (--_uses[number] > 0)
        {
          return;
        }
        _textures[number]?.Dispose();
        _textures[number] = null;
        Bytes -= _bytes[number];
        _bytes[number] = 0;
        if (_blobOf[number] is byte[] blob)
        {
          _known.Remove(blob);
        }
        _blobOf[number] = null;
        _free.Push(number);
      }
    }

    private void MakeRaw(int number, byte[] blob, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, out long bytes)
    {
      bytes = 0;
      int format = BitConverter.ToInt32(blob, 4);
      int width = BitConverter.ToInt32(blob, 8);
      int height = BitConverter.ToInt32(blob, 12);
      int levels = Math.Max(1, BitConverter.ToInt32(blob, 16));
      bool dxt = format <= 2;
      if (format > 4 || width <= 0 || height <= 0 || (dxt && (width % 4 != 0 || height % 4 != 0)))
      {
        return;
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
        var bytesOfLevel = new byte[size];
        Buffer.BlockCopy(blob, offset, bytesOfLevel, 0, size);
        offset += size;
        data.Add((bytesOfLevel, rowBytes, rows));
      }
      if (data.Count == 0)
      {
        return;
      }
      var dxgi = format switch
      {
        0 => Format.BC1_UNorm,
        1 => Format.BC2_UNorm,
        2 => Format.BC3_UNorm,
        3 => Format.B8G8R8A8_UNorm,
        _ => Format.R8_UNorm,
      };
      Make(number, dxgi, width, height, data.ToArray(), format == 4 ? MappingGrey : MappingDefault, list, uploads, out bytes);
    }

    private void MakeImage(int number, byte[] blob, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, out long bytes)
    {
      using var image = new Bitmap(new MemoryStream(blob));
      int width = image.Width, height = image.Height;
      var pixels = new byte[width * height * 4];
      var locked = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      for (int y = 0; y < height; y++)
      {
        System.Runtime.InteropServices.Marshal.Copy(locked.Scan0 + y * locked.Stride, pixels, y * width * 4, width * 4);
      }
      image.UnlockBits(locked);
      Make(number, Format.B8G8R8A8_UNorm, width, height, new[] { (pixels, width * 4, height) }, MappingDefault, list, uploads, out bytes);
    }

    /// <summary>A texture with its levels' data (rows of bytes each), copied over an upload buffer, into its slot (any thread).</summary>
    private unsafe void Make(int number, Format format, int width, int height, (byte[] Bytes, int RowBytes, int Rows)[] levels, int mapping,
                            ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, out long total)
    {
      total = 0;
      var d3d = _device.Device;
      var texture = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, (ushort)levels.Length),
        // (On the copy queue made in COMMON, the layout it copies into there.)
        list.CommandListType == CommandListType.Copy ? ResourceStates.Common : ResourceStates.CopyDest);

      // Each level's rows every 256 bytes, each level at a multiple of 512 bytes in the upload buffer.
      var offsets = new long[levels.Length];
      var pitches = new int[levels.Length];
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
      // (On the copy queue the texture is left to decay to COMMON, which the direct queue promotes to a read state.)
      if (list.CommandListType != CommandListType.Copy)
      {
        list.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
      }
      uploads.Add(upload);
      lock (_sync)
      {
        _textures[number] = texture;
        _bytes[number] = total;
        Bytes += total;
      }
      var view = new ShaderResourceViewDescription
      {
        Format = format,
        ViewDimension = ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = (uint)mapping,
        Texture2D = new Texture2DShaderResourceView { MipLevels = (uint)levels.Length },
      };
      d3d.CreateShaderResourceView(texture, view, _heap.GetCPUDescriptorHandleForHeapStart() + number * (int)_descriptorSize);
    }

    public void Dispose()
    {
      _device.WaitIdle();
      lock (_sync)
      {
        foreach (var texture in _textures)
        {
          texture?.Dispose();
        }
        Array.Clear(_textures);
        _known.Clear();
      }
      _heap.Dispose();
    }
  }
}
