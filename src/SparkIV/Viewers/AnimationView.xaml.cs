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
using RageLib.Animation;

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

    public AnimationView(ViewerContext context, AnimationDocument document)
    {
      InitializeComponent();
      _context = context;
      _document = document;

      PedBox.ItemsSource = document.Peds;
      PedBox.SelectedItem = document.Ped;
      // Only the players of the games installed; with one there is nothing to choose.
      PedContainer.Visibility = document.Peds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
    private ModelScene? Scene => _document.PedScene;

    private void ShowPedMessage()
    {
      PedMessage.Text = _document.PedError ?? "";
      PedMessage.Visibility = _document.PedError != null ? Visibility.Visible : Visibility.Collapsed;
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
        using var page = typeof(ModelView).Assembly.GetManifestResourceStream("SparkIV.ModelView.html")!;
        using var copy = new MemoryStream();
        page.CopyTo(copy);
        body = copy.ToArray();
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
          Working.IsActive = false;
          if (root.GetProperty("webgl").GetBoolean())
          {
            SendPed();
            SendClip(play: false);
          }
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

    /// <summary>The ped: its meshes, its skeleton in rest pose, and how it is shown.</summary>
    private void SendPed()
    {
      var scene = Scene;
      if (scene == null)
      {
        return;
      }
      Post(new
      {
        type = "scene",
        materials = scene.Materials.Select(m => new { id = m.Id, glass = m.Glass, cutout = m.Cutout, texture = m.TextureKey == null ? null : Host + m.TextureKey }),
        meshes = scene.Meshes.Select(m => m.ToMessage()),
      });
      var bones = scene.Bones.OrderBy(b => b.Index).ToList();
      Post(new
      {
        type = "skeleton",
        parent = bones.Select(b => b.Parent),
        position = bones.SelectMany(b => new[] { b.X, b.Y, b.Z }),
        rotation = bones.SelectMany(b => b.Quaternion),
        names = bones.Select(b => b.Name),
      });
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

    /// <summary>The bone tracks of a clip for the bones the ped has (by id); how many matched and didn't.</summary>
    private List<object> BoneTracks(Animation clip, out int matched, out int notInPed)
    {
      matched = 0;
      notInPed = 0;
      var tracks = new List<object>();
      var boneOfId = Scene?.Bones.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Index) ?? new Dictionary<int, int>();
      foreach (var track in clip.Tracks)
      {
        bool position = track.Kind == AnimationTrackKind.BonePosition, rotation = track.Kind == AnimationTrackKind.BoneRotation;
        if (!position && !rotation)
        {
          continue;
        }
        if (!track.IsDecoded || !boneOfId.TryGetValue(track.BoneId, out int bone))
        {
          notInPed++;
          continue;
        }
        tracks.Add(new { bone, kind = position ? "position" : "rotation", values = Sampled(clip, track, position ? 3 : 4) });
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

      var tracks = BoneTracks(clip, out int matched, out int notInPed);

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

      Post(new { type = "clip", frames, fps, tracks, mover, facials, play });

      var notes = new List<string> { matched + " bone tracks played" };
      if (notInPed > 0)
      {
        notes.Add(notInPed + " for bones this ped doesn't have");
      }
      if (facialsFound > 0)
      {
        notes.Add(facialsFound + (facialsFound == 1 ? " facial clip" : " facial clips"));
      }
      if (facialsMissing > 0)
      {
        notes.Add(facialsMissing + " facial " + (facialsMissing == 1 ? "event" : "events") + " without a clip for this face");
      }
      if (mover != null)
      {
        notes.Add("moves through the world" + (s_move ? "" : " (off)"));
      }
      Status.Text = clip.Name + "   ·   " + frames + " frames at " + fps.ToString("0.#") + " fps   ·   " + string.Join("   ·   ", notes);
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
        SendClip(play: _playing);
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
      await Task.Run(() => AnimationViewer.LoadPed(_document, ped));
      PedBox.IsEnabled = true;
      ShowPedMessage();
      _pageReady = false;
      _playing = false;
      Web.CoreWebView2?.Reload();
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

    public void Dispose()
    {
      if (Web.CoreWebView2 != null)
      {
        Web.CoreWebView2.WebResourceRequested -= Core_WebResourceRequested;
      }
      Web.Close();
    }
  }
}
