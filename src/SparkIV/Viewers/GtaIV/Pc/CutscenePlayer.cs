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
  public sealed class CutscenePlayer : IRenderScene
  {
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
    private IReadOnlyList<CutActor> _actors = Array.Empty<CutActor>();
    private readonly Dictionary<CutActor, int[]> _castInstances = new Dictionary<CutActor, int[]>();
    private CutsceneLocation? _location;
    private bool _rebuild;
    private float _far = 1000;
    private Dictionary<CutActor, CutscenePose> _poses = new Dictionary<CutActor, CutscenePose>();
    private CutsceneCamera? _camera;
    private bool _useCamera = true;
    private float _fps = 30;
    private int _frames = 1;
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

    /// <summary>Every actor of the scene (its models put on the GPU; shown while a section has it).</summary>
    public void SetCast(IReadOnlyList<CutActor> actors) => Order(() =>
    {
      _actors = actors.ToList();
      _rebuild = true;
    });

    /// <summary>The location shown (null: none), and how far the camera sees (its far plane).</summary>
    public void SetLocation(CutsceneLocation? location, float drawDistance) => Order(() =>
    {
      if (!ReferenceEquals(location, _location))
      {
        _location = location;
        _rebuild = true;
      }
      _far = Math.Max(1000f, drawDistance * 2);
    });

    /// <summary>
    /// A section: its actors' motions, its camera (null: none), its frames a second and frames; at a frame, playing or
    /// not; through the camera or not.
    /// </summary>
    public void SetSection(IReadOnlyList<CutsceneMessages.CastMotion> motions, CutsceneCamera? camera, float fps, int frames,
                           int frame, bool play, bool useCamera)
    {
      var poses = motions.ToDictionary(m => m.Actor, m => new CutscenePose(m));
      Order(() =>
      {
        _poses = poses;
        _camera = camera;
        _useCamera = useCamera;
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

    /// <summary>The cast and the location onto the GPU anew (a location changed: for now all of it, see #112 4b v2).</summary>
    private void Rebuild(MeshRenderer renderer)
    {
      var watch = Stopwatch.StartNew();
      renderer.Clear();
      _castInstances.Clear();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      foreach (var actor in _actors)
      {
        var numbers = renderer.AddModels(CutsceneDrawing.SourcesOf(actor));
        _castInstances[actor] = numbers.Select(n => renderer.AddInstance(n, Matrix4x4.Identity)).ToArray();
        foreach (int n in numbers)
        {
          if (renderer.Bounds(n) is var (a, b))
          {
            min = Vector3.Min(min, a);
            max = Vector3.Max(max, b);
          }
        }
      }
      if (min.X <= max.X)
      {
        _center = (min + max) * 0.5f;
        _radius = Math.Max(0.5f, (max - min).Length() * 0.5f);
      }
      if (_location != null)
      {
        var numbers = renderer.AddModels(_location.Models);
        foreach (var o in CutsceneDrawing.DrawnObjects(_location))
        {
          renderer.AddInstance(numbers[o.Model], o.Place);
        }
      }
      ShowActors();
      var textures = renderer.TextureStats;
      D3D12Device.Log($"scene on the GPU: {_actors.Count} actors, location {(_location != null ? _location.Objects.Count + " objects" : "none")}, " +
                      $"{renderer.InstanceCount} instances, {renderer.Triangles} triangles, {textures.Made} textures ({textures.Bytes / 1048576} MB) in {watch.ElapsedMilliseconds} ms");
      Tell(() => Loaded?.Invoke());
    }

    public (Matrix4x4 ViewProjection, Vector3 ToLight, float Ambient) Update(MeshRenderer renderer, double now, int width, int height)
    {
      _renderer = renderer;
      while (_orders.TryDequeue(out var order))
      {
        order();
      }
      if (_rebuild)
      {
        _rebuild = false;
        Rebuild(renderer);
        // (The time it took isn't played over.)
        _last = 0;
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
          foreach (int instance in instances)
          {
            renderer.SetPalette(instance, palette);
          }
        }
      }

      float aspect = width / (float)Math.Max(1, height);
      if (_useCamera && _camera != null)
      {
        var (eye, forward, up, fov) = _camera.At(frame / _fps * _camera.FramesPerSecond);
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, up);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        // The light from a little above and to the left of the camera, as the page's.
        return (view * Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.1f, _far), -forward + up * 0.55f - right * 0.35f, 0.35f);
      }
      // Without the cutscene's camera: slowly round the cast (the free view comes with the model view's move, #112 6).
      float angle = (float)(now * 0.3);
      float distance = _radius * 2.6f;
      var orbitEye = _center + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, _radius * 0.6f);
      var orbit = Matrix4x4.CreateLookAt(orbitEye, _center, Vector3.UnitZ);
      return (orbit * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, aspect, 0.05f, _far), new Vector3(0.4f, -0.5f, 0.8f), 0.35f);
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
