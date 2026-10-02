using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RageLib.Common;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// A game SparkIV can open the files of. More RAGE games (and their platforms) are added here.
  /// </summary>
  internal sealed class Game
  {
    public Game(string id, string name, string shortName, string[] platforms, string[] contentFolders,
                Func<string, KeyUtil?> check, Func<string?> find)
    {
      Id = id;
      Name = name;
      ShortName = shortName;
      Platforms = platforms;
      ContentFolders = contentFolders;
      _check = check;
      _find = find;
    }

    private readonly Func<string, KeyUtil?> _check;
    private readonly Func<string?> _find;

    /// <summary>The name in the settings ("gtaiv").</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>For the tile when there is no icon ("IV").</summary>
    public string ShortName { get; }

    /// <summary>The platforms SparkIV reads this game's files of.</summary>
    public IReadOnlyList<string> Platforms { get; }

    /// <summary>
    /// The folders of the install that hold the game's content (pc\html, common\text), nearest first:
    /// the episodes, then the folder of the install itself ("").
    /// </summary>
    public IReadOnlyList<string> ContentFolders { get; }

    /// <summary>The key util for a folder of this game, or null if the folder isn't the game.</summary>
    public KeyUtil? Check(string folder) => System.IO.Directory.Exists(folder) ? _check(folder) : null;

    /// <summary>The install found without asking (working directory, path file, registry), or null.</summary>
    public string? FindInstall() => _find();

    public override string ToString() => Name;
  }

  /// <summary>A game and where it is installed.</summary>
  internal sealed record GameInstall(Game Game, string Folder, string Platform)
  {
    public KeyUtil KeyUtil => Game.Check(Folder)!;
  }

  internal static class Games
  {
    public static Game GtaIV { get; } = new Game(
      "gtaiv", "Grand Theft Auto IV", "IV", new[] { "Windows" }, new[] { "" },
      folder => System.IO.File.Exists(Path.Combine(folder, "GTAIV.exe")) ? new KeyUtilGTAIV() : null,
      () => GameLocator.FindGameDirectory(episodes: false, out _));

    public static Game Episodes { get; } = new Game(
      "eflc", "Episodes from Liberty City", "EFLC", new[] { "Windows" }, new[] { "TBoGT", "TLAD", "" },
      folder => GameLocator.Check(folder, episodes: true),
      () => GameLocator.FindGameDirectory(episodes: true, out _));

    /// <summary>In the order the choice offers them.</summary>
    public static IReadOnlyList<Game> All { get; } = new[] { GtaIV, Episodes };

    /// <summary>
    /// The game of what is open: models find their textures through its .ide files, web pages copied out
    /// of it its texts and other sites. Null when that isn't known (an archive on its own).
    /// </summary>
    public static GameInstall? Current { get; private set; }

    public static void SetCurrent(GameInstall? install)
    {
      Current = install;
      GameTextures.SetGameFolder(install?.Folder);
    }

    /// <summary>
    /// Where the game is installed for a platform: the folder set in the settings, else (Windows) the one
    /// found. Null if neither.
    /// </summary>
    public static string? InstallFolder(Game game, string platform)
    {
      string? stored = AppSettings.GetGamePath(game.Id, platform);
      if (stored != null && game.Check(stored) != null)
      {
        return stored;
      }
      return platform == "Windows" ? game.FindInstall() : null;
    }

    /// <summary>
    /// The game a file on disk belongs to, from the folders above it: an install of a game, or a folder
    /// chosen for one before. In the Complete Edition (one GTAIV.exe) a file in TBoGT or TLAD is of the
    /// episodes. Null if the file isn't in a game.
    /// </summary>
    public static GameInstall? ForFile(string path)
    {
      string full = Path.GetFullPath(path);
      for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir != null; dir = dir.Parent)
      {
        string folder = dir.FullName;
        if (System.IO.File.Exists(Path.Combine(folder, "EFLC.exe")))
        {
          return new GameInstall(Episodes, folder, "Windows");
        }
        if (System.IO.File.Exists(Path.Combine(folder, "GTAIV.exe")))
        {
          string first = Path.GetRelativePath(folder, full).Split(Path.DirectorySeparatorChar)[0];
          bool episode = Episodes.ContentFolders.Any(f => f.Length > 0 && f.Equals(first, StringComparison.OrdinalIgnoreCase)) &&
                         GameLocator.IsCompleteEdition(folder);
          return new GameInstall(episode ? Episodes : GtaIV, folder, "Windows");
        }
      }
      return null;
    }

    /// <summary>Whether a file can't be opened without knowing its game: archives (their key), web pages (the game's texts).</summary>
    public static bool NeedsGame(string fileName)
    {
      return Volumes.IsArchiveName(fileName) || Path.GetExtension(fileName).Equals(".whm", StringComparison.OrdinalIgnoreCase);
    }
  }
}
