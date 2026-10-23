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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>A section in the list.</summary>
  public sealed class CutSectionRow
  {
    internal CutSectionRow(int index, CutSection section, bool hasAnimations)
    {
      Index = index;
      Name = "Section " + (index + 1);
      Detail = section.Duration.ToString("0.0") + " s  ·  " + section.Models.Count + " models  ·  " + (section.Anim ?? "?") + (hasAnimations ? "" : " (not beside the file)");
    }

    public int Index { get; }
    public string Name { get; }
    public string Detail { get; }
  }

  /// <summary>One of the cast in the list: the model, where it came from or why it is missing.</summary>
  public sealed class CutActorRow
  {
    internal CutActorRow(CutActor actor)
    {
      Name = actor.Model;
      Detail = actor.Scene != null ? actor.Source ?? "" : actor.Error ?? "not loaded";
    }

    public string Name { get; }
    public string Detail { get; }
  }

  /// <summary>A subtitle in the list: when it is said (in the whole scene), its text.</summary>
  public sealed class CutSubtitleRow
  {
    internal CutSubtitleRow(CutSubtitle subtitle, string text, int section)
    {
      Subtitle = subtitle;
      Text = text;
      Seconds = subtitle.Start / 1000.0;
      Time = TimeSpan.FromSeconds(Seconds).ToString(@"m\:ss\.f") + "  ·  section " + (section + 1);
    }

    public CutSubtitle Subtitle { get; }
    public string Text { get; }
    public double Seconds { get; }
    public string Time { get; }
  }

  /// <summary>
  /// A cutscene played in the 3D view of ModelView.html: every actor of a section with its clip, through
  /// the scene's camera or freely, the sections one after the other on one time line, with the scene's
  /// sound (which keeps the time: the page follows it) and the subtitles shown when they are said.
  /// </summary>
  public sealed partial class CutsceneView : UserControl, IDisposable
  {
    private const string Host = "https://cut.sparkiv/";
    private static readonly string[] BackgroundNames = { "", "grey", "black", "white" };

    // Remembered while SparkIV runs.
    private static int s_background;
    private static bool s_camera = true;
    private static bool s_showCamera = true;
    private static bool s_subtitles = true;
    private static bool s_lights = true;
    private static bool s_location = true;
    private static double s_volume = 100;

    private readonly ViewerContext _context;
    private readonly CutsceneDocument _document;
    private readonly List<CutSubtitleRow> _subtitles = new List<CutSubtitleRow>();
    // Where each section starts in the whole scene, in seconds; the last entry is the whole length.
    private readonly double[] _sectionStart;
    private readonly RageLib.Audio.AudioPlayer? _audio;
    private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _started;
    private bool _pageReady;
    private bool _ready;
    private bool _settingSlider;
    private bool _settingSection;
    private bool _playing;
    // The section in the page, its frames and the frame shown there.
    private int _section = -1;
    private int _frame;
    private int _frames = 1;
    private float _fps = 30;
    // The meshes shown: the cast's, the location's (once loaded); what the status says of the location.
    private List<int> _castVisible = new List<int>();
    private List<int> _locationVisible = new List<int>();
    private bool _locationLoading;
    private string? _locationNote;
    // The cast loading in the background after the view opened (see LoadCastAsync).
    private System.Threading.Tasks.Task? _castTask;
    private bool _castLoading;

    public CutsceneView(ViewerContext context, CutsceneDocument document)
    {
      InitializeComponent();
      _context = context;
      _document = document;

      var cut = document.Cut;
      SectionList.ItemsSource = cut.Sections.Select((s, i) => new CutSectionRow(i, s, document.Sections[i] != null)).ToList();
      CastList.ItemsSource = document.Actors.Select(a => new CutActorRow(a)).ToList();
      _sectionStart = new double[cut.Sections.Count + 1];
      for (int i = 0; i < cut.Sections.Count; i++)
      {
        _sectionStart[i + 1] = _sectionStart[i] + cut.Sections[i].Duration;
      }
      foreach (var subtitle in cut.Subtitles)
      {
        _subtitles.Add(new CutSubtitleRow(subtitle, document.Texts.TryGetValue(subtitle.Key, out var text) ? text : subtitle.Key, Locate(subtitle.Start / 1000.0).Section));
      }
      SubtitleList.ItemsSource = _subtitles;
      TimeSlider.Maximum = Math.Max(1, Math.Round(Length * 1000));

      if (document.AudioFile != null && document.AudioTrack != null)
      {
        try
        {
          _audio = new RageLib.Audio.AudioPlayer();
          _audio.Initialize(document.AudioFile, document.AudioTrack);
        }
        catch (Exception)
        {
          _audio = null;
        }
      }
      _timer.Tick += Timer_Tick;

      BackgroundBox.SelectedIndex = s_background;
      CameraButton.IsChecked = s_camera;
      ShowCameraButton.IsChecked = s_showCamera;
      SubtitlesButton.IsChecked = s_subtitles;
      LightsButton.IsChecked = s_lights;
      LocationButton.IsChecked = s_location;
      VolumeSlider.Value = s_volume;
      // Without the scene's sound there is nothing to set.
      VolumePanel.Visibility = _audio != null ? Visibility.Visible : Visibility.Collapsed;
      ShowVolume();
      LocationButton.IsEnabled = document.GameFolder != null && document.Cut.Offset != null;
      Message.Text = document.Error ?? "";
      Message.Visibility = document.Error != null ? Visibility.Visible : Visibility.Collapsed;
      _ready = true;

      // The keys aren't shown as a tooltip over the whole view ("Space" wherever the mouse is).
      KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
      KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Space, () => Play_Click(this, new RoutedEventArgs())));
      KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Left, () => Step(-1)));
      KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Right, () => Step(+1)));
      Loaded += CutsceneView_Loaded;
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

    /// <summary>The whole scene, in seconds.</summary>
    private double Length => _sectionStart[^1];

    /// <summary>A section's frames a second: its clips' (30 in GTA IV's).</summary>
    private float FpsOf(int section)
    {
      var clip = section >= 0 && section < _document.Sections.Count ? _document.Sections[section]?.Animations.FirstOrDefault() : null;
      return clip != null ? CutsceneMessages.FramesPerSecond(clip) : 30;
    }

    /// <summary>The section and its frame at a time of the whole scene.</summary>
    private (int Section, int Frame) Locate(double seconds)
    {
      int count = _document.Cut.Sections.Count;
      if (count == 0)
      {
        return (0, 0);
      }
      int section = 0;
      while (section + 1 < count && seconds >= _sectionStart[section + 1])
      {
        section++;
      }
      return (section, Math.Max(0, (int)Math.Round((seconds - _sectionStart[section]) * FpsOf(section))));
    }

    /// <summary>The time of the whole scene shown now.</summary>
    private double Now => (_section >= 0 ? _sectionStart[_section] : 0) + _frame / (double)_fps;

    #region Page

    private async void CutsceneView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;
      DrawSectionMarks();
      // The cast loads now, in the background, while the page starts (the view is already there).
      _castTask = LoadCastAsync();

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
      string name = Uri.UnescapeDataString(new Uri(args.Request.Uri).AbsolutePath.TrimStart('/'));
      byte[]? body;
      string type = "image/png";
      if (name == "index.html")
      {
        body = ModelViewPage.Bytes();
        type = "text/html; charset=utf-8";
      }
      else
      {
        body = name.StartsWith("m", StringComparison.Ordinal) ? _document.Location?.Texture(name) : CutsceneMessages.Texture(_document.Actors, name);
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
          _document.Log?.Step("open the view, start the page (WebView2)");
          if (!root.GetProperty("webgl").GetBoolean())
          {
            Working.IsActive = false;
            break;
          }
          ShowSceneWhenCastLoaded();
          break;
        case "loaded":
          // For the load log: the page's textures of the cast, then (later) the location's, are there.
          if (_document.Log is Services.LoadLog log && _pageWait != null)
          {
            log.Add(_pageWaitFor!, _pageWait.Elapsed.TotalSeconds);
            if (root.TryGetProperty("sceneSeconds", out var made) && made.TryGetDouble(out double seconds))
            {
              log.Add("  of which: meshes and materials made on the page", seconds);
            }
            _pageWait = null;
            if (_pageWaitFor!.Contains("location") || !s_location || _document.Cut.Offset == null || _document.GameFolder == null)
            {
              log.Write();
            }
          }
          // The cast's textures are there; the spinner goes on while the location still loads (its scene
          // says "loaded" again when its textures are there).
          Working.IsActive = _locationLoading;
          break;
        case "frame":
          _frame = root.GetProperty("frame").GetInt32();
          // With the sound, it keeps the time and says when the next section begins (the timer); without,
          // the page's end of a section while playing goes on with the next.
          if (_audio == null && _playing && !root.GetProperty("playing").GetBoolean())
          {
            if (_section + 1 < _document.Cut.Sections.Count)
            {
              ShowSection(_section + 1, 0, play: true);
              return;
            }
            _playing = false;
          }
          ShowFrame();
          break;
        case "filmCamera":
          // The view was turned by mouse: the cutscene's camera is left.
          s_camera = false;
          CameraButton.IsChecked = false;
          break;
      }
    }

    /// <summary>
    /// The cast (CutsceneDocument.LoadCast: models, textures, variations, props, colours; some seconds) loaded in
    /// the background once the view shows, the spinner going and the status saying so; then the cast list again.
    /// </summary>
    private async System.Threading.Tasks.Task LoadCastAsync()
    {
      var load = _document.LoadCast;
      if (load == null)
      {
        return;
      }
      _castLoading = true;
      Working.IsActive = true;
      Status.Text = "loading the cast …";
      try
      {
        await System.Threading.Tasks.Task.Run(load);
      }
      catch (Exception ex)
      {
        if (!_disposed)
        {
          Message.Text = "The cast couldn't be loaded: " + ex.Message;
          Message.Visibility = Visibility.Visible;
        }
      }
      finally
      {
        _document.LoadCast = null;
        _castLoading = false;
      }
      if (_disposed)
      {
        return;
      }
      CastList.ItemsSource = _document.Actors.Select(a => new CutActorRow(a)).ToList();
      ShowStatus();
    }

    /// <summary>The page is ready: the scene sent once the cast is there (at once when it already is).</summary>
    private async void ShowSceneWhenCastLoaded()
    {
      if (_castTask != null)
      {
        await _castTask;
      }
      if (_disposed)
      {
        return;
      }
      SendScene();
      ShowSection(Math.Max(0, _section), _frame, play: false);
    }

    private void Post(object message)
    {
      if (_pageReady && !_disposed)
      {
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
      }
    }

    /// <summary>Everyone's meshes (each moves with its own skeleton), and how they are shown.</summary>
    private void SendScene()
    {
      var (scene, visible) = CutsceneMessages.Scene(_document.Actors, key => Host + key);
      if (visible.Count == 0)
      {
        Working.IsActive = false;
      }
      _castVisible = visible;
      if (_document.Log != null && _document.Location == null)
      {
        _pageWait = System.Diagnostics.Stopwatch.StartNew();
        _pageWaitFor = "page: the cast's textures uploaded";
      }
      Post(scene);
      Post(new { type = "options", textures = true, wireframe = false });
      PostBackground();
      Post(new { type = "visible", ids = visible, fit = true });
      Post(new { type = "sceneLights", on = s_lights });
      // The sections play one after the other (here), not each over again.
      Post(new { type = "loop", on = false });
      // The location: sent again to a page started anew, else loaded now when it is to be shown.
      if (_document.Location != null)
      {
        SendLocation();
      }
      else if (s_location)
      {
        LoadLocation();
      }
    }

    /// <summary>The map around the scene, loaded in the background (some seconds), then shown.</summary>
    private async void LoadLocation()
    {
      string? folder = _document.GameFolder;
      float[]? offset = _document.Cut.Offset;
      // (While the cast loads: after it, with the scene - SendScene - as before.)
      if (_locationLoading || _castLoading || _document.Location != null || folder == null || offset == null)
      {
        return;
      }
      _locationLoading = true;
      Working.IsActive = true;
      _locationNote = "loading the location …";
      ShowStatus();
      try
      {
        var removals = _document.Cut.Removals;
        // How far the game draws in a cutscene (GTAIV.exe): the [DRAW_DISTANCE] line in force goes to the
        // cutscene's camera (0x9CA620 -> 0xC61D80, +0x154), which 0xD53280 makes its far clip, 800 and a flag
        // without any line; 0xC46050 then keeps it only as far as the time cycle's FarClp (0x15E8954, column
        // 23), with the flag (no line: et01_ba) it takes FarClp. So the smaller of the two, FarClp without lines.
        float farClip = TimeCycle.Load(folder, OutsideWeather, OutsideHour)?.FarClip ?? 0;
        float drawDistance = _document.Cut.DrawDistance > 0 && (farClip <= 0 || _document.Cut.DrawDistance < farClip) ? _document.Cut.DrawDistance : farClip;
        _drawDistance = drawDistance;
        var log = _document.Log;
        _document.Location = await System.Threading.Tasks.Task.Run(() => CutsceneLocation.Load(folder, offset, removals, LocationRadius, drawDistance, OutsideHour, log));
      }
      catch (Exception ex)
      {
        _locationNote = "no location: " + ex.Message;
        ShowStatus();
        _locationLoading = false;
        Working.IsActive = false;
        return;
      }
      finally
      {
        _locationLoading = false;
      }
      if (_disposed)
      {
        return;
      }
      var location = _document.Location;
      _locationNote = "location: " + location.Objects.Count + " objects within " + LocationRadius + " m"
                      + (location.Far > 0 ? ", " + location.Far + " far ones up to " + _drawDistance.ToString("0") + " m" : "")
                      + (location.Outdoors ? "" : ", inside")
                      + (location.Hidden > 0 ? ", " + location.Hidden + " hidden by the scene" : "")
                      + (location.Missing > 0 ? ", " + location.Missing + " not found" : "");
      ShowStatus();
      SendLocation();
    }

    // How far round the scene's origin the map is shown, in metres.
    private const float LocationRadius = 60;

    // The load log: how long the page has been loading textures, and what for.
    private System.Diagnostics.Stopwatch? _pageWait;
    private string? _pageWaitFor;

    // A scene outside: the weather and hour its light is of (CutsceneLocation.Load shows timed objects at it too).
    private const string OutsideWeather = "SUNNY";
    private const int OutsideHour = 12;
    // Whether the location's light from the time cycle was sent (the page then lights and shows it as the game).
    private bool _lit;
    // How far the location's far models were loaded (the [DRAW_DISTANCE] as the game limits it by the time cycle).
    private float _drawDistance;

    private void SendLocation()
    {
      if (_document.Location == null)
      {
        return;
      }
      // After the cast's ids (meshes and materials), well clear of them.
      var (scene, visible) = _document.Location.Message(key => Host + key, 100000, 100000);
      if (_document.Log != null)
      {
        _document.Log.Step("location: made into the page's scene");
        _pageWait = System.Diagnostics.Stopwatch.StartNew();
        _pageWaitFor = "page: the location's textures uploaded";
      }
      Post(scene);
      Post(_document.Location.LightsMessage());
      // The light of the game's time cycle (the .cut gives no time or weather: noon, sunny), and for each room of
      // the location as its time cycle modifier changes it: the page lights by the one of the room the camera
      // is in, as the game does, inside the same way as outside.
      _lit = false;
      if (_document.GameFolder is string folder && TimeCycle.Load(folder, OutsideWeather, OutsideHour) is TimeCycle cycle)
      {
        var modifiers = _document.Location.Rooms.Any(r => r.Modifier != 0) ? TimeCycle.LoadModifiers(folder) : null;
        var rooms = _document.Location.Rooms.Select(r =>
          modifiers != null && modifiers.TryGetValue(r.Modifier, out var modifier) ? cycle.With(modifier).Message(OutsideHour) : null).ToArray();
        Post(cycle.Message(OutsideHour, _document.Location.Clouds != null ? Host + CutsceneLocation.CloudKey : null,
                           _document.Location.CloudDetail != null ? Host + CutsceneLocation.CloudDetailKey : null, rooms));
        _lit = true;
      }
      _locationVisible = visible;
      PostVisible();
      PostBackground();
    }

    /// <summary>
    /// The view's background: with the location shown the scene's own (outside the time cycle's sky, inside
    /// black, as the game's world beyond the windows would be dark against the room), the choice greyed;
    /// without it the one chosen.
    /// </summary>
    private void PostBackground()
    {
      bool own = s_location && _document.Location != null;
      bool outside = own && _lit;
      BackgroundBox.IsEnabled = !own;
      Post(new { type = "useSky", on = outside });
      Post(new { type = "background", name = own ? (outside ? "" : "black") : BackgroundNames[Math.Clamp(s_background, 0, BackgroundNames.Length - 1)] });
    }

    private void PostVisible() =>
      Post(new { type = "visible", ids = s_location ? _castVisible.Concat(_locationVisible).ToList() : _castVisible, fit = false });

    /// <summary>A section in the page: its timeline, every actor's clip, the camera; at a frame, playing or not.</summary>
    private void ShowSection(int index, int frame, bool play)
    {
      var cut = _document.Cut;
      if (index < 0 || index >= cut.Sections.Count)
      {
        return;
      }
      var section = cut.Sections[index];
      var wad = _document.Sections[index];
      var (timeline, cast, camera, notes) = CutsceneMessages.Section(section, _document.Actors, wad, cut.Sections.Take(index));
      _section = index;
      _fps = FpsOf(index);
      var clip = wad?.Animations.FirstOrDefault();
      _frames = clip != null ? Math.Max(1, clip.FrameCount) : Math.Max(1, (int)(section.Duration * _fps) + 1);
      _frame = Math.Clamp(frame, 0, _frames - 1);
      if (SectionList.SelectedIndex != index)
      {
        _settingSection = true;
        SectionList.SelectedIndex = index;
        _settingSection = false;
      }

      // All in one message: the page draws once, the section complete (one by one, a picture came between
      // them: the new clip with the old cast and camera, a frame that tore the cutscene).
      var messages = new List<object>
      {
        timeline,
        cast,
        camera ?? new { type = "camera", camera = (object?)null },
        new { type = "useCamera", on = s_camera && camera != null },
        new { type = "showCamera", on = s_showCamera },
        // How far the game draws, shot by shot (the map beyond isn't drawn).
        new { type = "drawDistance", steps = cut.DrawDistanceSteps(index, _fps) },
        // The fade in at the start of the first section, out at the end of the last (in the section's frames).
        new
        {
          type = "fade",
          inEnd = index == 0 ? cut.FadeIn * _fps : 0,
          outStart = index == cut.Sections.Count - 1 && cut.FadeOut > 0 ? Math.Max(0, _frames - 1 - cut.FadeOut * _fps) : -1,
          outEnd = _frames - 1,
        },
      };
      if (_frame > 0)
      {
        messages.Add(new { type = "seek", frame = _frame });
      }
      if (play)
      {
        messages.Add(new { type = "play", on = true });
      }
      Post(new { type = "batch", messages });
      ShowFrame();

      var parts = new List<string>
      {
        _frames + " frames at " + _fps.ToString("0.#") + " fps",
        _document.Actors.Count(a => a.Scene != null) + " of " + _document.Actors.Count + " models",
      };
      if (section.Lights.Count > 0)
      {
        parts.Add(section.Lights.Count + (section.Lights.Count == 1 ? " light" : " lights"));
      }
      if (_document.Game != null)
      {
        parts.Add("models and texts of " + _document.Game);
      }
      if (_document.AudioNote != null)
      {
        parts.Add(_document.AudioNote);
      }
      if (camera == null)
      {
        parts.Add("no camera clip");
      }
      parts.AddRange(notes);
      _sectionStatus = string.Join("   ·   ", parts);
      ShowStatus();
    }

    private string _sectionStatus = "";

    private void ShowStatus() => Status.Text = _locationNote == null ? _sectionStatus : _sectionStatus + "   ·   " + _locationNote;

    private void Location_Click(object sender, RoutedEventArgs e)
    {
      s_location = LocationButton.IsChecked == true;
      if (s_location && _document.Location == null)
      {
        LoadLocation();
      }
      PostVisible();
      PostBackground();
    }

    #endregion

    #region Playing

    /// <summary>To a time of the whole scene: the section it is in, the sound there; playing on or not.</summary>
    private void GoTo(double seconds, bool play)
    {
      // Not while the cast still loads (its actors are being filled in the background); the page shows the
      // section it was at once they are there.
      if (_castLoading)
      {
        return;
      }
      seconds = Math.Clamp(seconds, 0, Length);
      var (section, frame) = Locate(seconds);
      _playing = play;
      if (section != _section)
      {
        ShowSection(section, frame, play);
      }
      else
      {
        _frame = Math.Clamp(frame, 0, _frames - 1);
        Post(new { type = "seek", frame = _frame });
        if (play)
        {
          Post(new { type = "play", on = true });
        }
        ShowFrame();
      }
      AudioTo(seconds, play);
      _timer.Start();
    }

    private void AudioTo(double seconds, bool play)
    {
      if (_audio == null)
      {
        return;
      }
      try
      {
        _audio.Pause();
        _audio.Seek(TimeSpan.FromSeconds(seconds));
        if (play)
        {
          _audio.Play();
        }
      }
      catch (Exception)
      {
        // No sound device: the pictures play on without.
      }
    }

    /// <summary>
    /// While playing with sound: the picture follows the sound, to the next section when the sound gets
    /// there; within a section the page is told the sound's time and follows it gently (a hard correction
    /// whenever it drifted made the picture stutter: the frame the page reported was always a little late).
    /// </summary>
    private void Timer_Tick(object? sender, object e)
    {
      if (!_playing || _audio == null)
      {
        return;
      }
      double seconds = _audio.Position.TotalSeconds;
      if (_audio.HasEnded || seconds >= Length - 0.001)
      {
        _playing = false;
        _audio.Pause();
        Post(new { type = "play", on = false });
        ShowFrame();
        return;
      }
      var (section, frame) = Locate(seconds);
      if (section != _section)
      {
        ShowSection(section, frame, play: true);
      }
      else
      {
        // The sound's time with its fraction; the page follows it gently.
        Post(new { type = "sync", frame = (seconds - _sectionStart[section]) * _fps });
      }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
      if (_playing)
      {
        _playing = false;
        Post(new { type = "play", on = false });
        try
        {
          _audio?.Pause();
        }
        catch (Exception)
        {
          // No sound device.
        }
        ShowFrame();
        return;
      }
      // At the end of the scene: from the start.
      GoTo(Now >= Length - 1 / (double)_fps ? 0 : Now, play: true);
    }

    private void Step(int by) => GoTo(Now + by / (double)_fps, play: false);

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(+1);

    private void PreviousSection_Click(object sender, RoutedEventArgs e)
    {
      // Back to the start of this section, or (already near it) to the one before.
      int target = Now - _sectionStart[Math.Max(0, _section)] > 1 || _section <= 0 ? Math.Max(0, _section) : _section - 1;
      GoTo(_sectionStart[target], _playing);
    }

    private void NextSection_Click(object sender, RoutedEventArgs e)
    {
      if (_section + 1 < _document.Cut.Sections.Count)
      {
        GoTo(_sectionStart[_section + 1], _playing);
      }
    }

    private void TimeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
      if (_settingSlider || !_ready)
      {
        return;
      }
      GoTo(e.NewValue / 1000.0, play: false);
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready || _settingSection || SectionList.SelectedItem is not CutSectionRow row || row.Index == _section)
      {
        return;
      }
      GoTo(_sectionStart[row.Index], _playing);
    }

    /// <summary>A subtitle clicked: to when it is said.</summary>
    private void SubtitleList_ItemClick(object sender, ItemClickEventArgs e)
    {
      if (e.ClickedItem is CutSubtitleRow row)
      {
        GoTo(row.Seconds, play: false);
      }
    }

    #endregion

    #region Controls

    private void ShowFrame()
    {
      double now = Now;
      FrameText.Text = TimeSpan.FromSeconds(now).ToString(@"m\:ss\.f") + " / " + TimeSpan.FromSeconds(Length).ToString(@"m\:ss\.f");
      SectionText.Text = "Section " + (_section + 1) + " of " + _document.Cut.Sections.Count + "   ·   frame " + (_frame + 1) + " / " + _frames;
      _settingSlider = true;
      TimeSlider.Value = Math.Clamp(Math.Round(now * 1000), 0, TimeSlider.Maximum);
      _settingSlider = false;
      PlayGlyph.Glyph = ((char)(_playing ? 0xE769 : 0xE768)).ToString();
      ToolTipService.SetToolTip(PlayButton, _playing ? "Pause (space)" : "Play (space)");

      // The subtitle said now, over the view and selected in the list (none between them).
      int ms = (int)Math.Round(now * 1000);
      var current = _subtitles.LastOrDefault(s => s.Subtitle.Start <= ms && ms < s.Subtitle.Start + s.Subtitle.Length);
      SubtitleText.Text = current?.Text ?? "";
      SubtitleBox.Visibility = s_subtitles && current != null ? Visibility.Visible : Visibility.Collapsed;
      if (!ReferenceEquals(SubtitleList.SelectedItem, current))
      {
        SubtitleList.SelectedItem = current;
        if (current != null)
        {
          SubtitleList.ScrollIntoView(current);
        }
      }
    }

    private void SectionMarks_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSectionMarks();

    /// <summary>A mark over the slider's track where each section after the first begins.</summary>
    private void DrawSectionMarks()
    {
      SectionMarks.Children.Clear();
      // The track runs between the thumb's half widths at either end.
      const double inset = 10;
      double width = SectionMarks.ActualWidth - 2 * inset, height = SectionMarks.ActualHeight;
      if (width <= 0 || Length <= 0)
      {
        return;
      }
      for (int i = 1; i < _document.Cut.Sections.Count; i++)
      {
        var mark = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
          Width = 2,
          Height = 12,
          Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Canvas.SetLeft(mark, inset + width * _sectionStart[i] / Length - 1);
        Canvas.SetTop(mark, (height - 12) / 2);
        ToolTipService.SetToolTip(mark, "Section " + (i + 1));
        SectionMarks.Children.Add(mark);
      }
    }

    private void Camera_Click(object sender, RoutedEventArgs e)
    {
      // Off: the free view goes on from where the cutscene's camera is (the page does that).
      s_camera = CameraButton.IsChecked == true;
      Post(new { type = "useCamera", on = s_camera });
    }

    private void ShowCamera_Click(object sender, RoutedEventArgs e)
    {
      s_showCamera = ShowCameraButton.IsChecked == true;
      Post(new { type = "showCamera", on = s_showCamera });
    }

    private void Lights_Click(object sender, RoutedEventArgs e)
    {
      s_lights = LightsButton.IsChecked == true;
      Post(new { type = "sceneLights", on = s_lights });
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
      if (!_ready)
      {
        return;
      }
      s_volume = e.NewValue;
      ShowVolume();
    }

    /// <summary>The sound at the volume set (0 to 100 %), and the speaker's glyph and tooltip to match.</summary>
    private void ShowVolume()
    {
      if (_audio != null)
      {
        _audio.Volume = (float)(s_volume / 100);
      }
      VolumeGlyph.Glyph = s_volume <= 0 ? "\uE74F" : s_volume < 34 ? "\uE993" : s_volume < 67 ? "\uE994" : "\uE995";
      ToolTipService.SetToolTip(VolumeSlider, "Volume: " + (int)s_volume + " %");
    }

    private void Subtitles_Click(object sender, RoutedEventArgs e)
    {
      s_subtitles = SubtitlesButton.IsChecked == true;
      ShowFrame();
    }

    private void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_ready)
      {
        return;
      }
      s_background = Math.Max(0, BackgroundBox.SelectedIndex);
      PostBackground();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
      // Fitting is the free view's: the cutscene's camera is left.
      if (s_camera)
      {
        s_camera = false;
        CameraButton.IsChecked = false;
        Post(new { type = "useCamera", on = false });
      }
      Post(new { type = "fit" });
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
      _timer.Stop();
      try
      {
        _audio?.Stop();
      }
      catch (Exception)
      {
        // The device is gone already.
      }
      if (Web.CoreWebView2 != null)
      {
        Web.CoreWebView2.WebResourceRequested -= Core_WebResourceRequested;
      }
      try
      {
        Web.Close();
      }
      catch (COMException)
      {
        // Already closing.
      }
    }
  }
}
