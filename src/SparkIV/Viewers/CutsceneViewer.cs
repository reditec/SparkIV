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
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>What the cutscene view shows: the .cut, its cast loaded, each section's animations, the subtitles' texts.</summary>
  public sealed class CutsceneDocument
  {
    public CutFile Cut { get; init; } = null!;
    public List<CutActor> Actors { get; init; } = new List<CutActor>();

    /// <summary>Each section's animation dictionary, or null where it isn't beside the file.</summary>
    public List<AnimationFile?> Sections { get; init; } = new List<AnimationFile?>();

    /// <summary>The subtitles' texts by key (from the game's american.gxt).</summary>
    public Dictionary<string, string> Texts { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game whose models and texts are used ("GTA IV", "TLAD", "TBoGT"), or null.</summary>
    public string? Game { get; init; }

    /// <summary>That game's folder (the one holding pc\), for the location's maps; or null.</summary>
    public string? GameFolder { get; init; }

    /// <summary>The map around the scene, once loaded (the view loads it after showing the cast).</summary>
    public CutsceneLocation? Location { get; set; }

    /// <summary>Why the cast couldn't be loaded (no game folder known), or null.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The scene's sound (cutscenes.rpf: CUTSCENES/B_1.ivaud), one track as long as all sections together,
    /// or null; and where it came from or why there is none.
    /// </summary>
    public RageLib.Audio.AudioFile? AudioFile { get; init; }
    public RageLib.Audio.AudioTrack? AudioTrack { get; init; }
    public string? AudioNote { get; init; }
  }

  /// <summary>Cutscenes (.cut): the sections played with everyone in them, through the scene's camera.</summary>
  internal sealed class CutsceneViewer : IViewer
  {
    public object Load(File file, byte[] data)
    {
      var cut = CutFile.Parse(System.Text.Encoding.ASCII.GetString(data));
      var sections = cut.Sections.Select(s => LoadWad(file, s.Anim)).ToList();
      var actors = CutsceneMessages.Actors(cut);

      // The game the cutscene is from: the one whose cuts.img has a file of this name and size (GTA IV's and
      // the episodes' names differ); the current game first.
      var game = Candidates().FirstOrDefault(c => HasCut(c.Folder, file));
      if (game.Folder == null)
      {
        return new CutsceneDocument
        {
          Cut = cut, Actors = actors, Sections = sections,
          Error = "This cutscene isn't in the cuts.img of a game set in Settings, so there are no models to play it with.",
        };
      }
      string anim = Path.Combine(game.Folder, "pc", "anim");
      var install = Games.ForFile(Path.Combine(anim, "cutsprops.img"));
      if (install != null)
      {
        var keyUtil = install.KeyUtil;
        KeyStore.SetKeyLoader(() => keyUtil.FindKey(install.Folder));
      }
      CutsceneMessages.LoadActors(actors, CutsceneMessages.ModelArchives(game.Folder, Games.InstallFolder(Games.GtaIV, "Windows")),
                                  Path.Combine(game.Folder, "pc", "models", "cdimages", "playerped.rpf"), game.Player);
      CutsceneMessages.ApplyVariations(cut, actors, AppSettings.CutsceneSeeThroughLikeGame);
      CutsceneMessages.LoadProps(cut, actors, game.Folder, Games.InstallFolder(Games.GtaIV, "Windows"));
      var (audioFile, audioTrack, audioNote) = LoadAudio(game.Folder, cut);
      return new CutsceneDocument
      {
        Cut = cut, Actors = actors, Sections = sections, Texts = LoadTexts(game.Folder, cut), Game = game.Name, GameFolder = game.Folder,
        AudioFile = audioFile, AudioTrack = audioTrack, AudioNote = audioNote,
      };
    }

    /// <summary>The sound the sections name ([AUDIO] B_1), from the game's pc\audio\sfx\cutscenes.rpf.</summary>
    private static (RageLib.Audio.AudioFile?, RageLib.Audio.AudioTrack?, string) LoadAudio(string folder, CutFile cut)
    {
      string? name = cut.Sections.Select(s => s.Audio).FirstOrDefault(a => !string.IsNullOrEmpty(a));
      if (name == null)
      {
        return (null, null, "no sound named");
      }
      string path = Path.Combine(folder, "pc", "audio", "sfx", "cutscenes.rpf");
      if (!System.IO.File.Exists(path))
      {
        return (null, null, "no cutscenes.rpf for the sound " + name);
      }
      try
      {
        var rpf = new RPFFileSystem();
        rpf.Open(path);
        try
        {
          var file = rpf.RootDirectory.OfType<RageLib.FileSystem.Common.Directory>()
                        .Select(d => d.FindByName(name + ".ivaud") as File)
                        .FirstOrDefault(f => f != null)
                     ?? rpf.RootDirectory.FindByName(name + ".ivaud") as File;
          if (file == null)
          {
            return (null, null, "the sound " + name + " isn't in cutscenes.rpf");
          }
          var audio = new RageLib.Audio.AudioFile();
          audio.Open(new MemoryStream(file.GetData()));
          // All waves together: left, centre, right (LCR) for GTA IV's cutscenes.
          var track = RageLib.Audio.AudioTrack.FindChannelGroups(audio.AudioWaves).OrderByDescending(t => t.Waves.Length).FirstOrDefault();
          return track == null ? (null, null, "no sound in " + file.Name) : (audio, track, "sound " + file.Name + " (" + track.LayoutName + ")");
        }
        finally
        {
          rpf.Close();
        }
      }
      catch (Exception ex)
      {
        return (null, null, "could not read the sound " + name + ": " + ex.Message);
      }
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new CutsceneView(context, (CutsceneDocument)loaded);

    private static AnimationFile? LoadWad(File cut, string? name)
    {
      if (name == null || cut.ParentDirectory?.FindByName(name + ".wad") is not File wad)
      {
        return null;
      }
      try
      {
        var animations = new AnimationFile();
        animations.Open(new MemoryStream(wad.GetData()));
        return animations;
      }
      catch (Exception)
      {
        return null;
      }
    }

    /// <summary>The games installed: the folder holding pc\anim and common\text, its name, its player.</summary>
    private static List<(string Folder, string Name, string Player)> Candidates()
    {
      var list = new List<(string, string, string)>();
      string? iv = Games.InstallFolder(Games.GtaIV, "Windows");
      string? episodes = Games.InstallFolder(Games.Episodes, "Windows");
      if (iv != null) list.Add((iv, "GTA IV", "Niko Bellic"));
      if (episodes != null)
      {
        list.Add((Path.Combine(episodes, "TLAD"), "TLAD", "Johnny Klebitz"));
        list.Add((Path.Combine(episodes, "TBoGT"), "TBoGT", "Luis Lopez"));
      }
      string? current = Games.Current?.Folder;
      return current == null ? list : list.OrderBy(c => c.Item1.StartsWith(current, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
    }

    private static bool HasCut(string folder, File cut)
    {
      string path = Path.Combine(folder, "pc", "anim", "cuts.img");
      if (!System.IO.File.Exists(path))
      {
        return false;
      }
      try
      {
        var install = Games.ForFile(path);
        if (install != null)
        {
          var keyUtil = install.KeyUtil;
          KeyStore.SetKeyLoader(() => keyUtil.FindKey(install.Folder));
        }
        var img = new IMGFileSystem();
        img.Open(path);
        try
        {
          return img.RootDirectory.FindByName(cut.Name) is File same && same.Size == cut.Size;
        }
        finally
        {
          img.Close();
        }
      }
      catch (Exception)
      {
        return false;
      }
    }

    /// <summary>
    /// The subtitles' texts, from the game's american.gxt (all tables are read; the keys are unique), without
    /// the game's formatting marks (~z~: a subtitle; ~n~: a new line).
    /// </summary>
    private static Dictionary<string, string> LoadTexts(string folder, CutFile cut)
    {
      var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      string path = Path.Combine(folder, "common", "text", "american.gxt");
      if (cut.Subtitles.Count == 0 || !System.IO.File.Exists(path))
      {
        return texts;
      }
      try
      {
        var gxt = new RageLib.Text.GxtFile { Charset = RageLib.Text.GxtCharset.Western };
        using (var stream = System.IO.File.OpenRead(path))
        {
          gxt.Open(stream);
        }
        foreach (var subtitle in cut.Subtitles)
        {
          if (gxt.Find(subtitle.Key) is string text)
          {
            text = text.Replace("~n~", " ");
            texts[subtitle.Key] = Regex.Replace(text, @"~[a-zA-Z]~", "").Trim();
          }
        }
      }
      catch (Exception)
      {
        // Without them the subtitles show their keys.
      }
      return texts;
    }
  }
}
