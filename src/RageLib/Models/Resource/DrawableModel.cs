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

using System;
using System.Diagnostics;
using System.IO;
using RageLib.Common;
using RageLib.Common.Resources;
using RageLib.Common.ResourceTypes;
using RageLib.Models.Resource.Models;
using RageLib.Models.Resource.Shaders;
using RageLib.Models.Resource.Skeletons;
using RageLib.Textures;

namespace RageLib.Models.Resource
{
  // gtaDrawable : rmcDrawable : rmcDrawableBase
  class DrawableModel : PGBase, IFileAccess, IDataReader, IEmbeddedResourceReader, IDisposable
  {
    public ShaderGroup ShaderGroup { get; set; }
    public Skeleton Skeleton { get; private set; }

    public Vector4 Center { get; private set; }
    public Vector4 BoundsMin { get; private set; }
    public Vector4 BoundsMax { get; private set; }

    public PtrCollection<Model>[] ModelCollection { get; private set; }

    /// <summary>For each entry of ModelCollection its level of detail: 0 high, 1 medium, 2 low, 3 very low.</summary>
    public int[] LevelsOfDetail { get; private set; }

    public Vector4 AbsoluteMax { get; private set; }

    private uint Unk1 { get; set; }     // either 1 or 9

    private uint Neg1 { get; set; }
    private uint Neg2 { get; set; }
    private uint Neg3 { get; set; }

    private float Unk2 { get; set; }

    private uint Unk3 { get; set; }
    private uint Unk4 { get; set; }
    private uint Unk5 { get; set; }

    /// <summary>The drawable's lights (pointer, then count and capacity as two ushorts); empty without.</summary>
    public LightAttributes[] Lights { get; private set; } = new LightAttributes[0];

    public void ReadData(BinaryReader br)
    {
      foreach (var geometryInfo in ModelCollection)
      {
        foreach (var info in geometryInfo)
        {
          foreach (var dataInfo in info.Geometries)
          {
            dataInfo.VertexBuffer.ReadData(br);
            dataInfo.IndexBuffer.ReadData(br);
          }
        }
      }
    }

    #region IFileAccess Members

    public new void Read(BinaryReader br)
    {
      base.Read(br);

      // rage::rmcDrawableBase
      //    rage::rmcDrawable
      //        gtaDrawable

      var shaderGroupOffset = ResourceUtil.ReadOffset(br);
      var skeletonOffset = ResourceUtil.ReadOffset(br);

      Center = new Vector4(br);
      BoundsMin = new Vector4(br);
      BoundsMax = new Vector4(br);

      int levelOfDetailCount = 0;
      var modelOffsets = new uint[4];
      for (int i = 0; i < 4; i++)
      {
        modelOffsets[i] = ResourceUtil.ReadOffset(br);
        if (modelOffsets[i] != 0)
        {
          levelOfDetailCount++;
        }
      }

      AbsoluteMax = new Vector4(br);

      Unk1 = br.ReadUInt32();

      Neg1 = br.ReadUInt32();
      Neg2 = br.ReadUInt32();
      Neg3 = br.ReadUInt32();

      Unk2 = br.ReadSingle();

      Unk3 = br.ReadUInt32();
      Unk4 = br.ReadUInt32();
      Unk5 = br.ReadUInt32();

      // Collection<LightAttrs>: in the drawables of models (gtaDrawable); a fragment's drawable has no such
      // collection there, so only a pointer (0x5...) with a count no larger than its capacity counts.
      uint lightsPointer = br.ReadUInt32();
      int lightCount = br.ReadUInt16();
      int lightCapacity = br.ReadUInt16();
      uint lightsOffset = (lightsPointer >> 28) == 5 && lightCount <= lightCapacity ? lightsPointer & 0x0FFFFFFF : 0;

      // The data follows:

      if (shaderGroupOffset != 0)
      {
        br.BaseStream.Seek(shaderGroupOffset, SeekOrigin.Begin);
        ShaderGroup = new ShaderGroup(br);
      }

      if (skeletonOffset != 0)
      {
        br.BaseStream.Seek(skeletonOffset, SeekOrigin.Begin);
        Skeleton = new Skeleton(br);
      }

      if (lightsOffset != 0 && lightCount > 0)
      {
        Lights = new LightAttributes[lightCount];
        for (int i = 0; i < lightCount; i++)
        {
          br.BaseStream.Seek(lightsOffset + i * LightAttributes.Size, SeekOrigin.Begin);
          Lights[i] = new LightAttributes(br);
        }
      }

      // The four slots are high, medium, low and very low; any of them may be empty, also one in the middle.
      ModelCollection = new PtrCollection<Model>[levelOfDetailCount];
      LevelsOfDetail = new int[levelOfDetailCount];
      int found = 0;
      for (int slot = 0; slot < 4; slot++)
      {
        if (modelOffsets[slot] == 0)
        {
          continue;
        }
        br.BaseStream.Seek(modelOffsets[slot], SeekOrigin.Begin);
        ModelCollection[found] = new PtrCollection<Model>(br);
        LevelsOfDetail[found] = slot;
        found++;
      }
    }

    public new void Write(BinaryWriter bw)
    {
      throw new NotImplementedException();
    }

    #endregion

    #region Implementation of IEmbeddedResourceReader

    public void ReadEmbeddedResources(Stream systemMemory, Stream graphicsMemory)
    {
      if (ShaderGroup.TextureDictionaryOffset != 0)
      {
        systemMemory.Seek(ShaderGroup.TextureDictionaryOffset, SeekOrigin.Begin);

        ShaderGroup.TextureDictionary = new TextureFile();
        ShaderGroup.TextureDictionary.Open(systemMemory, graphicsMemory);
      }
    }

    #endregion

    #region Implementation of IDisposable

    public void Dispose()
    {
      if (ShaderGroup != null)
      {
        if (ShaderGroup.TextureDictionary != null)
        {
          ShaderGroup.TextureDictionary.Dispose();
          ShaderGroup.TextureDictionary = null;
        }
      }
    }

    #endregion
  }
}
