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
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// Any file as bytes: offset, hex and text columns, a selection to copy, "go to offset" and a data
  /// inspector that reads the bytes at the caret as numbers.
  /// </summary>
  public sealed partial class HexView : UserControl
  {
    // Whether the inspector is shown; remembered while SparkIV runs.
    private static bool s_showInspector = true;

    private static readonly string[] InspectorNames =
      { "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "Binary" };

    private readonly ViewerContext _context;
    private readonly byte[] _data;
    private readonly string? _partName;
    private readonly TextBlock[] _values = new TextBlock[InspectorNames.Length];

    private bool _started;
    private bool _pageReady;
    private int _caret;
    private int _selectionStart;
    private int _selectionLength;

    /// <param name="partName">For a part of a file (the system memory of a resource, say): what it is
    /// called, also in the name the export suggests ("system" → name.system.bin). Null for the whole file.</param>
    public HexView(ViewerContext context, byte[] data, string? partName = null)
    {
      InitializeComponent();
      _context = context;
      _data = data;
      _partName = partName;
      if (partName != null)
      {
        ExportButton.Label = "Export this part";
        ToolTipService.SetToolTip(ExportButton, "Save the bytes shown here (the " + partName + " part, decompressed) as a file");
      }
      Loaded += HexView_Loaded;
      ActualThemeChanged += (sender, args) => Post(ViewerPages.Theme(this));

      BuildInspector();
      InspectorButton.IsChecked = s_showInspector;
      InspectorPane.Visibility = s_showInspector ? Visibility.Visible : Visibility.Collapsed;
      Update(0, 0, data.Length > 0 ? 1 : 0);
    }

    private async void HexView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;

      if (!await ViewerPages.StartAsync(Web, "SparkIV.HexView.html", _context, Core_WebMessageReceived, allowCopyMenu: false))
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
          Post(ViewerPages.Theme(this));
          Post(new { type = "data", base64 = Convert.ToBase64String(_data) });
          Working.IsActive = false;
          break;

        case "caret":
          Update(root.GetProperty("offset").GetInt32(), root.GetProperty("start").GetInt32(), root.GetProperty("length").GetInt32());
          break;

        case "copy":
          Copy(root.GetProperty("ascii").GetBoolean());
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

    private void Update(int caret, int start, int length)
    {
      _caret = caret;
      _selectionStart = start;
      _selectionLength = length;

      string text = "Offset 0x" + caret.ToString("X") + " (" + caret.ToString("N0") + ")";
      if (length > 1)
      {
        text += "   \u00B7   " + length.ToString("N0") + " bytes selected (0x" + start.ToString("X") + " \u2013 0x" + (start + length - 1).ToString("X") + ")";
      }
      text += "   \u00B7   " + _data.Length.ToString("N0") + " bytes";
      Status.Text = text;

      UpdateInspector();
    }

    #region Data inspector

    private void BuildInspector()
    {
      for (int i = 0; i < InspectorNames.Length; i++)
      {
        InspectorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { Text = InspectorNames[i], Style = (Style)Resources["InspectorNameStyle"] };
        Grid.SetRow(name, i);
        InspectorGrid.Children.Add(name);

        var value = new TextBlock
        {
          IsTextSelectionEnabled = true,
          FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
          TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(value, i);
        Grid.SetColumn(value, 1);
        InspectorGrid.Children.Add(value);
        _values[i] = value;
      }
    }

    private void UpdateInspector()
    {
      ReadOnlySpan<byte> at = _caret < _data.Length ? _data.AsSpan(_caret) : ReadOnlySpan<byte>.Empty;
      var c = CultureInfo.CurrentCulture;
      string none = "\u2013";

      _values[0].Text = at.Length >= 1 ? ((sbyte)at[0]).ToString(c) : none;
      _values[1].Text = at.Length >= 1 ? at[0].ToString(c) : none;
      _values[2].Text = at.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(at).ToString("N0", c) : none;
      _values[3].Text = at.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(at).ToString("N0", c) : none;
      _values[4].Text = at.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(at).ToString("N0", c) : none;
      _values[5].Text = at.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(at).ToString("N0", c) : none;
      _values[6].Text = at.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(at).ToString("N0", c) : none;
      _values[7].Text = at.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(at).ToString("N0", c) : none;
      _values[8].Text = at.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(at).ToString("G9", c) : none;
      _values[9].Text = at.Length >= 8 ? BinaryPrimitives.ReadDoubleLittleEndian(at).ToString("G17", c) : none;
      _values[10].Text = at.Length >= 1 ? Convert.ToString(at[0], 2).PadLeft(8, '0') : none;
    }

    private void Inspector_Click(object sender, RoutedEventArgs e)
    {
      s_showInspector = InspectorButton.IsChecked == true;
      InspectorPane.Visibility = s_showInspector ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Go to, copy, export

    private void GoToBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
      if (e.Key != VirtualKey.Enter)
      {
        return;
      }
      e.Handled = true;

      string text = GoToBox.Text.Trim();
      if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      {
        text = text.Substring(2);
      }
      if (long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long offset) && offset < _data.Length)
      {
        Post(new { type = "goto", offset });
        Web.Focus(FocusState.Programmatic);
      }
      else
      {
        _context.ShowMessage("Can't go there", "\"" + GoToBox.Text + "\" is not an offset in this file (0 \u2013 0x" + Math.Max(0, _data.Length - 1).ToString("X") + ").", true);
      }
    }

    private void CopyHex_Click(object sender, RoutedEventArgs e) => Copy(ascii: false);

    private void CopyText_Click(object sender, RoutedEventArgs e) => Copy(ascii: true);

    /// <summary>Copies the selection (at least the byte at the caret) as hex ("4D 45 54") or as text.</summary>
    private void Copy(bool ascii)
    {
      if (_data.Length == 0)
      {
        return;
      }
      int start = Math.Clamp(_selectionStart, 0, _data.Length - 1);
      int length = Math.Clamp(_selectionLength, 1, _data.Length - start);
      var bytes = _data.AsSpan(start, length);

      string text;
      if (ascii)
      {
        // Like the text column: bytes that aren't printable become dots.
        var sb = new StringBuilder(length);
        foreach (byte b in bytes)
        {
          sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        text = sb.ToString();
      }
      else
      {
        text = Convert.ToHexString(bytes);
        var sb = new StringBuilder(length * 3);
        for (int i = 0; i < text.Length; i += 2)
        {
          if (i > 0)
          {
            sb.Append(' ');
          }
          sb.Append(text, i, 2);
        }
        text = sb.ToString();
      }

      var package = new DataPackage();
      package.SetText(text);
      Clipboard.SetContent(package);
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _partName == null
        ? _context.File.Name
        : System.IO.Path.GetFileNameWithoutExtension(_context.File.Name) + "." + _partName + ".bin";
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, System.IO.Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    #endregion
  }
}
