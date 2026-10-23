/**********************************************************************\

 RageLib
 Copyright (C) 2008  Arushan/Aru <oneforaru at gmail.com>

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

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RageLib.Common.Resources;

namespace RageLib.GtaIV.Pc.Bounds
{
  /// <summary>The kind of a collision shape (phBound's type byte).</summary>
  public enum BoundType
  {
    Sphere = 0,
    Capsule = 1,
    Box = 3,
    Geometry = 4,
    CurvedGeometry = 6,
    BVH = 10,
    Composite = 12,
  }

  /// <summary>
  /// A collision material: the surface (its line in common\data\materials\materials.dat), the procedural
  /// objects grown on it (its tag's place in common\data\materials\procedural.dat; 0 for none) and flags.
  /// </summary>
  public readonly record struct BoundMaterial(int Id, int ProceduralTag, int Flags);

  /// <summary>A face of a polyhedron: three vertices, or four (D not -1) for a quad; its material's index in the bound.</summary>
  public readonly record struct BoundPolygon(int A, int B, int C, int D, int Material);

  /// <summary>
  /// A collision shape. Polyhedra (geometry, BVH, box) have vertices and faces; a sphere its centre and
  /// radius; a composite its children (placed by their matrices).
  /// </summary>
  public sealed class Bound
  {
    public BoundType Type { get; internal set; }
    public float Radius { get; internal set; }
    public Vector3 AabbMin { get; internal set; }
    public Vector3 AabbMax { get; internal set; }
    public Vector3 Centroid { get; internal set; }

    /// <summary>The vertices in the bound's space (decompressed: stored value x scale + offset).</summary>
    public Vector3[] Vertices { get; internal set; } = Array.Empty<Vector3>();

    public BoundPolygon[] Polygons { get; internal set; } = Array.Empty<BoundPolygon>();

    public BoundMaterial[] Materials { get; internal set; } = Array.Empty<BoundMaterial>();

    /// <summary>A composite's children and where each is placed (null for a child slot left empty).</summary>
    public List<Bound?> Children { get; } = new List<Bound?>();
    public List<Matrix4x4> ChildMatrices { get; } = new List<Matrix4x4>();
  }

  /// <summary>
  /// Collision files: static collision (.wbn, one bound) and collision dictionaries (.wbd, bounds by the
  /// hash of their model's name). Read from the resource's system memory; the layout was taken from the
  /// game's files against OpenIV's text export (.obn) of the same bounds.
  /// </summary>
  public sealed class BoundFile
  {
    private const uint MemoryBase = 0x50000000;

    private byte[] _data = Array.Empty<byte>();

    /// <summary>The bounds: one for a .wbn; a dictionary's entries with their hashes for a .wbd.</summary>
    public List<(uint Hash, Bound Bound)> Bounds { get; } = new List<(uint, Bound)>();

    /// <summary>Shapes this reader doesn't know (by type), skipped.</summary>
    public SortedDictionary<int, int> Skipped { get; } = new SortedDictionary<int, int>();

    public void Open(Stream stream)
    {
      var resource = new ResourceFile();
      resource.Read(stream);
      _data = resource.SystemMemData;

      // A dictionary (pgDictionary): hashes at +0x10 and bounds at +0x18, both arrays of (pointer, count, capacity).
      // A .wbn: the bound's pointer at +8.
      uint hashes = U32(0x10), entries = U32(0x18);
      int count = U16(0x14);
      if (IsPointer(hashes) && IsPointer(entries) && count > 0 && count == U16(0x1C))
      {
        for (int i = 0; i < count; i++)
        {
          var bound = ReadBound(Offset(U32(Offset(entries) + i * 4)), 0);
          if (bound != null)
          {
            Bounds.Add((U32(Offset(hashes) + i * 4), bound));
          }
        }
      }
      else
      {
        var bound = ReadBound(Offset(U32(8)), 0);
        if (bound != null)
        {
          Bounds.Add((0, bound));
        }
      }
    }

    private Bound? ReadBound(int at, int depth)
    {
      if (at <= 0 || at + 0xE0 > _data.Length || depth > 8)
      {
        return null;
      }
      var bound = new Bound
      {
        Type = (BoundType)_data[at + 4],
        Radius = F32(at + 0x08),
        AabbMax = V3(at + 0x10),
        AabbMin = V3(at + 0x20),
        Centroid = V3(at + 0x30),
      };
      switch (bound.Type)
      {
        case BoundType.Sphere:
          return bound;
        case BoundType.Box:
        case BoundType.Geometry:
        case BoundType.BVH:
          ReadPolyhedron(bound, at, materials: bound.Type != BoundType.Box);
          return bound;
        case BoundType.Composite:
          {
            uint children = U32(at + 0x80), matrices = U32(at + 0x84);
            int count = U16(at + 0x90);
            for (int i = 0; i < count; i++)
            {
              uint child = U32(Offset(children) + i * 4);
              bound.Children.Add(IsPointer(child) ? ReadBound(Offset(child), depth + 1) : null);
              bound.ChildMatrices.Add(IsPointer(matrices) ? Matrix(Offset(matrices) + i * 64) : Matrix4x4.Identity);
            }
            return bound;
          }
        default:
          Skipped[(int)bound.Type] = Skipped.TryGetValue((int)bound.Type, out int n) ? n + 1 : 1;
          return null;
      }
    }

    /// <summary>
    /// Vertices (+0xB0: 3 shorts each, x scale +0x90 + offset +0xA0; count +0xC8), polygons (+0x8C: 32 bytes
    /// each, count +0xCC: normal, area whose lowest byte is the material index, 4 vertex indices - the
    /// fourth 0 for a triangle - and 4 neighbours) and materials (+0xD0: 32 bits each, count +0xD8).
    /// </summary>
    private void ReadPolyhedron(Bound bound, int at, bool materials)
    {
      int vertexCount = (int)U32(at + 0xC8), polygonCount = (int)U32(at + 0xCC);
      uint vertices = U32(at + 0xB0), polygons = U32(at + 0x8C);
      if (!IsPointer(vertices) || !IsPointer(polygons) || vertexCount <= 0 || polygonCount <= 0 ||
          Offset(vertices) + vertexCount * 6 > _data.Length || Offset(polygons) + polygonCount * 32 > _data.Length)
      {
        return;
      }
      var scale = V3(at + 0x90);
      var offset = V3(at + 0xA0);
      bound.Vertices = new Vector3[vertexCount];
      for (int i = 0; i < vertexCount; i++)
      {
        int v = Offset(vertices) + i * 6;
        bound.Vertices[i] = new Vector3(I16(v), I16(v + 2), I16(v + 4)) * scale + offset;
      }
      bound.Polygons = new BoundPolygon[polygonCount];
      for (int i = 0; i < polygonCount; i++)
      {
        int p = Offset(polygons) + i * 32;
        int d = U16(p + 0x16);
        bound.Polygons[i] = new BoundPolygon(U16(p + 0x10), U16(p + 0x12), U16(p + 0x14), d == 0 ? -1 : d, _data[p + 0x0C]);
      }
      if (materials)
      {
        uint list = U32(at + 0xD0);
        int count = (int)U32(at + 0xD8);
        if (IsPointer(list) && count > 0 && count < 256)
        {
          bound.Materials = new BoundMaterial[count];
          for (int i = 0; i < count; i++)
          {
            uint m = U32(Offset(list) + i * 4);
            bound.Materials[i] = new BoundMaterial((int)(m & 0xFF), (int)((m >> 8) & 0xFF), (int)(m >> 16));
          }
        }
      }
    }

    private static bool IsPointer(uint value) => (value & 0xF0000000) == MemoryBase;
    private static int Offset(uint pointer) => (int)(pointer - MemoryBase);
    private uint U32(int at) => at >= 0 && at + 4 <= _data.Length ? BitConverter.ToUInt32(_data, at) : 0;
    private int U16(int at) => at >= 0 && at + 2 <= _data.Length ? BitConverter.ToUInt16(_data, at) : 0;
    private short I16(int at) => BitConverter.ToInt16(_data, at);
    private float F32(int at) => BitConverter.ToSingle(_data, at);
    private Vector3 V3(int at) => new Vector3(F32(at), F32(at + 4), F32(at + 8));

    // A child's matrix: 4 rows of 4 floats (the fourth column unused), the last row the position.
    private Matrix4x4 Matrix(int at)
    {
      if (at + 64 > _data.Length)
      {
        return Matrix4x4.Identity;
      }
      var m = new Matrix4x4(F32(at), F32(at + 4), F32(at + 8), 0, F32(at + 16), F32(at + 20), F32(at + 24), 0,
                            F32(at + 32), F32(at + 36), F32(at + 40), 0, F32(at + 48), F32(at + 52), F32(at + 56), 1);
      return float.IsNaN(m.M11) ? Matrix4x4.Identity : m;
    }
  }
}
