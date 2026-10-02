using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RageLib.FileSystem;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Which texture dictionaries a model of the game uses, as the game finds them: the .ide files say the
  /// dictionary of each model (and of each model dictionary), their txdp sections the parent dictionaries
  /// searched after it, and the shared dictionaries are in gtxd.img. Known once a game folder is open.
  /// </summary>
  internal static class GameTextures
  {
    private static readonly object Gate = new object();
    private static string? s_gameFolder;
    private static Dictionary<string, string>? s_dictionaryOfModel;
    private static Dictionary<string, string>? s_parentOf;
    private static Dictionary<string, File>? s_shared;
    private static readonly List<IMGFileSystem> s_open = new List<IMGFileSystem>();

    /// <summary>The folder of the game that is open, or null; the index is made again when it is needed.</summary>
    public static void SetGameFolder(string? folder)
    {
      lock (Gate)
      {
        s_gameFolder = folder;
        s_dictionaryOfModel = null;
        s_modelOfHash = null;
        s_parentOf = null;
        s_shared = null;
        foreach (var fs in s_open)
        {
          try
          {
            fs.Close();
          }
          catch (Exception)
          {
          }
        }
        s_open.Clear();
      }
    }

    /// <summary>
    /// The names of the dictionaries for a model or model dictionary (file name without extension), the
    /// model's own first, then its parents; empty if the game doesn't say.
    /// </summary>
    public static List<string> DictionariesFor(string modelName)
    {
      lock (Gate)
      {
        var names = new List<string>();
        if (!EnsureIndex() || !s_dictionaryOfModel!.TryGetValue(modelName, out var name))
        {
          return names;
        }
        // Parents can't loop in the game's data, but a broken mod might; stop after a few.
        while (name != null && names.Count < 8 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
          names.Add(name);
          name = s_parentOf!.TryGetValue(name, out var parent) ? parent : null;
        }
        return names;
      }
    }

    /// <summary>
    /// The name of a model by its hash (as the entries of model dictionaries are stored), from the .ide
    /// files; null if no .ide has it.
    /// </summary>
    public static string? ModelName(uint hash)
    {
      lock (Gate)
      {
        if (!EnsureIndex())
        {
          return null;
        }
        if (s_modelOfHash == null)
        {
          s_modelOfHash = new Dictionary<uint, string>();
          foreach (var name in s_dictionaryOfModel!.Keys)
          {
            s_modelOfHash.TryAdd(RageLib.Text.GxtFile.Hash(name), name);
          }
        }
        return s_modelOfHash.TryGetValue(hash, out var found) ? found : null;
      }
    }

    private static Dictionary<uint, string>? s_modelOfHash;

    /// <summary>The bytes of a shared texture dictionary of the game (from gtxd.img), or null.</summary>
    public static byte[]? ReadShared(string name)
    {
      // The archive is read here, under the lock, so two viewers don't read it at the same time.
      lock (Gate)
      {
        return EnsureIndex() && s_shared!.TryGetValue(name, out var file) ? file.GetData() : null;
      }
    }

    private static bool EnsureIndex()
    {
      if (s_dictionaryOfModel != null)
      {
        return true;
      }
      if (s_gameFolder == null || !System.IO.Directory.Exists(s_gameFolder))
      {
        return false;
      }

      var dictionaryOfModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var ide in EnumerateFiles(s_gameFolder, "*.ide"))
      {
        ReadIde(ide, dictionaryOfModel, parentOf);
      }

      var shared = new Dictionary<string, File>(StringComparer.OrdinalIgnoreCase);
      foreach (var path in EnumerateFiles(s_gameFolder, "gtxd.img"))
      {
        try
        {
          var fs = new IMGFileSystem();
          fs.Open(path);
          s_open.Add(fs);
          foreach (var file in fs.RootDirectory.OfType<File>().Where(f => f.Name.EndsWith(".wtd", StringComparison.OrdinalIgnoreCase)))
          {
            shared.TryAdd(Path.GetFileNameWithoutExtension(file.Name), file);
          }
        }
        catch (Exception)
        {
          // Without it the shared textures just aren't found.
        }
      }

      s_dictionaryOfModel = dictionaryOfModel;
      s_parentOf = parentOf;
      s_shared = shared;
      return true;
    }

    private static IEnumerable<string> EnumerateFiles(string folder, string pattern)
    {
      try
      {
        return System.IO.Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).ToList();
      }
      catch (Exception)
      {
        return Array.Empty<string>();
      }
    }

    /// <summary>
    /// objs, tobj and anim lines start with the model and its dictionary; the last field of an objs line may
    /// name the model dictionary (.wdd) the model is in. txdp lines are dictionary, parent.
    /// </summary>
    private static void ReadIde(string path, Dictionary<string, string> dictionaryOfModel, Dictionary<string, string> parentOf)
    {
      string section = "";
      IEnumerable<string> lines;
      try
      {
        lines = System.IO.File.ReadLines(path);
      }
      catch (Exception)
      {
        return;
      }
      foreach (var raw in lines)
      {
        string line = raw.Trim();
        int comment = line.IndexOf('#');
        if (comment >= 0)
        {
          line = line.Substring(0, comment).Trim();
        }
        if (line.Length == 0)
        {
          continue;
        }
        if (!line.Contains(','))
        {
          section = line.Equals("end", StringComparison.OrdinalIgnoreCase) ? "" : line.ToLowerInvariant();
          continue;
        }

        string[] fields = line.Split(',').Select(f => f.Trim()).ToArray();
        if (fields.Length < 2)
        {
          continue;
        }
        switch (section)
        {
          case "objs":
          case "tobj":
          case "anim":
            dictionaryOfModel.TryAdd(fields[0], fields[1]);
            string last = fields[fields.Length - 1];
            if (fields.Length > 2 && last.Length > 0 && !char.IsDigit(last[0]) && last[0] != '-' && last[0] != '.')
            {
              dictionaryOfModel.TryAdd(last, fields[1]);
            }
            break;
          case "txdp":
            parentOf.TryAdd(fields[0], fields[1]);
            break;
        }
      }
    }
  }
}
