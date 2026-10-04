using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using RageLib.Animation;
using RageLib.Common;
using RageLib.FileSystem;
using SparkIV.WinUI.Services;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>A ped the animations can be shown on: the player of a game, from its playerped.rpf.</summary>
  public sealed class PedChoice
  {
    internal PedChoice(string name, string archive)
    {
      Name = name;
      Archive = archive;
    }

    public string Name { get; }

    /// <summary>The playerped.rpf it is in.</summary>
    public string Archive { get; }

    public override string ToString() => Name;
  }

  /// <summary>What the animation view shows: the clips of a .wad and the ped they play on.</summary>
  public sealed class AnimationDocument
  {
    public AnimationFile Animations { get; init; } = null!;
    public List<PedChoice> Peds { get; init; } = new List<PedChoice>();
    public PedChoice? Ped { get; set; }
    public ModelScene? PedScene { get; set; }

    /// <summary>Why there is no ped (no game folder known, a part missing), or null.</summary>
    public string? PedError { get; set; }

    /// <summary>
    /// The facial clips of the ped's game by the hash of their name (what a clip's FacialAnimation track
    /// holds), with the dictionary each is from; from facials@m_hi.wad first, then m_lo, f_hi, f_lo.
    /// </summary>
    public Dictionary<uint, (Animation Clip, string Source)> Facials { get; set; } = new Dictionary<uint, (Animation, string)>();

    /// <summary>The names of the games' sounds by their hash (what a clip's AudioEvent track holds).</summary>
    public IReadOnlyDictionary<uint, string> SoundNames { get; init; } = new Dictionary<uint, string>();

    /// <summary>
    /// Whether the clips move map objects (a fan, a flag, a sign's texture), each the model it is named
    /// after, rather than a ped.
    /// </summary>
    public bool IsObjects { get; init; }

    /// <summary>The folder the .wad is in (the map archive, where its models are too), or null.</summary>
    public Directory? Folder { get; init; }

    /// <summary>The objects' models loaded so far, by name.</summary>
    public Dictionary<string, ObjectModel> Objects { get; } = new Dictionary<string, ObjectModel>(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>A map object's model for its clips, where it was found, or why there is none.</summary>
  public sealed record ObjectModel(string Name, ModelScene? Scene, string? Source, string? Error);

  /// <summary>Animation dictionaries (.wad): their clips played on a ped.</summary>
  internal sealed class AnimationViewer : IViewer
  {
    // The ped chosen last, while SparkIV runs.
    internal static string? s_ped;

    public object Load(File file, byte[] data)
    {
      var animations = new AnimationFile();
      animations.Open(new MemoryStream(data));
      if (IsObjectDictionary(animations, file.ParentDirectory))
      {
        return new AnimationDocument { Animations = animations, SoundNames = SoundNames(), IsObjects = true, Folder = file.ParentDirectory };
      }
      var document = new AnimationDocument { Animations = animations, Peds = FindPeds(), SoundNames = SoundNames(), Folder = file.ParentDirectory };
      var ped = document.Peds.FirstOrDefault(p => p.Name == s_ped) ?? document.Peds.FirstOrDefault();
      if (ped == null)
      {
        document.PedError = "No game folder is known, so there is no ped to play the animations on. Set the game folders in Settings.";
      }
      else
      {
        LoadPed(document, ped);
      }
      return document;
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new AnimationView(context, (AnimationDocument)loaded);

    // The bone ids of the ped skeleton: the same 90 in Niko, Johnny and Luis (and what ped clips name).
    private static readonly HashSet<int> PedBoneIds = new HashSet<int>
    {
      417, 418, 419, 420, 421, 423, 424, 425, 1200, 1202, 1203, 1204, 1205, 1216, 1217, 1218, 1219, 1223, 1224,
      1225, 1232, 13744, 13745, 13746, 13747, 13748, 13749, 13750, 13751, 13752, 13753, 13760, 13761, 13762, 13763,
      13764, 13776, 13777, 13778, 13779, 13780, 13781, 13782, 13783, 13784, 13785, 13792, 13793, 13794, 13795,
      13796, 13984, 13985, 14240, 14496, 14497, 14512, 14752, 14753, 14768, 15857, 15873, 43252, 43253, 43254,
      43255, 43256, 43257, 43258, 43259, 43260, 43267, 43268, 43269, 43270, 43271, 43272, 43273, 43274, 43275,
      43276, 43283, 43284, 43285, 43286, 43287, 45156, 45157, 45158,
    };

    /// <summary>The model a map object's clip is for: its name, without "_uv_N" (ts_atower_dc9_uv_27).</summary>
    internal static string ModelNameOf(Animation clip) => Regex.Replace(clip.ShortName, @"_uv_\d+$", "", RegexOptions.IgnoreCase);

    /// <summary>
    /// The material whose texture a clip moves ("_uv_27": material 27, whose two UV tracks are the rows of
    /// its texture matrix), or -1.
    /// </summary>
    internal static int UvMaterialOf(Animation clip)
    {
      var match = Regex.Match(clip.ShortName, @"_uv_(\d+)$", RegexOptions.IgnoreCase);
      return match.Success && IsUvClip(clip) ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>Whether a clip moves a texture: UV tracks as numbers with a fraction (in ped clips they are whole numbers: flags, sounds).</summary>
    internal static bool IsUvClip(Animation clip) =>
      clip.Tracks.Any(t => (t.Kind == AnimationTrackKind.UV0 || t.Kind == AnimationTrackKind.UV1) && t.DataType != 3);

    /// <summary>
    /// Whether a dictionary's clips are for map objects. Found in the map archives (bronx_e.wad beside
    /// bronx_e.img's models), each clip named like its model; ped clips are in anim.img. Told apart by what
    /// the clips are, wherever the file is: a model of a clip's name beside it, a texture moved, or bones
    /// that aren't a ped's (et_bboardanim01 moves 33, a viseme only 11 of a ped's face). A clip of a few ped
    /// bone ids only (pris_bigtow02: 418) is an object's when the game has a model of its name.
    /// </summary>
    internal static bool IsObjectDictionary(AnimationFile animations, Directory? folder)
    {
      var clips = animations.Animations;
      if (clips.Count == 0)
      {
        return false;
      }
      bool HasModelBeside(Animation clip) =>
        folder != null && (folder.FindByName(ModelNameOf(clip) + ".wdr") is File || folder.FindByName(ModelNameOf(clip) + ".wft") is File);
      if (clips.Any(HasModelBeside) || clips.Any(IsUvClip))
      {
        return true;
      }
      var ids = clips.SelectMany(c => c.Tracks)
                     .Where(t => t.Kind == AnimationTrackKind.BonePosition || t.Kind == AnimationTrackKind.BoneRotation)
                     .Select(t => (int)t.BoneId)
                     .Where(id => id != 0)
                     .Distinct()
                     .ToList();
      int ped = ids.Count(PedBoneIds.Contains);
      if (ids.Count - ped > ped)
      {
        return true;
      }
      return ped > 0 && ped <= 4 && clips.Any(c => GameModels.ArchivesWith(ModelNameOf(c)).Count > 0);
    }

    /// <summary>
    /// The model of a map object (on a background thread; remembered in the document): beside the .wad
    /// first, else in the maps of the games installed.
    /// </summary>
    internal static ObjectModel LoadObject(AnimationDocument document, string name)
    {
      lock (document.Objects)
      {
        if (document.Objects.TryGetValue(name, out var known))
        {
          return known;
        }
      }
      ObjectModel loaded;
      try
      {
        loaded = FindObject(document, name);
      }
      catch (Exception ex)
      {
        loaded = new ObjectModel(name, null, null, "Could not load the model " + name + ": " + ex.Message);
      }
      lock (document.Objects)
      {
        document.Objects[name] = loaded;
      }
      return loaded;
    }

    private static ObjectModel FindObject(AnimationDocument document, string name)
    {
      foreach (string extension in new[] { ".wdr", ".wft" })
      {
        if (document.Folder?.FindByName(name + extension) is File beside)
        {
          return new ObjectModel(name, ModelScene.Load(beside, beside.GetData()), beside.Name + " beside it", null);
        }
      }
      foreach (string archive in GameModels.ArchivesWith(name))
      {
        var fs = new IMGFileSystem();
        fs.Open(archive);
        try
        {
          foreach (string extension in new[] { ".wdr", ".wft" })
          {
            if (fs.RootDirectory.FindByName(name + extension) is File file)
            {
              // Loaded while the archive is open: the textures are looked for beside the model.
              return new ObjectModel(name, ModelScene.Load(file, file.GetData()), file.Name + " from " + Path.GetFileName(archive), null);
            }
          }
        }
        finally
        {
          fs.Close();
        }
      }
      return new ObjectModel(name, null, null,
                             "The model " + name + " isn't beside the file or in the maps of the games set in Settings, so there is nothing to play this clip on.");
    }

    /// <summary>The players of the games installed: Niko (GTA IV), Johnny (TLAD), Luis (TBoGT).</summary>
    internal static List<PedChoice> FindPeds()
    {
      var peds = new List<PedChoice>();
      void Add(string name, string? folder, string relative)
      {
        if (folder == null)
        {
          return;
        }
        string archive = Path.Combine(folder, relative, "pc", "models", "cdimages", "playerped.rpf");
        if (System.IO.File.Exists(archive) && !peds.Any(p => p.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)))
        {
          peds.Add(new PedChoice(name, archive));
        }
      }
      string? iv = Games.InstallFolder(Games.GtaIV, "Windows");
      string? episodes = Games.InstallFolder(Games.Episodes, "Windows");
      Add("Niko Bellic (GTA IV)", iv, "");
      Add("Johnny Klebitz (TLAD)", episodes, "TLAD");
      Add("Luis Lopez (TBoGT)", episodes, "TBoGT");
      return peds;
    }

    // The sound names, read once while SparkIV runs (some 75,000 from the three games, in a tenth of a second).
    private static Dictionary<uint, string>? s_soundNames;
    private static readonly object s_soundNamesLock = new object();

    /// <summary>
    /// The names of the sounds of all games installed, by the hash a clip's AudioEvent track holds
    /// (ANIM_PED_RUB_HANDS_02). They are in pc\audio\config\*.dat* (sounds.dat15 mostly), each preceded
    /// by its length; every run of name characters is taken, the same without its first character in case
    /// that is the length, and as long as the byte before it says. Not every sound a clip names is there:
    /// of the 240 in GTA IV's clips, 206 are (with the episodes' files).
    /// </summary>
    internal static Dictionary<uint, string> SoundNames()
    {
      lock (s_soundNamesLock)
      {
        if (s_soundNames != null)
        {
          return s_soundNames;
        }
        var names = new Dictionary<uint, string>();
        string? iv = Games.InstallFolder(Games.GtaIV, "Windows");
        string? episodes = Games.InstallFolder(Games.Episodes, "Windows");
        var folders = new[] { iv, episodes == null ? null : Path.Combine(episodes, "TLAD"), episodes == null ? null : Path.Combine(episodes, "TBoGT") };
        foreach (var folder in folders)
        {
          string config = folder == null ? "" : Path.Combine(folder, "pc", "audio", "config");
          if (!System.IO.Directory.Exists(config))
          {
            continue;
          }
          foreach (var path in System.IO.Directory.GetFiles(config, "*.dat*"))
          {
            try
            {
              AddNames(names, System.IO.File.ReadAllBytes(path));
            }
            catch (IOException)
            {
              // Unreadable: those sounds stay hashes.
            }
          }
        }
        return s_soundNames = names;
      }
    }

    private static void AddNames(Dictionary<uint, string> names, byte[] data)
    {
      static bool IsNameChar(byte b) => b == '_' || (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
      int i = 0;
      while (i < data.Length)
      {
        if (!IsNameChar(data[i]))
        {
          i++;
          continue;
        }
        int start = i;
        while (i < data.Length && IsNameChar(data[i]))
        {
          i++;
        }
        void Add(int at, int length)
        {
          if (length >= 3)
          {
            string name = System.Text.Encoding.ASCII.GetString(data, at, length);
            names.TryAdd(RageLib.Text.GxtFile.Hash(name), name);
          }
        }
        Add(start, i - start);
        Add(start + 1, i - start - 1);
        // As long as the byte before says (when the next byte happens to be a name character, too).
        if (start > 0 && data[start - 1] < i - start)
        {
          Add(start, data[start - 1]);
        }
      }
    }

    // The parts shown, one of each: the first variation there is (head_000_r before head_001_r).
    private static readonly string[] Parts = { "head", "uppr", "lowr", "feet", "hand", "hair", "teef" };

    /// <summary>Loads a ped into the document (on a background thread); on failure PedError says why.</summary>
    internal static void LoadPed(AnimationDocument document, PedChoice ped)
    {
      document.Ped = ped;
      document.PedScene = null;
      document.PedError = null;
      try
      {
        // The archive's key, from the game it belongs to (also when the .wad was opened on its own).
        var install = Games.ForFile(ped.Archive);
        if (install != null)
        {
          var keyUtil = install.KeyUtil;
          KeyStore.SetKeyLoader(() => keyUtil.FindKey(install.Folder));
        }
        var fs = new RPFFileSystem();
        fs.Open(ped.Archive);
        try
        {
          LoadParts(document, ped, fs.RootDirectory);
        }
        finally
        {
          fs.Close();
        }
        document.Facials = LoadFacials(ped.Archive);
      }
      catch (Exception ex)
      {
        document.PedError = "Could not load " + ped.Name + ": " + ex.Message;
      }
    }

    private static readonly string[] FacialDictionaries = { "facials@m_hi.wad", "facials@m_lo.wad", "facials@f_hi.wad", "facials@f_lo.wad" };

    /// <summary>
    /// The facial clips of the game a ped is from: in pc\anim\anim.img beside its pc\models (the episodes
    /// have their own). Empty if they can't be read.
    /// </summary>
    private static Dictionary<uint, (Animation, string)> LoadFacials(string pedArchive)
    {
      var facials = new Dictionary<uint, (Animation, string)>();
      try
      {
        // ...\pc\models\cdimages\playerped.rpf -> ...\pc\anim\anim.img
        string? pc = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(pedArchive)));
        string anim = pc == null ? "" : Path.Combine(pc, "anim", "anim.img");
        if (!System.IO.File.Exists(anim))
        {
          return facials;
        }
        var fs = new IMGFileSystem();
        fs.Open(anim);
        try
        {
          foreach (var name in FacialDictionaries)
          {
            if (fs.RootDirectory.FindByName(name) is not File file)
            {
              continue;
            }
            var dictionary = new AnimationFile();
            dictionary.Open(new MemoryStream(file.GetData()));
            foreach (var clip in dictionary.Animations)
            {
              facials.TryAdd(RageLib.Text.GxtFile.Hash(clip.ShortName), (clip, Path.GetFileNameWithoutExtension(name)));
            }
          }
        }
        finally
        {
          fs.Close();
        }
      }
      catch (Exception)
      {
        // Without them the faces just don't move by facial clips.
      }
      return facials;
    }

    private static void LoadParts(AnimationDocument document, PedChoice ped, Directory folder)
    {
      var files = AllFiles(folder).ToList();
      var parts = new List<File>();
      foreach (var part in Parts)
      {
        var file = files.Where(f => f.Name.StartsWith(part + "_", StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".wdr", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
        if (file != null)
        {
          parts.Add(file);
        }
      }
      if (parts.Count == 0)
      {
        document.PedError = "No ped parts in " + ped.Archive + ".";
        return;
      }
      // The dictionaries are next to the parts.
      var texturesFolder = parts[0].ParentDirectory ?? folder;
      document.PedScene = ModelScene.LoadPed(ped.Name, texturesFolder, parts);
      s_ped = ped.Name;
    }

    private static IEnumerable<File> AllFiles(Directory directory)
    {
      foreach (var entry in directory)
      {
        if (entry is Directory sub)
        {
          foreach (var file in AllFiles(sub))
          {
            yield return file;
          }
        }
        else if (entry is File file)
        {
          yield return file;
        }
      }
    }
  }
}
