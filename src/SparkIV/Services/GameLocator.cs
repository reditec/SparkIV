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

using System.IO;
using RageLib.Common;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Finds a game installation and the key for its archives.
  /// </summary>
  internal static class GameLocator
  {
    /// <summary>
    /// GTA IV: The Complete Edition: one GTAIV.exe, the episodes in the TLAD and TBoGT subfolders.
    /// </summary>
    public static bool IsCompleteEdition(string gamePath)
    {
      return System.IO.File.Exists(Path.Combine(gamePath, "GTAIV.exe")) &&
             (System.IO.Directory.Exists(Path.Combine(gamePath, "TBoGT")) || System.IO.Directory.Exists(Path.Combine(gamePath, "TLAD")));
    }

    /// <summary>
    /// The installation found automatically (working directory, path file, registry), or null.
    /// For the episodes the Complete Edition counts too: it has no EFLC.exe and only registers GTA IV.
    /// </summary>
    public static string? FindGameDirectory(bool episodes, out KeyUtil keyUtil)
    {
      keyUtil = episodes ? new KeyUtilEFLC() : new KeyUtilGTAIV();
      string? path = keyUtil.TryFindGameDirectory();
      if (path != null && System.IO.File.Exists(Path.Combine(path, keyUtil.ExecutableName)))
      {
        return path;
      }

      if (episodes)
      {
        var iv = new KeyUtilGTAIV();
        string? ivPath = iv.TryFindGameDirectory();
        if (ivPath != null && IsCompleteEdition(ivPath))
        {
          keyUtil = iv;
          return ivPath;
        }
      }

      return null;
    }

    /// <summary>
    /// Checks a folder the user picked. Returns the key util to use, or null if it isn't the game.
    /// </summary>
    public static KeyUtil? Check(string path, bool episodes)
    {
      KeyUtil keyUtil = episodes ? new KeyUtilEFLC() : new KeyUtilGTAIV();
      if (System.IO.File.Exists(Path.Combine(path, keyUtil.ExecutableName)))
      {
        return keyUtil;
      }
      if (episodes && IsCompleteEdition(path))
      {
        return new KeyUtilGTAIV();
      }
      return null;
    }
  }
}
