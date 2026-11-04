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
using System.Numerics;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The viewer's own camera, as ModelView.html's (its camera, eye, fit, fitDistance, the mouse and the keys): it looks
  /// at a target from a yaw and pitch (radians, about z up) and a distance. Turning (left drag) orbits the target, or
  /// looks round from where the eye is (a free camera, as in a cutscene); moving (middle or right drag, Shift + left)
  /// shifts the target in the plane of the screen; the wheel zooms; W/S/A/D/Q/E walk. Not thread-safe: one thread
  /// (the render thread) uses it.
  /// <para>
  /// Unlike the page (a deliberate departure, native only): the input changes the goal (<see cref="Yaw"/>,
  /// <see cref="Pitch"/>, <see cref="Distance"/>, <see cref="Target"/>), and what is drawn (<see cref="Eye"/>,
  /// <see cref="Forward"/>, <see cref="ViewProjection"/>) follows it each frame (<see cref="Settle"/>) with
  /// frame-rate-independent exponential damping, a = 1 - exp(-dt / tau): the turn by <see cref="TurnTau"/>, moving and
  /// zooming (the distance in log space) by <see cref="MoveTau"/>; walking's speed eases in and out by
  /// <see cref="WalkTau"/>. SPARKIV_CAMERA_SMOOTH=&lt;ms&gt; sets the turn's tau (the others in proportion), 0: none,
  /// the page's direct feel. Leaving another camera (<see cref="LookFrom"/>) and fitting (<see cref="Fit"/>) snap.
  /// </para>
  /// </summary>
  public sealed class OrbitCamera
  {
    /// <summary>The damping's time constants in seconds (before SPARKIV_CAMERA_SMOOTH's scaling).</summary>
    public const float TurnTau = 0.07f, MoveTau = 0.09f, WalkTau = 0.11f;

    /// <summary>The factor on the time constants: SPARKIV_CAMERA_SMOOTH (ms) / TurnTau; 1 without it, 0 for none.</summary>
    public static readonly float Smoothing =
      Environment.GetEnvironmentVariable("SPARKIV_CAMERA_SMOOTH") is string ms && float.TryParse(ms, System.Globalization.NumberStyles.Float,
                                                                                                 System.Globalization.CultureInfo.InvariantCulture, out float value) && value >= 0
        ? value / 1000 / TurnTau : 1;

    // The goal: the page's initial values (ModelView.html: const camera = { yaw: -0.6, pitch: 0.35, distance: 5, ... }; sceneRadius = 1).
    public float Yaw = -0.6f;
    public float Pitch = 0.35f;
    public float Distance = 5;
    public Vector3 Target = Vector3.Zero;
    public float Fov = 45 * MathF.PI / 180;

    /// <summary>Half the diagonal of what was fitted last (the page's sceneRadius): zoom limits and walking speed.</summary>
    public float SceneRadius = 1;

    // What is shown: yaw, pitch, the distance's log, and the point it keeps (the eye when looking round from the eye,
    // else the target), so that turning round the eye keeps the eye still while the turn eases.
    private float _yaw, _pitch, _logDistance;
    private Vector3 _anchor;
    private bool _pivotAtEye;
    // Walking's speed now (m/s), eased towards the keys'.
    private Vector3 _velocity;

    public OrbitCamera()
    {
      Snap();
    }

    private static Vector3 ToEyeOf(float yaw, float pitch)
    {
      float c = MathF.Cos(pitch);
      return new Vector3(c * MathF.Cos(yaw), c * MathF.Sin(yaw), MathF.Sin(pitch));
    }

    /// <summary>The goal's unit vector from the target to the eye.</summary>
    public Vector3 ToEye => ToEyeOf(Yaw, Pitch);

    /// <summary>Where the goal puts the camera (the page's eye()).</summary>
    public Vector3 GoalEye => Target + Distance * ToEye;

    private Vector3 GoalAnchor => _pivotAtEye ? GoalEye : Target;

    private Vector3 ShownToEye => ToEyeOf(_yaw, _pitch);

    /// <summary>The distance shown.</summary>
    public float ShownDistance => MathF.Exp(_logDistance);

    private Vector3 ShownTarget => _pivotAtEye ? _anchor - ShownToEye * ShownDistance : _anchor;

    /// <summary>Where the camera is drawn from.</summary>
    public Vector3 Eye => _pivotAtEye ? _anchor : _anchor + ShownToEye * ShownDistance;

    /// <summary>Where the drawn view looks.</summary>
    public Vector3 Forward => -ShownToEye;

    /// <summary>The drawn view's right and up (z up, as the page's viewProjection).</summary>
    public (Vector3 Right, Vector3 Up) Axes() => AxesOf(Forward);

    private static (Vector3 Right, Vector3 Up) AxesOf(Vector3 f)
    {
      var s = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
      return (s, Vector3.Cross(s, f));
    }

    /// <summary>
    /// Whether turning keeps the eye (looking round, a cutscene) or the target (orbiting) still while it eases; changing
    /// it keeps the view shown.
    /// </summary>
    public bool PivotAtEye
    {
      get => _pivotAtEye;
      set
      {
        if (value == _pivotAtEye)
        {
          return;
        }
        var eye = Eye;
        var target = ShownTarget;
        _pivotAtEye = value;
        _anchor = value ? eye : target;
      }
    }

    /// <summary>What is shown is the goal at once (no easing).</summary>
    public void Snap()
    {
      _yaw = Yaw;
      _pitch = Pitch;
      _logDistance = MathF.Log(Math.Max(1e-6f, Distance));
      _anchor = GoalAnchor;
      _velocity = Vector3.Zero;
    }

    private static float Ease(double dt, float tau) => tau <= 0 ? 1 : (float)(1 - Math.Exp(-dt / tau));

    /// <summary>Before drawing a frame, <paramref name="dt"/> seconds after the last: what is shown eases towards the goal.</summary>
    public void Settle(double dt)
    {
      dt = Math.Clamp(dt, 0, 0.25);
      float turn = Ease(dt, TurnTau * Smoothing), move = Ease(dt, MoveTau * Smoothing);
      _yaw += (Yaw - _yaw) * turn;
      _pitch += (Pitch - _pitch) * turn;
      _logDistance += (MathF.Log(Math.Max(1e-6f, Distance)) - _logDistance) * move;
      _anchor += (GoalAnchor - _anchor) * move;
    }

    /// <summary>
    /// Left drag by (dx, dy) pixels of the view (the page's pointermove, button 0 without Shift): yaw and pitch on;
    /// <paramref name="aroundEye"/> (a cutscene: a free camera) keeps the eye where it is and moves the target, else it
    /// turns round the target.
    /// </summary>
    public void Turn(float dx, float dy, bool aroundEye)
    {
      PivotAtEye = aroundEye;
      var before = GoalEye;
      Yaw -= dx * 0.008f;
      Pitch = Math.Clamp(Pitch + dy * 0.008f, -1.55f, 1.55f);
      if (aroundEye)
      {
        Target = before - ToEye * Distance;
      }
    }

    /// <summary>
    /// Middle or right drag (or Shift + left) by (dx, dy) pixels: the target moved in the plane of the screen as far as
    /// the mouse moved there (<paramref name="viewHeight"/> the view's height in the same pixels).
    /// </summary>
    public void Pan(float dx, float dy, float viewHeight)
    {
      var (s, u) = AxesOf(-ToEye);
      float perPixel = 2 * Distance * MathF.Tan(Fov / 2) / Math.Max(1, viewHeight);
      Target += (-s * dx + u * dy) * perPixel;
    }

    /// <summary>
    /// The wheel (<paramref name="deltaSign"/> the sign of the page's deltaY: positive away, negative nearer): close
    /// enough for a small part of a large model, no farther than 50 times the scene.
    /// </summary>
    public void Zoom(int deltaSign)
    {
      float r = SceneRadius;
      Distance = Math.Max(Math.Max(Math.Min(0.02f, r * 0.02f), r * 0.002f), Math.Min(r * 50, Distance * MathF.Pow(1.15f, Math.Sign(deltaSign))));
    }

    /// <summary>A walking key's axis (0 forward, 1 right, 2 up) and direction (the page's walkKeys), or null.</summary>
    public static (int Axis, int Sign)? WalkKey(Windows.System.VirtualKey key) => key switch
    {
      Windows.System.VirtualKey.W => (0, 1),
      Windows.System.VirtualKey.S => (0, -1),
      Windows.System.VirtualKey.A => (1, -1),
      Windows.System.VirtualKey.D => (1, 1),
      Windows.System.VirtualKey.Q => (2, -1),
      Windows.System.VirtualKey.E => (2, 1),
      _ => null,
    };

    /// <summary>
    /// Each frame, <paramref name="dt"/> seconds (at most 0.1) with the keys held (<see cref="WalkKey"/>; none: easing
    /// out): 3 m a second (a brisk walk; <paramref name="fast"/>, Shift: 4 times), slower in a scene smaller than that
    /// (the page's walk), along where the view shown looks. The speed eases towards the keys' by <see cref="WalkTau"/>;
    /// the place moves with it at once (goal and shown alike).
    /// </summary>
    public void Walk(IEnumerable<(int Axis, int Sign)> held, double dt, bool fast)
    {
      dt = Math.Clamp(dt, 0, 0.1);
      float walk = Math.Min(3, Math.Max(0.05f, SceneRadius));
      float speed = walk * (fast ? 4 : 1);
      var f = Forward;
      var s = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
      Span<Vector3> axes = stackalloc Vector3[] { f, s, Vector3.UnitZ };
      var wanted = Vector3.Zero;
      bool any = false;
      foreach (var (axis, sign) in held)
      {
        wanted += axes[axis] * sign;
        any = true;
      }
      wanted *= speed;
      _velocity += (wanted - _velocity) * Ease(dt, WalkTau * Smoothing);
      // Released: stopped once under 7 % of the walking speed (about 0.3 s from a walk).
      if (!any && _velocity.Length() < walk * 0.07f)
      {
        _velocity = Vector3.Zero;
      }
      var step = _velocity * (float)dt;
      Target += step;
      _anchor += step;
    }

    /// <summary>
    /// Going on from another camera (leaving a cutscene's): from its eye, looking along its forward with its opening,
    /// turning about a point <paramref name="distance"/> ahead (the page's leaveFilmCamera); shown at once.
    /// </summary>
    public void LookFrom(Vector3 eye, Vector3 forward, float fov, float distance)
    {
      Distance = distance;
      Target = eye + forward * distance;
      Yaw = MathF.Atan2(-forward.Y, -forward.X);
      Pitch = MathF.Asin(Math.Clamp(-forward.Z, -1, 1));
      Fov = fov;
      Snap();
    }

    /// <summary>
    /// Looks at a box (min, max) from the side and a little above (the page's fit): a long one from its long side, a
    /// little turned; a flat one from high up; as far as its corners need in a view of <paramref name="aspect"/>. Shown
    /// at once.
    /// </summary>
    public void Fit(Vector3 min, Vector3 max, float aspect)
    {
      Target = (min + max) * 0.5f;
      SceneRadius = Math.Max(0.01f, (max - min).Length() / 2);
      float lengthX = max.X - min.X, lengthY = max.Y - min.Y;
      Yaw = lengthX > lengthY * 1.5f ? -1.25f : lengthY > lengthX * 1.5f ? -0.3f : -0.6f;
      float height = max.Z - min.Z, width = Math.Max(lengthX, lengthY);
      Pitch = height < width * 0.15f ? 1.1f : 0.35f;
      Distance = FitDistance(min, max, aspect, false);
      Snap();
    }

    /// <summary>
    /// How far the camera must be, looking from its (goal) yaw and pitch, for the box's eight corners to fit the view with
    /// a little room (the page's fitDistance); <paramref name="close"/>: no lower limit of a 20th of the scene.
    /// </summary>
    public float FitDistance(Vector3 min, Vector3 max, float aspect, bool close)
    {
      var toEye = ToEye;
      var forward = -toEye;
      var side = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
      var up = Vector3.Cross(side, forward);
      float tanY = MathF.Tan(Fov / 2) * 0.92f;
      float tanX = tanY * aspect;
      float distance = 0;
      for (int i = 0; i < 8; i++)
      {
        var corner = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z) - Target;
        // A corner at x across, y up and z towards the camera is in view while (distance - z) * tan >= |x|, |y|.
        float z = Vector3.Dot(corner, toEye);
        distance = Math.Max(distance, Math.Max(z + Math.Abs(Vector3.Dot(corner, side)) / tanX, z + Math.Abs(Vector3.Dot(corner, up)) / tanY));
      }
      return Math.Max(distance, Math.Max(close ? 0 : SceneRadius * 0.05f, 0.05f));
    }

    /// <summary>
    /// The near clip for a far plane (the page's viewProjection for the free view, by the distance shown): not nearer
    /// than needed, at least a thousandth of the distance; with a location (<paramref name="background"/>) at least a
    /// 2000th of the far plane, at most 10 cm.
    /// </summary>
    public float NearClip(float far, bool background)
    {
      float distance = ShownDistance;
      float near = Math.Max(0.001f, distance - SceneRadius * 4);
      float nearClip = Math.Max(distance / 1000, near * 0.1f);
      if (background)
      {
        nearClip = Math.Max(nearClip, Math.Min(0.1f, far / 2000));
      }
      return nearClip;
    }

    /// <summary>The view shown and its projection (right-handed, z up) for a picture of <paramref name="aspect"/>.</summary>
    public Matrix4x4 ViewProjection(float aspect, float near, float far)
    {
      var eye = Eye;
      return Matrix4x4.CreateLookAt(eye, eye + Forward, Vector3.UnitZ) * Matrix4x4.CreatePerspectiveFieldOfView(Fov, aspect, near, far);
    }

    /// <summary>The light without a time cycle: from a little above and to the left of the camera (the page's light).</summary>
    public Vector3 HeadLight()
    {
      var (s, u) = Axes();
      return Vector3.Normalize(-Forward + u * 0.55f - s * 0.35f);
    }
  }
}
