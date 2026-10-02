using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// What SparkIV remembers between runs, in %LocalAppData%\SparkIV\settings.json: for now the folders of
  /// the games.
  /// </summary>
  internal static class AppSettings
  {
    private sealed class Data
    {
      public Dictionary<string, string> GamePaths { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string FilePath =
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkIV", "settings.json");

    private static Data? s_data;

    private static Data Load()
    {
      if (s_data == null)
      {
        try
        {
          s_data = System.IO.File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(System.IO.File.ReadAllText(FilePath)) : null;
        }
        catch (Exception)
        {
          // A broken file is started over.
          s_data = null;
        }
        s_data ??= new Data();
        s_data.GamePaths = new Dictionary<string, string>(s_data.GamePaths, StringComparer.OrdinalIgnoreCase);
      }
      return s_data;
    }

    // A folder per game and platform: "gtaiv/windows".
    private static string Key(string gameId, string platform) => gameId + "/" + platform.ToLowerInvariant();

    /// <summary>The folder set for a game ("gtaiv") on a platform ("Windows"), or null.</summary>
    public static string? GetGamePath(string gameId, string platform) =>
      Load().GamePaths.TryGetValue(Key(gameId, platform), out var path) ? path : null;

    public static void SetGamePath(string gameId, string platform, string path)
    {
      Load().GamePaths[Key(gameId, platform)] = path;
      Save();
    }

    /// <summary>Forgets the folder set: then the one found automatically is used again, or it is asked for.</summary>
    public static void RemoveGamePath(string gameId, string platform)
    {
      if (Load().GamePaths.Remove(Key(gameId, platform)))
      {
        Save();
      }
    }

    private static void Save()
    {
      var data = Load();
      try
      {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
      }
      catch (Exception)
      {
        // Then it is asked again next time.
      }
    }
  }
}
