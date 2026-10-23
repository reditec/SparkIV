/**********************************************************************\

 RageLib - Models
 Copyright (C) 2009  Arushan/Aru <oneforaru at gmail.com>

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
using System.IO;
using RageLib.Common;
using RageLib.Common.Resources;
using RageLib.Common.ResourceTypes;

namespace RageLib.Models.Resource
{
  // gtaFragType
  internal class FragTypeModel : IFileAccess, IDataReader, IEmbeddedResourceReader, IDisposable
  {
    // fragTypeChild: a part of the fragment that can break off (door, bonnet, wheel ...), at a bone of the
    // skeleton. Only some have a model of their own; a vehicle's wheel is one child's model the game puts
    // at every wheel bone.
    internal class FragTypeChild : IFileAccess
    {
      public DrawableModel Drawable { get; set; }

      public float PristineMass { get; private set; }
      public float DamagedMass { get; private set; }
      public ushort GroupIndex { get; private set; }

      /// <summary>The index of the bone in the skeleton of the fragment's drawable.</summary>
      public ushort BoneIndex { get; private set; }

      /// <summary>
      /// Two 4x4 matrices, row by row with the translation in the last row. The translation of the first is
      /// the negated position of the bone (from the fragment into the bone).
      /// </summary>
      public float[] Matrix1 { get; private set; }
      public float[] Matrix2 { get; private set; }

      public FragTypeChild(BinaryReader br)
      {
        Read(br);
      }

      #region Implementation of IFileAccess

      public void Read(BinaryReader br)
      {
        br.ReadUInt32();  // vtable
        PristineMass = br.ReadSingle();
        DamagedMass = br.ReadSingle();
        GroupIndex = br.ReadUInt16();
        BoneIndex = br.ReadUInt16();
        Matrix1 = ReadMatrix(br);
        Matrix2 = ReadMatrix(br);

        uint offset = ResourceUtil.ReadOffset(br);
        if (offset != 0)
        {
          Drawable = new DrawableModel();

          br.BaseStream.Seek(offset, SeekOrigin.Begin);
          Drawable.Read(br);
        }
      }

      public void Write(BinaryWriter bw)
      {
        throw new System.NotImplementedException();
      }

      #endregion

      private static float[] ReadMatrix(BinaryReader br)
      {
        var matrix = new float[16];
        for (int i = 0; i < 16; i++)
        {
          matrix[i] = br.ReadSingle();
        }
        return matrix;
      }
    }

    public DrawableModel Drawable { get; set; }
    public FragTypeChild[] Children { get; set; }

    #region Implementation of IFileAccess

    public void Read(BinaryReader br)
    {
      br.BaseStream.Seek(0xB4, SeekOrigin.Begin);
      uint offset = ResourceUtil.ReadOffset(br);
      if (offset != 0)
      {
        Drawable = new DrawableModel();
        br.BaseStream.Seek(offset, SeekOrigin.Begin);
        Drawable.Read(br);
      }
      else
      {
        throw new Exception("No model in FragType");
      }

      br.BaseStream.Seek(0x1F3, SeekOrigin.Begin);
      int childCount = br.ReadByte();

      br.BaseStream.Seek(0xD4, SeekOrigin.Begin);
      uint childListOffset = ResourceUtil.ReadOffset(br);

      br.BaseStream.Seek(childListOffset, SeekOrigin.Begin);
      var childOffsets = new SimpleArray<uint>(br, childCount, ResourceUtil.ReadOffset);

      Children = new FragTypeChild[childCount];
      for (int i = 0; i < childCount; i++)
      {
        br.BaseStream.Seek(childOffsets[i], SeekOrigin.Begin);
        Children[i] = new FragTypeChild(br);
      }

      foreach (var child in Children)
      {
        if (child.Drawable != null)
        {
          child.Drawable.ShaderGroup = Drawable.ShaderGroup;
        }
      }

    }

    public void Write(BinaryWriter bw)
    {
      throw new System.NotImplementedException();
    }

    #endregion

    #region Implementation of IDataReader

    public void ReadData(BinaryReader br)
    {
      Drawable.ReadData(br);

      foreach (var child in Children)
      {
        if (child.Drawable != null)
        {
          child.Drawable.ReadData(br);
        }
      }
    }

    #endregion

    #region Implementation of IEmbeddedResourceReader

    public void ReadEmbeddedResources(Stream systemMemory, Stream graphicsMemory)
    {
      Drawable.ReadEmbeddedResources(systemMemory, graphicsMemory);
    }

    #endregion

    #region Implementation of IDisposable

    public void Dispose()
    {
      if (Drawable != null)
      {
        Drawable.Dispose();
      }
    }

    #endregion
  }
}
