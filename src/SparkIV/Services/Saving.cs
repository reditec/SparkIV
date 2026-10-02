using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RageLib.FileSystem;
using SparkIV.WinUI.Model;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Saving what was changed in a volume. An archive is written anew into a temporary file, which then
  /// takes the place of the old one (with administrator rights if the folder needs them). An archive in
  /// an archive goes into the archive around it, and that one is saved too, up to the file on disk.
  /// Before a file on disk is replaced the first time, its original goes into the backups.
  /// </summary>
  internal static class Saving
  {
    /// <summary>The volumes saving <paramref name="volume"/> writes: it and the archives around it, inside out.</summary>
    public static List<Volume> Chain(Volume volume)
    {
      var chain = new List<Volume> { volume };
      for (var v = volume; v.ArchivePath == null && v.FileSystem is not RealFileSystem && v.Host != null; v = v.Host.Volume)
      {
        chain.Add(v.Host.Volume);
      }
      return chain;
    }

    /// <summary>The files on disk saving <paramref name="volume"/> replaces.</summary>
    public static List<string> Targets(Volume volume)
    {
      var outer = Chain(volume).Last();
      if (outer.FileSystem is RealFileSystem real)
      {
        return real.ModifiedFiles.ToList();
      }
      return outer.ArchivePath != null ? new List<string> { outer.ArchivePath } : new List<string>();
    }

    /// <summary>
    /// Saves a volume (on a background thread; may wait for Windows' question about administrator rights).
    /// <paramref name="saved"/> gets each volume whose changes are written, also when a later step fails:
    /// then the changes are in the archive around it, still to save.
    /// </summary>
    public static void Save(Volume volume, Volumes volumes, List<Volume> saved)
    {
      var v = volume;

      // Inside an archive: written anew, and that put into the archive around it.
      while (v.ArchivePath == null && v.FileSystem is not RealFileSystem)
      {
        if (v.Host == null || v.HostFile == null)
        {
          throw new InvalidOperationException(v.Name + " has no file to be saved into.");
        }
        string temp = volumes.NewTempFile(Path.GetExtension(v.Name));
        v.FileSystem.WriteTo(temp);
        byte[] bytes = System.IO.File.ReadAllBytes(temp);
        v.FileSystem.Release();
        v.FileSystem.Commit(temp);
        v.HostFile.SetData(bytes);
        saved.Add(v);
        v = v.Host.Volume;
      }

      if (v.FileSystem is RealFileSystem real)
      {
        // A folder: each changed file on its own.
        var paths = real.ModifiedFiles.ToList();
        var files = new List<(string, string)>();
        foreach (var path in paths)
        {
          string temp = volumes.NewTempFile(Path.GetExtension(path));
          System.IO.File.WriteAllBytes(temp, real.GetChange(path));
          files.Add((temp, path));
        }
        foreach (var path in paths)
        {
          Backups.Keep(path);
        }
        FileInstaller.Install(files);
        foreach (var path in paths)
        {
          real.MarkSaved(path);
        }
      }
      else
      {
        // An archive on disk: the new one takes its place.
        string target = v.ArchivePath!;
        string temp = volumes.NewTempFile(Path.GetExtension(target));
        v.FileSystem.WriteTo(temp);
        Backups.Keep(target);
        v.FileSystem.Release();
        try
        {
          FileInstaller.Install(new[] { (temp, target) });
        }
        catch
        {
          v.FileSystem.Revert();
          throw;
        }
        v.FileSystem.Commit(target);
      }
      saved.Add(v);
    }

    /// <summary>
    /// Writes one file on disk (a file opened on its own), after keeping its original. Returns where the
    /// original is kept, or null.
    /// </summary>
    public static string? WriteFile(string path, byte[] data)
    {
      string? backup = Backups.Keep(path);
      string temp = Path.Combine(Path.GetTempPath(), "SparkIV-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
      System.IO.File.WriteAllBytes(temp, data);
      FileInstaller.Install(new[] { (temp, path) });
      return backup;
    }
  }
}
