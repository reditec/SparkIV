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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
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
  public sealed record TimeCycle
  {
    private static readonly int[] Hours = { 0, 5, 6, 7, 9, 12, 18, 19, 20, 21, 22 };

    public Vector3 AmbientAbove { get; private init; }
    public Vector3 AmbientBelow { get; private init; }
    public Vector3 Sun { get; private init; }
    public Vector3 SkyTop { get; private init; }
    public Vector3 SkyBottom { get; private init; }
    public Vector4 Water { get; private init; }
    public float FarClip { get; private init; }
    /// <summary>SprBght (column 22): the strength of the coronas of lamps outside (see the page's gameCoronas).</summary>
    public float SpriteBrightness { get; private init; }
    public float FogStart { get; private init; }
    /// <summary>
    /// rage_postfx's gDepthFxParams (the fog's pass, before the fog): DesaturationFar (column 48), GammaFar (50),
    /// DepthFxNear (51), DepthFxFar (52); GTA IV's trace GTAIV.1 at 12:53: 0.973, 1.18, 15.95, 127.7.
    /// </summary>
    public Vector4 DepthFx { get; private init; }
    public float SunMultiplier { get; private init; }
    /// <summary>How the picture is exposed: the lights' values (with their multipliers, up to 10) times this come out about 0 to 2.</summary>
    public float Exposure { get; private init; }
    public float AmbientMultiplierAbove { get; private init; }
    public float AmbientMultiplierBelow { get; private init; }
    /// <summary>
    /// What the light from around reaches of what moves (peds, a cutscene's props, vehicles), not of the map: a
    /// modifier's AmbientDownMult (column 19), 1 without one. GTAIV.exe gives such an object the value of its room's
    /// modifier (0xA0D720 → 0xAEF820; x the room's blend, eased over its portals) as globalScalars.z (0xB12370,
    /// 0xC645A0 → 0xAC6FB0), which their vertex shaders multiply the vertex colour's AO by (gta_ped: AO = red x
    /// globalScalars.z); the map gets 254 / 255. The TLAD trace GTAIV.3, et01_aa: em_4's 0.20, globalScalars.z 0.2
    /// for every ped and Elizabeta's earrings, 0.996 for the map.
    /// </summary>
    public float ActorAmbient { get; private init; } = 1f;
    /// <summary>
    /// How bright the water mirrors (water.fxc's waterReflectionScale): the line's 126th number. GTAIV.exe's
    /// reader (0xAEFEB0, its sscanf's 126th target) puts it at +0x80 of the time's values, which 0xAD7EF0 sets as
    /// waterReflectionScale; the names in the file's header don't line up with its numbers that far.
    /// </summary>
    public float WaterReflection { get; private init; }
    /// <summary>The picture's gamma (column 47, "Gamma"): rage_postfx.fxc maps luminance L to L^gamma, the hue kept.</summary>
    public float Gamma { get; private init; }

    // The rest of rage_postfx.fxc's tone mapping, as GTAIV.exe sets it (0x930A80 → 0x930690 → 0x92F570) from
    // the time cycle's values, which its reader (0xAEFEB0) fills from these columns:
    /// <summary>ToneMapParams.y (column 37, "MidGreyValue"): the picture = light x exposure x this / the adapted luminance.</summary>
    public float MidGrey { get; private init; }
    /// <summary>IntensityBloom (column 38): the bloom added before the curve, x 0.5 (ToneMapParams.z) x 0.25.</summary>
    public float Bloom { get; private init; }
    /// <summary>
    /// ToneMapParams.x (column 36, "BrightPassThreshold"): only what of the bloom lies above it (in the picture's
    /// units) is added (rage_postfx.fxc program 12: max(bloom x Exposure - x x adapted / MidGrey, 0) x z x 0.25).
    /// The TLAD trace GTAIV.4 (et01_ba): c81 = (0.16, 0.62, 1.088), column 36 0.16 at 12h EXTRASUNNY.
    /// </summary>
    public float BrightPass { get; private init; }
    /// <summary>deSatContrastGamma.x (column 45): how much colour stays, lerp(luminance, colour, this).</summary>
    public float Saturation { get; private init; }
    /// <summary>ColorCorrect (columns 39 to 41, bytes / 255; the shader doubles it).</summary>
    public Vector3 ColorCorrect { get; private init; }
    /// <summary>ColorShift (columns 42 to 44, bytes / 255; w = 1 / (its luminance + 0.001)).</summary>
    public Vector3 ColorShift { get; private init; }
    /// <summary>AdaptedLumMin and AdaptedLumMax (columns 53, 54): the eye's adaptation kept within them.</summary>
    public float AdaptedLumMin { get; private init; }
    public float AdaptedLumMax { get; private init; }
    /// <summary>AdaptTime (column 55 x 10): from 1 on the adaptation follows by min(seconds / sqrt(2 x this), 1) a frame; below, at once.</summary>
    public float AdaptTime { get; private init; }

    /// <summary>SkyLightMult (column 60, blended between the hours by the larger one's weight squared): the fog's colours are the sky's times this (0xAF3A50).</summary>
    public float SkyLightMultiplier { get; private init; }
    /// <summary>The near fog's amount (column 11, / 255: the alpha packed with the sky bottom at +0x2C).</summary>
    public float NearFogAmount { get; private init; }
    /// <summary>The near fog's colour where a modifier gives one (globalFogColorN), else null: the sky bottom x SkyLightMult.</summary>
    public Vector3? NearFogColor { get; private init; }

    /// <summary>gDirectionalLight.w (GTAIV.exe 0xAC30F0, 0xAC51F0): max(1, (SkyLightMult + SunMult) / DirLightMult), columns 60, 61, 57; scales the sun's shine.</summary>
    public float DirectionalLightW { get; private init; }
    /// <summary>globalScalars.w (0xAC6F70): 0.6 x the 64th number (0 to 4), +0xD8 of the time's values; scales the mirrored surroundings in the shine.</summary>
    public float GlobalScalarsW { get; private init; }

    /// <summary>
    /// The film grain (column 10, the header's "Sky top" green; GTAIV.exe's parser 0xAEFEB0 reads it as an int into
    /// +0x20C of the time's values, blended between the hours as an int by 0xAF0DB0): rage_postfx's NoiseParams.z is
    /// it / 64 (0x930268); none while 0. Only TLAD's file has it (25 at noon in EXTRASUNNY; GTA IV's and TBoGT's 0).
    /// </summary>
    public int Noise { get; private init; }

    /// <summary>
    /// The sky as gta_atmoscatt_clouds draws it (its programs and constants in the TLAD trace GTAIV.4): SkyColor
    /// (columns 64 to 66), AzimuthColor (67 to 69), AzimuthColorEast (70 to 72), AzimuthHeight (77), AzimuthStrength
    /// (78), SunColor (103 to 105), HDRSunExposure's y (119).
    /// </summary>
    public Vector3 SkyColor { get; private init; }
    public Vector3 AzimuthColor { get; private init; }
    public Vector3 AzimuthColorEast { get; private init; }
    public float AzimuthHeight { get; private init; }
    public float AzimuthStrength { get; private init; }
    public Vector3 SkySunColor { get; private init; }
    public float SkySunExposure { get; private init; }

    /// <summary>
    /// The clouds as gta_atmoscatt_clouds draws them, its constants matched in both traces (TLAD's GTAIV.4 at 12:50,
    /// GTA IV's GTAIV.1 at 10:51): CloudColor (columns 81 to 83), SunsetColor (73 to 75, times column 106: its w in
    /// GTAIV.1 at 13:07, 0.843, the colour 0.843 x columns 73 to 75; 0 in TLAD's trace, where 106 is 0),
    /// CloudShadowStrength (84), CloudThreshold (85), CloudBias (86), CloudShadowOffset (87), CloudInscatteringRange (88), the detail's scale (90) and strength (91), the thickness (92), CloudFadeOut (94),
    /// TopCloudBiasDetailThresholdHeight (95 to 98) and TopCloudColor (99 to 101 plus 73 to 75).
    /// </summary>
    public Vector3 CloudColor { get; private init; }
    public Vector3 SunsetColor { get; private init; }
    public Vector3 TopCloudColor { get; private init; }
    public Vector4 TopCloud { get; private init; }
    /// <summary>CloudThreshold, CloudBias, CloudShadowStrength, CloudInscatteringRange.</summary>
    public Vector4 CloudShape { get; private init; }
    /// <summary>The thickness, the detail's scale and strength, CloudFadeOut.</summary>
    public Vector4 CloudDetail { get; private init; }
    public float CloudShadowOffset { get; private init; }
    /// <summary>
    /// The clouds' edge (column 89): GTAIV.exe 0x615D98 hands the shader EdgeSmooth = 1 / it (the sky's +0x184, next
    /// to the detail's scale 90, strength 91 and the thickness 92 at +0x188 to +0x190). 1 in GTA IV's trace GTAIV.1
    /// (column 89 = 1), infinite in TLAD's GTAIV.3/.4 (column 89 = 0).
    /// </summary>
    public float CloudEdge { get; private init; }
    /// <summary>
    /// How fast the clouds' noise moves (column 107; the parser 0xAEFEB0 puts it at +0x1B4, 0xDBBDF0 hands it to the
    /// noise, whose Phase grows by it per game hour, 0x62E110: 1.7 in TLAD's trace at noon, its Phase 1.8 on in a
    /// little over an hour).
    /// </summary>
    public float CloudSpeed { get; private init; }

    /// <summary>
    /// The name of the weather of this number (a script's FORCE_WEATHER_NOW(1)): the file's blocks in their order,
    /// EXTRASUNNY, SUNNY, SUNNY_WINDY, CLOUDY, RAIN, DRIZZLE, FOGGY, LIGHTNING (bernie3's FORCE_WEATHER_NOW(1)
    /// before bc_3, sunny in the game); null without the file or such a block.
    /// </summary>
    public static string? WeatherName(string gameFolder, int weather)
    {
      string path = Path.Combine(gameFolder, "pc", "data", "timecyc.dat");
      if (weather < 0 || !File.Exists(path))
      {
        return null;
      }
      return File.ReadLines(path).Select(l => l.Trim()).Where(l => l.StartsWith("//////////", StringComparison.Ordinal))
                 .Select(l => l.TrimStart('/').Trim()).ElementAtOrDefault(weather);
    }

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
      // SkyLightMult (column 60) GTAIV.exe 0xAF0DB0 blends unlike the rest: from the smaller value towards the larger
      // by the larger one's weight squared (its field +0x24; GTA IV's trace GTAIV.1 at 13:07, HDRExposure 23.45 =
      // 2.85 x (8 + 6.5 x 0.1873^2), not 2.85 x 9.22).
      float skyLight = b[60] >= a[60] ? a[60] + (b[60] - a[60]) * t * t : b[60] + (a[60] - b[60]) * (1 - t) * (1 - t);
      return new TimeCycle
      {
        AmbientAbove = Rgb(0),
        AmbientBelow = Rgb(3),
        Sun = Rgb(6),
        SkyTop = Rgb(9),
        SkyBottom = Rgb(12),
        FarClip = V(23),
        SpriteBrightness = V(22),
        FogStart = V(24),
        DepthFx = new Vector4(V(48), V(50), V(51), V(52)),
        // The water's colour (columns 31 to 34) packed as bytes: 0xAF0DB0 blends them as ints, cut off; water.fxc's
        // waterColour is them / 256 (GTA IV's trace GTAIV.1 at 12:53: 8, 7, 6, 239 / 256 for 8.39 ... 239).
        Water = new Vector4(MathF.Floor(V(31)), MathF.Floor(V(32)), MathF.Floor(V(33)), MathF.Floor(V(34))) / 256f,
        SunMultiplier = V(57),
        Exposure = V(35),
        AmbientMultiplierAbove = V(58),
        AmbientMultiplierBelow = V(59),
        WaterReflection = a.Length > 125 && b.Length > 125 ? V(125) : 1f,
        Gamma = V(47),
        MidGrey = V(37),
        Bloom = V(38),
        BrightPass = V(36),
        Saturation = V(45),
        ColorCorrect = Rgb(39),
        ColorShift = Rgb(42),
        AdaptedLumMin = V(53),
        AdaptedLumMax = V(54),
        AdaptTime = V(55) * 10,
        SkyLightMultiplier = skyLight,
        NearFogAmount = V(11) / 255f,
        DirectionalLightW = Math.Max(1f, (skyLight + V(61)) / Math.Max(V(57), 1e-4f)),
        GlobalScalarsW = 0.6f * Math.Clamp(V(63), 0f, 4f),
        Noise = (int)V(10),
        SkyColor = a.Length > 119 && b.Length > 119 ? new Vector3(V(64), V(65), V(66)) : Vector3.Zero,
        AzimuthColor = a.Length > 119 && b.Length > 119 ? new Vector3(V(67), V(68), V(69)) : Vector3.Zero,
        AzimuthColorEast = a.Length > 119 && b.Length > 119 ? new Vector3(V(70), V(71), V(72)) : Vector3.Zero,
        AzimuthHeight = a.Length > 119 && b.Length > 119 ? V(77) : 0,
        AzimuthStrength = a.Length > 119 && b.Length > 119 ? V(78) : 0,
        SkySunColor = a.Length > 119 && b.Length > 119 ? new Vector3(V(103), V(104), V(105)) : Vector3.Zero,
        SkySunExposure = a.Length > 119 && b.Length > 119 ? V(119) : 0,
        CloudColor = a.Length > 119 && b.Length > 119 ? new Vector3(V(81), V(82), V(83)) : Vector3.Zero,
        SunsetColor = a.Length > 119 && b.Length > 119 ? new Vector3(V(73), V(74), V(75)) * V(106) : Vector3.Zero,
        TopCloudColor = a.Length > 119 && b.Length > 119 ? new Vector3(V(99) + V(73), V(100) + V(74), V(101) + V(75)) : Vector3.Zero,
        TopCloud = a.Length > 119 && b.Length > 119 ? new Vector4(V(95), V(96), V(97), V(98)) : Vector4.Zero,
        CloudShape = a.Length > 119 && b.Length > 119 ? new Vector4(V(85), V(86), V(84), V(88)) : Vector4.Zero,
        CloudDetail = a.Length > 119 && b.Length > 119 ? new Vector4(V(92), V(90), V(91), V(94)) : Vector4.Zero,
        CloudShadowOffset = a.Length > 119 && b.Length > 119 ? V(87) : 0,
        CloudEdge = a.Length > 119 && b.Length > 119 ? V(89) : 0,
        CloudSpeed = a.Length > 119 && b.Length > 119 ? V(107) : 0,
      };
    }

    /// <summary>
    /// The time cycle modifiers of pc\data\timecyclemodifiers.dat, ...2, ...3 and ...4 by the hash of their name
    /// (GxtFile.Hash, as an interior's rooms name theirs). A line: the name, then its numbers - MinFarClip,
    /// MaxFarClip, MinFogStart, MaxFogStart, AmbientLight0 RGBA and multiplier, AmbientLight1 RGBA and
    /// multiplier, DirectionalLight RGBA and multiplier, AmbientDownMult, Fog RGBA, NearFog RGBA, FogMult,
    /// NearFogAxis, Temperature, PostFxStrength, Exposure, ExposureMultiplier, BrightPassThreshold, MidGreyValue,
    /// IntensityBloom, ColourCorrect RGB, ColourAdd RGB, Desaturation, Contrast, Gamma, ... LumMin, LumMax,
    /// LumAdaptDelay, ... (the files' header); -1 where it changes nothing.
    /// </summary>
    public static Dictionary<uint, float[]> LoadModifiers(string gameFolder)
    {
      var modifiers = new Dictionary<uint, float[]>();
      foreach (string file in new[] { "timecyclemodifiers.dat", "timecyclemodifiers2.dat", "timecyclemodifiers3.dat", "timecyclemodifiers4.dat" })
      {
        string path = Path.Combine(gameFolder, "pc", "data", file);
        // An episode without its own: GTA IV's (the folder above it), as the game loads GTA IV's data first.
        if (!File.Exists(path) && Directory.GetParent(Path.TrimEndingDirectorySeparator(gameFolder)) is DirectoryInfo parent)
        {
          path = Path.Combine(parent.FullName, "pc", "data", file);
        }
        if (!File.Exists(path))
        {
          continue;
        }
        foreach (string raw in File.ReadLines(path))
        {
          string line = raw.Trim();
          if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
          {
            continue;
          }
          var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
          var values = parts.Skip(1).Select(s => float.TryParse(s.TrimEnd('f'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : -1f).ToArray();
          if (values.Length >= 54)
          {
            modifiers.TryAdd(RageLib.GtaIV.Pc.Text.GxtFile.Hash(parts[0]), values);
          }
        }
      }
      return modifiers;
    }

    /// <summary>
    /// The time cycle as a modifier changes it: each of its values that isn't -1 taken instead. Checked against a
    /// D3D capture of bc_1 in Bernie's flat (BernieCrane): the game's gLightAmbient0 there was AmbientLight0's
    /// RGB / 255 x its multiplier exactly (4.447, 4.182, 3.741), AmbientLight1 the same, its sun 0 (the
    /// directional multiplier 0). The colours' alpha (255 there) is taken as how much of the modifier's colour
    /// is used (not checked otherwise).
    /// </summary>
    public TimeCycle With(float[] m)
    {
      bool Set(int i) => i < m.Length && Math.Abs(m[i] + 1f) > 1e-4f;
      float Or(int i, float value) => Set(i) ? m[i] : value;
      Vector3 Rgb(int i) => new Vector3(m[i], m[i + 1], m[i + 2]) / 255f;
      Vector3 Light(int i, Vector3 light) => Set(i + 4) ? Vector3.Lerp(light, Rgb(i) * m[i + 4], Math.Clamp(m[i + 3] / 255f, 0f, 1f)) : light;
      return this with
      {
        AmbientAbove = Light(4, AmbientAbove * AmbientMultiplierAbove),
        AmbientMultiplierAbove = 1f,
        AmbientBelow = Light(9, AmbientBelow * AmbientMultiplierBelow),
        AmbientMultiplierBelow = 1f,
        Sun = Light(14, Sun * SunMultiplier),
        SunMultiplier = 1f,
        FarClip = Or(1, FarClip),
        ActorAmbient = Or(19, ActorAmbient),
        // The fog as the modifier sets it (the TLAD trace GTAIV.3, et01_aa in Elizabeta's flat, modifier em_4:
        // globalFogParams c41 = (3, 1500, 0, 0.784) - MaxFogStart (column 3) 3, NearFog's alpha (column 27) 200
        // / 255 -, and the near fog's colour, measured back from the light before and after the fog, NearFog's
        // RGB (columns 24 to 26, 59/52/43) / 255 x 7.0 = column 66, as outside the sky bottom x SkyLightMult:
        // the room fogged three quarters towards a warm grey from 3 m on). MinFogStart (column 2) where there is
        // no MaxFogStart (BernieCrane: 300, its NearFog's alpha 0 - no near fog).
        FogStart = Set(3) ? m[3] : Or(2, FogStart),
        NearFogAmount = Set(27) ? Math.Clamp(m[27] / 255f, 0f, 1f) : NearFogAmount,
        NearFogColor = Set(27) && m[27] > 0 ? Rgb(24) * (Set(66) ? m[66] : SkyLightMultiplier) : NearFogColor,
        Exposure = Set(32) ? m[32] * Or(33, 1f) : Exposure,
        MidGrey = Or(35, MidGrey),
        Bloom = Or(36, Bloom),
        BrightPass = Or(34, BrightPass),
        ColorCorrect = Set(37) ? Rgb(37) : ColorCorrect,
        ColorShift = Set(40) ? Rgb(40) : ColorShift,
        Saturation = Or(43, Saturation),
        Gamma = Or(45, Gamma),
        AdaptedLumMin = Or(51, AdaptedLumMin),
        AdaptedLumMax = Or(52, AdaptedLumMax),
        AdaptTime = Set(53) ? m[53] * 10 : AdaptTime,
        // GlobEnvRefl (column 54) as globalScalars.w = 0.6 x it: em_4 and BernieCrane set 0, no mirrored
        // surroundings in the shine inside (the TLAD trace GTAIV.3 in Elizabeta's flat: c39.w = 0). With the
        // outside's 0.216 Bernie's hall wallpaper by his door showed bright vertical streaks over it, twice as
        // light as the game's video.
        GlobalScalarsW = Set(54) ? 0.6f * Math.Clamp(m[54], 0f, 4f) : GlobalScalarsW,
        // Halved under a modifier (GTAIV.exe 0xAEE7E0: x 0.5 by the modifier's strength, 0xFE8830).
        Noise = Noise / 2,
      };
    }

    /// <summary>
    /// The "sky" message for the page: the sun (where it is at the hour: rising in the east, at noon high in
    /// the south, setting in the west; the game's exact path isn't known), the light from around above and
    /// below, the sky's colours, the haze (from where the fog starts to how far is drawn) and the water's colour;
    /// the clouds' noise textures' addresses where there are clouds.
    /// </summary>
    /// <summary>
    /// Where the sun is (towards it) at the hour, as GTAIV.exe works it out (0xAEF38E in 0xAEF0E0): an angle from the
    /// time - before 6 o'clock t x pi/12, from 6 to 20 o'clock pi/2 + (t - 6) x pi/14, after it 3pi/2 + (t - 20) x
    /// pi/8, kept within pi/2 and 3pi/2 - then (1 - 2 x 0.1) x (angle - pi/2) + (0.1 + 0.5) x pi (0x103FC00 = 0.1),
    /// and the direction normalize(sin a - cos a x (0, -0.342, 0.940)) (the sun's axis at 0x103FBF0, 20° from
    /// upright). At noon (0.179, -0.337, 0.925). Before 6 and after 20 o'clock the game also turns it towards
    /// the horizon (not done here: the cutscenes so far are by day).
    /// </summary>
    public static Vector3 SunDirection(float hour)
    {
      double angle = Math.Clamp(SunAngle(hour), 1.570796, 4.712389);
      const double Offset = 0.1;
      return SunOnAxis((1 - Offset * 2) * (angle - 1.570796) + (Offset + 0.5) * Math.PI);
    }

    /// <summary>
    /// Where the sky draws the sun (towards it) at the hour: GTAIV.exe 0xDBBDF0 hands gta_atmoscatt_clouds its
    /// SunDirection from the same angle as <see cref="SunDirection"/>, but neither kept within pi/2 and 3pi/2 nor
    /// offset. So the sun in the sky is higher than the light at the same hour. The traces' sky draws (VS c64,
    /// against the clock from gDayNightEffects) match it to 0.0001 at 09:35, 09:44, 10:39, 10:42, 12:56 and 13:12.
    /// </summary>
    public static Vector3 SkySunDirection(float hour) => SunOnAxis(SunAngle(hour));

    static double SunAngle(float hour)
    {
      double t = ((hour % 24) + 24) % 24;
      return t < 6 ? t * 0.2617994 : t < 20 ? (t - 6) * 0.2243995 + 1.570796 : (t - 20) * 0.3926991 + 4.712389;
    }

    static Vector3 SunOnAxis(double angle)
    {
      double s = Math.Sin(angle), c = Math.Cos(angle);
      var axis = new Vector3(0f, -0.3420202f, 0.9396926f);
      return Vector3.Normalize(new Vector3((float)s, 0f, 0f) - (float)c * axis);
    }

    /// <param name="rooms">Per room of the location (in its list) its own message (the time cycle as the room's
    /// modifier changes it), or null for none.</param>
    public object Message(float hour, string? clouds = null, string? cloudDetail = null, object?[]? rooms = null)
    {
      var sunDirection = SunDirection(hour);
      static float[] A(Vector3 v) => new[] { v.X, v.Y, v.Z };
      return new
      {
        type = "sky",
        sunDirection = A(sunDirection),
        skySunDirection = A(SkySunDirection(hour)),
        sun = A(Sun * SunMultiplier * Exposure),
        ambientAbove = A(AmbientAbove * AmbientMultiplierAbove * Exposure),
        ambientBelow = A(AmbientBelow * AmbientMultiplierBelow * Exposure),
        // For the lamps: their strength is in the sun's units (GTAIV.exe passes both to the lights unchanged).
        exposure = Exposure,
        waterReflection = WaterReflection,
        gamma = Gamma,
        // rage_postfx.fxc's fog (see gameFog in gtaiv-pc.js), as GTAIV.exe 0xAF3A50 sets it: globalFogColorN =
        // the sky bottom (columns 12 to 14, / 255) x SkyLightMult (column 60); globalFogParams = (FogSt (column 24),
        // FarClp (column 23), NearFogAmount = column 11 / 255, the same).
        fog = new
        {
          nearColor = A(NearFogColor ?? SkyBottom * SkyLightMultiplier),
          start = FogStart,
          end = Math.Max(FarClip, FogStart + 1),
          nearAmount = NearFogAmount,
          depthFx = new[] { DepthFx.X, DepthFx.Y, DepthFx.Z, DepthFx.W },
        },
        actorAmbient = ActorAmbient,
        directionalLightW = DirectionalLightW,
        globalScalarsW = GlobalScalarsW,
        spriteBrightness = SpriteBrightness,
        // gDayNightEffects.x (GTAIV.exe 0x9CB895): seconds of the day / 43200 up to noon, then back down to 0.
        dayNight = hour <= 12 ? Math.Max(hour, 0) / 12f : Math.Max(0, 1 - (hour - 12) / 12f),
        tone = new
        {
          midGrey = MidGrey,
          // ToneMapParams.z (GTAIV.exe 0x92FE85): IntensityBloom (column 38) x 0.5 x 0.2 x (0x103F9B4 x 0.5), that
          // global 10 at run time (an apitrace capture: z 1.435 for IntensityBloom 2.87).
          bloom = Bloom * 0.5f,
          brightPass = BrightPass,
          saturation = Saturation,
          colorCorrect = A(ColorCorrect),
          colorShift = A(ColorShift),
          adaptedLumMin = AdaptedLumMin,
          adaptedLumMax = AdaptedLumMax,
          adaptTime = AdaptTime,
          // NoiseParams.z (GTAIV.exe 0x930268): the film grain / 64 (0.375 in TLAD's trace at 12:50).
          noise = Noise / 64f,
        },
        // The sky in light (see SKY_FRAGMENT): GTAIV.exe 0xDBC049 sets HDRExposure = SkyLightMult (column 60) x 2.85
        // (0xEF4224; the traces: 8 x 2.85 = 22.8 in GTA IV's at 10:51, 19.25 x 2.85 live in TLAD at noon) and
        // HDRSunExposure = (1 x 0.35 (0xFE880C), column 119); HDRExposureClamp 30 (0xDBC0A3).
        skyHdr = new
        {
          color = A(SkyColor),
          azimuth = A(AzimuthColor),
          azimuthEast = A(AzimuthColorEast),
          height = AzimuthHeight,
          strength = AzimuthStrength,
          sunColor = A(SkySunColor),
          exposure = SkyLightMultiplier * 2.85f,
          sunExposure = new[] { 0.35f, SkySunExposure },
          clamp = 30f,
          cloudColor = A(CloudColor),
          sunsetColor = A(SunsetColor),
          topCloudColor = A(TopCloudColor),
          topCloud = new[] { TopCloud.X, TopCloud.Y, TopCloud.Z, TopCloud.W },
          cloudShape = new[] { CloudShape.X, CloudShape.Y, CloudShape.Z, CloudShape.W },
          cloudDetail = new[] { CloudDetail.X, CloudDetail.Y, CloudDetail.Z, CloudDetail.W },
          cloudShadowOffset = CloudShadowOffset,
          cloudEdge = CloudEdge,
          cloudSpeed = CloudSpeed,
        },
        skyTop = A(SkyTop),
        skyBottom = A(SkyBottom),
        fogStart = FogStart,
        fogEnd = FarClip,
        water = new[] { Water.X, Water.Y, Water.Z, Water.W },
        // water.fxc's bottomSkyColour.w: its own colour is waterColour x 0.5 x this (GTAIV.1 at 12:53: 8.146).
        skyLight = SkyLightMultiplier,
        clouds,
        cloudDetail,
        rooms,
      };
    }
  }
}
