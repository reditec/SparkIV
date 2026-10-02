/**********************************************************************\

 RageLib
 Copyright (C) 2008  Arushan/Aru <oneforaru at gmail.com>

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

using System.Diagnostics;

namespace RageLib.FileSystem.Common
{
  public abstract class FileSystem
  {
    public Directory RootDirectory { get; protected set; }

    public abstract void Open(string filename);

    public abstract void Save();

    public abstract void Rebuild();

    public abstract void Close();

    public abstract bool SupportsRebuild { get; }
    public abstract bool HasDirectoryStructure { get; }

    // Saving an archive without changing it in place: the whole archive with its changes is written into
    // a new file (WriteTo); the archive lets go of its file (Release) so the new one can take its place;
    // then it reads from the new one (Commit), or, if that failed, from the old one again (Revert) with
    // its changes kept. The archive is only ever opened for reading.

    /// <summary>Whether the archive can be written with <see cref="WriteTo"/>.</summary>
    public virtual bool SupportsWriteTo
    {
      get { return false; }
    }

    /// <summary>Writes the archive with all its changes into a new file. This one stays as it is.</summary>
    public virtual void WriteTo(string path)
    {
      throw new System.NotSupportedException();
    }

    /// <summary>Closes the archive's file, so that the file written by <see cref="WriteTo"/> can take its place.</summary>
    public virtual void Release()
    {
    }

    /// <summary>The file written by <see cref="WriteTo"/> is in place at <paramref name="path"/>: read from it from now on.</summary>
    public virtual void Commit(string path)
    {
      throw new System.NotSupportedException();
    }

    /// <summary>The written file could not take its place: read from the old file again, changes still to save.</summary>
    public virtual void Revert()
    {
    }

    /// <summary>Saves through <see cref="WriteTo"/>: a new file next to the archive, which then replaces it.</summary>
    protected void SaveByReplacing(string filename)
    {
      string temp = filename + ".sparkiv-new";
      WriteTo(temp);
      Release();
      try
      {
        System.IO.File.Move(temp, filename, true);
      }
      catch
      {
        Revert();
        System.IO.File.Delete(temp);
        throw;
      }
      Commit(filename);
    }

    internal void DumpFSToDebug()
    {
      DumpDirToDebug("", RootDirectory);
    }

    private static void DumpDirToDebug(string indent, Directory dir)
    {
      Debug.WriteLine(indent + dir.Name);
      indent += "  ";
      foreach (FSObject item in dir)
      {
        if (item.IsDirectory)
        {
          DumpDirToDebug(indent, item as Directory);
        }
        else
        {
          var file = item as File;
          Debug.WriteLine(indent + item.Name + "   (Size: " + file.Size + ", Compressed: " + file.IsCompressed +
                          ")");
        }
      }
    }
  }
}