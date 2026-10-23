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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RageLib.Textures;
using SparkIV.WinUI.Model;
using Bitmap = System.Drawing.Bitmap;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A texture dictionary (.wtd): its textures with thumbnails, one of them large with zoom, mipmap
  /// level and colour channel, and export as PNG.
  /// </summary>
  public sealed partial class TextureView : UserControl, IDisposable
  {
    // Remembered while SparkIV runs.
    private static PreviewBackground s_background = PreviewBackground.Gradient;

    private readonly ViewerContext _context;
    private readonly TextureDictionary _dictionary;
    private readonly List<TextureRow> _rows;

    private bool _started;
    private int _renderVersion;
    private int _shownWidth;
    private int _shownHeight;

    public TextureView(ViewerContext context, TextureDictionary dictionary)
    {
      InitializeComponent();
      _context = context;
      _dictionary = dictionary;
      BackgroundBox.SelectedIndex = (int)s_background;
      ApplyBackground();
      HookPanning();

      // In alphabetical order, not in the order they are stored in.
      _rows = dictionary.Textures.Select((t, i) => new TextureRow(t, dictionary.Thumbnails[i].ToBitmap()))
                                 .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                                 .ToList();
      TextureList.ItemsSource = _rows.ToList();
      ExportAllButton.IsEnabled = _rows.Count > 0;
      _savedData = context.Data;
      ReplaceButton.Visibility = context.ReplaceFile != null ? Visibility.Visible : Visibility.Collapsed;

      Loaded += (sender, args) =>
      {
        if (_started)
        {
          return;
        }
        _started = true;
        if (_rows.Count > 0)
        {
          TextureList.SelectedIndex = 0;
        }
        else
        {
          InfoText.Text = "This dictionary has no textures.";
        }
      };
    }

    private Texture? Selected => (TextureList.SelectedItem as TextureRow)?.Texture;

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ShowRows(TextureList.SelectedItem);

    private void ShowRows(object? selected)
    {
      string filter = FilterBox.Text.Trim();
      TextureList.ItemsSource = filter.Length == 0
        ? _rows.ToList()
        : _rows.Where(r => r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
      TextureList.SelectedItem = selected;
    }

    /// <summary>Puts a picture in place of the selected texture, and the changed file back into its archive.</summary>
    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
      if (TextureList.SelectedItem is not TextureRow row || _context.ReplaceFile == null)
      {
        return;
      }
      // Changing a file needs edit mode; asked for here when it is off.
      if (!await _context.EnsureEditModeAsync())
      {
        return;
      }

      var picker = new Windows.Storage.Pickers.FileOpenPicker();
      foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
      {
        picker.FileTypeFilter.Add(type);
      }
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_context.Window));
      var picked = await picker.PickSingleFileAsync();
      if (picked == null)
      {
        return;
      }

      var texture = row.Texture;
      Working.IsActive = true;
      ReplaceButton.IsEnabled = false;
      try
      {
        byte[] original = _savedData;
        var (data, thumbnail) = await Task.Run(() =>
        {
          _dictionary.Replace(texture, picked.Path);
          return (_dictionary.Save(original), TexturePixels.Thumbnail(texture, 48));
        });
        _savedData = data;
        string note = await _context.ReplaceFile(data);

        // The row with the new thumbnail, and the texture shown again.
        int index = _rows.IndexOf(row);
        var updated = new TextureRow(texture, thumbnail.ToBitmap());
        _rows[index] = updated;
        ShowRows(updated);
        await RenderAsync(fit: false);
        _context.ShowMessage("Texture replaced", texture.TitleName + " is now " + picked.Name + " (" + texture.Width + " \u00D7 " + texture.Height + ", " +
                             texture.TextureType + "). " + note, false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not replace " + texture.TitleName, ex.Message, true);
      }
      finally
      {
        Working.IsActive = false;
        ReplaceButton.IsEnabled = true;
      }
    }

    // The file as it is now: at first as it was opened, after a replacement as it was put back.
    private byte[] _savedData = Array.Empty<byte>();

    private void TextureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var texture = Selected;
      if (texture == null)
      {
        return;
      }

      // The mipmap levels, each halving the size.
      LevelBox.Items.Clear();
      for (int level = 0; level < texture.Levels; level++)
      {
        LevelBox.Items.Add("Level " + level + "   \u00B7   " + texture.GetWidth(level) + " \u00D7 " + texture.GetHeight(level));
      }
      LevelBox.SelectedIndex = 0;  // Renders, and fits the new texture into the window.
    }

    // Both boxes also report selections while the view is being built; nothing is shown before it is loaded.
    private void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_started && LevelBox.SelectedIndex >= 0)
      {
        _ = RenderAsync(fit: true);
      }
    }

    private void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_started)
      {
        _ = RenderAsync(fit: false);
      }
    }

    private void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      s_background = (PreviewBackground)Math.Max(0, BackgroundBox.SelectedIndex);
      ApplyBackground();
    }

    // The checkerboard behind the texture keeps its size when zooming; only the texture scales.
    private const int CheckerSquare = 12;
    private WriteableBitmap? _checker;

    private void PreviewLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (s_background == PreviewBackground.Checkerboard &&
          (_checker == null || _checker.PixelWidth < e.NewSize.Width || _checker.PixelHeight < e.NewSize.Height))
      {
        ApplyBackground();
      }
    }

    /// <summary>A checkerboard at least as big as the preview, made again only when the preview outgrows it.</summary>
    private WriteableBitmap CheckerBitmap()
    {
      int width = RoundUp(Math.Max(256, (int)Math.Ceiling(PreviewArea.ActualWidth)));
      int height = RoundUp(Math.Max(256, (int)Math.Ceiling(PreviewArea.ActualHeight)));
      if (_checker != null && _checker.PixelWidth >= width && _checker.PixelHeight >= height)
      {
        return _checker;
      }

      var pixels = new byte[width * height * 4];
      for (int y = 0; y < height; y++)
      {
        for (int x = 0; x < width; x++)
        {
          byte v = ((x / CheckerSquare) + (y / CheckerSquare)) % 2 == 0 ? (byte)255 : (byte)232;
          int i = (y * width + x) * 4;
          pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
          pixels[i + 3] = 255;
        }
      }
      _checker = new WriteableBitmap(width, height);
      using (var stream = _checker.PixelBuffer.AsStream())
      {
        stream.Write(pixels, 0, pixels.Length);
      }
      _checker.Invalidate();
      return _checker;

      // Grows in steps, so resizing the window doesn't make a new one all the time.
      static int RoundUp(int value) => (value + 511) / 512 * 512;
    }

    private void ApplyBackground()
    {
      PreviewArea.Background = s_background switch
      {
        // Like OpenIV: slate blue at the top to almost black at the bottom.
        PreviewBackground.Gradient => new LinearGradientBrush
        {
          StartPoint = new Windows.Foundation.Point(0, 0),
          EndPoint = new Windows.Foundation.Point(0, 1),
          GradientStops =
          {
            new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x68, 0x78, 0x8A), Offset = 0 },
            new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x22, 0x25, 0x2A), Offset = 1 },
          },
        },
        PreviewBackground.Black => new SolidColorBrush(Microsoft.UI.Colors.Black),
        PreviewBackground.White => new SolidColorBrush(Microsoft.UI.Colors.White),
        _ => new ImageBrush
        {
          ImageSource = CheckerBitmap(),
          Stretch = Stretch.None,
          AlignmentX = AlignmentX.Left,
          AlignmentY = AlignmentY.Top,
        },
      };
    }

    /// <summary>Decodes the selected texture at the chosen level and channel and shows it.</summary>
    private async Task RenderAsync(bool fit)
    {
      var texture = Selected;
      int level = Math.Max(0, LevelBox.SelectedIndex);
      var channel = (PixelChannel)Math.Max(0, ChannelBox.SelectedIndex);
      if (texture == null)
      {
        return;
      }

      int version = ++_renderVersion;
      Working.IsActive = true;
      try
      {
        var pixels = await Task.Run(() => TexturePixels.Decode(texture, level).ForDisplay(channel, checkerboard: false));
        if (version != _renderVersion)
        {
          return;  // Another texture or level was chosen meanwhile.
        }

        Preview.Source = pixels.ToBitmap();
        _shownWidth = pixels.Width;
        _shownHeight = pixels.Height;
        Preview.Width = pixels.Width;
        Preview.Height = pixels.Height;

        InfoText.Text = texture.TitleName + "   \u00B7   " + texture.Width + " \u00D7 " + texture.Height + "   \u00B7   " + texture.TextureType +
                        "   \u00B7   " + (texture.Levels == 1 ? "1 mipmap level" : texture.Levels + " mipmap levels");
        if (fit)
        {
          // Big textures fit the window; small ones are shown at their real size.
          SetZoomCentred(Math.Min(1, FitZoom()));
        }
      }
      catch (Exception ex)
      {
        Preview.Source = null;
        _context.ShowMessage("Could not show " + texture.TitleName, ex.Message, true);
      }
      finally
      {
        if (version == _renderVersion)
        {
          Working.IsActive = false;
        }
      }
    }

    #region Zoom and moving

    // Zoom is the size on screen per texture pixel; the slider and the wheel cover 10 % to 500 %, like in Word,
    // Fit may go beyond.
    private const double MinZoom = 0.01;
    private const double MaxZoom = 32;
    private const double SliderMinZoom = 0.1;
    private const double SliderMaxZoom = 5;

    // At least this much of the texture stays in view when it is moved.
    private const double KeepVisible = 48;

    private double _zoom = 1;
    private double _x;  // Where the top left of the texture is in the view
    private double _y;
    private bool _updatingZoomSlider;

    private void ApplyTransform()
    {
      double width = _shownWidth * _zoom;
      double height = _shownHeight * _zoom;
      double viewWidth = PreviewViewport.ActualWidth;
      double viewHeight = PreviewViewport.ActualHeight;
      _x = Math.Clamp(_x, KeepVisible - width, Math.Max(KeepVisible - width, viewWidth - KeepVisible));
      _y = Math.Clamp(_y, KeepVisible - height, Math.Max(KeepVisible - height, viewHeight - KeepVisible));

      PreviewTransform.ScaleX = PreviewTransform.ScaleY = _zoom;
      PreviewTransform.TranslateX = Math.Round(_x);
      PreviewTransform.TranslateY = Math.Round(_y);
      UpdateZoomText();
    }

    /// <summary>The zoom at which the whole texture fits into the view, with a small margin.</summary>
    private double FitZoom()
    {
      // Before the first layout there is no view to fit into.
      if (_shownWidth == 0 || _shownHeight == 0 || PreviewViewport.ActualWidth < 1 || PreviewViewport.ActualHeight < 1)
      {
        return 1;
      }
      double width = Math.Max(1, PreviewViewport.ActualWidth - 24);
      double height = Math.Max(1, PreviewViewport.ActualHeight - 24);
      return Math.Clamp(Math.Min(width / _shownWidth, height / _shownHeight), MinZoom, MaxZoom);
    }

    /// <summary>Zooms keeping the spot at <paramref name="anchor"/> (in the view) where it is.</summary>
    private void SetZoom(double zoom, Windows.Foundation.Point anchor)
    {
      zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
      double textureX = (anchor.X - _x) / _zoom;
      double textureY = (anchor.Y - _y) / _zoom;
      _zoom = zoom;
      _x = anchor.X - textureX * zoom;
      _y = anchor.Y - textureY * zoom;
      ApplyTransform();
    }

    /// <summary>Zooms and puts the texture in the middle of the view.</summary>
    private void SetZoomCentred(double zoom)
    {
      _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
      _x = (PreviewViewport.ActualWidth - _shownWidth * _zoom) / 2;
      _y = (PreviewViewport.ActualHeight - _shownHeight * _zoom) / 2;
      ApplyTransform();
    }

    private Windows.Foundation.Point ViewCentre => new Windows.Foundation.Point(PreviewViewport.ActualWidth / 2, PreviewViewport.ActualHeight / 2);

    private void Fit_Click(object sender, RoutedEventArgs e) => SetZoomCentred(FitZoom());

    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1, ViewCentre);

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
      // Only what is in the view is drawn, and the texture keeps its place relative to the middle.
      PreviewViewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
      _x += (e.NewSize.Width - e.PreviousSize.Width) / 2;
      _y += (e.NewSize.Height - e.PreviousSize.Height) / 2;
      ApplyTransform();
    }

    /// <summary>Each notch of the wheel zooms by a fifth, around the mouse.</summary>
    private void PreviewViewport_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
      var point = e.GetCurrentPoint(PreviewViewport);
      int delta = point.Properties.MouseWheelDelta;
      if (delta == 0 || point.Properties.IsHorizontalMouseWheel)
      {
        return;
      }
      e.Handled = true;

      // From a zoom outside 10 % to 500 % (after Fit) the wheel leads back into that range.
      double wanted = _zoom * Math.Pow(1.2, delta / 120.0);
      SetZoom(Math.Clamp(wanted, Math.Min(SliderMinZoom, _zoom), Math.Max(SliderMaxZoom, _zoom)), point.Position);
    }

    // The slider runs from 0 to 1000 on a logarithmic scale from 10 % to 500 %.
    private static double SliderFromZoom(double zoom) =>
      Math.Clamp(1000 * Math.Log(zoom / SliderMinZoom) / Math.Log(SliderMaxZoom / SliderMinZoom), 0, 1000);

    private static double ZoomFromSlider(double value) => SliderMinZoom * Math.Pow(SliderMaxZoom / SliderMinZoom, value / 1000);

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
      if (!_updatingZoomSlider)
      {
        SetZoom(ZoomFromSlider(e.NewValue), ViewCentre);
      }
    }

    private void UpdateZoomText()
    {
      ZoomText.Text = Math.Round(_zoom * 100) + " %";
      _updatingZoomSlider = true;
      ZoomSlider.Value = SliderFromZoom(_zoom);
      _updatingZoomSlider = false;
    }

    // Dragging with the left button or a finger, and pinching, come as manipulations.
    private void PreviewViewport_ManipulationStarted(object sender, Microsoft.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e) =>
      ProtectedCursor = MoveCursor();

    private void PreviewViewport_ManipulationDelta(object sender, Microsoft.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs e)
    {
      _x += e.Delta.Translation.X;
      _y += e.Delta.Translation.Y;
      if (e.Delta.Scale != 1)
      {
        SetZoom(_zoom * e.Delta.Scale, e.Position);
      }
      else
      {
        ApplyTransform();
      }
    }

    private void PreviewViewport_ManipulationCompleted(object sender, Microsoft.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e) =>
      ProtectedCursor = null;

    // The middle button isn't a manipulation; it is followed with the pointer events.
    private bool _panning;
    private Windows.Foundation.Point _panLast;

    private void HookPanning()
    {
      PreviewViewport.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Preview_PointerPressed), true);
      PreviewViewport.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Preview_PointerMoved), true);
      PreviewViewport.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Preview_PointerReleased), true);
      PreviewViewport.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Preview_PointerReleased), true);
    }

    private void Preview_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
      var point = e.GetCurrentPoint(PreviewViewport);
      if (!point.Properties.IsMiddleButtonPressed)
      {
        return;
      }
      _panning = true;
      _panLast = point.Position;
      PreviewViewport.CapturePointer(e.Pointer);
      ProtectedCursor = MoveCursor();
      e.Handled = true;
    }

    private void Preview_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
      if (!_panning)
      {
        return;
      }
      var position = e.GetCurrentPoint(PreviewViewport).Position;
      _x += position.X - _panLast.X;
      _y += position.Y - _panLast.Y;
      _panLast = position;
      ApplyTransform();
      e.Handled = true;
    }

    private void Preview_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
      if (!_panning)
      {
        return;
      }
      _panning = false;
      PreviewViewport.ReleasePointerCapture(e.Pointer);
      ProtectedCursor = null;
    }

    private static Microsoft.UI.Input.InputCursor MoveCursor() =>
      Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);

    #endregion

    #region Export

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
      var texture = Selected;
      if (texture == null)
      {
        return;
      }

      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, texture.TitleName + ".png", ".png");
      if (path == null)
      {
        return;
      }

      try
      {
        await Task.Run(() => SavePng(texture, path));
        _context.ShowMessage("Exported", path, false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export " + texture.TitleName, ex.Message, true);
      }
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
      var picker = new Windows.Storage.Pickers.FolderPicker();
      picker.FileTypeFilter.Add("*");
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_context.Window));
      var folder = await picker.PickSingleFolderAsync();
      if (folder == null)
      {
        return;
      }

      Working.IsActive = true;
      try
      {
        var textures = _rows.Select(r => r.Texture).ToList();
        await Task.Run(() =>
        {
          foreach (var texture in textures)
          {
            SavePng(texture, Path.Combine(folder.Path, SafeFileName(texture.TitleName) + ".png"));
          }
        });
        _context.ShowMessage("Exported", (textures.Count == 1 ? "1 texture" : textures.Count + " textures") + " saved to " + folder.Path + ".", false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the textures", ex.Message, true);
      }
      finally
      {
        Working.IsActive = false;
      }
    }

    /// <summary>The full-size texture with its alpha channel, as it is in the game.</summary>
    private void SavePng(Texture texture, string path)
    {
      lock (_dictionary)
      {
        using var image = texture.Decode();
        image.Save(path, ImageFormat.Png);
      }
    }

    private static string SafeFileName(string name)
    {
      foreach (char c in Path.GetInvalidFileNameChars())
      {
        name = name.Replace(c, '_');
      }
      return name;
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _context.File.Name;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    #endregion

    public void Dispose()
    {
      _renderVersion++;
      _dictionary.Dispose();
    }
  }

  /// <summary>What shows through where a texture is see-through; the order of the toolbar box.</summary>
  internal enum PreviewBackground
  {
    Gradient,
    Checkerboard,
    Black,
    White,
  }

  /// <summary>Which colours of a texture to show: all, or one channel as grey.</summary>
  internal enum PixelChannel
  {
    All,
    Red,
    Green,
    Blue,
    Alpha,
  }

  /// <summary>Decoded pixels: 32-bit BGRA, top row first.</summary>
  internal sealed class TexturePixels
  {
    private const int CheckerSize = 8;

    private TexturePixels(byte[] bgra, int width, int height)
    {
      Bgra = bgra;
      Width = width;
      Height = height;
    }

    /// <summary>One see-through pixel, for a texture that can't be decoded.</summary>
    public static TexturePixels Blank { get; } = new TexturePixels(new byte[4], 1, 1);

    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }

    public static TexturePixels Decode(Texture texture, int level)
    {
      using Bitmap bitmap = (Bitmap)texture.Decode(level);
      var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
      var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      try
      {
        var bgra = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
        {
          System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * bitmap.Width * 4, bitmap.Width * 4);
        }
        return new TexturePixels(bgra, bitmap.Width, bitmap.Height);
      }
      finally
      {
        bitmap.UnlockBits(data);
      }
    }

    /// <summary>
    /// Pixels for the screen. All channels either over a light checkerboard (drawn in, opaque) or with
    /// their transparency kept, so the background of the view shows through; one channel as opaque grey.
    /// </summary>
    public TexturePixels ForDisplay(PixelChannel channel, bool checkerboard)
    {
      var shown = new byte[Bgra.Length];
      for (int y = 0; y < Height; y++)
      {
        for (int x = 0; x < Width; x++)
        {
          int i = (y * Width + x) * 4;
          byte b = Bgra[i], g = Bgra[i + 1], r = Bgra[i + 2], a = Bgra[i + 3];
          if (channel == PixelChannel.All && checkerboard)
          {
            int checker = ((x / CheckerSize) + (y / CheckerSize)) % 2 == 0 ? 255 : 235;
            shown[i] = (byte)((b * a + checker * (255 - a)) / 255);
            shown[i + 1] = (byte)((g * a + checker * (255 - a)) / 255);
            shown[i + 2] = (byte)((r * a + checker * (255 - a)) / 255);
          }
          else if (channel == PixelChannel.All)
          {
            // The screen expects premultiplied alpha.
            shown[i] = (byte)(b * a / 255);
            shown[i + 1] = (byte)(g * a / 255);
            shown[i + 2] = (byte)(r * a / 255);
            shown[i + 3] = a;
            continue;
          }
          else
          {
            // One channel exactly as stored, alpha ignored; under fully transparent pixels that includes
            // what DXT compression left there.
            byte v = channel switch
            {
              PixelChannel.Red => r,
              PixelChannel.Green => g,
              PixelChannel.Blue => b,
              _ => a,
            };
            shown[i] = shown[i + 1] = shown[i + 2] = v;
          }
          shown[i + 3] = 255;
        }
      }
      return new TexturePixels(shown, Width, Height);
    }

    /// <summary>A small opaque version, at most <paramref name="size"/> pixels wide or high.</summary>
    public static TexturePixels Thumbnail(Texture texture, int size)
    {
      // The smallest level that is still at least as big as the thumbnail (DXT levels below 4 px don't decode).
      int level = 0;
      while (level + 1 < texture.Levels &&
             Math.Max(texture.GetWidth(level + 1), texture.GetHeight(level + 1)) >= size &&
             Math.Min(texture.GetWidth(level + 1), texture.GetHeight(level + 1)) >= 4)
      {
        level++;
      }
      return Decode(texture, level).ForDisplay(PixelChannel.All, checkerboard: true);
    }

    public WriteableBitmap ToBitmap()
    {
      var bitmap = new WriteableBitmap(Width, Height);
      using (var stream = bitmap.PixelBuffer.AsStream())
      {
        stream.Write(Bgra, 0, Bgra.Length);
      }
      bitmap.Invalidate();
      return bitmap;
    }
  }

  /// <summary>A texture dictionary as loaded on the background thread, with its thumbnails already decoded.</summary>
  public sealed class TextureDictionary : IDisposable
  {
    // One dictionary; or for the textures stored in a model dictionary (.wdd), one per entry that has any.
    private readonly List<TextureFile> _files;

    internal TextureDictionary(TextureFile file) : this(new[] { file })
    {
    }

    internal TextureDictionary(IEnumerable<TextureFile> files)
    {
      _files = files.ToList();
      Textures = _files.SelectMany(f => f.Textures).ToList();
      Thumbnails = Textures.Select(SafeThumbnail).ToList();
    }

    public IReadOnlyList<Texture> Textures { get; }
    internal IReadOnlyList<TexturePixels> Thumbnails { get; }

    /// <summary>
    /// True for the textures of a model or web page: then they are written back into that file's resource
    /// instead of making a texture dictionary.
    /// </summary>
    public bool Embedded { get; init; }

    /// <summary>
    /// Replaces a texture with a picture: scaled to the texture's size and encoded in its format, every
    /// mipmap level made again from it. The texture's size and format stay.
    /// </summary>
    public void Replace(Texture texture, string picturePath)
    {
      using var picture = System.Drawing.Image.FromFile(picturePath);
      lock (this)
      {
        texture.Encode(picture);
      }
    }

    /// <summary>The file with the changed textures: <paramref name="original"/> is the file as it was.</summary>
    public byte[] Save(byte[] original)
    {
      lock (this)
      {
        using var result = new MemoryStream();
        if (!Embedded)
        {
          _files[0].Save(result);
          return result.ToArray();
        }

        // The textures are part of the model's resource: written into its two parts, which are then
        // compressed again (as the old SparkIV's embedded texture editor did). Each dictionary writes
        // at its own place in them.
        var resource = new RageLib.Common.Resources.ResourceFile();
        resource.Read(new MemoryStream(original));
        using (var system = new MemoryStream(resource.SystemMemData))
        using (var graphics = new MemoryStream(resource.GraphicsMemData))
        {
          foreach (var file in _files)
          {
            file.Save(system, graphics);
          }
        }
        resource.Write(result);
        return result.ToArray();
      }
    }

    // A texture that doesn't decode still gets a row, with a blank thumbnail.
    private static TexturePixels SafeThumbnail(Texture texture)
    {
      try
      {
        return TexturePixels.Thumbnail(texture, 48);
      }
      catch (Exception)
      {
        return TexturePixels.Blank;
      }
    }

    public void Dispose()
    {
      foreach (var file in _files)
      {
        file.Dispose();
      }
    }
  }

  /// <summary>Texture dictionaries (.wtd).</summary>
  internal sealed class TextureViewer : IViewer
  {
    public object Load(RageLib.FileSystem.Common.File file, byte[] data)
    {
      var textures = new TextureFile();
      textures.Open(new MemoryStream(data));
      return new TextureDictionary(textures);
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new TextureView(context, (TextureDictionary)loaded);
  }
}
