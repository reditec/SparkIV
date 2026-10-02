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
using System.IO;
using RageLib.Models.Data;
using RageLib.Models.Resource;
using RageLib.Textures;

namespace RageLib.Models
{
  public class ModelFragTypeFile : IModelFile
  {
    internal File<FragTypeModel> File { get; private set; }

    public void Open(string filename)
    {
      File = new File<FragTypeModel>();
      File.Open(filename);
    }

    public void Open(Stream stream)
    {
      File = new File<FragTypeModel>();
      File.Open(stream);
    }

    public TextureFile EmbeddedTextureFile
    {
      get { return File.Data.Drawable.ShaderGroup.TextureDictionary; }
    }

    public ModelNode GetModel(TextureFile[] textures)
    {
      return ModelGenerator.GenerateModel(File.Data, textures);
    }

    public Drawable GetDataModel()
    {
      return new Drawable(File.Data.Drawable);
    }

    /// <summary>All the fragment's children (the parts that break off), with their bone, whether or not they have a model.</summary>
    public List<FragmentChild> GetChildren()
    {
      var children = new List<FragmentChild>();
      foreach (var child in File.Data.Children)
      {
        bool hasModel = child.Drawable != null && child.Drawable.ModelCollection.Length > 0;
        children.Add(new FragmentChild(child.GroupIndex, child.BoneIndex, child.PristineMass, child.DamagedMass,
                                       child.Matrix1, child.Matrix2, hasModel ? new Drawable(child.Drawable) : null));
      }
      return children;
    }

    /// <summary>The drawables of the fragment's children (the parts that break off), those that have a model.</summary>
    public List<Drawable> GetChildDataModels()
    {
      var children = new List<Drawable>();
      foreach (var child in File.Data.Children)
      {
        if (child.Drawable != null && child.Drawable.ModelCollection.Length > 0)
        {
          children.Add(new Drawable(child.Drawable));
        }
      }
      return children;
    }

    #region Implementation of IDisposable

    public void Dispose()
    {
      if (File != null)
      {
        File.Dispose();
        File = null;
      }
    }

    #endregion
  }
}
