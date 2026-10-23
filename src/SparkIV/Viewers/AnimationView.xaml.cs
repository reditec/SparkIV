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
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using RageLib.GtaIV.Pc.Animation;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>One event in the events list: a whole-number track's value from a frame on.</summary>
  public sealed class AnimationEventRow
  {
    internal AnimationEventRow(int frame, string kind, string value)
    {
      Frame = frame;
      Kind = kind;
      Value = value;
    }

    public int Frame { get; }
    public string FrameLabel => "Frame " + (Frame + 1);
    public string Kind { get; }
    public string Value { get; }
  }

  /// <summary>One clip in the list.</summary>
  public sealed class AnimationClipRow
  {
    internal AnimationClipRow(Animation animation)
    {
      Animation = animation;
      Name = animation.ShortName;
      Detail = animation.FrameCount + " frames  ·  " + animation.Duration.ToString("0.00") + " s  ·  " + animation.Tracks.Count + " tracks";
    }

    public Animation Animation { get; }
    public string Name { get; }
    public string Detail { get; }
  }

  /// <summary>
  /// The clips of an animation dictionary (.wad) played on a ped, in the 3D view of ModelView.html, which
  /// moves the ped's skin with its bones. Playing runs in the page (smooth between frames); it reports the
  /// frame shown.
  /// </summary>
  public sealed partial class AnimationView : UserControl, IDisposable
  {
    private const string Host = "https://anim.sparkiv/";
    private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f, 2.5f };
    private static readonly string[] BackgroundNames = { "", "grey", "black", "white" };

    // Remembered while SparkIV runs.
    private static int s_background;
    private static int s_speed = 3;
    private static bool s_loop = true;
    private static bool s_move;
    private static bool s_facials = true;
    private static bool s_events;

    private readonly ViewerContext _context;
    private readonly AnimationDocument _document;
    private bool _started;
    private bool _pageReady;
    private bool _ready;
    private bool _settingSlider;
    private bool _playing;
    private int _frame;

    // Map objects: the model shown (of the selected clip), and whether to play when the page has it.
    private ObjectModel? _object;
    private string? _objectName;
    private bool _playWhenReady;

    public AnimationView(ViewerContext context, AnimationDocument document)
    {
      InitializeComponent();
      _context = context;
      _document = document;

      PedBox.ItemsSource = document.Peds;
      PedBox.SelectedItem = document.Ped;
      // Only the players of the games installed; with one there is nothing to choose.
      PedContainer.Visibility = document.Peds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
      // Map objects have no face and don't walk.
      if (document.IsObjects)
      {
        FacialsButton.Visibility = Visibility.Collapsed;
        MoveButton.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(FitButton,"Look at the whole object (or double-click the view)");
      }
      ClipList.ItemsSource = document.Animations.Animations.Select(a => new AnimationClipRow(a)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
      BackgroundBox.SelectedIndex = s_background;
      SpeedBox.SelectedIndex = s_speed;
      LoopButton.IsChecked = s_loop;
      MoveButton.IsChecked = s_move;
      FacialsButton.IsChecked = s_facials;
      EventsButton.IsChecked = s_events;
      EventsPanel.Visibility = s_events ? Visibility.Visible : Visibility.Collapsed;
      _ready = true;
      ShowPedMessage();
      if (ClipList.Items.Count > 0)
      {
        ClipList.SelectedIndex = 0;
      }

      // Space plays and pauses, the arrow keys step.
      // The keys aren't shown as a tooltip over the whole view ("Space" wherever the mouse is).
      KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
      KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Space, () => Play_Click(this, new RoutedEventArgs())));
      KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Left, () => Step(-1)));
      KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Right, () => Step(+1)));
      Loaded += AnimationView_Loaded;
    }

    private static KeyboardAccelerator Accelerator(Windows.System.VirtualKey key, Action action)
    {
      var accelerator = new KeyboardAccelerator { Key = key };
      accelerator.Invoked += (s, args) =>
      {
        args.Handled = true;
        action();
      };
      return accelerator;
    }

    private AnimationClipRow? SelectedClip => ClipList.SelectedItem as AnimationClipRow;
    private ModelScene? Scene => _document.IsObjects ? _object?.Scene : _document.PedScene;

    private void ShowPedMessage()
    {
      string? error = _document.IsObjects ? _object?.Error : _document.PedError;
      PedMessage.Text = error ?? "";
      PedMessage.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The selected clip in the page. For a map object, its model first (loaded in the background, then the
    /// page starts again with it) when it is another object's than the one shown.
    /// </summary>
    private async void ShowClip(bool play)
    {
      var clip = SelectedClip?.Animation;
      if (clip == null)
      {
        return;
      }
      if (_document.IsObjects)
      {
        string name = AnimationViewer.ModelNameOf(clip);
        if (!string.Equals(name, _objectName, StringComparison.OrdinalIgnoreCase))
        {
          _objectName = name;
          Working.IsActive = true;
          var loaded = await Task.Run(() => AnimationViewer.LoadObject(_document, name));
          if (_disposed || !string.Equals(name, _objectName, StringComparison.OrdinalIgnoreCase))
          {
            return;  // Closed, or another object's clip was chosen meanwhile.
          }
          _object = loaded;
          ShowPedMessage();
          // The spinner goes on until the page has the model and its textures ("loaded").
          Working.IsActive = loaded.Scene != null;
          _playWhenReady = play;
          RestartPage();
          return;
        }
      }
      SendClip(play);
    }

    #region Page

    private async void AnimationView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;

      if (!await ViewerPages.StartAsync(Web, null, _context, Core_WebMessageReceived, allowCopyMenu: false))
      {
        Working.IsActive = false;
        return;
      }
      var core = Web.CoreWebView2;
      core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
      core.WebResourceRequested += Core_WebResourceRequested;
      core.NavigationStarting += (s, args) =>
      {
        if (!args.Uri.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
        {
          args.Cancel = true;
        }
      };
      core.Navigate(Host + "index.html");
    }

    private void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
      string name = new Uri(args.Request.Uri).AbsolutePath.TrimStart('/');
      byte[]? body = null;
      string type = "image/png";
      if (name == "index.html")
      {
        body = ModelViewPage.Bytes();
        type = "text/html; charset=utf-8";
      }
      else
      {
        Scene?.Textures.TryGetValue(name, out body);
      }
      args.Response = sender.Environment.CreateWebResourceResponse(new MemoryStream(body ?? Array.Empty<byte>()).AsRandomAccessStream(),
                                                                   body != null ? 200 : 404, body != null ? "OK" : "Not Found",
                                                                   "Content-Type: " + type);
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
      using var message = JsonDocument.Parse(args.WebMessageAsJson);
      var root = message.RootElement;
      switch (root.GetProperty("type").GetString())
      {
        case "ready":
          _pageReady = true;
          if (root.GetProperty("webgl").GetBoolean() && Scene != null)
          {
            // The spinner stays until the page says the textures are there ("loaded").
            Working.IsActive = true;
            SendScene();
          }
          else
          {
            Working.IsActive = false;
          }
          if (root.GetProperty("webgl").GetBoolean())
          {
            SendClip(play: _playWhenReady);
          }
          _playWhenReady = false;
          break;
        case "loaded":
          Working.IsActive = false;
          break;
        case "frame":
          _frame = root.GetProperty("frame").GetInt32();
          _playing = root.GetProperty("playing").GetBoolean();
          ShowFrame();
          break;
      }
    }

    private void Post(object message)
    {
      if (_pageReady)
      {
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
      }
    }

    /// <summary>The ped or object: its meshes, its skeleton in rest pose, and how it is shown.</summary>
    private void SendScene()
    {
      var scene = Scene;
      if (scene == null)
      {
        return;
      }
      Post(new
      {
        type = "scene",
        materials = scene.Materials.Select(m => m.ToMessage(key => Host + key)),
        meshes = scene.Meshes.Select(m => m.ToMessage()),
      });
      // A model without a skeleton gets its one bone with the clip (SendClip).
      var bones = scene.Bones.OrderBy(b => b.Index).ToList();
      if (bones.Count > 0)
      {
        Post(new
        {
          type = "skeleton",
          parent = bones.Select(b => b.Parent),
          position = bones.SelectMany(b => new[] { b.X, b.Y, b.Z }),
          rotation = bones.SelectMany(b => b.Quaternion),
          names = bones.Select(b => b.Name),
        });
      }
      Post(new { type = "options", textures = true, wireframe = false });
      Post(new { type = "background", name = BackgroundNames[Math.Clamp(s_background, 0, BackgroundNames.Length - 1)] });
      Post(new { type = "visible", ids = scene.Root.CheckedMeshes().ToArray(), fit = true });
      Post(new { type = "speed", value = Speeds[Math.Clamp(s_speed, 0, Speeds.Length - 1)] });
      Post(new { type = "loop", on = s_loop });
      Post(new { type = "move", on = s_move });
      Post(new { type = "facials", on = s_facials });
    }

    private static float FramesPerSecond(Animation clip) =>
      clip.Duration > 0 && clip.FrameCount > 1 ? (clip.FrameCount - 1) / clip.Duration : 30;

    /// <summary>A track's values at every frame (3 or 4 per frame), for the page.</summary>
    private static string Sampled(Animation clip, AnimationTrack track, int components)
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

    /// <summary>
    /// A map object's root track, as its change since the first frame applied to the root's rest: the track
    /// holds where the object stood in the artist's scene (cj_pigeon_08: 24 m aside, all the time), which
    /// isn't movement.
    /// </summary>
    private static string SampledFromRest(Animation clip, AnimationTrack track, bool position, SceneBone rest)
    {
      int frames = Math.Max(1, clip.FrameCount);
      int components = position ? 3 : 4;
      var values = new float[frames * components];
      var first = track.GetValue(0, clip.SequenceFrameLimit);
      var restTurn = new System.Numerics.Quaternion(rest.Quaternion[0], rest.Quaternion[1], rest.Quaternion[2], rest.Quaternion[3]);
      var firstTurn = position ? System.Numerics.Quaternion.Identity : new System.Numerics.Quaternion(first[0], first[1], first[2], first[3]);
      for (int f = 0; f < frames; f++)
      {
        var value = track.GetValue(f, clip.SequenceFrameLimit);
        if (position)
        {
          values[f * 3] = rest.X + value[0] - first[0];
          values[f * 3 + 1] = rest.Y + value[1] - first[1];
          values[f * 3 + 2] = rest.Z + value[2] - first[2];
        }
        else
        {
          // rest × (first⁻¹ × now): the turn since the first frame, in the root's own frame.
          var now = new System.Numerics.Quaternion(value[0], value[1], value[2], value[3]);
          var turned = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(System.Numerics.Quaternion.Concatenate(now, System.Numerics.Quaternion.Inverse(firstTurn)), restTurn));
          values[f * 4] = turned.X;
          values[f * 4 + 1] = turned.Y;
          values[f * 4 + 2] = turned.Z;
          values[f * 4 + 3] = turned.W;
        }
      }
      return Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    /// <summary>
    /// The bone tracks of a clip for the bones the ped or object has (by id, or by <paramref name="boneOfId"/>);
    /// how many matched and didn't.
    /// </summary>
    private List<object> BoneTracks(Animation clip, out int matched, out int notInPed, Dictionary<int, int>? boneOfId = null)
    {
      matched = 0;
      notInPed = 0;
      var tracks = new List<object>();
      boneOfId ??= Scene?.Bones.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Index) ?? new Dictionary<int, int>();
      int rootById = -1;
      if (_document.IsObjects && boneOfId.ContainsKey(0))
      {
        // A map object's root bone has id 0 in the model, but its clips name it by its own id (cj_dm_flag_1:
        // 5041); in all 26 such clips of the games that is the one id the model lacks, with no track for 0.
        var ids = clip.Tracks.Where(t => t.Kind == AnimationTrackKind.BonePosition || t.Kind == AnimationTrackKind.BoneRotation)
                             .Select(t => (int)t.BoneId)
                             .Distinct()
                             .ToList();
        var lacking = ids.Where(id => !boneOfId.ContainsKey(id)).ToList();
        if (lacking.Count == 1 && !ids.Contains(0))
        {
          rootById = lacking[0];
          boneOfId = new Dictionary<int, int>(boneOfId) { [rootById] = boneOfId[0] };
        }
      }
      foreach (var track in clip.Tracks)
      {
        bool position = track.Kind == AnimationTrackKind.BonePosition, rotation = track.Kind == AnimationTrackKind.BoneRotation;
        if (!position && !rotation)
        {
          continue;
        }
        if (!track.IsDecoded || !boneOfId.TryGetValue(track.BoneId, out int bone))
        {
          // The root of an object without a skeleton (it stays) isn't missing.
          if (track.BoneId != 0 || Scene == null || Scene.Bones.Count > 0)
          {
            notInPed++;
          }
          continue;
        }
        string values = track.BoneId == rootById && Scene?.Bones.FirstOrDefault(b => b.Index == bone) is SceneBone root
          ? SampledFromRest(clip, track, position, root)
          : Sampled(clip, track, position ? 3 : 4);
        tracks.Add(new { bone, kind = position ? "position" : "rotation", values });
        matched++;
      }
      return tracks;
    }

    /// <summary>
    /// The clip for the page: its bone tracks; its movement through the world (model tracks); the facial
    /// clips it starts (FacialAnimation track: the hash of a facial clip's name, from the ped's game). The
    /// events list shows every whole-number track's changes.
    /// </summary>
    private void SendClip(bool play)
    {
      var clip = SelectedClip?.Animation;
      if (clip == null)
      {
        return;
      }
      int frames = Math.Max(1, clip.FrameCount);
      float fps = FramesPerSecond(clip);
      FrameSlider.Maximum = Math.Max(1, frames - 1);
      _frame = 0;

      var objectNotes = new List<string>();
      Dictionary<int, int>? oneBone = _document.IsObjects ? OneBoneOf(clip, objectNotes) : null;
      var tracks = BoneTracks(clip, out int matched, out int notInPed, oneBone);
      object? uv = _document.IsObjects ? TextureMatrix(clip, objectNotes) : null;

      var moverPosition = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.ModelPosition && t.IsDecoded);
      var moverRotation = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.ModelRotation && t.IsDecoded);
      object? mover = moverPosition == null && moverRotation == null ? null : new
      {
        position = moverPosition == null ? null : new { bone = 0, kind = "position", values = Sampled(clip, moverPosition, 3) },
        rotation = moverRotation == null ? null : new { bone = 0, kind = "rotation", values = Sampled(clip, moverRotation, 4) },
      };
      MoveButton.IsEnabled = mover != null;

      // Events, and the facial clips started.
      var events = new List<AnimationEventRow>();
      var facials = new List<object>();
      int facialsFound = 0, facialsMissing = 0;
      foreach (var track in clip.Tracks.Where(t => t.DataType == 3 && t.IsDecoded))
      {
        uint? last = null;
        for (int f = 0; f < frames; f++)
        {
          uint? value = track.GetInteger(f, clip.SequenceFrameLimit);
          if (value == last)
          {
            continue;
          }
          last = value;
          if (value == null || (value == 0 && f == 0))
          {
            continue;
          }
          string text = "0x" + value.Value.ToString("x8");
          if (track.Kind == AnimationTrackKind.FacialAnimation)
          {
            if (value == 0)
            {
              text = "(none)";
            }
            else if (_document.Facials.TryGetValue(value.Value, out var facial))
            {
              var facialTracks = BoneTracks(facial.Clip, out int onFace, out _);
              if (onFace > 0)
              {
                text = facial.Clip.ShortName + "   (" + facial.Source + ", " + facial.Clip.FrameCount + " frames)";
                facialsFound++;
                facials.Add(new
                {
                  start = f,
                  frames = Math.Max(1, facial.Clip.FrameCount),
                  fps = FramesPerSecond(facial.Clip),
                  tracks = facialTracks,
                });
              }
              else
              {
                // Only in the dictionaries of simpler faces (facials@m_lo: other bones than a player's face).
                text = facial.Clip.ShortName + "   (" + facial.Source + ": made for simpler faces, none of its bones are in this ped's face)";
                facialsMissing++;
              }
            }
            else
            {
              text += "   (no facial clip of this name in the game)";
              facialsMissing++;
            }
          }
          else if (track.Kind == AnimationTrackKind.UV1)
          {
            text = value == 0 ? "(none)"
              : _document.SoundNames.TryGetValue(value.Value, out var sound) ? sound
              : text + "   (sound not named in the game's audio files)";
          }
          else if (track.Kind == AnimationTrackKind.ActionFlags && value != 0)
          {
            var bits = Enumerable.Range(0, 32).Where(b => (value.Value & (1u << b)) != 0);
            text += "   bits " + string.Join(", ", bits.Select(b => ActionFlagNames.TryGetValue(b, out var name) ? b + " (" + name + ")" : b.ToString()));
          }
          events.Add(new AnimationEventRow(f, IntegerTrackName(track.Kind), text));
        }
      }
      events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
      EventList.ItemsSource = events;
      NoEventsText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      FacialsButton.IsEnabled = facials.Count > 0;
      ShowFrame();

      Post(new { type = "clip", frames, fps, tracks, mover = _document.IsObjects ? null : mover, facials, uv, play });

      var notes = new List<string>();
      if (_document.IsObjects && _object?.Source != null)
      {
        notes.Add(_object.Source);
      }
      if (matched > 0 || uv == null)
      {
        notes.Add(matched + " bone tracks played");
      }
      if (notInPed > 0)
      {
        notes.Add(notInPed + (_document.IsObjects ? " for bones the model doesn't have" : " for bones this ped doesn't have"));
      }
      notes.AddRange(objectNotes);
      if (facialsFound > 0)
      {
        notes.Add(facialsFound + (facialsFound == 1 ? " facial clip" : " facial clips"));
      }
      if (facialsMissing > 0)
      {
        notes.Add(facialsMissing + " facial " + (facialsMissing == 1 ? "event" : "events") + " without a clip for this face");
      }
      if (mover != null && !_document.IsObjects)
      {
        notes.Add("moves through the world" + (s_move ? "" : " (off)"));
      }
      Status.Text = clip.Name + "   ·   " + frames + " frames at " + fps.ToString("0.#") + " fps   ·   " + string.Join("   ·   ", notes);
    }

    /// <summary>
    /// For a model without a skeleton (bkn2_bs_logobust2): the clip turns it as a whole when it moves one
    /// bone besides the root (the object's own, its pivot at that track's place); the page gets that one
    /// bone, in rest where the clip starts. Null otherwise (the model's bones are used).
    /// </summary>
    private Dictionary<int, int>? OneBoneOf(Animation clip, List<string> notes)
    {
      var scene = Scene;
      if (scene == null || scene.Bones.Count > 0)
      {
        return null;
      }
      var ids = clip.Tracks.Where(t => (t.Kind == AnimationTrackKind.BonePosition || t.Kind == AnimationTrackKind.BoneRotation) && t.IsDecoded && t.BoneId != 0)
                           .Select(t => (int)t.BoneId)
                           .Distinct()
                           .ToList();
      if (ids.Count == 0)
      {
        return null;
      }
      if (ids.Count > 1)
      {
        // Several parts (p_crane_3: the hook among them), but nothing says which vertices belong to which.
        notes.Add("the model has no skeleton, so its " + ids.Count + " parts can't move apart");
        return new Dictionary<int, int>();
      }
      int id = ids[0];
      var position = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.BonePosition && t.BoneId == id && t.IsDecoded)?.GetValue(0, clip.SequenceFrameLimit);
      var rotation = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.BoneRotation && t.BoneId == id && t.IsDecoded)?.GetValue(0, clip.SequenceFrameLimit);
      Post(new
      {
        type = "skeleton",
        parent = new[] { -1 },
        position = position == null ? new[] { 0f, 0f, 0f } : new[] { position[0], position[1], position[2] },
        rotation = rotation == null ? new[] { 0f, 0f, 0f, 1f } : new[] { rotation[0], rotation[1], rotation[2], rotation[3] },
        names = new[] { AnimationViewer.ModelNameOf(clip) },
      });
      return new Dictionary<int, int> { [id] = 0 };
    }

    /// <summary>
    /// A clip moving a texture ("_uv_N"): material N's texture matrix at every frame, its two rows (u' =
    /// row0 · (u, v, 1), v' = row1 · (u, v, 1)); null for other clips.
    /// </summary>
    private object? TextureMatrix(Animation clip, List<string> notes)
    {
      int material = AnimationViewer.UvMaterialOf(clip);
      if (material < 0)
      {
        return null;
      }
      var scene = Scene;
      var row0 = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.UV0 && t.DataType != 3 && t.IsDecoded);
      var row1 = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.UV1 && t.DataType != 3 && t.IsDecoded);
      if (scene == null || row0 == null || row1 == null)
      {
        return null;
      }
      if (material >= scene.Materials.Count)
      {
        notes.Add("moves the texture of material " + material + ", which the model doesn't have (it has " + scene.Materials.Count + ")");
        return null;
      }
      var target = scene.Materials[material];
      notes.Add("moves the texture of material " + material + (target.TextureName != null ? " (" + target.TextureName + ")" : ""));
      return new[] { new { material, row0 = Sampled(clip, row0, 3), row1 = Sampled(clip, row1, 3) } };
    }

    // What the ActionFlags bits signal. Documented nowhere (OpenIV shows the number); worked out from the
    // clips of GTA IV that set them. The feet checked against Niko's feet in move_player/walk and run (the
    // heel down, then the foot flat); the others from the clips they are in (15 in every fire and blindfire
    // clip, one frame after 9; 11 in unholster and create clips, 12 in holster ones, 16 and 17 in vehicle
    // door clips), with a question mark where that is less clear-cut. Bits not listed are unknown.
    private static readonly Dictionary<int, string> ActionFlagNames = new Dictionary<int, string>
    {
      [5] = "left heel down",
      [6] = "right heel down",
      [7] = "left foot flat",
      [8] = "right foot flat",
      [9] = "before shot or hit?",
      [11] = "object into hand",
      [12] = "object put away",
      [13] = "object thrown away",
      [15] = "shot",
      [16] = "vehicle door?",
      [17] = "vehicle door?",
      [18] = "smash window",
      [22] = "smoke puff",
      [23] = "swim stroke?",
      [24] = "swim stroke?",
      [25] = "swim stroke?",
    };

    // The names OpenIV uses for whole-number tracks; 131 holds sounds there, not texture coordinates.
    private static string IntegerTrackName(AnimationTrackKind kind) =>
      kind == AnimationTrackKind.UV1 ? "AudioEvent"
      : Enum.IsDefined(typeof(AnimationTrackKind), kind) ? kind.ToString() : "Track " + (int)kind;

    #endregion

    #region Controls

    private void ShowFrame()
    {
      var clip = SelectedClip?.Animation;
      int frames = clip?.FrameCount ?? 0;
      FrameText.Text = frames > 0 ? "Frame " + (_frame + 1) + " / " + frames : "";
      _settingSlider = true;
      FrameSlider.Value = _frame;
      _settingSlider = false;
      PlayGlyph.Glyph = ((char)(_playing ? 0xE769 : 0xE768)).ToString();
      ToolTipService.SetToolTip(PlayButton, _playing ? "Pause (space)" : "Play (space)");

      // The event reached last.
      if (EventsPanel.Visibility == Visibility.Visible && EventList.ItemsSource is List<AnimationEventRow> events)
      {
        var current = events.LastOrDefault(e => e.Frame <= _frame);
        if (!ReferenceEquals(EventList.SelectedItem, current))
        {
          EventList.SelectedItem = current;
          if (current != null)
          {
            EventList.ScrollIntoView(current);
          }
        }
      }
    }

    private void Facials_Click(object sender, RoutedEventArgs e)
    {
      s_facials = FacialsButton.IsChecked == true;
      Post(new { type = "facials", on = s_facials });
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
      s_move = MoveButton.IsChecked == true;
      Post(new { type = "move", on = s_move });
      Status.Text = s_move ? Status.Text.Replace("moves through the world (off)", "moves through the world")
                           : Status.Text.Replace("moves through the world", "moves through the world (off)").Replace("(off) (off)", "(off)");
      if (!s_move)
      {
        Post(new { type = "fit" });
      }
    }

    private void Events_Click(object sender, RoutedEventArgs e)
    {
      s_events = EventsButton.IsChecked == true;
      EventsPanel.Visibility = s_events ? Visibility.Visible : Visibility.Collapsed;
      ShowFrame();
    }

    /// <summary>An event clicked: to its frame.</summary>
    private void EventList_ItemClick(object sender, ItemClickEventArgs e)
    {
      if (e.ClickedItem is AnimationEventRow row)
      {
        _playing = false;
        _frame = row.Frame;
        Post(new { type = "seek", frame = _frame });
        ShowFrame();
      }
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_ready)
      {
        // A new clip plays at once (as in OpenIV) while something was playing; else it shows its first frame.
        ShowClip(play: _playing);
      }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
      _playing = !_playing;
      Post(new { type = "play", on = _playing });
      ShowFrame();
    }

    private void Step(int by)
    {
      var clip = SelectedClip?.Animation;
      if (clip == null)
      {
        return;
      }
      _playing = false;
      _frame = Math.Clamp(_frame + by, 0, Math.Max(0, clip.FrameCount - 1));
      Post(new { type = "seek", frame = _frame });
      ShowFrame();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(+1);
    private void First_Click(object sender, RoutedEventArgs e) => Step(-int.MaxValue / 2);
    private void Last_Click(object sender, RoutedEventArgs e) => Step(int.MaxValue / 2);

    private void FrameSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
      if (_settingSlider || !_ready)
      {
        return;
      }
      _playing = false;
      _frame = (int)Math.Round(e.NewValue);
      Post(new { type = "seek", frame = _frame });
      ShowFrame();
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
      s_loop = LoopButton.IsChecked == true;
      Post(new { type = "loop", on = s_loop });
    }

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready || SpeedBox.SelectedIndex < 0)
      {
        return;
      }
      s_speed = SpeedBox.SelectedIndex;
      Post(new { type = "speed", value = Speeds[s_speed] });
    }

    private void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready)
      {
        return;
      }
      s_background = Math.Max(0, BackgroundBox.SelectedIndex);
      Post(new { type = "background", name = BackgroundNames[s_background] });
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Post(new { type = "fit" });

    /// <summary>Another ped: loaded in the background, then the page starts again with it.</summary>
    private async void PedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready || PedBox.SelectedItem is not PedChoice ped || ped == _document.Ped)
      {
        return;
      }
      Working.IsActive = true;
      PedBox.IsEnabled = false;
      _playWhenReady = false;
      await Task.Run(() => AnimationViewer.LoadPed(_document, ped));
      PedBox.IsEnabled = true;
      ShowPedMessage();
      _playing = false;
      RestartPage();
    }

    /// <summary>
    /// The page from the start, for another ped or object; it asks for the scene when ready. Navigating
    /// again rather than Reload: Reload fails while the page is still loading (two objects' clips clicked
    /// quickly). Nothing when the page isn't there yet (it loads with the scene then) or the view closed.
    /// </summary>
    private void RestartPage()
    {
      if (_disposed || Web.CoreWebView2 == null)
      {
        return;
      }
      _pageReady = false;
      try
      {
        Web.CoreWebView2.Navigate(Host + "index.html");
      }
      catch (COMException)
      {
        // The view is closing.
      }
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _context.File.Name;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    #endregion

    private bool _disposed;

    public void Dispose()
    {
      _disposed = true;
      if (Web.CoreWebView2 != null)
      {
        Web.CoreWebView2.WebResourceRequested -= Core_WebResourceRequested;
      }
      Web.Close();
    }
  }
}
