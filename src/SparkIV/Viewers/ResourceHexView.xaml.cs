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
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RageLib.Common.Resources;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A RAGE resource (RSC5, the format of .wtd, .wdr, .wft ...) as bytes. It opens as stored (header and
  /// compressed data); the tabs System and Graphics show its two parts, decompressed when first chosen.
  /// </summary>
  public sealed partial class ResourceHexView : UserControl
  {
    private readonly ViewerContext _context;
    private readonly BinaryContent _content;
    private readonly Dictionary<string, FrameworkElement> _views = new Dictionary<string, FrameworkElement>();
    private Task<ResourceParts>? _parts;

    public ResourceHexView(ViewerContext context, BinaryContent content)
    {
      InitializeComponent();
      _context = context;
      _content = content;

      var header = content.Header!;
      FileItem.Text = "File  \u00B7  " + Size(content.Raw.Length);
      SystemItem.Text = "System  \u00B7  " + Size(header.SystemSize);
      GraphicsItem.Text = "Graphics  \u00B7  " + Size(header.GraphicsSize);
      Summary.Text = "Resource " + header.TypeName + "   \u00B7   " + header.Compression + " compressed";

      Parts.SelectedItem = FileItem;
    }

    private static string Size(int bytes) => bytes < 1024 ? bytes + " bytes" : ((bytes + 1023) / 1024).ToString("N0") + " KB";

    // Each tab gets its own hex view the first time it is shown; it keeps its place when switching back.
    private async void Parts_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
      string part = (string)(sender.SelectedItem?.Tag ?? "file");
      if (_views.TryGetValue(part, out var shown))
      {
        PartHost.Content = shown;
        return;
      }

      if (part == "file")
      {
        PartHost.Content = _views[part] = new HexView(_context, _content.Raw);
        return;
      }

      PartHost.Content = new ProgressRing { IsActive = true, Width = 32, Height = 32 };
      try
      {
        _parts ??= Task.Run(() => ResourceParts.Decompress(_content.Raw));
        var parts = await _parts;
        var view = new HexView(_context, part == "graphics" ? parts.Graphics : parts.System, part);
        _views[part] = view;
        if (Equals(Parts.SelectedItem?.Tag, part))
        {
          PartHost.Content = view;
        }
      }
      catch (Exception ex)
      {
        _parts = null;
        PartHost.Content = null;
        _context.ShowMessage("Could not decompress the resource", ex.Message, true);
      }
    }
  }

  /// <summary>The bytes of a file, and for a resource what its header says.</summary>
  public sealed class BinaryContent
  {
    private BinaryContent(byte[] raw, ResourceHeaderInfo? header)
    {
      Raw = raw;
      Header = header;
    }

    public byte[] Raw { get; }
    public ResourceHeaderInfo? Header { get; }

    public static BinaryContent Read(byte[] data) => new BinaryContent(data, ResourceHeaderInfo.Read(data));

    /// <summary>The view for these bytes: tabs for a resource, else one hex view.</summary>
    public FrameworkElement CreateView(ViewerContext context) =>
      Header != null ? new ResourceHexView(context, this) : new HexView(context, Raw);
  }

  /// <summary>The 14-byte header of a resource: type, the sizes of its two parts and the compression.</summary>
  public sealed class ResourceHeaderInfo
  {
    private ResourceHeaderInfo(string typeName, int systemSize, int graphicsSize, string compression)
    {
      TypeName = typeName;
      SystemSize = systemSize;
      GraphicsSize = graphicsSize;
      Compression = compression;
    }

    public string TypeName { get; }
    public int SystemSize { get; }
    public int GraphicsSize { get; }
    public string Compression { get; }

    /// <summary>The header if the data starts with "RSC" and version 5 (in either byte order), else null.</summary>
    public static ResourceHeaderInfo? Read(byte[] data)
    {
      if (data.Length < 14)
      {
        return null;
      }
      bool little = data[0] == 'R' && data[1] == 'S' && data[2] == 'C' && data[3] == 5;
      bool big = data[0] == 5 && data[1] == 'C' && data[2] == 'S' && data[3] == 'R';
      if (!little && !big)
      {
        return null;
      }

      var span = data.AsSpan();
      uint type = little ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4)) : BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4));
      uint flags = little ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)) : BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8));
      ushort codec = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12));

      // As in RageLib's ResourceHeader: 11 bits of size and 4 bits of shift for each part.
      int systemSize = (int)(flags & 0x7FF) << (int)(((flags >> 11) & 0xF) + 8);
      int graphicsSize = (int)((flags >> 15) & 0x7FF) << (int)(((flags >> 26) & 0xF) + 8);

      string typeName = Enum.IsDefined(typeof(ResourceType), (int)type) ? ((ResourceType)type).ToString() : "0x" + type.ToString("x");
      string compression = Enum.IsDefined(typeof(CompressionType), (int)codec) ? ((CompressionType)codec).ToString() : "0x" + codec.ToString("x");
      return new ResourceHeaderInfo(typeName, systemSize, graphicsSize, compression);
    }
  }

  /// <summary>A resource after decompression.</summary>
  public sealed class ResourceParts
  {
    private ResourceParts(byte[] system, byte[] graphics)
    {
      System = system;
      Graphics = graphics;
    }

    public byte[] System { get; }
    public byte[] Graphics { get; }

    public static ResourceParts Decompress(byte[] data)
    {
      var resource = new ResourceFile();
      resource.Read(new MemoryStream(data));
      return new ResourceParts(resource.SystemMemData, resource.GraphicsMemData);
    }
  }
}
