/**********************************************************************\

 Spark IV
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
using System.Collections.Generic;
using System.IO;

namespace SparkIV.Explorer
{
  /// <summary>
  /// What a file is, for the Type column, the list groups and the icon.
  /// </summary>
  internal class FileType
  {
    public FileType(string group, int groupOrder, string name, string iconExtension)
    {
      Group = group;
      GroupOrder = groupOrder;
      Name = name;
      IconExtension = iconExtension;
    }

    public string Group { get; private set; }
    public int GroupOrder { get; private set; }
    public string Name { get; private set; }

    /// <summary>The extension whose shell icon represents this type.</summary>
    public string IconExtension { get; private set; }
  }

  internal static class FileTypes
  {
    public static readonly FileType Folder = new FileType("Folders", 0, "Folder", null);

    private const string Archives = "Archives";
    private const string Textures = "Textures";
    private const string Models = "Models";
    private const string Audio = "Audio";
    private const string Scripts = "Scripts";
    private const string Text = "Text and Data";
    private const string Web = "Web Pages";
    private const string Programs = "Programs";
    private const string Other = "Other Files";

    private static readonly Dictionary<string, int> GroupOrders = new Dictionary<string, int>
    {
      [Archives] = 1, [Textures] = 2, [Models] = 3, [Audio] = 4, [Scripts] = 5,
      [Text] = 6, [Web] = 7, [Programs] = 8, [Other] = 9,
    };

    private static readonly Dictionary<string, FileType> Known = new Dictionary<string, FileType>(StringComparer.OrdinalIgnoreCase);

    static FileTypes()
    {
      Add(Archives, ".zip", ".rpf", "RPF Archive");
      Add(Archives, ".zip", ".img", "IMG Archive");

      Add(Textures, ".png", ".wtd", "Texture Dictionary");
      Add(Textures, ".png", ".wtx", "Texture Dictionary");
      Add(Textures, ".png", ".dds", "DDS Texture");

      Add(Models, ".obj", ".wdr", "Model");
      Add(Models, ".obj", ".wdd", "Model Dictionary");
      Add(Models, ".obj", ".wft", "Fragment Model");
      Add(Models, ".obj", ".wbd", "Collision Dictionary");
      Add(Models, ".obj", ".wbn", "Collision Bounds");
      Add(Models, ".obj", ".wad", "Animation Dictionary");
      Add(Models, ".obj", ".wnv", "Navigation Mesh");
      Add(Models, ".obj", ".wpl", "Placement Data");

      Add(Audio, ".wav", ".ivaud", "Audio Bank");
      Add(Audio, ".wav", ".wav", "Wave Audio");

      Add(Scripts, ".bat", ".sco", "Compiled Script");

      foreach (var ext in new[] { ".dat", ".txt", ".ide", ".ipl", ".ped", ".cmb", ".csv", ".ini", ".list", ".dcl", ".sps", ".nod", ".cfg" })
      {
        Add(Text, ".txt", ext, ext.Substring(1).ToUpperInvariant() + " Data");
      }
      Add(Text, ".xml", ".xml", "XML Document");
      Add(Text, ".txt", ".gxt", "Game Text");

      Add(Web, ".htm", ".whm", "Web Page");

      Add(Programs, ".exe", ".exe", "Application");
      Add(Programs, ".dll", ".dll", "Dynamic-link Library");
    }

    private static void Add(string group, string iconExtension, string extension, string name)
    {
      Known[extension] = new FileType(group, GroupOrders[group], name, iconExtension);
    }

    public static FileType ForFile(string fileName)
    {
      string ext = Path.GetExtension(fileName);
      FileType type;
      if (Known.TryGetValue(ext, out type))
      {
        return type;
      }

      // Like Explorer: "ABC File", or just "File" without an extension.
      string name = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() + " File" : "File";
      return new FileType(Other, GroupOrders[Other], name, ext.Length > 1 ? ext : ".");
    }
  }
}
