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
