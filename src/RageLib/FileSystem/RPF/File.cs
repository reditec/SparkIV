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

using System;
using System.Collections.Generic;
using System.IO;

namespace RageLib.FileSystem.RPF
{
  internal class File
  {
    private Stream _stream;

    public File()
    {
      Header = new Header(this);
      TOC = new TOC(this);
    }

    public Header Header { get; private set; }
    public TOC TOC { get; private set; }

    private string _filename;

    public string Filename
    {
      get { return _filename; }
    }

    public bool Open(string filename)
    {
      _filename = filename;

      // Only read: archives in protected folders (Program Files) open without administrator rights too.
      // Saving writes a new file (WriteTo).
      _stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

      var br = new BinaryReader(_stream);
      Header.Read(br);

      if (!Enum.IsDefined(typeof(MagicId), (int)Header.Identifier))
      {
        _stream.Close();
        return false;
      }

      _stream.Seek(0x800, SeekOrigin.Begin);
      TOC.Read(br);

      return true;
    }

    public void Close()
    {
      if (_stream != null)
      {
        _stream.Close();
      }
    }

    // Where each file entry is in the file written by WriteTo, until Commit.
    private (FileEntry Entry, int Offset, int SizeUsed)[] _written;

    /// <summary>
    /// Writes the archive with all changes into a new file, packed: the header, the table of contents at
    /// 0x800, then the files one after the other. Unencrypted (the game reads that too). This file and its
    /// entries stay as they are until <see cref="Commit"/>.
    /// </summary>
    public void WriteTo(string path)
    {
      const int tocOffset = 0x800;
      var written = new List<(FileEntry, int, int)>();
      using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
      {
        var bw = new BinaryWriter(output);

        int dataOffset = (tocOffset + Header.TOCSize + FileEntry.BlockSize - 1) / FileEntry.BlockSize * FileEntry.BlockSize;
        output.Position = dataOffset;

        foreach (var entry in TOC)
        {
          if (entry is not FileEntry fileEntry)
          {
            continue;
          }
          // Custom data is already padded to whole blocks; the stored data is taken as it is (compressed or not).
          byte[] data = fileEntry.CustomData ?? ReadData(fileEntry.Offset, fileEntry.SizeUsed);
          int sizeUsed = (data.Length + FileEntry.BlockSize - 1) / FileEntry.BlockSize * FileEntry.BlockSize;
          bw.Write(data);
          if (sizeUsed > data.Length)
          {
            bw.Write(new byte[sizeUsed - data.Length]);
          }
          written.Add((fileEntry, dataOffset, sizeUsed));
          dataOffset += sizeUsed;
        }

        // The table with the new places, then the entries as they were again until Commit.
        var old = new List<int>();
        foreach (var (entry, offset, _) in written)
        {
          old.Add(entry.Offset);
          entry.Offset = offset;
        }
        output.Position = 0;
        Header.Write(bw);
        output.Position = tocOffset;
        bw.Write(TableBytes());
        for (int i = 0; i < written.Count; i++)
        {
          written[i].Item1.Offset = old[i];
        }
      }
      _written = written.ToArray();
    }

    /// <summary>The table of contents, exactly as long as the header says.</summary>
    private byte[] TableBytes()
    {
      var ms = new MemoryStream();
      TOC.Write(new BinaryWriter(ms));
      var table = new byte[Header.TOCSize];
      Array.Copy(ms.ToArray(), table, Math.Min(table.Length, (int)ms.Length));
      return table;
    }

    /// <summary>The file written by <see cref="WriteTo"/> took the place of this one at <paramref name="path"/>.</summary>
    public void Commit(string path)
    {
      Close();
      foreach (var (entry, offset, sizeUsed) in _written)
      {
        entry.Offset = offset;
        entry.SizeUsed = sizeUsed;
        entry.SetCustomData(null);
      }
      _written = null;
      // As written: not encrypted any more.
      Header.Encrypted = false;
      _filename = path;
      _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>The written file couldn't take its place: on with this one, changes kept.</summary>
    public void Revert()
    {
      _written = null;
      if (_stream == null || !_stream.CanRead)
      {
        _stream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      }
    }

    public byte[] ReadData(int offset, int length)
    {
      var buffer = new byte[length];
      _stream.Seek(offset, SeekOrigin.Begin);
      _stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
      return buffer;
    }

  }
}