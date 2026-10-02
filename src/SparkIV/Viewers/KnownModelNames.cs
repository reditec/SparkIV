using System.Collections.Generic;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// Names of entries in model dictionaries that no .ide file names (the files only have their hashes):
  /// the plants of pc\models\plantsmgr.wdd, grass0_1 to grass3_4 (four plant sets of four models, as in
  /// San Andreas).
  /// </summary>
  internal static class KnownModelNames
  {
    /// <summary>
    /// The texture the game gives a model itself, where the model doesn't name it (or names a leftover):
    /// grassS_M gets txgrassS_(M-1) of pc\textures\plantsmgr.wtd, one texture per plant model. Null for
    /// any other model.
    /// </summary>
    public static string? TextureFor(string modelName)
    {
      var match = System.Text.RegularExpressions.Regex.Match(modelName, @"^grass([0-3])_([1-4])$");
      return match.Success ? "txgrass" + match.Groups[1].Value + "_" + (int.Parse(match.Groups[2].Value) - 1) : null;
    }

    private static Dictionary<uint, string>? s_names;

    /// <summary>The name of a hash, or null if it isn't one of these.</summary>
    public static string? Find(uint hash)
    {
      if (s_names == null)
      {
        var names = new Dictionary<uint, string>();
        for (int set = 0; set < 4; set++)
        {
          for (int model = 1; model <= 4; model++)
          {
            string name = "grass" + set + "_" + model;
            names[RageLib.Text.GxtFile.Hash(name)] = name;
          }
        }
        s_names = names;
      }
      return s_names.TryGetValue(hash, out var found) ? found : null;
    }
  }
}
