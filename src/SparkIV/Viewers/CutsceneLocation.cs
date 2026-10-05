using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RageLib.FileSystem;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A map object around a cutscene: its model, how it stands in the scene (model to scene), and the interior
  /// room it is in (an index into <see cref="CutsceneLocation.Rooms"/>; -1 for none).
  /// </summary>
  public sealed record LocationObject(int Model, Matrix4x4 Place, int Room = -1);

  /// <summary>
  /// A room of an interior placed around a cutscene: its box in the interior (Min, Max), the interior's place
  /// in the scene (At) and the turn from the scene into the interior (Turn: local = Turn applied to scene - At).
  /// </summary>
  public sealed record LocationRoom(string Name, Vector3 At, Quaternion Turn, Vector3 Min, Vector3 Max);

  /// <summary>
  /// Where a cutscene plays: the map objects around its origin ([OFFSET]; the scene's places are the
  /// world's less it, unturned: b_1's door in the scene stands where [REMOVE]'s real one is in the world),
  /// without those [REMOVE] hides, and the water there.
  /// </summary>
  public sealed class CutsceneLocation
  {
    public List<ModelScene> Models { get; } = new List<ModelScene>();
    public List<string> ModelNames { get; } = new List<string>();
    public List<LocationObject> Objects { get; } = new List<LocationObject>();

    /// <summary>The rooms of the interiors placed there (but their "limbo", the shell around the rooms).</summary>
    public List<LocationRoom> Rooms { get; } = new List<LocationRoom>();

    /// <summary>The water's quads in the scene's places (4 corners each), or empty.</summary>
    public List<Vector3> Water { get; } = new List<Vector3>();

    /// <summary>Placements whose model couldn't be loaded.</summary>
    public int Missing { get; private set; }

    /// <summary>Why placements weren't shown, by model name: "no archive", "archive unreadable", "not in its archive", "unreadable".</summary>
    public Dictionary<string, string> MissingModels { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int Hidden { get; private set; }

    /// <summary>Whether the scene's origin is in none of the interiors' rooms: a scene outside.</summary>
    public bool Outdoors { get; private set; }

    /// <summary>Outside: the far versions (lod…) placed beyond the near objects, up to the scene's draw distance.</summary>
    public int Far { get; private set; }

    /// <summary>A texture's key for the page: "m" and the model's number, then its key in the model's scene.</summary>
    public static string TextureKey(int model, string key) => "m" + model + "/" + key;

    /// <summary>The water's surface texture's key (see <see cref="WaterSurface"/>).</summary>
    public const string WaterKey = "mwater/surface";

    /// <summary>
    /// The water's surface (pc\textures\water.wtd's waterclear, as ModelScene.Raw makes it): its blue and alpha
    /// are the slope of the little waves the game's water shader lays over it, three times in different
    /// sizes; null without it.
    /// </summary>
    public byte[]? WaterSurface { get; private set; }

    /// <summary>The clouds' noise textures' keys (see <see cref="Clouds"/>).</summary>
    public const string CloudKey = "msky/clouds", CloudDetailKey = "msky/detail";

    /// <summary>
    /// Outside: the clouds' noise (pc\textures\skydome.wtd's baseperlinnoise3channel; the game's sky shader
    /// gta_atmoscatt_clouds takes its red for the clouds' shape, its green for the higher ones') and their
    /// fine detail (detailbump2), as ModelScene.Raw makes them; null without them.
    /// </summary>
    public byte[]? Clouds { get; private set; }
    public byte[]? CloudDetail { get; private set; }

    /// <summary>The texture behind a key from <see cref="TextureKey"/>, or null.</summary>
    public byte[]? Texture(string key)
    {
      if (key == WaterKey)
      {
        return WaterSurface;
      }
      if (key == CloudKey || key == CloudDetailKey)
      {
        return key == CloudKey ? Clouds : CloudDetail;
      }
      int slash = key.IndexOf('/');
      if (slash < 2 || key[0] != 'm' || !int.TryParse(key.AsSpan(1, slash - 1), out int model) || model < 0 || model >= Models.Count)
      {
        return null;
      }
      return Models[model].Textures.TryGetValue(key.Substring(slash + 1), out var png) ? png : null;
    }

    // The placements and models of a game's maps, read once per game folder while SparkIV runs.
    private sealed class MapIndex
    {
      /// <summary>
      /// Every placement of the maps, with its far version (Parent: its index here, -1 for none): a model's in
      /// an archive's .wpl is one of the loose .wpl of the archive's name (brook_n.img's in brook_n.wpl), a far
      /// version's one of the same loose file (its district's "slod"). A name not known is empty.
      /// </summary>
      public List<(string Name, Vector3 Position, Quaternion Rotation, int Parent)> Placements { get; } = new List<(string, Vector3, Quaternion, int)>();
      public Dictionary<string, string> ArchiveOf { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      /// <summary>
      /// The interiors (the .ide files' mlo sections: Apart, Brucie ...), their objects and rooms; a placement
      /// of the interior's name places them all.
      /// </summary>
      public Dictionary<string, Interior> Interiors { get; } = new Dictionary<string, Interior>(StringComparer.OrdinalIgnoreCase);

      /// <summary>The objects' (objs) distance they are drawn up to and the radius of their bounds, by name.</summary>
      /// <remarks>Timed objects (tobj) too: <see cref="Hours"/> says when they are there.</remarks>
      public Dictionary<string, (float Draw, float Radius)> Sizes { get; } = new Dictionary<string, (float, float)>(StringComparer.OrdinalIgnoreCase);

      /// <summary>The dictionary (.wdd) an object is in, by name, where the .ide gives one (the far versions: lod_bxe2_01_grp).</summary>
      public Dictionary<string, string> DictionaryOf { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      /// <summary>
      /// The timed objects (tobj: lit windows at night, lod_e_bset07): the hours they are shown, bit h for
      /// the hour from h (0x1F0001F: 20 to 5 o'clock), by name. Objects not in it are always there.
      /// </summary>
      public Dictionary<string, uint> Hours { get; } = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

      /// <summary>The archive each dictionary (.wdd) is in, by its name.</summary>
      public Dictionary<string, string> DictionaryArchiveOf { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An interior: its objects in the order the .ide lists them, placed in the interior as the map places
    /// objects in the world (Lod: a far version, not shown; Room: the index of the room listing it, -1 for
    /// none), and its rooms' boxes in the interior.
    /// </summary>
    private sealed class Interior
    {
      public List<(string Name, Vector3 Position, Quaternion Rotation, bool Lod, int Room)> Objects { get; } =
        new List<(string, Vector3, Quaternion, bool, int)>();
      public List<(string Name, Vector3 Min, Vector3 Max)> Rooms { get; } = new List<(string, Vector3, Vector3)>();
    }

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, MapIndex> s_indexes = new Dictionary<string, MapIndex>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The map objects within <paramref name="radius"/> metres (across the ground) of the scene's origin in
    /// the maps of the game folder (the one holding pc\; each episode has its own city), on a background
    /// thread with the archives' key set. The far-away versions (lod…) are left out near; a scene outside
    /// (its origin in no room) shows them beyond, up to <paramref name="farRadius"/> (the .cut's draw
    /// distance) and as far as each is drawn (its .ide distance): low models with small textures, quick to
    /// load, as the game shows the city in the distance (bc_3: the bridge over the pier). Timed objects only
    /// at their hours: at <paramref name="hour"/> (the scene's time isn't in the .cut; noon for now).
    /// </summary>
    public static CutsceneLocation Load(string folder, float[] offset, IReadOnlyList<CutRemoval> removals, float radius, float farRadius = 0,
                                        int hour = 12, SparkIV.WinUI.Services.LoadLog? log = null)
    {
      log?.Restart();
      var index = IndexOf(folder);
      log?.Step("location: the maps' index", "read once per game while SparkIV runs");
      var origin = new Vector3(offset[0], offset[1], offset[2]);
      var location = new CutsceneLocation();
      // An interior placed: its objects, each placed in it as the interior is in the world (the interior's
      // turn inverted as any placement's in the map; the object's own turn applied first, as it is).
      var world = new List<int>();
      var inside = new List<(string Name, Vector3 Position, Quaternion Rotation, int Room)>();
      for (int i = 0; i < index.Placements.Count; i++)
      {
        var p = index.Placements[i];
        if (p.Name.Length == 0)
        {
          continue;
        }
        if (!index.Interiors.TryGetValue(p.Name, out var interior))
        {
          world.Add(i);
          continue;
        }
        if (Math.Abs(p.Position.X - origin.X) > radius + 60 || Math.Abs(p.Position.Y - origin.Y) > radius + 60)
        {
          continue;
        }
        var turn = Quaternion.Conjugate(p.Rotation);
        // Its rooms, numbered across the location; "limbo" (the walls around the rooms, in none of them) as none.
        var roomOf = new int[interior.Rooms.Count];
        for (int r = 0; r < interior.Rooms.Count; r++)
        {
          var room = interior.Rooms[r];
          if (room.Name.Equals("limbo", StringComparison.OrdinalIgnoreCase))
          {
            roomOf[r] = -1;
            continue;
          }
          roomOf[r] = location.Rooms.Count;
          location.Rooms.Add(new LocationRoom(room.Name, p.Position - origin, p.Rotation, room.Min, room.Max));
        }
        foreach (var o in interior.Objects)
        {
          if (o.Lod)
          {
            continue;
          }
          // An object's turn in an interior is the model's own, not inverted as a placement's in the map
          // (pub3_BERNIProps, turned 128° in Bernie's flat: inverted, its candles, frames and box stood in the
          // middle of the room instead of on the shelves where they are in the game).
          var combined = Quaternion.Concatenate(o.Rotation, turn);
          inside.Add((o.Name, p.Position + Vector3.Transform(o.Position, turn), Quaternion.Conjugate(combined), o.Room >= 0 ? roomOf[o.Room] : -1));
        }
      }
      float Bound(string name) => index.Sizes.TryGetValue(name, out var size) ? size.Radius : 0;
      float Distance(Vector3 position) => new Vector2(position.X - origin.X, position.Y - origin.Y).Length();
      bool AtHour(string name) => !index.Hours.TryGetValue(name, out uint shown) || (shown & (1u << Math.Clamp(hour, 0, 23))) != 0;
      bool IsFar(string name) => name.StartsWith("lod", StringComparison.OrdinalIgnoreCase) || name.StartsWith("slod", StringComparison.OrdinalIgnoreCase);
      // Near: every model that reaches into the radius (by its bounds: a bridge whose middle is far off but
      // which spans the scene, bc_3).
      bool Near(string name, Vector3 position) => Distance(position) - Bound(name) <= radius;
      var near = inside.Where(p => Near(p.Name, p.Position) && AtHour(p.Name)).ToList();

      // Inside: the origin across the ground in a room, up to 5 m below or above it (a scene's origin isn't
      // where it plays: bc_1's is 3.4 m under the floor of Bernie's living room).
      location.Outdoors = !location.Rooms.Any(r =>
      {
        var local = Vector3.Transform(-r.At, r.Turn);
        return local.X >= r.Min.X && local.X <= r.Max.X && local.Y >= r.Min.Y && local.Y <= r.Max.Y && local.Z >= r.Min.Z - 5 && local.Z <= r.Max.Z + 5;
      });
      if (!location.Outdoors || farRadius <= radius)
      {
        foreach (int i in world)
        {
          var p = index.Placements[i];
          if (!IsFar(p.Name) && Math.Abs(p.Position.X - origin.X) <= radius + 300 && Math.Abs(p.Position.Y - origin.Y) <= radius + 300 &&
              Near(p.Name, p.Position) && AtHour(p.Name))
          {
            near.Add((p.Name, p.Position, p.Rotation, -1));
          }
        }
      }
      else
      {
        // Outside, as the game chooses: of a model, its far version (lod…) and that one's (slod…: a district's)
        // one is shown. A placement is wanted where it is drawn: a model near, a far version up to its .ide
        // distance and the scene's. One with a wanted placement under it gives way to all of those under it
        // (each then the same way), else it is shown itself if wanted or if the one above gave way.
        var below = new Dictionary<int, List<int>>();
        foreach (int i in world)
        {
          int parent = index.Placements[i].Parent;
          if (parent >= 0 && parent < index.Placements.Count && parent != i)
          {
            (below.TryGetValue(parent, out var list) ? list : below[parent] = new List<int>()).Add(i);
          }
        }
        bool Wanted(int i)
        {
          var p = index.Placements[i];
          if (!AtHour(p.Name))
          {
            return false;
          }
          if (!IsFar(p.Name))
          {
            // A model near, or as far as the game draws it (its .ide distance), up to OutsideModels: beyond,
            // its coarse far version (sea floor and shore far versions lie above the water near the shore).
            return Near(p.Name, p.Position) ||
                   index.Sizes.TryGetValue(p.Name, out var own) && Distance(p.Position) <= Math.Min(own.Draw, OutsideModels);
          }
          float draw = index.Sizes.TryGetValue(p.Name, out var size) ? size.Draw : farRadius;
          return Distance(p.Position) <= Math.Min(draw, farRadius);
        }
        var detail = new Dictionary<int, bool>();
        bool WantsDetail(int i)
        {
          if (detail.TryGetValue(i, out bool known))
          {
            return known;
          }
          detail[i] = false;  // A loop in the links stops here.
          bool wants = below.TryGetValue(i, out var list) && list.Any(c => Wanted(c) || WantsDetail(c));
          detail[i] = wants;
          return wants;
        }
        var chosen = new List<int>();
        void Choose(int i, bool gaveWay, int depth)
        {
          if (depth < 4 && WantsDetail(i))
          {
            foreach (int c in below[i])
            {
              Choose(c, true, depth + 1);
            }
          }
          else if ((gaveWay || Wanted(i)) && AtHour(index.Placements[i].Name))
          {
            chosen.Add(i);
          }
        }
        foreach (int i in world)
        {
          var p = index.Placements[i];
          int parent = p.Parent;
          bool top = parent < 0 || parent >= index.Placements.Count || parent == i || index.Placements[parent].Name.Length == 0;
          if (top && Distance(p.Position) - Bound(p.Name) <= farRadius)
          {
            Choose(i, false, 0);
          }
        }
        foreach (int i in chosen)
        {
          var p = index.Placements[i];
          near.Add((p.Name, p.Position, p.Rotation, -1));
          if (IsFar(p.Name))
          {
            location.Far++;
          }
        }
      }
      log?.Step("location: choose its objects", (location.Outdoors ? "outside" : "inside") + ", " + near.Count + " placements");
      // Hidden by the scene: the same model within a metre of the place [REMOVE] gives (to a tenth of a metre there).
      var shown = near.Where(p => !removals.Any(r => r.Model.Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
                                                     Vector3.Distance(new Vector3(r.X, r.Y, r.Z), p.Position) < 1f)).ToList();
      location.Hidden = near.Count - shown.Count;

      var modelOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var dictionaryData = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      using var shared = new ModelScene.SharedTextures();
      // A model on its own (.wdr, .wft), else in the dictionary the .ide names (.wdd).
      string? DictionaryFor(string name) =>
        !index.ArchiveOf.ContainsKey(name) && index.DictionaryOf.TryGetValue(name, out var wdd) && index.DictionaryArchiveOf.ContainsKey(wdd) ? wdd : null;
      foreach (var group in shown.GroupBy(p => index.ArchiveOf.TryGetValue(p.Name, out var a) ? a
                                             : DictionaryFor(p.Name) is string wdd ? index.DictionaryArchiveOf[wdd] : ""))
      {
        if (group.Key.Length == 0)
        {
          location.Missing += group.Count();
          foreach (var p in group) location.MissingModels.TryAdd(p.Name, "no archive");
          continue;
        }
        var img = new IMGFileSystem();
        try
        {
          img.Open(group.Key);
        }
        catch (Exception)
        {
          location.Missing += group.Count();
          foreach (var p in group) location.MissingModels.TryAdd(p.Name, "archive unreadable");
          continue;
        }
        try
        {
          foreach (var placement in group)
          {
            if (!modelOf.TryGetValue(placement.Name, out int model))
            {
              model = -1;
              string? dictionary = DictionaryFor(placement.Name);
              var file = dictionary != null ? img.RootDirectory.FindByName(dictionary + ".wdd") as File
                : new[] { ".wdr", ".wft" }.Select(e => img.RootDirectory.FindByName(placement.Name + e) as File).FirstOrDefault(f => f != null);
              if (file != null)
              {
                try
                {
                  // While the archive is open: the textures are looked for beside the model and through the .ide files.
                  // A dictionary is read once for all the far versions in it.
                  byte[] bytes = dictionary == null ? file.GetData()
                    : dictionaryData.TryGetValue(dictionary, out var known) ? known : dictionaryData[dictionary] = file.GetData();
                  location.Models.Add(ModelScene.Load(file, bytes, vertexColors: true,
                                                      onlyModel: dictionary != null ? RageLib.Text.GxtFile.Hash(placement.Name) : null, shared: shared, rawTextures: true));
                  location.ModelNames.Add(placement.Name);
                  model = location.Models.Count - 1;
                }
                catch (Exception ex)
                {
                  model = -1;
                  location.MissingModels.TryAdd(placement.Name, "unreadable: " + ex.Message);
                }
              }
              else
              {
                location.MissingModels.TryAdd(placement.Name, "not in its archive" + (dictionary != null ? " (" + dictionary + ".wdd)" : ""));
              }
              modelOf[placement.Name] = model;
            }
            if (model < 0)
            {
              location.Missing++;
              continue;
            }
            // The placement's turn is the inverse of the model's (as the game reads it), then its place,
            // less the scene's origin.
            var place = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(placement.Rotation)) * Matrix4x4.CreateTranslation(placement.Position - origin);
            location.Objects.Add(new LocationObject(model, place, placement.Room));
          }
        }
        finally
        {
          img.Close();
        }
      }
      if (log != null)
      {
        log.Step("location: models", location.Models.Count + " models, " + location.Objects.Count + " objects (with the textures below)");
        log.Add("  of which: textures", shared.TextureSeconds, shared.TexturesMade + " made");
        log.Add("  of which: texture dictionaries", shared.DictionarySeconds, shared.Opened.Count + " opened");
      }
      location.AddWater(folder, origin, location.Far > 0 ? farRadius : radius);
      if (location.Outdoors)
      {
        try
        {
          string sky = Path.Combine(folder, "pc", "textures", "skydome.wtd");
          if (System.IO.File.Exists(sky))
          {
            using var textures = new RageLib.Textures.TextureFile();
            textures.Open(sky);
            RageLib.Textures.Texture? Named(string part) => textures.Textures.FirstOrDefault(x => x.Name.Contains(part, StringComparison.OrdinalIgnoreCase));
            location.Clouds = Named("baseperlinnoise") is RageLib.Textures.Texture noise ? ModelScene.Raw(noise) : null;
            location.CloudDetail = Named("detailbump2") is RageLib.Textures.Texture detail ? ModelScene.Raw(detail) : null;
          }
        }
        catch (Exception)
        {
          // Then the sky has no clouds.
        }
      }
      if (location.Water.Count > 0)
      {
        try
        {
          string surface = Path.Combine(folder, "pc", "textures", "water.wtd");
          if (System.IO.File.Exists(surface))
          {
            using var textures = new RageLib.Textures.TextureFile();
            textures.Open(surface);
            if (textures.Textures.FirstOrDefault(x => x.Name.Contains("waterclear", StringComparison.OrdinalIgnoreCase)) is RageLib.Textures.Texture clear)
            {
              location.WaterSurface = ModelScene.Raw(clear);
            }
          }
        }
        catch (Exception)
        {
          // Then the water is smooth.
        }
      }
      log?.Step("location: water and sky", location.Water.Count / 4 + " water quads");
      return location;
    }

    /// <summary>The water quads of the game's common\data\water.dat that reach the area, cut to it.</summary>
    private void AddWater(string folder, Vector3 origin, float radius)
    {
      string path = Path.Combine(folder, "common", "data", "water.dat");
      if (!System.IO.File.Exists(path))
      {
        return;
      }
      foreach (string line in System.IO.File.ReadLines(path))
      {
        // Four corners of 7 numbers (x y z and four not needed here), then two more.
        var v = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : float.NaN).ToArray();
        if (v.Length < 28 || v.Take(28).Any(float.IsNaN))
        {
          continue;
        }
        var corners = Enumerable.Range(0, 4).Select(k => new Vector3(v[k * 7], v[k * 7 + 1], v[k * 7 + 2])).ToArray();
        float x0 = corners.Min(c => c.X), x1 = corners.Max(c => c.X), y0 = corners.Min(c => c.Y), y1 = corners.Max(c => c.Y);
        if (x0 == x1 || y0 == y1 || x1 < origin.X - radius || x0 > origin.X + radius || y1 < origin.Y - radius || y0 > origin.Y + radius)
        {
          continue;
        }
        foreach (var c in corners)
        {
          Water.Add(new Vector3(Math.Clamp(c.X, origin.X - radius, origin.X + radius), Math.Clamp(c.Y, origin.Y - radius, origin.Y + radius), c.Z) - origin);
        }
      }
    }

    // Outside: how far models (not their far versions) are shown at most, in metres.
    private const float OutsideModels = 150;

    private static MapIndex IndexOf(string folder)
    {
      lock (Gate)
      {
        if (s_indexes.TryGetValue(folder, out var known))
        {
          return known;
        }
        var index = new MapIndex();
        // The models' names by the hash a placement gives (from the .ide files).
        var nameOf = new Dictionary<uint, string>();
        string data = Path.Combine(folder, "pc", "data");
        if (System.IO.Directory.Exists(data))
        {
          foreach (var ide in System.IO.Directory.EnumerateFiles(data, "*.ide", SearchOption.AllDirectories))
          {
            ReadIde(ide, nameOf, index.Interiors, index.Sizes, index.DictionaryOf, index.Hours);
          }
        }
        string maps = Path.Combine(folder, "pc", "data", "maps");
        var looseStart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(maps))
        {
          // The far versions' placements are in loose files beside the archives (east\bronx_e.wpl ...), first:
          // the archives' placements point into them.
          foreach (var path in System.IO.Directory.EnumerateFiles(maps, "*.wpl", SearchOption.AllDirectories))
          {
            try
            {
              looseStart[Path.GetFileNameWithoutExtension(path)] = index.Placements.Count;
              ReadPlacements(System.IO.File.ReadAllBytes(path), nameOf, index.Placements, index.Placements.Count);
            }
            catch (Exception)
            {
              // A file that can't be read: its objects aren't shown.
            }
          }
          foreach (var path in System.IO.Directory.EnumerateFiles(maps, "*.img", SearchOption.AllDirectories))
          {
            var img = new IMGFileSystem();
            try
            {
              img.Open(path);
              foreach (var file in img.RootDirectory.OfType<File>())
              {
                string extension = Path.GetExtension(file.Name);
                if (extension.Equals(".wdr", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wft", StringComparison.OrdinalIgnoreCase))
                {
                  index.ArchiveOf.TryAdd(Path.GetFileNameWithoutExtension(file.Name), path);
                }
                else if (extension.Equals(".wdd", StringComparison.OrdinalIgnoreCase))
                {
                  index.DictionaryArchiveOf.TryAdd(Path.GetFileNameWithoutExtension(file.Name), path);
                }
                else if (extension.Equals(".wpl", StringComparison.OrdinalIgnoreCase))
                {
                  ReadPlacements(file.GetData(), nameOf, index.Placements,
                             looseStart.TryGetValue(Path.GetFileNameWithoutExtension(path), out int parents) ? parents : -1);
                }
              }
            }
            catch (Exception)
            {
              // An archive that can't be read: its objects aren't shown.
            }
            finally
            {
              try
              {
                img.Close();
              }
              catch (Exception)
              {
              }
            }
          }
        }
        s_indexes[folder] = index;
        return index;
      }
    }

    /// <summary>
    /// An .ide file: every name (models and interiors, by the hash a placement gives), and its interiors.
    /// An mlo section holds interiors one after the other: a line naming it, its objects (indented: model,
    /// place x y z, turn x y z w, a level of detail: 0 the model itself, 1 and 2 its far versions, flags),
    /// then its rooms and portals from "mloroomstart" to "mloend". A room: a line naming it (name, how many
    /// objects, a number, one corner of its box x y z, the other, more), the numbers of its objects in the
    /// interior's list (-1 filling up the lines), "roomend"; the portals from "mloportalstart".
    /// </summary>
    private static void ReadIde(string path, Dictionary<uint, string> nameOf, Dictionary<string, Interior> interiors,
                                Dictionary<string, (float Draw, float Radius)> sizes, Dictionary<string, string> dictionaryOf,
                                Dictionary<string, uint> hours)
    {
      static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
      string? section = null;
      Interior? interior = null;
      bool inRooms = false, inPortals = false;
      foreach (var raw in System.IO.File.ReadLines(path))
      {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
        {
          continue;
        }
        if (!line.Contains(','))
        {
          // A section's start or end ("objs", "mlo", "end"), or within an interior where its rooms and
          // portals begin and end.
          if (section == "mlo" && line == "mloroomstart")
          {
            inRooms = true;
          }
          else if (section == "mlo" && line == "mloportalstart")
          {
            inPortals = true;
          }
          else if (section == "mlo" && line == "mloend")
          {
            inRooms = inPortals = false;
            interior = null;
          }
          else if (line == "end")
          {
            section = null;
            interior = null;
            inRooms = inPortals = false;
          }
          else if (section == null)
          {
            section = line.ToLowerInvariant();
          }
          continue;
        }
        var fields = line.Split(',').Select(f => f.Trim()).ToArray();
        if (section != "mlo" || !inRooms)
        {
          if (fields[0].Length > 0)
          {
            nameOf.TryAdd(RageLib.Text.GxtFile.Hash(fields[0]), fields[0]);
          }
        }
        // An object: name, texture dictionary, the distance it is drawn up to, flags, a number, its bounds
        // (box min, max, sphere centre, radius), the dictionary it is in; a timed one (tobj) also the hours
        // it is shown.
        if (section is "objs" or "tobj" && fields.Length >= 15 &&
            float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float draw) &&
            float.TryParse(fields[14], NumberStyles.Float, CultureInfo.InvariantCulture, out float bound))
        {
          sizes.TryAdd(fields[0], (draw, bound));
          if (fields.Length >= 16 && fields[15].Length > 0 && !fields[15].Equals("null", StringComparison.OrdinalIgnoreCase))
          {
            dictionaryOf.TryAdd(fields[0], fields[15]);
          }
          if (section == "tobj" && fields.Length >= 17 && uint.TryParse(fields[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint shownHours))
          {
            hours.TryAdd(fields[0], shownHours);
          }
        }
        if (section != "mlo" || inPortals)
        {
          continue;
        }
        if (inRooms)
        {
          if (interior == null)
          {
            continue;
          }
          if (fields.Length >= 9 && !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
          {
            // A room's own line: its name and box.
            try
            {
              var a = new Vector3(F(fields[3]), F(fields[4]), F(fields[5]));
              var b = new Vector3(F(fields[6]), F(fields[7]), F(fields[8]));
              interior.Rooms.Add((fields[0], Vector3.Min(a, b), Vector3.Max(a, b)));
            }
            catch (FormatException)
            {
              interior.Rooms.Add((fields[0], Vector3.Zero, Vector3.Zero));
            }
          }
          else if (interior.Rooms.Count > 0)
          {
            // The numbers of the room's objects.
            int room = interior.Rooms.Count - 1;
            foreach (var f in fields)
            {
              if (int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < interior.Objects.Count)
              {
                var o = interior.Objects[i];
                interior.Objects[i] = (o.Name, o.Position, o.Rotation, o.Lod, room);
              }
            }
          }
          continue;
        }
        bool indented = raw.Length > 0 && char.IsWhiteSpace(raw[0]);
        if (!indented)
        {
          // The interior's own line: its name first.
          interior = new Interior();
          interiors[fields[0]] = interior;
        }
        else if (interior != null && fields.Length >= 9)
        {
          try
          {
            // The far versions (level of detail 1, 2) are kept in the list (the rooms count them) but not shown.
            interior.Objects.Add((fields[0], new Vector3(F(fields[1]), F(fields[2]), F(fields[3])), new Quaternion(F(fields[4]), F(fields[5]), F(fields[6]), F(fields[7])),
                                  fields[8] != "0", -1));
          }
          catch (FormatException)
          {
            // A line of another form: left out.
          }
        }
      }
    }

    /// <summary>
    /// The placements of a .wpl (version 3): after 68 bytes of head, 48 a placement: place (3 floats),
    /// turn (quaternion x y z w), the model's hash at 28, its far version at 36 (the index in the loose file
    /// whose placements start at <paramref name="parents"/>; -1 for none). All are kept, those of names not
    /// known empty, so the indexes stay right.
    /// </summary>
    private static void ReadPlacements(byte[] d, Dictionary<uint, string> nameOf, List<(string, Vector3, Quaternion, int)> placements, int parents)
    {
      if (d.Length < 68 || BitConverter.ToUInt32(d, 0) != 3)
      {
        return;
      }
      int count = (int)BitConverter.ToUInt32(d, 4);
      for (int i = 0; i < count && 68 + i * 48 + 40 <= d.Length; i++)
      {
        int o = 68 + i * 48;
        string name = nameOf.TryGetValue(BitConverter.ToUInt32(d, o + 28), out var known) ? known : "";
        int lod = BitConverter.ToInt32(d, o + 36);
        placements.Add((name,
                        new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8)),
                        new Quaternion(BitConverter.ToSingle(d, o + 12), BitConverter.ToSingle(d, o + 16), BitConverter.ToSingle(d, o + 20), BitConverter.ToSingle(d, o + 24)),
                        lod >= 0 && parents >= 0 ? parents + lod : -1));
      }
    }

    /// <summary>
    /// The "scene" message adding the location to the page's (after the cast's ids): every object's meshes
    /// moved into the scene, in the background (not fitted to), its models' materials once; the water a
    /// see-through blue. Returns the meshes shown.
    /// </summary>
    public (JsonObject Scene, List<int> Visible) Message(Func<string, string> url, int firstMesh, int firstMaterial)
    {
      var materials = new JsonArray();
      var meshes = new JsonArray();
      var visible = new List<int>();
      var materialStart = new int[Models.Count];
      int nextMaterial = firstMaterial;
      for (int m = 0; m < Models.Count; m++)
      {
        materialStart[m] = nextMaterial;
        foreach (var material in Models[m].Materials)
        {
          int model = m;
          var node = JsonSerializer.SerializeToNode(material.ToMessage(key => url(TextureKey(model, key))))!.AsObject();
          node["id"] = materialStart[m] + material.Id;
          materials.Add(node);
          nextMaterial = Math.Max(nextMaterial, materialStart[m] + material.Id + 1);
        }
      }
      int nextMesh = firstMesh;
      foreach (var obj in Objects)
      {
        var scene = Models[obj.Model];
        var shown = scene.Root.CheckedMeshes().ToHashSet();
        foreach (var mesh in scene.Meshes)
        {
          if (!shown.Contains(mesh.Id))
          {
            continue;
          }
          var node = JsonSerializer.SerializeToNode(mesh.ToMessage())!.AsObject();
          int id = nextMesh++;
          node["id"] = id;
          node["material"] = materialStart[obj.Model] + mesh.Material;
          node["background"] = true;
          node["room"] = obj.Room;
          // Moved and turned into the scene; it doesn't move with bones there.
          node["positions"] = Floats(Transform(mesh.Positions, obj.Place, true));
          if (mesh.Normals != null)
          {
            node["normals"] = Floats(Transform(mesh.Normals, obj.Place, false));
          }
          node.Remove("skinBones");
          node.Remove("skinWeights");
          meshes.Add(node);
          visible.Add(id);
        }
      }
      if (Water.Count >= 4)
      {
        int water = nextMaterial;
        materials.Add(new JsonObject
        {
          ["id"] = water, ["glass"] = true, ["water"] = true, ["cutout"] = false, ["blend"] = false, ["decal"] = false, ["hidden"] = false,
          ["texture"] = null, ["color"] = new JsonArray(0.18f, 0.38f, 0.42f),
          ["normal"] = WaterSurface != null ? url(WaterKey) : null,
        });
        var positions = Water.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        var indices = new List<ushort>();
        for (int q = 0; q + 3 < Water.Count && q + 3 < ushort.MaxValue; q += 4)
        {
          // Corners in the file's order: 0, 1 one edge, 2, 3 the other.
          indices.AddRange(new[] { (ushort)q, (ushort)(q + 1), (ushort)(q + 2), (ushort)(q + 1), (ushort)(q + 3), (ushort)(q + 2) });
        }
        int id = nextMesh++;
        meshes.Add(new JsonObject
        {
          ["id"] = id, ["material"] = water, ["background"] = true,
          ["positions"] = Floats(positions),
          ["normals"] = Floats(Water.SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray()),
          ["indices"] = Convert.ToBase64String(MemoryMarshal.AsBytes(indices.ToArray().AsSpan())),
        });
        visible.Add(id);
      }
      return (new JsonObject { ["type"] = "scene", ["materials"] = materials, ["meshes"] = meshes }, visible);
    }

    /// <summary>
    /// The "mapLights" message: the lights of the objects' models (lamps, wall lights; the light the game
    /// puts there), moved into the scene: place, direction, colour, range, a spot's angle in degrees from its axis
    /// (0 for a light all round). With them the interiors' rooms (by their numbers in the objects' room):
    /// the interior's place, the turn from the scene into it, the box there.
    /// </summary>
    public object LightsMessage()
    {
      var lights = new List<object>();
      foreach (var obj in Objects)
      {
        foreach (var light in Models[obj.Model].Lights)
        {
          var at = Vector3.Transform(light.Position, obj.Place);
          var way = Vector3.TransformNormal(light.Direction, obj.Place);
          way = way.LengthSquared() > 1e-8f ? Vector3.Normalize(way) : new Vector3(0, 0, -1);
          lights.Add(new
          {
            position = new[] { at.X, at.Y, at.Z },
            direction = new[] { way.X, way.Y, way.Z },
            color = new[] { light.Red, light.Green, light.Blue },
            // How far it lights (Distance); its Range is how far it is seen.
            range = light.Distance > 0 ? light.Distance : light.Range,
            cone = light.Spot ? light.Falloff : 0f,
            hotSpot = light.Spot ? light.HotSpot : 0f,
            intensity = light.Intensity,
          });
        }
      }
      var rooms = Rooms.Select(r => new
      {
        name = r.Name,
        at = new[] { r.At.X, r.At.Y, r.At.Z },
        turn = new[] { r.Turn.X, r.Turn.Y, r.Turn.Z, r.Turn.W },
        min = new[] { r.Min.X, r.Min.Y, r.Min.Z },
        max = new[] { r.Max.X, r.Max.Y, r.Max.Z },
      }).ToList();
      return new { type = "mapLights", lights, rooms };
    }

    private static string Floats(float[] values) => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));

    private static float[] Transform(float[] values, Matrix4x4 place, bool isPoint)
    {
      var result = new float[values.Length];
      for (int i = 0; i + 2 < values.Length; i += 3)
      {
        var v = new Vector3(values[i], values[i + 1], values[i + 2]);
        var t = isPoint ? Vector3.Transform(v, place) : Vector3.Normalize(Vector3.TransformNormal(v, place));
        result[i] = t.X;
        result[i + 1] = t.Y;
        result[i + 2] = t.Z;
      }
      return result;
    }
  }
}
