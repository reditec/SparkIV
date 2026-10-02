using System;
using System.IO;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Before SparkIV writes a file on disk, the original is copied into the backup folder, once: the copy
  /// is always the file as it was before the first change. Files of a game are kept by game and path in
  /// the game ("Grand Theft Auto IV\pc\data\cdimages\vehicles.img"), others by drive and path
  /// ("Other\D\Mods\vehicles.img").
  /// </summary>
  internal static class Backups
  {
    public static string DefaultFolder { get; } =
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SparkIV", "Backups");

    public static string Folder => AppSettings.BackupFolder ?? DefaultFolder;

    /// <summary>Where the original of a file is kept.</summary>
    public static string PathFor(string file)
    {
      string full = Path.GetFullPath(file);
      if (Games.ForFile(full) is GameInstall install)
      {
        return Path.Combine(Folder, install.Game.Name, Path.GetRelativePath(install.Folder, full));
      }

      string root = Path.GetPathRoot(full) ?? "";
      string drive = root.TrimEnd('\\', '/', ':').Replace(":", "").Replace("\\", "_").TrimStart('_');
      return Path.Combine(Folder, "Other", drive.Length > 0 ? drive : "_", full.Substring(root.Length));
    }

    /// <summary>
    /// Keeps the original of a file before it is written, unless one is kept already. Returns where it is
    /// (null if the file isn't there yet).
    /// </summary>
    public static string? Keep(string file)
    {
      if (!System.IO.File.Exists(file))
      {
        return null;
      }
      string backup = PathFor(file);
      if (!System.IO.File.Exists(backup))
      {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        // Copied under another name first, so a copy cut short (a full disk) never counts as the original.
        string part = backup + ".part";
        System.IO.File.Copy(file, part, overwrite: true);
        System.IO.File.Move(part, backup);
      }
      return backup;
    }
  }
}
