using RageLib.FileSystem.Common;

namespace SparkIV.WinUI.Model
{
  /// <summary>
  /// A node in the folder tree: a folder, or an archive that becomes a folder once opened.
  /// </summary>
  public sealed class TreeEntry
  {
    private TreeEntry(string name, string glyph)
    {
      Name = name;
      Glyph = glyph;
    }

    public static TreeEntry ForFolder(Location location) =>
      new TreeEntry(location.Name, "\uE8B7") { Folder = location };

    public static TreeEntry ForArchive(Location host, File file) =>
      new TreeEntry(file.Name, "\uF012") { ArchiveHost = host, ArchiveFile = file };

    public string Name { get; }
    public string Glyph { get; }

    /// <summary>The folder, or for an archive its root once it has been opened.</summary>
    public Location? Folder { get; set; }

    public Location? ArchiveHost { get; private set; }
    public File? ArchiveFile { get; private set; }

    public bool IsArchive => ArchiveFile != null;

    /// <summary>The archive is being opened; a second expand must not open it again.</summary>
    public bool IsRealizing { get; set; }
  }
}
