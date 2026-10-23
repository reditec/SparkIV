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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// Where a cutscene's sound is, as the game's audio metadata says: the sounds' file (GTA IV's
  /// pc\audio\config\sounds.dat15; an episode's named in its audio XML, TLAD's e1_audio.xml &lt;sounds&gt;
  /// e1:/pc/audio/config/ep1_sounds.dat, on disk EP1_SOUNDS.DAT15) names each wave bank as its wave pack and
  /// the bank: CUTSCENES\FAU1_B, EP1_SFX/ET01_BA, EP2_SFX/... The wave pack is the .rpf of that name in the
  /// game's pc\audio\sfx (cutscenes.rpf, TLAD's EP1_SFX.rpf), the bank NAME.ivaud in its folder of the pack's
  /// name. The episode's metadata first, then GTA IV's (the game reads both).
  /// </summary>
  public static class CutsceneSound
  {
    /// <summary>The wave pack (.rpf) holding the bank of that name, and the pack's name; null where none says.</summary>
    public static (string Rpf, string Pack)? Find(string folder, string bank)
    {
      foreach (string game in Games(folder))
      {
        foreach (string sounds in SoundsFiles(game))
        {
          string text = Encoding.ASCII.GetString(File.ReadAllBytes(sounds));
          var match = Regex.Match(text, @"([A-Za-z0-9_]+)[\\/]" + Regex.Escape(bank) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
          if (!match.Success)
          {
            continue;
          }
          string pack = match.Groups[1].Value;
          // The pack in the game whose metadata names it, else GTA IV's.
          foreach (string g in Games(game))
          {
            string sfx = Path.Combine(g, "pc", "audio", "sfx");
            if (Directory.Exists(sfx) &&
                Directory.EnumerateFiles(sfx, "*.rpf").FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(pack, StringComparison.OrdinalIgnoreCase)) is string rpf)
            {
              return (rpf, pack);
            }
          }
        }
      }
      return null;
    }

    // The game folder, then GTA IV's above it for an episode (a content.dat in its folder).
    private static IEnumerable<string> Games(string folder)
    {
      yield return folder;
      var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(folder));
      if (File.Exists(Path.Combine(folder, "content.dat")) && parent != null && Directory.Exists(Path.Combine(parent.FullName, "pc", "audio")))
      {
        yield return parent.FullName;
      }
    }

    // A game's sounds metadata: an episode's from its e?_audio.xml (<sounds>, "eN:/" its folder; the file on
    // disk has the format's number after .dat), GTA IV's pc\audio\config\sounds.dat*.
    private static IEnumerable<string> SoundsFiles(string game)
    {
      var named = new List<string>();
      foreach (string xml in Directory.EnumerateFiles(game, "e*_audio.xml"))
      {
        foreach (Match m in Regex.Matches(File.ReadAllText(xml), @"<sounds[^>]*>\s*e\d+:/([^<\s]+)\s*</sounds>", RegexOptions.IgnoreCase))
        {
          named.Add(m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
        }
      }
      if (named.Count == 0)
      {
        named.Add(Path.Combine("pc", "audio", "config", "sounds.dat"));
      }
      foreach (string relative in named)
      {
        string dir = Path.Combine(game, Path.GetDirectoryName(relative) ?? "");
        string name = Path.GetFileName(relative);
        if (!Directory.Exists(dir))
        {
          continue;
        }
        foreach (string file in Directory.EnumerateFiles(dir).Where(p => Path.GetFileName(p).StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                                                                          Path.GetFileName(p).Substring(name.Length).All(char.IsDigit)))
        {
          yield return file;
        }
      }
    }
  }
}
