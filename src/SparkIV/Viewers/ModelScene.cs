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
using SparkIV.WinUI.Viewers.GtaIV.Pc;
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

    /// <summary>The bones of the model's skeleton by index (empty without one), for the export.</summary>
    public List<SceneBone> Bones { get; } = new List<SceneBone>();

    /// <summary>The lights of the drawables (street lamps, neon, spots), placed in the model.</summary>
    public List<SceneLight> Lights { get; } = new List<SceneLight>();

    /// <summary>Whether the model has textures of its own (stored in it, not in a .wtd).</summary>
    public bool HasEmbeddedTextures { get; private set; }

    /// <summary>The textures, by the name the view asks for ("12.png").</summary>
    public Dictionary<string, byte[]> Textures { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    public ModelPart Root { get; }

    /// <summary>Material textures no texture dictionary had.</summary>
    public SortedSet<string> MissingTextures { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What "Source" says for the textures stored in the model itself.</summary>
    public const string EmbeddedSource = "In the model";

    /// <summary>What "Source" says for textures no dictionary had.</summary>
    public const string MissingSource = "Not found";

    /// <summary>
    /// The model's textures with where each comes from: all those stored in the model (also unused), and
    /// those of other dictionaries the materials use, and the ones not found.
    /// </summary>
    public List<SceneTexture> TextureList { get; } = new List<SceneTexture>();

    private SceneTexture Listed(string name, string source, Texture? texture)
    {
      var listed = TextureList.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && t.Source == source);
      if (listed == null)
      {
        listed = new SceneTexture(name, source, texture);
        TextureList.Add(listed);
      }
      return listed;
    }

    /// <summary>
    /// The files the textures come from: the model itself and the dictionaries opened from files next to
    /// it. When one of them changes, the textures are made again (<see cref="TakeTexturesFrom"/>).
    /// </summary>
    public HashSet<File> SourceFiles { get; } = new HashSet<File>();

    /// <summary>
    /// Takes the materials and textures of the same model loaded again (after a texture was replaced); the
    /// meshes and parts stay. Returns false if the model itself changed so much that they don't fit.
    /// </summary>
    public bool TakeTexturesFrom(ModelScene again)
    {
      if (again.Materials.Count != Materials.Count || again.Meshes.Count != Meshes.Count)
      {
        return false;
      }
      Materials.Clear();
      Materials.AddRange(again.Materials);
      Textures.Clear();
      foreach (var pair in again.Textures)
      {
        Textures[pair.Key] = pair.Value;
      }
      MissingTextures.Clear();
      MissingTextures.UnionWith(again.MissingTextures);
      TextureList.Clear();
      TextureList.AddRange(again.TextureList);
      SourceFiles.Clear();
      SourceFiles.UnionWith(again.SourceFiles);
      HasEmbeddedTextures = again.HasEmbeddedTextures;
      return true;
    }

    private readonly List<TextureFile> _dictionaries;

    // The file name of each of _dictionaries ("vehshare.wtd"), for where a texture comes from.
    private readonly List<string> _dictionaryNames = new List<string>();
    private Directory? _folder;
    private bool _folderSearched;

    // At most this many other dictionaries of the folder are searched for a texture that is still missing.
    private const int MaxDictionariesSearched = 300;

    /// <summary>A scene without models or textures, for shapes built elsewhere (collision).</summary>
    internal static ModelScene Empty(string name) => new ModelScene(name, new List<TextureFile>());

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
    private Texture? FindTexture(string name, TextureFile? embedded, out string source)
    {
      source = EmbeddedSource;
      var texture = embedded?.FindTextureByName(name);
      if (texture != null)
      {
        return texture;
      }

      texture = FindInDictionaries(name, out source);
      if (texture != null || _folder == null)
      {
        return texture;
      }

      if (_folder.FindByName(name + ".wtd") is File named && OpenDictionary(named) is TextureFile own)
      {
        texture = own.FindTextureByName(name);
        if (texture != null)
        {
          source = named.Name;
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
        return FindInDictionaries(name, out source);
      }
      return null;
    }

    /// <summary>
    /// A texture as PNG; one from a dictionary (not the model's own) made once per <see cref="SharedTextures"/>.
    /// </summary>
    private byte[] Png(Texture texture, string source)
    {
      string cacheKey = source + "|" + texture.Name + (_rawTextures ? "|raw" : "");
      if (_shared != null && source != EmbeddedSource && _shared.Pngs.TryGetValue(cacheKey, out var cached))
      {
        return cached;
      }
      long started = System.Diagnostics.Stopwatch.GetTimestamp();
      var bytes = _rawTextures ? Raw(texture) : EncodePng(texture);
      if (_shared != null)
      {
        _shared.TextureSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        _shared.TexturesMade++;
      }
      if (_shared != null && source != EmbeddedSource)
      {
        _shared.Pngs[cacheKey] = bytes;
      }
      return bytes;
    }

    private SharedTextures? _shared;

    // The textures as the game has them (Raw), not as PNG: see Load's rawTextures.
    private bool _rawTextures;

    private static byte[] EncodePng(Texture texture)
    {
      using var image = texture.Decode();
      using var png = new MemoryStream();
      image.Save(png, ImageFormat.Png);
      return png.ToArray();
    }

    /// <summary>
    /// A texture as the game has it, for the page to give the graphics card as it is (DXT1, 3, 5 compressed,
    /// with its smaller levels; A8R8G8B8 and L8 as they are): "SXT1", then as 32-bit numbers its format
    /// (0 DXT1, 1 DXT3, 2 DXT5, 3 A8R8G8B8, 4 L8), width, height and levels, then the data of all levels.
    /// No decoding and PNG making (about half of a cutscene's location's loading, bc_3: 5 of 10 s).
    /// </summary>
    public static byte[] Raw(Texture texture)
    {
      var data = texture.TextureData;
      var bytes = new byte[20 + data.Length];
      bytes[0] = (byte)'S'; bytes[1] = (byte)'X'; bytes[2] = (byte)'T'; bytes[3] = (byte)'1';
      BitConverter.GetBytes((uint)texture.TextureType).CopyTo(bytes, 4);
      BitConverter.GetBytes(texture.Width).CopyTo(bytes, 8);
      BitConverter.GetBytes(texture.Height).CopyTo(bytes, 12);
      BitConverter.GetBytes((uint)Math.Max(1, texture.Levels)).CopyTo(bytes, 16);
      data.CopyTo(bytes, 20);
      return bytes;
    }

    /// <summary>
    /// What the models loaded with it share (a location's: its far versions use their district's few
    /// dictionaries): the texture dictionaries opened, by name, and the textures made into PNG, by
    /// dictionary and name. Disposed when the last model is loaded.
    /// </summary>
    public sealed class SharedTextures : IDisposable
    {
      internal Dictionary<string, TextureFile> Opened { get; } = new Dictionary<string, TextureFile>(StringComparer.OrdinalIgnoreCase);
      internal Dictionary<string, byte[]> Pngs { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      /// <summary>
      /// Model dictionaries (.wdd) read once for all the models taken from them (a district's far versions:
      /// lod_bxe2_01_grp holds dozens), by the very bytes they were read from.
      /// </summary>
      internal Dictionary<byte[], ModelDictionaryFile> ModelDictionaries { get; } = new Dictionary<byte[], ModelDictionaryFile>(ReferenceEqualityComparer.Instance);

      /// <summary>For the load log: seconds spent making textures (and how many), opening texture dictionaries and reading model dictionaries.</summary>
      public double TextureSeconds { get; internal set; }
      public int TexturesMade { get; internal set; }
      public double DictionarySeconds { get; internal set; }
      public double ModelDictionarySeconds { get; internal set; }
      public int ModelDictionariesRead => ModelDictionaries.Count;

      public void Dispose()
      {
        foreach (var textures in Opened.Values)
        {
          textures.Dispose();
        }
        Opened.Clear();
        foreach (var models in ModelDictionaries.Values)
        {
          models.Dispose();
        }
        ModelDictionaries.Clear();
      }
    }

    private Texture? FindInDictionaries(string name, out string source)
    {
      for (int i = 0; i < _dictionaries.Count; i++)
      {
        var texture = _dictionaries[i].FindTextureByName(name);
        if (texture != null)
        {
          source = _dictionaryNames[i];
          return texture;
        }
      }
      source = MissingSource;
      return null;
    }

    private readonly HashSet<string> _opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private TextureFile? OpenDictionary(File file)
    {
      if (_opened.Contains(file.Name))
      {
        return null;  // Already among the dictionaries.
      }
      if (_shared != null && _shared.Opened.TryGetValue(file.Name, out var known))
      {
        _opened.Add(file.Name);
        _dictionaries.Add(known);
        _dictionaryNames.Add(file.Name);
        SourceFiles.Add(file);
        return known;
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
      var opened = OpenDictionary(file.Name, data);
      if (opened != null)
      {
        SourceFiles.Add(file);
      }
      return opened;
    }

    private TextureFile? OpenDictionary(string name, byte[] data)
    {
      if (!_opened.Add(name))
      {
        return null;
      }
      try
      {
        if (_shared != null && _shared.Opened.TryGetValue(name, out var known))
        {
          _dictionaries.Add(known);
          _dictionaryNames.Add(name);
          return known;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var textures = new TextureFile();
        textures.Open(new MemoryStream(data));
        _dictionaries.Add(textures);
        _dictionaryNames.Add(name);
        if (_shared != null)
        {
          _shared.Opened[name] = textures;
          _shared.DictionarySeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        }
        return textures;
      }
      catch (Exception)
      {
        return null;
      }
    }

    /// <summary>
    /// A ped put together from its parts (head_000_r.wdr, uppr_000_u.wdr ...), all in one folder with the
    /// dictionaries named like their textures; each part carries the ped's whole skeleton.
    /// </summary>
    public static ModelScene LoadPed(string name, Directory folder, IReadOnlyList<File> parts)
    {
      var dictionaries = new List<TextureFile>();
      var scene = new ModelScene(name, dictionaries) { _folder = folder };
      try
      {
        foreach (var part in parts)
        {
          scene.SourceFiles.Add(part);
          using var model = new ModelFile();
          model.Open(new MemoryStream(part.GetData()));
          scene._pedTexture = PedTextureFor(part.Name, folder);
          scene.AddDrawable(scene.Root, Path.GetFileNameWithoutExtension(part.Name), model.GetDataModel(), true);
        }
        scene._pedTexture = null;
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

    // For the ped part being added: its texture by the game's naming rule, if the material's isn't there.
    private string? _pedTexture;

    /// <summary>
    /// The texture the game gives a ped part: part_diff_number_variant_kind (feet_000_u.wdr gets
    /// feet_diff_000_a_uni, head_000_r.wdr head_diff_000_a_whi); variant a. Null if there is none.
    /// </summary>
    private static string? PedTextureFor(string partFile, Directory folder)
    {
      var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(partFile), @"^([a-z0-9]+)_(\d{3})_[a-z]$",
                                                              System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      if (!match.Success)
      {
        return null;
      }
      string prefix = match.Groups[1].Value + "_diff_" + match.Groups[2].Value + "_a_";
      var file = folder.OfType<File>()
                       .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".wtd", StringComparison.OrdinalIgnoreCase))
                       .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .FirstOrDefault();
      return file == null ? null : Path.GetFileNameWithoutExtension(file.Name);
    }

    // A texture to use in place of one a material names, where there is (or null): see Load.
    private Func<string, string?>? _preferTexture;

    // Whether every mesh keeps its vertex colours (see Load).
    private bool _vertexColors;

    /// <summary>
    /// A model with its textures. <paramref name="moreDictionaries"/>: the bytes of a dictionary not beside
    /// the model ("vehshare"), from wherever the caller knows; null where it isn't.
    /// <paramref name="preferTexture"/>: the name of a texture to use in place of one a material names, when
    /// it is there (a ped's texture variation), or null. <paramref name="vertexColors"/>: every mesh keeps
    /// its vertex colours (map objects: the light baked into them), not only terrain and dirt decals.
    /// <paramref name="onlyModel"/>: of a dictionary (.wdd), only the model of that name hash (a map object's far
    /// version among its district's, lod_bxe2_01_grp). <paramref name="shared"/>: the dictionaries and textures
    /// shared with the other models loaded with it (a location's), kept open by it. <paramref name="rawTextures"/>:
    /// the textures for the page as the game has them (<see cref="Raw"/>), not PNG (the cutscenes' locations).
    /// </summary>
    public static ModelScene Load(File file, byte[] data, Func<string, byte[]?>? moreDictionaries = null, Func<string, string?>? preferTexture = null,
                                  bool vertexColors = false, uint? onlyModel = null, SharedTextures? shared = null,
                                  bool rawTextures = false)
    {
      string extension = Path.GetExtension(file.Name).ToLowerInvariant();
      var dictionaries = new List<TextureFile>();
      var scene = new ModelScene(file.Name, dictionaries) { _folder = file.ParentDirectory, _preferTexture = preferTexture, _vertexColors = vertexColors, _shared = shared, _rawTextures = rawTextures };
      scene.SourceFiles.Add(file);
      // A ped part on its own (feet_000_u.wdr): its texture by the game's naming rule where the material's isn't there.
      if (file.ParentDirectory != null && extension == ".wdr")
      {
        scene._pedTexture = PedTextureFor(file.Name, file.ParentDirectory);
      }
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
          else if (moreDictionaries?.Invoke(name) is byte[] elsewhere)
          {
            // In another archive the caller knows of (TBoGT's vehicles.img lacks GTA IV's vehshare.wtd).
            scene.OpenDictionary(name + ".wtd", elsewhere);
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
        // l_bxe_cm_treelod tree_lods) and their parents, next to the model or shared in gtxd.img; for one
        // model of a dictionary, the model's (the .ide names it, not its dictionary).
        string modelName = onlyModel is uint only && Services.GameTextures.ModelName(only) is string named ? named : baseName;
        foreach (string name in Services.GameTextures.DictionariesFor(modelName))
        {
          if (file.ParentDirectory?.FindByName(name + ".wtd") is File wtd)
          {
            scene.OpenDictionary(wtd);
          }
          else if (shared?.Opened.ContainsKey(name + ".wtd") == true)
          {
            scene.OpenDictionary(name + ".wtd", Array.Empty<byte>());
          }
          else if (Services.GameTextures.ReadShared(name) is byte[] inGtxd)
          {
            scene.OpenDictionary(name + ".wtd", inGtxd);
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
            // Kept for the next model of the same dictionary where shared, else closed after this one.
            ModelDictionaryFile? own = null;
            if (shared == null || !shared.ModelDictionaries.TryGetValue(data, out var dictionary))
            {
              var reading = System.Diagnostics.Stopwatch.StartNew();
              dictionary = new ModelDictionaryFile();
              dictionary.Open(new MemoryStream(data));
              if (shared != null)
              {
                shared.ModelDictionaries[data] = dictionary;
                shared.ModelDictionarySeconds += reading.Elapsed.TotalSeconds;
              }
              else
              {
                own = dictionary;
              }
            }
            using var closing = own;
            // One model of it: only that entry looked at.
            if (onlyModel is uint wanted)
            {
              for (int i = 0; i < dictionary.ModelsInDictionary; i++)
              {
                if (dictionary.GetNameHash(i) == wanted)
                {
                  string name = Services.GameTextures.ModelName(wanted) ?? KnownModelNames.Find(wanted) ?? "0x" + wanted.ToString("x8");
                  scene.AddDrawable(scene.Root, name, dictionary.GetDataModel(i), true);
                }
              }
              break;
            }
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
            // A prop's part is its anchor (p_head_000, p_head_001: one hat; p_eyes_000 beside it).
            var firstOfPart = names.Where(n => n != null)
                                   .GroupBy(n => n!.Substring(0, n.IndexOf('_', n.StartsWith("p_", StringComparison.OrdinalIgnoreCase) ? 2 : 0)), StringComparer.OrdinalIgnoreCase)
                                   .Select(g => g.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First())
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var order = Enumerable.Range(0, names.Count).Where(i => onlyModel == null || dictionary.GetNameHash(i) == onlyModel)
                                  .OrderBy(i => names[i] ?? mapNames[i] ?? "~", StringComparer.OrdinalIgnoreCase);
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
        // The shared ones are kept for the next model.
        foreach (var textures in dictionaries.Where(d => shared == null || !shared.Opened.ContainsValue(d)))
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
      // The skeleton for the export: of the first drawable that has one (the body of a fragment, a ped).
      if (Bones.Count == 0 && drawable.Skeleton != null)
      {
        _bonesFrom = drawable.Skeleton;
        var stack = new Stack<Bone>();
        stack.Push(drawable.Skeleton.RootBone);
        var all = new List<Bone>();
        while (stack.Count > 0)
        {
          var bone = stack.Pop();
          all.Add(bone);
          foreach (var child in bone.Children)
          {
            stack.Push(child);
          }
        }
        foreach (var bone in all.OrderBy(b => b.Index))
        {
          Bones.Add(new SceneBone(bone.Index, bone.Name, bone.Parent?.Index ?? -1,
                                  bone.Position.X, bone.Position.Y, bone.Position.Z, bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z)
          {
            Id = (ushort)bone.Id,
            Quaternion = new[] { bone.RotationQuaternion.X, bone.RotationQuaternion.Y, bone.RotationQuaternion.Z, bone.RotationQuaternion.W },
          });
        }
      }
      if (drawable.AttachedTexture != null && drawable.AttachedTexture.Count > 0)
      {
        HasEmbeddedTextures = true;
        // All of them are listed, also those no material uses.
        foreach (var texture in drawable.AttachedTexture.Textures)
        {
          Listed(texture.Name, EmbeddedSource, texture);
        }
      }
      string? assigned = KnownModelNames.TextureFor(name);
      foreach (var material in drawable.Materials)
      {
        Materials.Add(CreateMaterial(material, drawable.AttachedTexture, assigned));
      }
      var node = parent.Add(new ModelPart(name, drawable.Models.Count + (drawable.Models.Count == 1 ? " level of detail" : " levels of detail"), "\uF158"));

      // Parts that aren't skinned hang on a bone, their vertices relative to it (map objects with moving
      // parts: a fan, a crane's hook); they are put in place with the bone's rest pose. Only for the
      // drawable's own skeleton, which is then the scene's (Bones).
      var rest = drawable.Skeleton != null && placements == null && ReferenceEquals(drawable.Skeleton, _bonesFrom) ? RestPose(drawable.Skeleton) : null;
      bool wholeOnOneBone = drawable.Skeleton == null && Bones.Count == 0 && placements == null;

      int firstMesh = Meshes.Count;

      string[] lodNames = LodNames.Split('|');
      for (int index = 0; index < drawable.Models.Count; index++)
      {
        // The slot it is in: high, medium, low or very low (a drawable may lack some, also in the middle).
        int lod = index < drawable.LevelsOfDetail.Count ? drawable.LevelsOfDetail[index] : index;
        var meshes = drawable.Models[index].Geometries.SelectMany(g => g.Meshes).ToList();
        var boneOfMesh = drawable.Models[index].Geometries.SelectMany(g => g.Meshes.Select(m => (m, g.IsSkinned ? -1 : g.BoneIndex)))
                                                         .ToDictionary(p => p.m, p => p.Item2);
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
            // Without any skeleton (bkn2_bs_logobust2), everything on one bone, as it is: an animation of the
            // object can still turn it as a whole.
            int rigidBone = rest != null && boneOfMesh[mesh] >= 0 && boneOfMesh[mesh] < rest.Length && !mesh.VertexHasBlendInfo ? boneOfMesh[mesh]
                          : wholeOnOneBone ? 0 : -1;
            int id = AddMesh(mesh, mesh.MaterialIndex >= 0 && mesh.MaterialIndex < drawable.Materials.Count ? firstMaterial + mesh.MaterialIndex : -1, placement,
                             rigidBone, rigidBone >= 0 && rest != null ? rest[rigidBone] : System.Numerics.Matrix4x4.Identity);
            string shader = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < drawable.Materials.Count ? drawable.Materials[mesh.MaterialIndex].ShaderName : "";
            meshParent.Add(new ModelPart("Mesh " + (meshParent.Children.Count + 1), shader + " \u00B7 " + mesh.FaceCount.ToString("N0") + " triangles", "\uE7B8") { MeshId = id });
          }
        }
        // The most detailed one there is is shown; the others are there to look at.
        lodNode.Initialize(show && index == 0);
      }

      // The drawable's lights: at their bone (in its rest pose) when the drawable has a skeleton; shown
      // while a mesh of the drawable is.
      if (drawable.Lights.Count > 0 && placements == null)
      {
        var meshIds = Enumerable.Range(firstMesh, Meshes.Count - firstMesh).ToArray();
        var boneOfId = new Dictionary<int, int>();
        if (rest != null)
        {
          foreach (var bone in Bones)
          {
            boneOfId.TryAdd(bone.Id, bone.Index);
          }
        }
        foreach (var light in drawable.Lights)
        {
          var at = boneOfId.TryGetValue(light.BoneId, out int index) && index < rest!.Length ? rest[index] : System.Numerics.Matrix4x4.Identity;
          var position = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(light.Position.X, light.Position.Y, light.Position.Z), at);
          var direction = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(light.Direction.X, light.Direction.Y, light.Direction.Z), at);
          Lights.Add(new SceneLight(position, direction, light.Color[0] / 255f, light.Color[1] / 255f, light.Color[2] / 255f,
                                    light.IsSpot, light.Range, light.Falloff, meshIds, light.Distance, light.HotSpot, light.Intensity)
                     { Flags = light.Flags });
        }
      }
    }

    // The skeleton Bones were taken from.
    private Skeleton? _bonesFrom;

    /// <summary>
    /// Each bone's place in the model in rest pose, by index: down the chain parent × translate × turn (as
    /// the page poses them; row vectors here, so the other way round).
    /// </summary>
    private static System.Numerics.Matrix4x4[] RestPose(Skeleton skeleton)
    {
      var all = new List<Bone>();
      var stack = new Stack<Bone>();
      stack.Push(skeleton.RootBone);
      while (stack.Count > 0)
      {
        var bone = stack.Pop();
        all.Add(bone);
        foreach (var child in bone.Children)
        {
          stack.Push(child);
        }
      }
      var world = new System.Numerics.Matrix4x4[all.Count == 0 ? 0 : all.Max(b => b.Index) + 1];
      var done = new bool[world.Length];
      System.Numerics.Matrix4x4 Of(Bone bone)
      {
        if (!done[bone.Index])
        {
          var q = bone.RotationQuaternion;
          var local = System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W)) *
                      System.Numerics.Matrix4x4.CreateTranslation(bone.Position.X, bone.Position.Y, bone.Position.Z);
          world[bone.Index] = bone.Parent == null ? local : local * Of(bone.Parent);
          done[bone.Index] = true;
        }
        return world[bone.Index];
      }
      foreach (var bone in all)
      {
        Of(bone);
      }
      return world;
    }

    private static int SkeletonBone(Mesh mesh, uint blendIndex) =>
      mesh.BoneMapping.Length > 0 ? mesh.BoneMapping[Math.Min((int)blendIndex, mesh.BoneMapping.Length - 1)] : (int)blendIndex;

    /// <param name="rigidBone">For a part that isn't skinned: the bone it hangs on (its vertices are moved
    /// from the bone into the model by <paramref name="atBone"/>, the bone's rest pose), else -1.</param>
    private int AddMesh(Mesh mesh, int material, Placement? placement, int rigidBone = -1, System.Numerics.Matrix4x4 atBone = default)
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
        if (rigidBone >= 0)
        {
          var p = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(v.Position.X, v.Position.Y, v.Position.Z), atBone);
          var n = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z), atBone);
          positions[i * 3] = p.X;
          positions[i * 3 + 1] = p.Y;
          positions[i * 3 + 2] = p.Z;
          if (normals != null)
          {
            normals[i * 3] = n.X;
            normals[i * 3 + 1] = n.Y;
            normals[i * 3 + 2] = n.Z;
          }
        }
        else
        {
          positions[i * 3] = v.Position.X * turn + dx;
          positions[i * 3 + 1] = v.Position.Y * turn + dy;
          positions[i * 3 + 2] = v.Position.Z + dz;
          if (normals != null)
          {
            normals[i * 3] = v.Normal.X * turn;
            normals[i * 3 + 1] = v.Normal.Y * turn;
            normals[i * 3 + 2] = v.Normal.Z;
          }
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

      // The bone of each vertex: its strongest blend bone (peds), or the bone it is placed at (wheels).
      // A blend index is one of the mesh's own few bones: BoneMapping gives the skeleton bone.
      var bones = new int[vertices.Length];
      float[]? skinBones = null, skinWeights = null;
      bool skinned = mesh.VertexHasBlendInfo && mesh.BoneMapping.Length > 0;
      int placedAt = placement == null ? 0 : Math.Max(0, Bones.FindIndex(b => b.Name.Equals(placement.Bone, StringComparison.OrdinalIgnoreCase)));
      // A part placed at a bone (a vehicle's wheels, the parts that break off) moves with it, all of it on that bone:
      // laid at the bone's rest place, so the bone's move from its rest pose carries it (et01_ba: the Presidente's
      // wheels stood on the road where the car was at rest, the car having driven on).
      if (rigidBone < 0 && placement != null && !skinned && Bones.Count > 0)
      {
        rigidBone = placedAt;
      }
      if (skinned || rigidBone >= 0)
      {
        skinBones = new float[vertices.Length * 4];
        skinWeights = new float[vertices.Length * 4];
      }
      for (int i = 0; i < vertices.Length; i++)
      {
        bones[i] = placedAt;
        if (rigidBone >= 0)
        {
          // All of it on its bone.
          bones[i] = rigidBone;
          skinBones![i * 4] = rigidBone;
          skinWeights![i * 4] = 1;
        }
        else if (mesh.VertexHasBlendInfo && vertices[i].BlendIndices != null)
        {
          int strongest = 0;
          for (int k = 0; k < 4; k++)
          {
            int bone = SkeletonBone(mesh, vertices[i].BlendIndices[k]);
            if (skinned)
            {
              skinBones![i * 4 + k] = bone;
              skinWeights![i * 4 + k] = vertices[i].BlendWeights[k];
            }
            if (vertices[i].BlendWeights[k] > vertices[i].BlendWeights[strongest])
            {
              strongest = k;
            }
          }
          bones[i] = SkeletonBone(mesh, vertices[i].BlendIndices[strongest]);
        }
        if (bones[i] >= Bones.Count)
        {
          bones[i] = 0;
        }
      }

      // Terrain with N layers: texture coordinates 1 to N-1 tile the layers laid over the first; the next
      // two hold how much of each layer covers the one below (0 the first, about always 1; 1 to 3 from 0 to
      // 1), as found in the games' 2-, 3- and 4-layer terrain; the vertex colour shades it (hollows darker).
      float[]? layerUvs = null, layerWeights = null, colors = null;
      int layers = material >= 0 && material < Materials.Count ? Materials[material].LayerCount : 1;
      int texcoords = mesh.VertexDeclaration.Elements.TakeWhile(e => e.Stream != -1).Count(e => e.Usage == VertexElementUsage.TextureCoordinate);
      if (layers > 1 && texcoords >= layers + 1)
      {
        layerUvs = new float[vertices.Length * 6];
        layerWeights = new float[vertices.Length * 4];
        colors = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
          var t = vertices[i].TextureCoordinates;
          for (int layer = 1; layer < layers; layer++)
          {
            layerUvs[i * 6 + (layer - 1) * 2] = t[layer].X;
            layerUvs[i * 6 + (layer - 1) * 2 + 1] = t[layer].Y;
          }
          layerWeights[i * 4] = t[layers].X;
          layerWeights[i * 4 + 1] = t[layers].Y;
          layerWeights[i * 4 + 2] = layers + 1 < Vertex.MaxTextureCoordinates ? t[layers + 1].X : 0;
          layerWeights[i * 4 + 3] = layers + 1 < Vertex.MaxTextureCoordinates ? t[layers + 1].Y : 0;
          // Red in the lowest byte (OpenIV's export of tmp_ci6_7_beach04: 236 231 231 for 0xffe7e7ec).
          uint color = vertices[i].DiffuseColor;
          colors[i * 3] = (color & 0xFF) / 255f;
          colors[i * 3 + 1] = ((color >> 8) & 0xFF) / 255f;
          colors[i * 3 + 2] = ((color >> 16) & 0xFF) / 255f;
        }
      }

      // See-through materials and decals fade by their vertices' alpha too (the coastline decal of
      // tmp_ci6_7_beach04: 0 at its edges, so the dark hollow below shows, as in OpenIV). GTA IV PC's map glass
      // as well: gta_glass_spec's vertex shader passes the colour's alpha on (program 4: o3.zw = v1) and its
      // pixel shader (program 20) outputs alpha = texture alpha x it x globalScalars.x, blended SRCALPHA /
      // INVSRCALPHA (TLAD trace GTAIV.4, et01_ba's toll booth panes from call 145517152). Not a vehicle's glass:
      // gta_vehicle_vehglass sets the alpha to 1 (vertex shader 0x247A27C0).
      float[]? alphas = null;
      bool hasColor = mesh.VertexDeclaration.Elements.TakeWhile(e => e.Stream != -1).Any(e => e.Usage == VertexElementUsage.Color && e.UsageIndex == 0);
      if (hasColor && material >= 0 && material < Materials.Count &&
          (Materials[material].Blend || Materials[material].Glass && !Materials[material].Vehicle))
      {
        alphas = new float[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
          alphas[i] = (vertices[i].DiffuseColor >> 24) / 255f;
        }
      }
      // Dirt decals (gta_decal_dirt) take their colour from the vertices, the texture only says where; map
      // objects shown in their place (a cutscene's location) the light baked into them.
      // Vehicles' too: their shaders multiply the colour by them (gta_vehicle_paint2: texture x matDiffuseColor x vertex colour).
      // Peds' as well: their red is the AO of the light from around (GtaIVPcMaterials.IsPed).
      if (hasColor && colors == null && (_vertexColors || (material >= 0 && material < Materials.Count &&
          (Materials[material].DirtMask != null || Materials[material].Vehicle || GtaIVPcMaterials.IsPed(Materials[material].Shader)))))
      {
        colors = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
          uint color = vertices[i].DiffuseColor;
          colors[i * 3] = (color & 0xFF) / 255f;
          colors[i * 3 + 1] = ((color >> 8) & 0xFF) / 255f;
          colors[i * 3 + 2] = ((color >> 16) & 0xFF) / 255f;
        }
      }

      var scene = new SceneMesh(Meshes.Count, material, positions, normals, uvs, triangles, bones)
      {
        SkinBones = skinBones,
        SkinWeights = skinWeights,
        LayerUvs = layerUvs,
        LayerWeights = layerWeights,
        Colors = colors,
        Alphas = alphas,
      };
      Meshes.Add(scene);
      return scene.Id;
    }

    /// <param name="assigned">The texture the game gives the model itself (the plants), in place of the one it names.</param>
    private SceneMaterial CreateMaterial(Material material, TextureFile? embedded, string? assigned = null)
    {
      // Terrain (gta_terrain_va_2lyr ... 4lyr) has no single texture but layers mixed per vertex; its
      // first layer, the ground the others are laid over, stands for it (tmp_ci6_7_beach04: CI_sandLHT_cm).
      string? textureName = assigned ?? (material.Parameters.TryGetValue((uint)ParamNameHash.Texture, out var param) ||
                                         material.Parameters.TryGetValue(GtaIVPcMaterials.TerrainLayer0, out param)
        ? (param as MaterialParamTexture)?.TextureName
        : null);
      // Planes the game never draws (boats' no-water and no-splash planes) have no real texture, only the
      // artists' placeholder "givemechecker": not looked for, so it isn't reported missing.
      bool neverDrawn = GtaIVPcMaterials.IsNeverDrawn(material.DrawBucket);
      if (neverDrawn)
      {
        textureName = null;
      }
      // A render target ("script_rt": a TV's screen, which a script draws its picture on while it is on) isn't
      // a texture in any file: not looked for (see RenderTarget).
      bool renderTarget = textureName != null && textureName.StartsWith("script_rt", StringComparison.OrdinalIgnoreCase);
      if (renderTarget)
      {
        textureName = null;
      }

      string? key = null;
      if (!string.IsNullOrEmpty(textureName))
      {
        // Another texture in its place where the caller wants one and it is there (see Load).
        if (_preferTexture?.Invoke(textureName) is string other && !other.Equals(textureName, StringComparison.OrdinalIgnoreCase) &&
            FindTexture(other, embedded, out _) != null)
        {
          textureName = other;
        }
        // The model's own textures first, then the dictionaries next to it.
        var texture = FindTexture(textureName, embedded, out string source);
        if (texture == null && _pedTexture != null)
        {
          // A ped part naming a texture that isn't there (Johnny's feet: "givemechecker"): the one the game
          // takes by name for the part.
          var byRule = FindTexture(_pedTexture, embedded, out string ruleSource);
          if (byRule != null)
          {
            texture = byRule;
            source = ruleSource;
            textureName = _pedTexture;
          }
        }
        Listed(texture?.Name ?? textureName, source, texture).IsUsed = true;
        if (texture != null)
        {
          key = Materials.Count + ".png";
          try
          {
            Textures[key] = Png(texture, source);
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

      // Terrain: the layers laid over the first (the texture above), each its own texture.
      var layerKeys = new List<string?>();
      var terrainLayers = GtaIVPcMaterials.TerrainLayers;
      if (material.Parameters.ContainsKey(GtaIVPcMaterials.TerrainLayer0))
      {
        for (int layer = 1; layer < terrainLayers.Length && material.Parameters.ContainsKey(terrainLayers[layer]); layer++)
        {
          string? name = (material.Parameters[terrainLayers[layer]] as MaterialParamTexture)?.TextureName;
          layerKeys.Add(string.IsNullOrEmpty(name) ? null : AddTexture(name, embedded, Materials.Count + "_" + layer + ".png"));
        }
      }

      // Normal and specular maps, and how bumpy and shiny (the map shaders' parameters: gta_normal...,
      // gta_spec...). Not for planes the game doesn't draw.
      string? normalKey = null, specularKey = null;
      if (!neverDrawn)
      {
        if (TextureParameter(material, GtaIVPcMaterials.BumpSampler) is string normalName)
        {
          normalKey = AddTexture(normalName, embedded, Materials.Count + "_n.png");
        }
        if (TextureParameter(material, GtaIVPcMaterials.SpecSampler) is string specularName)
        {
          specularKey = AddTexture(specularName, embedded, Materials.Count + "_s.png");
        }
      }
      string? environmentKey = !neverDrawn && GtaIVPcMaterials.IsMapReflect(material.ShaderName) &&
                               TextureParameter(material, GtaIVPcMaterials.EnvironmentSampler) is string environmentName
        ? AddTexture(environmentName, embedded, Materials.Count + "_e.png") : null;
      var mask = VectorParameter(material, GtaIVPcMaterials.SpecMapIntMask);

      string shader = material.ShaderName;
      bool glass = GtaIVPcMaterials.IsGlass(shader);
      bool vehicle = GtaIVPcMaterials.IsVehicle(shader);
      var drawing = GtaIVPcMaterials.DrawingOf(material.DrawBucket, shader, glass);
      return new SceneMaterial(Materials.Count, shader, textureName, key, glass)
      {
        LayerKeys = layerKeys,
        DrawBucket = material.DrawBucket,
        Cutout = drawing.Cutout,
        Blend = drawing.Blend,
        Stipple = drawing.Stipple,
        Decal = drawing.Decal,
        Hidden = drawing.Hidden,
        NoWater = drawing.NoWater,
        Emissive = drawing.Emissive,
        RenderTarget = renderTarget,
        NormalKey = normalKey,
        SpecularKey = specularKey,
        EnvironmentKey = environmentKey,
        Bumpiness = VectorParameter(material, GtaIVPcMaterials.Bumpiness)?.X ?? 1,
        SpecularPower = VectorParameter(material, GtaIVPcMaterials.SpecularFactor)?.X ?? 0,
        SpecularIntensity = VectorParameter(material, GtaIVPcMaterials.SpecularColorFactor)?.X ?? 0,
        SpecularMask = mask == null ? new[] { 1f, 0f, 0f } : new[] { mask.Value.X, mask.Value.Y, mask.Value.Z },
        DirtMask = GtaIVPcMaterials.IsDirtDecal(shader)
          ? (VectorParameter(material, GtaIVPcMaterials.DirtDecalMask) is var dirt && dirt != null ? new[] { dirt.Value.X, dirt.Value.Y, dirt.Value.Z } : new[] { 1f, 0f, 0f })
          : null,
        PaintSlot = VectorParameter(material, GtaIVPcMaterials.MatDiffuseColor) is { X: 2 } paint && paint.Y >= 1 && paint.Y <= 6 ? (int)paint.Y : 0,
        Vehicle = vehicle,
        DirtKey = vehicle && !neverDrawn && TextureParameter(material, GtaIVPcMaterials.DirtSampler) is string dirtName
          ? AddTexture(dirtName, embedded, Materials.Count + "_d.png") : null,
        DirtColor = VectorParameter(material, GtaIVPcMaterials.DirtColor) is { } dirtColour ? new[] { dirtColour.X, dirtColour.Y, dirtColour.Z } : new[] { 0.23f, 0.22f, 0.2f },
        ReflectivePower = (VectorParameter(material, GtaIVPcMaterials.ReflectivePowerED) ?? VectorParameter(material, GtaIVPcMaterials.ReflectivePower))?.X ?? 0,
        HasSpecular2 = vehicle && VectorParameter(material, GtaIVPcMaterials.Specular2ColorIntensityED) != null,
        VehicleShine = vehicle ? GtaIVPcMaterials.VehicleShineOf(shader) : null,
        EmissiveMultiplier = GtaIVPcMaterials.IsEmissive(shader) ? VectorParameter(material, GtaIVPcMaterials.EmissiveMultiplier)?.X : null,
        EmissiveAtNight = GtaIVPcMaterials.IsEmissiveAtNight(shader),
        SubSurface = VectorParameter(material, GtaIVPcMaterials.SubColor) is { } sub && VectorParameter(material, GtaIVPcMaterials.SubScatWidth) is { } width
          ? new[] { sub.X, sub.Y, sub.Z, VectorParameter(material, GtaIVPcMaterials.SubScatWrap)?.X ?? 0, width.X }
          : null,
      };
    }

    // Rockstar's placeholder for a texture never given (some vehicles' normal maps): not looked for.
    private static string? TextureParameter(Material material, uint hash) =>
      material.Parameters.TryGetValue(hash, out var param) && param is MaterialParamTexture texture && !string.IsNullOrEmpty(texture.TextureName) &&
      !texture.TextureName.Equals(GtaIVPcMaterials.NoTexture, StringComparison.OrdinalIgnoreCase)
        ? texture.TextureName
        : null;

    private static RageLib.Common.ResourceTypes.Vector4? VectorParameter(Material material, uint hash) =>
      material.Parameters.TryGetValue(hash, out var param) && param is MaterialParamVector4 vector ? vector.Value : null;

    /// <summary>A texture of the model (from it or the dictionaries) as PNG under a key, listed; null if not found.</summary>
    private string? AddTexture(string name, TextureFile? embedded, string key)
    {
      // Another texture in its place where the caller wants one and it is there (a ped's texture
      // variation: uppr_diff_000_b_uni for uppr_diff_000_a_uni).
      if (_preferTexture?.Invoke(name) is string other && !other.Equals(name, StringComparison.OrdinalIgnoreCase) &&
          FindTexture(other, embedded, out _) != null)
      {
        name = other;
      }
      var texture = FindTexture(name, embedded, out string source);
      Listed(texture?.Name ?? name, source, texture).IsUsed = true;
      if (texture == null)
      {
        MissingTextures.Add(name);
        return null;
      }
      try
      {
        Textures[key] = Png(texture, source);
        return key;
      }
      catch (Exception)
      {
        return null;
      }
    }
  }

  public sealed class SceneMesh
  {
    public SceneMesh(int id, int material, float[] positions, float[]? normals, float[]? uvs, ushort[] indices, int[] bones)
    {
      Id = id;
      Material = material;
      Positions = positions;
      Normals = normals;
      Uvs = uvs;
      Indices = indices;
      Bones = bones;
    }

    /// <summary>The bone each vertex moves with (index into <see cref="ModelScene.Bones"/>; 0 without a skeleton), for the export.</summary>
    public int[] Bones { get; }

    /// <summary>For a skinned mesh (peds): four skeleton bones and their weights per vertex; null otherwise.</summary>
    public float[]? SkinBones { get; init; }
    public float[]? SkinWeights { get; init; }

    /// <summary>Terrain: the texture coordinates of layers 1 to 3 (6 per vertex), how much each layer covers
    /// (4 per vertex) and the vertex colour (3 per vertex); null otherwise.</summary>
    public float[]? LayerUvs { get; init; }
    public float[]? LayerWeights { get; init; }
    public float[]? Colors { get; init; }

    /// <summary>See-through materials and decals: each vertex's alpha (how much of it shows); null otherwise.</summary>
    public float[]? Alphas { get; init; }

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
      skinBones = SkinBones == null ? null : Base64(SkinBones),
      skinWeights = SkinWeights == null ? null : Base64(SkinWeights),
      layerUvs = LayerUvs == null ? null : Base64(LayerUvs),
      layerWeights = LayerWeights == null ? null : Base64(LayerWeights),
      colors = Colors == null ? null : Base64(Colors),
      alphas = Alphas == null ? null : Base64(Alphas),
      indices = Convert.ToBase64String(MemoryMarshal.AsBytes(Indices.AsSpan())),
    };

    private static string Base64(float[] values) => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
  }

  /// <summary>A light of the model: where, which way, its colour (0 to 1), a spot's outer angle (degrees from its axis), the meshes of its drawable.</summary>
  public sealed record SceneLight(System.Numerics.Vector3 Position, System.Numerics.Vector3 Direction, float Red, float Green, float Blue,
                                  bool Spot, float Range, float Falloff, int[] Meshes, float Distance = 0, float HotSpot = 0, float Intensity = 1)
  {
    /// <summary>
    /// When it is on (GTAIV.exe 0xC1F9A0, by the game clock's night factor N): 0x20 by day, 0x40 by night, both
    /// always; with 0x1 it fades (by day × (1 - N), by night × N); neither, never.
    /// </summary>
    public uint Flags { get; init; }

    /// <summary>For the page.</summary>
    public object ToMessage() => new
    {
      position = new[] { Position.X, Position.Y, Position.Z },
      direction = new[] { Direction.X, Direction.Y, Direction.Z },
      color = new[] { Red, Green, Blue },
      spot = Spot,
      range = Range,
      falloff = Falloff,
      meshes = Meshes,
    };
  }

  /// <summary>A bone: its place and turn relative to its parent (Euler angles in radians, as RageLib reads them).</summary>
  public sealed record SceneBone(int Index, string Name, int Parent, float X, float Y, float Z, float RotationX, float RotationY, float RotationZ)
  {
    /// <summary>The id animations refer to the bone by (as an unsigned 16-bit number, as in the tracks).</summary>
    public int Id { get; init; }

    /// <summary>The turn relative to the parent as a quaternion (x, y, z, w).</summary>
    public float[] Quaternion { get; init; } = { 0, 0, 0, 1 };
  }

  /// <summary>A texture of the model and where it comes from, with a thumbnail made while loading.</summary>
  public sealed class SceneTexture
  {
    internal SceneTexture(string name, string source, Texture? texture)
    {
      Name = name;
      Source = source;
      if (texture != null)
      {
        Detail = texture.Width + " × " + texture.Height + " · " + texture.TextureType;
        try
        {
          Thumbnail = TexturePixels.Thumbnail(texture, 40);
        }
        catch (Exception)
        {
          Thumbnail = TexturePixels.Blank;
        }
      }
    }

    public string Name { get; }

    /// <summary><see cref="ModelScene.EmbeddedSource"/>, the dictionary's file name, or <see cref="ModelScene.MissingSource"/>.</summary>
    public string Source { get; }

    public bool IsEmbedded => Source == ModelScene.EmbeddedSource;

    /// <summary>Whether a material uses it (a model may carry textures it doesn't use).</summary>
    public bool IsUsed { get; set; }

    public string Detail { get; } = "";

    internal TexturePixels? Thumbnail { get; }
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

    /// <summary>
    /// Terrain: the textures of the layers laid over the first one (layer 1 to 3; null where not found);
    /// empty for other materials.
    /// </summary>
    public IReadOnlyList<string?> LayerKeys { get; init; } = Array.Empty<string?>();

    /// <summary>How many texture layers it has: 1, or 2 to 4 for terrain.</summary>
    public int LayerCount => 1 + LayerKeys.Count;

    /// <summary>The normal map (bumps) and the specular map (where it shines), or null.</summary>
    public string? NormalKey { get; init; }
    public string? SpecularKey { get; init; }

    /// <summary>
    /// A map *_reflect shader's environment texture (GTA IV (PC): its EnvironmentSampler, added to the albedo by
    /// the specular map x specularColorFactor x reflectivePower), or null.
    /// </summary>
    public string? EnvironmentKey { get; init; }

    /// <summary>How strongly the normal map bends the surface (1 as made).</summary>
    public float Bumpiness { get; init; } = 1;

    /// <summary>How sharp the shine is (the exponent; 0 for no shine) and how bright.</summary>
    public float SpecularPower { get; init; }
    public float SpecularIntensity { get; init; }

    /// <summary>Which channels of the specular map hold the shine (usually red only).</summary>
    public float[] SpecularMask { get; init; } = { 1, 0, 0 };

    /// <summary>
    /// A dirt decal's (gta_decal_dirt): which texture channels say how much dirt there is (the game's
    /// pixel shader: alpha = dot(texture.rgb, mask) × vertex alpha, the colour the vertices'); null otherwise.
    /// </summary>
    public float[]? DirtMask { get; init; }

    /// <summary>
    /// A vehicle's material coloured by the vehicle (its matdiffusecolor (2, slot, slot)): which of its six
    /// colours (GTAIV.exe 0xD25D80: 1 to 4 its carcols colours, 5 white, 6 the third again); 0 for others.
    /// </summary>
    public int PaintSlot { get; init; }

    /// <summary>
    /// A vehicle's shader (gta_vehicle_*): lit as the game's (gta_vehicle_paint2's pixel shader: the texture
    /// mixed towards <see cref="DirtColor"/> by its dirt texture's red x the vehicle's dirt; the shine and the
    /// mirrored surroundings by <see cref="VehicleShine"/> and reflectivity).
    /// </summary>
    public bool Vehicle { get; init; }
    public string? DirtKey { get; init; }
    public float[] DirtColor { get; init; } = { 0.23f, 0.22f, 0.2f };
    public float ReflectivePower { get; init; }

    /// <summary>
    /// A vehicle's shader with a second shine (gta_vehicle_paint2: specular2ColorIntensityED ...). Its values are
    /// the vehicle's, set when the game makes it (see CutsceneMessages.ApplyPaint), not the model's.
    /// </summary>
    public bool HasSpecular2 { get; init; }

    /// <summary>
    /// A vehicle shader's shine numbers (intensity factor, power factor, 1 for the forward-drawn glass), read
    /// from its pixel programs (see GtaIV.Pc.GtaIVPcMaterials.VehicleShineOf); null if not known.
    /// </summary>
    public float[]? VehicleShine { get; init; }

    /// <summary>
    /// An emissive shader's brightness (gta_emissive*: texture x vertex colour x emissiveMultiplier, unlit), or
    /// null; and whether it only shines at night (gta_emissivenight: x the time cycle's day/night effects).
    /// </summary>
    public float? EmissiveMultiplier { get; init; }
    public bool EmissiveAtNight { get; init; }

    /// <summary>gta_ped_skin's light through the skin: SubColor (r, g, b), SubScatWrap, SubScatWidth; or null.</summary>
    public float[]? SubSurface { get; init; }

    /// <summary>For the page; <paramref name="url"/> makes a texture key the address the page asks for.</summary>
    public object ToMessage(Func<string, string> url) => new
    {
      id = Id,
      glass = Glass,
      cutout = Cutout,
      stipple = Stipple,
      blend = Blend,
      decal = Decal,
      hidden = Hidden,
      noWater = NoWater,
      emissive = Emissive,
      emissiveMultiplier = EmissiveMultiplier,
      emissiveAtNight = EmissiveAtNight,
      subSurface = SubSurface,
      texture = TextureKey == null ? null : url(TextureKey),
      color = Color ?? (RenderTarget ? new[] { 0f, 0f, 0f } : null),
      layers = LayerKeys.Select(k => k == null ? null : url(k)).ToArray(),
      normal = NormalKey == null ? null : url(NormalKey),
      specular = SpecularKey == null ? null : url(SpecularKey),
      environment = EnvironmentKey == null ? null : url(EnvironmentKey),
      bumpiness = Bumpiness,
      specularPower = SpecularPower,
      specularIntensity = SpecularIntensity,
      specularMask = SpecularMask,
      dirtMask = DirtMask,
      vehicle = Vehicle,
      dirtTexture = DirtKey == null ? null : url(DirtKey),
      dirtColor = DirtColor,
      reflectivePower = ReflectivePower,
      vehicleShine = VehicleShine,
    };

    /// <summary>The material's draw bucket as the model stores it (what it means is the game's: see GtaIV.Pc.GtaIVPcMaterials).</summary>
    public int DrawBucket { get; init; }

    // How it is drawn, set by the game's rules (GtaIV.Pc.GtaIVPcMaterials.DrawingOf for GTA IV (PC)).

    /// <summary>Whether see-through texels are cut out (leaves, fences, hair).</summary>
    public bool Cutout { get; init; }

    /// <summary>Whether it is drawn over what is behind it by its texture's alpha (see-through, decals); glass has its own way.</summary>
    public bool Blend { get; init; }

    /// <summary>Whether only part of its pixels are kept by alpha before blending (a stipple pattern), as the game draws peds' see-through parts.</summary>
    public bool Stipple { get; init; }

    /// <summary>Whether it lies on another surface (a puddle, dirt, a poster): drawn a little in front of it.</summary>
    public bool Decal { get; init; }

    /// <summary>
    /// A layer showing a render target (script_rt: a TV's picture), which no file holds: black, as the game's
    /// while no script draws on it (a TV switched off; under it GB_tv07 has its cracked screen, for when it
    /// is shot).
    /// </summary>
    public bool RenderTarget { get; init; }

    /// <summary>A colour of its own (0 to 1) for a material without a texture (collision surfaces); null for the page's own.</summary>
    public float[]? Color { get; init; }

    /// <summary>Planes the game doesn't draw (a vehicle's no-splash and no-water planes).</summary>
    public bool Hidden { get; init; }

    /// <summary>A plane that keeps the water out where it is (a boat's cockpit), not drawn itself.</summary>
    public bool NoWater { get; init; }

    /// <summary>Glowing (lamp shades, signs, windows at night): shown at full brightness, not lit.</summary>
    public bool Emissive { get; init; }
  }
}
