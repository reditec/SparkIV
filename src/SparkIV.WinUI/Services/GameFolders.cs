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
