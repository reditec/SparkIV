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
