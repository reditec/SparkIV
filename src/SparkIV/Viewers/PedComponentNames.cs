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
              names[RageLib.Text.GxtFile.Hash(name)] = name;
            }
          }
        }
        foreach (var anchor in PropAnchors)
        {
          for (int number = 0; number < 40; number++)
          {
            string name = "p_" + anchor + "_" + number.ToString("000");
            names[RageLib.Text.GxtFile.Hash(name)] = name;
          }
        }
        s_names = names;
      }
      return s_names.TryGetValue(hash, out var found) ? found : null;
    }
  }
}
