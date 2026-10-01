using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using RageLib.Scripting;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>Plain text and data files (.dat, .ide, .ipl ...).</summary>
  internal sealed class TextViewer : IViewer
  {
    public object Load(File file, byte[] data)
    {
      // A byte order mark tells the encoding (the game's Readme.txt is UTF-16).
      if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
      {
        return Encoding.Unicode.GetString(data, 2, data.Length - 2);
      }
      if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
      {
        return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
      }
      if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
      {
        return Encoding.UTF8.GetString(data, 3, data.Length - 3);
      }

      // Some .dat files are binary (speech.dat, navprecalc.dat ...); as text they'd only be garbage,
      // so they get the hex view.
      if (LooksBinary(data))
      {
        return data;
      }

      // Latin-1 maps every byte to one character, so nothing is lost or replaced.
      return Encoding.Latin1.GetString(data);
    }

    /// <summary>More than 1 % control characters (other than tab, line breaks, form feed) in the first 4 KB.</summary>
    private static bool LooksBinary(byte[] data)
    {
      int length = System.Math.Min(data.Length, 4096);
      int control = 0;
      for (int i = 0; i < length; i++)
      {
        byte b = data[i];
        if (b < 9 || (b > 13 && b < 32))
        {
          control++;
        }
      }
      return length > 0 && control * 100 > length;
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) =>
      loaded is byte[] binary ? new HexView(context, binary) : new TextDocumentView(context, (string)loaded, "data");
  }

  /// <summary>Any file as bytes. For files without a viewer of their own, and "Open as binary".</summary>
  internal sealed class HexViewer : IViewer
  {
    public object Load(File file, byte[] data) => data;

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new HexView(context, (byte[])loaded);
  }

  /// <summary>XML, indented for reading. Falls back to the raw text if it doesn't parse.</summary>
  internal sealed class XmlViewer : IViewer
  {
    public object Load(File file, byte[] data)
    {
      string text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
      try
      {
        return XDocument.Parse(text).ToString();
      }
      catch (XmlException)
      {
        return text;
      }
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new TextDocumentView(context, (string)loaded, "xml");
  }

  /// <summary>Compiled scripts (.sco): decompiled, Scruff format or disassembly.</summary>
  internal sealed class ScriptViewer : IViewer
  {
    private static readonly string[] FormatNames = { "Decompiled", "Scruff (control flow)", "Disassembly" };
    private static readonly CodeFormat[] Formats = { CodeFormat.FullDecompile, CodeFormat.ScruffDecompile, CodeFormat.Disassemble };

    public object Load(File file, byte[] data)
    {
      var script = new ScriptFile();
      script.Open(new MemoryStream(data));
      return script;
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded)
    {
      var script = (ScriptFile)loaded;
      var view = new TextDocumentView(context, "", "script");
      view.SetFormats(FormatNames, 0, index =>
      {
        lock (script)
        {
          return script.GetCode(Formats[index]);
        }
      }, ".c");
      return view;
    }
  }
}
