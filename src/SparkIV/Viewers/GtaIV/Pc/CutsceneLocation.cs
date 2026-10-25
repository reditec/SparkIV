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
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RageLib.FileSystem;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A map object around a cutscene: its model, how it stands in the scene (model to scene), and the interior
  /// room it is in (an index into <see cref="CutsceneLocation.Rooms"/>; -1 for none).
  /// </summary>
  public sealed record LocationObject(int Model, Matrix4x4 Place, int Room = -1)
  {
    /// <summary>Floating litter: where it floats (its centre on the water, in the scene), for the page to lift it with the sea; null otherwise.</summary>
    public Vector3? FloatAt { get; init; }

    /// <summary>A far version (lod…, slod…): what the game draws into its map of the mirrored surroundings.</summary>
    public bool Far { get; init; }

    /// <summary>Only for that map: the far version of a model shown near (not shown itself, nor casting a shadow).</summary>
    public bool MirrorOnly { get; init; }

    /// <summary>Its placement's instance flags have 0x100: its lamps with their own flag 0x8000 cast shadows (see MapIndex.LampShadows).</summary>
    public bool LampShadows { get; init; }
  }

  /// <summary>
  /// A room of an interior placed around a cutscene: its box in the interior (Min, Max), the interior's place
  /// in the scene (At) and the turn from the scene into the interior (Turn: local = Turn applied to scene - At);
  /// Modifier: the hash of its time cycle modifier's name (0: none).
  /// </summary>
  public sealed record LocationRoom(string Name, Vector3 At, Quaternion Turn, Vector3 Min, Vector3 Max, uint Modifier = 0)
  {
    /// <summary>
    /// Its portals as GTAIV.exe eases a ped's light from around over them (0xA0D720): the sphere through two
    /// opposite corners (0xB20EC0: centre half way, radius half the diagonal; in the interior, as Min and Max),
    /// and the room on the other side (the index in the location's list; -1 outside or none, limbo).
    /// </summary>
    public IReadOnlyList<(Vector3 Centre, float Radius, int To)> Portals { get; init; } = Array.Empty<(Vector3, float, int)>();
  }

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

    /// <summary>The floating litter placed, by model and count (for the load log).</summary>
    public string LitterModels { get; private set; } = "";

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
    /// Outside: the clouds' noise (pc\textures\skydome.wtd's baseperlinnoise3channel, which rage_perlinnoise turns
    /// into the game's PerlinNoiseSampler every frame) and their fine detail (noise16_p, its HighDetailNoiseSampler:
    /// the TLAD trace GTAIV.4 binds a DXT1 noise there, two levels smaller at its texture quality, and noise16_p
    /// is the file's only one), as ModelScene.Raw makes them; null without them.
    /// </summary>
    public byte[]? Clouds { get; private set; }
    public byte[]? CloudDetail { get; private set; }

    /// <summary>The light volumes' lookup texture's key (see <see cref="LightVolume"/>).</summary>
    public const string LightVolumeKey = "mlight/volume";

    /// <summary>
    /// The lookup texture the light volumes of lamps without flag 0x100000 read (deferred_lighting's ps18/ps19,
    /// gDeferredLightSampler2): "volume" of pc\textures\lights_occluders.wtd, as GTAIV.exe takes it (0xAD1A90 from the
    /// dictionary 0xABF130 loads, platform:/textures/lights_occluders; 32 x 512 L8 in the bc_1 trace GTAIV.2, call
    /// 1539806), as ModelScene.Raw makes it; null without it.
    /// </summary>
    public byte[]? LightVolume { get; private set; }

    /// <summary>
    /// The models' textures in packs of about <see cref="PackBytes"/> each (keys "mpack/0" ...), as the scene
    /// message lists them: the page asks for a few dozen instead of two thousand, one by one (bc_3: 10 s
    /// waiting on the requests alone).
    /// </summary>
    private const string PackKey = "mpack/";
    private const int PackBytes = 16 << 20;
    private readonly List<List<string>> _packs = new List<List<string>>();

    /// <summary>A pack: "SXP1", then each texture's length (uint32) and bytes, in the list's order.</summary>
    private byte[] Pack(List<string> keys)
    {
      using var stream = new MemoryStream();
      using var writer = new BinaryWriter(stream);
      writer.Write("SXP1"u8);
      foreach (string key in keys)
      {
        byte[] texture = Texture(key) ?? Array.Empty<byte>();
        writer.Write((uint)texture.Length);
        writer.Write(texture);
      }
      writer.Flush();
      return stream.ToArray();
    }

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
      if (key == LightVolumeKey)
      {
        return LightVolume;
      }
      if (key.StartsWith(PackKey, StringComparison.Ordinal))
      {
        return int.TryParse(key.AsSpan(PackKey.Length), out int pack) && pack >= 0 && pack < _packs.Count ? Pack(_packs[pack]) : null;
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
      /// <summary>
      /// The placements whose instance flags have 0x100 (the .wpl entry's +32): GTAIV.exe lets their lamps cast
      /// shadows (0x9D76C0: entity +0x28 |= 8; 0xC1DB60 → 0xC1E200 passes it, with the lamp's own flag 0x8000).
      /// </summary>
      public HashSet<int> LampShadows { get; } = new HashSet<int>();
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
      public List<(string Name, Vector3 Min, Vector3 Max, uint Modifier)> Rooms { get; } = new List<(string, Vector3, Vector3, uint)>();
      /// <summary>Its objects (their numbers in Objects) whose instance flags have 0x100 (see MapIndex.LampShadows).</summary>
      public HashSet<int> LampShadows { get; } = new HashSet<int>();
      /// <summary>Its portals: the rooms on either side (their numbers in Rooms, 0 limbo), the sphere through two opposite corners.</summary>
      public List<(int From, int To, Vector3 Centre, float Radius)> Portals { get; } = new List<(int, int, Vector3, float)>();
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
      // An interior placed: its objects, each placed in it as the interior is in the world (the object's own
      // turn applied first, then the interior's, both as they are: an interior's placement isn't inverted as
      // a model's in the map. et01_aa (TLAD) plays in E1_Elizabeta's lounge, turned 245°: inverted, the cast
      // stood 12 m beside the flat in the air, as it is, all five in the lounge and csElizabetaDoor at its end
      // wall, as in the game's video. Bernie's flat, turned 180°, is the same either way).
      var world = new List<int>();
      var inside = new List<(string Name, Vector3 Position, Quaternion Rotation, int Room)>();
      // The objects (by name and place) whose lamps may cast shadows (instance flag 0x100, see MapIndex.LampShadows).
      var shadowPlaces = new HashSet<(string, Vector3)>(index.LampShadows.Select(i => (index.Placements[i].Name, index.Placements[i].Position)));
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
        var turn = p.Rotation;
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
          // (The room's turn from the scene into the interior: the other way.)
          location.Rooms.Add(new LocationRoom(room.Name, p.Position - origin, Quaternion.Conjugate(turn), room.Min, room.Max, room.Modifier));
        }
        for (int r = 0; r < interior.Rooms.Count; r++)
        {
          if (roomOf[r] < 0)
          {
            continue;
          }
          var portals = interior.Portals.Where(q => q.From == r || q.To == r)
                                        .Select(q => (q.Centre, q.Radius, To: (q.From == r ? q.To : q.From) is int other && other > 0 && other < roomOf.Length ? roomOf[other] : -1))
                                        .ToList();
          location.Rooms[roomOf[r]] = location.Rooms[roomOf[r]] with { Portals = portals };
        }
        for (int k = 0; k < interior.Objects.Count; k++)
        {
          var o = interior.Objects[k];
          if (o.Lod)
          {
            continue;
          }
          // An object's turn in an interior is the model's own, not inverted as a placement's in the map
          // (pub3_BERNIProps, turned 128° in Bernie's flat: inverted, its candles, frames and box stood in the
          // middle of the room instead of on the shelves where they are in the game).
          var combined = Quaternion.Concatenate(o.Rotation, turn);
          var at = p.Position + Vector3.Transform(o.Position, turn);
          inside.Add((o.Name, at, Quaternion.Conjugate(combined), o.Room >= 0 ? roomOf[o.Room] : -1));
          if (interior.LampShadows.Contains(k))
          {
            shadowPlaces.Add((o.Name, at));
          }
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
      // Far versions only for the map of the mirrored surroundings (see below), by index and by name and place.
      var mirrorOnly = new HashSet<int>();
      var mirrorPlaces = new HashSet<(string, Vector3)>();

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
          // A far version: named so (lod…, slod…) or the far version of others (SupLOD_Ks3_02, og_BS3blk02a_lod:
          // bc_3's buildings across the water, 500 m off, were missing as models beyond OutsideModels).
          if (!IsFar(p.Name) && !below.ContainsKey(i))
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
        // The game's map of the mirrored surroundings (paint, glass, the cast's shine) is drawn from the far
        // versions, not from the models near: in the TLAD trace GTAIV.4 (et01_ba, frame 9350) 100 of its 102 vertex
        // buffers aren't in the main pass, the 2 others far versions shown there too. So of each model shown near
        // its far version as well, only for that map.
        var chosenSet = chosen.ToHashSet();
        foreach (int i in chosen)
        {
          var p = index.Placements[i];
          int parent = p.Parent;
          if (!IsFar(p.Name) && parent >= 0 && parent < index.Placements.Count && parent != i && !chosenSet.Contains(parent) &&
              IsFar(index.Placements[parent].Name) && AtHour(index.Placements[parent].Name) && mirrorOnly.Add(parent))
          {
            var lod = index.Placements[parent];
            near.Add((lod.Name, lod.Position, lod.Rotation, -1));
            mirrorPlaces.Add((lod.Name, lod.Position));
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
                                                      onlyModel: dictionary != null ? RageLib.GtaIV.Pc.Text.GxtFile.Hash(placement.Name) : null, shared: shared, rawTextures: true));
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
            location.Objects.Add(new LocationObject(model, place, placement.Room)
            {
              Far = IsFar(placement.Name),
              MirrorOnly = mirrorPlaces.Contains((placement.Name, placement.Position)),
              LampShadows = shadowPlaces.Contains((placement.Name, placement.Position)),
            });
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
        log.Add("  of which: model dictionaries", shared.ModelDictionarySeconds, shared.ModelDictionariesRead + " read (each once)");
      }
      location.AddWater(folder, origin, location.Far > 0 ? farRadius : radius);
      if (location.Outdoors && location.Water.Count > 0)
      {
        // The collision lies in the archives of the models shown (bronx_e.img's bronx_e_*.wbn), near and far: the
        // litter's reach (below) goes beyond the near models' (each bound is checked against it).
        var archives = shown.Where(p => index.ArchiveOf.ContainsKey(p.Name))
                            .Select(p => index.ArchiveOf[p.Name]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // As far round the scene as the page shows litter from a camera at its edge (the page's FloatingRadius, 75 m).
        int litter = location.AddFloatingLitter(folder, origin, radius + 75, archives, index.ArchiveOf, shared);
        log?.Step("location: floating litter", litter + " objects on " + archives.Count + " archives' collision (" + location.LitterModels + ")");
      }
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
            location.CloudDetail = Named("noise16_p") is RageLib.Textures.Texture detail ? ModelScene.Raw(detail) : null;
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
      try
      {
        string occluders = Path.Combine(folder, "pc", "textures", "lights_occluders.wtd");
        if (System.IO.File.Exists(occluders))
        {
          using var textures = new RageLib.Textures.TextureFile();
          textures.Open(occluders);
          // Named "pack:/volume.dds" in the file; the game asks for "volume".
          if (textures.Textures.FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x.Name.Substring(x.Name.LastIndexOf('/') + 1)), "volume", StringComparison.OrdinalIgnoreCase)) is RageLib.Textures.Texture volume)
          {
            location.LightVolume = ModelScene.Raw(volume);
          }
        }
      }
      catch (Exception)
      {
        // Then no light volumes of that kind.
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

    /// <summary>
    /// Floating litter, as the game grows procedural objects (procedural.dat) on its collision: on the faces
    /// within <paramref name="radius"/> of the origin whose material carries a tag whose objects float (bc_3's
    /// harbour: PROC_INDUS_FLOATING - wood, a tyre), one per spacing² m² of face, none nearer than its minimum
    /// distance to another of its kind, turned at random, on the water's surface above the face (the face must
    /// be under water). The game places them anew each time (USESEED 0); here the same each time. The page
    /// shows them within 75 m of the camera, as the game makes them at the reference videos' detail distance
    /// (see FloatingRadius in gtaiv-pc.js). Returns how many were placed.
    /// </summary>
    private int AddFloatingLitter(string folder, Vector3 origin, float radius, List<string> archives,
                                  Dictionary<string, string> archiveOf, ModelScene.SharedTextures shared)
    {
      CollisionNames.Load(folder);
      var floating = CollisionNames.Objects.Where(o => o.Floats && o.Spacing > 0).GroupBy(o => o.Tag).ToDictionary(g => g.Key, g => g.ToList());
      if (floating.Count == 0)
      {
        return 0;
      }
      // The faces whose objects float, in the scene's places.
      var faces = new List<(Vector3 A, Vector3 B, Vector3 C, int Tag)>();
      foreach (string archive in archives)
      {
        var img = new IMGFileSystem();
        try
        {
          img.Open(archive);
          foreach (var file in img.RootDirectory.OfType<File>().Where(f => f.Name.EndsWith(".wbn", StringComparison.OrdinalIgnoreCase)))
          {
            var bounds = new RageLib.GtaIV.Pc.Bounds.BoundFile();
            try
            {
              bounds.Open(new MemoryStream(file.GetData()));
            }
            catch (Exception)
            {
              continue;
            }
            void Collect(RageLib.GtaIV.Pc.Bounds.Bound bound, Matrix4x4 place)
            {
              if (bound.AabbMax.X < origin.X - radius || bound.AabbMin.X > origin.X + radius ||
                  bound.AabbMax.Y < origin.Y - radius || bound.AabbMin.Y > origin.Y + radius)
              {
                return;
              }
              for (int i = 0; i < bound.Children.Count; i++)
              {
                if (bound.Children[i] is RageLib.GtaIV.Pc.Bounds.Bound child)
                {
                  Collect(child, bound.ChildMatrices[i] * place);
                }
              }
              foreach (var polygon in bound.Polygons)
              {
                int tag = polygon.Material < bound.Materials.Length ? bound.Materials[polygon.Material].ProceduralTag : 0;
                if (!floating.ContainsKey(tag))
                {
                  continue;
                }
                Vector3 V(int i) => Vector3.Transform(bound.Vertices[i], place) - origin;
                faces.Add((V(polygon.A), V(polygon.B), V(polygon.C), tag));
                if (polygon.D >= 0)
                {
                  faces.Add((V(polygon.A), V(polygon.C), V(polygon.D), tag));
                }
              }
            }
            foreach (var (_, bound) in bounds.Bounds)
            {
              Collect(bound, Matrix4x4.Identity);
            }
          }
        }
        catch (Exception)
        {
          // Then no litter from this archive.
        }
        finally
        {
          img.Close();
        }
      }
      if (faces.Count == 0)
      {
        return 0;
      }

      // The water's height above a point: the quad it lies in (flat; its corners' mean), or none.
      float? WaterAt(Vector2 at)
      {
        for (int q = 0; q + 3 < Water.Count; q += 4)
        {
          float x0 = Math.Min(Math.Min(Water[q].X, Water[q + 1].X), Math.Min(Water[q + 2].X, Water[q + 3].X));
          float x1 = Math.Max(Math.Max(Water[q].X, Water[q + 1].X), Math.Max(Water[q + 2].X, Water[q + 3].X));
          float y0 = Math.Min(Math.Min(Water[q].Y, Water[q + 1].Y), Math.Min(Water[q + 2].Y, Water[q + 3].Y));
          float y1 = Math.Max(Math.Max(Water[q].Y, Water[q + 1].Y), Math.Max(Water[q + 2].Y, Water[q + 3].Y));
          if (at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1)
          {
            return (Water[q].Z + Water[q + 1].Z + Water[q + 2].Z + Water[q + 3].Z) / 4;
          }
        }
        return null;
      }

      // The same each time: seeded by the place.
      var random = new Random(unchecked((int)(origin.X * 31 + origin.Y * 17)));
      var placed = new Dictionary<ProceduralObject, List<Vector3>>();
      var models = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      int count = 0;
      foreach (var (a, b, c, tag) in faces)
      {
        float area = Vector3.Cross(b - a, c - a).Length() / 2;
        foreach (var entry in floating[tag])
        {
          // One per spacing² m² (spacing at least 2 m, as the game reads it): the whole number, and one more by chance.
          float spacing = Math.Max(entry.Spacing, 2f);
          float expected = area / (spacing * spacing);
          int n = (int)expected + (random.NextDouble() < expected - (int)expected ? 1 : 0);
          float minDistance = Math.Min(entry.MinDistance, 80f);
          for (int k = 0; k < n; k++)
          {
            // A point on the face, evenly.
            float u = (float)random.NextDouble(), v = (float)random.NextDouble();
            if (u + v > 1)
            {
              u = 1 - u;
              v = 1 - v;
            }
            var at = a + (b - a) * u + (c - a) * v;
            if (WaterAt(new Vector2(at.X, at.Y)) is not float level || at.Z >= level)
            {
              continue;
            }
            var list = placed.TryGetValue(entry, out var known) ? known : placed[entry] = new List<Vector3>();
            var onWater = new Vector3(at.X, at.Y, level + entry.ZOffsetMin + (float)random.NextDouble() * (entry.ZOffsetMax - entry.ZOffsetMin));
            if (list.Any(p => Vector2.DistanceSquared(new Vector2(p.X, p.Y), new Vector2(onWater.X, onWater.Y)) < minDistance * minDistance))
            {
              continue;
            }
            int model = ModelFor(entry.Model);
            if (model < 0)
            {
              continue;
            }
            list.Add(onWater);
            float turn = (entry.MinTurn + (float)random.NextDouble() * (entry.MaxTurn - entry.MinTurn)) * MathF.PI / 180;
            Objects.Add(new LocationObject(model, Matrix4x4.CreateRotationZ(turn) * Matrix4x4.CreateTranslation(onWater)) { FloatAt = onWater });
            count++;
          }
        }
      }
      LitterModels = string.Join(", ", placed.GroupBy(p => p.Key.Model, StringComparer.OrdinalIgnoreCase).Select(g => g.Key + " " + g.Sum(p => p.Value.Count)))
                     + "; faces by tag " + string.Join(", ", faces.GroupBy(f => f.Tag).Select(g => g.Key + ": " + g.Count()));
      return count;

      // The litter's model (in pc\data\maps\generic\procobj.img), loaded once; -1 if not found.
      int ModelFor(string name)
      {
        if (models.TryGetValue(name, out int known))
        {
          return known;
        }
        models[name] = -1;
        if (!archiveOf.TryGetValue(name, out var archive))
        {
          MissingModels.TryAdd(name, "no archive");
          return -1;
        }
        var img = new IMGFileSystem();
        try
        {
          img.Open(archive);
          if (new[] { ".wdr", ".wft" }.Select(e => img.RootDirectory.FindByName(name + e) as File).FirstOrDefault(f => f != null) is File file)
          {
            Models.Add(ModelScene.Load(file, file.GetData(), vertexColors: true, shared: shared, rawTextures: true));
            ModelNames.Add(name);
            models[name] = Models.Count - 1;
          }
          else
          {
            MissingModels.TryAdd(name, "not in its archive");
          }
        }
        catch (Exception ex)
        {
          MissingModels.TryAdd(name, "unreadable: " + ex.Message);
        }
        finally
        {
          img.Close();
        }
        return models[name];
      }
    }

    // Outside: how far models (not their far versions) are shown at most, in metres.
    private const float OutsideModels = 150;

    /// <summary>
    /// The map's files of a kind under <paramref name="sub"/> (pc\data ...) as the game loads them. An episode
    /// (TLAD, TBoGT: a content.dat in its folder, GTA IV's folder above it) only adds to GTA IV's city: its
    /// content.dat and images.txt list its own files besides GTA IV's (GTA IV's images.txt is read too), so
    /// GTA IV's files come too; where the episode has a file of the same path (TLAD's east\bronx_w.img) both
    /// are loaded, the episode's first so its models win where both have one (et01_ba: the roads and bridge
    /// round the toll booth are only in GTA IV's bronx_w.img). <paramref name="replaced"/> gets GTA IV's files
    /// the episode has one of the same path of; without it those aren't given (the loose .wpl: the episode's
    /// alone).
    /// </summary>
    private static IEnumerable<string> MapFiles(string folder, string sub, string pattern, HashSet<string>? replaced = null)
    {
      string own = Path.Combine(folder, sub);
      var ownFiles = System.IO.Directory.Exists(own)
        ? System.IO.Directory.EnumerateFiles(own, pattern, SearchOption.AllDirectories).ToList()
        : new List<string>();
      foreach (var path in ownFiles)
      {
        yield return path;
      }
      var parent = System.IO.Directory.GetParent(Path.TrimEndingDirectorySeparator(folder));
      if (!System.IO.File.Exists(Path.Combine(folder, "content.dat")) || parent == null)
      {
        yield break;
      }
      string baseDir = Path.Combine(parent.FullName, sub);
      if (!System.IO.Directory.Exists(baseDir))
      {
        yield break;
      }
      var episodes = new HashSet<string>(ownFiles.Select(p => Path.GetRelativePath(own, p)), StringComparer.OrdinalIgnoreCase);
      foreach (var path in System.IO.Directory.EnumerateFiles(baseDir, pattern, SearchOption.AllDirectories))
      {
        if (!episodes.Contains(Path.GetRelativePath(baseDir, path)))
        {
          yield return path;
        }
        else if (replaced != null)
        {
          replaced.Add(path);
          yield return path;
        }
      }
    }

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
        foreach (var ide in MapFiles(folder, Path.Combine("pc", "data"), "*.ide", new HashSet<string>()))
        {
          ReadIde(ide, nameOf, index.Interiors, index.Sizes, index.DictionaryOf, index.Hours);
        }
        var looseStart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        {
          string maps = Path.Combine("pc", "data", "maps");
          // The far versions' placements are in loose files beside the archives (east\bronx_e.wpl ...), first:
          // the archives' placements point into them.
          foreach (var path in MapFiles(folder, maps, "*.wpl"))
          {
            try
            {
              looseStart[Path.GetFileNameWithoutExtension(path)] = index.Placements.Count;
              ReadPlacements(System.IO.File.ReadAllBytes(path), nameOf, index.Placements, index.Placements.Count, index.LampShadows);
            }
            catch (Exception)
            {
              // A file that can't be read: its objects aren't shown.
            }
          }
          // An archive's placements by the name of their .wpl, each name once: an episode's archive of the same
          // path as GTA IV's holds only the .wpl it changes (TLAD's bronx_w.img 9 of GTA IV's 18, its manhat09.img
          // none), GTA IV's others stay (et01_aa: St Mary's ground, path and church in GTA IV's
          // bronx_w_stream3.wpl, which TLAD's archive hasn't; without them the cast stood in the air). Their
          // links to far versions point into the loose file of the archive's name, the episode's having GTA IV's
          // records in the same order.
          var placementFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          foreach (var path in MapFiles(folder, maps, "*.img", new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
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
                else if (extension.Equals(".wpl", StringComparison.OrdinalIgnoreCase) && placementFiles.Add(file.Name))
                {
                  ReadPlacements(file.GetData(), nameOf, index.Placements,
                             looseStart.TryGetValue(Path.GetFileNameWithoutExtension(path), out int parents) ? parents : -1, index.LampShadows);
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
            nameOf.TryAdd(RageLib.GtaIV.Pc.Text.GxtFile.Hash(fields[0]), fields[0]);
          }
        }
        // An object: name, texture dictionary, the distance it is drawn up to, flags, a number, its bounds
        // (box min, max, sphere centre, radius), the dictionary it is in; a timed one (tobj) also the hours
        // it is shown.
        // An animated one (anim, tanm) the same with its animations' dictionary third (manhat09.ide's
        // LOD_BAWSAQ_01_DC9, manhat09, ...: far versions of Manhattan's signs, in slodmh96mh98_dc9_grp); shown at
        // rest here, not animated.
        int shift = section is "anim" or "tanm" ? 1 : 0;
        if (section is "objs" or "tobj" or "anim" or "tanm" && fields.Length >= 15 + shift &&
            float.TryParse(fields[2 + shift], NumberStyles.Float, CultureInfo.InvariantCulture, out float draw) &&
            float.TryParse(fields[14 + shift], NumberStyles.Float, CultureInfo.InvariantCulture, out float bound))
        {
          sizes.TryAdd(fields[0], (draw, bound));
          if (fields.Length >= 16 + shift && fields[15 + shift].Length > 0 && !fields[15 + shift].Equals("null", StringComparison.OrdinalIgnoreCase))
          {
            dictionaryOf.TryAdd(fields[0], fields[15 + shift]);
          }
          if (section is "tobj" or "tanm" && fields.Length >= 17 + shift && uint.TryParse(fields[16 + shift], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint shownHours))
          {
            hours.TryAdd(fields[0], shownHours);
          }
        }
        if (section == "mlo" && inPortals && interior != null && fields.Length >= 19)
        {
          // A portal: the rooms on either side, its four corners, ..., its flags (19th number). GTAIV.exe leaves
          // out of a ped's easing those with flag 0x10 or 0x4 (0xA8F6A0, 0xA8F7D0 on the portal's +0x4C).
          try
          {
            int flags = int.TryParse(fields[18], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fl) ? fl : 0;
            var first = new Vector3(F(fields[2]), F(fields[3]), F(fields[4]));
            var third = new Vector3(F(fields[8]), F(fields[9]), F(fields[10]));
            if ((flags & 0x14) == 0 && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int from) &&
                int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int to))
            {
              interior.Portals.Add((from, to, (first + third) / 2, Vector3.Distance(first, third) / 2));
            }
          }
          catch (FormatException)
          {
          }
          continue;
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
            // A room's own line: its name and box; its 11th number the hash of its time cycle modifier's name
            // (Bernie's rooms 1720238244, "BernieCrane" in timecyclemodifiers3.dat; 0 none, limbo).
            uint modifier = fields.Length > 10 && uint.TryParse(fields[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint m) ? m : 0;
            try
            {
              var a = new Vector3(F(fields[3]), F(fields[4]), F(fields[5]));
              var b = new Vector3(F(fields[6]), F(fields[7]), F(fields[8]));
              interior.Rooms.Add((fields[0], Vector3.Min(a, b), Vector3.Max(a, b), modifier));
            }
            catch (FormatException)
            {
              interior.Rooms.Add((fields[0], Vector3.Zero, Vector3.Zero, modifier));
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
            // Its instance flags (tenth number; 384 = 0x180 for most), 0x100 letting its lamps cast shadows.
            if (fields.Length > 9 && uint.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint instanceFlags) && (instanceFlags & 0x100) != 0)
            {
              interior.LampShadows.Add(interior.Objects.Count - 1);
            }
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
    private static void ReadPlacements(byte[] d, Dictionary<uint, string> nameOf, List<(string, Vector3, Quaternion, int)> placements, int parents,
                                       HashSet<int>? lampShadows = null)
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
        if ((BitConverter.ToUInt32(d, o + 32) & 0x100) != 0)
        {
          lampShadows?.Add(placements.Count);
        }
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
      // The textures the materials name, each once, for the packs.
      var named = new List<string>();
      var namedOnce = new HashSet<string>(StringComparer.Ordinal);
      string Named(string key)
      {
        if (namedOnce.Add(key))
        {
          named.Add(key);
        }
        return url(key);
      }
      for (int m = 0; m < Models.Count; m++)
      {
        materialStart[m] = nextMaterial;
        foreach (var material in Models[m].Materials)
        {
          int model = m;
          var node = JsonSerializer.SerializeToNode(material.ToMessage(key => Named(TextureKey(model, key))))!.AsObject();
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
          // A far version (drawn into the mirrored surroundings' map), and one only for that map.
          if (obj.Far)
          {
            node["far"] = true;
          }
          if (obj.MirrorOnly)
          {
            node["mirrorOnly"] = true;
          }
          if (obj.FloatAt is Vector3 floatAt)
          {
            node["floatAt"] = new JsonArray(floatAt.X, floatAt.Y, floatAt.Z);
          }
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
      _packs.Clear();
      long filled = 0;
      foreach (string key in named)
      {
        if (Texture(key) is not byte[] texture)
        {
          continue;
        }
        if (_packs.Count == 0 || (filled > 0 && filled + texture.Length > PackBytes))
        {
          _packs.Add(new List<string>());
          filled = 0;
        }
        _packs[^1].Add(key);
        filled += texture.Length;
      }
      var packs = new JsonArray();
      for (int i = 0; i < _packs.Count; i++)
      {
        packs.Add(new JsonObject { ["url"] = url(PackKey + i), ["textures"] = new JsonArray(_packs[i].Select(k => (JsonNode)JsonValue.Create(url(k))!).ToArray()) });
      }
      return (new JsonObject { ["type"] = "scene", ["packs"] = packs, ["materials"] = materials, ["meshes"] = meshes }, visible);
    }

    /// <summary>
    /// The "mapLights" message: the lights of the objects' models (lamps, wall lights; the light the game
    /// puts there), moved into the scene: place, direction, colour, range, a spot's angle in degrees from its axis
    /// (0 for a light all round). With them the interiors' rooms (by their numbers in the objects' room):
    /// the interior's place, the turn from the scene into it, the box there.
    /// </summary>
    /// <summary>The "mapLights" message; <paramref name="volumeTexture"/>: the address of <see cref="LightVolume"/>, or null.</summary>
    public object LightsMessage(string? volumeTexture = null)
    {
      var lights = new List<object>();
      foreach (var obj in Objects)
      {
        foreach (var light in Models[obj.Model].Lights)
        {
          var at = Vector3.Transform(light.Position, obj.Place);
          var way = Vector3.TransformNormal(light.Direction, obj.Place);
          way = way.LengthSquared() > 1e-8f ? Vector3.Normalize(way) : new Vector3(0, 0, -1);
          var across = Vector3.TransformNormal(light.Tangent, obj.Place);
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
            // As GTAIV.exe hands it to the deferred light (0xC1E200 → 0xABCCD0): the radius is Distance
            // (+0x38; none without it), the strength Range (+0x3C); Flags say when it is on (0xC1F9A0).
            radius = light.Distance,
            strength = light.Range,
            flags = light.Flags,
            // Drawn by the lighting's shadow program: its own flag 0x8000 and its object's instance flag 0x100.
            shadows = obj.LampShadows && (light.Flags & 0x8000) != 0,
            // Its light volume (flag 0x80000; GTAIV.exe 0xAC2270): strength = its own x Range, size x the radius.
            volume = (light.Flags & 0x80000) != 0 && light.VolumeIntensity != 0 && light.VolumeSize != 0
              ? new[] { light.VolumeIntensity * light.Range, light.VolumeSize }
              : null,
            // Its second axis (the volume's mesh is turned by it: gDeferredLightTangent).
            tangent = new[] { across.X, across.Y, across.Z },
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
        portals = r.Portals.Select(q => new { centre = new[] { q.Centre.X, q.Centre.Y, q.Centre.Z }, radius = q.Radius, to = q.To }).ToList(),
      }).ToList();
      return new { type = "mapLights", lights, rooms, volumeTexture };
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
