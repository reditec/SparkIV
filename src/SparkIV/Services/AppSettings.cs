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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// What SparkIV remembers between runs, in %LocalAppData%\SparkIV\settings.json: the folders of the
  /// games and of the backups, how cutscenes are shown.
  /// </summary>
  internal static class AppSettings
  {
    private sealed class Data
    {
      public Dictionary<string, string> GamePaths { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      public string? BackupFolder { get; set; }

      public bool CutsceneSeeThroughLikeGame { get; set; } = true;

      public string? UpdateChannel { get; set; }
    }

    /// <summary>The update channel chosen ("Alpha", "Beta", "Release"); null for the one of the running version.</summary>
    public static string? UpdateChannel
    {
      get => Load().UpdateChannel;
      set
      {
        Load().UpdateChannel = value;
        Save();
      }
    }

    /// <summary>
    /// Cutscenes: leave out the see-through parts the game doesn't draw (a part a variation adds, see
    /// CutsceneMessages.HideUnflaggedSeeThrough) rather than show everything the .cut asks for.
    /// </summary>
    public static bool CutsceneSeeThroughLikeGame
    {
      get => Load().CutsceneSeeThroughLikeGame;
      set
      {
        Load().CutsceneSeeThroughLikeGame = value;
        Save();
      }
    }

    /// <summary>Where the originals of changed files are kept; null for the default (Documents\SparkIV\Backups).</summary>
    public static string? BackupFolder
    {
      get => Load().BackupFolder;
      set
      {
        Load().BackupFolder = value;
        Save();
      }
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
