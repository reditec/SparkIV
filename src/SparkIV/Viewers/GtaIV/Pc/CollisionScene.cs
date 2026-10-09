using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RageLib.GtaIV.Pc.Bounds;
using SparkIV.WinUI.Model;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A procedural object (procedural.dat's PROCOBJ line) grown on collision faces of its tag: the model, one per
  /// Spacing² m² (GTAIV.exe 0xC29310: 1 / spacing², spacing at least 2 m), none closer than MinDistance to
  /// another (at most 80 m), turned MinTurn to MaxTurn degrees, lifted ZOffsetMin to ZOffsetMax; Floats: on the
  /// water's surface above the face (which must be under water). Scales are left out: they don't apply to the
  /// objects that float (dynamic ones, as the file says) and those are the ones placed here.
  /// </summary>
  internal sealed record ProceduralObject(int Tag, string Model, float Spacing, float MinDistance, float MinTurn, float MaxTurn,
                                          float ZOffsetMin, float ZOffsetMax, bool Align, bool Grid, bool Seed, bool Floats);

  /// <summary>
  /// The names collision materials refer to: surfaces (common\data\materials\materials.dat, by line) and the
  /// procedural objects grown on them (procedural.dat: each tag numbered from 1 in the order it first appears,
  /// objects and plants together - GTAIV.exe 0xC29AE0; 0 is none). Read once from the game's folder.
  /// </summary>
  internal static class CollisionNames
  {
    private static string? s_folder;
    private static string[] s_materials = Array.Empty<string>();
    private static string[] s_groups = Array.Empty<string>();
    private static string[] s_tags = Array.Empty<string>();

    /// <summary>The tags whose objects float on the water (procedural.dat's FLOATS column set).</summary>
    public static HashSet<int> FloatingTags { get; private set; } = new HashSet<int>();

    /// <summary>procedural.dat's objects (PROCOBJ lines) with their tags' numbers.</summary>
    public static List<ProceduralObject> Objects { get; private set; } = new List<ProceduralObject>();

    public static void Load(string? gameFolder)
    {
      if (gameFolder == null || gameFolder == s_folder)
      {
        return;
      }
      s_folder = gameFolder;
      string folder = Path.Combine(gameFolder, "common", "data", "materials");
      try
      {
        var lines = System.IO.File.ReadAllLines(Path.Combine(folder, "materials.dat"))
                          .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#") && l != "2.00")
                          .Select(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
        s_materials = lines.Select(f => f[0]).ToArray();
        s_groups = lines.Select(f => f.Length > 1 ? f[1] : "").ToArray();
      }
      catch (Exception)
      {
        s_materials = s_groups = Array.Empty<string>();
      }
      try
      {
        var tags = new List<string>();
        var floating = new HashSet<int>();
        var objectList = new List<ProceduralObject>();
        bool inData = false, objects = false;
        foreach (string raw in System.IO.File.ReadAllLines(Path.Combine(folder, "procedural.dat")))
        {
          string line = raw.Trim();
          if (line.Length == 0 || line.StartsWith("#"))
          {
            continue;
          }
          if (line.EndsWith("_DATA_START", StringComparison.OrdinalIgnoreCase))
          {
            inData = true;
            objects = line.StartsWith("PROCOBJ", StringComparison.OrdinalIgnoreCase);
            continue;
          }
          if (line.EndsWith("_DATA_END", StringComparison.OrdinalIgnoreCase))
          {
            inData = false;
            continue;
          }
          if (!inData)
          {
            continue;
          }
          var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
          int index = tags.IndexOf(fields[0]);
          if (index < 0)
          {
            tags.Add(fields[0]);
            index = tags.Count - 1;
          }
          // An object line: tag, object, spacing, min. distance, min./max. turn, min./max. scale (xy, z), min./max.
          // height offset, then ALIGN, USEGRID, USESEED, FLOATS.
          if (objects && fields.Length >= 16)
          {
            float F(int i) => float.TryParse(fields[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0;
            var entry = new ProceduralObject(index + 1, fields[1], F(2), F(3), F(4), F(5), F(10), F(11),
                                             fields[12] == "1", fields[13] == "1", fields[14] == "1", fields[15] == "1");
            objectList.Add(entry);
            if (entry.Floats)
            {
              floating.Add(index + 1);
            }
          }
        }
        s_tags = tags.ToArray();
        FloatingTags = floating;
        Objects = objectList;
      }
      catch (Exception)
      {
        s_tags = Array.Empty<string>();
      }
    }

    public static string Material(int id) => id >= 0 && id < s_materials.Length ? s_materials[id] : "Material " + id;

    /// <summary>The material's effects group (CONCRETE, WOOD, METAL, GRASS ...), or "".</summary>
    public static string Group(int id) => id >= 0 && id < s_groups.Length ? s_groups[id] : "";

    public static string Tag(int tag) => tag <= 0 ? "" : tag <= s_tags.Length ? s_tags[tag - 1] : "tag " + tag;
  }

  /// <summary>
  /// Collision (.wbn static collision, .wbd collision dictionaries) as a scene for the model view: each
  /// shape's faces by material, flat shaded and coloured by the kind of surface; the tree shows the shapes,
  /// their materials and the procedural objects grown on them.
  /// </summary>
  internal static class CollisionScene
  {
    // Vertices per mesh stay below 65536 (16-bit indices); three per triangle (flat shaded).
    private const int MaxTrianglesPerMesh = 21000;

    /// <summary>The scene of a collision file; <paramref name="gameFolder"/> for the names of its materials (or null).</summary>
    public static ModelScene Load(File file, byte[] data, string? gameFolder)
    {
      CollisionNames.Load(gameFolder);
      var bounds = new BoundFile();
      bounds.Open(new MemoryStream(data));
      if (bounds.Bounds.Count == 0)
      {
        throw new InvalidDataException("No collision shapes this viewer can show" +
                                       (bounds.Skipped.Count > 0 ? " (only " + string.Join(", ", bounds.Skipped.Keys.Select(k => "type " + k)) + ")" : "") + ".");
      }
      var scene = ModelScene.Empty(file.Name);
      var materialIds = new Dictionary<(int Id, int Tag), int>();
      // A dictionary's entries are named by the hash of their model's name; the models lie beside it.
      var beside = new Dictionary<uint, string>();
      foreach (var other in file.ParentDirectory?.OfType<File>() ?? Enumerable.Empty<File>())
      {
        string model = Path.GetFileNameWithoutExtension(other.Name);
        beside.TryAdd(RageLib.GtaIV.Pc.Text.GxtFile.Hash(model), model);
      }
      foreach (var (hash, bound) in bounds.Bounds)
      {
        string name = bounds.Bounds.Count == 1 && hash == 0 ? Path.GetFileNameWithoutExtension(file.Name)
          : beside.TryGetValue(hash, out var model) ? model : KnownName(hash);
        AddBound(scene, scene.Root, name, bound, Matrix4x4.Identity, materialIds);
      }
      scene.Root.Initialize(true);
      scene.Root.SettleParents();
      return scene;
    }

    private static string KnownName(uint hash)
    {
      string? name = KnownModelNames.Find(hash);
      return name ?? "0x" + hash.ToString("X8");
    }

    private static void AddBound(ModelScene scene, ModelPart parent, string name, Bound bound, Matrix4x4 place,
                                 Dictionary<(int, int), int> materialIds)
    {
      if (bound.Type == BoundType.Composite)
      {
        var node = parent.Add(new ModelPart(name, "Composite · " + bound.Children.Count + (bound.Children.Count == 1 ? " shape" : " shapes"), ""));
        for (int i = 0; i < bound.Children.Count; i++)
        {
          if (bound.Children[i] is Bound child)
          {
            AddBound(scene, node, "Shape " + i, child, bound.ChildMatrices[i] * place, materialIds);
          }
        }
        return;
      }
      if (bound.Type == BoundType.Sphere)
      {
        AddSphere(scene, parent, name, bound, place, materialIds);
        return;
      }

      var shape = parent.Add(new ModelPart(name, bound.Type + " · " + bound.Polygons.Length.ToString("N0") + " faces · " +
                                                 bound.Vertices.Length.ToString("N0") + " vertices", ""));
      // The faces as triangles (a quad as two), by material.
      var byMaterial = new SortedDictionary<int, List<(Vector3 A, Vector3 B, Vector3 C)>>();
      var vertices = bound.Vertices.Select(v => Vector3.Transform(v, place)).ToArray();
      foreach (var polygon in bound.Polygons)
      {
        if (!byMaterial.TryGetValue(polygon.Material, out var list))
        {
          byMaterial[polygon.Material] = list = new List<(Vector3, Vector3, Vector3)>();
        }
        if (polygon.A >= vertices.Length || polygon.B >= vertices.Length || polygon.C >= vertices.Length || polygon.D >= vertices.Length)
        {
          continue;
        }
        list.Add((vertices[polygon.A], vertices[polygon.B], vertices[polygon.C]));
        if (polygon.D >= 0)
        {
          list.Add((vertices[polygon.A], vertices[polygon.C], vertices[polygon.D]));
        }
      }
      foreach (var (index, triangles) in byMaterial)
      {
        var material = index < bound.Materials.Length ? bound.Materials[index] : new BoundMaterial(0, 0, 0);
        int id = MaterialFor(scene, material, materialIds);
        string tag = CollisionNames.Tag(material.ProceduralTag);
        string detail = triangles.Count.ToString("N0") + " triangles" + (tag.Length > 0 ? " · " + tag : "");
        var node = shape.Add(new ModelPart(CollisionNames.Material(material.Id), detail, ""));
        for (int start = 0; start < triangles.Count; start += MaxTrianglesPerMesh)
        {
          var part = triangles.Skip(start).Take(MaxTrianglesPerMesh).ToList();
          int meshId = AddMesh(scene, id, part);
          if (triangles.Count <= MaxTrianglesPerMesh)
          {
            node = ReplaceWithMesh(node, meshId);
          }
          else
          {
            node.Add(new ModelPart("Part " + (start / MaxTrianglesPerMesh + 1), part.Count.ToString("N0") + " triangles", "") { MeshId = meshId });
          }
        }
      }
    }

    // A material node with one mesh: the node itself is that mesh.
    private static ModelPart ReplaceWithMesh(ModelPart node, int meshId)
    {
      var parent = node.Parent!;
      parent.Children.Remove(node);
      return parent.Add(new ModelPart(node.Name, node.Detail, node.Glyph) { MeshId = meshId });
    }

    private static void AddSphere(ModelScene scene, ModelPart parent, string name, Bound bound, Matrix4x4 place,
                                  Dictionary<(int, int), int> materialIds)
    {
      const int rings = 8, segments = 12;
      var centre = Vector3.Transform(bound.Centroid, place);
      Vector3 At(int ring, int segment)
      {
        double theta = Math.PI * ring / rings, phi = 2 * Math.PI * segment / segments;
        return centre + bound.Radius * new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta));
      }
      var triangles = new List<(Vector3, Vector3, Vector3)>();
      for (int r = 0; r < rings; r++)
      {
        for (int s = 0; s < segments; s++)
        {
          var a = At(r, s);
          var b = At(r + 1, s);
          var c = At(r + 1, s + 1);
          var d = At(r, s + 1);
          if (r > 0)
          {
            triangles.Add((a, b, d));
          }
          if (r < rings - 1)
          {
            triangles.Add((b, c, d));
          }
        }
      }
      int id = MaterialFor(scene, new BoundMaterial(0, 0, 0), materialIds);
      parent.Add(new ModelPart(name, "Sphere · radius " + bound.Radius.ToString("0.###") + " m", "") { MeshId = AddMesh(scene, id, triangles) });
    }

    private static int AddMesh(ModelScene scene, int material, List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
      var positions = new float[triangles.Count * 9];
      var normals = new float[triangles.Count * 9];
      var indices = new ushort[triangles.Count * 3];
      for (int t = 0; t < triangles.Count; t++)
      {
        var (a, b, c) = triangles[t];
        var normal = Vector3.Cross(b - a, c - a);
        normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitZ;
        int i = 0;
        foreach (var v in new[] { a, b, c })
        {
          int k = t * 9 + i * 3;
          positions[k] = v.X;
          positions[k + 1] = v.Y;
          positions[k + 2] = v.Z;
          normals[k] = normal.X;
          normals[k + 1] = normal.Y;
          normals[k + 2] = normal.Z;
          indices[t * 3 + i] = (ushort)(t * 3 + i);
          i++;
        }
      }
      int id = scene.Meshes.Count;
      scene.Meshes.Add(new SceneMesh(id, material, positions, normals, null, indices, new int[triangles.Count * 3]));
      return id;
    }

    // One scene material per surface and tag, coloured by the surface's kind (its effects group).
    private static int MaterialFor(ModelScene scene, BoundMaterial material, Dictionary<(int, int), int> ids)
    {
      if (ids.TryGetValue((material.Id, material.ProceduralTag), out int id))
      {
        return id;
      }
      id = scene.Materials.Count;
      scene.Materials.Add(new SceneMaterial(id, "collision", null, null, false) { Color = ColorOf(material) });
      ids[(material.Id, material.ProceduralTag)] = id;
      return id;
    }

    private static float[] ColorOf(BoundMaterial material)
    {
      // Surfaces whose procedural objects float: water blue, so the floating litter's places stand out.
      if (CollisionNames.FloatingTags.Contains(material.ProceduralTag))
      {
        return new[] { 0.2f, 0.55f, 0.95f };
      }
      string group = CollisionNames.Group(material.Id).ToUpperInvariant();
      float[] colour = group switch
      {
        "CONCRETE" or "STONE" or "MARBLE" or "PAVING_SLABS" or "GENERIC" => new[] { 0.62f, 0.62f, 0.6f },
        "BRICK_COBBLE" => new[] { 0.68f, 0.45f, 0.38f },
        "WOOD" or "LAMINATE" or "CARDBOARD" or "PAPER" => new[] { 0.72f, 0.52f, 0.3f },
        "TREE_BARK_DARK" or "TREE_BARK_MEDIUM" or "TREE_BARK_LIGHT" => new[] { 0.5f, 0.38f, 0.26f },
        "METAL" or "CAR_METAL" => new[] { 0.55f, 0.6f, 0.68f },
        "GRASS" or "LEAVES_PILE" or "BUSHES" or "FLOWERS" => new[] { 0.35f, 0.7f, 0.3f },
        "MUD_SOFT" or "SAND" or "GRAVEL" or "DIRT_DRY" => new[] { 0.75f, 0.62f, 0.38f },
        "GLASS" or "WINDSCREEN" => new[] { 0.6f, 0.85f, 0.9f },
        "WATER" => new[] { 0.25f, 0.45f, 0.8f },
        "PLASTIC" or "RUBBER" or "CAR_PLASTIC" or "CERAMICS" or "LINOLEUM" => new[] { 0.8f, 0.78f, 0.7f },
        "CARPET_FABRIC" or "MATTRESS_FOAM" or "PILLOW_FEATHERS" => new[] { 0.7f, 0.5f, 0.6f },
        "VOID" => new[] { 0.9f, 0.4f, 0.9f },
        _ => Hue(material.Id),
      };
      // Surfaces with other procedural objects (plants, litter) a little lighter.
      return material.ProceduralTag > 0 ? colour.Select(c => Math.Min(1f, c * 1.18f)).ToArray() : colour;
    }

    private static float[] Hue(int id)
    {
      double h = (id * 0.618) % 1 * 6, s = 0.3, l = 0.6;
      double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = l - c / 2;
      var (r, g, b) = h < 1 ? (c, x, 0d) : h < 2 ? (x, c, 0d) : h < 3 ? (0d, c, x) : h < 4 ? (0d, x, c) : h < 5 ? (x, 0d, c) : (c, 0d, x);
      return new[] { (float)(r + m), (float)(g + m), (float)(b + m) };
    }
  }
}
