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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using SparkIV.WinUI.Rendering.D3D12;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A GTA IV (PC) cutscene played by the native renderer (#112 step 4b), in place of the page's playback: the cast
  /// (every actor's models, shown while its section has it, posed at the frame), the location of the section's origin,
  /// the section's camera, and the clock. CutsceneView stays in charge (sound, sections, locations) and tells it what
  /// the page was told; the render thread takes those orders at the start of a frame. Like the page it says which
  /// frame it shows (when the whole frame changes), the frames per second while playing (once a second, with the
  /// lowest and highest rate) and when the rate falls below 30 or is back (the page's countFrame: below under 27.5,
  /// back from 29.5, over the last second), on the UI thread.
  /// </summary>
  public sealed class CutscenePlayer : IRenderScene, IDisposable
  {
    // The sky (#112 4e) and the water (4f): made on the render thread at its first frame (Update); the sky's cloud
    // textures those of the location shown (made again only when their bytes change: every location has skydome.wtd's).
    private CutsceneSky? _sky;
    private System.Threading.Tasks.Task<CutsceneSky>? _skyMade;
    private System.Threading.Tasks.Task<CutsceneWater>? _waterMade;
    private (byte[]? Clouds, byte[]? Detail, byte[]? Stars, byte[]? Galaxy)? _skyTextures;
    private bool _skyTexturesNew;

    private static bool SameBytes(byte[]? a, byte[]? b) => ReferenceEquals(a, b) || (a != null && b != null && a.AsSpan().SequenceEqual(b));

    public void DrawBackground(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle lightTarget,
                               int width, int height, SceneView view)
    {
      // (The sky only with the location shown, as the page's useSky; without it the view's background, see Update.)
      if (_lighting == null || _sky == null || _visibleLocation == null)
      {
        return;
      }
      if (_skyTexturesNew && _skyTextures is var (clouds, detail, stars, galaxy))
      {
        long started = GpuProfiler.Now;
        _skyTexturesNew = false;
        using var allocator = device.Device.CreateCommandAllocator(Vortice.Direct3D12.CommandListType.Direct);
        using var upload = device.Device.CreateCommandList<Vortice.Direct3D12.ID3D12GraphicsCommandList>(0, Vortice.Direct3D12.CommandListType.Direct, allocator, null);
        var uploads = new List<Vortice.Direct3D12.ID3D12Resource>();
        _sky.SetTextures(clouds, detail, upload, uploads, stars, galaxy);
        upload.Close();
        device.Queue.ExecuteCommandList(upload);
        device.WaitIdle();
        foreach (var u in uploads)
        {
          u.Dispose();
        }
        device.Profiler?.CpuSince("  draw: sky textures upload", started);
      }
      var cycle = _lighting.CycleAt(view.Eye, view.ViewProjection);
      float phase = CloudPhaseNow(cycle);
      var sky = SkyParams.From(cycle, _lighting.Hour, phase);
      // The water's simulations brought to the section's time every frame, also while the location isn't shown yet (they
      // go on a step a frame instead of catching up seconds when the water first shows; CutsceneWater.Advance).
      if (_water != null && _waterShown != null && _waterShown.Water.Count >= 4)
      {
        long started = GpuProfiler.Now;
        _water.Advance(list, WaterParams.From(cycle, _lighting.Hour, sky, _waterWind, _swellAt), _time, view.Eye, WaterExact, width, height);
        device.Profiler?.CpuSince("  draw: water advance", started);
      }
      _sky.Draw(list, lightTarget, width, height, view.ViewProjection, view.Eye, sky);
    }

    // The clouds' Phase now: on from the page's start value by the section's time (the trace's per cutscene: not known
    // here), held at it while the clock is held (the page's perlinNoise, ModelView.html ~2169-2203: clockLocked, from the
    // view's "lockTime" - CutsceneViewer.ClockLocked: "Lock time", else the mission script holding the clock, the intro).
    private float CloudPhaseNow(TimeCycle cycle) => CutsceneSky.CloudPhase(cycle, _time, _cloudsHeld);

    private bool _cloudsHeld;

    /// <summary>The clock held or running (the page's "lockTime" message): the clouds then stand still or drift.</summary>
    public void HoldClouds(bool held) => Order(() => _cloudsHeld = held);

    // The sky into the mirrored surroundings' map (the page's drawSky toMap, ModelView.html ~3765-3766): without the near
    // fog. (The sky's textures are made by DrawBackground, drawn first each frame.)
    public void DrawParabolicSky(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle target,
                                 int size, SceneView view)
    {
      if (_lighting == null || _sky == null)
      {
        return;
      }
      var cycle = _lighting.CycleAt(view.Eye, view.ViewProjection);
      var sky = SkyParams.From(cycle, _lighting.Hour, CloudPhaseNow(cycle));
      sky.NearFogAmount = 0;
      _sky.Draw(list, target, size, size, view.ViewProjection, view.Eye, sky, toMap: true);
    }

    // The water: the location whose water is wanted, and the one whose water is on the GPU and shown (its quads made on
    // another thread: CutsceneWater.Prepare, then Use here). Prepared waters by location (at most the shown and the next).
    private CutsceneWater? _water;
    private CutsceneLocation? _waterLocation;
    private CutsceneLocation? _waterShown;
    private readonly Dictionary<CutsceneLocation, System.Threading.Tasks.Task<CutsceneWater.Prepared>> _waterPrepared =
      new Dictionary<CutsceneLocation, System.Threading.Tasks.Task<CutsceneWater.Prepared>>();
    // The mission script's wind (null: the page's breeze) and the swell's centre, scene-local (null: round the camera).
    private float? _waterWind;
    private Vector3? _swellAt;

    // The water exact (a still frame: the simulations a pure function of the time) only after a seek while stopped, not
    // when the section ran out while the scene plays on (its next section follows: computing the swell afresh there
    // took 115 ms at bc_3's section ends), nor when paused: the state just played is kept (#64 - recomputed afresh, the
    // waves and the swell went through their steps again over several frames of up to 190 ms: the picture jerked a
    // moment after pausing).
    private bool WaterExact => !_playing && !_ranOut && !_paused;
    private bool _ranOut;
    private bool _paused;

    // A location's water made on the GPU on another thread (below the render thread), once.
    private void PrepareWater(CutsceneLocation location)
    {
      if (_water == null || location.Water.Count < 4 || _waterPrepared.ContainsKey(location))
      {
        return;
      }
      // (At most the wanted one and the next: older ones let go once made.)
      foreach (var old in _waterPrepared.Keys.Where(k => !ReferenceEquals(k, _waterLocation)).ToList())
      {
        var dropped = _waterPrepared[old];
        _waterPrepared.Remove(old);
        var device = _water.Device;
        dropped.ContinueWith(d =>
        {
          if (d.IsCompletedSuccessfully)
          {
            device.Retire(d.Result.Dispose);
          }
        });
      }
      var water = _water;
      _waterPrepared[location] = System.Threading.Tasks.Task.Run(() =>
      {
        var thread = System.Threading.Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = System.Threading.ThreadPriority.BelowNormal;
        try
        {
          return water.Prepare(location);
        }
        finally
        {
          thread.Priority = priority;
        }
      });
    }

    // The wanted water shown once it is made (the render thread, each frame); the prepared ones no longer wanted let go.
    private void ShowWater()
    {
      if (_water == null || _waterLocation == null || ReferenceEquals(_waterLocation, _waterShown))
      {
        return;
      }
      if (_waterLocation.Water.Count < 4)
      {
        _waterShown = _waterLocation;
        return;
      }
      PrepareWater(_waterLocation);
      if (!_waterPrepared.TryGetValue(_waterLocation, out var task) || !task.IsCompleted)
      {
        return;
      }
      if (task.IsCompletedSuccessfully)
      {
        _water.Use(task.Result);
      }
      else
      {
        D3D12Device.Log("water not made: " + task.Exception?.GetBaseException().Message);
      }
      _waterPrepared.Remove(_waterLocation);
      _waterShown = _waterLocation;
    }

    /// <summary>
    /// The water as the mission script leaves it (the page's "water" message): the wind it forces (null: none), and the
    /// point the sea's swell keeps round (the mission's marker, scene-local; null: the camera).
    /// </summary>
    public void SetWater(float? wind, Vector3? swellAt) => Order("  order: set water", () =>
    {
      _waterWind = wind;
      _swellAt = swellAt;
    });

    public void DrawAfterSolid(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle lightTarget,
                               Vortice.Direct3D12.CpuDescriptorHandle depth, int width, int height, SceneView view,
                               Vortice.Direct3D12.ID3D12Resource? depthBuffer)
    {
      if (_lighting == null || _water == null || _waterShown == null || _waterShown.Water.Count < 4)
      {
        return;
      }
      var cycle = _lighting.CycleAt(view.Eye, view.ViewProjection);
      var sky = SkyParams.From(cycle, _lighting.Hour, CloudPhaseNow(cycle));
      // The mirror first: the sky seen from the view mirrored in the water's level, without the near fog (the page's
      // reflection pass, ModelView.html: drawSky(mirrored, mirroredEye, false, true)).
      if (_sky != null && _water.MirrorOf(view.ViewProjection, view.Eye, out var mirroredViewProjection, out var mirroredEye))
      {
        var mirroredSky = sky;
        mirroredSky.NearFogAmount = 0;
        var (mirrorTarget, mirrorDepth, mirrorWidth, mirrorHeight) = _water.BeginMirror(list, width, height);
        _sky.Draw(list, mirrorTarget, mirrorWidth, mirrorHeight, mirroredViewProjection, mirroredEye, mirroredSky, mirror: true);
        // Then the far versions mirrored (ModelView.html drawMeshes(true, ...)): 0.515 m under the level clipped, as far
        // as the haze (the light's fog end, or nearer the location's reach) and the shot's draw distance (along the
        // camera's own forward).
        if (_renderer != null)
        {
          var forward = new Vector3(view.ViewProjection.M14, view.ViewProjection.M24, view.ViewProjection.M34);
          forward = forward.LengthSquared() > 0 ? Vector3.Normalize(forward) : Vector3.UnitY;
          float locationReach = _visibleLocation != null && _locations.TryGetValue(_visibleLocation, out var shown) ? shown.Reach : float.PositiveInfinity;
          _renderer.DrawMirror(list, mirrorTarget, mirrorDepth, mirrorWidth, mirrorHeight, mirroredViewProjection, mirroredEye, forward,
                               (_water.Level ?? 0) - 0.515f, CutsceneSunShadow.CasterReach(view.Light.FogEnd, locationReach, null, 0), view.Light.DrawReach);
        }
        _water.EndMirror(list);
      }
      // (Its simulations brought to the time by DrawBackground's Advance already; exact only when stopped by the user.)
      _water.Draw(list, lightTarget, depth, width, height, view.ViewProjection, view.Eye, view.Light, WaterParams.From(cycle, _lighting.Hour, sky, _waterWind, _swellAt), _time,
                  _renderer is { SunShadowDrawn: true } r ? r.SunShadowMap : null, exact: WaterExact, depthBuffer: depthBuffer);
    }
    public void Dispose()
    {
      // (Made on another thread: waited for, then let go.)
      try
      {
        _sky ??= _skyMade?.GetAwaiter().GetResult();
        _water ??= _waterMade?.GetAwaiter().GetResult();
      }
      catch (Exception)
      {
        // Not made: nothing to let go.
      }
      _sky?.Dispose();
      _sky = null;
      _water?.Dispose();
      _water = null;
    }
    // Runs an action on the UI thread (the app: its DispatcherQueue; cutgen: at once).
    private readonly Action<Action> _tell;
    // The orders for the render thread, each with the profiler's name for it.
    private readonly ConcurrentQueue<(string What, Action Run)> _orders = new ConcurrentQueue<(string, Action)>();

    /// <summary>The frame shown changed (whole frames), and whether it plays on.</summary>
    public Action<int, bool>? FrameChanged { get; set; }

    /// <summary>Once a second while playing: frames per second, the lowest and highest rate since playing began.</summary>
    public Action<int, (double Fps, int Frame)?, (double Fps, int Frame)?>? FpsMeasured { get; set; }

    /// <summary>The rate fell below 30 (true) or is back (false), the rate and the frame.</summary>
    public Action<bool, double, int>? FpsBelow { get; set; }

    /// <summary>What was asked to be shown is on the GPU (the cast, a location): what the page's "loaded" says.</summary>
    public Action? Loaded { get; set; }

    // The render thread's state.
    private MeshRenderer? _renderer;
    private readonly Dictionary<CutActor, int[]> _castInstances = new Dictionary<CutActor, int[]>();
    private float _far = 1000;
    private float _drawDistance;

    // Models go onto the GPU on a thread of their own, one load at a time (the drawing goes on meanwhile): the cast in
    // group 1, each location (by its origin's key, as CutsceneView keeps them) in a group of its own.
    private const int CastGroup = 1;
    private readonly System.Threading.SemaphoreSlim _loading = new System.Threading.SemaphoreSlim(1, 1);
    private sealed class LocationGroup
    {
      public int Group;
      public int[] Instances = Array.Empty<int>();
      public bool Registered;
      // Its lamps (#112 step 5), made on the loading thread.
      public CutsceneLights? Lights;
      // The farthest of its objects across the ground (CutsceneSunShadow.LocationReach: the page's hazeReach).
      public float Reach = float.PositiveInfinity;
    }

    // The section's draw distances by its frames (CutFile.DrawDistanceSteps; the page's drawSteps).
    private IReadOnlyList<float[]>? _drawSteps;

    // The section's own lights' tracks, and whether lights are shown at all (the view's light button, the page's
    // "sceneLights").
    private List<CutsceneLights.CutLight> _cutLights = new List<CutsceneLights.CutLight>();
    private bool _showLights = true;

    /// <summary>The cutscene's lights and the location's lamps shown or not (the view's light button).</summary>
    public void ShowLights(bool on) => Order(() => _showLights = on);
    private readonly Dictionary<string, LocationGroup> _locations = new Dictionary<string, LocationGroup>();
    private string? _shownLocation;
    private int _nextGroup = 2;
    // Orders that need the renderer, held until the first frame has it.
    private readonly List<Action> _waiting = new List<Action>();
    private Dictionary<CutActor, CutscenePose> _poses = new Dictionary<CutActor, CutscenePose>();
    private CutsceneCamera? _camera;
    private bool _useCamera = true;
    private float _fps = 30;
    private int _frames = 1;
    private CutsceneMessages.SectionFade _fade = CutsceneMessages.SectionFade.None;
    private double _time;
    private bool _playing;
    private double _last;
    private int _reportedFrame = -1;

    // The free view (without the cutscene's camera, or after leaving it): the page's own camera (ModelView.html's camera,
    // its mouse and keys), fitted to the cast at the first section (as the page at its first cast), the walking keys held.
    private readonly OrbitCamera _free = new OrbitCamera();
    private bool _fitWanted;
    private bool _fittedOnce;
    private List<(int Axis, int Sign)> _walking = new List<(int, int)>();
    private bool _walkFast;
    private double _walkLast = -1;

    /// <summary>
    /// The cutscene's camera was left by the mouse or a walking key (the page's "filmCamera" message, off), on the UI
    /// thread: the view's camera toggle goes off.
    /// </summary>
    public Action? CameraLeft { get; set; }

    /// <summary>
    /// A press in the view or a walking key (the page's pointerdown, keydown): through the cutscene's camera, the free
    /// view takes over from where it is (<see cref="CameraLeft"/> says so); else nothing.
    /// </summary>
    public void LeaveCamera() => Order(() =>
    {
      if (_useCamera && _camera != null)
      {
        LeaveFilmCamera();
        _useCamera = false;
        Tell(() => CameraLeft?.Invoke());
      }
    });

    /// <summary>Left drag by (dx, dy) view pixels: with a cutscene camera looking round from the eye, else round the target.</summary>
    public void Turn(float dx, float dy) => Order(() => _free.Turn(dx, dy, _camera != null));

    /// <summary>Middle or right drag (Shift + left) by (dx, dy) pixels of a view <paramref name="viewHeight"/> pixels high.</summary>
    public void Pan(float dx, float dy, float viewHeight) => Order(() => _free.Pan(dx, dy, viewHeight));

    // A mouse drag, its moves read on the render thread at each frame (CursorDrag), and the view's height in DIPs.
    private readonly CursorDrag _drag = new CursorDrag();
    private float _dragHeight = 1;

    /// <summary>
    /// A mouse press in the view (UI thread): its button (0 left, 1 middle, 2 right), the screen's pixels per DIP and the
    /// view's height in DIPs. From then on each frame turns or moves by the cursor's move since the last (the page's
    /// pointermove, sampled as late as possible) until <see cref="EndDrag"/>.
    /// </summary>
    public void BeginDrag(int button, double scale, float viewHeight)
    {
      var at = CursorDrag.Cursor();
      Order(() =>
      {
        _drag.Begin(button, scale, at);
        _dragHeight = viewHeight;
      });
    }

    /// <summary>The mouse released (UI thread): the move up to the cursor now is still taken.</summary>
    public void EndDrag()
    {
      var at = CursorDrag.Cursor();
      Order(() => _drag.End(at));
    }

    // The drag's move since the last frame (the page's pointermove: left without Shift turns, else moves).
    private void ApplyDrag()
    {
      if (!_drag.Take(out float dx, out float dy, out int button) || (dx == 0 && dy == 0))
      {
        return;
      }
      if (button == 0 && !CursorDrag.ShiftDown)
      {
        _free.Turn(dx, dy, _camera != null);
      }
      else
      {
        _free.Pan(dx, dy, _dragHeight);
      }
    }

    /// <summary>
    /// The wheel: WinUI's MouseWheelDelta (positive nearer). The deltas of a frame are summed and taken as one step of
    /// the page's deltaY sign, as the browser hands the page at most one coalesced wheel event a frame (a high-resolution
    /// wheel or a touchpad gives WinUI many small ones, each a whole 1.15 step otherwise).
    /// </summary>
    public void Wheel(int mouseWheelDelta) => Order(() => _wheel += mouseWheelDelta);

    private int _wheel;

    /// <summary>The free view looks at the cast where it is now (the page's fit: double-click, the Fit button).</summary>
    public void Fit() => Order(() => _fitWanted = true);

    /// <summary>The walking keys held (<see cref="OrbitCamera.WalkKey"/>) and Shift.</summary>
    public void Walk(IEnumerable<(int Axis, int Sign)> held, bool fast)
    {
      var keys = held.ToList();
      Order(() =>
      {
        _walking = keys;
        _walkFast = fast;
      });
    }

    // Leaving the cutscene's camera (the page's leaveFilmCamera): the free view goes on from its eye, looking the same
    // way, turning about a point ahead of it as far as the nearest actor before it (else 3 m, at most 15).
    private void LeaveFilmCamera()
    {
      if (_camera == null)
      {
        return;
      }
      double frame = Math.Min(_time * _fps, _frames - 1);
      var (eye, forward, _, fov) = _camera.At(frame / _fps * _camera.FramesPerSecond);
      float distance = float.PositiveInfinity;
      foreach (var pose in _poses.Values)
      {
        if (pose.LastWorld is not { Length: > 0 } world)
        {
          continue;
        }
        float along = Vector3.Dot(world[0].Translation - eye, forward);
        if (along > 0.5f)
        {
          distance = Math.Min(distance, along);
        }
      }
      _free.LookFrom(eye, forward, fov, Math.Min(float.IsPositiveInfinity(distance) ? 3 : distance, 15));
      // A first fit still waiting for the cast (it fits once the cast is posed) would throw the view left here away later.
      _fitWanted = false;
    }

    // The page's fit for a cutscene: the actors where they are now (their bones, and 0.3 m round them for the body);
    // false while none is posed yet (the cast still loading: the fit waits).
    private bool FitToCast(float aspect)
    {
      var min = new Vector3(float.PositiveInfinity);
      var max = new Vector3(float.NegativeInfinity);
      foreach (var pose in _poses.Values)
      {
        if (pose.LastWorld is not { } world)
        {
          continue;
        }
        foreach (var w in world)
        {
          min = Vector3.Min(min, w.Translation - new Vector3(0.3f));
          max = Vector3.Max(max, w.Translation + new Vector3(0.3f));
        }
      }
      if (min.X > max.X)
      {
        return false;
      }
      _free.Fit(min, max, aspect);
      return true;
    }

    /// <summary><paramref name="tell"/> runs an action on the UI thread (the callbacks: FrameChanged, Loaded, ...).</summary>
    public CutscenePlayer(Action<Action> tell)
    {
      _tell = tell;
      // The drawing code compiled ahead, not by the first frame that draws a location.
      JitWarmUp.Start(typeof(MeshRenderer), typeof(ToneMapper), typeof(TextureCache), typeof(CutscenePlayer), typeof(CutsceneWater), typeof(CutsceneSky),
                      typeof(CutsceneLights), typeof(CutscenePose), typeof(CutsceneLighting), typeof(CutsceneSunShadow));
    }

    private void Order(Action order) => _orders.Enqueue(("  order: other", order));

    private void Order(string what, Action order) => _orders.Enqueue((what, order));

    private void Tell(Action action) => _tell(action);

    /// <summary>Every actor of the scene (its models put on the GPU in the background; shown while a section has it).</summary>
    public void SetCast(IReadOnlyList<CutActor> actors)
    {
      var list = actors.ToList();
      Order(() => Load(renderer =>
      {
        var sources = list.Select(a => CutsceneDrawing.SourcesOf(a)).ToList();
        var prepared = renderer.Prepare(sources.SelectMany(s => s).ToList());
        return () =>
        {
          renderer.RemoveGroup(CastGroup);
          _castInstances.Clear();
          var numbers = renderer.Register(prepared, CastGroup);
          int at = 0;
          for (int a = 0; a < list.Count; a++)
          {
            var mine = numbers.Skip(at).Take(sources[a].Count).ToArray();
            at += sources[a].Count;
            // The cast (peds, vehicles, props) casts into the lamps' shadow maps, the location does not (as the page's;
            // the game's atlas held only peds in the traced frame).
            _castInstances[list[a]] = mine.Select(n => renderer.AddInstance(n, Matrix4x4.Identity, castsShadow: true)).ToArray();
          }
          ShowActors();
          LogScene(renderer, "cast: " + list.Count + " actors");
        };
      }));
    }

    /// <summary>
    /// The location shown, by its origin's key (null: none), and how far the camera sees (its far plane). One not on the
    /// GPU yet is put there in the background; the one shown so far stays until it is there.
    /// </summary>
    public void SetLocation(string? key, CutsceneLocation? location, float drawDistance) => Order("  order: set location", () =>
    {
      // The far plane with the location's reach once a loading thread has worked it out (LoadLocation; until then, while
      // the location isn't shown either, without it).
      _drawDistance = drawDistance;
      CutsceneDrawing.TryFarClip(location, drawDistance, out _far);
      _shownLocation = location != null ? key : null;
      if (location != null && (_skyTextures is not var (c, d, _, _) || !SameBytes(c, location.Clouds) || !SameBytes(d, location.CloudDetail)))
      {
        _skyTextures = (location.Clouds, location.CloudDetail, location.StarField, location.Galaxy);
        _skyTexturesNew = true;
      }
      if (location != null && !ReferenceEquals(location, _waterLocation))
      {
        _waterLocation = location;
        PrepareWater(location);
      }
      if (key != null && location != null && !_locations.ContainsKey(key))
      {
        LoadLocation(key, location);
      }
      ShowLocations();
    });

    /// <summary>A location put on the GPU ahead, not shown (the next section's at another origin).</summary>
    public void PrepareLocation(string key, CutsceneLocation location) => Order("  order: prepare location", () =>
    {
      if (!_locations.ContainsKey(key))
      {
        LoadLocation(key, location);
      }
      PrepareWater(location);
    });

    /// <summary>A location no longer wanted: off the GPU (or not put there, if it is still loading).</summary>
    public void DropLocation(string key) => Order("  order: drop location", () =>
    {
      if (_locations.Remove(key, out var group) && group.Registered && _renderer != null)
      {
        _renderer.RemoveGroup(group.Group);
      }
    });

    private void LoadLocation(string key, CutsceneLocation location)
    {
      var group = new LocationGroup { Group = _nextGroup++ };
      _locations[key] = group;
      // All its objects: the far versions also into the water's mirror, those only for the mirror (and the page's map
      // of the surroundings) drawn there alone (see MeshRenderer.DrawMirror).
      var objects = location.Objects.ToList();
      var models = location.Models.ToList();
      Load(renderer =>
      {
        var prepared = renderer.Prepare(models.Select(m => ModelSource.Shown(m, false, baked: true)).ToList());
        var lamps = new CutsceneLights(location);
        // The far plane's reach (every vertex: here, not on the render thread).
        CutsceneDrawing.BackgroundReach(location);
        // The coronas' sprite and the light volumes' lookup (the same for every location): made here, once.
        int corona = !_coronaSpriteSet && location.Corona != null ? renderer.PrepareTexture(location.Corona) : 0;
        int volume = !_volumeLookupSet && location.LightVolume != null ? renderer.PrepareTexture(location.LightVolume) : 0;
        return () =>
        {
          // Dropped (or another load for the key) meanwhile: not wanted any more.
          if (!_locations.TryGetValue(key, out var now) || now != group)
          {
            renderer.Discard(prepared);
            return;
          }
          var numbers = renderer.Register(prepared, group.Group);
          // (Floating litter casts no sun shadow, as the page's.)
          group.Instances = objects.Select(o => renderer.AddInstance(numbers[o.Model], o.Place, inRoom: o.Room >= 0, castsSunShadow: o.FloatAt == null,
                                                                     mirrored: o.Far, mainView: !o.MirrorOnly)).ToArray();
          group.Lights = lamps;
          // The coronas' sprite (coronas.wtd's, the same for every location): once.
          if (corona != 0)
          {
            renderer.UseCoronaSprite(corona);
            _coronaSpriteSet = true;
          }
          // The light volumes' lookup texture (lights_occluders.wtd's, the same for every location): once.
          if (volume != 0)
          {
            renderer.UseVolumeLookup(volume);
            _volumeLookupSet = true;
          }
          group.Reach =CutsceneSunShadow.LocationReach(location, m => renderer.Bounds(numbers[m]));
          group.Registered = true;
          if (key == _shownLocation)
          {
            _far = CutsceneDrawing.FarClip(location, _drawDistance);
          }
          ShowLocations();
          LogScene(renderer, "location " + key + ": " + objects.Count + " objects");
        };
      });
    }

    /// <summary>
    /// Work for the GPU on a loading thread (one at a time): <paramref name="prepare"/> makes the models there and gives
    /// back what the render thread then does with them (at the start of a frame).
    /// </summary>
    private void Load(Func<MeshRenderer, Action> prepare)
    {
      if (_renderer is not MeshRenderer renderer)
      {
        _waiting.Add(() => Load(prepare));
        return;
      }
      _pendingLoads++;
      System.Threading.Tasks.Task.Run(async () =>
      {
        await _loading.WaitAsync();
        var watch = Stopwatch.StartNew();
        // Below the render thread: a load never takes time from a frame (restored: a thread of the pool).
        var thread = System.Threading.Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = System.Threading.ThreadPriority.BelowNormal;
        try
        {
          var then = prepare(renderer);
          long ms = watch.ElapsedMilliseconds;
          Order("  order: loaded (register)", () =>
          {
            then();
            _pendingLoads--;
            D3D12Device.Log($"loaded in the background in {ms} ms");
            if (_pendingLoads == 0)
            {
              Tell(() => Loaded?.Invoke());
            }
          });
        }
        catch (Exception ex)
        {
          D3D12Device.Log("loading failed: " + ex);
          Order(() => _pendingLoads--);
        }
        finally
        {
          thread.Priority = priority;
          _loading.Release();
        }
      });
    }

    private int _pendingLoads;
    private bool _coronaSpriteSet, _volumeLookupSet;

    private void ShowLocations()
    {
      if (_renderer == null)
      {
        return;
      }
      // The one asked for once it is on the GPU; until then the one shown before stays (none: none).
      if (_shownLocation == null)
      {
        _visibleLocation = null;
      }
      else if (_locations.TryGetValue(_shownLocation, out var wanted) && wanted.Registered)
      {
        _visibleLocation = _shownLocation;
      }
      foreach (var (key, group) in _locations)
      {
        foreach (int instance in group.Instances)
        {
          _renderer.SetVisible(instance, key == _visibleLocation);
        }
      }
    }

    private string? _visibleLocation;
    private readonly CutsceneLights _noLamps = new CutsceneLights(null);

    private static void LogScene(MeshRenderer renderer, string what)
    {
      var textures = renderer.TextureStats;
      D3D12Device.Log($"{what} on the GPU; now {renderer.InstanceCount} instances, {renderer.Triangles} triangles, {textures.Made} textures ({textures.Bytes / 1048576} MB)");
    }

    /// <summary>
    /// A section: its actors' motions, its camera (null: none), its frames a second and frames; at a frame, playing or
    /// not; through the camera or not; its own lights (CutsceneLights.TracksOf); its draw distances by its frames
    /// (CutFile.DrawDistanceSteps).
    /// </summary>
    public void SetSection(IReadOnlyList<CutsceneMessages.CastMotion> motions, CutsceneCamera? camera, float fps, int frames,
                           int frame, bool play, bool useCamera, List<CutsceneLights.CutLight>? lights = null,
                           CutsceneMessages.SectionFade? fade = null, IReadOnlyList<float[]>? drawSteps = null)
    {
      var poses = motions.ToDictionary(m => m.Actor, m => new CutscenePose(m));
      Order("  order: set section", () =>
      {
        _poses = poses;
        // The free view fitted to the first section's cast (the page: "if (list.length && first) fit()").
        if (!_fittedOnce && poses.Count > 0)
        {
          _fittedOnce = true;
          _fitWanted = true;
        }
        _drawSteps = drawSteps;
        _cutLights = lights ?? new List<CutsceneLights.CutLight>();
        _camera = camera;
        _useCamera = useCamera;
        _fade = fade ?? CutsceneMessages.SectionFade.None;
        _fps = fps > 0 ? fps : 30;
        _frames = Math.Max(1, frames);
        _time = Math.Clamp(frame, 0, _frames - 1) / _fps;
        // (Another section, or one shown again: a time of its own, not one played on to.)
        _paused = false;
        _playing = false;
        StartPlaying(play);
        ShowActors();
      });
    }

    /// <summary>To a frame of the section (as the page's seek).</summary>
    public void Seek(int frame) => Order(() =>
    {
      _ranOut = false;
      _paused = false;
      _time = Math.Clamp(frame, 0, _frames - 1) / _fps;
      _reportedFrame = -1;
    });

    /// <summary>Playing on or stopping (as the page's play).</summary>
    public void Play(bool on) => Order(() => StartPlaying(on));

    /// <summary>The sound's time (a frame with its fraction): followed gently, as the page's sync.</summary>
    public void Sync(double frame) => Order(() =>
    {
      if (!_playing)
      {
        return;
      }
      double d = Math.Clamp(frame, 0, _frames - 1) / _fps - _time;
      _time += Math.Abs(d) > 0.4 ? d : d * 0.2;
    });

    /// <summary>The cutscene's camera shown as lines in the free view or not (the page's showCamera).</summary>
    public void ShowCamera(bool on) => Order(() => _showCamera = on);

    private bool _showCamera = true;

    /// <summary>The view's background without the location (0 the gradient, 1 grey, 2 black, 3 white; the page's background).</summary>
    public void SetBackground(int background) => Order(() => _background = background);

    private int _background;

    /// <summary>Through the section's camera or the free view (off: it goes on from where the camera is, as the page's useCamera).</summary>
    public void UseCamera(bool on) => Order(() =>
    {
      if (!on && _useCamera)
      {
        LeaveFilmCamera();
      }
      _useCamera = on;
    });

    /// <summary>The light of the time cycle at the clock (null: a plain light, as without a location).</summary>
    public void SetLighting(CutsceneLighting? lighting) => Order("  order: set lighting", () => _lighting = lighting);

    private CutsceneLighting? _lighting;

    private void StartPlaying(bool on)
    {
      // (Stopped while playing: paused, the water kept as played.)
      _paused = !on && (_playing || _paused);
      _ranOut = false;
      if (on && _time * _fps >= _frames - 1)
      {
        _time = 0;
      }
      _playing = on;
      _last = 0;
      _reportedFrame = -1;
      FpsReset();
    }

    private void ShowActors()
    {
      if (_renderer == null)
      {
        return;
      }
      foreach (var (actor, instances) in _castInstances)
      {
        bool shown = _poses.ContainsKey(actor);
        foreach (int instance in instances)
        {
          _renderer.SetVisible(instance, shown);
        }
      }
    }

    public SceneView Update(MeshRenderer renderer, double now, int width, int height)
    {
      if (_renderer == null)
      {
        _renderer = renderer;
        // The sky and the water made on another thread (their shaders compiled, their pipelines made: ~100 ms), taken
        // once ready; the water's noise and base come with its first frames.
        var device = renderer.Device;
        _skyMade = System.Threading.Tasks.Task.Run(() => new CutsceneSky(device, ToneMapper.LightFormat));
        _waterMade = System.Threading.Tasks.Task.Run(() => new CutsceneWater(device, ToneMapper.LightFormat, PanelSwapChainFormats.Depth));
        foreach (var waiting in _waiting)
        {
          waiting();
        }
        _waiting.Clear();
      }
      // (Profiling: each order's time as a CPU stage of its name.)
      var profiler = renderer.Profiler;
      while (_orders.TryDequeue(out var order))
      {
        long started = profiler != null ? GpuProfiler.Now : 0;
        order.Run();
        profiler?.CpuSince(order.What, started);
      }
      if (_sky == null && _skyMade is { IsCompletedSuccessfully: true })
      {
        _sky = _skyMade.Result;
      }
      if (_water == null && _waterMade is { IsCompletedSuccessfully: true })
      {
        _water = _waterMade.Result;
      }
      ShowWater();
      // The clock, as the page's tick: on by the time passed, stopping at the section's end.
      if (_playing)
      {
        CountFrame(now);
        double dt = _last > 0 ? now - _last : 0;
        _last = now;
        double length = (_frames - 1) / _fps;
        _time += dt;
        if (_time > length)
        {
          _time = length;
          _playing = false;
          _ranOut = true;
          _reportedFrame = -1;
        }
      }
      double frame = Math.Min(_time * _fps, _frames - 1);
      int whole = (int)Math.Floor(frame + 1e-6);
      if (whole != _reportedFrame)
      {
        _reportedFrame = whole;
        bool playing = _playing;
        Tell(() => FrameChanged?.Invoke(whole, playing));
      }

      // (Profiling: the update's parts as CPU stages.)
      long stage = profiler != null ? GpuProfiler.Now : 0;
      foreach (var (actor, pose) in _poses)
      {
        if (_castInstances.TryGetValue(actor, out var instances))
        {
          var palette = pose.SkinAt(frame, _fps);
          float ambient = _lighting?.ActorAmbient(pose.Place) ?? 1;
          // The face's shapes by its clip's weights now (the page's blendFace; null: the head's own shape).
          var face = pose.FaceAt(frame, _fps);
          foreach (int instance in instances)
          {
            renderer.SetPalette(instance, palette);
            renderer.SetActorAmbient(instance, ambient);
            renderer.SetShapeWeights(instance, face);
          }
        }
      }

      if (profiler != null)
      {
        profiler.CpuSince("  update: poses", stage);
        stage = GpuProfiler.Now;
      }
      // The mouse's move since the last frame, read as late as possible: right before the view is built (after a press's
      // leaving of the cutscene's camera, in the orders above, and after the poses).
      ApplyDrag();
      if (_wheel != 0)
      {
        _free.Zoom(-Math.Sign(_wheel));
        _wheel = 0;
      }
      float aspect = width / (float)Math.Max(1, height);
      // The free view: fitted once the actors are posed (their bones' places now), walking by the keys held (the page's
      // walk: by the time passed, at most 0.1 s a step; its speed easing in and out), then what is shown eased towards
      // the goal (OrbitCamera.Settle: native only, not the page's), every frame, also while stopped.
      if (_fitWanted && FitToCast(aspect))
      {
        _fitWanted = false;
      }
      double sinceLast = _walkLast < 0 ? 0 : now - _walkLast;
      _walkLast = now;
      _free.PivotAtEye = _camera != null;
      _free.Walk(_walking, sinceLast, _walkFast);
      _free.Settle(sinceLast);
      Matrix4x4 viewProjection;
      Vector3 eyeAt, toLight;
      // The camera's axes and opening (for the sun's shadow cascades).
      Vector3 viewForward, viewUp;
      float viewFov;
      var post = PostParams.None;
      List<Vector3>? cameraLines = null;
      if (_useCamera && _camera != null)
      {
        var (eye, forward, up, fov) = _camera.At(frame / _fps * _camera.FramesPerSecond);
        (viewForward, viewUp, viewFov) = (forward, up, fov);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        viewProjection = Matrix4x4.CreateLookAt(eye, eye + forward, up) * Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.1f, _far);
        eyeAt = eye;
        toLight = -forward + up * 0.55f - right * 0.35f;
        // #112 4h: the camera's depth of field and the cutscene's fades, only through its camera (as the page).
        var dof = _camera.DofAt(frame / _fps * _camera.FramesPerSecond);
        post = new PostParams
        {
          DepthOfField = dof != null,
          NearClip = 0.1f,
          FarClip = _far,
          FocusNear = dof?.Near ?? 0,
          FocusFar = dof?.Far ?? 0,
          Fade = _fade.At(frame),
        };
      }
      else
      {
        // Without the cutscene's camera: the free view (the page's own camera; no depth of field, no fades). Its far plane
        // the location's as through the camera (and beyond the scene), its near one as the page's free view.
        float far = Math.Max(_far, _free.ShownDistance + _free.SceneRadius * 4);
        eyeAt = _free.Eye;
        viewProjection = _free.ViewProjection(aspect, _free.NearClip(far, _visibleLocation != null), far);
        toLight = _free.HeadLight();
        (viewForward, viewUp, viewFov) = (_free.Forward, _free.Axes().Up, _free.Fov);
        // The cutscene's camera shown where it is and what it sees (the page's drawFilmCamera, "Show camera").
        if (_camera != null && _showCamera)
        {
          var (cEye, cForward, cUp, cFov) = _camera.At(frame / _fps * _camera.FramesPerSecond);
          cameraLines = LineOverlay.CameraLines(cEye, cForward, cUp, cFov);
        }
      }
      var (light, tone) = _lighting?.At(eyeAt, viewProjection) ?? (FrameLight.Plain(toLight), ToneParams.Plain);
      // Through the cutscene's camera nothing beyond the shot's draw distance (the page's drawReach, ModelView.html:
      // reachNow = filmView() ? drawDistanceNow() : Infinity; the fragment discards beyond it, but not the water).
      if (_useCamera && _camera != null)
      {
        float reach = CutsceneSunShadow.DrawDistance(_drawSteps, frame);
        light.DrawReach = float.IsFinite(reach) ? reach : 0;
      }
      // The sun's shadow wherever the game's light is (the page's: outside, which every cutscene with a location is).
      if (_lighting != null)
      {
        // (Its casters as far as the page draws them: the haze's end, the shot's draw distance.)
        float locationReach = _visibleLocation != null && _locations.TryGetValue(_visibleLocation, out var at) ? at.Reach : float.PositiveInfinity;
        light.SunShadow = CutsceneSunShadow.Cascades(eyeAt, viewForward, viewUp, viewFov, aspect, light.SunDirection,
                                                     CutsceneSunShadow.CasterReach(light.FogEnd, locationReach, _drawSteps, frame));
      }
      if (profiler != null)
      {
        profiler.CpuSince("  update: camera, light, cascades", stage);
        stage = GpuProfiler.Now;
      }
      // The lights of the frame (#112 step 5): the section's own, the tail lamps, the shown location's lamps.
      List<DeferredLight>? lights = null;
      List<Corona>? coronas = null;
      List<LightVolume>? volumes = null;
      if (_showLights && _lighting != null)
      {
        var lamps = _visibleLocation != null && _locations.TryGetValue(_visibleLocation, out var shown) ? shown.Lights : null;
        lamps ??= _noLamps;
        lamps.SetSection(_cutLights);
        // (Through the cutscene's camera only the lights before its far clip, the shot's draw distance, as the game's.)
        lights = lamps.Now(frame, _fps, eyeAt, viewProjection, _lighting.Hour, light.Exposure, _poses.Values,
                           farClip: _useCamera && _camera != null ? CutsceneSunShadow.DrawDistance(_drawSteps, frame) : float.PositiveInfinity);
        if (profiler != null)
        {
          profiler.CpuSince("  update: lights list", stage);
          stage = GpuProfiler.Now;
        }
        // The lamps' coronas (the page's coronasNow), x the time cycle's SprBght outside a room.
        coronas = lamps.CoronasNow(eyeAt, viewProjection, _lighting.Hour, _lighting.CycleAt(eyeAt, viewProjection).SpriteBrightness);
        volumes = lamps.VolumesNow(eyeAt, viewProjection, _lighting.Hour, _volumeLookupSet);
        profiler?.CpuSince("  update: coronas + volumes lists", stage);
      }
      // The mirrored surroundings' map wherever the game's light is (outside), its objects as far as the haze.
      float? parabolicHaze = null;
      if (_lighting != null)
      {
        float mapReach = _visibleLocation != null && _locations.TryGetValue(_visibleLocation, out var mapAt) ? mapAt.Reach : float.PositiveInfinity;
        parabolicHaze = CutsceneSunShadow.CasterReach(light.FogEnd, mapReach, null, 0);
      }
      return new SceneView(viewProjection, eyeAt, light, tone, _playing)
      {
        Lights = lights, Coronas = coronas, Volumes = volumes, Post = post, ParabolicHaze = parabolicHaze, OverlayLines = cameraLines,
        // Without the location the view's own background (the page's: the dropdown's choice; with it the sky or black).
        Background = _visibleLocation == null ? _background : null,
      };
    }

    // The page's countFrame (see FpsBelow, FpsMeasured).
    private readonly Queue<double> _times = new Queue<double>();
    private int _counted;
    private double _since;
    private bool _below;
    private (double Fps, int Frame)? _min, _max;

    private void FpsReset()
    {
      _times.Clear();
      _counted = 0;
      _since = 0;
      _below = false;
      _min = _max = null;
    }

    private void CountFrame(double now)
    {
      _times.Enqueue(now);
      while (_times.Count > 2 && now - _times.Peek() > 1.0)
      {
        _times.Dequeue();
      }
      if (_times.Count > 2 && now - _times.Peek() >= 0.95)
      {
        double rate = Math.Round((_times.Count - 1) * 10 / (now - _times.Peek())) / 10;
        int frame = (int)Math.Floor(Math.Min(_time * _fps, _frames - 1) + 1e-6);
        if (_below ? rate >= 29.5 : rate < 27.5)
        {
          _below = !_below;
          bool below = _below;
          Tell(() => FpsBelow?.Invoke(below, rate, frame));
        }
        if (_min == null || rate < _min.Value.Fps)
        {
          _min = (rate, frame);
        }
        if (_max == null || rate > _max.Value.Fps)
        {
          _max = (rate, frame);
        }
      }
      if (_since == 0)
      {
        _since = now;
      }
      _counted++;
      if (now - _since >= 1)
      {
        int fps = (int)Math.Round(_counted / (now - _since));
        var (min, max) = (_min, _max);
        Tell(() => FpsMeasured?.Invoke(fps, min, max));
        _counted = 0;
        _since = now;
      }
    }
  }
}
