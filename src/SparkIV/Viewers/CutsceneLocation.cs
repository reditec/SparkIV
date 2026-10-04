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
  /// <summary>A map object around a cutscene: its model, and how it stands in the scene (model to scene).</summary>
  public sealed record LocationObject(int Model, Matrix4x4 Place);

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

    /// <summary>The water's quads in the scene's places (4 corners each), or empty.</summary>
    public List<Vector3> Water { get; } = new List<Vector3>();

    /// <summary>Placements whose model couldn't be loaded.</summary>
    public int Missing { get; private set; }
    public int Hidden { get; private set; }

    /// <summary>A texture's key for the page: "m" and the model's number, then its key in the model's scene.</summary>
    public static string TextureKey(int model, string key) => "m" + model + "/" + key;

    /// <summary>The texture behind a key from <see cref="TextureKey"/>, or null.</summary>
    public byte[]? Texture(string key)
    {
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
      public List<(string Name, Vector3 Position, Quaternion Rotation)> Placements { get; } = new List<(string, Vector3, Quaternion)>();
      public Dictionary<string, string> ArchiveOf { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      /// <summary>
      /// The interiors (the .ide files' mlo sections: Apart, Brucie ...) and their objects, placed in the
      /// interior as the map places objects in the world; a placement of the interior's name places them all.
      /// </summary>
      public Dictionary<string, List<(string Name, Vector3 Position, Quaternion Rotation)>> Interiors { get; } =
        new Dictionary<string, List<(string, Vector3, Quaternion)>>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, MapIndex> s_indexes = new Dictionary<string, MapIndex>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The map objects within <paramref name="radius"/> metres (across the ground) of the scene's origin in
    /// the maps of the game folder (the one holding pc\; each episode has its own city), on a background
    /// thread with the archives' key set. The far-away versions (lod…) are left out.
    /// </summary>
    public static CutsceneLocation Load(string folder, float[] offset, IReadOnlyList<CutRemoval> removals, float radius)
    {
      var index = IndexOf(folder);
      var origin = new Vector3(offset[0], offset[1], offset[2]);
      var location = new CutsceneLocation();
      // An interior placed: its objects, each placed in it as the interior is in the world (the interior's
      // turn inverted as any placement's in the map; the object's own turn applied first, as it is).
      var placements = new List<(string Name, Vector3 Position, Quaternion Rotation)>();
      foreach (var p in index.Placements)
      {
        if (!index.Interiors.TryGetValue(p.Name, out var objects))
        {
          placements.Add(p);
          continue;
        }
        if (Math.Abs(p.Position.X - origin.X) > radius + 60 || Math.Abs(p.Position.Y - origin.Y) > radius + 60)
        {
          continue;
        }
        var turn = Quaternion.Conjugate(p.Rotation);
        foreach (var o in objects)
        {
          // An object's turn in an interior is the model's own, not inverted as a placement's in the map
          // (pub3_BERNIProps, turned 128° in Bernie's flat: inverted, its candles, frames and box stood in the
          // middle of the room instead of on the shelves where they are in the game).
          var combined = Quaternion.Concatenate(o.Rotation, turn);
          placements.Add((o.Name, p.Position + Vector3.Transform(o.Position, turn), Quaternion.Conjugate(combined)));
        }
      }
      var near = placements.Where(p => Math.Abs(p.Position.X - origin.X) <= radius && Math.Abs(p.Position.Y - origin.Y) <= radius &&
                                             new Vector2(p.Position.X - origin.X, p.Position.Y - origin.Y).Length() <= radius &&
                                             !p.Name.StartsWith("lod", StringComparison.OrdinalIgnoreCase))
                                 .ToList();
      // Hidden by the scene: the same model within a metre of the place [REMOVE] gives (to a tenth of a metre there).
      var shown = near.Where(p => !removals.Any(r => r.Model.Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
                                                     Vector3.Distance(new Vector3(r.X, r.Y, r.Z), p.Position) < 1f)).ToList();
      location.Hidden = near.Count - shown.Count;

      var modelOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (var group in shown.GroupBy(p => index.ArchiveOf.TryGetValue(p.Name, out var a) ? a : ""))
      {
        if (group.Key.Length == 0)
        {
          location.Missing += group.Count();
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
          continue;
        }
        try
        {
          foreach (var placement in group)
          {
            if (!modelOf.TryGetValue(placement.Name, out int model))
            {
              model = -1;
              var file = new[] { ".wdr", ".wft" }.Select(e => img.RootDirectory.FindByName(placement.Name + e) as File).FirstOrDefault(f => f != null);
              if (file != null)
              {
                try
                {
                  // While the archive is open: the textures are looked for beside the model and through the .ide files.
                  location.Models.Add(ModelScene.Load(file, file.GetData(), vertexColors: true));
                  location.ModelNames.Add(placement.Name);
                  model = location.Models.Count - 1;
                }
                catch (Exception)
                {
                  model = -1;
                }
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
            location.Objects.Add(new LocationObject(model, place));
          }
        }
        finally
        {
          img.Close();
        }
      }
      location.AddWater(folder, origin, radius);
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
            ReadIde(ide, nameOf, index.Interiors);
          }
        }
        string maps = Path.Combine(folder, "pc", "data", "maps");
        if (System.IO.Directory.Exists(maps))
        {
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
                else if (extension.Equals(".wpl", StringComparison.OrdinalIgnoreCase))
                {
                  ReadPlacements(file.GetData(), nameOf, index.Placements);
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
    /// then its rooms and portals from "mloroomstart" to "mloend".
    /// </summary>
    private static void ReadIde(string path, Dictionary<uint, string> nameOf, Dictionary<string, List<(string, Vector3, Quaternion)>> interiors)
    {
      string? section = null;
      List<(string, Vector3, Quaternion)>? interior = null;
      bool inRooms = false;
      foreach (var raw in System.IO.File.ReadLines(path))
      {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
        {
          continue;
        }
        if (!line.Contains(','))
        {
          // A section's start or end ("objs", "mlo", "end"), or within an interior where its rooms begin and end.
          if (section == "mlo" && line == "mloroomstart")
          {
            inRooms = true;
          }
          else if (section == "mlo" && line == "mloend")
          {
            inRooms = false;
            interior = null;
          }
          else if (line == "end")
          {
            section = null;
            interior = null;
            inRooms = false;
          }
          else if (section == null)
          {
            section = line.ToLowerInvariant();
          }
          continue;
        }
        var fields = line.Split(',').Select(f => f.Trim()).ToArray();
        if (fields[0].Length > 0)
        {
          nameOf.TryAdd(RageLib.Text.GxtFile.Hash(fields[0]), fields[0]);
        }
        if (section != "mlo" || inRooms)
        {
          continue;
        }
        bool indented = raw.Length > 0 && char.IsWhiteSpace(raw[0]);
        if (!indented)
        {
          // The interior's own line: its name first.
          interior = new List<(string, Vector3, Quaternion)>();
          interiors[fields[0]] = interior;
        }
        else if (interior != null && fields.Length >= 9)
        {
          static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
          try
          {
            // The far versions (level of detail 1, 2) are left out.
            if (fields[8] == "0")
            {
              interior.Add((fields[0], new Vector3(F(fields[1]), F(fields[2]), F(fields[3])), new Quaternion(F(fields[4]), F(fields[5]), F(fields[6]), F(fields[7]))));
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
    /// turn (quaternion x y z w), the model's hash at 28.
    /// </summary>
    private static void ReadPlacements(byte[] d, Dictionary<uint, string> nameOf, List<(string, Vector3, Quaternion)> placements)
    {
      if (d.Length < 68 || BitConverter.ToUInt32(d, 0) != 3)
      {
        return;
      }
      int count = (int)BitConverter.ToUInt32(d, 4);
      for (int i = 0; i < count && 68 + i * 48 + 32 <= d.Length; i++)
      {
        int o = 68 + i * 48;
        if (!nameOf.TryGetValue(BitConverter.ToUInt32(d, o + 28), out var name))
        {
          continue;
        }
        placements.Add((name,
                        new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8)),
                        new Quaternion(BitConverter.ToSingle(d, o + 12), BitConverter.ToSingle(d, o + 16), BitConverter.ToSingle(d, o + 20), BitConverter.ToSingle(d, o + 24))));
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
          ["id"] = water, ["glass"] = true, ["cutout"] = false, ["blend"] = false, ["decal"] = false, ["hidden"] = false,
          ["texture"] = null, ["color"] = new JsonArray(0.18f, 0.38f, 0.42f),
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
