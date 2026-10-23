using System;
using System.Collections.Generic;
using System.IO;
using RageLib.FileSystem;
using SparkIV.WinUI.Model;
using FileSystem = RageLib.FileSystem.Common.FileSystem;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// The open file systems. Opening an archive twice returns the volume opened the first time.
  /// </summary>
  internal sealed class Volumes : IDisposable
  {
    private readonly List<Volume> _open = new List<Volume>();
    private readonly List<string> _tempFiles = new List<string>();

    public Volume OpenFolder(string path)
    {
      var fs = new RealFileSystem();
      fs.Open(path);
      return Add(new Volume(fs, new DirectoryInfo(path).Name, null, null));
    }

    public Volume OpenArchive(string path)
    {
      var fs = CreateArchive(path);
      fs.Open(path);
      return Add(new Volume(fs, System.IO.Path.GetFileName(path), null, null, path));
    }

    /// <summary>Opens an .rpf or .img file that lies in an open volume.</summary>
    public Volume OpenNested(Location host, RageLib.FileSystem.Common.File file)
    {
      foreach (var v in _open)
      {
        if (ReferenceEquals(v.HostFile, file))
        {
          return v;
        }
      }

      string path;
      string? archivePath = null;
      if (host.Volume.FileSystem is RealFileSystem real)
      {
        // FullName starts with the name of the root folder, so it is relative to the folder above it.
        string parent = new DirectoryInfo(real.RealDirectory).Parent?.FullName ?? real.RealDirectory;
        path = System.IO.Path.Combine(parent, file.FullName);
        archivePath = path;
      }
      else
      {
        // An archive inside an archive: the readers need a file.
        path = NewTempFile(System.IO.Path.GetExtension(file.Name));
        System.IO.File.WriteAllBytes(path, file.GetData());
      }

      var fs = CreateArchive(file.Name);
      fs.Open(path);
      return Add(new Volume(fs, file.Name, host, file, archivePath));
    }

    /// <summary>A path for a temporary file, removed when the volumes are closed.</summary>
    public string NewTempFile(string extension)
    {
      string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SparkIV-" + Guid.NewGuid().ToString("N") + extension);
      lock (_tempFiles)
      {
        _tempFiles.Add(path);
      }
      return path;
    }

    public static bool IsArchiveName(string name)
    {
      string ext = System.IO.Path.GetExtension(name);
      return ext.Equals(".rpf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    private static FileSystem CreateArchive(string name)
    {
      return System.IO.Path.GetExtension(name).Equals(".img", StringComparison.OrdinalIgnoreCase)
               ? new IMGFileSystem()
               : new RPFFileSystem();
    }

    private Volume Add(Volume volume)
    {
      _open.Add(volume);
      return volume;
    }

    public void CloseAll()
    {
      foreach (var v in _open)
      {
        try
        {
          v.FileSystem.Close();
        }
        catch (Exception)
        {
          // Closing is best effort; the process is going away or a new game is opened.
        }
      }
      _open.Clear();

      foreach (var f in _tempFiles)
      {
        try
        {
          System.IO.File.Delete(f);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
      }
      _tempFiles.Clear();
    }

    public void Dispose() => CloseAll();
  }
}
