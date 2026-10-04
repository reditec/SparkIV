using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RageLib.FileSystem;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Where the map objects' models (.wdr, .wft) are: in the archives of pc\data\maps of each game installed
  /// (GTA IV, TLAD, TBoGT). Read once while SparkIV runs, when first needed (the archives' tables of
  /// contents only, a second or two).
  /// </summary>
  internal static class GameModels
  {
    private static readonly object Gate = new object();
    private static Dictionary<string, List<string>>? s_archivesOf;

    /// <summary>
    /// The archives holding a model of this name (without extension), those of the game that is open first;
    /// empty if no game folder is known or none has it.
    /// </summary>
    public static List<string> ArchivesWith(string modelName)
    {
      lock (Gate)
      {
        s_archivesOf ??= ReadIndex();
        if (!s_archivesOf.TryGetValue(modelName, out var archives))
        {
          return new List<string>();
        }
        string? current = Games.Current?.Folder;
        return archives.OrderBy(a => current != null && a.StartsWith(current, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
      }
    }

    /// <summary>The game folders whose maps are searched: GTA IV's, then each episode's.</summary>
    private static IEnumerable<string> Folders()
    {
      string? iv = Games.InstallFolder(Games.GtaIV, "Windows");
      string? episodes = Games.InstallFolder(Games.Episodes, "Windows");
      if (iv != null)
      {
        yield return iv;
      }
      if (episodes != null)
      {
        yield return Path.Combine(episodes, "TLAD");
        yield return Path.Combine(episodes, "TBoGT");
      }
    }

    private static Dictionary<string, List<string>> ReadIndex()
    {
      var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
      foreach (var folder in Folders().Distinct(StringComparer.OrdinalIgnoreCase))
      {
        string maps = Path.Combine(folder, "pc", "data", "maps");
        if (!System.IO.Directory.Exists(maps))
        {
          continue;
        }
        IEnumerable<string> archives;
        try
        {
          archives = System.IO.Directory.EnumerateFiles(maps, "*.img", SearchOption.AllDirectories).ToList();
        }
        catch (Exception)
        {
          continue;
        }
        foreach (var path in archives)
        {
          var fs = new IMGFileSystem();
          try
          {
            fs.Open(path);
            foreach (var file in fs.RootDirectory.OfType<File>())
            {
              string extension = Path.GetExtension(file.Name);
              if (extension.Equals(".wdr", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wft", StringComparison.OrdinalIgnoreCase))
              {
                string name = Path.GetFileNameWithoutExtension(file.Name);
                if (!index.TryGetValue(name, out var list))
                {
                  index[name] = list = new List<string>();
                }
                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                  list.Add(path);
                }
              }
            }
          }
          catch (Exception)
          {
            // An archive that can't be read: its models aren't found.
          }
          finally
          {
            try
            {
              fs.Close();
            }
            catch (Exception)
            {
            }
          }
        }
      }
      return index;
    }
  }
}
