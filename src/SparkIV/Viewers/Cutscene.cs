using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using RageLib.Animation;

namespace SparkIV.WinUI.Viewers
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

    /// <summary>Which variation of a ped's parts is worn (CS_Bernie_CraneT: uppr_001, its uppr_000 an empty stand-in).</summary>
    public List<CutVariation> Variations { get; } = new List<CutVariation>();

    /// <summary>Where the scene is in the world (its origin), and its turn about Z in degrees.</summary>
    public float[]? Offset { get; private set; }
    public float Orient { get; private set; }

    public static CutFile Parse(string text)
    {
      var cut = new CutFile();
      CutSection? section = null;
      string? block = null;
      foreach (string raw in text.Split('\n'))
      {
        string line = raw.Trim();
        if (line.Length == 0)
        {
          continue;
        }
        if (line == "[SECTION_START]")
        {
          section = new CutSection();
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
                                                parts.Length > 4 ? int.Parse(parts[4], CultureInfo.InvariantCulture) : 0));
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
        }
      }
      return cut;
    }
  }

  public sealed class CutSection
  {
    public List<CutModel> Models { get; } = new List<CutModel>();

    /// <summary>The animation dictionary (B_1_0: b_1_0.wad).</summary>
    public string? Anim { get; set; }

    /// <summary>The camera's clip in it (camera_0).</summary>
    public string? Camera { get; set; }
    public string? Audio { get; set; }

    /// <summary>Seconds.</summary>
    public float Duration { get; set; }
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
  public sealed record CutVariation(int Model, int Component, int Drawable, int Texture, int Time)
  {
    private static readonly string[] Components = { "head", "uppr", "lowr", "suse", "hand", "feet", "jack", "hair", "sus2", "teef", "face" };

    /// <summary>The part's name ("uppr"), or null for a number not known.</summary>
    public string? ComponentName => Component >= 0 && Component < Components.Length ? Components[Component] : null;
  }

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

    /// <summary>The model's number in the .cut, plus 1 (0 is no actor on the page).</summary>
    public int Id { get; }
    public string Model { get; }
    public ModelScene? Scene { get; set; }
    public string? Source { get; set; }
    public string? Error { get; set; }

    /// <summary>A ped (placed in the scene by its clip's model tracks) rather than a prop (by its root bone).</summary>
    public bool IsPed => Scene != null && Scene.Bones.Count > 40;
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

    /// <summary>The texture behind a key from <see cref="TextureKey"/>, or null.</summary>
    public static byte[]? Texture(IEnumerable<CutActor> actors, string key)
    {
      int slash = key.IndexOf('/');
      if (slash < 2 || !int.TryParse(key.AsSpan(1, slash - 1), out int id))
      {
        return null;
      }
      var actor = actors.FirstOrDefault(a => a.Id == id);
      return actor?.Scene != null && actor.Scene.Textures.TryGetValue(key.Substring(slash + 1), out var png) ? png : null;
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
          meshes.Add(node);
          if (shown.Contains(mesh.Id))
          {
            visible.Add(id);
          }
        }
      }
      return (new JsonObject { ["type"] = "scene", ["materials"] = materials, ["meshes"] = meshes }, visible);
    }

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
    public static (object Timeline, object Cast, object? Camera, List<string> Notes) Section(CutSection section, IReadOnlyList<CutActor> actors, AnimationFile? wad)
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
        var actor = actors.FirstOrDefault(a => a.Id == model.Id + 1);
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
        cast.Add(new
        {
          id = actor.Id,
          skeleton,
          clip = clip == null ? null : new { frames = Math.Max(1, clip.FrameCount), fps = FramesPerSecond(clip), tracks },
          root,
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

      var timeline = new { type = "clip", frames, fps, tracks = Array.Empty<object>() };
      return (timeline, new { type = "cast", actors = cast }, camera == null ? null : new { type = "camera", camera }, notes);
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
            actor.Scene = ModelScene.Load(file, file.GetData(), Elsewhere);
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
    /// Shows the variation of each ped's parts the cutscene asks for (the first one, where a part changes
    /// during the scene): of the parts of that kind (uppr_000_u, uppr_001_u) only the one of that number.
    /// The texture variations aren't applied yet.
    /// </summary>
    public static void ApplyVariations(CutFile cut, IEnumerable<CutActor> actors)
    {
      foreach (var actor in actors)
      {
        var root = actor.Scene?.Root;
        if (root == null)
        {
          continue;
        }
        var wanted = cut.Variations.Where(v => v.Model == actor.Id - 1 && v.ComponentName != null)
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
      }
    }

    /// <summary>The actors of all sections: one per model number, in the order of the first section.</summary>
    public static List<CutActor> Actors(CutFile cut)
    {
      var actors = new List<CutActor>();
      foreach (var model in cut.Sections.SelectMany(s => s.Models))
      {
        if (!actors.Any(a => a.Id == model.Id + 1))
        {
          actors.Add(new CutActor(model.Id + 1, model.Model));
        }
      }
      return actors;
    }
  }
}
