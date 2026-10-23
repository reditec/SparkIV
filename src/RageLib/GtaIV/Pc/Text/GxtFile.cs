/**********************************************************************\

 RageLib - Text

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

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RageLib.GtaIV.Pc.Text
{
  /// <summary>
  /// How the characters of a text table map to the letters of the game's fonts.
  /// </summary>
  public enum GxtCharset
  {
    /// <summary>English, German, French, Italian, Spanish: Latin-1, with Windows-1252 at 0x80 to 0x9F.</summary>
    Western,

    /// <summary>Russian: Cyrillic letters in place of 0x8E to 0xCD, a few signs around them.</summary>
    Russian,
  }

  /// <summary>
  /// A GTA IV text table (.gxt, e.g. common\text\american.gxt): the game's texts by the hash of their key.
  /// </summary>
  /// <remarks>
  /// Version (4) and bits per character (16), then "TABL" with the tables (8-byte name, offset). Each table
  /// (all but MAIN start with their name again) has "TKEY" with (offset, key hash) pairs and "TDAT" with
  /// the texts in UTF-16, each ending in a zero character.
  /// </remarks>
  public class GxtFile
  {
    private readonly Dictionary<uint, string> _entries = new Dictionary<uint, string>();

    public int Count
    {
      get { return _entries.Count; }
    }

    public void Open(Stream stream)
    {
      var br = new BinaryReader(stream);
      br.ReadUInt16(); // version
      br.ReadUInt16(); // bits per character

      ExpectMagic(br, "TABL");
      int tableBytes = br.ReadInt32();
      var tables = new List<KeyValuePair<string, int>>();
      for (int i = 0; i < tableBytes / 12; i++)
      {
        string name = Encoding.ASCII.GetString(br.ReadBytes(8)).TrimEnd('\0');
        tables.Add(new KeyValuePair<string, int>(name, br.ReadInt32()));
      }

      foreach (var table in tables)
      {
        br.BaseStream.Position = table.Value;
        if (table.Key != "MAIN")
        {
          br.ReadBytes(8); // the name again
        }

        ExpectMagic(br, "TKEY");
        int keyBytes = br.ReadInt32();
        var keys = new List<KeyValuePair<int, uint>>();
        for (int i = 0; i < keyBytes / 8; i++)
        {
          keys.Add(new KeyValuePair<int, uint>(br.ReadInt32(), br.ReadUInt32()));
        }

        ExpectMagic(br, "TDAT");
        byte[] data = br.ReadBytes(br.ReadInt32());
        foreach (var key in keys)
        {
          int end = key.Key;
          while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
          {
            end += 2;
          }
          _entries[key.Value] = Encoding.Unicode.GetString(data, key.Key, end - key.Key);
        }
      }
    }

    /// <summary>How the characters map to letters; set it before looking texts up.</summary>
    public GxtCharset Charset { get; set; }

    /// <summary>The text for a key such as "EYE_HOME_3", as Unicode, or null.</summary>
    public string Find(string key)
    {
      string text;
      return _entries.TryGetValue(Hash(key), out text) ? ToUnicode(text, Charset) : null;
    }

    // Windows-1252 at 0x80 to 0x9F; 0 where it has no character.
    private static readonly char[] Windows1252 =
    {
      '\u20AC', '\0', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021', '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\0', '\u017D', '\0',
      '\0', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014', '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\0', '\u017E', '\u0178',
    };

    // The signs of the Russian table below its letters (0x80 to 0x8D) and after them (0xCE to 0xD4),
    // found by comparing texts with the English ones.
    private static readonly Dictionary<char, char> RussianSigns = new Dictionary<char, char>
    {
      { '\u0080', '\u00A0' }, { '\u0081', '\u00A9' }, { '\u0083', '\u2122' }, { '\u0084', '\u00AE' },
      { '\u0086', '\u00E0' }, { '\u0087', '\u00E1' }, { '\u0088', '\u00E7' }, { '\u0089', '\u00E8' },
      { '\u008A', '\u00E9' }, { '\u008B', '\u00F1' }, { '\u008C', '\u00FA' }, { '\u008D', '\u00FC' },
      { '\u00CE', '\u2014' }, { '\u00CF', '\u2019' }, { '\u00D0', '\u00AB' }, { '\u00D1', '\u00BB' },
      { '\u00D2', '\u2026' }, { '\u00D3', '\u2116' }, { '\u00D4', '\u2122' },
    };

    /// <summary>Converts a text as stored in the table to Unicode.</summary>
    public static string ToUnicode(string text, GxtCharset charset)
    {
      var sb = new StringBuilder(text.Length);
      foreach (char c in text)
      {
        if (charset == GxtCharset.Russian)
        {
          char sign;
          if (c >= '\u008E' && c <= '\u00AD')
          {
            sb.Append((char)('\u0410' + (c - '\u008E')));  // А to Я
          }
          else if (c >= '\u00AE' && c <= '\u00CD')
          {
            sb.Append((char)('\u0430' + (c - '\u00AE')));  // а to я
          }
          else if (RussianSigns.TryGetValue(c, out sign))
          {
            sb.Append(sign);
          }
          else if (c >= '\u8000' && c < '\u8080')
          {
            sb.Append((char)(c - '\u8000'));  // ASCII in the codes of button names (~PAD_UP~)
          }
          else
          {
            sb.Append(c);
          }
        }
        else if (c >= '\u0080' && c <= '\u009F' && Windows1252[c - 0x80] != '\0')
        {
          sb.Append(Windows1252[c - 0x80]);
        }
        else if (c == '\u00AC')
        {
          sb.Append('\u2122');  // The fonts have ™ where Latin-1 has ¬ ("the Rockstar Games ¬ logo").
        }
        else
        {
          sb.Append(c);
        }
      }
      return sb.ToString();
    }

    /// <summary>Keys are stored as their Jenkins one-at-a-time hash, ignoring case.</summary>
    public static uint Hash(string key)
    {
      uint hash = 0;
      foreach (char c in key)
      {
        hash += (byte)char.ToLowerInvariant(c);
        hash += hash << 10;
        hash ^= hash >> 6;
      }
      hash += hash << 3;
      hash ^= hash >> 11;
      hash += hash << 15;
      return hash;
    }

    private static void ExpectMagic(BinaryReader br, string magic)
    {
      string found = Encoding.ASCII.GetString(br.ReadBytes(4));
      if (found != magic)
      {
        throw new InvalidDataException("Not a GTA IV text table: expected " + magic + ", found " + found + ".");
      }
    }
  }
}
