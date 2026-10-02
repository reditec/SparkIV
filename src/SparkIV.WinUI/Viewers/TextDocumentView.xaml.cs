using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// Read-only, selectable text with line numbers, copy, save, export of the original file and an
  /// optional choice of formats (for scripts: disassembly, decompiled ...).
  /// </summary>
  public sealed partial class TextDocumentView : UserControl
  {
    // Whether the function list is shown; remembered while SparkIV runs.
    private static bool s_showFunctions = true;

    // "void main()" / "function sub_55(var0)" in decompiled code, "a2: FnBegin ..." in the disassembly.
    private static readonly Regex HeaderPattern = new Regex(
      @"^(?:(?:void|function)\s+(?<name>[A-Za-z_]\w*)\s*\(|(?<offset>[0-9a-fA-F]+): FnBegin\b)", RegexOptions.Compiled);

    private readonly ViewerContext _context;
    private Func<int, string>? _render;
    private string _textExtension = ".txt";
    private string _text = "";
    private readonly string _language;
    private int _lineCount = 1;
    private List<FunctionEntry> _functions = new List<FunctionEntry>();
    private bool _allFolded;

    private bool _started;
    private bool _pageReady;
    private bool _textSent;

    /// <param name="language">How to highlight: "script", "xml", "data", or "" for none.</param>
    public TextDocumentView(ViewerContext context, string text, string language)
    {
      InitializeComponent();
      _context = context;
      _language = language;
      Loaded += TextDocumentView_Loaded;
      ActualThemeChanged += (sender, args) => SendTheme();
      ShowText(text);
    }

    /// <summary>
    /// Offers several renderings of the file. <paramref name="render"/> gets the index of the chosen
    /// format and runs on a background thread.
    /// </summary>
    public void SetFormats(IList<string> names, int selected, Func<int, string> render, string textExtension)
    {
      _render = render;
      _textExtension = textExtension;
      FormatContainer.Visibility = Visibility.Visible;
      foreach (var name in names)
      {
        FormatBox.Items.Add(name);
      }
      FormatBox.SelectedIndex = selected;
    }

    private async void TextDocumentView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;

      if (!await ViewerPages.StartAsync(Web, "SparkIV.TextDocumentView.html", _context, Core_WebMessageReceived, allowCopyMenu: true))
      {
        Working.IsActive = false;
      }
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
      using var message = JsonDocument.Parse(args.WebMessageAsJson);
      var root = message.RootElement;
      switch (root.GetProperty("type").GetString())
      {
        case "ready":
          _pageReady = true;
          SendTheme();
          Post(new { type = "wrap", on = WrapButton.IsChecked == true });
          SendText();
          break;

        case "caret":
          int line = root.GetProperty("line").GetInt32();
          UpdateCaretStatus(line, root.GetProperty("column").GetInt32(), root.GetProperty("selected").GetInt32());
          SelectFunctionAt(line);
          break;
      }
    }

    private void Post(object message)
    {
      if (_pageReady)
      {
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
      }
    }

    private void ShowText(string text)
    {
      _text = text;
      _lineCount = 1;
      for (int i = 0; i < text.Length; i++)
      {
        if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
        {
          _lineCount++;
        }
      }
      _textSent = false;
      FindFunctions();
      SendText();
      UpdateCaretStatus(1, 1, 0);
    }

    private void SendText()
    {
      if (_pageReady && !_textSent)
      {
        _textSent = true;
        Post(new { type = "text", text = _text, language = _language });
        if (_render == null || FormatBox.IsEnabled)
        {
          Working.IsActive = false;
        }
      }
    }

    private void SendTheme() => Post(ViewerPages.Theme(this));

    private void UpdateCaretStatus(int line, int column, int selected)
    {
      string lines = _lineCount == 1 ? "1 line" : _lineCount.ToString("N0") + " lines";
      string text = "Ln " + line.ToString("N0") + ", Col " + column.ToString("N0") + "   \u00B7   " + lines;
      if (selected > 0)
      {
        text += "   \u00B7   " + selected.ToString("N0") + " selected";
      }
      CaretStatus.Text = text;
    }

    private async void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_render == null || FormatBox.SelectedIndex < 0)
      {
        return;
      }

      int index = FormatBox.SelectedIndex;
      Working.IsActive = true;
      FormatBox.IsEnabled = false;
      try
      {
        ShowText(await Task.Run(() => _render(index)));
      }
      catch (Exception ex)
      {
        ShowText("");
        _context.ShowMessage("Could not show this format", ex.Message, true);
      }
      finally
      {
        FormatBox.IsEnabled = true;
        Working.IsActive = !_textSent;
      }
    }

    /// <summary>A function of a script and the line its header is on.</summary>
    private sealed class FunctionEntry
    {
      public FunctionEntry(string name, int line)
      {
        Name = name;
        Line = line;
      }

      public string Name { get; }
      public int Line { get; }
      public override string ToString() => Name;
    }

    private void FindFunctions()
    {
      var functions = new List<FunctionEntry>();
      if (_language == "script")
      {
        int line = 1;
        int start = 0;
        while (start <= _text.Length)
        {
          int end = _text.IndexOf('\n', start);
          if (end < 0)
          {
            end = _text.Length;
          }
          // Headers start at column 0, so most lines are ruled out by their first character.
          if (end > start && (_text[start] == 'v' || _text[start] == 'f' || Uri.IsHexDigit(_text[start])))
          {
            var match = HeaderPattern.Match(_text.Substring(start, Math.Min(end - start, 200)));
            if (match.Success)
            {
              string name = match.Groups["name"].Success
                ? match.Groups["name"].Value
                : Convert.ToInt32(match.Groups["offset"].Value, 16) == 0 ? "main" : "sub_" + match.Groups["offset"].Value;
              functions.Add(new FunctionEntry(name, line));
            }
          }
          start = end + 1;
          line++;
        }
      }

      _functions = functions;
      FunctionList.ItemsSource = functions;
      FunctionHeader.Text = functions.Count == 1 ? "1 function" : functions.Count.ToString("N0") + " functions";
      bool any = functions.Count > 0;
      FunctionsButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
      FunctionsButton.IsChecked = s_showFunctions;
      FunctionPane.Visibility = any && s_showFunctions ? Visibility.Visible : Visibility.Collapsed;
      bool canFold = _language == "script" || _language == "xml" || (_language == "data" && _text.Contains('{'));
      FoldButton.Visibility = canFold ? Visibility.Visible : Visibility.Collapsed;
      // New text starts unfolded.
      _allFolded = false;
      UpdateFoldButton();
    }

    /// <summary>Marks the function the caret is in.</summary>
    private void SelectFunctionAt(int line)
    {
      int index = -1;
      for (int i = 0; i < _functions.Count && _functions[i].Line <= line; i++)
      {
        index = i;
      }
      if (FunctionList.SelectedIndex != index)
      {
        FunctionList.SelectedIndex = index;
        if (index >= 0)
        {
          FunctionList.ScrollIntoView(_functions[index]);
        }
      }
    }

    private void FunctionList_ItemClick(object sender, ItemClickEventArgs e)
    {
      var function = (FunctionEntry)e.ClickedItem;
      Post(new { type = "goto", line = function.Line });
      Web.Focus(FocusState.Programmatic);
    }

    private void Functions_Click(object sender, RoutedEventArgs e)
    {
      s_showFunctions = FunctionsButton.IsChecked == true;
      FunctionPane.Visibility = s_showFunctions && _functions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One button that collapses everything and then offers to expand it again.</summary>
    private void FoldAll_Click(object sender, RoutedEventArgs e)
    {
      _allFolded = !_allFolded;
      Post(new { type = "foldAll", on = _allFolded });
      UpdateFoldButton();
    }

    private void UpdateFoldButton()
    {
      FoldButton.Label = _allFolded ? "Expand all" : "Collapse all";
      FoldIcon.Glyph = _allFolded ? "\uE70D" : "\uE70E";
    }

    // The numbers stay on the first row of a wrapped line.
    private void Wrap_Click(object sender, RoutedEventArgs e) => Post(new { type = "wrap", on = WrapButton.IsChecked == true });

    private void CopyAll_Click(object sender, RoutedEventArgs e) => SetClipboard(_text);

    private static void SetClipboard(string text)
    {
      var package = new DataPackage();
      package.SetText(text);
      Clipboard.SetContent(package);
    }

    private async void SaveText_Click(object sender, RoutedEventArgs e)
    {
      string name = Path.GetFileNameWithoutExtension(_context.File.Name) + _textExtension;
      string? path = await PickSaveFileAsync(name, _textExtension);
      if (path != null)
      {
        await System.IO.File.WriteAllTextAsync(path, _text);
        _context.ShowMessage("Saved", path, false);
      }
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string? path = await PickSaveFileAsync(_context.File.Name, Path.GetExtension(_context.File.Name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    private Task<string?> PickSaveFileAsync(string suggestedName, string extension) =>
      ViewerPages.PickSaveFileAsync(_context.Window, suggestedName, extension);
  }
}
