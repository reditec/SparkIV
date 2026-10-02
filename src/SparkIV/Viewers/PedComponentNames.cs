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
        s_names = names;
      }
      return s_names.TryGetValue(hash, out var found) ? found : null;
    }
  }
}
