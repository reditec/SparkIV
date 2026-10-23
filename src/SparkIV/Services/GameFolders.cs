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
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace SparkIV.WinUI.Services
{
  /// <summary>Asking for the folder of a game, from the settings, the game choice and the main window.</summary>
  internal static class GameFolders
  {
    /// <summary>
    /// Asks for the folder of a game on a platform and remembers it. Folder null if cancelled or not the
    /// game; then error says why (null when cancelled).
    /// </summary>
    public static async Task<(string? Folder, string? Error)> PickAsync(Window owner, Game game, string platform)
    {
      var picker = new FolderPicker();
      picker.FileTypeFilter.Add("*");
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
      var picked = await picker.PickSingleFolderAsync();
      if (picked == null)
      {
        return (null, null);
      }

      if (game.Check(picked.Path) == null)
      {
        return (null, picked.Path + " does not contain " +
                      (game == Games.Episodes ? "EFLC.exe or the GTAIV.exe of the Complete Edition." : "GTAIV.exe."));
      }
      AppSettings.SetGamePath(game.Id, platform, picked.Path);
      return (picked.Path, null);
    }
  }
}
