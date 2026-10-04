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
using RageLib.Models.Resource;

namespace RageLib.Models.Data
{
  public class Drawable
  {
    public List<Model> Models { get; private set; }

    /// <summary>The level of detail of each entry of Models: 0 high, 1 medium, 2 low, 3 very low (slots may be empty).</summary>
    public List<int> LevelsOfDetail { get; private set; }
    public List<Material> Materials { get; private set; }
    public Skeleton Skeleton { get; private set; }
    public Textures.TextureFile AttachedTexture { get; private set; }

    /// <summary>The model's lights (street lamps, neon, spots); empty for most.</summary>
    public List<Light> Lights { get; private set; }

    internal Drawable(DrawableModel drawableModel)
    {
      Lights = new List<Light>();
      foreach (var light in drawableModel.Lights)
      {
        Lights.Add(new Light(light));
      }

      if (drawableModel.ShaderGroup != null)
      {
        Materials = new List<Material>(drawableModel.ShaderGroup.Shaders.Count);
        foreach (var info in drawableModel.ShaderGroup.Shaders)
        {
          Materials.Add(new Material(info));
        }
      }
      else
      {
        Materials = new List<Material>();
      }

      if (drawableModel.Skeleton != null)
      {
        Skeleton = new Skeleton(drawableModel.Skeleton);
      }

      Models = new List<Model>(drawableModel.ModelCollection.Length);
      foreach (var info in drawableModel.ModelCollection)
      {
        Models.Add(new Model(info));
      }
      LevelsOfDetail = new List<int>(drawableModel.LevelsOfDetail);

      AttachedTexture = drawableModel.ShaderGroup.TextureDictionary;
    }
  }
}
