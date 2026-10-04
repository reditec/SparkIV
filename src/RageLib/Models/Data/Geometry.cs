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

using System.Collections.Generic;

namespace RageLib.Models.Data
{
  public class Geometry
  {
    public List<Mesh> Meshes { get; private set; }

    /// <summary>
    /// The bone (index in the drawable's skeleton) the meshes move with when they aren't skinned; their
    /// vertices are then relative to that bone (et_cargoship36's parts on bones 1 and 2 lie about the origin).
    /// </summary>
    public int BoneIndex { get; private set; }

    /// <summary>Whether the meshes' vertices carry bones and weights of their own (peds, flags).</summary>
    public bool IsSkinned { get; private set; }

    internal Geometry(Resource.Models.Model info)
    {
      BoneIndex = info.BoneIndex;
      IsSkinned = info.IsSkinned;
      int index = 0;
      Meshes = new List<Mesh>(info.Geometries.Count);
      foreach (var dataInfo in info.Geometries)
      {
        var mesh = new Mesh(dataInfo);
        mesh.MaterialIndex = info.ShaderMappings[index++];
        Meshes.Add(mesh);
      }
    }
  }
}