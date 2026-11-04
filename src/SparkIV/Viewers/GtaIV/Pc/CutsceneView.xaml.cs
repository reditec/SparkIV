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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Vector3 = System.Numerics.Vector3;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
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
      // Each part's subtitles from the start of that part's first section (a .cut of several scenes one after the other
      // times each part's [TEXT] from its own start).
      foreach (var subtitle in cut.Subtitles)
      {
        int first = cut.Sections.FindIndex(s => s.Part == subtitle.Part);
        double seconds = (first >= 0 ? _sectionStart[first] : 0) + subtitle.Start / 1000.0;
        _subtitles.Add(new CutSubtitleRow(subtitle, document.Texts.TryGetValue(subtitle.Key, out var text) ? text : subtitle.Key, seconds, Locate(seconds).Section));
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

      if (D3D12View.Enabled)
      {
        StartNative();
        return;
      }

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

    // The native renderer (#112), with SPARKIV_D3D12=1 instead of the page.
    private D3D12View? _native;

    // The scene the native renderer plays (#112 4b): told here what the page is told (location, section, seek, play,
    // sync), it says which frame it shows and its frame rate as the page does.
    private CutscenePlayer? _player;

    /// <summary>
    /// SPARKIV_D3D12=1: the view drawn by the native Direct3D 12 renderer instead of the WebView2 page (#112 4b): the
    /// cutscene played by a <see cref="CutscenePlayer"/>, everything else here as with the page (the page's messages go
    /// nowhere without one; at each place one goes out, the player is told the same).
    /// </summary>
    private async void StartNative()
    {
      Web.Visibility = Visibility.Collapsed;
      Native.Visibility = Visibility.Visible;
      try
      {
        _player = new CutscenePlayer(DispatcherQueue)
        {
          FrameChanged = OnFrame,
          FpsMeasured = OnFps,
          FpsBelow = OnFpsBelow,
          Loaded = () => Working.IsActive = _locationLoading || _castLoading,
          // The view was turned or walked: the cutscene's camera is left (as the page's "filmCamera" message).
          CameraLeft = () =>
          {
            s_camera = false;
            CameraButton.IsChecked = false;
          },
        };
        _player.ShowLights(s_lights);
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
      // As when the page is ready: the scene (the location loads), then the section.
      ShowSceneWhenCastLoaded();
    }

    // The native view's mouse and keys (the page's, ModelView.html "Mouse" and "Keys"): the drag going on (its pointer,
    // where it was, its button: 0 left, 1 middle, 2 right), the walking keys held and Shift.
    private uint? _dragPointer;
    private Windows.Foundation.Point _dragAt;
    private int _dragButton;
    private readonly HashSet<Windows.System.VirtualKey> _held = new HashSet<Windows.System.VirtualKey>();
    private bool _shiftDown;

    /// <summary>
    /// The free view's controls on the native view, as the page's: left drag turns, middle or right drag (or Shift + left)
    /// moves, the wheel zooms, a double-click fits; W/S/A/D/Q/E walk while the view has the focus (a press in it gives it),
    /// Shift faster. A press or a walking key leaves the cutscene's camera.
    /// </summary>
    private void HookFreeView()
    {
      // The panel can't take the focus; the view itself does (its keys only while it has it, as the page's while focused).
      IsTabStop = true;
      Native.PointerPressed += Native_PointerPressed;
      Native.PointerMoved += Native_PointerMoved;
      Native.PointerReleased += Native_PointerEnded;
      Native.PointerCanceled += Native_PointerEnded;
      Native.PointerCaptureLost += Native_PointerEnded;
      Native.PointerWheelChanged += Native_PointerWheelChanged;
      Native.DoubleTapped += Native_DoubleTapped;
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
      // Looking round by hand leaves the cutscene's camera.
      _player.LeaveCamera();
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
      // (The page's deltaY is positive turning the wheel towards oneself, WinUI's delta then negative.)
      _player.Zoom(-Math.Sign(properties.MouseWheelDelta));
      e.Handled = true;
    }

    private void Native_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
      _player?.Fit();
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
      if (combined || OrbitCamera.WalkKey(e.Key) == null)
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

    private void SendWalk() => _player?.Walk(_held.Select(k => OrbitCamera.WalkKey(k)!.Value), _shiftDown);

    /// <summary>The motions and camera of a section for the player, made once on another thread (as SectionDataOf).</summary>
    private readonly Dictionary<int, System.Threading.Tasks.Task<(List<CutsceneMessages.CastMotion> Motions, CutsceneCamera? Camera)>> _nativeSections =
      new Dictionary<int, System.Threading.Tasks.Task<(List<CutsceneMessages.CastMotion>, CutsceneCamera?)>>();

    private System.Threading.Tasks.Task<(List<CutsceneMessages.CastMotion> Motions, CutsceneCamera? Camera)> NativeSectionOf(int index)
    {
      if (!_nativeSections.TryGetValue(index, out var task))
      {
        var cut = _document.Cut;
        var section = cut.Sections[index];
        var wad = _document.Sections[index];
        var actors = _document.Actors;
        task = System.Threading.Tasks.Task.Run(() =>
        {
          var motions = CutsceneMessages.Motions(section, actors, wad, cut.Sections.Take(index), null);
          var clip = section.Camera != null ? wad?.Animations.FirstOrDefault(a => string.Equals(a.ShortName, section.Camera, StringComparison.OrdinalIgnoreCase)) : null;
          return (motions, clip != null ? CutsceneCamera.From(clip) : null);
        });
        _nativeSections[index] = task;
      }
      return task;
    }

    /// <summary>The frame shown (the page's "frame" message, or the player's).</summary>
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
        if (_section + 1 < _document.Cut.Sections.Count)
        {
          ShowSection(_section + 1, 0, play: true);
          return;
        }
        _playing = false;
      }
      ShowFrame();
    }

    /// <summary>The frames per second while playing, once a second, with the lowest and highest rate (the page's "fps").</summary>
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

    /// <summary>The rate fell below 30 frames a second, or is back (the page's "fpsBelow"), into SparkIV-fps.log.</summary>
    private void OnFpsBelow(bool below, double fps, int frame) =>
      FpsLog((below ? "below 30 fps" : "back at 30 fps or more").PadRight(24) +
             $"{fps.ToString("0.0", CultureInfo.InvariantCulture),6} fps   section {_section + 1}, frame {frame + 1}");

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
      else if (name.StartsWith("loc", StringComparison.Ordinal) && name.IndexOf('/') is int slash and > 3 &&
               int.TryParse(name.AsSpan(3, slash - 3), out int band) && band < _locationKeys.Count &&
               _locations.TryGetValue(_locationKeys[band], out var location))
      {
        body = location.Texture(name.Substring(slash + 1));
      }
      else
      {
        body = CutsceneMessages.Texture(_document.Actors, name);
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
        case "fps":
          // The page's frames per second while playing (once a second); cleared when playing stops. With it the lowest
          // and highest rate since the page began playing (its section), kept here across the sections.
          static (double, int)? Rate(JsonElement root, string name) =>
            root.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.Object ? (r.GetProperty("fps").GetDouble(), r.GetProperty("frame").GetInt32()) : null;
          OnFps(root.TryGetProperty("fps", out var fps) ? fps.GetInt32() : 0, Rate(root, "min"), Rate(root, "max"));
          break;
        case "fpsBelow":
          // The rate fell below 30 frames a second, or is back at 30 or more (over the last second).
          OnFpsBelow(root.GetProperty("below").GetBoolean(), root.GetProperty("fps").GetDouble(), root.GetProperty("frame").GetInt32());
          break;
        case "frame":
          OnFrame(root.GetProperty("frame").GetInt32(), root.GetProperty("playing").GetBoolean());
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

    private void Post(object message) => PostJson(JsonSerializer.Serialize(message));

    // While a section is being changed to: the messages gathered into its one batch (the page draws no picture between
    // them, else one came with the new location and the old camera, or without any location).
    private List<string>? _bundle;

    private void PostJson(string json)
    {
      if (_bundle != null)
      {
        _bundle.Add(json);
      }
      else if (_pageReady && !_disposed)
      {
        Web.CoreWebView2?.PostWebMessageAsJson(json);
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
      _locationsSent.Clear();
      _sectionsPrepared.Clear();
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
    // origin, with its own band of mesh and material ids and its own texture urls (loc<band>/<key>); changing sections
    // shows the one of the section's origin. Only the shown section's and the next one's are kept (here and on the
    // page): the intro's harbour alone is 16,000 meshes, every location at once took several GB.
    private readonly Dictionary<string, CutsceneLocation> _locations = new Dictionary<string, CutsceneLocation>();
    private readonly List<string> _locationKeys = new List<string>();
    private readonly Dictionary<string, List<int>> _locationVisibleOf = new Dictionary<string, List<int>>();
    private readonly HashSet<string> _locationsSent = new HashSet<string>();
    // Origins whose location couldn't be loaded (not tried again).
    private readonly HashSet<string> _locationsFailed = new HashSet<string>();
    private string? _locationKey;
    private const int LocationIdBand = 500000;

    private float[]? OriginOf(int section) =>
      section >= 0 && section < _document.Cut.Sections.Count ? _document.Cut.Sections[section].Offset ?? _document.Cut.Offset : _document.Cut.Offset;

    private static string KeyOf(float[] origin) => string.Join(",", origin.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    private string LocationUrl(string key) => LocationUrlOf(_locationKey!, key);

    private string LocationUrlOf(string location, string key) => Host + "loc" + _locationKeys.IndexOf(location) + "/" + key;

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
    /// The next section's location loaded in the background and sent to the page unseen (paced, so that the playing
    /// scene goes on); the locations no longer wanted dropped here and on the page.
    /// </summary>
    private async void PreloadLocations()
    {
      string? folder = _document.GameFolder;
      if (_locationLoading || _castLoading || folder == null || !s_location)
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
      // (The player: onto the GPU ahead, in the background.)
      _player?.PrepareLocation(key, _locations[key]);
      await SendLocationScene(key, paced: true);
      // A section there chosen meanwhile: shown now; else on to the next one.
      LoadLocation();
    }

    /// <summary>A location no longer wanted: its meshes, materials and textures dropped on the page, its data here.</summary>
    private void DropLocation(string key)
    {
      if (_locationsSent.Remove(key))
      {
        int first = 100000 + _locationKeys.IndexOf(key) * LocationIdBand;
        Post(new { type = "drop", from = first, to = first + LocationIdBand });
      }
      _locations.Remove(key);
      _locationVisibleOf.Remove(key);
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
        if (!_locationKeys.Contains(key))
        {
          _locationKeys.Add(key);
        }
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
    /// <summary>A loaded location made the shown one: its note, then sent (its scene once) and shown.</summary>
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

    // The load log: how long the page has been loading textures, and what for.
    private System.Diagnostics.Stopwatch? _pageWait;
    private string? _pageWaitFor;

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
    // The game minute whose sky the page has (it follows the clock, by the minute).
    private int _clockMinute = -1;
    private Dictionary<uint, float[]>? _skyModifiers;
    // The modifier boxes the page's sky was made under, and each section's camera places.
    private string _skyBoxes = "";
    private readonly Dictionary<int, List<Vector3>> _cameraPlaces = new Dictionary<int, List<Vector3>>();
    // Whether the location's light from the time cycle was sent (the page then lights and shows it as the game).
    private bool _lit;
    // How far the location's far models were loaded (the [DRAW_DISTANCE] as the game limits it by the time cycle).
    private float _drawDistance;

    /// <summary>
    /// A location's scene, once per page (not shown by this: the visible list says what is): after the cast's ids
    /// (meshes and materials), well clear of them, each location in its own band. Its meshes in messages of 1000 (the
    /// page adds them): one message is one string in the page, at most ~536 million characters (V8), and the intro's
    /// harbour comes to about that as one.
    /// </summary>
    private async System.Threading.Tasks.Task SendLocationScene(string key, bool paced = false)
    {
      if (!_locations.TryGetValue(key, out var location) || !_locationsSent.Add(key))
      {
        return;
      }
      if (_player != null)
      {
        // The native renderer takes the location as it is (CutscenePlayer): no page messages made (the harbour's took
        // seconds and gigabytes); marked as there for the section changes.
        _locationVisibleOf[key] = new List<int>();
        return;
      }
      int first = 100000 + _locationKeys.IndexOf(key) * LocationIdBand;
      // (Filled on the other thread while the parts are made: kept here only once they all are.)
      var visible = new List<int>();
      if (_document.Log != null)
      {
        _document.Log.Step("location: made into the page's scene");
        _pageWait = System.Diagnostics.Stopwatch.StartNew();
        _pageWaitFor = "page: the location's textures uploaded";
      }
      // Each part made (on another thread) and posted in turn; in the background with pauses between them, so the
      // page draws on while it takes them in.
      using var parts = location.MessageParts(k => LocationUrlOf(key, k), first, first, visible).GetEnumerator();
      while (await System.Threading.Tasks.Task.Run(parts.MoveNext))
      {
        if (_disposed || !_locationsSent.Contains(key))
        {
          return;
        }
        PostJson(parts.Current);
        if (paced)
        {
          await System.Threading.Tasks.Task.Delay(40);
        }
      }
      _locationVisibleOf[key] = visible;
      if (key == _locationKey)
      {
        _locationVisible = visible;
        PostVisible();
      }
    }

    private void SendLocation()
    {
      if (_document.Location == null || _locationKey == null)
      {
        return;
      }
      _player?.SetLocation(s_location ? _locationKey : null, _document.Location, _drawDistance);
      _ = SendLocationScene(_locationKey);
      Post(_document.Location.LightsMessage(_document.Location.LightVolume != null ? LocationUrl(CutsceneLocation.LightVolumeKey) : null,
                                            _document.Location.Corona != null ? LocationUrl(CutsceneLocation.CoronaKey) : null));
      _lit = PostSky();
      // The water as the mission script leaves it: the wind it forces, and the sea's swell kept round where the
      // player stands, the mission's marker (CutsceneScriptIndex.Entry), in the page's metres (less the origin).
      var script = _document.Script;
      float[]? origin = OriginOf(Math.Max(0, _section));
      float[]? swellAt = script?.Marker is float[] marker && origin != null ? new[] { marker[0] - origin[0], marker[1] - origin[1], marker[2] - origin[2] } : null;
      Post(new
      {
        type = "water",
        wind = script?.Wind,
        swellAt,
      });
      // (The same to the native renderer.)
      _player?.SetWater(script?.Wind, swellAt != null ? new Vector3(swellAt[0], swellAt[1], swellAt[2]) : null);
      _locationVisible = _locationVisibleOf.TryGetValue(_locationKey, out var shown) ? shown : new List<int>();
      PostVisible();
      PostBackground();
    }

    /// <summary>
    /// The light of the game's time cycle at the clock (OutsideWeather, HourNow), and for each room of the location
    /// as its time cycle modifier changes it: the page lights by the one of the room the camera is in, as the game
    /// does, inside the same way as outside.
    /// </summary>
    private bool PostSky()
    {
      float hour = HourNow;
      if (_document.Location is not CutsceneLocation location || _document.GameFolder is not string folder ||
          TimeCycle.Load(folder, OutsideWeather, hour) is not TimeCycle cycle)
      {
        return false;
      }
      _skyModifiers ??= TimeCycle.LoadModifiers(folder);
      if (_player != null && OriginOf(Math.Max(0, _section)) is float[] lightOrigin)
      {
        // The player's light of the same time cycle (the boxes and the room by its own camera, each frame).
        _player.SetLighting(CutsceneLighting.Load(folder, OutsideWeather, hour, location, new Vector3(lightOrigin[0], lightOrigin[1], lightOrigin[2])));
      }
      // As the game, under the map's modifier boxes where the camera is (the intro's harbour at 05:15, GTAIV.3.trace:
      // gDirectionalColour 1/7 in colour and strength, KsS1nomoon's boxes).
      if (CameraNow() is Vector3 camera)
      {
        cycle = cycle.WithBoxes(location.ModifierBoxes, _skyModifiers, camera, hour);
        _skyBoxes = BoxesAt(location, camera, hour);
      }
      var rooms = location.Rooms.Select(r =>
        _skyModifiers != null && _skyModifiers.TryGetValue(r.Modifier, out var modifier) ? cycle.With(modifier).Message(hour, parts: cycle.Blended(modifier, 1)) : null).ToArray();
      Post(cycle.Message(hour, location.Clouds != null ? LocationUrl(CutsceneLocation.CloudKey) : null,
                         location.CloudDetail != null ? LocationUrl(CutsceneLocation.CloudDetailKey) : null, rooms));
      Post(new { type = "lockTime", on = _document.ClockLocked });
      _clockMinute = (int)(hour * 60);
      return true;
    }

    /// <summary>Where the section's camera is in the world now, by the frame (see CutsceneMessages.CameraPlaces).</summary>
    private Vector3? CameraNow()
    {
      if (_section < 0 || OriginOf(_section) is not float[] origin)
      {
        return null;
      }
      if (!_cameraPlaces.TryGetValue(_section, out var places))
      {
        _cameraPlaces[_section] = places = CutsceneMessages.CameraPlaces(_document.Cut.Sections[_section], _document.Sections[_section], 1);
      }
      return places.Count == 0 ? null : places[Math.Clamp(_frame, 0, places.Count - 1)] + new Vector3(origin[0], origin[1], origin[2]);
    }

    /// <summary>The boxes' strengths at a place, in steps: when they change the sky is sent anew.</summary>
    private static string BoxesAt(CutsceneLocation location, Vector3 camera, float hour) =>
      string.Join(",", TimeCycle.StrengthsAt(location.ModifierBoxes, camera, hour).OrderBy(b => b.Key).Select(b => b.Key + ":" + (int)(b.Value * 50)));

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

    // A section's messages (timeline, the cast with every actor's clip, camera, draw distance, fades) as JSON, made once on
    // another thread: the intro's are 20 to 39 MB each and took 140 to 190 ms to make here, the page about as long to
    // take in. The next section's are sent ahead ("prepare"), so changing to it takes only a short message. Only the
    // shown section's and the next one's are kept, here and on the page.
    private sealed record SectionData(string Json, bool HasCamera, List<string> Notes);
    private readonly Dictionary<int, System.Threading.Tasks.Task<SectionData>> _sectionData = new Dictionary<int, System.Threading.Tasks.Task<SectionData>>();
    private readonly HashSet<int> _sectionsPrepared = new HashSet<int>();

    private System.Threading.Tasks.Task<SectionData> SectionDataOf(int index)
    {
      if (!_sectionData.TryGetValue(index, out var task))
      {
        var cut = _document.Cut;
        var section = cut.Sections[index];
        var wad = _document.Sections[index];
        var actors = _document.Actors;
        float fps = FpsOf(index);
        var clip = wad?.Animations.FirstOrDefault();
        int frames = clip != null ? Math.Max(1, clip.FrameCount) : Math.Max(1, (int)(section.Duration * fps) + 1);
        task = System.Threading.Tasks.Task.Run(() =>
        {
          var (timeline, cast, camera, notes) = CutsceneMessages.Section(section, actors, wad, cut.Sections.Take(index));
          var messages = new object[]
          {
            timeline,
            cast,
            camera ?? new { type = "camera", camera = (object?)null },
            // How far the game draws, shot by shot (the map beyond isn't drawn).
            new { type = "drawDistance", steps = cut.DrawDistanceSteps(index, fps) },
            // The fade in at the start of the first section, out at the end of the last (in the section's frames).
            CutsceneMessages.Fade(cut, index, frames, fps),
          };
          return new SectionData(JsonSerializer.Serialize(messages), camera != null, notes);
        });
        _sectionData[index] = task;
      }
      return task;
    }

    /// <summary>The next section's messages made and sent to the page ahead; the others no longer kept.</summary>
    private async void PrepareNextSection()
    {
      int next = _section + 1;
      foreach (int old in _sectionData.Keys.Where(k => k != _section && k != next).ToList())
      {
        _sectionData.Remove(old);
      }
      if (_player != null)
      {
        // The player's: the next section's motions made ahead too, the others no longer kept.
        foreach (int old in _nativeSections.Keys.Where(k => k != _section && k != next).ToList())
        {
          _nativeSections.Remove(old);
        }
        if (next < _document.Cut.Sections.Count && !_castLoading)
        {
          _ = NativeSectionOf(next);
        }
      }
      if (_sectionsPrepared.RemoveWhere(k => k != _section && k != next) > 0)
      {
        Post(new { type = "unprepare", keep = new[] { _section, next } });
      }
      if (next >= _document.Cut.Sections.Count || _sectionsPrepared.Contains(next) || _castLoading)
      {
        return;
      }
      var data = await SectionDataOf(next);
      if (_disposed || _section + 1 != next || !_pageReady || !_sectionsPrepared.Add(next))
      {
        return;
      }
      PostJson("{\"type\":\"prepare\",\"section\":" + next + ",\"messages\":" + data.Json + "}");
    }

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

      // A section at another origin (#83): the location there, at once when it is on the page already (loaded ahead);
      // else the one shown so far left out until it is there. Its messages go into the section's batch (sent apart, the
      // page drew a picture between them: the sky without the map - a bright flash after the dark ship - or the new
      // map through the old camera).
      _bundle = new List<string>();
      try
      {
        if (s_location && _locationKey != null && OriginOf(index) is float[] here && KeyOf(here) is string hereKey && hereKey != _locationKey)
        {
          if (_locations.TryGetValue(hereKey, out var ready) && _locationVisibleOf.ContainsKey(hereKey))
          {
            ShowLocation(hereKey, ready);
            PreloadLocations();
          }
          else
          {
            _locationVisible = new List<int>();
            PostVisible();
            LoadLocation();
          }
        }
      }
      catch
      {
        _bundle = null;
        throw;
      }

      // All in one message: the page draws once, the section complete (one by one, a picture came between
      // them: the new clip with the old cast and camera, a frame that tore the cutscene). The section's own messages
      // the page has from "prepare" already, else in this one.
      var messages = new List<object>
      {
        new { type = "useCamera", on = s_camera && data.HasCamera },
        new { type = "showCamera", on = s_showCamera },
      };
      if (_frame > 0)
      {
        messages.Add(new { type = "seek", frame = _frame });
      }
      if (play)
      {
        messages.Add(new { type = "play", on = true });
      }
      string rest = JsonSerializer.Serialize(messages);
      // The location's messages of the change (if any) after the section's own, before the camera is used and played.
      string bundled = string.Concat(_bundle.Select(b => b + ","));
      _bundle = null;
      PostJson(_sectionsPrepared.Contains(index)
        ? "{\"type\":\"batch\",\"prepared\":" + index + ",\"messages\":[" + bundled + rest.Substring(1) + "}"
        : "{\"type\":\"batch\",\"messages\":" + data.Json.Substring(0, data.Json.Length - 1) + "," + bundled + rest.Substring(1) + "}");
      if (_player != null)
      {
        // (Made ahead on another thread as a rule; else now.)
        var (motions, camera) = NativeSectionOf(index).GetAwaiter().GetResult();
        _player.SetSection(motions, camera, _fps, _frames, _frame, play, s_camera && camera != null,
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
      if (!data.HasCamera)
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
        _player?.Seek(_frame);
        if (play)
        {
          Post(new { type = "play", on = true });
          _player?.Play(true);
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
        // The sound's time with its fraction; the page follows it gently.
        Post(new { type = "sync", frame = (seconds - _sectionStart[section]) * _fps });
        _player?.Sync((seconds - _sectionStart[section]) * _fps);
      }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
      if (_playing)
      {
        _playing = false;
        Post(new { type = "play", on = false });
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

    /// <summary>The clock at the time in the scene, forwards and backwards as the time goes, and the sky with it.</summary>
    private void ShowClock()
    {
      int minute = (int)(HourNow * 60);
      ClockText.Text = (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00");
      if (_lit && (minute != _clockMinute ||
                   (_document.Location is CutsceneLocation location && CameraNow() is Vector3 camera && BoxesAt(location, camera, HourNow) != _skyBoxes)))
      {
        PostSky();
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
      Post(new { type = "lockTime", on = _document.ClockLocked });
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
    // and on stopping the lowest and highest rate with their section and frame (from the page, see its countFrame).
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
      FrameText.Text = TimeSpan.FromSeconds(now).ToString(@"m\:ss\.f") + " / " + TimeSpan.FromSeconds(Length).ToString(@"m\:ss\.f");
      SectionText.Text = "Section " + (_section + 1) + " of " + _document.Cut.Sections.Count + "   ·   frame " + (_frame + 1) + " / " + _frames;
      _settingSlider = true;
      TimeSlider.Value = Math.Clamp(Math.Round(now * 1000), 0, TimeSlider.Maximum);
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
      _player?.UseCamera(s_camera);
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
        _player?.UseCamera(false);
      }
      Post(new { type = "fit" });
      _player?.Fit();
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
