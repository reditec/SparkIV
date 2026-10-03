using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
  }

  /// <summary>Animation dictionaries (.wad): their clips played on a ped.</summary>
  internal sealed class AnimationViewer : IViewer
  {
    // The ped chosen last, while SparkIV runs.
    internal static string? s_ped;

    public object Load(File file, byte[] data)
    {
      var animations = new AnimationFile();
      animations.Open(new MemoryStream(data));
      var document = new AnimationDocument { Animations = animations, Peds = FindPeds(), SoundNames = SoundNames() };
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
