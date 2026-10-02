using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RageLib.Models;
using RageLib.Models.Data;
using RageLib.Models.Resource.Shaders;
using RageLib.Textures;
using SparkIV.WinUI.Model;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A model (.wdr), model dictionary (.wdd) or fragment (.wft) prepared for the 3D view: every mesh as
  /// arrays for the graphics card, its materials, the textures they use as PNG, and the tree of parts.
  /// </summary>
  public sealed class ModelScene
  {
    private const string LodNames = "high|medium|low|very low";

    public List<SceneMesh> Meshes { get; } = new List<SceneMesh>();
    public List<SceneMaterial> Materials { get; } = new List<SceneMaterial>();

    /// <summary>The textures, by the name the view asks for ("12.png").</summary>
    public Dictionary<string, byte[]> Textures { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    public ModelPart Root { get; }

    /// <summary>Material textures no texture dictionary had.</summary>
    public SortedSet<string> MissingTextures { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly List<TextureFile> _dictionaries;
    private Directory? _folder;
    private bool _folderSearched;

    // At most this many other dictionaries of the folder are searched for a texture that is still missing.
    private const int MaxDictionariesSearched = 300;

    private ModelScene(string name, List<TextureFile> dictionaries)
    {
      Root = new ModelPart(name, "", "\uF158");
      _dictionaries = dictionaries;
    }

    /// <summary>
    /// A texture the model uses, from its own dictionary or the ones next to it: those named like the model
    /// and the shared ones, one named like the texture (peds: feet_diff_000_a_uni.wtd holds
    /// feet_diff_000_a_uni), and finally any other dictionary in the folder.
    /// </summary>
    private Texture? FindTexture(string name, TextureFile? embedded)
    {
      var texture = embedded?.FindTextureByName(name) ?? _dictionaries.Select(d => d.FindTextureByName(name)).FirstOrDefault(t => t != null);
      if (texture != null || _folder == null)
      {
        return texture;
      }

      if (_folder.FindByName(name + ".wtd") is File named && OpenDictionary(named) is TextureFile own)
      {
        texture = own.FindTextureByName(name);
        if (texture != null)
        {
          return texture;
        }
      }

      if (!_folderSearched)
      {
        _folderSearched = true;
        foreach (var file in _folder.OfType<File>().Where(f => f.Name.EndsWith(".wtd", StringComparison.OrdinalIgnoreCase)).Take(MaxDictionariesSearched))
        {
          OpenDictionary(file);
        }
        return _dictionaries.Select(d => d.FindTextureByName(name)).FirstOrDefault(t => t != null);
      }
      return null;
    }

    private readonly HashSet<string> _opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private TextureFile? OpenDictionary(File file)
    {
      if (_opened.Contains(file.Name))
      {
        return null;  // Already among the dictionaries.
      }
      byte[] data;
      try
      {
        data = file.GetData();
      }
      catch (Exception)
      {
        _opened.Add(file.Name);
        return null;
      }
      return OpenDictionary(file.Name, data);
    }

    private TextureFile? OpenDictionary(string name, byte[] data)
    {
      if (!_opened.Add(name))
      {
        return null;
      }
      try
      {
        var textures = new TextureFile();
        textures.Open(new MemoryStream(data));
        _dictionaries.Add(textures);
        return textures;
      }
      catch (Exception)
      {
        return null;
      }
    }

    public static ModelScene Load(File file, byte[] data)
    {
      string extension = Path.GetExtension(file.Name).ToLowerInvariant();
      var dictionaries = new List<TextureFile>();
      var scene = new ModelScene(file.Name, dictionaries) { _folder = file.ParentDirectory };
      try
      {
        // Textures the model doesn't have itself: a dictionary of the same name next to it (admiral.wtd for
        // admiral.wft) and the ones vehicles share, as the old viewer did. More are looked for as needed.
        string baseName = Path.GetFileNameWithoutExtension(file.Name);
        foreach (string name in new[] { baseName, "vehshare", "vehshare_truck" })
        {
          if (file.ParentDirectory?.FindByName(name + ".wtd") is File wtd)
          {
            scene.OpenDictionary(wtd);
          }
        }
        // Models loose in the game folder have theirs in the textures folder beside: pc\models\plantsmgr.wdd,
        // pc\textures\plantsmgr.wtd.
        if (file.ParentDirectory?.ParentDirectory?.FindByName("textures") is Directory textures &&
            textures.FindByName(baseName + ".wtd") is File beside)
        {
          scene.OpenDictionary(beside);
        }

        // The dictionaries the game's .ide files give the model (map objects: bronx_e.ide gives
        // l_bxe_cm_treelod tree_lods) and their parents, next to the model or shared in gtxd.img.
        foreach (string name in Services.GameTextures.DictionariesFor(baseName))
        {
          if (file.ParentDirectory?.FindByName(name + ".wtd") is File wtd)
          {
            scene.OpenDictionary(wtd);
          }
          else if (Services.GameTextures.ReadShared(name) is byte[] shared)
          {
            scene.OpenDictionary(name + ".wtd", shared);
          }
        }

        switch (extension)
        {
          case ".wft":
          {
            using var fragment = new ModelFragTypeFile();
            fragment.Open(new MemoryStream(data));
            var body = fragment.GetDataModel();
            scene.AddDrawable(scene.Root, "Body", body, true);
            scene.AddFragmentChildren(body, fragment.GetChildren());
            break;
          }
          case ".wdd":
          {
            using var dictionary = new ModelDictionaryFile();
            dictionary.Open(new MemoryStream(data));
            // A ped's dictionary holds its parts (head_000_r, uppr_000_u, lowr_001_u ...), some in several
            // variations. One variation of each part is shown, the lowest number; entries whose name isn't
            // known are all shown.
            var names = Enumerable.Range(0, dictionary.ModelsInDictionary).Select(i => PedComponentNames.Find(dictionary.GetNameHash(i))).ToList();
            // Map objects are named in the game's .ide files, a few others are known (the plants); they are all shown.
            var mapNames = Enumerable.Range(0, dictionary.ModelsInDictionary)
                                     .Select(i => names[i] == null
                                                    ? Services.GameTextures.ModelName(dictionary.GetNameHash(i)) ?? KnownModelNames.Find(dictionary.GetNameHash(i))
                                                    : null)
                                     .ToList();
            var firstOfPart = names.Where(n => n != null)
                                   .GroupBy(n => n!.Substring(0, n.IndexOf('_')), StringComparer.OrdinalIgnoreCase)
                                   .Select(g => g.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First())
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var order = Enumerable.Range(0, names.Count).OrderBy(i => names[i] ?? mapNames[i] ?? "~", StringComparer.OrdinalIgnoreCase);
            foreach (int i in order)
            {
              string name = names[i] ?? mapNames[i] ?? "0x" + dictionary.GetNameHash(i).ToString("x8");
              scene.AddDrawable(scene.Root, name, dictionary.GetDataModel(i), names[i] == null || firstOfPart.Contains(names[i]!));
            }
            break;
          }
          default:
          {
            using var model = new ModelFile();
            model.Open(new MemoryStream(data));
            scene.AddDrawable(scene.Root, "Model", model.GetDataModel(), true);
            break;
          }
        }
      }
      finally
      {
        foreach (var textures in dictionaries)
        {
          textures.Dispose();
        }
      }

      scene.Root.SettleParents();
      return scene;
    }

    /// <summary>Where a drawable is put: at a bone, turned round about Z (a wheel made for the other side).</summary>
    private sealed record Placement(string Bone, float X, float Y, float Z, bool TurnedRound);

    /// <summary>
    /// The children of a fragment that have a model, each at its bone. Most children (doors, bonnet ...) are
    /// part of the body's model; those with a model of their own are mostly wheels. A vehicle has a model
    /// only for some wheels (the admiral for wheel_lf, a motorbike for the front and the rear wheel); the
    /// game puts it at the other wheel bones too: the one of the same axle on the other side, turned round,
    /// else the front wheel's.
    /// </summary>
    private void AddFragmentChildren(Drawable body, List<FragmentChild> children)
    {
      var withModel = children.Where(c => c.Drawable != null).ToList();
      if (withModel.Count == 0)
      {
        return;
      }

      Bone? BoneOf(int index) => body.Skeleton != null && index >= 0 ? body.Skeleton[index] : null;
      bool IsWheelBone(Bone? bone) => bone != null && bone.Name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase);

      // wheel_lf → ("l", "f"); wheel_rm1 → ("r", "m1")
      static (string Side, string Axle) WheelOf(string name) => (name.Substring(6, 1).ToLowerInvariant(), name.Substring(7).ToLowerInvariant());

      var wheelBones = new List<Bone>();
      if (body.Skeleton != null)
      {
        var stack = new Stack<Bone>();
        stack.Push(body.Skeleton.RootBone);
        while (stack.Count > 0)
        {
          var bone = stack.Pop();
          if (IsWheelBone(bone) && bone.Name.Length > 7)
          {
            wheelBones.Add(bone);
          }
          foreach (var child in bone.Children)
          {
            stack.Push(child);
          }
        }
      }

      // The wheel models, by the bone they were made for.
      var wheelModels = withModel.Where(c => IsWheelBone(BoneOf(c.BoneIndex)) && BoneOf(c.BoneIndex)!.Name.Length > 7)
                                 .ToDictionary(c => BoneOf(c.BoneIndex)!.Name, StringComparer.OrdinalIgnoreCase);
      if (wheelModels.Count > 0)
      {
        var uses = new Dictionary<FragmentChild, List<Placement>>();
        foreach (var bone in wheelBones.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
          var (side, axle) = WheelOf(bone.Name);
          FragmentChild? model = null;
          if (!wheelModels.TryGetValue(bone.Name, out model))
          {
            // The same axle on the other side; for a middle axle (lorries) the rear wheel; else the front one.
            model = wheelModels.Values.FirstOrDefault(m => WheelOf(BoneOf(m.BoneIndex)!.Name).Axle == axle)
                    ?? (axle != "f" ? wheelModels.Values.FirstOrDefault(m => WheelOf(BoneOf(m.BoneIndex)!.Name).Axle == "r") : null)
                    ?? wheelModels.Values.FirstOrDefault(m => WheelOf(BoneOf(m.BoneIndex)!.Name).Axle == "f")
                    ?? wheelModels.Values.First();
          }
          // Turned round when the model was made for the other side.
          bool turned = WheelOf(BoneOf(model.BoneIndex)!.Name).Side != side;
          var p = bone.AbsolutePosition;
          if (!uses.TryGetValue(model, out var list))
          {
            uses[model] = list = new List<Placement>();
          }
          list.Add(new Placement(bone.Name, p.X, p.Y, p.Z, turned));
        }

        foreach (var (model, places) in uses)
        {
          string madeFor = BoneOf(model.BoneIndex)!.Name;
          AddDrawable(Root, "Wheel (" + madeFor + ")", model.Drawable!, true, places);
        }
      }

      // Other children with a model: at their bone.
      var others = withModel.Where(c => !wheelModels.ContainsValue(c)).ToList();
      if (others.Count > 0)
      {
        var parts = Root.Add(new ModelPart("Parts", others.Count + (others.Count == 1 ? " part" : " parts") + " that can break off", "\uE8F1"));
        foreach (var child in others)
        {
          var bone = BoneOf(child.BoneIndex);
          var places = bone == null ? null : new[] { new Placement(bone.Name, bone.AbsolutePosition.X, bone.AbsolutePosition.Y, bone.AbsolutePosition.Z, false) };
          AddDrawable(parts, bone?.Name ?? "Part", child.Drawable!, true, places);
        }
      }
    }

    private void AddDrawable(ModelPart parent, string name, Drawable drawable, bool show, IReadOnlyList<Placement>? placements = null)
    {
      // The materials of this drawable, numbered for the whole scene.
      int firstMaterial = Materials.Count;
      string? assigned = KnownModelNames.TextureFor(name);
      foreach (var material in drawable.Materials)
      {
        Materials.Add(CreateMaterial(material, drawable.AttachedTexture, assigned));
      }
      var node = parent.Add(new ModelPart(name, drawable.Models.Count + (drawable.Models.Count == 1 ? " level of detail" : " levels of detail"), "\uF158"));

      string[] lodNames = LodNames.Split('|');
      for (int index = 0; index < drawable.Models.Count; index++)
      {
        // The slot it is in: high, medium, low or very low (a drawable may lack some, also in the middle).
        int lod = index < drawable.LevelsOfDetail.Count ? drawable.LevelsOfDetail[index] : index;
        var meshes = drawable.Models[index].Geometries.SelectMany(g => g.Meshes).ToList();
        int lodFaces = meshes.Sum(m => m.FaceCount);
        var lodNode = node.Add(new ModelPart("LOD " + lod + (lod < lodNames.Length ? " \u00B7 " + lodNames[lod] : ""),
                                             meshes.Count + (meshes.Count == 1 ? " mesh, " : " meshes, ") + lodFaces.ToString("N0") + " triangles", "\uE81E") { Lod = lod });
        // Placed more than once (the wheels): a node for each place with the meshes in it.
        var places = new List<Placement?>();
        if (placements != null)
        {
          places.AddRange(placements);
        }
        else
        {
          places.Add(null);
        }
        foreach (var placement in places)
        {
          var meshParent = placement == null ? lodNode : lodNode.Add(new ModelPart(placement.Bone, "", "\uE81D"));
          foreach (var mesh in meshes)
          {
            int id = AddMesh(mesh, mesh.MaterialIndex >= 0 && mesh.MaterialIndex < drawable.Materials.Count ? firstMaterial + mesh.MaterialIndex : -1, placement);
            string shader = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < drawable.Materials.Count ? drawable.Materials[mesh.MaterialIndex].ShaderName : "";
            meshParent.Add(new ModelPart("Mesh " + (meshParent.Children.Count + 1), shader + " \u00B7 " + mesh.FaceCount.ToString("N0") + " triangles", "\uE7B8") { MeshId = id });
          }
        }
        // The most detailed one there is is shown; the others are there to look at.
        lodNode.Initialize(show && index == 0);
      }
    }

    private int AddMesh(Mesh mesh, int material, Placement? placement)
    {
      var vertices = mesh.DecodeVertexData();
      var positions = new float[vertices.Length * 3];
      var normals = mesh.VertexHasNormal ? new float[vertices.Length * 3] : null;
      var uvs = mesh.VertexHasTexture ? new float[vertices.Length * 2] : null;
      // Turned round about Z: x and y change sign (a turn, not a mirror, so the triangles keep their side).
      float turn = placement?.TurnedRound == true ? -1 : 1;
      float dx = placement?.X ?? 0, dy = placement?.Y ?? 0, dz = placement?.Z ?? 0;
      for (int i = 0; i < vertices.Length; i++)
      {
        var v = vertices[i];
        positions[i * 3] = v.Position.X * turn + dx;
        positions[i * 3 + 1] = v.Position.Y * turn + dy;
        positions[i * 3 + 2] = v.Position.Z + dz;
        if (normals != null)
        {
          normals[i * 3] = v.Normal.X * turn;
          normals[i * 3 + 1] = v.Normal.Y * turn;
          normals[i * 3 + 2] = v.Normal.Z;
        }
        if (uvs != null)
        {
          uvs[i * 2] = v.TextureCoordinates[0].X;
          uvs[i * 2 + 1] = v.TextureCoordinates[0].Y;
        }
      }

      ushort[] indices = mesh.DecodeIndexData();
      int count = Math.Min(indices.Length, mesh.FaceCount * 3);
      var triangles = new ushort[count];
      Array.Copy(indices, triangles, count);

      var scene = new SceneMesh(Meshes.Count, material, positions, normals, uvs, triangles);
      Meshes.Add(scene);
      return scene.Id;
    }

    /// <param name="assigned">The texture the game gives the model itself (the plants), in place of the one it names.</param>
    private SceneMaterial CreateMaterial(Material material, TextureFile? embedded, string? assigned = null)
    {
      string? textureName = assigned ?? (material.Parameters.TryGetValue((uint)ParamNameHash.Texture, out var param)
        ? (param as MaterialParamTexture)?.TextureName
        : null);

      string? key = null;
      if (!string.IsNullOrEmpty(textureName))
      {
        // The model's own textures first, then the dictionaries next to it.
        var texture = FindTexture(textureName, embedded);
        if (texture != null)
        {
          key = Materials.Count + ".png";
          try
          {
            using var image = texture.Decode();
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            Textures[key] = png.ToArray();
          }
          catch (Exception)
          {
            key = null;
          }
        }
        else
        {
          MissingTextures.Add(textureName);
        }
      }

      // Glass is drawn see-through; it is named so in the shader (gta_vehicle_vehglass, gta_glass ...).
      bool glass = material.ShaderName.Contains("glass", StringComparison.OrdinalIgnoreCase);
      return new SceneMaterial(Materials.Count, material.ShaderName, textureName, key, glass);
    }
  }

  public sealed class SceneMesh
  {
    public SceneMesh(int id, int material, float[] positions, float[]? normals, float[]? uvs, ushort[] indices)
    {
      Id = id;
      Material = material;
      Positions = positions;
      Normals = normals;
      Uvs = uvs;
      Indices = indices;
    }

    public int Id { get; }
    public int Material { get; }
    public float[] Positions { get; }
    public float[]? Normals { get; }
    public float[]? Uvs { get; }
    public ushort[] Indices { get; }

    public int TriangleCount => Indices.Length / 3;

    /// <summary>For the page: the arrays as base64 of their little-endian bytes.</summary>
    public object ToMessage() => new
    {
      id = Id,
      material = Material,
      positions = Base64(Positions),
      normals = Normals == null ? null : Base64(Normals),
      uvs = Uvs == null ? null : Base64(Uvs),
      indices = Convert.ToBase64String(MemoryMarshal.AsBytes(Indices.AsSpan())),
    };

    private static string Base64(float[] values) => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
  }

  public sealed class SceneMaterial
  {
    public SceneMaterial(int id, string shader, string? textureName, string? textureKey, bool glass)
    {
      Id = id;
      Shader = shader;
      TextureName = textureName;
      TextureKey = textureKey;
      Glass = glass;
    }

    public int Id { get; }
    public string Shader { get; }
    public string? TextureName { get; }
    public string? TextureKey { get; }
    public bool Glass { get; }
  }
}
