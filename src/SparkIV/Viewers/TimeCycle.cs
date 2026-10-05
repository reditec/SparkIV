using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// The light of a time and weather outside, from the game's pc\data\timecyc.dat: per weather (EXTRASUNNY,
  /// SUNNY, CLOUDY ...) a line for each of 0, 5, 6, 7, 9, 12, 18, 19, 20, 21 and 22 o'clock, its columns: the
  /// light from around (Amb0, Amb1), the sun's (Dir), the sky's top and bottom, the sun's core and corona
  /// (each RGB 0 to 255), its size, sprite brightness, how far is drawn (FarClp) and where the fog starts
  /// (FogSt), the clouds' colours, the water's (RGBA), then exposure, grading, ... and the lights' multipliers
  /// (DirLightMult at 57, AmbLightMult0 and 1 at 58 and 59, SkyLightMult, SunMult). Between two times the
  /// values are blended.
  /// </summary>
  public sealed class TimeCycle
  {
    private static readonly int[] Hours = { 0, 5, 6, 7, 9, 12, 18, 19, 20, 21, 22 };

    public Vector3 AmbientAbove { get; private init; }
    public Vector3 AmbientBelow { get; private init; }
    public Vector3 Sun { get; private init; }
    public Vector3 SkyTop { get; private init; }
    public Vector3 SkyBottom { get; private init; }
    public Vector4 Water { get; private init; }
    public float FarClip { get; private init; }
    public float FogStart { get; private init; }
    public float SunMultiplier { get; private init; }
    /// <summary>How the picture is exposed: the lights' values (with their multipliers, up to 10) times this come out about 0 to 2.</summary>
    public float Exposure { get; private init; }
    public float AmbientMultiplierAbove { get; private init; }
    public float AmbientMultiplierBelow { get; private init; }

    /// <summary>The weather's line at the hour (blended between the two times round it), or null without the file or weather.</summary>
    public static TimeCycle? Load(string gameFolder, string weather, float hour)
    {
      string path = Path.Combine(gameFolder, "pc", "data", "timecyc.dat");
      if (!File.Exists(path))
      {
        return null;
      }
      var lines = new List<float[]>();
      bool inWeather = false;
      foreach (string raw in File.ReadLines(path))
      {
        string line = raw.Trim();
        if (line.StartsWith("//////////", StringComparison.Ordinal))
        {
          if (inWeather)
          {
            break;
          }
          inWeather = line.TrimStart('/').Trim().Equals(weather, StringComparison.OrdinalIgnoreCase);
          continue;
        }
        if (!inWeather || line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
        {
          continue;
        }
        var values = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f).ToArray();
        if (values.Length >= 62)
        {
          lines.Add(values);
        }
      }
      if (lines.Count < Hours.Length)
      {
        return null;
      }
      // The two times round the hour (after 22 o'clock towards midnight again).
      hour = ((hour % 24) + 24) % 24;
      int after = Array.FindIndex(Hours, h => h > hour);
      int before = after < 0 ? Hours.Length - 1 : after - 1;
      float from = Hours[before], to = after < 0 ? 24 : Hours[after];
      var a = lines[before];
      var b = lines[after < 0 ? 0 : after];
      float t = to > from ? (hour - from) / (to - from) : 0;
      float V(int i) => a[i] + (b[i] - a[i]) * t;
      Vector3 Rgb(int i) => new Vector3(V(i), V(i + 1), V(i + 2)) / 255f;
      return new TimeCycle
      {
        AmbientAbove = Rgb(0),
        AmbientBelow = Rgb(3),
        Sun = Rgb(6),
        SkyTop = Rgb(9),
        SkyBottom = Rgb(12),
        FarClip = V(23),
        FogStart = V(24),
        Water = new Vector4(Rgb(31), V(34) / 255f),
        SunMultiplier = V(57),
        Exposure = V(35),
        AmbientMultiplierAbove = V(58),
        AmbientMultiplierBelow = V(59),
      };
    }

    /// <summary>
    /// The "sky" message for the page: the sun (where it is at the hour: rising in the east, at noon high in
    /// the south, setting in the west; the game's exact path isn't known), the light from around above and
    /// below, the sky's colours, the haze (from where the fog starts to how far is drawn) and the water's colour;
    /// the clouds' noise textures' addresses where there are clouds.
    /// </summary>
    public object Message(float hour, string? clouds = null, string? cloudDetail = null)
    {
      double t = (hour - 6) / 12.0 * Math.PI;
      var sunDirection = Vector3.Normalize(new Vector3((float)-Math.Cos(t), -0.35f, (float)Math.Max(Math.Sin(t), 0.05)));
      static float[] A(Vector3 v) => new[] { v.X, v.Y, v.Z };
      return new
      {
        type = "sky",
        sunDirection = A(sunDirection),
        sun = A(Sun * SunMultiplier * Exposure),
        ambientAbove = A(AmbientAbove * AmbientMultiplierAbove * Exposure),
        ambientBelow = A(AmbientBelow * AmbientMultiplierBelow * Exposure),
        skyTop = A(SkyTop),
        skyBottom = A(SkyBottom),
        fogStart = FogStart,
        fogEnd = FarClip,
        water = new[] { Water.X, Water.Y, Water.Z, Water.W },
        clouds,
        cloudDetail,
      };
    }
  }
}
