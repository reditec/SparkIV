/**********************************************************************\

 RageLib
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

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RageLib.Common.Resources;

namespace RageLib.GtaIV.Pc.Models
{
  /// <summary>
  /// A face shape of a blend shape mesh: which vertices it moves and by how much. Applied as the game does
  /// (GTA IV (PC), see docs/GtaIV/Pc/formats/wbs.md): a vertex's position + weight x Position / 4096, its normal and
  /// tangent + weight x Normal (Tangent) / 128, summed over the shapes, before skinning.
  /// </summary>
  public sealed class BlendShapeTarget
  {
    /// <summary>Its place in the mesh's list.</summary>
    public int Index { get; internal set; }

    /// <summary>Its name ("jaw_wide_open", "left_blink").</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The 16-bit key a face clip's tracks of type 21 name it by (their bone id).</summary>
    public int Key { get; internal set; } = -1;

    /// <summary>The vertices it moves, ascending.</summary>
    public int[] Vertices { get; internal set; } = Array.Empty<int>();

    /// <summary>Per moved vertex x, y, z, in 1/4096.</summary>
    public short[] Positions { get; internal set; } = Array.Empty<short>();

    /// <summary>Per moved vertex the normal's x, y, z change in 1/128 (null when the file has none).</summary>
    public sbyte[]? Normals { get; internal set; }

    /// <summary>Per moved vertex the tangent's x, y, z change in 1/128 (null when the file has none).</summary>
    public sbyte[]? Tangents { get; internal set; }
  }

  /// <summary>A mesh of a .wbs: its name ("head_000_R.mesh"), its vertices' number and its face shapes.</summary>
  public sealed class BlendShapeMesh
  {
    public string Name { get; internal set; } = "";
    public int VertexCount { get; internal set; }
    public List<BlendShapeTarget> Targets { get; } = new List<BlendShapeTarget>();
  }

  /// <summary>
  /// A cutscene ped's face shapes (.wbs, in pc\anim\cutsprops.img beside the ped: cs_malc.wbs), GTA IV (PC).
  /// Read from the resource's system memory; the layout was taken from cs_malc.wbs and cs_bernie_cranet.wbs and
  /// checked against the deltas the game uploads per frame (the TLAD trace GTAIV.3, call 102274443).
  /// </summary>
  public sealed class BlendShapeFile
  {
    private byte[] _data = Array.Empty<byte>();

    public List<BlendShapeMesh> Meshes { get; } = new List<BlendShapeMesh>();

    public void Open(Stream stream)
    {
      var resource = new ResourceFile();
      resource.Read(stream);
      _data = resource.SystemMemData;

      // The keys: a hash table (pointer +0xC, buckets +0x10) of entries {u16 key, the shape's name object, next};
      // a name object holds the name inline (+4) and the shape's place in its mesh's list (+0x44).
      var keyOf = new Dictionary<int, (int Key, string Name)>();
      int table = Offset(U32(0xC)), buckets = U16(0x10);
      for (int b = 0; b < buckets && table > 0; b++)
      {
        uint entry = U32(table + b * 4);
        for (int guard = 0; IsPointer(entry) && guard < 4096; guard++)
        {
          int e = Offset(entry);
          int named = Offset(U32(e + 4));
          if (named > 0)
          {
            keyOf[(int)U32(named + 0x44)] = (U16(e), String(named + 4, 0x40));
          }
          entry = U32(e + 8);
        }
      }

      // The meshes (pointer +4, count +8): the name inline; +0x88 an object whose first word is the mesh's data:
      // the shapes' list at +0x44 (count +0x48) and the vertices' number at +0x60.
      int list = Offset(U32(4)), meshes = U16(8);
      for (int m = 0; m < meshes && list > 0; m++)
      {
        int at = Offset(U32(list + m * 4));
        int holder = at > 0 ? Offset(U32(at + 0x88)) : -1;
        int data = holder > 0 ? Offset(U32(holder)) : -1;
        if (data <= 0)
        {
          continue;
        }
        var mesh = new BlendShapeMesh { Name = String(at, 0x80), VertexCount = (int)U32(data + 0x60) };
        int shapes = Offset(U32(data + 0x44)), count = U16(data + 0x48);
        for (int t = 0; t < count && shapes > 0; t++)
        {
          var target = ReadTarget(Offset(U32(shapes + t * 4)));
          if (target == null)
          {
            continue;
          }
          target.Index = t;
          if (keyOf.TryGetValue(t, out var key))
          {
            target.Key = key.Key;
            target.Name = key.Name;
          }
          mesh.Targets.Add(target);
        }
        Meshes.Add(mesh);
      }
    }

    // A shape: (pointer, count) pairs from +0x2C: +0x2C the positions (4 x int16 a vertex: 0, z, y, x), +0x34 the
    // normals and +0x3C the tangents (4 x int8 each, the same order), +0x4C the vertices' indices (u32).
    private BlendShapeTarget? ReadTarget(int at)
    {
      if (at <= 0)
      {
        return null;
      }
      int indices = Offset(U32(at + 0x4C)), n = U16(at + 0x50);
      int positions = Offset(U32(at + 0x2C));
      if (indices <= 0 || positions <= 0 || n == 0 || U16(at + 0x30) != n)
      {
        return null;
      }
      var target = new BlendShapeTarget { Vertices = new int[n], Positions = new short[n * 3] };
      for (int i = 0; i < n; i++)
      {
        target.Vertices[i] = (int)U32(indices + i * 4);
        for (int c = 0; c < 3; c++)
        {
          target.Positions[i * 3 + c] = BitConverter.ToInt16(_data, positions + i * 8 + (3 - c) * 2);
        }
      }
      target.Normals = Bytes(at + 0x34, n);
      target.Tangents = Bytes(at + 0x3C, n);
      return target;
    }

    private sbyte[]? Bytes(int field, int n)
    {
      int at = Offset(U32(field));
      if (at <= 0 || U16(field + 4) != n)
      {
        return null;
      }
      var values = new sbyte[n * 3];
      for (int i = 0; i < n; i++)
      {
        for (int c = 0; c < 3; c++)
        {
          values[i * 3 + c] = unchecked((sbyte)_data[at + i * 4 + 3 - c]);
        }
      }
      return values;
    }

    private uint U32(int at) => at >= 0 && at + 4 <= _data.Length ? BitConverter.ToUInt32(_data, at) : 0;
    private int U16(int at) => at >= 0 && at + 2 <= _data.Length ? BitConverter.ToUInt16(_data, at) : 0;
    private static bool IsPointer(uint value) => (value & 0xF0000000) == 0x50000000;
    private int Offset(uint pointer) => IsPointer(pointer) && (pointer & 0x0FFFFFFF) < _data.Length ? (int)(pointer & 0x0FFFFFFF) : -1;

    private string String(int at, int max)
    {
      int end = at;
      while (end < _data.Length && end < at + max && _data[end] != 0)
      {
        end++;
      }
      return Encoding.ASCII.GetString(_data, at, end - at);
    }
  }
}
