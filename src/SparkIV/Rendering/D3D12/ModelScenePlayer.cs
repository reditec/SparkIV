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
using System.Linq;
using System.Numerics;
using SparkIV.WinUI.Viewers;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// What moves a model in the animation view (its game's: see GtaIV.Pc.AnimationPose): its bones' matrices (rest to now)
  /// at a frame of its clip, and the textures it moves.
  /// </summary>
  public interface IViewerAnimation
  {
    int Frames { get; }
    float Fps { get; }
    bool HasClip { get; }

    /// <summary>
    /// The skinning matrices at a frame (null: the rest pose; the facial clips played or not, the movement through the
    /// world or not); null without a skeleton (drawn unskinned). <paramref name="moverAt"/>: where the clip has moved the
    /// model to, when it does.
    /// </summary>
    Matrix4x4[]? SkinAt(double? frame, bool facials, bool move, out Vector3? moverAt);

    /// <summary>The texture matrices the clip sets at a frame, by material id; null for none.</summary>
    IReadOnlyDictionary<int, (Vector3 Row0, Vector3 Row1)>? UvAt(double frame);
  }

  /// <summary>
  /// The model view's and the animation view's scene for the native renderer (#112 step 6): one model as ModelView.html
  /// shows it, told what the page is told (the views' messages: scene, visible, options, background, lights, fit, spot,
  /// and for the animation view the skeleton and clip, play, seek, speed, loop, move, facials), saying what the page says
  /// (spot, frame, loaded). Its own camera is the page's (OrbitCamera, with the cutscene view's smoothing), orbiting the
  /// model: left drag turns round it, middle or right drag (Shift + left) moves, the wheel zooms, W/S/A/D/Q/E walk.
  /// The orders are taken by the render thread at the start of a frame; the callbacks run on the UI thread.
  /// </summary>
  public sealed class ModelScenePlayer : IViewerScene, IDisposable
  {
    private readonly Action<Action> _tell;
    private readonly ConcurrentQueue<Action> _orders = new ConcurrentQueue<Action>();
    private readonly List<Action> _waiting = new List<Action>();
    private MeshRenderer? _renderer;
    private readonly System.Threading.SemaphoreSlim _loading = new System.Threading.SemaphoreSlim(1, 1);
    private int _loads;

    // The scene shown: its model (the renderer's number), its meshes by id (for fit, spots, the wire frame).
    private ModelScene? _scene;
    private int _model = -1;
    private int _group = 1;
    private readonly Dictionary<int, SceneMesh> _meshes = new Dictionary<int, SceneMesh>();
    private readonly Dictionary<int, (Vector3 Min, Vector3 Max)> _bounds = new Dictionary<int, (Vector3, Vector3)>();
    private List<int> _visible = new List<int>();
    private bool _fitWanted;
    private bool _textures = true, _wireframe, _showLights;
    private int _background;
    private IReadOnlyList<ViewerLight> _lights = Array.Empty<ViewerLight>();

    // The camera: the page's own, its drag, wheel and walking keys.
    private readonly OrbitCamera _camera = new OrbitCamera();
    private readonly CursorDrag _drag = new CursorDrag();
    private float _dragHeight = 1;
    private int _wheel;
    private List<(int Axis, int Sign)> _walking = new List<(int, int)>();
    private bool _walkFast;
    private double _last = -1;
    private int _width = 1, _height = 1;

    // The animation: its pose, the page's playback (time in seconds, speed, loop), its options.
    private IViewerAnimation? _animation;
    private bool _playing, _loop = true, _move, _facials = true;
    private double _time, _speed = 1;
    private int _reportedFrame = -1;
    private Vector3? _lastMover;
    private float? _ground;

    private readonly ViewerFrame _frame = new ViewerFrame();

    /// <summary><paramref name="tell"/> runs an action on the UI thread (the callbacks).</summary>
    public ModelScenePlayer(Action<Action> tell)
    {
      _tell = tell;
      JitWarmUp.Start(typeof(MeshRenderer), typeof(TextureCache), typeof(ModelScenePlayer), typeof(OrbitCamera));
    }

    /// <summary>The spots found or the one looked at (the page's "spot": index -1 the whole model, count).</summary>
    public Action<int, int>? SpotChanged { get; set; }

    /// <summary>The model and its textures are on the GPU (the page's "loaded").</summary>
    public Action? Loaded { get; set; }

    /// <summary>The whole frame shown changed, and whether it plays on (the page's "frame").</summary>
    public Action<int, bool>? FrameChanged { get; set; }

    /// <summary>The draw calls of the last frame.</summary>
    public int DrawCalls { get; private set; }

    private void Order(Action order) => _orders.Enqueue(order);

    private void Tell(Action action) => _tell(action);

    // Runs on the loading thread (one load at a time); what it returns runs on the render thread at a frame's start.
    private void Load(Func<MeshRenderer, Action> work)
    {
      if (_renderer == null)
      {
        _waiting.Add(() => Load(work));
        return;
      }
      var renderer = _renderer;
      System.Threading.Interlocked.Increment(ref _loads);
      System.Threading.Tasks.Task.Run(async () =>
      {
        await _loading.WaitAsync();
        try
        {
          var done = work(renderer);
          Order(done);
        }
        catch (Exception ex)
        {
          D3D12Device.Log("model load failed: " + ex);
        }
        finally
        {
          _loading.Release();
          if (System.Threading.Interlocked.Decrement(ref _loads) == 0)
          {
            Order(() => Tell(() => Loaded?.Invoke()));
          }
        }
      });
    }

    /// <summary>
    /// The model (the page's "scene"): all its meshes onto the GPU in the background (their textures with their smaller
    /// levels, as the page makes them), <paramref name="skinned"/> moving with bones (the animation view's ped).
    /// </summary>
    public void SetScene(ModelScene? scene, bool skinned)
    {
      Order(() =>
      {
        _scene = scene;
        _meshes.Clear();
        _bounds.Clear();
        _ground = null;
        if (scene == null)
        {
          Drop();
          return;
        }
        foreach (var mesh in scene.Meshes)
        {
          _meshes[mesh.Id] = mesh;
          var min = new Vector3(float.PositiveInfinity);
          var max = new Vector3(float.NegativeInfinity);
          for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
          {
            var p = new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
          }
          _bounds[mesh.Id] = (min, max);
          // The grid's height: the lowest point of the meshes (the page's createGrid, all of them).
          _ground = Math.Min(_ground ?? float.PositiveInfinity, min.Z);
        }
        var meshes = new Dictionary<int, SceneMesh>(_meshes);
        Load(renderer =>
        {
          renderer.ImageMipmaps = true;
          renderer.ViewerMeshes = id => meshes.TryGetValue(id, out var m) ? m : null;
          var source = new ModelSource(scene.Meshes, scene.Materials, scene.Textures, skinned);
          var prepared = renderer.Prepare(new[] { source });
          return () =>
          {
            Drop();
            int group = ++_group;
            _model = renderer.Register(prepared, group)[0];
            _modelGroup = group;
          };
        });
      });
    }

    private int _modelGroup = -1;

    private void Drop()
    {
      if (_renderer != null && _model >= 0)
      {
        _renderer.ForgetViewerModel(_model);
        _renderer.RemoveGroup(_modelGroup);
      }
      _model = -1;
    }

    /// <summary>The meshes shown (the page's "visible"); the first time, or with <paramref name="fit"/>, the view fits them.</summary>
    public void SetVisible(IReadOnlyList<int> ids, bool fit)
    {
      var list = ids.ToList();
      Order(() =>
      {
        bool first = _visible.Count == 0;
        _visible = list;
        FindSpots();
        if (first || fit)
        {
          _fitWanted = true;
        }
      });
    }

    /// <summary>The page's "options": textures, wire frame, the lights' markers.</summary>
    public void SetOptions(bool textures, bool wireframe, bool lights) => Order(() =>
    {
      _textures = textures;
      _wireframe = wireframe;
      bool changed = _showLights != lights;
      _showLights = lights;
      if (changed)
      {
        FindSpots();
      }
    });

    /// <summary>The page's "background": 0 the gradient, 1 grey, 2 black, 3 white.</summary>
    public void SetBackground(int background) => Order(() => _background = background);

    /// <summary>The model's lights (the page's "lights"), shown as markers while the option is on.</summary>
    public void SetLights(IReadOnlyList<ViewerLight> lights)
    {
      var list = lights.ToList();
      Order(() =>
      {
        _lights = list;
        FindSpots();
      });
    }

    /// <summary>Looks at all that is shown (the page's "fit", the Fit button, a double-click).</summary>
    public void Fit() => Order(() => _fitWanted = true);

    /// <summary>To the next (+1) or previous (-1) spot (the page's "spot").</summary>
    public void Spot(int step) => Order(() => GoToSpot(step));

    // --- The animation (the page's skeleton, clip and playback) ---

    /// <summary>
    /// The pose and clip (the page's "skeleton" and "clip"): playing from its first frame, at once when
    /// <paramref name="play"/> (the page's clip with play); null: none.
    /// </summary>
    public void SetAnimation(IViewerAnimation? animation, bool play) => Order(() =>
    {
      _playing = false;
      _animation = animation;
      _time = 0;
      _reportedFrame = -1;
      ShowFrame();
      if (play)
      {
        StartPlaying(true);
      }
    });

    /// <summary>Plays or pauses (the page's "play").</summary>
    public void Play(bool on) => Order(() => StartPlaying(on));

    /// <summary>To a frame, stopped (the page's "seek").</summary>
    public void Seek(int frame) => Order(() =>
    {
      StartPlaying(false);
      if (_animation is { HasClip: true } a)
      {
        _time = Math.Clamp(frame, 0, a.Frames - 1) / (double)a.Fps;
        ShowFrame();
      }
    });

    /// <summary>The page's "speed", "loop", "move" (through the world) and "facials".</summary>
    public void SetSpeed(double speed) => Order(() => _speed = speed);
    public void SetLoop(bool on) => Order(() => _loop = on);
    public void SetMove(bool on) => Order(() => _move = on);
    public void SetFacials(bool on) => Order(() => _facials = on);

    // The page's play: from the start again when at the end without looping.
    private void StartPlaying(bool on)
    {
      if (_animation is not { HasClip: true } a)
      {
        return;
      }
      if (on && CurrentFrame() >= a.Frames - 1 && !_loop)
      {
        _time = 0;
      }
      _playing = on;
      _last = -1;
      _reportedFrame = -1;
      ShowFrame();
    }

    private double CurrentFrame() => _animation is { HasClip: true } a ? Math.Min(_time * a.Fps, a.Frames - 1) : 0;

    // The page's showFrame: the whole frame reported when it changes.
    private void ShowFrame()
    {
      int whole = (int)Math.Floor(CurrentFrame() + 1e-6);
      if (whole != _reportedFrame)
      {
        _reportedFrame = whole;
        bool playing = _playing;
        Tell(() => FrameChanged?.Invoke(whole, playing));
      }
    }

    // --- The camera's controls (the page's mouse and keys, as the cutscene view's) ---

    /// <summary>A mouse press in the view (UI thread): its button, the screen's pixels per DIP, the view's height in DIPs.</summary>
    public void BeginDrag(int button, double scale, float viewHeight)
    {
      var at = CursorDrag.Cursor();
      Order(() =>
      {
        _drag.Begin(button, scale, at);
        _dragHeight = viewHeight;
      });
    }

    /// <summary>The mouse released (UI thread).</summary>
    public void EndDrag()
    {
      var at = CursorDrag.Cursor();
      Order(() => _drag.End(at));
    }

    /// <summary>A touch or pen drag's move: left turns round the target, else moves it.</summary>
    public void Turn(float dx, float dy) => Order(() => _camera.Turn(dx, dy, false));

    public void Pan(float dx, float dy, float viewHeight) => Order(() => _camera.Pan(dx, dy, viewHeight));

    /// <summary>The wheel (WinUI's MouseWheelDelta), summed per frame into one 1.15 step.</summary>
    public void Wheel(int mouseWheelDelta) => Order(() => _wheel += mouseWheelDelta);

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

    private void ApplyDrag()
    {
      if (!_drag.Take(out float dx, out float dy, out int button) || (dx == 0 && dy == 0))
      {
        return;
      }
      if (button == 0 && !CursorDrag.ShiftDown)
      {
        _camera.Turn(dx, dy, false);
      }
      else
      {
        _camera.Pan(dx, dy, _dragHeight);
      }
    }

    // --- Fit and spots (the page's fit, fitDistance, findSpots, goToSpot) ---

    // The page's fit (ModelView.html ~3019-3054): the box of what is shown (and the lights shown), from the side.
    private bool FitShown()
    {
      var min = new Vector3(float.PositiveInfinity);
      var max = new Vector3(float.NegativeInfinity);
      foreach (int id in _visible)
      {
        if (_bounds.TryGetValue(id, out var b) && b.Min.X <= b.Max.X)
        {
          min = Vector3.Min(min, b.Min);
          max = Vector3.Max(max, b.Max);
        }
      }
      if (_showLights)
      {
        foreach (var l in ShownLights())
        {
          min = Vector3.Min(min, l.Position);
          max = Vector3.Max(max, l.Position);
        }
      }
      if (float.IsPositiveInfinity(min.X))
      {
        return false;
      }
      _camera.Fit(min, max, Aspect);
      // The whole model again: no spot is the current one.
      if (_spotIndex >= 0)
      {
        _spotIndex = -1;
        ReportSpot();
      }
      return true;
    }

    // (The page's canvas.clientWidth / clientHeight.)
    private float Aspect => _width / (float)Math.Max(1, _height);

    private IEnumerable<ViewerLight> ShownLights()
    {
      var visible = new HashSet<int>(_visible);
      return _lights.Where(l => l.Meshes.Any(visible.Contains));
    }

    private List<(Vector3 Min, Vector3 Max)> _spots = new List<(Vector3, Vector3)>();
    private int _spotIndex = -1;

    private void ReportSpot()
    {
      int index = _spotIndex, count = _spots.Count;
      Tell(() => SpotChanged?.Invoke(index, count));
    }

    // The page's findSpots (ModelView.html ~1359-1444): the triangles shown on a grid of cells a 40th of the model
    // across, touching cells one spot, the lights with theirs or a spot each; offered when there are 2 to 80, the middle
    // one under a fifth of the model.
    private void FindSpots()
    {
      _spots = new List<(Vector3, Vector3)>();
      _spotIndex = -1;
      var shownLights = _showLights ? ShownLights().ToList() : new List<ViewerLight>();
      var min = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
      var max = new double[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
      foreach (int id in _visible)
      {
        if (!_bounds.TryGetValue(id, out var b))
        {
          continue;
        }
        for (int k = 0; k < 3; k++)
        {
          min[k] = Math.Min(min[k], Get(b.Min, k));
          max[k] = Math.Max(max[k], Get(b.Max, k));
        }
      }
      foreach (var l in shownLights)
      {
        for (int k = 0; k < 3; k++)
        {
          min[k] = Math.Min(min[k], Get(l.Position, k));
          max[k] = Math.Max(max[k], Get(l.Position, k));
        }
      }
      if (double.IsPositiveInfinity(min[0]))
      {
        ReportSpot();
        return;
      }
      double cell = Math.Max(1e-3, Math.Sqrt((max[0] - min[0]) * (max[0] - min[0]) + (max[1] - min[1]) * (max[1] - min[1]) + (max[2] - min[2]) * (max[2] - min[2])) / 40);
      int Index(double c) => Math.Min(39, Math.Max(0, (int)Math.Floor(c)));
      var cells = new Dictionary<(int, int, int), SpotCell>();
      long triangles = 0;
      foreach (int id in _visible)
      {
        if (_meshes.TryGetValue(id, out var m))
        {
          triangles += m.Indices.Length / 3;
        }
      }
      int stride = (int)Math.Max(1, triangles / 200000) * 3;
      var order = new List<SpotCell>();
      foreach (int id in _visible)
      {
        if (!_meshes.TryGetValue(id, out var m))
        {
          continue;
        }
        var p = m.Positions;
        var t = m.Indices;
        for (int i = 0; i + 2 < t.Length; i += stride)
        {
          var lo = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
          var hi = new double[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
          for (int v = 0; v < 3; v++)
          {
            for (int a = 0; a < 3; a++)
            {
              double value = p[t[i + v] * 3 + a];
              lo[a] = Math.Min(lo[a], value);
              hi[a] = Math.Max(hi[a], value);
            }
          }
          var from = new int[3];
          var to = new int[3];
          for (int a = 0; a < 3; a++)
          {
            from[a] = Index((lo[a] - min[a]) / cell);
            to[a] = Index((hi[a] - min[a]) / cell);
          }
          for (int ix = from[0]; ix <= to[0]; ix++)
          {
            for (int iy = from[1]; iy <= to[1]; iy++)
            {
              for (int iz = from[2]; iz <= to[2]; iz++)
              {
                var cellLo = new[] { min[0] + ix * cell, min[1] + iy * cell, min[2] + iz * cell };
                var partLo = new double[3];
                var partHi = new double[3];
                for (int a = 0; a < 3; a++)
                {
                  partLo[a] = Math.Max(lo[a], cellLo[a]);
                  partHi[a] = Math.Min(hi[a], cellLo[a] + cell);
                }
                if (!cells.TryGetValue((ix, iy, iz), out var c))
                {
                  cells[(ix, iy, iz)] = c = new SpotCell { X = ix, Y = iy, Z = iz, Min = partLo, Max = partHi };
                  order.Add(c);
                }
                else
                {
                  for (int a = 0; a < 3; a++)
                  {
                    c.Min[a] = Math.Min(c.Min[a], partLo[a]);
                    c.Max[a] = Math.Max(c.Max[a], partHi[a]);
                  }
                }
              }
            }
          }
        }
      }
      // Touching cells into spots (in the order the cells were made, as the page's Map).
      var found = new List<(double[] Min, double[] Max)>();
      foreach (var start in order)
      {
        if (start.Spot >= 0)
        {
          continue;
        }
        var spot = (Min: (double[])start.Min.Clone(), Max: (double[])start.Max.Clone());
        start.Spot = found.Count;
        var queue = new Stack<SpotCell>();
        queue.Push(start);
        while (queue.Count > 0)
        {
          var c = queue.Pop();
          for (int a = 0; a < 3; a++)
          {
            spot.Min[a] = Math.Min(spot.Min[a], c.Min[a]);
            spot.Max[a] = Math.Max(spot.Max[a], c.Max[a]);
          }
          for (int dx = -1; dx <= 1; dx++)
          {
            for (int dy = -1; dy <= 1; dy++)
            {
              for (int dz = -1; dz <= 1; dz++)
              {
                if (cells.TryGetValue((c.X + dx, c.Y + dy, c.Z + dz), out var n) && n.Spot < 0)
                {
                  n.Spot = found.Count;
                  queue.Push(n);
                }
              }
            }
          }
        }
        found.Add(spot);
        if (found.Count > 80)
        {
          ReportSpot();
          return;
        }
      }
      // Lights to the spot they are at, else a spot of their own.
      foreach (var l in shownLights)
      {
        var at = new double[] { l.Position.X, l.Position.Y, l.Position.Z };
        int near = found.FindIndex(s => Enumerable.Range(0, 3).All(a => at[a] >= s.Min[a] - cell && at[a] <= s.Max[a] + cell));
        if (near >= 0)
        {
          for (int a = 0; a < 3; a++)
          {
            found[near].Min[a] = Math.Min(found[near].Min[a], at[a]);
            found[near].Max[a] = Math.Max(found[near].Max[a], at[a]);
          }
        }
        else
        {
          found.Add((at.Select(v => v - cell / 2).ToArray(), at.Select(v => v + cell / 2).ToArray()));
        }
      }
      static double Across((double[] Min, double[] Max) s) =>
        Math.Sqrt((s.Max[0] - s.Min[0]) * (s.Max[0] - s.Min[0]) + (s.Max[1] - s.Min[1]) * (s.Max[1] - s.Min[1]) + (s.Max[2] - s.Min[2]) * (s.Max[2] - s.Min[2]));
      var sizes = found.Select(Across).OrderBy(v => v).ToList();
      bool small = sizes.Count > 0 && sizes[sizes.Count / 2] < cell * 40 / 5;
      if (found.Count >= 2 && found.Count <= 80 && small)
      {
        // In order along the model's longest side, then across it.
        var longest = Enumerable.Range(0, 3).OrderByDescending(a => max[a] - min[a]).ToArray();
        double Centre((double[] Min, double[] Max) s, int a) => (s.Min[a] + s.Max[a]) / 2;
        var sorted = found.ToList();
        sorted.Sort((p, q) =>
        {
          double a0 = Centre(p, longest[0]), b0 = Centre(q, longest[0]);
          double d = Math.Abs(a0 - b0) > cell ? a0 - b0 : Centre(p, longest[1]) - Centre(q, longest[1]);
          return d < 0 ? -1 : d > 0 ? 1 : 0;
        });
        _spots = sorted.Select(s => (new Vector3((float)s.Min[0], (float)s.Min[1], (float)s.Min[2]), new Vector3((float)s.Max[0], (float)s.Max[1], (float)s.Max[2]))).ToList();
      }
      ReportSpot();
    }

    private static float Get(Vector3 v, int k) => k == 0 ? v.X : k == 1 ? v.Y : v.Z;

    private sealed class SpotCell
    {
      public int X, Y, Z;
      public double[] Min = null!, Max = null!;
      public int Spot = -1;
    }

    // The page's goToSpot: looked at from where the camera looks now, filling the view.
    private void GoToSpot(int step)
    {
      if (_spots.Count == 0)
      {
        return;
      }
      _spotIndex = _spotIndex < 0 ? (step > 0 ? 0 : _spots.Count - 1) : (_spotIndex + step + _spots.Count) % _spots.Count;
      var (min, max) = _spots[_spotIndex];
      _camera.Target = (min + max) * 0.5f;
      _camera.Distance = _camera.FitDistance(min, max, Aspect, true);
      ReportSpot();
    }

    // --- The frame ---

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
      _width = Math.Max(1, width);
      _height = Math.Max(1, height);
      while (_orders.TryDequeue(out var order))
      {
        order();
      }
      double dt = _last < 0 ? 0 : now - _last;
      _last = now;

      // The camera: the drag since the last frame, the wheel's step, walking, then the shown view eases to the goal.
      ApplyDrag();
      if (_wheel != 0)
      {
        _camera.Zoom(-Math.Sign(_wheel));
        _wheel = 0;
      }
      _camera.Walk(_walking, dt, _walkFast);

      // Playing (the page's tick): the time on by the speed, round again or stopped at the end.
      if (_playing && _animation is { HasClip: true } clip)
      {
        double length = (clip.Frames - 1) / (double)clip.Fps;
        _time += dt * _speed;
        if (_time > length)
        {
          if (_loop && length > 0)
          {
            _time %= length;
          }
          else
          {
            _time = length;
            _playing = false;
            _reportedFrame = -1;
          }
        }
        ShowFrame();
      }

      // The pose (the page's pose): the clip's frame (none without a clip), the camera carried along by the movement.
      Matrix4x4[]? palette = null;
      IReadOnlyDictionary<int, (Vector3, Vector3)>? uv = null;
      if (_animation != null)
      {
        double? frame = _animation.HasClip ? CurrentFrame() : null;
        palette = _animation.SkinAt(frame, _facials, _move, out var moverAt);
        if (moverAt is Vector3 at)
        {
          if (_lastMover is Vector3 was)
          {
            _camera.Shift(at - was);
          }
          _lastMover = at;
        }
        else if (_lastMover is Vector3 was)
        {
          _camera.Shift(-was);
          _lastMover = null;
        }
        if (frame is double f)
        {
          uv = _animation.UvAt(f);
        }
      }
      else if (_lastMover is Vector3 was)
      {
        _camera.Shift(-was);
        _lastMover = null;
      }
      // A fit asked for (after the pose's move, as the page's fit after its move message), then the view eases.
      if (_fitWanted && _bounds.Count > 0 && FitShown())
      {
        _fitWanted = false;
      }
      _camera.Settle(dt);

      // The page's viewProjection for its own camera (ModelView.html ~3092-3124): near and far by the distance and the
      // scene's size, the light from a little above and to the left of the camera.
      float distance = _camera.ShownDistance;
      float far = distance + Math.Max(_camera.SceneRadius * 4, distance);
      float near = _camera.NearClip(far, false);
      _frame.ViewProjection = _camera.ViewProjection(Aspect, near, far);
      _frame.Eye = _camera.Eye;
      _frame.ToEye = -_camera.Forward;
      _frame.Light = _camera.HeadLight();
      _frame.Model = _model;
      _frame.Palette = palette;
      _frame.Visible = _visible;
      _frame.Textures = _textures;
      _frame.Wireframe = _wireframe;
      _frame.ShowLights = _showLights;
      _frame.Lights = _lights;
      _frame.Background = _background;
      _frame.UvRows = uv;
      _frame.GridHeight = _move && palette != null ? _ground : null;
      return new SceneView(_frame.ViewProjection, _frame.Eye, default, default, _playing);
    }

    public void Draw(MeshRenderer renderer, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.ID3D12Resource target, int width, int height)
    {
      renderer.DrawViewer(list, target, width, height, _frame);
      DrawCalls = renderer.DrawCalls;
    }

    /// <summary>
    /// Sets the camera directly (the visual tests: the same view as the page's camera.yaw, pitch, distance, target),
    /// shown at once.
    /// </summary>
    public void SetCamera(float yaw, float pitch, float distance, Vector3 target, float sceneRadius) => Order(() =>
    {
      _camera.Yaw = yaw;
      _camera.Pitch = pitch;
      _camera.Distance = distance;
      _camera.Target = target;
      _camera.SceneRadius = sceneRadius;
      _camera.Snap();
      _fitWanted = false;
    });

    /// <summary>Whether a load is still going on (the visual tests wait for it).</summary>
    public bool Loading => System.Threading.Volatile.Read(ref _loads) > 0 || !_orders.IsEmpty;

    public void Dispose()
    {
      _loading.Wait();
      _loading.Release();
    }
  }
}
