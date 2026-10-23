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

namespace RageLib.Models.Resource.Shaders
{
  /// <summary>
  /// A parameter of several vectors: its type byte is how many (gta_trees has two of 16 and one of 8; a
  /// matrix is 4 and has its own class, one vector 1, a texture 0).
  /// </summary>
  internal class ShaderParamVectorArray : IShaderParam
  {
    private readonly int _count;

    public ShaderParamVectorArray(int count)
    {
      _count = count;
    }

    public Vector4[] Data { get; private set; }

    public void Read(BinaryReader br)
    {
      Data = new Vector4[_count];
      for (int i = 0; i < _count; i++)
      {
        Data[i] = new Vector4(br);
      }
    }

    public void Write(BinaryWriter bw)
    {
    }
  }
}
