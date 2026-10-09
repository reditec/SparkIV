using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RageLib.GtaIV.Pc.Animation;
using SparkIV.WinUI.Model;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A cutscene (.cut, text): its sections, each played from one animation dictionary beside it (b_1.cut:
  /// b_1_0.wad, b_1_1.wad ...) with a clip for every model (CS_PhilB_0), its face (cs_philb_head_0) and the
  /// camera (camera_0); the subtitles' keys and times over the whole scene.
  /// </summary>
  public sealed class CutFile
  {
    public List<CutSection> Sections { get; } = new List<CutSection>();
    public List<CutSubtitle> Subtitles { get; } = new List<CutSubtitle>();

    /// <summary>The text table the subtitles are in (B1AUD), or null.</summary>
    public string? MissionText { get; private set; }

    public List<string> Flags { get; } = new List<string>();

    /// <summary>
    /// Seconds the game fades in from black at the start: none with FLAG_DONT_FADE_IN. How long isn't in the
    /// file (the game's own); assumed.
    /// </summary>
    public float FadeIn => HasFlag("DONT_FADE_IN") ? 0f : 0.5f;

    /// <summary>
    /// Seconds the game fades out to black at the end: LONG_FADE_OUT, SHORT_FADE_OUT, none with
    /// FLAG_DONT_FADE_OUT, else its usual one. How long isn't in the file; assumed.
    /// </summary>
    public float FadeOut => HasFlag("DONT_FADE_OUT") ? 0f : HasFlag("LONG_FADE_OUT") ? 2f : HasFlag("SHORT_FADE_OUT") ? 0.5f : 1f;

    // Flags come with and without FLAG_ (FLAG_USE_ONE_AUDIO, USE_ONE_AUDIO).
    private bool HasFlag(string name) => Flags.Any(f => f.EndsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The map objects hidden while the scene plays (the real door where the scene's own one moves).</summary>
    public List<CutRemoval> Removals { get; } = new List<CutRemoval>();

    /// <summary>Which variation of a ped's parts is worn (CS_Bernie_CraneT: uppr_001, its uppr_000 an empty stand-in).</summary>
    public List<CutVariation> Variations { get; } = new List<CutVariation>();

    /// <summary>The props the peds wear (bc_3: Bernie's hat and glasses).</summary>
    public List<CutProp> Props { get; } = new List<CutProp>();

    /// <summary>Where the scene is in the world (its origin), and its turn about Z in degrees.</summary>
    public float[]? Offset { get; private set; }
    public float Orient { get; private set; }

    /// <summary>
    /// How far the game draws the world during the scene, in metres: the largest of its [DRAW_DISTANCE] lines
    /// ("time section metres": bc_3 800, later 200); 0 without any.
    /// </summary>
    public float DrawDistance { get; private set; }

    /// <summary>
    /// How far the game draws, shot by shot: from a time (milliseconds into the file's part, its sections one
    /// after the other) the metres it draws (bc_3: 800 at the start, 89.2 in its last shot from the boat,
    /// where the city's towers aren't drawn).
    /// </summary>
    public List<(int Part, float Ms, float Metres)> DrawDistances { get; } = new List<(int, float, float)>();

    /// <summary>
    /// [VEHICLE_DETAILS]: a vehicle's colours (four carcols.dat colour numbers) and dirt (0 to 15) by its model number, in the
    /// order of the file (the game takes the first for a model: a cutscene object is coloured once).
    /// </summary>
    public List<(int Part, int Model, int[] Colours, int Dirt)> VehicleDetails { get; } = new List<(int, int, int[], int)>();

    /// <summary>
    /// A section's draw distances by its frames: [frame, metres] from the one in force at its start on (its
    /// start the durations of the sections before it in its part); empty without any.
    /// </summary>
    public List<float[]> DrawDistanceSteps(int sectionIndex, float fps)
    {
      var steps = new List<float[]>();
      if (sectionIndex < 0 || sectionIndex >= Sections.Count)
      {
        return steps;
      }
      var section = Sections[sectionIndex];
      float start = Sections.Take(sectionIndex).Where(s => s.Part == section.Part).Sum(s => s.Duration) * 1000;
      float end = start + section.Duration * 1000;
      var mine = DrawDistances.Where(d => d.Part == section.Part).OrderBy(d => d.Ms).ToList();
      var before = mine.LastOrDefault(d => d.Ms <= start);
      if (before.Metres > 0)
      {
        steps.Add(new[] { 0f, before.Metres });
      }
      foreach (var d in mine.Where(d => d.Ms > start && d.Ms < end))
      {
        steps.Add(new[] { (d.Ms - start) / 1000 * fps, d.Metres });
      }
      return steps;
    }

    public static CutFile Parse(string text)
    {
      var cut = new CutFile();
      CutSection? section = null;
      string? block = null;
      // The file's part: a .cut can be several scenes one after the other (gt06_ba: its own three sections,
      // then gt6bap2's), each from its [CUTSCENE_HEADER] with its own variations, props and model numbers.
      int part = -1;
      foreach (string raw in text.Split('\n'))
      {
        string line = raw.Trim();
        if (line.Length == 0)
        {
          continue;
        }
        if (line == "[SECTION_START]")
        {
          section = new CutSection { Part = Math.Max(0, part) };
          cut.Sections.Add(section);
          continue;
        }
        if (line == "[SECTION_END]")
        {
          section = null;
          continue;
        }
        if (line.StartsWith("[/", StringComparison.Ordinal))
        {
          block = null;
          continue;
        }
        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
        {
          block = line.Substring(1, line.Length - 2);
          if (block == "CUTSCENE_HEADER")
          {
            part++;
          }
          continue;
        }
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        switch (block)
        {
          case "MISSION_TEXT_NAME":
            cut.MissionText = line;
            break;
          case "TEXT" when parts.Length >= 3:
            cut.Subtitles.Add(new CutSubtitle(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]));
            break;
          case "FLAGS":
            cut.Flags.Add(line);
            break;
          case "VARIATION" when parts.Length >= 3:
            cut.Variations.Add(new CutVariation(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                                                int.Parse(parts[2], CultureInfo.InvariantCulture),
                                                parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : 0,
                                                parts.Length > 4 ? int.Parse(parts[4], CultureInfo.InvariantCulture) : 0, Math.Max(0, part)));
            break;
          case "PROPS" when parts.Length >= 3:
            cut.Props.Add(new CutProp(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                                      int.Parse(parts[2], CultureInfo.InvariantCulture), Math.Max(0, part)));
            break;
          case "MODELS" when section != null && parts.Length >= 3:
            section.Models.Add(new CutModel(int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], parts[2], parts.Length > 3 ? parts[3] : null));
            break;
          case "OFFSET" when parts.Length >= 3:
            var offset = new[] { F(parts[0]), F(parts[1]), F(parts[2]) };
            if (section != null) section.Offset = offset;
            cut.Offset ??= offset;
            break;
          case "ORIENT":
            if (section != null) section.Orient = F(parts[0]);
            if (cut.Sections.Count <= 1) cut.Orient = F(parts[0]);
            break;
          case "DURATION" when section != null:
            section.Duration = F(parts[0]) / 1000f;
            break;
          case "ANIM" when section != null:
            section.Anim = line;
            break;
          case "CAMERA" when section != null:
            section.Camera = line;
            break;
          case "AUDIO" when section != null:
            section.Audio = line;
            break;
          case "LIGHTS" when section != null:
            section.Lights.Add(line);
            break;
          case "DRAW_DISTANCE" when parts.Length >= 3:
            cut.DrawDistance = Math.Max(cut.DrawDistance, F(parts[2]));
            cut.DrawDistances.Add((Math.Max(0, part), F(parts[0]), F(parts[2])));
            break;
          case "VEHICLE_DETAILS" when parts.Length >= 5:
            cut.VehicleDetails.Add((Math.Max(0, part), int.Parse(parts[0], CultureInfo.InvariantCulture),
                                    parts.Skip(1).Take(4).Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray(),
                                    parts.Length > 5 ? Math.Clamp(int.Parse(parts[5], CultureInfo.InvariantCulture), 0, 15) : 0));
            break;
          case "VEHICLE_REMOVAL" when section != null && parts.Length >= 2:
            section.VehicleRemovals.Add((int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)));
            break;
          case "REMOVE" when parts.Length >= 4:
            var removal = new CutRemoval(F(parts[0]), F(parts[1]), F(parts[2]), parts[3]);
            if (!cut.Removals.Contains(removal)) cut.Removals.Add(removal);
            break;
        }
      }
      return cut;
    }
  }

  public sealed class CutSection
  {
    /// <summary>The part of the file it is in (0 for most; see <see cref="CutFile.Parse"/>).</summary>
    public int Part { get; set; }

    public List<CutModel> Models { get; } = new List<CutModel>();

    /// <summary>The scene's own lights: their clips in the section's dictionary (spt_dr_0, fil_red_0).</summary>
    public List<string> Lights { get; } = new List<string>();

    /// <summary>The animation dictionary (B_1_0: b_1_0.wad).</summary>
    public string? Anim { get; set; }

    /// <summary>The camera's clip in it (camera_0).</summary>
    public string? Camera { get; set; }
    public string? Audio { get; set; }

    /// <summary>Seconds.</summary>
    public float Duration { get; set; }

    /// <summary>
    /// [VEHICLE_REMOVAL]: parts of a model taken off in this section, by the model's number and a bone's id
    /// (a vehicle's extras: bc_3 removes four of the boat's five tops).
    /// </summary>
    public List<(int Model, int BoneId)> VehicleRemovals { get; } = new List<(int, int)>();
    public float[]? Offset { get; set; }
    public float Orient { get; set; }
  }

  /// <summary>A model in a section: which (CS_PhilB, player, csOldPhone), its clip there, its face's clip.</summary>
  public sealed record CutModel(int Id, string Model, string Clip, string? HeadClip);

  /// <summary>
  /// A line of [VARIATION]: a model's part (0 head, 1 uppr, 2 lowr, 3 suse, 4 hand, 5 feet, 6 jack, 7 hair,
  /// 8 sus2, 9 teef, 10 face; the game's order of ped components), the variation of its model (uppr_001)
  /// and of its texture (a, b, c ...), from when (milliseconds; mostly 0, some change during the scene).
  /// </summary>
  public sealed record CutVariation(int Model, int Component, int Drawable, int Texture, int Time, int Part = 0)
  {
    internal static readonly string[] Components = { "head", "uppr", "lowr", "suse", "hand", "feet", "jack", "hair", "sus2", "teef", "face" };

    /// <summary>The part's name ("uppr"), or null for a number not known.</summary>
    public string? ComponentName => Component >= 0 && Component < Components.Length ? Components[Component] : null;
  }

  /// <summary>
  /// A line of [PROPS]: a model wears a prop of its ped's prop dictionary (peds.ide's second column, in
  /// pedprops.img): its number and where (the anchor: 0 head, 1 eyes ... <see cref="PedComponentNames.PropAnchors"/>);
  /// bc_3's "2 0 0" and "2 0 1" are Bernie's p_head_000 (his hat) and p_eyes_000 (his glasses).
  /// </summary>
  public sealed record CutProp(int Model, int Index, int Anchor, int Part = 0)
  {
    /// <summary>The prop's part in the dictionary (p_head_000), or null for an anchor not known.</summary>
    public string? PartName => Anchor >= 0 && Anchor < PedComponentNames.PropAnchors.Length
      ? "p_" + PedComponentNames.PropAnchors[Anchor] + "_" + Index.ToString("000", CultureInfo.InvariantCulture)
      : null;
  }

  /// <summary>A map object hidden during the scene: where it is in the world (to a tenth of a metre), its model.</summary>
  public sealed record CutRemoval(float X, float Y, float Z, string Model);

  /// <summary>A subtitle: from when, for how long (milliseconds over the whole scene), its key in the text table.</summary>
  public sealed record CutSubtitle(int Start, int Length, string Key);

  /// <summary>One of the cast: the model it is, loaded (or why not), with its number in the .cut.</summary>
  public sealed class CutActor
  {
    internal CutActor(int id, string model)
    {
      Id = id;
      Model = model;
    }

    /// <summary>Its number on the page, from 1 (0 is no actor there).</summary>
    public int Id { get; }
    public string Model { get; }

    /// <summary>The part of the .cut it is in and its model number there (what its sections, variations and props name it by).</summary>
    public int Part { get; init; }
    public int Number { get; init; }
    public ModelScene? Scene { get; set; }
    public string? Source { get; set; }
    public string? Error { get; set; }

    /// <summary>The texture variation of each part the cutscene asks for ("uppr" → 1: uppr_diff_…_b_…).</summary>
    public Dictionary<string, int> TextureVariations { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The texture of the variation asked for in place of a ped texture's name (uppr_diff_001_a_uni →
    /// uppr_diff_001_b_uni), or null.
    /// </summary>
    public string? VariationOf(string texture)
    {
      var match = System.Text.RegularExpressions.Regex.Match(texture, @"^([a-z0-9]+)_diff_(\d{3})_([a-z])(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      if (!match.Success || !TextureVariations.TryGetValue(match.Groups[1].Value, out int variation) || variation <= 0 || variation > 25)
      {
        return null;
      }
      // The letter swapped, in the name's own case: "uppr_diff_001_" + "b" + "_uni".
      bool upper = char.IsUpper(match.Groups[3].Value[0]);
      char letter = (char)((upper ? 'A' : 'a') + variation);
      int at = match.Groups[3].Index;
      return texture.Substring(0, at) + letter + texture.Substring(at + 1);
    }

    /// <summary>A ped (placed in the scene by its clip's model tracks) rather than a prop (by its root bone).</summary>
    public bool IsPed => Scene != null && Scene.Bones.Count > 40;

    /// <summary>
    /// A vehicle's six colours (RGB 0 to 1) its materials' paint slots take (see SceneMaterial.PaintSlot), or null.
    /// </summary>
    public float[][]? Paint { get; set; }

    /// <summary>A vehicle's dirt, 0 to 1 (the .cut's [VEHICLE_DETAILS] 0 to 15).</summary>
    public float Dirt { get; set; }

    /// <summary>
    /// A vehicle's shine as the game sets it on its shaders when it makes the vehicle (GTAIV.exe 0xD25040,
    /// not the model's values): specularFactorED, specularColorFactorED, specular2FactorED,
    /// specular2ColorIntensityED, reflectivePowerED; or null.
    /// </summary>
    public float[]? Shine { get; set; }

    /// <summary>The ped's prop dictionary (CS_Bernie_Crane_p), loaded when the cutscene has it wear some, or null.</summary>
    public ModelScene? PropScene { get; set; }

    /// <summary>The props worn: the part in <see cref="PropScene"/> and the skeleton's bone it is fixed to.</summary>
    public List<(ModelPart Part, int Bone)> Props { get; } = new List<(ModelPart, int)>();
  }

  /// <summary>
  /// What the page gets for a cutscene: all actors' meshes in one scene (each mesh tagged with its actor,
  /// its textures under the actor's number), and per section every actor's clip and the camera's.
  /// </summary>
  public static class CutsceneMessages
  {
    public static float FramesPerSecond(Animation clip) =>
      clip.Duration > 0 && clip.FrameCount > 1 ? (clip.FrameCount - 1) / clip.Duration : 30;

    /// <summary>A texture's key for the page: the actor's number, then its key in the actor's scene.</summary>
    public static string TextureKey(int actor, string key) => "a" + actor + "/" + key;

    private const string PropKey = "p/";

    /// <summary>The texture behind a key from <see cref="TextureKey"/>, or null.</summary>
    public static byte[]? Texture(IEnumerable<CutActor> actors, string key)
    {
      int slash = key.IndexOf('/');
      if (slash < 2 || !int.TryParse(key.AsSpan(1, slash - 1), out int id))
      {
        return null;
      }
      var actor = actors.FirstOrDefault(a => a.Id == id);
      string rest = key.Substring(slash + 1);
      var scene = actor?.Scene;
      // The props' textures under "p/".
      if (rest.StartsWith(PropKey, StringComparison.Ordinal))
      {
        scene = actor?.PropScene;
        rest = rest.Substring(PropKey.Length);
      }
      return scene != null && scene.Textures.TryGetValue(rest, out var png) ? png : null;
    }

    /// <summary>The "scene" message (all actors) and the meshes shown.</summary>
    public static (JsonObject Scene, List<int> Visible) Scene(IEnumerable<CutActor> actors, Func<string, string> url)
    {
      var materials = new JsonArray();
      var meshes = new JsonArray();
      var visible = new List<int>();
      int nextMaterial = 0, nextMesh = 0;
      foreach (var actor in actors)
      {
        var scene = actor.Scene;
        if (scene == null)
        {
          continue;
        }
        int firstMaterial = nextMaterial;
        foreach (var material in scene.Materials)
        {
          var node = JsonSerializer.SerializeToNode(material.ToMessage(key => url(TextureKey(actor.Id, key))))!.AsObject();
          node["id"] = firstMaterial + material.Id;
          if (actor.Paint != null && material.PaintSlot > 0)
          {
            node["paint"] = new JsonArray(actor.Paint[material.PaintSlot - 1].Select(c => (JsonNode)c).ToArray());
          }
          if (material.Vehicle)
          {
            node["dirtLevel"] = actor.Dirt;
            if (actor.Shine is float[] shine)
            {
              node["reflectivePower"] = shine[4];
              // The second shine (gta_vehicle_paint2's deferred pixel shader): the third colour (slot 6), the
              // exponent's factor 6.8 x specular2FactorED / specularFactorED, the strength's
              // specular2ColorIntensityED² / specularColorFactorED (both then x spec map and reflectivity).
              if (material.HasSpecular2 && actor.Paint != null)
              {
                node["specular2"] = new JsonArray(actor.Paint[5][0], actor.Paint[5][1], actor.Paint[5][2],
                                                  6.8f * shine[2] / shine[0], shine[3] * shine[3] / shine[1]);
              }
            }
          }
          materials.Add(node);
          nextMaterial = Math.Max(nextMaterial, firstMaterial + material.Id + 1);
        }
        var shown = scene.Root.CheckedMeshes().ToHashSet();
        foreach (var mesh in scene.Meshes)
        {
          var node = JsonSerializer.SerializeToNode(mesh.ToMessage())!.AsObject();
          int id = nextMesh++;
          node["id"] = id;
          node["material"] = firstMaterial + mesh.Material;
          node["actor"] = actor.Id;
          // Peds are lit as they are; props (a door) as the location is (no light of the camera).
          node["ped"] = actor.IsPed;
          meshes.Add(node);
          if (shown.Contains(mesh.Id))
          {
            visible.Add(id);
          }
        }
      }
      // The props worn, after everyone's own meshes (their numbers stay as they were): each fixed to its
      // bone, made where that bone is at rest (the dictionary's meshes are in the bone's own space: Bernie's
      // glasses 3 to 8 cm before his head bone), so the page moves it with the bone as it moves the skin.
      foreach (var actor in actors)
      {
        if (actor.Scene == null || actor.PropScene == null || actor.Props.Count == 0)
        {
          continue;
        }
        int firstMaterial = nextMaterial;
        foreach (var material in actor.PropScene.Materials)
        {
          var node = JsonSerializer.SerializeToNode(material.ToMessage(key => url(TextureKey(actor.Id, PropKey + key))))!.AsObject();
          node["id"] = firstMaterial + material.Id;
          materials.Add(node);
          nextMaterial = Math.Max(nextMaterial, firstMaterial + material.Id + 1);
        }
        var rest = RestOf(actor.Scene.Bones);
        foreach (var (part, bone) in actor.Props)
        {
          var ids = part.CheckedMeshes().ToHashSet();
          foreach (var mesh in actor.PropScene.Meshes.Where(m => ids.Contains(m.Id)))
          {
            var node = JsonSerializer.SerializeToNode(mesh.ToMessage())!.AsObject();
            int id = nextMesh++;
            node["id"] = id;
            node["material"] = firstMaterial + mesh.Material;
            node["actor"] = actor.Id;
            node["ped"] = true;
            var place = rest[bone];
            node["positions"] = Floats(Moved(mesh.Positions, place, true));
            if (mesh.Normals != null)
            {
              node["normals"] = Floats(Moved(mesh.Normals, place, false));
            }
            int count = mesh.Positions.Length / 3;
            var bones = new float[count * 4];
            var weights = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
              bones[i * 4] = bone;
              weights[i * 4] = 1;
            }
            node["skinBones"] = Floats(bones);
            node["skinWeights"] = Floats(weights);
            meshes.Add(node);
            visible.Add(id);
          }
        }
      }
      return (new JsonObject { ["type"] = "scene", ["materials"] = materials, ["meshes"] = meshes }, visible);
    }

    // Where each bone of a skeleton is at rest, in the model: its place and turn on its parent's, as the page
    // makes them (world = local × parent's world).
    private static System.Numerics.Matrix4x4[] RestOf(IReadOnlyList<SceneBone> bones)
    {
      var byIndex = bones.ToDictionary(b => b.Index);
      var world = new System.Numerics.Matrix4x4?[bones.Count == 0 ? 0 : bones.Max(b => b.Index) + 1];
      System.Numerics.Matrix4x4 Of(int i)
      {
        if (world[i] is System.Numerics.Matrix4x4 known)
        {
          return known;
        }
        var b = byIndex[i];
        var q = new System.Numerics.Quaternion(b.Quaternion[0], b.Quaternion[1], b.Quaternion[2], b.Quaternion[3]);
        var local = System.Numerics.Matrix4x4.CreateFromQuaternion(q) * System.Numerics.Matrix4x4.CreateTranslation(b.X, b.Y, b.Z);
        var m = b.Parent >= 0 && b.Parent != i && byIndex.ContainsKey(b.Parent) ? local * Of(b.Parent) : local;
        world[i] = m;
        return m;
      }
      return Enumerable.Range(0, world.Length).Select(i => byIndex.ContainsKey(i) ? Of(i) : System.Numerics.Matrix4x4.Identity).ToArray();
    }

    private static float[] Moved(float[] values, System.Numerics.Matrix4x4 place, bool isPoint)
    {
      var result = new float[values.Length];
      for (int i = 0; i + 2 < values.Length; i += 3)
      {
        var v = new System.Numerics.Vector3(values[i], values[i + 1], values[i + 2]);
        var t = isPoint ? System.Numerics.Vector3.Transform(v, place) : System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(v, place));
        result[i] = t.X;
        result[i + 1] = t.Y;
        result[i + 2] = t.Z;
      }
      return result;
    }

    private static string Floats(float[] values) => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));

    /// <summary>A track's values at every frame (3 or 4 a frame).</summary>
    private static float[] SampledValues(Animation clip, AnimationTrack track, int components)
    {
      int frames = Math.Max(1, clip.FrameCount);
      var values = new float[frames * components];
      for (int f = 0; f < frames; f++)
      {
        var value = track.GetValue(f, clip.SequenceFrameLimit);
        for (int c = 0; c < components && value != null && c < value.Length; c++)
        {
          values[f * components + c] = value[c];
        }
      }
      return values;
    }

    /// <summary>
    /// Tracks made for another parent than the model's skeleton has: a vehicle's clip moves its wheels as
    /// children of the suspension (banshee_0: wheel_lf at -0.44, 0.05, 0.13 from suspension_lf), the
    /// skeleton has them under the chassis beside it. Such a bone's track is the place where the sibling's
    /// place plus the track's value is the bone's rest (within 2 cm), the value itself far from it; it is
    /// turned into the place and turn under the real parent, frame by frame (sibling × track).
    /// </summary>
    private static void ReparentToSiblings(List<SceneBone> bones, Dictionary<int, float[]> positions, Dictionary<int, float[]> rotations)
    {
      static System.Numerics.Vector3 P(float[] v, int f) => new System.Numerics.Vector3(v[f * 3], v[f * 3 + 1], v[f * 3 + 2]);
      static System.Numerics.Quaternion Q(float[] v, int f) => new System.Numerics.Quaternion(v[f * 4], v[f * 4 + 1], v[f * 4 + 2], v[f * 4 + 3]);
      var byIndex = bones.ToDictionary(b => b.Index);
      var moves = new List<(int Bone, int Sibling)>();
      foreach (var (bone, values) in positions)
      {
        if (!byIndex.TryGetValue(bone, out var b))
        {
          continue;
        }
        var rest = new System.Numerics.Vector3(b.X, b.Y, b.Z);
        var first = P(values, 0);
        if (System.Numerics.Vector3.Distance(first, rest) < 0.05f)
        {
          continue;
        }
        foreach (var (sibling, siblingValues) in positions)
        {
          if (sibling == bone || !byIndex.TryGetValue(sibling, out var s) || s.Parent != b.Parent || moves.Any(m => m.Bone == sibling))
          {
            continue;
          }
          var turn = rotations.TryGetValue(sibling, out var r) ? Q(r, 0) : new System.Numerics.Quaternion(s.Quaternion[0], s.Quaternion[1], s.Quaternion[2], s.Quaternion[3]);
          if (System.Numerics.Vector3.Distance(P(siblingValues, 0) + System.Numerics.Vector3.Transform(first, turn), rest) < 0.02f)
          {
            moves.Add((bone, sibling));
            break;
          }
        }
      }
      foreach (var (bone, sibling) in moves)
      {
        var place = positions[bone];
        var siblingPlace = positions[sibling];
        var b = byIndex[bone];
        var s = byIndex[sibling];
        int frames = Math.Min(place.Length / 3, siblingPlace.Length / 3);
        rotations.TryGetValue(bone, out var turnValues);
        rotations.TryGetValue(sibling, out var siblingTurns);
        var newTurns = new float[frames * 4];
        for (int f = 0; f < frames; f++)
        {
          var siblingTurn = siblingTurns != null && f * 4 + 3 < siblingTurns.Length ? Q(siblingTurns, f)
                            : new System.Numerics.Quaternion(s.Quaternion[0], s.Quaternion[1], s.Quaternion[2], s.Quaternion[3]);
          var ownTurn = turnValues != null && f * 4 + 3 < turnValues.Length ? Q(turnValues, f)
                        : new System.Numerics.Quaternion(b.Quaternion[0], b.Quaternion[1], b.Quaternion[2], b.Quaternion[3]);
          var at = P(siblingPlace, f) + System.Numerics.Vector3.Transform(P(place, f), siblingTurn);
          place[f * 3] = at.X;
          place[f * 3 + 1] = at.Y;
          place[f * 3 + 2] = at.Z;
          // q = sibling × own (the own turn applied first, in the sibling's frame).
          var turned = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(ownTurn, siblingTurn));
          newTurns[f * 4] = turned.X;
          newTurns[f * 4 + 1] = turned.Y;
          newTurns[f * 4 + 2] = turned.Z;
          newTurns[f * 4 + 3] = turned.W;
        }
        rotations[bone] = newTurns;
      }
    }

    /// <summary>A track's values at every frame (3 or 4 a frame), as the page takes them.</summary>
    public static string Sampled(Animation clip, AnimationTrack track, int components)
    {
      int frames = Math.Max(1, clip.FrameCount);
      var values = new float[frames * components];
      for (int f = 0; f < frames; f++)
      {
        var value = track.GetValue(f, clip.SequenceFrameLimit);
        for (int c = 0; c < components && value != null && c < value.Length; c++)
        {
          values[f * components + c] = value[c];
        }
      }
      return Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    /// <summary>One number a frame (the camera's field of view).</summary>
    private static string SampledScalar(Animation clip, AnimationTrack track)
    {
      int frames = Math.Max(1, clip.FrameCount);
      var values = new float[frames];
      for (int f = 0; f < frames; f++)
      {
        var value = track.GetValue(f, clip.SequenceFrameLimit);
        values[f] = value != null && value.Length > 0 ? value[0] : 0;
      }
      return Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    /// <summary>
    /// A section's messages: the timeline (a clip without tracks, as long as the section), the cast with
    /// each actor's skeleton and clip, the camera. Notes say what was missing.
    /// </summary>
    /// <param name="earlier">The sections played before it: their [VEHICLE_REMOVAL] parts stay off (see below).</param>
    public static (object Timeline, object Cast, object? Camera, List<string> Notes) Section(CutSection section, IReadOnlyList<CutActor> actors, AnimationFile? wad,
                                                                                         IEnumerable<CutSection>? earlier = null)
    {
      var notes = new List<string>();
      var clips = wad?.Animations.ToDictionary(a => a.ShortName, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, Animation>(StringComparer.OrdinalIgnoreCase);
      if (wad == null)
      {
        notes.Add((section.Anim ?? "the section's animations") + ".wad isn't beside the file");
      }
      int frames = 1;
      float fps = 30;
      var anyClip = wad?.Animations.FirstOrDefault();
      if (anyClip != null)
      {
        frames = Math.Max(1, anyClip.FrameCount);
        fps = FramesPerSecond(anyClip);
      }
      else if (section.Duration > 0)
      {
        frames = (int)(section.Duration * fps) + 1;
      }

      var cast = new List<object>();
      foreach (var model in section.Models)
      {
        var actor = ActorOf(actors, section, model);
        var scene = actor?.Scene;
        if (actor == null || scene == null)
        {
          continue;
        }
        var bones = scene.Bones.OrderBy(b => b.Index).ToList();
        object? skeleton = bones.Count == 0 ? null : new
        {
          parent = bones.Select(b => b.Parent),
          position = bones.SelectMany(b => new[] { b.X, b.Y, b.Z }),
          rotation = bones.SelectMany(b => b.Quaternion),
          names = bones.Select(b => b.Name),
        };
        var boneOfId = bones.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Index);
        clips.TryGetValue(model.Clip, out var clip);
        Animation? head = model.HeadClip != null && clips.TryGetValue(model.HeadClip, out var h) ? h : null;
        if (clip == null)
        {
          notes.Add("no clip " + model.Clip);
        }
        var tracks = new List<object>();
        object? root = null;
        if (clip != null)
        {
          // The face's clip moves the face's bones of the same skeleton, frame for frame with the body's.
          var positions = new Dictionary<int, float[]>();
          var rotations = new Dictionary<int, float[]>();
          foreach (var source in head == null ? new[] { clip } : new[] { clip, head })
          {
            foreach (var track in source.Tracks)
            {
              bool position = track.Kind == AnimationTrackKind.BonePosition, rotation = track.Kind == AnimationTrackKind.BoneRotation;
              if ((!position && !rotation) || !track.IsDecoded || !boneOfId.TryGetValue(track.BoneId, out int bone))
              {
                continue;
              }
              (position ? positions : rotations)[bone] = SampledValues(source, track, position ? 3 : 4);
            }
          }
          ReparentToSiblings(bones, positions, rotations);
          foreach (var (bone, values) in positions)
          {
            tracks.Add(new { bone, kind = "position", values = Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan())) });
          }
          foreach (var (bone, values) in rotations)
          {
            tracks.Add(new { bone, kind = "rotation", values = Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan())) });
          }
          var rootPosition = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.ModelPosition && t.IsDecoded);
          var rootRotation = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.ModelRotation && t.IsDecoded);
          if (rootPosition != null || rootRotation != null)
          {
            root = new
            {
              position = rootPosition == null ? null : new { bone = 0, kind = "position", values = Sampled(clip, rootPosition, 3) },
              rotation = rootRotation == null ? null : new { bone = 0, kind = "rotation", values = Sampled(clip, rootRotation, 4) },
            };
          }
        }
        // The bones the section's [VEHICLE_REMOVAL] lines take off this model: the game sets their matrices to
        // nothing while drawing it (GTAIV.exe 0xC66BE0 / 0xC66F80), so what hangs on them isn't seen (bc_3: of the
        // boat's five tops, extra_1 to extra_4 removed in its first section, an open boat left).
        // Those of the sections before stay off: the game zeroes them in the vehicle's own skeleton, which
        // nothing sets again (its extras have no tracks) and which the scene keeps from section to section (bc_3:
        // extra_3, the top's frame, removed in the first section only, isn't there in the game's second either).
        var removals = section.VehicleRemovals.Where(r => r.Model == model.Id).Select(r => r.BoneId).ToList();
        foreach (var before in earlier ?? Enumerable.Empty<CutSection>())
        {
          removals.AddRange(before.VehicleRemovals.Where(r => before.Models.FirstOrDefault(m => m.Id == r.Model) is CutModel same &&
                                                              ReferenceEquals(ActorOf(actors, before, same), actor))
                                                  .Select(r => r.BoneId));
        }
        var removed = removals.Where(boneOfId.ContainsKey).Select(id => boneOfId[id]).Distinct().ToList();
        cast.Add(new
        {
          id = actor.Id,
          skeleton,
          clip = clip == null ? null : new { frames = Math.Max(1, clip.FrameCount), fps = FramesPerSecond(clip), tracks },
          root,
          removedBones = removed,
        });
      }

      object? camera = null;
      if (section.Camera != null && clips.TryGetValue(section.Camera, out var cameraClip))
      {
        var position = cameraClip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.CameraPosition && t.IsDecoded);
        var rotation = cameraClip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.CameraRotation && t.IsDecoded);
        var fov = cameraClip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.CameraFov && t.IsDecoded);
        camera = new
        {
          frames = Math.Max(1, cameraClip.FrameCount),
          fps = FramesPerSecond(cameraClip),
          position = position == null ? null : Sampled(cameraClip, position, 3),
          rotation = rotation == null ? null : Sampled(cameraClip, rotation, 4),
          fov = fov == null ? null : SampledScalar(cameraClip, fov),
        };
      }
      else if (section.Camera != null)
      {
        notes.Add("no camera clip " + section.Camera);
      }

      // The scene's lights, each a clip: place, colour, range, strength, cone, direction per frame.
      var lights = new List<object>();
      foreach (string name in section.Lights)
      {
        if (!clips.TryGetValue(name, out var light))
        {
          continue;
        }
        AnimationTrack? Track(AnimationTrackKind kind) => light.Tracks.FirstOrDefault(t => t.Kind == kind && t.IsDecoded);
        var place = Track(AnimationTrackKind.BonePosition);
        if (place == null)
        {
          continue;
        }
        string? Scalar(AnimationTrackKind kind) => Track(kind) is AnimationTrack t ? SampledScalar(light, t) : null;
        string? Vector(AnimationTrackKind kind) => Track(kind) is AnimationTrack t ? Sampled(light, t, 3) : null;
        lights.Add(new
        {
          name,
          frames = Math.Max(1, light.FrameCount),
          fps = FramesPerSecond(light),
          position = Sampled(light, place, 3),
          color = Vector(AnimationTrackKind.LightColor),
          range = Scalar(AnimationTrackKind.LightRange),
          intensity = Scalar(AnimationTrackKind.LightIntensity1),
          cone = Scalar(AnimationTrackKind.LightIntensity2),
          direction = Vector(AnimationTrackKind.LightDirection),
        });
      }

      var timeline = new { type = "clip", frames, fps, tracks = Array.Empty<object>() };
      return (timeline, new { type = "cast", actors = cast, lights }, camera == null ? null : new { type = "camera", camera }, notes);
    }

    // The parts of the player shown, one of each: the first variation there is (head_000_r before head_001_r).
    private static readonly string[] PlayerParts = { "head", "uppr", "lowr", "feet", "hand", "hair", "teef" };

    /// <summary>
    /// The archives a cutscene's models are looked for in, in this order, for a game's folder (the one
    /// holding pc\) and GTA IV's (null for GTA IV itself): its cutsprops.img, then GTA IV's (the episodes'
    /// scenes use its props and some of its people: csPistol, CS_Luis); the ordinary peds of the streets
    /// (componentpeds.img: M_Y_Goon_01, F_Y_HotChick_01); the vehicles (vehicles.img: schafter, hexer).
    /// </summary>
    public static List<string> ModelArchives(string folder, string? gtaIV)
    {
      var folders = gtaIV == null || string.Equals(Path.GetFullPath(gtaIV), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)
        ? new[] { folder }
        : new[] { folder, gtaIV };
      var archives = new List<string>();
      foreach (string relative in new[] { Path.Combine("pc", "anim", "cutsprops.img"), Path.Combine("pc", "models", "cdimages", "componentpeds.img"),
                                          Path.Combine("pc", "models", "cdimages", "vehicles.img") })
      {
        foreach (string f in folders)
        {
          string path = Path.Combine(f, relative);
          if (System.IO.File.Exists(path))
          {
            archives.Add(path);
          }
        }
      }
      return archives;
    }

    /// <summary>
    /// Loads the actors' models (on a background thread, the archives' key set): the player from the
    /// game's playerped.rpf, everyone and everything else from the first of <paramref name="archives"/>
    /// that has it (a ped's .wdd holds its parts and skeleton, a prop is a .wdr, a vehicle a .wft); the
    /// textures beside the model in its archive.
    /// </summary>
    public static void LoadActors(IEnumerable<CutActor> actors, IReadOnlyList<string> archives, string? playerPed, string playerName)
    {
      var open = new List<RageLib.FileSystem.IMGFileSystem?>();
      RageLib.FileSystem.IMGFileSystem? Open(int i)
      {
        while (open.Count <= i)
        {
          RageLib.FileSystem.IMGFileSystem? img = new RageLib.FileSystem.IMGFileSystem();
          try
          {
            img.Open(archives[open.Count]);
          }
          catch (Exception)
          {
            img = null;
          }
          open.Add(img);
        }
        return open[i];
      }
      try
      {
        foreach (var actor in actors)
        {
          try
          {
            if (actor.Model.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
              LoadPlayer(actor, playerPed, playerName);
              continue;
            }
            RageLib.FileSystem.Common.File? file = null;
            string? from = null;
            for (int i = 0; i < archives.Count && file == null; i++)
            {
              var img = Open(i);
              file = img == null ? null
                : new[] { ".wdd", ".wdr", ".wft" }.Select(e => img.RootDirectory.FindByName(actor.Model + e) as RageLib.FileSystem.Common.File).FirstOrDefault(f => f != null);
              from = archives[i];
            }
            if (file == null || from == null)
            {
              actor.Error = actor.Model + (archives.Count == 0 ? " can't be looked for (no game folder known)" : " isn't in the game's cutsprops, peds or vehicles");
              continue;
            }
            // Loaded while the archive is open: the textures are looked for beside the model, the shared ones
            // (vehshare) in the other archives too.
            byte[]? Elsewhere(string name)
            {
              for (int i = 0; i < archives.Count; i++)
              {
                if (Open(i)?.RootDirectory.FindByName(name + ".wtd") is RageLib.FileSystem.Common.File wtd)
                {
                  return wtd.GetData();
                }
              }
              return null;
            }
            actor.Scene = ModelScene.Load(file, file.GetData(), Elsewhere, actor.TextureVariations.Count > 0 ? actor.VariationOf : null);
            actor.Source = file.Name + " · " + RelativeArchive(from);
          }
          catch (Exception ex)
          {
            actor.Error = "Could not load " + actor.Model + ": " + ex.Message;
          }
        }
      }
      finally
      {
        foreach (var img in open)
        {
          img?.Close();
        }
      }
    }

    /// <summary>An archive's place for the list: "TBoGT\…\componentpeds.img" or "…\cutsprops.img".</summary>
    private static string RelativeArchive(string path)
    {
      string name = Path.GetFileName(path);
      var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
      // ...\<game or episode>\pc\anim\x.img or ...\pc\models\cdimages\x.img
      while (dir != null && !dir.Name.Equals("pc", StringComparison.OrdinalIgnoreCase))
      {
        dir = dir.Parent;
      }
      string? game = dir?.Parent?.Name;
      return game != null && (game.Equals("TLAD", StringComparison.OrdinalIgnoreCase) || game.Equals("TBoGT", StringComparison.OrdinalIgnoreCase))
        ? game + " " + name
        : name;
    }

    private static void LoadPlayer(CutActor actor, string? playerPed, string playerName)
    {
      if (playerPed == null || !System.IO.File.Exists(playerPed))
      {
        actor.Error = "The player's playerped.rpf isn't there (no game folder known)";
        return;
      }
      var fs = new RageLib.FileSystem.RPFFileSystem();
      fs.Open(playerPed);
      try
      {
        var files = AllFiles(fs.RootDirectory).ToList();
        var parts = PlayerParts.Select(part => files.Where(f => f.Name.StartsWith(part + "_", StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".wdr", StringComparison.OrdinalIgnoreCase))
                                                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                                                    .FirstOrDefault())
                               .Where(f => f != null)
                               .Select(f => f!)
                               .ToList();
        if (parts.Count == 0)
        {
          actor.Error = "No player parts in " + playerPed;
          return;
        }
        actor.Scene = ModelScene.LoadPed(playerName, parts[0].ParentDirectory ?? fs.RootDirectory, parts);
        actor.Source = playerName + " (playerped.rpf)";
      }
      finally
      {
        fs.Close();
      }
    }

    private static IEnumerable<RageLib.FileSystem.Common.File> AllFiles(RageLib.FileSystem.Common.Directory directory)
    {
      foreach (var entry in directory)
      {
        if (entry is RageLib.FileSystem.Common.Directory sub)
        {
          foreach (var file in AllFiles(sub))
          {
            yield return file;
          }
        }
        else if (entry is RageLib.FileSystem.Common.File file)
        {
          yield return file;
        }
      }
    }

    /// <summary>
    /// Loads the props the cutscene's peds wear ([PROPS]): each ped's prop dictionary as peds.ide names it
    /// (CS_Bernie_CraneC → CS_Bernie_Crane_p), from pedprops.img of the game's folder or GTA IV's, its
    /// parts fixed to the bone of their anchor (a hat and glasses to the head).
    /// </summary>
    public static void LoadProps(CutFile cut, IEnumerable<CutActor> actors, string folder, string? gtaIV)
    {
      if (cut.Props.Count == 0)
      {
        return;
      }
      var folders = new[] { folder, gtaIV }.Where(f => f != null).Select(f => f!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      var dictionaryOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var f in folders)
      {
        string ide = Path.Combine(f, "common", "data", "peds.ide");
        if (!System.IO.File.Exists(ide))
        {
          continue;
        }
        foreach (string raw in System.IO.File.ReadLines(ide))
        {
          var fields = raw.Split(',').Select(s => s.Trim()).ToArray();
          if (fields.Length > 1 && fields[0].Length > 0 && !fields[0].StartsWith("#", StringComparison.Ordinal) && !fields[1].Equals("null", StringComparison.OrdinalIgnoreCase))
          {
            dictionaryOf.TryAdd(fields[0], fields[1]);
          }
        }
      }
      foreach (var actor in actors)
      {
        var worn = cut.Props.Where(p => p.Part == actor.Part && p.Model == actor.Number && p.PartName != null).ToList();
        if (worn.Count == 0 || actor.Scene == null || !dictionaryOf.TryGetValue(actor.Model, out var dictionary))
        {
          continue;
        }
        foreach (var f in folders)
        {
          string path = Path.Combine(f, "pc", "models", "cdimages", "pedprops.img");
          if (!System.IO.File.Exists(path))
          {
            continue;
          }
          var img = new RageLib.FileSystem.IMGFileSystem();
          try
          {
            img.Open(path);
            if (img.RootDirectory.FindByName(dictionary + ".wdd") is not RageLib.FileSystem.Common.File file)
            {
              continue;
            }
            // Loaded while the archive is open: its textures (CS_Bernie_Crane_p.wtd) are beside it.
            actor.PropScene = ModelScene.Load(file, file.GetData());
          }
          catch (Exception)
          {
            continue;
          }
          finally
          {
            img.Close();
          }
          break;
        }
        if (actor.PropScene == null)
        {
          continue;
        }
        foreach (var prop in worn)
        {
          var part = actor.PropScene.Root.Children.FirstOrDefault(p => p.Name.Equals(prop.PartName, StringComparison.OrdinalIgnoreCase));
          var bone = actor.Scene.Bones.FirstOrDefault(b => b.Name.Equals(BoneOfAnchor(prop.Anchor), StringComparison.OrdinalIgnoreCase));
          if (part != null && bone != null)
          {
            actor.Props.Add((part, bone.Index));
          }
        }
      }
    }

    // The bone a prop's anchor is on: the head (hats, glasses, earrings), a hand (and its wrist: watches),
    // the pelvis, a foot.
    private static string BoneOfAnchor(int anchor) => anchor switch
    {
      4 or 6 => "Char_L_Hand",
      5 or 7 => "Char_R_Hand",
      8 => "Char_Pelvis",
      9 => "Char_L_Foot",
      10 => "Char_R_Foot",
      _ => "Char_Head",
    };

    /// <summary>
    /// Shows the variation of each ped's parts the cutscene asks for (the first one, where a part changes
    /// during the scene): of the parts of that kind (uppr_000_u, uppr_001_u) only the one of that number.
    /// The texture variations are applied when the models load (<see cref="CutActor.TextureVariations"/>).
    /// </summary>
    /// <param name="likeGame">Leave out the see-through parts the game doesn't draw (<see cref="HideUnflaggedSeeThrough"/>).</param>
    /// <summary>
    /// The vehicles' colours: by the .cut's [VEHICLE_DETAILS] (its first line for the model), else the first
    /// set carcols.dat gives the model; numbers into carcols.dat's colour table (the game's folder's, else GTA
    /// IV's). Six colours as the game makes them for a vehicle's shaders (GTAIV.exe 0xD25D80): the four, white,
    /// the third again.
    /// </summary>
    public static void ApplyPaint(CutFile cut, IEnumerable<CutActor> actors, string folder, string? gtaIV)
    {
      string? file = new[] { folder, gtaIV }.Where(f => f != null).Select(f => Path.Combine(f!, "common", "data", "carcols.dat")).FirstOrDefault(File.Exists);
      if (file == null)
      {
        return;
      }
      var colours = new List<float[]>();
      var sets = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
      string? block = null;
      foreach (string raw in File.ReadLines(file))
      {
        string line = raw.Split('#')[0].Trim();
        if (line.Length == 0)
        {
          continue;
        }
        if (line is "col" or "car" or "car3" or "car4" or "end")
        {
          block = line == "end" ? null : line;
          continue;
        }
        var fields = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (block == "col" && fields.Length >= 3 && fields.Take(3).All(f => int.TryParse(f, out _)))
        {
          colours.Add(fields.Take(3).Select(f => int.Parse(f, CultureInfo.InvariantCulture) / 255f).ToArray());
        }
        else if (block is "car" or "car3" or "car4" && fields.Length >= 2)
        {
          var numbers = fields.Skip(1).Select(f => int.TryParse(f, out int n) ? n : -1).Where(n => n >= 0).ToArray();
          sets.TryAdd(fields[0], numbers);
        }
      }
      float[] Colour(int number) => number >= 0 && number < colours.Count ? colours[number] : new[] { 1f, 1f, 1f };
      foreach (var actor in actors)
      {
        if (actor.Scene == null || !actor.Scene.Materials.Any(m => m.PaintSlot > 0))
        {
          continue;
        }
        var details = cut.VehicleDetails.FirstOrDefault(d => d.Part == actor.Part && d.Model == actor.Number);
        int[]? numbers = details.Colours;
        actor.Dirt = details.Colours != null ? details.Dirt / 15f : 0;
        if (numbers == null && sets.TryGetValue(actor.Model, out var set))
        {
          int per = set.Length % 4 == 0 && set.Length % 3 != 0 ? 4 : 3;
          numbers = set.Take(per).Concat(Enumerable.Repeat(per == 3 ? set.ElementAtOrDefault(2) : 0, 4 - per)).ToArray();
        }
        if (numbers == null || numbers.Length < 4)
        {
          continue;
        }
        actor.Paint = new[] { Colour(numbers[0]), Colour(numbers[1]), Colour(numbers[2]), Colour(numbers[3]), new[] { 1f, 1f, 1f }, Colour(numbers[2]) };
      }
      ApplyShine(actors, folder, gtaIV);
    }

    /// <summary>
    /// The vehicles' shine (<see cref="CutActor.Shine"/>), as GTAIV.exe 0xD25040 sets it by the model's
    /// vehicles.ide line (its number after lodMult, read by 0x9D9D90 into the model's +0x84): 0 all five 1;
    /// otherwise drawn at random per vehicle (rand / 32767) - specular, its colour and the second shine's
    /// intensity 0.9 to 1, the second shine's factor 3.9 to 4, the reflectivity 0.7 to 0.75 for 1, else 0.55
    /// to 0.6. Here the middle of each.
    /// </summary>
    private static void ApplyShine(IEnumerable<CutActor> actors, string folder, string? gtaIV)
    {
      string? file = new[] { folder, gtaIV }.Where(f => f != null).Select(f => Path.Combine(f!, "common", "data", "vehicles.ide")).FirstOrDefault(File.Exists);
      if (file == null)
      {
        return;
      }
      var kind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (string raw in File.ReadLines(file))
      {
        // Model, txd, type, handling, game name, anims, anims2, frequency, max. number, wheel radii (2), dirt,
        // swankness, lodMult, then this number, the flags.
        var fields = raw.Split('#')[0].Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length >= 16 && int.TryParse(fields[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
          kind.TryAdd(fields[0], number);
        }
      }
      foreach (var actor in actors)
      {
        if (actor.Scene == null || !actor.Scene.Materials.Any(m => m.Vehicle) || !kind.TryGetValue(actor.Model, out int number))
        {
          continue;
        }
        actor.Shine = number == 0 ? new[] { 1f, 1f, 1f, 1f, 1f } : new[] { 0.95f, 0.95f, 3.95f, 0.95f, number == 1 ? 0.725f : 0.575f };
      }
    }

    public static void ApplyVariations(CutFile cut, IEnumerable<CutActor> actors, bool likeGame = true)
    {
      foreach (var actor in actors)
      {
        var root = actor.Scene?.Root;
        if (root == null)
        {
          continue;
        }
        var wanted = cut.Variations.Where(v => v.Part == actor.Part && v.Model == actor.Number && v.ComponentName != null)
                                   .GroupBy(v => v.Component)
                                   .Select(g => g.OrderBy(v => v.Time).First());
        foreach (var variation in wanted)
        {
          string kind = variation.ComponentName + "_";
          var parts = root.Children.Where(p => p.Name.StartsWith(kind, StringComparison.OrdinalIgnoreCase)).ToList();
          var chosen = parts.FirstOrDefault(p => p.Name.StartsWith(kind + variation.Drawable.ToString("000") + "_", StringComparison.OrdinalIgnoreCase));
          if (chosen == null)
          {
            continue;
          }
          foreach (var part in parts)
          {
            part.IsChecked = ReferenceEquals(part, chosen);
          }
        }
        if (likeGame && !actor.Model.Equals("player", StringComparison.OrdinalIgnoreCase))
        {
          HideUnflaggedSeeThrough(actor.Scene!, root);
        }
      }
    }

    /// <summary>
    /// What the game leaves out of a cutscene ped: whether a ped has see-through parts (draw buckets 1 and 3,
    /// its flags 0x1000 and 0x4000) is worked out once, from the parts it is made with: number 000 of each
    /// (CPed_InitVariations, PedVariation_UpdateFlags). The cutscene's variations change the parts without
    /// working it out again (PedVariation_Set), and the game doesn't draw a ped's see-through geometry
    /// without the flag. So a part a variation adds shows nothing of those buckets unless a part 000 had
    /// them too: Bernie's headband in bc_1 (hair_001, all bucket 3, where his hair_000 is opaque). Found in
    /// GTAIV.exe and checked in the game (setting the flag by hand brings the headband).
    /// </summary>
    private static void HideUnflaggedSeeThrough(ModelScene scene, ModelPart root)
    {
      IEnumerable<int> Buckets(ModelPart part) =>
        AllMeshes(part).Select(id => scene.Meshes[id].Material)
                       .Where(m => m >= 0 && m < scene.Materials.Count)
                       .Select(m => scene.Materials[m].DrawBucket);
      // The ped's flags: the buckets of its parts 000 (the high level of detail, as the game asks).
      var start = root.Children.Where(p => CutVariation.Components.Any(c => p.Name.StartsWith(c + "_000_", StringComparison.OrdinalIgnoreCase)))
                               .SelectMany(p => Buckets(HighDetail(p)))
                               .ToHashSet();
      foreach (int bucket in new[] { 1, 3 }.Where(b => !start.Contains(b)))
      {
        foreach (var part in root.Children.Where(p => p.IsChecked != false && !p.Name.Contains("_000_") &&
                                                      CutVariation.Components.Any(c => p.Name.StartsWith(c + "_", StringComparison.OrdinalIgnoreCase))))
        {
          foreach (var mesh in MeshParts(part).Where(m => scene.Materials.ElementAtOrDefault(scene.Meshes[m.MeshId].Material)?.DrawBucket == bucket))
          {
            mesh.IsChecked = false;
          }
        }
      }

      static ModelPart HighDetail(ModelPart part) => part.Children.FirstOrDefault(c => c.Lod == 0) ?? part;
      static IEnumerable<ModelPart> MeshParts(ModelPart part) =>
        part.MeshId >= 0 ? new[] { part } : part.Children.SelectMany(MeshParts);
      static IEnumerable<int> AllMeshes(ModelPart part) => MeshParts(part).Select(m => m.MeshId);
    }

    /// <summary>
    /// The actors of all sections: one per model number of each part of the file (a part numbers its models
    /// itself: gt06_ba's first part has CS_VicGirl as 2 and CS_Vic as 3, its second the other way round), in
    /// the order they come.
    /// </summary>
    public static List<CutActor> Actors(CutFile cut)
    {
      var actors = new List<CutActor>();
      foreach (var section in cut.Sections)
      {
        foreach (var model in section.Models)
        {
          if (!actors.Any(a => a.Part == section.Part && a.Number == model.Id))
          {
            actors.Add(new CutActor(actors.Count + 1, model.Model) { Part = section.Part, Number = model.Id });
          }
        }
      }
      // The texture variations asked for (the first of a part, where it changes during the scene).
      foreach (var actor in actors)
      {
        foreach (var variation in cut.Variations.Where(v => v.Part == actor.Part && v.Model == actor.Number && v.ComponentName != null).GroupBy(v => v.Component).Select(g => g.OrderBy(v => v.Time).First()))
        {
          if (variation.Texture > 0)
          {
            actor.TextureVariations[variation.ComponentName!] = variation.Texture;
          }
        }
      }
      return actors;
    }

    /// <summary>The actor a section's model is (by the section's part and the model's number there), or null.</summary>
    public static CutActor? ActorOf(IEnumerable<CutActor> actors, CutSection section, CutModel model) =>
      actors.FirstOrDefault(a => a.Part == section.Part && a.Number == model.Id);
  }
}
