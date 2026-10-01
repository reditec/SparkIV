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
    private const string MapGlyph = "\uE707";
    private const string AnimationGlyph = "\uE768";
    private const string ShaderGlyph = "\uE790";
    private const string VideoGlyph = "\uE714";
    private const string FileGlyph = "\uE7C3";

    public static readonly FileType Folder = new FileType("Folders", 0, "Folder", FolderGlyph);

    private static readonly Dictionary<string, FileType> Known = new Dictionary<string, FileType>(StringComparer.OrdinalIgnoreCase);

    // Placement lists ending in "_e.wpl" are the extended ones; the extension alone can't tell them apart.
    private static readonly FileType ExtendedPlacement = new FileType("Map and Collision", 4, "eXtended Placement Lists", MapGlyph);

    // The names are those used for GTA IV and the Episodes from Liberty City.
    static FileTypes()
    {
      Add("Archives", 1, ArchiveGlyph, ".img", "GTA Image File");
      Add("Archives", 1, ArchiveGlyph, ".rpf", "Rage Package File");

      Add("Textures and Images", 2, ImageGlyph, ".wtd", "Texture dictionary");
      Add("Textures and Images", 2, ImageGlyph, ".wtx", "Texture dictionary");
      Add("Textures and Images", 2, ImageGlyph, ".dds", "DDS Texture");
      Add("Textures and Images", 2, ImageGlyph, ".png", "Portable Network Graphics");
      Add("Textures and Images", 2, ImageGlyph, ".tag", "GTA IV Clip Thumbs image");

      Add("Models", 3, ModelGlyph, ".wdr", "Drawable object");
      Add("Models", 3, ModelGlyph, ".wdd", "Drawable dictionary");
      Add("Models", 3, ModelGlyph, ".wft", "Fragment object");
      Add("Models", 3, ModelGlyph, ".wbs", "Blendshapes");

      Add("Map and Collision", 4, MapGlyph, ".ide", "Item Definition Entries");
      Add("Map and Collision", 4, MapGlyph, ".ipl", "Item Placement Lists");
      Add("Map and Collision", 4, MapGlyph, ".wpl", "Placement Lists");
      Add("Map and Collision", 4, MapGlyph, ".wbd", "Collision dictionary");
      Add("Map and Collision", 4, MapGlyph, ".wbn", "Static collision");
      Add("Map and Collision", 4, MapGlyph, ".wnv", "Nav mesh");
      Add("Map and Collision", 4, MapGlyph, ".nod", "Paths");

      Add("Animation", 5, AnimationGlyph, ".wad", "Animation dictionary");
      Add("Animation", 5, AnimationGlyph, ".cut", "CUT scene");
      Add("Animation", 5, AnimationGlyph, ".rrr", "Car Recordings");
      Add("Animation", 5, AnimationGlyph, ".cli", "GTA IV Clip");

      Add("Audio", 6, AudioGlyph, ".ivaud", "GTA IV Audio Files");
      Add("Audio", 6, AudioGlyph, ".wav", "Wave Audio");
      foreach (var ext in new[] { ".dat11", ".dat12", ".dat15", ".dat16" })
      {
        Add("Audio", 6, AudioGlyph, ext, "Audio Metadata");
      }

      Add("Scripts", 7, ScriptGlyph, ".sco", "Compiled Scripts");

      Add("Text and Data", 8, TextGlyph, ".dat", "Data");
      Add("Text and Data", 8, TextGlyph, ".txt", "Plain Text");
      Add("Text and Data", 8, TextGlyph, ".xml", "XML text");
      Add("Text and Data", 8, TextGlyph, ".gxt", "GTA text table");
      Add("Text and Data", 8, TextGlyph, ".ped", "Ped information");
      Add("Text and Data", 8, TextGlyph, ".cmb", "Combat information");
      Add("Text and Data", 8, TextGlyph, ".tune", "Tune");
      Add("Text and Data", 8, TextGlyph, ".cfg", "Config");
      Add("Text and Data", 8, TextGlyph, ".ini", "Config text");
      Add("Text and Data", 8, TextGlyph, ".list", "List");
      Add("Text and Data", 8, TextGlyph, ".srl", "Streaming");
      Add("Text and Data", 8, TextGlyph, ".csv", "CSV File");

      Add("Shaders and Effects", 9, ShaderGlyph, ".fxc", "Compiled shader");
      Add("Shaders and Effects", 9, ShaderGlyph, ".sps", "Shader presets");
      Add("Shaders and Effects", 9, ShaderGlyph, ".dcl", "DCL File");
      Add("Shaders and Effects", 9, ShaderGlyph, ".wpfl", "Particle effects library");

      Add("Web Pages", 10, WebGlyph, ".whm", "HTML object");

      Add("Videos and Documents", 11, VideoGlyph, ".bik", "Bink Video");
      Add("Videos and Documents", 11, TextGlyph, ".pdf", "PDF File");
      Add("Videos and Documents", 11, FileGlyph, ".art", "ART File");

      Add("Programs", 12, ProgramGlyph, ".exe", "Application");
      Add("Programs", 12, ProgramGlyph, ".dll", "Dynamic-link Library");
    }

    private static void Add(string group, int order, string glyph, string extension, string name)
    {
      Known[extension] = new FileType(group, order, name, glyph);
    }

    public static FileType ForFile(string fileName)
    {
      if (fileName.EndsWith("_e.wpl", StringComparison.OrdinalIgnoreCase))
      {
        return ExtendedPlacement;
      }

      string ext = Path.GetExtension(fileName);
      if (Known.TryGetValue(ext, out var type))
      {
        return type;
      }

      // Like Explorer: "ABC File", or just "File" without an extension.
      string name = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() + " File" : "File";
      return new FileType("Other Files", 13, name, FileGlyph);
    }
  }
}
