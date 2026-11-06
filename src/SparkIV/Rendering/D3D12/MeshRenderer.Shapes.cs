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
using Vortice.Direct3D12;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// A blend shape (morph target) of a mesh: the vertices it moves (their numbers in the mesh) and per vertex the change
  /// of its position (x, y, z) and, where known, of its normal and its tangent (x, y, z), each added x the shape's weight
  /// before skinning. Found by its key (see <see cref="MeshRenderer.SetShapeWeights"/>).
  /// </summary>
  public sealed record BlendShape(int Key, int[] Vertices, float[] Positions, float[]? Normals, float[]? Tangents);

  /// <summary>
  /// Blend shapes (#112, the page's makeFace / blendFace, ModelView.html ~1091-1128): a model's meshes with shapes
  /// (<see cref="ModelSource.Shapes"/>) drawn per instance from vertex buffers of the instance's own, which hold the model's
  /// vertices with the shapes added by the instance's weights. The CPU adds them, as the page does (a cutscene head:
  /// ~2,000 vertices), into an upload ring (a slot per frame in flight, as the palette's); a copy at the start of
  /// <see cref="Draw"/> brings the changed ranges into the instance's buffers, only when the weights changed.
  /// </summary>
  public sealed partial class MeshRenderer
  {
    // A mesh with shapes in its model's vertex buffer: its first vertex and count, its vertices as built (VertexFloats
    // each; ExtraFloats each in the extras, when the model has them), whether its normals and tangents are its own.
    internal sealed class ShapedRange
    {
      public uint VertexBase;
      public int VertexCount;
      public float[] Base = Array.Empty<float>();
      public float[]? BaseExtras;
      public bool Normals;
      public bool Tangents;
      public IReadOnlyList<BlendShape> Shapes = Array.Empty<BlendShape>();
    }

    // An instance's own vertex buffers (default heap: the model's copied in once, then the shaped ranges each time the
    // weights change) and the weights last copied.
    private sealed class InstanceShapes : IDisposable
    {
      public ID3D12Resource Vertices = null!;
      public ID3D12Resource? Extras;
      public VertexBufferView VertexView;
      public VertexBufferView ExtrasView;
      public bool Filled;
      public float[]? Applied;

      public void Dispose()
      {
        Vertices.Dispose();
        Extras?.Dispose();
      }
    }

    // The upload ring for the shaped ranges: a slot per frame in flight (_paletteSlot), made when first needed.
    private const long ShapeSlotBytes = 8L << 20;
    private ID3D12Resource? _shapeUpload;
    private unsafe byte* _shapeMapped;
    private bool _shapeFullLogged;

    /// <summary>
    /// An instance's blend shapes' weights by the shapes' keys (a key not given: 0); null: none, the model's own
    /// vertices. Only for a model with <see cref="ModelSource.Shapes"/>; taken by the next <see cref="Draw"/>.
    /// </summary>
    public void SetShapeWeights(int instance, IReadOnlyDictionary<int, float>? weights)
    {
      if (instance >= 0 && instance < _instances.Count && _instances[instance] is Instance i)
      {
        i.ShapeWeights = weights;
      }
    }

    // The vertex buffers an instance is drawn from: its own while it has weights, else its model's.
    private static VertexBufferView VertexViewOf(Instance instance, Model model) =>
      instance.ShapeWeights != null && instance.Shaped is { Filled: true } s ? s.VertexView : model.VertexView;

    private static VertexBufferView ExtrasViewOf(Instance instance, Model model) =>
      instance.ShapeWeights != null && instance.Shaped is { Filled: true, Extras: not null } s ? s.ExtrasView : model.ExtrasView;

    // The visible shaped instances' buffers brought to their weights, recorded at the start of the frame's list (before
    // any draw reads them). The buffers are COMMON there (buffers decay at the end of every ExecuteCommandLists), so the
    // copies promote them to COPY_DEST; one barrier back to vertex buffers after.
    private unsafe void UpdateShapes(ID3D12GraphicsCommandList list)
    {
      List<ID3D12Resource>? touched = null;
      long used = 0;
      long slotStart = (long)_paletteSlot * ShapeSlotBytes;
      foreach (var instance in _instances)
      {
        if (instance == null || !instance.Visible || instance.ShapeWeights is not IReadOnlyDictionary<int, float> weights ||
            _models[instance.Model] is not Model model || model.Shapes == null)
        {
          continue;
        }
        // The weights in the order of the model's shapes (compared with those last copied).
        int shapeCount = 0;
        foreach (var range in model.Shapes)
        {
          shapeCount += range.Shapes.Count;
        }
        var now = new float[shapeCount];
        int k = 0;
        foreach (var range in model.Shapes)
        {
          foreach (var shape in range.Shapes)
          {
            now[k++] = weights.TryGetValue(shape.Key, out float w) ? w : 0;
          }
        }
        var s = instance.Shaped;
        if (s == null)
        {
          var d3d = _device.Device;
          s = new InstanceShapes
          {
            Vertices = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                                                   ResourceDescription.Buffer(model.VertexView.SizeInBytes), ResourceStates.Common),
          };
          s.VertexView = new VertexBufferView(s.Vertices.GPUVirtualAddress, model.VertexView.SizeInBytes, model.VertexView.StrideInBytes);
          if (model.Extras != null)
          {
            s.Extras = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                                                   ResourceDescription.Buffer(model.ExtrasView.SizeInBytes), ResourceStates.Common);
            s.ExtrasView = new VertexBufferView(s.Extras.GPUVirtualAddress, model.ExtrasView.SizeInBytes, model.ExtrasView.StrideInBytes);
          }
          instance.Shaped = s;
        }
        bool changed = false;
        bool fill = !s.Filled;
        if (fill || s.Applied == null || !now.AsSpan().SequenceEqual(s.Applied))
        {
          long needed = 0;
          foreach (var range in model.Shapes)
          {
            needed += (long)range.Base.Length * 4 + (range.BaseExtras != null && s.Extras != null ? (long)range.BaseExtras.Length * 4 : 0);
          }
          bool fits = used + needed <= ShapeSlotBytes;
          if (!fits && !_shapeFullLogged)
          {
            _shapeFullLogged = true;
            D3D12Device.Log("blend shapes: more shaped vertices in a frame than the upload ring holds; some left as they were");
          }
          // The model's vertices into the instance's buffers once: all of them when the shapes don't fit this frame, else
          // only between the shaped ranges (no two copies write the same bytes: their order isn't kept without a barrier).
          if (fill)
          {
            CopyGaps(list, s.Vertices, model.Vertices, model.VertexView.SizeInBytes, VertexFloats, model.Shapes, fits);
            if (s.Extras != null && model.Extras != null)
            {
              CopyGaps(list, s.Extras, model.Extras, model.ExtrasView.SizeInBytes, ExtraFloats, model.Shapes, fits);
            }
            s.Filled = true;
            changed = true;
          }
          if (fits)
          {
            if (_shapeUpload == null)
            {
              _shapeUpload = _device.Device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                                                    ResourceDescription.Buffer((ulong)(PaletteSlots * ShapeSlotBytes)), ResourceStates.GenericRead);
              void* mapped;
              _shapeUpload.Map(0, null, &mapped).CheckError();
              _shapeMapped = (byte*)mapped;
              slotStart = (long)_paletteSlot * ShapeSlotBytes;
            }
            k = 0;
            foreach (var range in model.Shapes)
            {
              int first = k;
              k += range.Shapes.Count;
              long at = slotStart + used;
              float* v = (float*)(_shapeMapped + at);
              AddShapes(range, now, first, v, null);
              list.CopyBufferRegion(s.Vertices, (ulong)range.VertexBase * VertexFloats * 4, _shapeUpload, (ulong)at, (ulong)range.Base.Length * 4);
              used += (long)range.Base.Length * 4;
              if (range.BaseExtras != null && s.Extras != null)
              {
                at = slotStart + used;
                float* e = (float*)(_shapeMapped + at);
                AddShapes(range, now, first, null, e);
                list.CopyBufferRegion(s.Extras, (ulong)range.VertexBase * ExtraFloats * 4, _shapeUpload, (ulong)at, (ulong)range.BaseExtras.Length * 4);
                used += (long)range.BaseExtras.Length * 4;
              }
            }
            s.Applied = now;
            changed = true;
          }
        }
        if (changed)
        {
          touched ??= new List<ID3D12Resource>();
          touched.Add(s.Vertices);
          if (s.Extras != null)
          {
            touched.Add(s.Extras);
          }
        }
      }
      if (touched != null)
      {
        foreach (var buffer in touched)
        {
          list.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.VertexAndConstantBuffer);
        }
      }
    }

    // A model's buffer copied into an instance's: whole, or (gaps) all but the shaped ranges (in the order of their first
    // vertex, as Build adds them), which the shapes' copies write.
    private static void CopyGaps(ID3D12GraphicsCommandList list, ID3D12Resource into, ID3D12Resource from, ulong size, int floats,
                                 List<ShapedRange> ranges, bool gaps)
    {
      if (!gaps)
      {
        list.CopyBufferRegion(into, 0, from, 0, size);
        return;
      }
      ulong at = 0;
      foreach (var range in ranges)
      {
        ulong start = (ulong)range.VertexBase * (ulong)floats * 4;
        if (start > at)
        {
          list.CopyBufferRegion(into, at, from, at, start - at);
        }
        at = Math.Max(at, start + (ulong)range.VertexCount * (ulong)floats * 4);
      }
      if (size > at)
      {
        list.CopyBufferRegion(into, at, from, at, size - at);
      }
    }

    // A shaped range with its shapes added (the page's blendFace): into vertices (VertexFloats each: position 0-2,
    // normal 3-5) or extras (ExtraFloats each: tangent 0-2), the range's own as built plus weight x change.
    private static unsafe void AddShapes(ShapedRange range, float[] weights, int firstWeight, float* vertices, float* extras)
    {
      if (vertices != null)
      {
        fixed (float* b = range.Base)
        {
          Buffer.MemoryCopy(b, vertices, range.Base.Length * 4L, range.Base.Length * 4L);
        }
      }
      if (extras != null && range.BaseExtras != null)
      {
        fixed (float* b = range.BaseExtras)
        {
          Buffer.MemoryCopy(b, extras, range.BaseExtras.Length * 4L, range.BaseExtras.Length * 4L);
        }
      }
      for (int j = 0; j < range.Shapes.Count; j++)
      {
        float w = weights[firstWeight + j];
        if (w == 0)
        {
          continue;
        }
        var shape = range.Shapes[j];
        var list = shape.Vertices;
        for (int i = 0; i < list.Length; i++)
        {
          int v = list[i];
          if (v < 0 || v >= range.VertexCount)
          {
            continue;
          }
          if (vertices != null)
          {
            float* p = vertices + (long)v * VertexFloats;
            if (shape.Positions.Length >= i * 3 + 3)
            {
              p[0] += w * shape.Positions[i * 3];
              p[1] += w * shape.Positions[i * 3 + 1];
              p[2] += w * shape.Positions[i * 3 + 2];
            }
            if (range.Normals && shape.Normals is float[] n && n.Length >= i * 3 + 3)
            {
              p[3] += w * n[i * 3];
              p[4] += w * n[i * 3 + 1];
              p[5] += w * n[i * 3 + 2];
            }
          }
          if (extras != null && range.Tangents && shape.Tangents is float[] t && t.Length >= i * 3 + 3)
          {
            float* e = extras + (long)v * ExtraFloats;
            e[0] += w * t[i * 3];
            e[1] += w * t[i * 3 + 1];
            e[2] += w * t[i * 3 + 2];
          }
        }
      }
    }

    // An instance's own buffers let go (once the GPU is past the frames that may still read them).
    private void ReleaseShapes(Instance instance)
    {
      if (instance.Shaped is InstanceShapes s)
      {
        instance.Shaped = null;
        _device.Retire(s.Dispose);
      }
    }

    private unsafe void DisposeShapes()
    {
      if (_shapeUpload != null)
      {
        _shapeUpload.Unmap(0);
        _shapeUpload.Dispose();
        _shapeUpload = null;
        _shapeMapped = null;
      }
    }
  }
}
