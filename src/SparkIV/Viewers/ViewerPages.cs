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
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// The views that are small pages in a WebView2 (text, hex): one shared browser environment, locked-down
  /// settings, and the page itself, embedded in the assembly.
  /// </summary>
  internal static class ViewerPages
  {
    private static Task<CoreWebView2Environment>? s_environment;

    /// <summary>One browser environment for all viewers; its data lives in the user's profile, not next to the exe.</summary>
    private static Task<CoreWebView2Environment> SharedEnvironment()
    {
      if (s_environment == null)
      {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkIV", "WebView2");
        s_environment = CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions()).AsTask();
      }
      return s_environment;
    }

    /// <summary>
    /// Starts the browser in <paramref name="web"/> and shows the embedded page <paramref name="resource"/>
    /// (if given; otherwise the caller navigates). Returns false (after telling the user) if the WebView2
    /// runtime is missing.
    /// </summary>
    public static async Task<bool> StartAsync(WebView2 web, string? resource, ViewerContext context,
                                              Action<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs> onMessage,
                                              bool allowCopyMenu)
    {
      bool Closed() => context.Window is ViewerWindow window && window.IsClosed;
      try
      {
        await web.EnsureCoreWebView2Async(await SharedEnvironment());
      }
      catch (Exception ex)
      {
        if (!Closed())
        {
          context.ShowMessage("Could not show the file", "The Microsoft Edge WebView2 runtime is needed for this view. " + ex.Message, true);
        }
        return false;
      }

      // Starting the browser takes a moment; the window may have been closed meanwhile, and then there is none.
      var core = web.CoreWebView2;
      if (core == null || Closed())
      {
        return false;
      }
      var settings = core.Settings;
      settings.AreDevToolsEnabled = false;
      settings.AreBrowserAcceleratorKeysEnabled = false;
      settings.AreHostObjectsAllowed = false;
      settings.IsStatusBarEnabled = false;
      settings.IsGeneralAutofillEnabled = false;
      settings.IsPasswordAutosaveEnabled = false;
      core.ContextMenuRequested += (sender, args) => FilterContextMenu(args, allowCopyMenu);
      core.NewWindowRequested += (sender, args) => args.Handled = true;
      core.WebMessageReceived += (sender, args) => onMessage(sender, args);

      if (resource != null)
      {
        using var page = typeof(ViewerPages).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(page);
        web.NavigateToString(reader.ReadToEnd());
      }
      return true;
    }

    /// <summary>The page's context menu only offers what makes sense for read-only content.</summary>
    private static void FilterContextMenu(CoreWebView2ContextMenuRequestedEventArgs args, bool allowCopyMenu)
    {
      var items = args.MenuItems;
      for (int i = items.Count - 1; i >= 0; i--)
      {
        if (!allowCopyMenu || (items[i].Name != "copy" && items[i].Name != "selectAll"))
        {
          items.RemoveAt(i);
        }
      }
      if (items.Count == 0)
      {
        args.Handled = true;
      }
    }

    /// <summary>Asks where to save a file; null if the user cancels.</summary>
    public static async Task<string?> PickSaveFileAsync(Window window, string suggestedName, string extension)
    {
      var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
      if (string.IsNullOrEmpty(extension))
      {
        extension = ".bin";
      }
      picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant() + " file", new System.Collections.Generic.List<string> { extension });
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
      var file = await picker.PickSaveFileAsync();
      return file?.Path;
    }

    /// <summary>The colours the pages need from the app's theme.</summary>
    public static object Theme(FrameworkElement element)
    {
      var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
      string selection = "rgba(" + accent.R + ", " + accent.G + ", " + accent.B + ", 0.45)";
      return new { type = "theme", dark = element.ActualTheme == ElementTheme.Dark, selection };
    }
  }
}
