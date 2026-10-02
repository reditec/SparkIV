using System;
using System.IO;
using System.Threading.Tasks;
using RageLib.FileSystem;
using SparkIV.WinUI.Viewers;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// A file SparkIV is started with ("Open with" in Explorer): an archive opens in the main window, any
  /// other file straight in the viewer for its type.
  /// </summary>
  internal static class FileLaunch
  {
    /// <summary>The file given on the command line, if there is one.</summary>
    public static string? FileFromCommandLine()
    {
      string[] args = Environment.GetCommandLineArgs();
      for (int i = 1; i < args.Length; i++)
      {
        if (System.IO.File.Exists(args[i]))
        {
          return Path.GetFullPath(args[i]);
        }
      }
      return null;
    }

    public static bool IsArchive(string path) => Volumes.IsArchiveName(path);


    /// <summary>
    /// Opens a file that isn't an archive in its viewer, in a window of its own. <paramref name="install"/>:
    /// the game it is from (where it lies, or as asked), or null.
    /// </summary>
    public static void OpenInViewer(string path, GameInstall? install)
    {
      string folder = Path.GetDirectoryName(path)!;

      // Models find their textures through the game's .ide files, web pages its texts.
      Games.SetCurrent(install);

      // The viewers work on RageLib's files: a folder is opened as a file system. For a file in its game the
      // game's folder, so the viewer finds what is around it as in the main window (a page its texts and
      // the sites of the episodes, a model its textures in pc\textures); else the file's folder, for a web
      // page copied out of the game the folder above its site, so its links reach the sites next to it.
      string root = folder;
      if (install != null && IsBelow(path, install.Folder))
      {
        root = install.Folder;
      }
      else if (Path.GetExtension(path).Equals(".whm", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(folder).Contains('.') && Path.GetDirectoryName(folder) is string above)
      {
        root = above;
      }
      var fs = new RealFileSystem();
      fs.Open(root);
      var directory = fs.RootDirectory;
      foreach (var name in Path.GetRelativePath(root, folder).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
      {
        if (name != "." && directory.FindByName(name) is RageLib.FileSystem.Common.Directory sub)
        {
          directory = sub;
        }
      }
      if (directory.FindByName(Path.GetFileName(path)) is not File file)
      {
        throw new FileNotFoundException("Could not open " + path, path);
      }

      var viewer = ViewerRegistry.Find(file.Name) ?? ViewerRegistry.Binary;

      // There is no archive to save into: a change goes straight into the file, after its original is
      // kept in the backup folder.
      // In a protected folder Windows asks for administrator rights to write it.
      async Task<string> Replace(byte[] data)
      {
        string? backup = await Task.Run(() => Saving.WriteFile(path, data));
        file.SetData(data);
        return "Written into " + path + (backup != null ? " (the original is kept in " + backup + ")." : ".");
      }

      var window = new ViewerWindow(file, viewer, folder, Replace);
      window.Activate();
      window.BringToFront();
    }

    private static bool IsBelow(string path, string folder)
    {
      string relative = Path.GetRelativePath(folder, path);
      return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
  }
}
