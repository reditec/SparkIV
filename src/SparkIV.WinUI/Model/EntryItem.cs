using System;
using System.Collections.Generic;
using RageLib.FileSystem.Common;

namespace SparkIV.WinUI.Model
{
  /// <summary>
  /// One row in the file list.
  /// </summary>
  public sealed class EntryItem
  {
    public EntryItem(FSObject entry, bool unsaved = false)
    {
      Entry = entry;
      IsUnsaved = unsaved;
      Type = entry.IsDirectory ? FileTypes.Folder : FileTypes.ForFile(entry.Name);

      if (entry is File file)
      {
        SizeText = FriendlySize(file.Size);
        Resource = file.IsResource ? ResourceName(file) : "";
      }
    }

    public FSObject Entry { get; }
    public FileType Type { get; }

    public string Name => Entry.Name;
    public string Glyph => Type.Glyph;
    public string TypeName => Type.Name;
    public string SizeText { get; } = "";
    public string Resource { get; } = "";

    /// <summary>Changed and not saved yet (the old SparkIV showed these in blue).</summary>
    public bool IsUnsaved { get; }

    public Microsoft.UI.Xaml.Visibility UnsavedVisibility => IsUnsaved ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public File? File => Entry as File;
    public Directory? Directory => Entry as Directory;
    public bool IsArchive => !Entry.IsDirectory && Type.IsArchive;
    public long Size => File?.Size ?? -1;

    /// <summary>Like Explorer's details view: whole kilobytes, rounded up.</summary>
    public static string FriendlySize(long size) => (size == 0 ? 0 : (size + 1023) / 1024).ToString("N0") + " KB";

    private static string ResourceName(File file)
    {
      return Enum.IsDefined(file.ResourceType.GetType(), file.ResourceType)
               ? file.ResourceType.ToString()
               : string.Format("Unknown 0x{0:x}", (int)file.ResourceType);
    }
  }

  /// <summary>
  /// A group of rows with a header, like "Textures (40)".
  /// </summary>
  public sealed class EntryGroup : List<EntryItem>
  {
    public EntryGroup(string name, int order, IEnumerable<EntryItem> items) : base(items)
    {
      Name = name;
      Order = order;
    }

    public string Name { get; }
    public int Order { get; }
    public string Header => Name + " (" + Count + ")";
  }
}
