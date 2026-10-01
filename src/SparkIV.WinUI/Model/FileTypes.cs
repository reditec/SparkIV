using System;
using System.Collections.Generic;
using System.IO;

namespace SparkIV.WinUI.Model
{
  /// <summary>
  /// What a file is, for the Type column, the list groups and the icon.
  /// </summary>
  public sealed class FileType
  {
    public FileType(string group, int groupOrder, string name, string glyph)
    {
      Group = group;
      GroupOrder = groupOrder;
      Name = name;
      Glyph = glyph;
    }

    public string Group { get; }
    public int GroupOrder { get; }
    public string Name { get; }

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; }

    public bool IsArchive => GroupOrder == 1;
  }

  public static class FileTypes
  {
    // Segoe Fluent Icons
    private const string FolderGlyph = "\uE8B7";
    private const string ArchiveGlyph = "\uF012";
    private const string ImageGlyph = "\uEB9F";
    private const string ModelGlyph = "\uF158";
    private const string AudioGlyph = "\uE8D6";
    private const string ScriptGlyph = "\uE943";
    private const string TextGlyph = "\uE8A5";
    private const string WebGlyph = "\uE774";
    private const string ProgramGlyph = "\uECAA";
    private const string FileGlyph = "\uE7C3";

    public static readonly FileType Folder = new FileType("Folders", 0, "Folder", FolderGlyph);

    private static readonly Dictionary<string, FileType> Known = new Dictionary<string, FileType>(StringComparer.OrdinalIgnoreCase);

    static FileTypes()
    {
      Add("Archives", 1, ArchiveGlyph, ".rpf", "RPF Archive");
      Add("Archives", 1, ArchiveGlyph, ".img", "IMG Archive");

      Add("Textures", 2, ImageGlyph, ".wtd", "Texture Dictionary");
      Add("Textures", 2, ImageGlyph, ".wtx", "Texture Dictionary");
      Add("Textures", 2, ImageGlyph, ".dds", "DDS Texture");

      Add("Models", 3, ModelGlyph, ".wdr", "Model");
      Add("Models", 3, ModelGlyph, ".wdd", "Model Dictionary");
      Add("Models", 3, ModelGlyph, ".wft", "Fragment Model");
      Add("Models", 3, ModelGlyph, ".wbd", "Collision Dictionary");
      Add("Models", 3, ModelGlyph, ".wbn", "Collision Bounds");
      Add("Models", 3, ModelGlyph, ".wad", "Animation Dictionary");
      Add("Models", 3, ModelGlyph, ".wnv", "Navigation Mesh");
      Add("Models", 3, ModelGlyph, ".wpl", "Placement Data");

      Add("Audio", 4, AudioGlyph, ".ivaud", "Audio Bank");
      Add("Audio", 4, AudioGlyph, ".wav", "Wave Audio");

      Add("Scripts", 5, ScriptGlyph, ".sco", "Compiled Script");

      foreach (var ext in new[] { ".dat", ".txt", ".ide", ".ipl", ".ped", ".cmb", ".csv", ".ini", ".list", ".dcl", ".sps", ".nod", ".cfg" })
      {
        Add("Text and Data", 6, TextGlyph, ext, ext.Substring(1).ToUpperInvariant() + " Data");
      }
      Add("Text and Data", 6, TextGlyph, ".xml", "XML Document");
      Add("Text and Data", 6, TextGlyph, ".gxt", "Game Text");

      Add("Web Pages", 7, WebGlyph, ".whm", "Web Page");

      Add("Programs", 8, ProgramGlyph, ".exe", "Application");
      Add("Programs", 8, ProgramGlyph, ".dll", "Dynamic-link Library");
    }

    private static void Add(string group, int order, string glyph, string extension, string name)
    {
      Known[extension] = new FileType(group, order, name, glyph);
    }

    public static FileType ForFile(string fileName)
    {
      string ext = Path.GetExtension(fileName);
      if (Known.TryGetValue(ext, out var type))
      {
        return type;
      }

      // Like Explorer: "ABC File", or just "File" without an extension.
      string name = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() + " File" : "File";
      return new FileType("Other Files", 9, name, FileGlyph);
    }
  }
}
