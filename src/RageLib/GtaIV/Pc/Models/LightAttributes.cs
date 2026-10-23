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

using System.IO;
using RageLib.Common.ResourceTypes;

namespace RageLib.GtaIV.Pc.Models
{
  /// <summary>
  /// A light of a drawable (CLightAttr, 0x6C bytes): a street lamp's, a neon sign's, a club's spot. Laid out
  /// as OpenIV's export of .light files names it (its offsets, "_f28", count from after the first 4 bytes),
  /// checked against the 227 lights of eleven exported map archives.
  /// </summary>
  internal class LightAttributes
  {
    public const int Size = 0x6C;

    public Vector3 Position { get; private set; }
    public Vector3 Direction { get; private set; }

    /// <summary>A second axis of the light (a spot's "up"; OpenIV calls it Origin).</summary>
    public Vector3 Tangent { get; private set; }

    /// <summary>The colour's four bytes as they are stored (OpenIV prints them in this order).</summary>
    public byte[] Color { get; private set; }

    public float Unknown28 { get; private set; }
    public float Unknown2C { get; private set; }
    public float Unknown30 { get; private set; }
    public float Distance { get; private set; }
    public float Range { get; private set; }
    public float CoronaSize { get; private set; }
    public float HotSpot { get; private set; }
    public float Falloff { get; private set; }
    public uint Flags { get; private set; }
    public uint CoronaHash { get; private set; }
    public uint LuminescenceHash { get; private set; }
    public byte TypeFlags { get; private set; }

    /// <summary>1 omni (a point), 2 spot.</summary>
    public byte Type { get; private set; }

    public float Unknown58 { get; private set; }
    public float Unknown5C { get; private set; }
    public float Unknown60 { get; private set; }

    /// <summary>The bone the light moves with (an id of the skeleton; 0 the root).</summary>
    public ushort BoneId { get; private set; }

    public ushort Unknown66 { get; private set; }

    public LightAttributes(BinaryReader br)
    {
      br.ReadUInt32(); // vtable
      Position = new Vector3(br);
      Direction = new Vector3(br);
      Tangent = new Vector3(br);
      Color = br.ReadBytes(4);
      Unknown28 = br.ReadSingle();
      Unknown2C = br.ReadSingle();
      Unknown30 = br.ReadSingle();
      Distance = br.ReadSingle();
      Range = br.ReadSingle();
      CoronaSize = br.ReadSingle();
      HotSpot = br.ReadSingle();
      Falloff = br.ReadSingle();
      Flags = br.ReadUInt32();
      CoronaHash = br.ReadUInt32();
      LuminescenceHash = br.ReadUInt32();
      TypeFlags = br.ReadByte();
      br.ReadBytes(2);
      Type = br.ReadByte();
      Unknown58 = br.ReadSingle();
      Unknown5C = br.ReadSingle();
      Unknown60 = br.ReadSingle();
      BoneId = br.ReadUInt16();
      Unknown66 = br.ReadUInt16();
    }
  }
}
