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

using System.Collections.Generic;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// The names of the parts in a ped's model dictionary, which the file only has as hashes:
  /// part_number_kind, e.g. head_000_r, uppr_001_u.
  /// </summary>
  internal static class PedComponentNames
  {
    private static readonly string[] Parts =
    {
      "head", "uppr", "lowr", "hand", "feet", "suse", "suse2", "hair", "teef", "accs", "task", "decl", "jaw",
      "uppr2", "lowr2", "hat", "glasses",
    };

    private static readonly string[] Kinds = { "u", "r", "m", "w", "b", "a", "l", "h", "s", "c", "e", "i", "g", "x" };

    /// <summary>
    /// The places a ped's props are worn (a pedprops.img dictionary's parts: p_head_000 a hat, p_eyes_000
    /// glasses), in the game's order of anchors (a .cut's [PROPS] names them by number; GTAIV.exe's own table
    /// of them at 0x103EDDC: p_head ... p_hip, p_lfoot, p_rfoot).
    /// </summary>
    public static readonly string[] PropAnchors = { "head", "eyes", "ears", "mouth", "lhand", "rhand", "lwrist", "rwrist", "hip", "lfoot", "rfoot" };

    private static Dictionary<uint, string>? s_names;

    /// <summary>The name of a hash, or null if it isn't one of these.</summary>
    public static string? Find(uint hash)
    {
      if (s_names == null)
      {
        var names = new Dictionary<uint, string>();
        foreach (var part in Parts)
        {
          for (int number = 0; number < 40; number++)
          {
            foreach (var kind in Kinds)
            {
              string name = part + "_" + number.ToString("000") + "_" + kind;
              names[RageLib.GtaIV.Pc.Text.GxtFile.Hash(name)] = name;
            }
          }
        }
        foreach (var anchor in PropAnchors)
        {
          for (int number = 0; number < 40; number++)
          {
            string name = "p_" + anchor + "_" + number.ToString("000");
            names[RageLib.GtaIV.Pc.Text.GxtFile.Hash(name)] = name;
          }
        }
        s_names = names;
      }
      return s_names.TryGetValue(hash, out var found) ? found : null;
    }
  }
}
