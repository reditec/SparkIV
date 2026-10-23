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

using System.IO;
using RageLib.Common;
using RageLib.Common.Resources;
using RageLib.Common.ResourceTypes;

namespace RageLib.Models.Resource.Models
{
  internal class Model : DATBase, IFileAccess
  {
    public PtrCollection<Geometry> Geometries { get; private set; }
    private ushort Unknown1 { get; set; } // the four following really should be bytes
    private ushort Unknown2 { get; set; }
    private ushort Unknown3 { get; set; }
    private ushort Unknown4 { get; set; }
    public SimpleArray<Vector4> UnknownVectors { get; private set; }
    public SimpleArray<ushort> ShaderMappings { get; private set; }

    // What the bytes hold, worked out from map objects with moving parts (e1_lost_g_vent1: the frame on
    // bone 0, the fan on bone 1) and skinned ones (flags, pigeons): Unknown1 is the number of bones a
    // skinned part uses (low byte) and 1 when skinned (high byte); Unknown2's high byte the bone a part
    // that isn't skinned hangs on; Unknown3's high byte 1 when skinned; Unknown4 the number of geometries.

    /// <summary>The bone (index in the skeleton) this part moves with when it isn't skinned.</summary>
    public int BoneIndex => Unknown2 >> 8;

    /// <summary>Whether the part's vertices carry their own bones and weights.</summary>
    public bool IsSkinned => (Unknown3 >> 8) != 0;

    #region Implementation of IFileAccess

    public new void Read(BinaryReader br)
    {
      base.Read(br);

      Geometries = new PtrCollection<Geometry>(br);

      var unknownVectorOffsets = ResourceUtil.ReadOffset(br);
      var materialMappingOffset = ResourceUtil.ReadOffset(br);

      Unknown1 = br.ReadUInt16();
      Unknown2 = br.ReadUInt16();

      Unknown3 = br.ReadUInt16();
      Unknown4 = br.ReadUInt16();

      //

      br.BaseStream.Seek(unknownVectorOffsets, SeekOrigin.Begin);
      UnknownVectors = new SimpleArray<Vector4>(br, 4, reader => new Vector4(reader));

      br.BaseStream.Seek(materialMappingOffset, SeekOrigin.Begin);
      ShaderMappings = new SimpleArray<ushort>(br, Geometries.Count, reader => reader.ReadUInt16());
    }

    public new void Write(BinaryWriter bw)
    {
      throw new System.NotImplementedException();
    }

    #endregion
  }
}