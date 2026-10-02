using System;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Tells open views that a file got new content (a texture replaced in it), so those that show it or
  /// use it (a model and the texture dictionary it takes its textures from) show the new one. Raised on
  /// the UI thread.
  /// </summary>
  internal static class FileChanges
  {
    public static event Action<File>? Changed;

    public static void Raise(File file) => Changed?.Invoke(file);
  }
}
