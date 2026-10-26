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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SparkIV.WinUI
{
  /// <summary>
  /// The changelog (CHANGELOG.md, shipped next to the program): all versions (Settings > About) or the part of one
  /// version (after an update). Its "## " headings are the versions, its "- " lines their changes.
  /// </summary>
  internal static class ChangelogDialog
  {
    private sealed class Section
    {
      public string Title = "";
      public List<string> Items = new List<string>();
    }

    private static string FilePath => System.IO.Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");

    /// <summary>Shows the changelog; with a version, only its section if there is one (else all).</summary>
    public static async Task ShowAsync(XamlRoot root, string? version = null)
    {
      List<Section> sections;
      try
      {
        sections = Read(System.IO.File.ReadAllLines(FilePath));
      }
      catch (Exception ex)
      {
        sections = new List<Section> { new Section { Title = "Could not read " + FilePath, Items = { ex.Message } } };
      }

      string title = "Changelog";
      if (version != null)
      {
        var own = sections.Find(s => s.Title == "v" + version || s.Title.StartsWith("v" + version + " ", StringComparison.Ordinal));
        if (own != null)
        {
          sections = new List<Section> { own };
          title = "What's new in SparkIV " + version;
        }
      }

      var list = new StackPanel { Spacing = 4 };
      foreach (var section in sections)
      {
        list.Children.Add(new TextBlock
        {
          Text = section.Title,
          Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
          Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 0),
          IsTextSelectionEnabled = true,
        });
        foreach (string item in section.Items)
        {
          list.Children.Add(new TextBlock { Text = "•  " + item, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
      }

      var dialog = new ContentDialog
      {
        XamlRoot = root,
        Title = title,
        CloseButtonText = "Close",
        DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer { MaxHeight = 420, Content = list },
      };
      await dialog.ShowAsync();
    }

    private static List<Section> Read(string[] lines)
    {
      var sections = new List<Section>();
      foreach (string raw in lines)
      {
        string line = raw.Trim();
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
          sections.Add(new Section { Title = Plain(line.Substring(3)) });
        }
        else if (sections.Count > 0 && (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
        {
          sections[sections.Count - 1].Items.Add(Plain(line.Substring(2)));
        }
      }
      return sections;
    }

    // Markdown's links as their text, without its emphasis.
    private static string Plain(string text) =>
      Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("*", "").Replace("`", "").Trim();
  }
}
