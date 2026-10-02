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
using RageLib.Common;

namespace RageLib.FileSystem.IMG
{
  internal class File
  {
    private Stream _stream;
    private string _filename;

    public Header Header { get; private set; }
    public TOC TOC { get; private set; }

    public bool Open(string filename)
    {
      Header = new Header(this);
      TOC = new TOC(this);
      _filename = filename;

      bool encrypted = false;

      // Only read: archives in protected folders (Program Files) open without administrator rights too.
      // Saving writes a new file (WriteTo).
      _stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

      var br = new BinaryReader(_stream);

      byte[] headerData = br.ReadBytes(0x14);

      if (BitConverter.ToUInt32(headerData, 0) != Header.MagicId)
      {
        encrypted = true;
      }

      if (encrypted)
      {
        headerData = DataUtil.Decrypt(headerData);
      }

      var headerMS = new MemoryStream(headerData);

      Header.Read(new BinaryReader(headerMS));

      headerMS.Close();

      if (Header.Identifier != Header.MagicId || Header.Version != Header.SupportedVersion)
      {
        _stream.Close();
        return false;
      }

      byte[] tocData = br.ReadBytes(Header.TocSize);

      if (encrypted)
      {
        tocData = DataUtil.Decrypt(tocData);
      }

      var tocMS = new MemoryStream(tocData);

      TOC.Read(new BinaryReader(tocMS));

      tocMS.Close();

      return true;
    }

    public void Close()
    {
      if (_stream != null)
      {
        _stream.Close();
      }
    }

    public string Filename
    {
      get { return _filename; }
    }

    // Where each entry is in the file written by WriteTo, until Commit.
    private (TOCEntry Entry, int OffsetBlock, short UsedBlocks)[] _written;

    /// <summary>
    /// Writes the archive with all changes into a new file, packed: the header, the table of contents,
    /// then the entries one after the other. Unencrypted (the game reads that too). This file and its
    /// entries stay as they are until <see cref="Commit"/>.
    /// </summary>
    public void WriteTo(string path)
    {
      var written = new List<(TOCEntry, int, short)>();
      using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
      {
        var bw = new BinaryWriter(output);

        // The data starts at the first block after header and table (the old Rebuild left out the header).
        const int headerSize = 0x14;
        int dataBlock = (headerSize + Header.TocSize + TOCEntry.BlockSize - 1) / TOCEntry.BlockSize;
        output.Position = (long)dataBlock * TOCEntry.BlockSize;

        foreach (var entry in TOC)
        {
          byte[] data;
          short blocks;
          if (entry.CustomData != null)
          {
            // Already padded to whole blocks.
            data = entry.CustomData;
            blocks = (short)((data.Length + TOCEntry.BlockSize - 1) / TOCEntry.BlockSize);
          }
          else
          {
            blocks = entry.UsedBlocks;
            data = ReadData(entry.OffsetBlock * TOCEntry.BlockSize, blocks * TOCEntry.BlockSize);
          }
          bw.Write(data);
          int padding = blocks * TOCEntry.BlockSize - data.Length;
          if (padding > 0)
          {
            bw.Write(new byte[padding]);
          }
          written.Add((entry, dataBlock, blocks));
          dataBlock += blocks;
        }

        // The table with the new places, then the entries as they were again until Commit.
        var old = new List<(int, short)>();
        foreach (var (entry, offsetBlock, usedBlocks) in written)
        {
          old.Add((entry.OffsetBlock, entry.UsedBlocks));
          entry.OffsetBlock = offsetBlock;
          entry.UsedBlocks = usedBlocks;
        }
        output.Position = 0;
        Header.Write(bw);
        bw.Write(TableBytes());
        for (int i = 0; i < written.Count; i++)
        {
          written[i].Item1.OffsetBlock = old[i].Item1;
          written[i].Item1.UsedBlocks = old[i].Item2;
        }
      }
      _written = written.ToArray();
    }

    /// <summary>The table of contents, exactly as long as the header says.</summary>
    private byte[] TableBytes()
    {
      var ms = new MemoryStream();
      TOC.Write(new BinaryWriter(ms));
      var table = new byte[Header.TocSize];
      Array.Copy(ms.ToArray(), table, Math.Min(table.Length, (int)ms.Length));
      return table;
    }

    /// <summary>The file written by <see cref="WriteTo"/> took the place of this one at <paramref name="path"/>.</summary>
    public void Commit(string path)
    {
      Close();
      foreach (var (entry, offsetBlock, usedBlocks) in _written)
      {
        entry.OffsetBlock = offsetBlock;
        entry.UsedBlocks = usedBlocks;
        entry.SetCustomData(null);
      }
      _written = null;
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