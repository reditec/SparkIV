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
    private const int Format = 1;

    /// <summary>A vehicle a script makes: its model's hash and the four colours it gives it (-1: not set).</summary>
    public sealed record ScriptVehicle(uint Model, int[] Colours);

    /// <summary>Per cutscene (upper case): the script that starts it and the vehicles that script makes.</summary>
    public sealed record Entry(string Script, List<ScriptVehicle> Vehicles);

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
          var cutscenes = Regex.Matches(code, @"START_CUTSCENE_NOW\(""([^""]+)""\)").Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
          if (cutscenes.Count == 0)
          {
            continue;
          }
          var vehicles = Vehicles(code);
          string name = Path.GetFileNameWithoutExtension(scripts[i].Name);
          foreach (string cutscene in cutscenes)
          {
            index.Cutscenes.TryAdd(cutscene, new Entry(name, vehicles));
          }
        }
        progress?.Invoke(scripts.Count, scripts.Count);
      }
      finally
      {
        img.Close();
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
