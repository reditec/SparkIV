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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Vector3 = System.Numerics.Vector3;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SparkIV.WinUI.Rendering.D3D12;

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
    internal CutSubtitleRow(CutSubtitle subtitle, string text, double seconds, int section)
    {
      Subtitle = subtitle;
      Text = text;
      Seconds = seconds;
      Time = TimeSpan.FromSeconds(Seconds).ToString(@"m\:ss\.f") + "  ·  section " + (section + 1);
    }

    public CutSubtitle Subtitle { get; }
    public string Text { get; }
    /// <summary>When it is said in the whole scene (its part's start plus its own time).</summary>
    public double Seconds { get; }
    public double End => Seconds + Subtitle.Length / 1000.0;
    public string Time { get; }
  }

  /// <summary>
  /// A cutscene played in 3D (the Direct3D 12 renderer): every actor of a section with its clip, through
  /// the scene's camera or freely, the sections one after the other on one time line, with the scene's
  /// sound (which keeps the time: the picture follows it) and the subtitles shown when they are said.
  /// </summary>
  public sealed partial class CutsceneView : UserControl, IDisposable
  {
    // The backgrounds to choose without the location: gradient, grey, black, white.
    private const int Backgrounds = 4;

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
    private bool _ready;
    private bool _settingSlider;
    private bool _settingSection;
    private bool _playing;
    // The section shown, its frames and the frame shown.
    private int _section = -1;
    private int _frame;
    private int _frames = 1;
    private float _fps = 30;
    // Whether the location is loading; what the status says of it.
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
      _only = document.OnlySection is int o && o >= 0 && o < cut.Sections.Count ? o : null;
      SectionList.ItemsSource = cut.Sections.Select((s, i) => new CutSectionRow(i, s, document.Sections[i] != null))
                                            .Where(row => _only == null || row.Index == _only).ToList();
      CastList.ItemsSource = document.Actors.Select(a => new CutActorRow(a)).ToList();
      _sectionStart = new double[cut.Sections.Count + 1];
      for (int i = 0; i < cut.Sections.Count; i++)
      {
        _sectionStart[i + 1] = _sectionStart[i] + cut.Sections[i].Duration;
      }
      // Opened from one section's .wad: that section alone is played (the rest of the scene's time isn't reached).
      _from = _only is int only ? _sectionStart[only] : 0;
      _to = _only is int last ? _sectionStart[last + 1] : _sectionStart[^1];
      _startSection = _only ?? 0;
      // Each part's subtitles from the start of that part's first section (a .cut of several scenes one after the other
      // times each part's [TEXT] from its own start).
      foreach (var subtitle in cut.Subtitles)
      {
        int first = cut.Sections.FindIndex(s => s.Part == subtitle.Part);
        double seconds = (first >= 0 ? _sectionStart[first] : 0) + subtitle.Start / 1000.0;
        // (One section alone: only its subtitles.)
        if (seconds < _from - 0.001 || seconds >= _to)
        {
          continue;
        }
        _subtitles.Add(new CutSubtitleRow(subtitle, document.Texts.TryGetValue(subtitle.Key, out var text) ? text : subtitle.Key, seconds, Locate(seconds).Section));
      }
      SubtitleList.ItemsSource = _subtitles;
      TimeSlider.Maximum = Math.Max(Math.Round(_from * 1000) + 1, Math.Round(_to * 1000));
      TimeSlider.Minimum = Math.Round(_from * 1000);

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

    // Only this section played (opened from its .wad), or null; the time played: all of the scene or that section's.
    private readonly int? _only;
    private readonly double _from, _to;
    private readonly int _startSection;

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

    #region View

    private void CutsceneView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;
      DrawSectionMarks();
      // The cast loads now, in the background, while the renderer starts (the view is already there).
      _castTask = LoadCastAsync();
      StartNative();
    }

    // The Direct3D 12 renderer.
    private D3D12View? _native;

    // The scene it plays: told here the location, section, seek, play and sync; it says which frame it shows and its
    // frame rate.
    private CutscenePlayer? _player;

    /// <summary>The view drawn by the Direct3D 12 renderer: the cutscene played by a <see cref="CutscenePlayer"/>.</summary>
    private async void StartNative()
    {
      try
      {
        _player = new CutscenePlayer(action => DispatcherQueue.TryEnqueue(() => action()))
        {
          FrameChanged = OnFrame,
          FpsMeasured = OnFps,
          FpsBelow = OnFpsBelow,
          Loaded = () => Working.IsActive = _locationLoading || _castLoading,
          // The view was turned or walked: the cutscene's camera is left.
          CameraLeft = () =>
          {
            s_camera = false;
            CameraButton.IsChecked = false;
          },
        };
        _player.ShowLights(s_lights);
        _player.ShowCamera(s_showCamera);
        _player.SetBackground(Math.Clamp(s_background, 0, Backgrounds - 1));
        HookFreeView();
        _native = new D3D12View(Native, _player, text =>
        {
          Message.Text = text;
          Message.Visibility = Visibility.Visible;
        });
        _document.Log?.Step("open the view, start the native renderer (Direct3D 12, " + _native.AdapterName + ")");
      }
      catch (Exception ex)
      {
        D3D12Device.Log("start failed: " + ex);
        _player = null;
        Working.IsActive = false;
        Message.Text = "The Direct3D 12 renderer couldn't start: " + ex.Message;
        Message.Visibility = Visibility.Visible;
        return;
      }
      if (_castTask != null)
      {
        await _castTask;
      }
      if (_disposed)
      {
        return;
      }
      _player.SetCast(_document.Actors);
      // The scene (the location loads), then the section.
      ShowSceneWhenCastLoaded();
    }

    // The view's mouse and keys: the drag going on (its pointer, where it was, its button: 0 left, 1 middle, 2 right),
    // the walking keys held and Shift.
    private uint? _dragPointer;
    private Windows.Foundation.Point _dragAt;
    private int _dragButton;
    private bool _dragMouse;
    private readonly HashSet<Windows.System.VirtualKey> _held = new HashSet<Windows.System.VirtualKey>();
    private bool _shiftDown;

    /// <summary>
    /// The free view's controls: left drag turns, middle or right drag (or Shift + left) moves, the wheel zooms;
    /// W/S/A/D/Q/E walk while the view has the focus (a press in it gives it), Shift faster. A press or a walking key
    /// leaves the cutscene's camera.
    /// </summary>
    private void HookFreeView()
    {
      // The panel can't take the focus; the view itself does (its keys only while it has it).
      IsTabStop = true;
      Native.PointerPressed += Native_PointerPressed;
      Native.PointerMoved += Native_PointerMoved;
      Native.PointerReleased += Native_PointerEnded;
      Native.PointerCanceled += Native_PointerEnded;
      Native.PointerCaptureLost += Native_PointerEnded;
      Native.PointerWheelChanged += Native_PointerWheelChanged;
      KeyDown += View_KeyDown;
      KeyUp += View_KeyUp;
      LostFocus += View_LostFocus;
      _context.Window.Activated += Window_Activated;
    }

    private void Native_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
      if (_player == null)
      {
        return;
      }
      var point = e.GetCurrentPoint(Native);
      var kind = point.Properties.PointerUpdateKind;
      _dragButton = kind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed ? 1 : kind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed ? 2 : 0;
      _dragAt = point.Position;
      _dragPointer = e.Pointer.PointerId;
      _dragMouse = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
      // Looking round by hand leaves the cutscene's camera.
      _player.LeaveCamera();
      // A mouse's moves are read by the render thread right before each frame (CursorDrag); touch and pen by their events.
      if (_dragMouse)
      {
        _player.BeginDrag(_dragButton, XamlRoot?.RasterizationScale ?? 1, (float)Native.ActualHeight);
      }
      Native.CapturePointer(e.Pointer);
      Focus(FocusState.Pointer);
      e.Handled = true;
    }

    private void Native_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
      if (_player == null || _dragPointer != e.Pointer.PointerId)
      {
        return;
      }
      if (_dragMouse)
      {
        // (Taken by the render thread from the cursor.)
        e.Handled = true;
        return;
      }
      var at = e.GetCurrentPoint(Native).Position;
      float dx = (float)(at.X - _dragAt.X), dy = (float)(at.Y - _dragAt.Y);
      _dragAt = at;
      if (_dragButton == 0 && (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == 0)
      {
        _player.Turn(dx, dy);
      }
      else
      {
        _player.Pan(dx, dy, (float)Native.ActualHeight);
      }
      e.Handled = true;
    }

    private void Native_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
      if (_dragPointer == e.Pointer.PointerId)
      {
        _dragPointer = null;
        if (_dragMouse)
        {
          _player?.EndDrag();
        }
        Native.ReleasePointerCapture(e.Pointer);
      }
    }

    private void Native_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
      var properties = e.GetCurrentPoint(Native).Properties;
      if (_player == null || properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
      {
        return;
      }
      // (WinUI's delta is negative turning the wheel towards oneself; summed per frame.)
      _player.Wheel(properties.MouseWheelDelta);
      e.Handled = true;
    }

    private static bool KeyIsDown(Windows.System.VirtualKey key) =>
      (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
      if (_player == null || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this))
      {
        return;
      }
      _shiftDown = KeyIsDown(Windows.System.VirtualKey.Shift);
      bool combined = KeyIsDown(Windows.System.VirtualKey.Control) || KeyIsDown(Windows.System.VirtualKey.Menu) ||
                      KeyIsDown(Windows.System.VirtualKey.LeftWindows) || KeyIsDown(Windows.System.VirtualKey.RightWindows);
      if (combined || OrbitCamera.WalkKey((int)e.Key) == null)
      {
        SendWalk();
        return;
      }
      e.Handled = true;
      if (_held.Add(e.Key))
      {
        // Walking is the free view's: the cutscene's camera is left.
        _player.LeaveCamera();
      }
      SendWalk();
    }

    private void View_KeyUp(object sender, KeyRoutedEventArgs e)
    {
      _shiftDown = KeyIsDown(Windows.System.VirtualKey.Shift);
      _held.Remove(e.Key);
      SendWalk();
    }

    private void View_LostFocus(object sender, RoutedEventArgs e)
    {
      if (ReferenceEquals(e.OriginalSource, this))
      {
        ClearWalk();
      }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
      if (args.WindowActivationState == WindowActivationState.Deactivated)
      {
        ClearWalk();
      }
    }

    private void ClearWalk()
    {
      _held.Clear();
      _shiftDown = false;
      SendWalk();
    }

    private void SendWalk() => _player?.Walk(_held.Select(k => OrbitCamera.WalkKey((int)k)!.Value), _shiftDown);

    /// <summary>
    /// A section's motions and camera for the player, and what was missing (the status's notes), made once on another
    /// thread; the next section's ahead (PrepareNextSection), only the shown one's and the next one's kept.
    /// </summary>
    private sealed record SectionData(List<CutsceneMessages.CastMotion> Motions, CutsceneCamera? Camera, List<string> Notes);
    private readonly Dictionary<int, System.Threading.Tasks.Task<SectionData>> _sectionData = new Dictionary<int, System.Threading.Tasks.Task<SectionData>>();

    private System.Threading.Tasks.Task<SectionData> SectionDataOf(int index)
    {
      if (!_sectionData.TryGetValue(index, out var task))
      {
        var cut = _document.Cut;
        var section = cut.Sections[index];
        var wad = _document.Sections[index];
        var actors = _document.Actors;
        task = System.Threading.Tasks.Task.Run(() =>
        {
          var notes = new List<string>();
          if (wad == null)
          {
            notes.Add((section.Anim ?? "the section's animations") + ".wad isn't beside the file");
          }
          var motions = CutsceneMessages.Motions(section, actors, wad, cut.Sections.Take(index), notes);
          var clip = section.Camera != null ? wad?.Animations.FirstOrDefault(a => string.Equals(a.ShortName, section.Camera, StringComparison.OrdinalIgnoreCase)) : null;
          if (section.Camera != null && clip == null)
          {
            notes.Add("no camera clip " + section.Camera);
          }
          return new SectionData(motions, clip != null ? CutsceneCamera.From(clip) : null, notes);
        });
        _sectionData[index] = task;
      }
      return task;
    }

    /// <summary>The frame shown (the player's).</summary>
    private void OnFrame(int frame, bool playing)
    {
      if (_disposed)
      {
        return;
      }
      _frame = frame;
      // With the sound, it keeps the time and says when the next section begins (the timer); without,
      // the end of a section while playing goes on with the next.
      if (_audio == null && _playing && !playing)
      {
        if (_section + 1 < _document.Cut.Sections.Count && _only == null)
        {
          ShowSection(_section + 1, 0, play: true);
          return;
        }
        _playing = false;
      }
      ShowFrame();
    }

    /// <summary>The frames per second while playing, once a second, with the lowest and highest rate.</summary>
    private void OnFps(int fps, (double Fps, int Frame)? low, (double Fps, int Frame)? high)
    {
      if (_disposed)
      {
        return;
      }
      FpsText.Text = _playing ? $"{fps} fps" : "";
      if (_playing)
      {
        if (low is var (lowFps, lowFrame) && (_fpsMin == null || lowFps < _fpsMin.Value.Fps))
        {
          _fpsMin = (lowFps, _section, lowFrame);
        }
        if (high is var (highFps, highFrame) && (_fpsMax == null || highFps > _fpsMax.Value.Fps))
        {
          _fpsMax = (highFps, _section, highFrame);
        }
      }
    }

    /// <summary>The rate fell below 30 frames a second, or is back, into SparkIV-fps.log.</summary>
    private void OnFpsBelow(bool below, double fps, int frame) =>
      FpsLog((below ? "below 30 fps" : "back at 30 fps or more").PadRight(24) +
             $"{fps.ToString("0.0", CultureInfo.InvariantCulture),6} fps   section {_section + 1}, frame {frame + 1}");

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

    /// <summary>The renderer is ready: the scene shown once the cast is there (at once when it already is).</summary>
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
      ShowSection(_section >= 0 ? _section : _startSection, _frame, play: false);
    }

    /// <summary>The scene's background and its location (loaded now when it is to be shown).</summary>
    private void SendScene()
    {
      if (!_document.Actors.Any(a => a.Scene != null))
      {
        Working.IsActive = false;
      }
      ShowBackground();
      _locationsSent.Clear();
      if (_document.Location != null)
      {
        SendLocation();
      }
      else if (s_location)
      {
        LoadLocation();
      }
    }

    // Each section is placed at its own origin, its [OFFSET] (its actors' and camera's places are relative to it): the
    // intro's sections 2 and 3 lie some 4 m from the first's, 4 to 6 (Roman's cab at the harbour) 1.7 km away; the
    // game's camera in the trace (GTAIV.3, gViewInverse) is on each section's track only so (#83). So a location per
    // origin; changing sections shows the one of the section's origin. Only the shown section's and the next one's are
    // kept: the intro's harbour alone is 16,000 meshes, every location at once took several GB.
    private readonly Dictionary<string, CutsceneLocation> _locations = new Dictionary<string, CutsceneLocation>();
    // The locations handed to the player (on the GPU or going there).
    private readonly HashSet<string> _locationsSent = new HashSet<string>();
    // Origins whose location couldn't be loaded (not tried again).
    private readonly HashSet<string> _locationsFailed = new HashSet<string>();
    private string? _locationKey;

    private float[]? OriginOf(int section) =>
      section >= 0 && section < _document.Cut.Sections.Count ? _document.Cut.Sections[section].Offset ?? _document.Cut.Offset : _document.Cut.Offset;

    private static string KeyOf(float[] origin) => string.Join(",", origin.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// The location of the shown section's origin: shown at once when it was loaded before, else loaded first; then
    /// the other sections' locations loaded in the background (PreloadLocations), so that changing to a section at
    /// another origin (the intro's harbour) shows its map at once instead of loading it then.
    /// </summary>
    private async void LoadLocation()
    {
      string? folder = _document.GameFolder;
      float[]? offset = OriginOf(Math.Max(0, _section));
      // (While the cast loads: after it, with the scene - SendScene - as before.)
      if (_locationLoading || _castLoading || folder == null || offset == null)
      {
        return;
      }
      string key = KeyOf(offset);
      if (_locationsFailed.Contains(key))
      {
        return;
      }
      if (_locations.TryGetValue(key, out var known))
      {
        if (key != _locationKey || _document.Location != known)
        {
          ShowLocation(key, known);
        }
        PreloadLocations();
        return;
      }
      if (!await LoadLocationAt(folder, key, offset, quiet: false) || _disposed)
      {
        return;
      }
      // Shown now (or the one of a section chosen meanwhile), then the others.
      LoadLocation();
    }

    /// <summary>The origins wanted now: the shown section's and the next one's (the one playing goes on to).</summary>
    private List<float[]> WantedOrigins()
    {
      int at = Math.Max(0, _section);
      return new[] { at, at + 1 }.Where(i => i < _document.Cut.Sections.Count).Select(OriginOf).OfType<float[]>().ToList();
    }

    /// <summary>
    /// The next section's location loaded in the background and put on the GPU unseen; the locations no longer wanted
    /// dropped.
    /// </summary>
    private async void PreloadLocations()
    {
      string? folder = _document.GameFolder;
      // (One section alone: no other section's location to load ahead.)
      if (_locationLoading || _castLoading || folder == null || !s_location || _only != null)
      {
        return;
      }
      var wanted = WantedOrigins();
      var wantedKeys = wanted.Select(KeyOf).ToHashSet();
      foreach (string old in _locations.Keys.Where(k => !wantedKeys.Contains(k) && k != _locationKey).ToList())
      {
        DropLocation(old);
      }
      var missing = wanted.FirstOrDefault(o => !_locations.ContainsKey(KeyOf(o)) && !_locationsFailed.Contains(KeyOf(o)));
      if (missing == null)
      {
        return;
      }
      string key = KeyOf(missing);
      if (!await LoadLocationAt(folder, key, missing, quiet: true) || _disposed)
      {
        return;
      }
      // (Onto the GPU ahead, in the background.)
      _player?.PrepareLocation(key, _locations[key]);
      _locationsSent.Add(key);
      // A section there chosen meanwhile: shown now; else on to the next one.
      LoadLocation();
    }

    /// <summary>A location no longer wanted: dropped by the player and here.</summary>
    private void DropLocation(string key)
    {
      _locationsSent.Remove(key);
      _locations.Remove(key);
      _player?.DropLocation(key);
    }

    /// <summary>One location loaded (some seconds, in the background) and kept; false when it couldn't be.</summary>
    private async System.Threading.Tasks.Task<bool> LoadLocationAt(string folder, string key, float[] offset, bool quiet)
    {
      _locationLoading = true;
      if (!quiet)
      {
        Working.IsActive = true;
        _locationNote = "loading the location …";
        ShowStatus();
      }
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
        int hour = (int)OutsideHour;
        // Where the cameras of the sections at this origin are: one outside makes the location one outside.
        var cut = _document.Cut;
        var sectionsHere = Enumerable.Range(0, cut.Sections.Count).Where(i => OriginOf(i) is float[] o && KeyOf(o) == key).ToList();
        var loaded = await System.Threading.Tasks.Task.Run(() =>
        {
          var cameras = sectionsHere.SelectMany(i => CutsceneMessages.CameraPlaces(cut.Sections[i], _document.Sections[i])).ToList();
          return CutsceneLocation.Load(folder, offset, removals, LocationRadius, drawDistance, hour, log, cameras);
        });
        _locations[key] = loaded;
        return true;
      }
      catch (Exception ex)
      {
        _locationsFailed.Add(key);
        if (!quiet)
        {
          _locationNote = "no location: " + ex.Message;
          ShowStatus();
          Working.IsActive = false;
        }
        return false;
      }
      finally
      {
        _locationLoading = false;
      }
    }
    /// <summary>A loaded location made the shown one: its note, then handed to the player and shown.</summary>
    private void ShowLocation(string key, CutsceneLocation location)
    {
      _document.Location = location;
      _locationKey = key;
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

    // A scene outside: the weather and the hour it starts at (CutsceneLocation.Load shows timed objects at it too),
    // as the mission script playing it forces them (bernie3: FORCE_WEATHER_NOW(1) before bc_3), else noon, sunny
    // (the .cut gives neither); the viewer's user can set another start.
    private string OutsideWeather =>
      _document.GameFolder is string folder && _document.Script is { Weather: >= 0 } script ? TimeCycle.WeatherName(folder, script.Weather) ?? "SUNNY" : "SUNNY";
    private float OutsideHour => _document.StartHour ?? _document.Script?.Hour ?? 12;
    // The game's clock runs on through a cutscene: a game hour took 123.4 s of the cutscene's time in the traces
    // (gDayNightEffects and the sun's direction per frame: GTAIV.1.trace, bc_3 s1; GTAIV.2.trace, et01_ba).
    private const double SecondsPerGameHour = 123.4;
    private float HourNow => (float)((OutsideHour + (_document.ClockLocked ? 0 : Now / SecondsPerGameHour)) % 24);
    // The game minute whose light the player has (it follows the clock, by the minute).
    private int _clockMinute = -1;
    // Whether the location's light from the time cycle was set (the location is then lit and shown as the game).
    private bool _lit;
    // How far the location's far models were loaded (the [DRAW_DISTANCE] as the game limits it by the time cycle).
    private float _drawDistance;

    private void SendLocation()
    {
      if (_document.Location == null || _locationKey == null)
      {
        return;
      }
      _player?.SetLocation(s_location ? _locationKey : null, _document.Location, _drawDistance);
      _locationsSent.Add(_locationKey);
      _lit = SendLighting();
      // The water as the mission script leaves it: the wind it forces, and the sea's swell kept round where the
      // player stands, the mission's marker (CutsceneScriptIndex.Entry), in the scene's metres (less the origin).
      var script = _document.Script;
      float[]? origin = OriginOf(Math.Max(0, _section));
      Vector3? swellAt = script?.Marker is float[] marker && origin != null ? new Vector3(marker[0] - origin[0], marker[1] - origin[1], marker[2] - origin[2]) : null;
      _player?.SetWater(script?.Wind, swellAt);
      ShowBackground();
    }

    /// <summary>
    /// The light of the game's time cycle at the clock (OutsideWeather, HourNow) for the player, which lights by the
    /// modifier boxes and the room its camera is in, each frame, as the game does.
    /// </summary>
    private bool SendLighting()
    {
      float hour = HourNow;
      if (_document.Location is not CutsceneLocation location || _document.GameFolder is not string folder ||
          OriginOf(Math.Max(0, _section)) is not float[] lightOrigin)
      {
        return false;
      }
      var lighting = CutsceneLighting.Load(folder, OutsideWeather, hour, location, new Vector3(lightOrigin[0], lightOrigin[1], lightOrigin[2]));
      if (lighting == null)
      {
        return false;
      }
      _player?.SetLighting(lighting);
      // (The clouds held with the clock.)
      _player?.HoldClouds(_document.ClockLocked);
      _clockMinute = (int)(hour * 60);
      return true;
    }

    /// <summary>
    /// The view's background: with the location shown the scene's own (outside the time cycle's sky, inside
    /// black, as the game's world beyond the windows would be dark against the room), the choice greyed;
    /// without it the one chosen.
    /// </summary>
    private void ShowBackground()
    {
      BackgroundBox.IsEnabled = !(s_location && _document.Location != null);
      _player?.SetBackground(Math.Clamp(s_background, 0, Backgrounds - 1));
    }

    /// <summary>The next section's motions made ahead; the others no longer kept.</summary>
    private void PrepareNextSection()
    {
      int next = _section + 1;
      foreach (int old in _sectionData.Keys.Where(k => k != _section && k != next).ToList())
      {
        _sectionData.Remove(old);
      }
      if (next < _document.Cut.Sections.Count && !_castLoading)
      {
        _ = SectionDataOf(next);
      }
    }

    /// <summary>A section shown: every actor's clip, the camera, the lights, fades, draw distance; at a frame, playing or not.</summary>
    private void ShowSection(int index, int frame, bool play)
    {
      var cut = _document.Cut;
      if (index < 0 || index >= cut.Sections.Count)
      {
        return;
      }
      var section = cut.Sections[index];
      var wad = _document.Sections[index];
      // (Made ahead on another thread as a rule; else now.)
      var data = SectionDataOf(index).GetAwaiter().GetResult();
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

      // A section at another origin (#83): the location there, at once when it is on the GPU already (loaded ahead);
      // else the one shown so far left out until it is there.
      if (s_location && _locationKey != null && OriginOf(index) is float[] here && KeyOf(here) is string hereKey && hereKey != _locationKey)
      {
        if (_locations.TryGetValue(hereKey, out var ready) && _locationsSent.Contains(hereKey))
        {
          ShowLocation(hereKey, ready);
          PreloadLocations();
        }
        else
        {
          LoadLocation();
        }
      }

      if (_player != null)
      {
        _player.SetSection(data.Motions, data.Camera, _fps, _frames, _frame, play, s_camera && data.Camera != null,
                           CutsceneLights.TracksOf(_document.Cut.Sections[index], _document.Sections[index]),
                           CutsceneMessages.FadeOf(_document.Cut, index, _frames, _fps),
                           _document.Cut.DrawDistanceSteps(index, _fps));
      }
      ShowFrame();
      PrepareNextSection();

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
      if (data.Camera == null)
      {
        parts.Add("no camera clip");
      }
      parts.AddRange(data.Notes);
      _sectionStatus = string.Join("   ·   ", parts);
      ShowStatus();
    }

    private string _sectionStatus = "";

    private void ShowStatus() => Status.Text = _locationNote == null ? _sectionStatus : _sectionStatus + "   ·   " + _locationNote;

    private void Location_Click(object sender, RoutedEventArgs e)
    {
      s_location = LocationButton.IsChecked == true;
      _player?.SetLocation(s_location ? _locationKey : null, _document.Location, _drawDistance);
      if (s_location)
      {
        LoadLocation();
      }
      ShowBackground();
    }

    #endregion

    #region Playing

    // Playing started with the sound: since when the picture waits for it, and the time it was sent to (Timer_Tick).
    private System.Diagnostics.Stopwatch? _audioWait;
    private double _audioFrom;

    /// <summary>To a time of the whole scene: the section it is in, the sound there; playing on or not.</summary>
    private void GoTo(double seconds, bool play)
    {
      // Not while the cast still loads (its actors are being filled in the background); the view shows the
      // section it was at once they are there.
      if (_castLoading)
      {
        return;
      }
      seconds = Math.Clamp(seconds, _from, _only != null ? Math.Max(_from, _to - 0.0005) : _to);
      var (section, frame) = Locate(seconds);
      _playing = play;
      if (section != _section)
      {
        ShowSection(section, frame, play);
      }
      else
      {
        _frame = Math.Clamp(frame, 0, _frames - 1);
        // (The time with its fraction: a resumed scene goes on where it stood.)
        _player?.Seek(Math.Clamp((seconds - _sectionStart[section]) * _fps, 0, _frames - 1));
        // (Its seek doesn't stop it: told the play state either way - a player still playing after the view stopped ran
        // to the section's end and stayed there.)
        _player?.Play(play);
        ShowFrame();
      }
      AudioTo(seconds, play);
      // With the sound: the picture waits for it to run (Timer_Tick).
      _audioWait = null;
      if (play && _audio != null && _player != null)
      {
        _player.Play(false);
        _audioFrom = seconds;
        _audioWait = System.Diagnostics.Stopwatch.StartNew();
      }
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
    /// there; within a section the player is told the sound's time and follows it gently (a hard correction
    /// whenever it drifted made the picture stutter: the frame reported was always a little late).
    /// </summary>
    private void Timer_Tick(object? sender, object e)
    {
      if (!_playing || _audio == null)
      {
        return;
      }
      double seconds = _audio.Position.TotalSeconds;
      // Started (or sought) with the sound: the picture held until the sound really runs (#64: it starts some 100 to
      // 200 ms after Play, and the picture running ahead was pulled back by the sync several times - the frames before
      // played again, then on), at most half a second (no sound device: on without).
      if (_audioWait != null)
      {
        if (seconds <= _audioFrom + 0.002 && _audioWait.Elapsed.TotalSeconds < 0.5)
        {
          return;
        }
        _audioWait = null;
        _player?.Play(true);
      }
      if (_audio.HasEnded || seconds >= _to - 0.001)
      {
        _playing = false;
        _audio.Pause();
        _player?.Play(false);
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
        // The sound's time with its fraction; the player follows it gently.
        _player?.Sync((seconds - _sectionStart[section]) * _fps);
      }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
      if (_playing)
      {
        _playing = false;
        _audioWait = null;
        _player?.Play(false);
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
      // At the end of the scene: from the start; else on from the time shown, with its fraction (the whole frame went
      // back a little first, #64).
      double at = Now >= _to - 1 / (double)_fps ? _from
                : _player != null && _section >= 0 ? _sectionStart[_section] + _player.SectionSeconds : Now;
      GoTo(at, play: true);
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
      // Scrubbing while playing plays on from there (and on into the next section); stopped, it stays stopped.
      GoTo(e.NewValue / 1000.0, play: _playing);
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

    /// <summary>The clock at the time in the scene, forwards and backwards as the time goes, and the sky with it.</summary>
    private void ShowClock()
    {
      int minute = (int)(HourNow * 60);
      ClockText.Text = (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00");
      if (_lit && minute != _clockMinute)
      {
        SendLighting();
      }
    }

    private void StartFlyout_Opening(object sender, object e)
    {
      _settingStart = true;
      StartPicker.SelectedTime = TimeSpan.FromHours(OutsideHour);
      LockTimeToggle.IsChecked = _document.ClockLocked;
      _settingStart = false;
    }

    /// <summary>The clock (and the clouds) held at the start hour, or running on with the cutscene's time again.</summary>
    private void LockTime_Click(object sender, RoutedEventArgs e)
    {
      if (_settingStart)
      {
        return;
      }
      _document.LockTime = LockTimeToggle.IsChecked == true;
      // (The clouds held with the clock.)
      _player?.HoldClouds(_document.ClockLocked);
      ShowClock();
    }

    private void StartPicker_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs e)
    {
      if (!_settingStart && e.NewTime is TimeSpan time)
      {
        _document.StartHour = (float)time.TotalHours;
        ShowClock();
      }
    }

    private void StartReset_Click(object sender, RoutedEventArgs e)
    {
      _document.StartHour = null;
      StartFlyout_Opening(sender, e);
      ShowClock();
    }

    private bool _settingStart;

    // The frame rate while playing, for SparkIV-fps.log beside SparkIV.exe: when it falls below 30 and when it is back,
    // and on stopping the lowest and highest rate with their section and frame (from the player).
    private (double Fps, int Section, int Frame)? _fpsMin, _fpsMax;
    private bool _fpsHeader;

    private void FpsLog(string line)
    {
      try
      {
        var text = new StringBuilder();
        if (!_fpsHeader)
        {
          _fpsHeader = true;
          text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + _document.Name + "  playing");
        }
        text.AppendLine("  " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SparkIV-fps.log"), text.ToString());
      }
      catch (Exception)
      {
        // A folder not to be written in: no log.
      }
    }

    private void FpsSummary()
    {
      if (_fpsMin is { } low && _fpsMax is { } high)
      {
        FpsLog("lowest  " + low.Fps.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6) + $" fps   section {low.Section + 1}, frame {low.Frame + 1}");
        FpsLog("highest " + high.Fps.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6) + $" fps   section {high.Section + 1}, frame {high.Frame + 1}");
      }
      if (_fpsHeader)
      {
        try
        {
          File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SparkIV-fps.log"), Environment.NewLine);
        }
        catch (Exception)
        {
        }
      }
      _fpsMin = _fpsMax = null;
      _fpsHeader = false;
    }

    private void ShowFrame()
    {
      double now = Now;
      if (!_playing)
      {
        FpsText.Text = "";
        FpsSummary();
      }
      FrameText.Text = TimeSpan.FromSeconds(now - _from).ToString(@"m\:ss\.f") + " / " + TimeSpan.FromSeconds(_to - _from).ToString(@"m\:ss\.f");
      // (One section alone: only its frame.)
      SectionText.Text = (_only != null ? "" : "Section " + (_section + 1) + " of " + _document.Cut.Sections.Count + "   ·   ") + "Frame " + (_frame + 1) + " / " + _frames;
      _settingSlider = true;
      TimeSlider.Value = Math.Clamp(Math.Round(now * 1000), TimeSlider.Minimum, TimeSlider.Maximum);
      _settingSlider = false;
      PlayGlyph.Glyph = ((char)(_playing ? 0xE769 : 0xE768)).ToString();
      ToolTipService.SetToolTip(PlayButton, _playing ? "Pause (space)" : "Play (space)");
      ShowClock();

      // The subtitle said now, over the view and selected in the list (none between them).
      var current = _subtitles.LastOrDefault(s => s.Seconds <= now && now < s.End);
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
      // (One section alone: no marks.)
      if (width <= 0 || Length <= 0 || _only != null)
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
      // Off: the free view goes on from where the cutscene's camera is (the player does that).
      s_camera = CameraButton.IsChecked == true;
      _player?.UseCamera(s_camera);
    }

    private void ShowCamera_Click(object sender, RoutedEventArgs e)
    {
      s_showCamera = ShowCameraButton.IsChecked == true;
      _player?.ShowCamera(s_showCamera);
    }

    private void Lights_Click(object sender, RoutedEventArgs e)
    {
      s_lights = LightsButton.IsChecked == true;
      _player?.ShowLights(s_lights);
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
      ShowBackground();
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
      _context.Window.Activated -= Window_Activated;
      _native?.Dispose();
      _native = null;
    }
  }
}
