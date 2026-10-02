using System.Collections.Generic;
using RageLib.FileSystem.Common;

namespace SparkIV.WinUI.Model
{
  /// <summary>
  /// An opened file system: the game folder, or an archive opened from inside another one.
  /// </summary>
  public sealed class Volume
  {
    public Volume(FileSystem fileSystem, string name, Location? host, File? hostFile, string? archivePath = null)
    {
      FileSystem = fileSystem;
      Name = name;
      Host = host;
      HostFile = hostFile;
      ArchivePath = archivePath;
    }

    public FileSystem FileSystem { get; }

    /// <summary>
    /// The archive file on disk that Save writes, for the backup before. Null for a folder (its files are
    /// written one by one) and for an archive inside an archive (opened from a temporary copy).
    /// </summary>
    public string? ArchivePath { get; }
    public string Name { get; }

    /// <summary>The folder the archive file lies in, or null for the outermost volume.</summary>
    public Location? Host { get; }

    /// <summary>The archive file in the host folder.</summary>
    public File? HostFile { get; }

    public Location Root => new Location(this, FileSystem.RootDirectory);
  }

  /// <summary>
  /// A folder in a volume.
  /// </summary>
  public sealed class Location
  {
    public Location(Volume volume, Directory directory)
    {
      Volume = volume;
      Directory = directory;
    }

    public Volume Volume { get; }
    public Directory Directory { get; }

    public bool IsVolumeRoot => Directory.ParentDirectory == null;

    public string Name => IsVolumeRoot ? Volume.Name : Directory.Name;

    /// <summary>One level up: the parent folder, or the folder the archive lies in.</summary>
    public Location? Parent =>
      Directory.ParentDirectory != null ? new Location(Volume, Directory.ParentDirectory) : Volume.Host;

    /// <summary>The way from the outermost volume to here, for the address bar.</summary>
    public List<Location> Path()
    {
      var path = new List<Location>();
      for (Location? l = this; l != null; l = l.Parent)
      {
        path.Insert(0, l);
      }
      return path;
    }

    public override bool Equals(object? obj) =>
      obj is Location other && ReferenceEquals(other.Volume, Volume) && ReferenceEquals(other.Directory, Directory);

    public override int GetHashCode() => Directory.GetHashCode();

    public override string ToString() => Name;
  }
}
