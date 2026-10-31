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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using RageLib.GtaIV.Pc.Animation;
using RageLib.Common;
using RageLib.FileSystem;
using SparkIV.WinUI.Services;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
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

    /// <summary>How long the opening takes, step by step (SparkIV-load.log beside the exe); the view adds its own steps.</summary>
    public LoadLog? Log { get; init; }

    /// <summary>
    /// Loads the cast into <see cref="Actors"/> (some seconds; the view runs it in the background once it shows,
    /// then sets it to null); null when there is nothing to load or it was loaded.
    /// </summary>
    public Action? LoadCast { get; set; }

    /// <summary>
    /// What the mission script playing the cutscene sets for it (weather, wind, hour, where its marker stands),
    /// from the game's cutscene script index once the cast is loaded; null without one.
    /// </summary>
    public CutsceneScriptIndex.Entry? Script { get; set; }

    /// <summary>The hour the viewer's user set for the cutscene's start, or null for the script's (else noon).</summary>
    public float? StartHour { get; set; }

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
      var log = new LoadLog("cutscene " + file.Name);
      var cut = CutFile.Parse(System.Text.Encoding.ASCII.GetString(data));
      var sections = cut.Sections.Select(s => LoadWad(file, s.Anim)).ToList();
      var actors = CutsceneMessages.Actors(cut);
      log.Step("read the .cut and its clips", cut.Sections.Count + " sections");

      // The game the cutscene is from: the one whose cuts.img has a file of this name and size (GTA IV's and
      // the episodes' names differ); the current game first.
      var game = Candidates().FirstOrDefault(c => HasCut(c.Folder, file));
      if (game.Folder == null)
      {
        return new CutsceneDocument
        {
          Cut = cut, Actors = actors, Sections = sections, Log = log,
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
      log.Step("find the game it is from", game.Name);
      var (audioFile, audioTrack, audioNote) = LoadAudio(game.Folder, cut);
      log.Step("sound", audioNote);
      var texts = LoadTexts(game.Folder, cut);
      log.Step("subtitles");
      // The cast (some seconds) is loaded by the view once it shows, with its spinner (CutsceneDocument.LoadCast).
      var folder = game.Folder;
      var player = game.Player;
      var document = new CutsceneDocument
      {
        Cut = cut, Actors = actors, Sections = sections, Texts = texts, Game = game.Name, GameFolder = game.Folder,
        AudioFile = audioFile, AudioTrack = audioTrack, AudioNote = audioNote, Log = log,
      };
      document.LoadCast = () => document.Script = LoadCast(file.Name, cut, actors, folder, player, install, log);
      return document;
    }

    /// <summary>
    /// The cast: everyone's model and textures, their variations and props, the vehicles' colours; returns what
    /// the script playing the cutscene sets for it (null without one).
    /// </summary>
    private static CutsceneScriptIndex.Entry? LoadCast(string fileName, CutFile cut, List<CutActor> actors, string folder, string player, GameInstall? install, LoadLog log)
    {
      if (install != null)
      {
        // Again: another file opened since may have set another game's key.
        var keyUtil = install.KeyUtil;
        KeyStore.SetKeyLoader(() => keyUtil.FindKey(install.Folder));
      }
      CutsceneMessages.LoadActors(actors, CutsceneMessages.ModelArchives(folder, Games.InstallFolder(Games.GtaIV, "Windows")),
                                  Path.Combine(folder, "pc", "models", "cdimages", "playerped.rpf"), player);
      log.Step("cast: models and their textures", actors.Count + " models");
      CutsceneMessages.ApplyVariations(cut, actors, AppSettings.CutsceneSeeThroughLikeGame);
      CutsceneMessages.LoadProps(cut, actors, folder, Games.InstallFolder(Games.GtaIV, "Windows"));
      // The vehicles' colours as the mission script that plays the cutscene gives them (the game takes those of
      // the mission's vehicle in the world), from the game's cutscene script index - built when the game was
      // loaded, else now (some seconds, once).
      CutsceneScriptIndex? scripts = null;
      try
      {
        scripts = CutsceneScriptIndex.EnsureAsync(folder).GetAwaiter().GetResult();
      }
      catch (Exception)
      {
        // Without the index: the .cut's colours.
      }
      string cutName = Path.GetFileNameWithoutExtension(fileName);
      CutsceneScriptIndex.Entry? entry = null;
      bool found = scripts?.Cutscenes.TryGetValue(cutName, out entry) == true;
      log.Step("cutscene script index", found ? "played by " + entry!.Script : "no script found");
      CutsceneMessages.ApplyPaint(cut, actors, folder, Games.InstallFolder(Games.GtaIV, "Windows"),
                                  scripts == null ? null : model => scripts.ColoursFor(cutName, model));
      log.Step("cast: variations and props");
      return entry;
    }

    /// <summary>The sound the sections name ([AUDIO] B_1), from the game's pc\audio\sfx\cutscenes.rpf.</summary>
    private static (RageLib.Audio.AudioFile?, RageLib.Audio.AudioTrack?, string) LoadAudio(string folder, CutFile cut)
    {
      string? name = cut.Sections.Select(s => s.Audio).FirstOrDefault(a => !string.IsNullOrEmpty(a));
      if (name == null)
      {
        return (null, null, "no sound named");
      }
      // The wave pack the game's audio metadata names for the bank (CUTSCENES\B_1: cutscenes.rpf; TLAD's
      // EP1_SFX/ET01_BA: EP1_SFX.rpf), see CutsceneSound.
      if (CutsceneSound.Find(folder, name) is not (string path, string pack))
      {
        return (null, null, "the sound " + name + " isn't in the game's audio metadata");
      }
      try
      {
        var rpf = new RPFFileSystem();
        rpf.Open(path);
        try
        {
          var file = (rpf.RootDirectory.FindByName(pack) as RageLib.FileSystem.Common.Directory)?.FindByName(name + ".ivaud") as File
                     ?? rpf.RootDirectory.OfType<RageLib.FileSystem.Common.Directory>()
                           .Select(d => d.FindByName(name + ".ivaud") as File)
                           .FirstOrDefault(f => f != null)
                     ?? rpf.RootDirectory.FindByName(name + ".ivaud") as File;
          if (file == null)
          {
            return (null, null, "the sound " + name + " isn't in " + Path.GetFileName(path));
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
        var gxt = new RageLib.GtaIV.Pc.Text.GxtFile { Charset = RageLib.GtaIV.Pc.Text.GxtCharset.Western };
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
