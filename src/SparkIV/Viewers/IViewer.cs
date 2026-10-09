using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// Shows one kind of file in a viewer window.
  /// </summary>
  public interface IViewer
  {
    /// <summary>
    /// Reads and decodes the file (called on a background thread) and returns what the view needs.
    /// </summary>
    object Load(File file, byte[] data);

    /// <summary>Builds the view on the UI thread from what <see cref="Load"/> returned.</summary>
    FrameworkElement CreateView(ViewerContext context, object loaded);
  }

  /// <summary>
  /// What a view may use from its window.
  /// </summary>
  public sealed class ViewerContext
  {
    public ViewerContext(Window window, File file, byte[] data, Action<string, string, bool> showMessage, Action<string, string> setTitle,
                         Func<File, Func<byte[], Task<string>>?>? replaceOf = null, Func<Task<bool>>? ensureEditMode = null)
    {
      Window = window;
      File = file;
      Data = data;
      ShowMessage = showMessage;
      SetTitle = setTitle;
      ReplaceOf = replaceOf;
      ReplaceFile = replaceOf?.Invoke(file);
      EnsureEditModeAsync = ensureEditMode ?? (() => Task.FromResult(Services.EditMode.IsOn));
    }

    /// <summary>
    /// Before changing the file: true if edit mode is on, else it is asked to be turned on (with its warning).
    /// </summary>
    public Func<Task<bool>> EnsureEditModeAsync { get; }

    /// <summary>
    /// Puts new bytes in place of the file: in its archive (written with Save in the main window), or for a
    /// file opened on its own straight into it. Returns what happened, for the message. Null when the file
    /// can't be changed from here.
    /// </summary>
    public Func<byte[], Task<string>>? ReplaceFile { get; }

    /// <summary>
    /// How to replace other files next to this one (the page a web page links to, for its images); null for
    /// a file that can't be changed from here. Handed on to the viewer windows a view opens.
    /// </summary>
    public Func<File, Func<byte[], Task<string>>?>? ReplaceOf { get; }

    public Window Window { get; }
    public File File { get; }

    /// <summary>The original bytes of the file, for "Export original".</summary>
    public byte[] Data { get; }

    /// <summary>title, message, isError</summary>
    public Action<string, string, bool> ShowMessage { get; }

    /// <summary>Replaces the window's title (file name) and the detail next to it (where the file is).</summary>
    public Action<string, string> SetTitle { get; }
  }

  public static class ViewerRegistry
  {
    private static readonly Dictionary<string, IViewer> ByExtension = new Dictionary<string, IViewer>(StringComparer.OrdinalIgnoreCase);

    static ViewerRegistry()
    {
      // The text formats of the old SparkIV.Config.xml, plus .tune (fragment tuning, common\data\fragments).
      // Not every game file with a familiar extension is text: .cfg (controls) and .nod (path nodes) are binary.
      var text = new TextViewer();
      foreach (var ext in new[] { ".dat", ".txt", ".ide", ".ipl", ".ped", ".cmb", ".csv", ".ini", ".list", ".dcl", ".sps", ".tune" })
      {
        ByExtension[ext] = text;
      }
      ByExtension[".xml"] = new XmlViewer();
      ByExtension[".sco"] = new ScriptViewer();
      ByExtension[".ivaud"] = new AudioViewer();
      ByExtension[".wtd"] = new TextureViewer();
      ByExtension[".whm"] = new WebPageViewer();
      ByExtension[".wad"] = new AnimationViewer();
      ByExtension[".cut"] = new CutsceneViewer();
      var collision = new CollisionViewer();
      ByExtension[".wbn"] = collision;
      ByExtension[".wbd"] = collision;
      var models = new ModelViewer();
      foreach (var ext in new[] { ".wdr", ".wdd", ".wft" })
      {
        ByExtension[ext] = models;
      }
    }

    /// <summary>The hex view, for any file.</summary>
    public static IViewer Binary { get; } = new HexViewer();

    /// <summary>The viewer for the file's type, or null if there is none (yet).</summary>
    public static IViewer? Find(string fileName)
    {
      return ByExtension.TryGetValue(Path.GetExtension(fileName), out var viewer) ? viewer : null;
    }
  }
}
