using System;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Changing game files has to be turned on, once per run, after a warning (like OpenIV's edit mode).
  /// It has nothing to do with administrator rights: those are only asked for by Windows when a save
  /// writes into a protected folder (see <see cref="FileInstaller"/>).
  /// </summary>
  internal static class EditMode
  {
    public static bool IsOn { get; private set; }

    /// <summary>Raised when edit mode is turned on or off, for the buttons it enables.</summary>
    public static event Action? Changed;

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
      using var identity = WindowsIdentity.GetCurrent();
      return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Whether SparkIV may write into a folder as it runs now: tried with an empty file of its own, which
    /// is removed again.
    /// </summary>
    public static bool CanWrite(string? folder)
    {
      if (folder == null || !System.IO.Directory.Exists(folder))
      {
        return true;
      }
      string probe = Path.Combine(folder, ".sparkiv-write-test-" + Guid.NewGuid().ToString("N"));
      try
      {
        using (System.IO.File.Create(probe, 1, FileOptions.DeleteOnClose))
        {
        }
        return true;
      }
      catch (UnauthorizedAccessException)
      {
        return false;
      }
      catch (IOException)
      {
        return false;
      }
    }

    /// <summary>Whether writing into this folder needs administrator rights (Windows asks when saving).</summary>
    public static bool NeedsAdministrator(string? folder) => !IsElevated && !CanWrite(folder);

    public static void TurnOff()
    {
      if (IsOn)
      {
        IsOn = false;
        Changed?.Invoke();
      }
    }

    /// <summary>Asks before turning edit mode on. True if it is on now.</summary>
    public static async Task<bool> TurnOnAsync(XamlRoot root)
    {
      if (IsOn)
      {
        return true;
      }

      var dialog = new ContentDialog
      {
        XamlRoot = root,
        Title = "Turn on edit mode?",
        Content = Message("In edit mode you can change game files: import files, replace textures, save archives.\n\n" +
                          "Changes are written into the files when you save. Before a file is replaced the first time, " +
                          "SparkIV keeps its original in " + Backups.Folder + " (set in Settings)."),
        PrimaryButtonText = "Turn on",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Close,
      };
      if (await dialog.ShowAsync() != ContentDialogResult.Primary)
      {
        return false;
      }
      IsOn = true;
      Changed?.Invoke();
      return true;
    }

    private static UIElement Message(string text)
    {
      var grid = new Grid { ColumnSpacing = 12 };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      grid.Children.Add(new FontIcon
      {
        Glyph = "",
        FontSize = 28,
        VerticalAlignment = VerticalAlignment.Top,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
      });
      var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
      Grid.SetColumn(block, 1);
      grid.Children.Add(block);
      return grid;
    }
  }
}
