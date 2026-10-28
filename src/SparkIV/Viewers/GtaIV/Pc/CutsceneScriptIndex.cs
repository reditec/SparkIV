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
using System.Text.Json;
using System.Text.RegularExpressions;
using RageLib.FileSystem;
using RageLib.Scripting;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// Which mission script plays a cutscene and how it colours the vehicles it makes, from the game's scripts
  /// (common\data\cdimages\script.img). No file of the game says which script plays which cutscene (the
  /// names only loosely follow the characters: TLAD's BG07_AA is in stubbs4, e1rb4b in ray2); the scripts
  /// do: START_CUTSCENE_NOW("ET01_BA") in elizabeta1. The game gives a cutscene's vehicle the colours of a
  /// vehicle of the same model in the world (GTAIV.exe 0xC67450, from CCutsceneObject_Init), only without one
  /// the .cut's [VEHICLE_DETAILS] - in a mission that is the one its script made: elizabeta1's
  /// CREATE_CAR(pres) then CHANGE_CAR_COLOUR(0, 0), SET_EXTRA_CAR_COLOURS(0, 0), the black Presidente of
  /// et01_ba. Built from the decompiled scripts when a game is first loaded in SparkIV and again when the
  /// game's scripts or SparkIV change (see <see cref="Fingerprint"/>), kept in the local app data.
  /// </summary>
  public sealed class CutsceneScriptIndex
  {
    // Raised when what is read from the scripts changes, so older indexes are built again.
    private const int Format = 2;

    /// <summary>A vehicle a script makes: its model's hash and the four colours it gives it (-1: not set).</summary>
    public sealed record ScriptVehicle(uint Model, int[] Colours);

    /// <summary>
    /// Per cutscene (upper case): the script that starts it and the vehicles that script makes; what it sets
    /// right before (in the same function, before START_CUTSCENE_NOW): the weather (FORCE_WEATHER_NOW: the
    /// number of its block in timecyc.dat, -1 none), the wind (FORCE_WIND, null none) and the hour
    /// (SET_TIME_OF_DAY / FORCE_TIME_OF_DAY, hours and minutes as hours; null none); and where the mission's marker
    /// stands (null none), where the player is when the mission and its first cutscene start (main.sco: the
    /// mission's registration names its marker, another call places that marker - Bernie3's
    /// sub_879a(18, 2, 0, 32, "M_78", ..., "Bernie3", ...) and sub_5d16(32, 487.5043, -499.1311, 3.6922, ...)).
    /// GTAIV.exe keeps the sea's swell round the player (see the page's dynamicWaterStep).
    /// </summary>
    public sealed record Entry(string Script, List<ScriptVehicle> Vehicles, int Weather = -1, float? Wind = null, float? Hour = null, float[]? Marker = null);

    public string Fingerprint { get; init; } = "";
    public Dictionary<string, Entry> Cutscenes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The colours the script playing this cutscene gives a vehicle of this model (-1 where it sets none: the
    /// script's CHANGE_CAR_COLOUR without SET_EXTRA_CAR_COLOURS), or null without any.
    /// </summary>
    public int[]? ColoursFor(string cutscene, uint model) =>
      Cutscenes.TryGetValue(cutscene, out var entry) ? entry.Vehicles.FirstOrDefault(v => v.Model == model && v.Colours.Any(c => c >= 0))?.Colours : null;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CutsceneScriptIndex> s_loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The script.img of a game folder (an episode's own).</summary>
    public static string ScriptArchive(string gameFolder) => Path.Combine(gameFolder, "common", "data", "cdimages", "script.img");

    /// <summary>
    /// What the index depends on: the script archive's size and time, SparkIV's build and this class's format.
    /// </summary>
    public static string FingerprintOf(string gameFolder)
    {
      var info = new FileInfo(ScriptArchive(gameFolder));
      string app = typeof(CutsceneScriptIndex).Assembly.GetName().Version?.ToString() ?? "";
      string build = System.IO.File.GetLastWriteTimeUtc(typeof(CutsceneScriptIndex).Assembly.Location).Ticks.ToString(CultureInfo.InvariantCulture);
      return string.Join("|", Format, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc.Ticks : 0, app, build);
    }

    private static string CachePath(string gameFolder)
    {
      string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkIV", "CutsceneScripts");
      string key = Regex.Replace(Path.GetFullPath(gameFolder).ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
      return Path.Combine(dir, key + ".json");
    }

    /// <summary>The index of a game folder already loaded or kept and still current, else null (see <see cref="Build"/>).</summary>
    public static CutsceneScriptIndex? Current(string gameFolder)
    {
      lock (Gate)
      {
        string fingerprint = FingerprintOf(gameFolder);
        if (s_loaded.TryGetValue(gameFolder, out var known) && known.Fingerprint == fingerprint)
        {
          return known;
        }
        try
        {
          string path = CachePath(gameFolder);
          if (System.IO.File.Exists(path) &&
              JsonSerializer.Deserialize<CutsceneScriptIndex>(System.IO.File.ReadAllText(path)) is CutsceneScriptIndex kept &&
              kept.Fingerprint == fingerprint)
          {
            // (Read back, the dictionary has lost its comparer: by name again whatever the case, as built.)
            kept = new CutsceneScriptIndex { Fingerprint = kept.Fingerprint, Cutscenes = new(kept.Cutscenes, StringComparer.OrdinalIgnoreCase) };
            s_loaded[gameFolder] = kept;
            return kept;
          }
        }
        catch (Exception)
        {
          // A kept index that can't be read: built again.
        }
        return null;
      }
    }

    private static readonly Dictionary<string, System.Threading.Tasks.Task<CutsceneScriptIndex>> s_building = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The game folder's index: the current one, else built (once at a time per folder: a cutscene opened while
    /// the game's loading builds it waits for that build).
    /// </summary>
    public static System.Threading.Tasks.Task<CutsceneScriptIndex> EnsureAsync(string gameFolder, Action<int, int>? progress = null)
    {
      lock (Gate)
      {
        if (Current(gameFolder) is CutsceneScriptIndex current)
        {
          return System.Threading.Tasks.Task.FromResult(current);
        }
        if (!s_building.TryGetValue(gameFolder, out var task))
        {
          task = System.Threading.Tasks.Task.Run(() =>
          {
            try
            {
              return Build(gameFolder, progress);
            }
            finally
            {
              lock (Gate)
              {
                s_building.Remove(gameFolder);
              }
            }
          });
          s_building[gameFolder] = task;
        }
        return task;
      }
    }

    /// <summary>Whether the game folder's index has to be built (none kept, or the game or SparkIV changed).</summary>
    public static bool NeedsBuild(string gameFolder) =>
      System.IO.File.Exists(ScriptArchive(gameFolder)) && Current(gameFolder) == null;

    /// <summary>
    /// Builds the index of a game folder from its scripts (on a background thread: some seconds), keeps it and
    /// returns it. <paramref name="progress"/> gets how many scripts of how many are done.
    /// </summary>
    public static CutsceneScriptIndex Build(string gameFolder, Action<int, int>? progress = null)
    {
      var index = new CutsceneScriptIndex { Fingerprint = FingerprintOf(gameFolder) };
      var img = new IMGFileSystem();
      img.Open(ScriptArchive(gameFolder));
      // The missions' markers (see Entry): the marker each mission script's registration names, and the places
      // of the markers by the function that places them.
      var markerOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var placed = new Dictionary<int, float[]>();
      try
      {
        var scripts = img.RootDirectory.OfType<File>().Where(f => f.Name.EndsWith(".sco", StringComparison.OrdinalIgnoreCase)).ToList();
        for (int i = 0; i < scripts.Count; i++)
        {
          progress?.Invoke(i, scripts.Count);
          string code;
          try
          {
            var script = new ScriptFile();
            script.Open(new MemoryStream(scripts[i].GetData()));
            code = script.GetCode(CodeFormat.FullDecompile);
          }
          catch (Exception)
          {
            continue;
          }
          string name = Path.GetFileNameWithoutExtension(scripts[i].Name);
          if (name.Equals("main", StringComparison.OrdinalIgnoreCase))
          {
            Markers(code, markerOf, placed);
          }
          var starts = Regex.Matches(code, @"START_CUTSCENE_NOW\(""([^""]+)""\)");
          if (starts.Count == 0)
          {
            continue;
          }
          var vehicles = Vehicles(code);
          foreach (Match start in starts)
          {
            var (weather, wind, hour) = SetBefore(code, start.Index);
            index.Cutscenes.TryAdd(start.Groups[1].Value, new Entry(name, vehicles, weather, wind, hour));
          }
        }
        progress?.Invoke(scripts.Count, scripts.Count);
      }
      finally
      {
        img.Close();
      }
      foreach (var (cutscene, entry) in index.Cutscenes.ToList())
      {
        if (markerOf.TryGetValue(entry.Script, out int marker) && placed.TryGetValue(marker, out var at))
        {
          index.Cutscenes[cutscene] = entry with { Marker = at };
        }
      }
      lock (Gate)
      {
        s_loaded[gameFolder] = index;
        try
        {
          string path = CachePath(gameFolder);
          Directory.CreateDirectory(Path.GetDirectoryName(path)!);
          System.IO.File.WriteAllText(path, JsonSerializer.Serialize(index));
        }
        catch (Exception)
        {
          // Not kept: built again next time.
        }
      }
      return index;
    }

    // A number as the decompiled code writes it: 480,7322f (its decimal comma), 0.0f, (float)250, 3, -1.
    private const string Number = @"(?:\(float\))?-?\d+(?:[.,]\d+)?f?";

    private static float? ParseNumber(string text)
    {
      string t = text.Trim().Replace("(float)", "").TrimEnd('f').Replace(',', '.');
      return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
    }

    // What the script sets in the function of a START_CUTSCENE_NOW before it: the last FORCE_WEATHER_NOW(1),
    // FORCE_WIND(0.0f) and SET_TIME_OF_DAY(5, 0) / FORCE_TIME_OF_DAY, with numbers written out.
    private static (int Weather, float? Wind, float? Hour) SetBefore(string code, int at)
    {
      int from = Math.Max(code.LastIndexOf("\nvoid ", at, StringComparison.Ordinal), code.LastIndexOf("\nfunction ", at, StringComparison.Ordinal));
      string before = code.Substring(Math.Max(0, from), at - Math.Max(0, from));
      var weather = Regex.Matches(before, @"FORCE_WEATHER_NOW\((\d+)\)").LastOrDefault();
      var wind = Regex.Matches(before, @"FORCE_WIND\((" + Number + @")\)").LastOrDefault();
      var time = Regex.Matches(before, @"(?:SET|FORCE)_TIME_OF_DAY\((\d+),\s*(\d+)\)").LastOrDefault();
      return (weather != null ? int.Parse(weather.Groups[1].Value, CultureInfo.InvariantCulture) : -1,
              wind != null ? ParseNumber(wind.Groups[1].Value) : null,
              time != null ? int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture) + int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) / 60f : null);
    }

    // main.sco's missions: each registration (strand, step, trigger type, contact point, its text, three
    // numbers, the script's name: sub_879a(18, 2, 0, 32, "M_78", 78, 1, 1, "Bernie3", ...); TLAD's
    // sub_7180(4, 0, 0, 11, "E1M_19", 115, 1, 1, "Elizabeta1")) keeps its contact point's number,
    // except for trigger types 1 to 7, which get 50, none (sub_7020); a mission's blip goes to the contact point
    // of that number (sub_bb0f). The contact points are placed by the function asserting "Store_Contact_Point"
    // (sub_5d16(32, 487,5043f, -499,1311f, 3,6922f, ...)); the map's other blips have numbers of their own
    // ("Add_Map_Blip": sub_9a75(32, -477,8248f, ...)).
    private static void Markers(string code, Dictionary<string, int> markerOf, Dictionary<int, float[]> placed)
    {
      int named = code.IndexOf("Store_Contact_Point", StringComparison.Ordinal);
      int from = named < 0 ? -1 : code.LastIndexOf("\nvoid ", named, StringComparison.Ordinal);
      var store = from < 0 ? Match.Empty : Regex.Match(code.Substring(from + 6), @"^\w+");
      if (!store.Success)
      {
        return;
      }
      foreach (Match m in Regex.Matches(code, @"sub_\w+\(\d+, \d+, (\d+), (\d+), ""\w+"", \d+, \d+, \d+, ""(\w+)"""))
      {
        int type = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (type < 1 || type > 7)
        {
          markerOf.TryAdd(m.Groups[3].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
      }
      foreach (Match m in Regex.Matches(code, @"\b" + store.Value + @"\((\d+), (" + Number + "), (" + Number + "), (" + Number + ")[,)]"))
      {
        if (ParseNumber(m.Groups[2].Value) is float x && ParseNumber(m.Groups[3].Value) is float y && ParseNumber(m.Groups[4].Value) is float z)
        {
          placed.TryAdd(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), new[] { x, y, z });
        }
      }
    }

    // The vehicles a script makes with a model's hash written out (CREATE_CAR(0x8b0d2ba6, x, y, z, &L[503], 1))
    // and the colours it gives that vehicle right after (CHANGE_CAR_COLOUR(L[503], 0, 0);
    // SET_EXTRA_CAR_COLOURS(L[503], 0, 0)), within the next few lines.
    private static List<ScriptVehicle> Vehicles(string code)
    {
      var list = new List<ScriptVehicle>();
      var lines = code.Split('\n');
      for (int i = 0; i < lines.Length; i++)
      {
        var create = Regex.Match(lines[i], @"CREATE_CAR\((0x[0-9a-fA-F]+|-?\d+),[^&]*&([^,]+),");
        if (!create.Success)
        {
          continue;
        }
        string hash = create.Groups[1].Value;
        uint model = hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
          ? uint.Parse(hash.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
          : unchecked((uint)int.Parse(hash, CultureInfo.InvariantCulture));
        string handle = Regex.Escape(create.Groups[2].Value.Trim());
        var colours = new[] { -1, -1, -1, -1 };
        for (int k = i + 1; k < Math.Min(lines.Length, i + 8); k++)
        {
          var main = Regex.Match(lines[k], @"CHANGE_CAR_COLOUR\(" + handle + @",\s*(-?\d+),\s*(-?\d+)\)");
          if (main.Success)
          {
            colours[0] = int.Parse(main.Groups[1].Value, CultureInfo.InvariantCulture);
            colours[1] = int.Parse(main.Groups[2].Value, CultureInfo.InvariantCulture);
          }
          var extra = Regex.Match(lines[k], @"SET_EXTRA_CAR_COLOURS\(" + handle + @",\s*(-?\d+),\s*(-?\d+)\)");
          if (extra.Success)
          {
            colours[2] = int.Parse(extra.Groups[1].Value, CultureInfo.InvariantCulture);
            colours[3] = int.Parse(extra.Groups[2].Value, CultureInfo.InvariantCulture);
          }
        }
        list.Add(new ScriptVehicle(model, colours));
      }
      return list;
    }
  }
}
