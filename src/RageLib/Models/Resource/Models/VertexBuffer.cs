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
  internal class VertexBuffer : DATBase, IFileAccess
  {
    public ushort VertexCount { get; private set; }
    private ushort Unknown1 { get; set; }               // byte bLocked, byte align
    public uint DataOffset { get; private set; }        // pLockedData
    public uint StrideSize { get; private set; }
    private uint Unknown2 { get; set; }
    private uint DataOffset2 { get; set; }              // piVertexBuffer

    public byte[] RawData { get; private set; }

    public VertexDeclaration VertexDeclaration { get; private set; }

    public VertexBuffer()
    {
    }

    public VertexBuffer(BinaryReader br)
    {
      Read(br);
    }

    public void ReadData(BinaryReader br)
    {
      // GTA IV PC (reditec): the vertices are where piVertexBuffer (+0x18) points. The game's current files
      // carry the same pointer in pLockedData (+0x08) too, but files of older patches (vigero.wft of 1.1.2.0)
      // leave pLockedData null; reading from it put every buffer at the start of the graphics segment.
      // See docs\GtaIV\Pc\formats\drawable.md.
      uint dataOffset = DataOffset != 0 ? DataOffset : DataOffset2;
      br.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
      RawData = br.ReadBytes((int)(VertexCount * StrideSize));
    }

    #region Implementation of IFileAccess

    public new void Read(BinaryReader br)
    {
      base.Read(br);

      VertexCount = br.ReadUInt16();
      Unknown1 = br.ReadUInt16();

      DataOffset = ResourceUtil.ReadDataOffset(br);

      StrideSize = br.ReadUInt32();

      var vertexDeclOffset = ResourceUtil.ReadOffset(br);

      Unknown2 = br.ReadUInt32();

      DataOffset2 = ResourceUtil.ReadDataOffset(br);

      var p2Offset = ResourceUtil.ReadOffset(br); // null

      //

      br.BaseStream.Seek(vertexDeclOffset, SeekOrigin.Begin);
      VertexDeclaration = new VertexDeclaration(br);
    }

    public new void Write(BinaryWriter bw)
    {
      throw new System.NotImplementedException();
    }

    #endregion
  }
}