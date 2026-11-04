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
using Microsoft.UI.Dispatching;
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
    // The sky (#112 4e): made on the render thread at its first frame; its cloud textures those of the location shown.
    private CutsceneSky? _sky;
    private (byte[]? Clouds, byte[]? Detail)? _skyTextures;
    private bool _skyTexturesNew;

    public void DrawBackground(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle lightTarget,
                               int width, int height, SceneView view)
    {
      if (_lighting == null)
      {
        return;
      }
      _sky ??= new CutsceneSky(device, ToneMapper.LightFormat);
      if (_skyTexturesNew && _skyTextures is var (clouds, detail))
      {
        _skyTexturesNew = false;
        using var allocator = device.Device.CreateCommandAllocator(Vortice.Direct3D12.CommandListType.Direct);
        using var upload = device.Device.CreateCommandList<Vortice.Direct3D12.ID3D12GraphicsCommandList>(0, Vortice.Direct3D12.CommandListType.Direct, allocator, null);
        var uploads = new List<Vortice.Direct3D12.ID3D12Resource>();
        _sky.SetTextures(clouds, detail, upload, uploads);
        upload.Close();
        device.Queue.ExecuteCommandList(upload);
        device.WaitIdle();
        foreach (var u in uploads)
        {
          u.Dispose();
        }
      }
      var cycle = _lighting.CycleAt(view.Eye, view.ViewProjection);
      // The clouds' Phase on from the page's start value by the section's time (the trace's per cutscene: not known here).
      float phase = CutsceneSky.CloudPhase(cycle, _time, false);
      _sky.Draw(list, lightTarget, width, height, view.ViewProjection, view.Eye, SkyParams.From(cycle, _lighting.Hour, phase));
    }

    // The water (#112 4f): made on the render thread; the location shown's water put on the GPU when it changes.
    private CutsceneWater? _water;
    private CutsceneLocation? _waterLocation;
    private bool _waterNew;
    // The mission script's wind (null: the page's breeze) and the swell's centre, scene-local (null: round the camera).
    private float? _waterWind;
    private Vector3? _swellAt;

    /// <summary>
    /// The water as the mission script leaves it (the page's "water" message): the wind it forces (null: none), and the
    /// point the sea's swell keeps round (the mission's marker, scene-local; null: the camera).
    /// </summary>
    public void SetWater(float? wind, Vector3? swellAt) => Order(() =>
    {
      _waterWind = wind;
      _swellAt = swellAt;
    });

    public void DrawAfterSolid(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle lightTarget,
                               Vortice.Direct3D12.CpuDescriptorHandle depth, int width, int height, SceneView view)
    {
      if (_lighting == null || _waterLocation == null || _waterLocation.Water.Count < 4)
      {
        return;
      }
      _water ??= new CutsceneWater(device, ToneMapper.LightFormat, PanelSwapChainFormats.Depth);
      if (_waterNew)
      {
        _waterNew = false;
        using var allocator = device.Device.CreateCommandAllocator(Vortice.Direct3D12.CommandListType.Direct);
        using var upload = device.Device.CreateCommandList<Vortice.Direct3D12.ID3D12GraphicsCommandList>(0, Vortice.Direct3D12.CommandListType.Direct, allocator, null);
        var uploads = new List<Vortice.Direct3D12.ID3D12Resource>();
        _water.SetWater(_waterLocation, upload, uploads);
        upload.Close();
        device.Queue.ExecuteCommandList(upload);
        device.WaitIdle();
        foreach (var u in uploads)
        {
          u.Dispose();
        }
      }
      var cycle = _lighting.CycleAt(view.Eye, view.ViewProjection);
      var sky = SkyParams.From(cycle, _lighting.Hour, CutsceneSky.CloudPhase(cycle, _time, false));
      // (Its simulation warms up and catches up over the first frames by itself; exact while still: the water a pure
      // function of the section's time, while playing a small nudge back of the sync held.)
      _water.Draw(list, lightTarget, depth, width, height, view.ViewProjection, view.Eye, view.Light, WaterParams.From(cycle, _lighting.Hour, sky, _waterWind, _swellAt), _time,
                  _renderer is { SunShadowDrawn: true } r ? r.SunShadowMap : null, exact: !view.Playing);
    }

    public void Dispose()
    {
      _sky?.Dispose();
      _sky = null;
      _water?.Dispose();
      _water = null;
    }
    private readonly DispatcherQueue _dispatcher;
    private readonly ConcurrentQueue<Action> _orders = new ConcurrentQueue<Action>();

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
    private Vector3 _center;
    private float _radius = 2;

    public CutscenePlayer(DispatcherQueue dispatcher)
    {
      _dispatcher = dispatcher;
    }

    private void Order(Action order) => _orders.Enqueue(order);

    private void Tell(Action action) => _dispatcher.TryEnqueue(() => action());

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
          var min = new Vector3(float.MaxValue);
          var max = new Vector3(float.MinValue);
          int at = 0;
          for (int a = 0; a < list.Count; a++)
          {
            var mine = numbers.Skip(at).Take(sources[a].Count).ToArray();
            at += sources[a].Count;
            // The cast (peds, vehicles, props) casts into the lamps' shadow maps, the location does not (as the page's;
            // the game's atlas held only peds in the traced frame).
            _castInstances[list[a]] = mine.Select(n => renderer.AddInstance(n, Matrix4x4.Identity, castsShadow: true)).ToArray();
            foreach (int n in mine)
            {
              if (renderer.Bounds(n) is var (lo, hi))
              {
                min = Vector3.Min(min, lo);
                max = Vector3.Max(max, hi);
              }
            }
          }
          if (min.X <= max.X)
          {
            _center = (min + max) * 0.5f;
            _radius = Math.Max(0.5f, (max - min).Length() * 0.5f);
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
    public void SetLocation(string? key, CutsceneLocation? location, float drawDistance) => Order(() =>
    {
      _far = Math.Max(1000f, drawDistance * 2);
      _shownLocation = location != null ? key : null;
      if (location != null && (_skyTextures is not var (c, d) || !ReferenceEquals(c, location.Clouds) || !ReferenceEquals(d, location.CloudDetail)))
      {
        _skyTextures = (location.Clouds, location.CloudDetail);
        _skyTexturesNew = true;
      }
      if (location != null && !ReferenceEquals(location, _waterLocation))
      {
        _waterLocation = location;
        _waterNew = true;
      }
      if (key != null && location != null && !_locations.ContainsKey(key))
      {
        LoadLocation(key, location);
      }
      ShowLocations();
    });

    /// <summary>A location put on the GPU ahead, not shown (the next section's at another origin).</summary>
    public void PrepareLocation(string key, CutsceneLocation location) => Order(() =>
    {
      if (!_locations.ContainsKey(key))
      {
        LoadLocation(key, location);
      }
    });

    /// <summary>A location no longer wanted: off the GPU (or not put there, if it is still loading).</summary>
    public void DropLocation(string key) => Order(() =>
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
      var objects = CutsceneDrawing.DrawnObjects(location).ToList();
      var models = location.Models.ToList();
      Load(renderer =>
      {
        var prepared = renderer.Prepare(models.Select(m => ModelSource.Shown(m, false, baked: true)).ToList());
        var lamps = new CutsceneLights(location);
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
          group.Instances = objects.Select(o => renderer.AddInstance(numbers[o.Model], o.Place, inRoom: o.Room >= 0, castsSunShadow: o.FloatAt == null)).ToArray();
          group.Lights = lamps;
          group.Reach = CutsceneSunShadow.LocationReach(location, m => renderer.Bounds(numbers[m]));
          group.Registered = true;
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
        try
        {
          var then = prepare(renderer);
          long ms = watch.ElapsedMilliseconds;
          Order(() =>
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
          _loading.Release();
        }
      });
    }

    private int _pendingLoads;

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
      Order(() =>
      {
        _poses = poses;
        _drawSteps = drawSteps;
        _cutLights = lights ?? new List<CutsceneLights.CutLight>();
        _camera = camera;
        _useCamera = useCamera;
        _fade = fade ?? CutsceneMessages.SectionFade.None;
        _fps = fps > 0 ? fps : 30;
        _frames = Math.Max(1, frames);
        _time = Math.Clamp(frame, 0, _frames - 1) / _fps;
        StartPlaying(play);
        ShowActors();
      });
    }

    /// <summary>To a frame of the section (as the page's seek).</summary>
    public void Seek(int frame) => Order(() =>
    {
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

    /// <summary>Through the section's camera or around the cast.</summary>
    public void UseCamera(bool on) => Order(() => _useCamera = on);

    /// <summary>The light of the time cycle at the clock (null: a plain light, as without a location).</summary>
    public void SetLighting(CutsceneLighting? lighting) => Order(() => _lighting = lighting);

    private CutsceneLighting? _lighting;

    private void StartPlaying(bool on)
    {
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
        foreach (var waiting in _waiting)
        {
          waiting();
        }
        _waiting.Clear();
      }
      while (_orders.TryDequeue(out var order))
      {
        order();
      }

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

      foreach (var (actor, pose) in _poses)
      {
        if (_castInstances.TryGetValue(actor, out var instances))
        {
          var palette = pose.SkinAt(frame, _fps);
          float ambient = _lighting?.ActorAmbient(pose.Place) ?? 1;
          foreach (int instance in instances)
          {
            renderer.SetPalette(instance, palette);
            renderer.SetActorAmbient(instance, ambient);
          }
        }
      }

      float aspect = width / (float)Math.Max(1, height);
      Matrix4x4 viewProjection;
      Vector3 eyeAt, toLight;
      // The camera's axes and opening (for the sun's shadow cascades).
      Vector3 viewForward, viewUp;
      float viewFov;
      var post = PostParams.None;
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
        // Without the cutscene's camera: slowly round the cast (the free view comes with the model view's move, #112 6).
        float angle = (float)(now * 0.3);
        float distance = _radius * 2.6f;
        eyeAt = _center + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, _radius * 0.6f);
        viewProjection = Matrix4x4.CreateLookAt(eyeAt, _center, Vector3.UnitZ) * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, aspect, 0.05f, _far);
        toLight = new Vector3(0.4f, -0.5f, 0.8f);
        (viewForward, viewUp, viewFov) = (Vector3.Normalize(_center - eyeAt), Vector3.UnitZ, MathF.PI / 4);
      }
      var (light, tone) = _lighting?.At(eyeAt, viewProjection) ?? (FrameLight.Plain(toLight), ToneParams.Plain);
      // The sun's shadow wherever the game's light is (the page's: outside, which every cutscene with a location is).
      if (_lighting != null)
      {
        // (Its casters as far as the page draws them: the haze's end, the shot's draw distance.)
        float locationReach = _visibleLocation != null && _locations.TryGetValue(_visibleLocation, out var at) ? at.Reach : float.PositiveInfinity;
        light.SunShadow = CutsceneSunShadow.Cascades(eyeAt, viewForward, viewUp, viewFov, aspect, light.SunDirection,
                                                     CutsceneSunShadow.CasterReach(light.FogEnd, locationReach, _drawSteps, frame));
      }
      // The lights of the frame (#112 step 5): the section's own, the tail lamps, the shown location's lamps.
      List<DeferredLight>? lights = null;
      if (_showLights && _lighting != null)
      {
        var lamps = _visibleLocation != null && _locations.TryGetValue(_visibleLocation, out var shown) ? shown.Lights : null;
        lamps ??= _noLamps;
        lamps.SetSection(_cutLights);
        lights = lamps.Now(frame, _fps, eyeAt, viewProjection, _lighting.Hour, light.Exposure, _poses.Values);
      }
      return new SceneView(viewProjection, eyeAt, light, tone, _playing) { Lights = lights, Post = post };
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
