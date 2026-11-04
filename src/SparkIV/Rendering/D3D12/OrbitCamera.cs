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
  /// </summary>
  public sealed class OrbitCamera
  {
    // The page's initial values (ModelView.html: const camera = { yaw: -0.6, pitch: 0.35, distance: 5, ... }; sceneRadius = 1).
    public float Yaw = -0.6f;
    public float Pitch = 0.35f;
    public float Distance = 5;
    public Vector3 Target = Vector3.Zero;
    public float Fov = 45 * MathF.PI / 180;

    /// <summary>Half the diagonal of what was fitted last (the page's sceneRadius): zoom limits and walking speed.</summary>
    public float SceneRadius = 1;

    /// <summary>From the eye towards the target's opposite: the unit vector from the target to the eye.</summary>
    public Vector3 ToEye
    {
      get
      {
        float c = MathF.Cos(Pitch);
        return new Vector3(c * MathF.Cos(Yaw), c * MathF.Sin(Yaw), MathF.Sin(Pitch));
      }
    }

    /// <summary>Where the camera is (the page's eye()).</summary>
    public Vector3 Eye => Target + Distance * ToEye;

    /// <summary>Where it looks.</summary>
    public Vector3 Forward => Vector3.Normalize(Target - Eye);

    /// <summary>The view's right and up (z up, as the page's viewProjection).</summary>
    public (Vector3 Right, Vector3 Up) Axes()
    {
      var f = Forward;
      var s = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
      return (s, Vector3.Cross(s, f));
    }

    /// <summary>
    /// Left drag by (dx, dy) pixels of the view (the page's pointermove, button 0 without Shift): yaw and pitch on;
    /// <paramref name="aroundEye"/> (a cutscene: a free camera) keeps the eye where it is and moves the target, else it
    /// turns round the target.
    /// </summary>
    public void Turn(float dx, float dy, bool aroundEye)
    {
      var before = Eye;
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
      var (s, u) = Axes();
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
    /// Walking <paramref name="dt"/> seconds (at most 0.1) with the keys held (<see cref="WalkKey"/>): 3 m a second (a
    /// brisk walk; <paramref name="fast"/>, Shift: 4 times), slower in a scene smaller than that (the page's walk).
    /// </summary>
    public void Walk(IEnumerable<(int Axis, int Sign)> held, double dt, bool fast)
    {
      dt = Math.Min(0.1, dt);
      float speed = Math.Min(3, Math.Max(0.05f, SceneRadius)) * (fast ? 4 : 1);
      var f = Forward;
      var s = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
      Span<Vector3> axes = stackalloc Vector3[] { f, s, Vector3.UnitZ };
      var step = Vector3.Zero;
      foreach (var (axis, sign) in held)
      {
        step += axes[axis] * sign;
      }
      Target += step * speed * (float)dt;
    }

    /// <summary>
    /// Going on from another camera (leaving a cutscene's): from its eye, looking along its forward with its opening,
    /// turning about a point <paramref name="distance"/> ahead (the page's leaveFilmCamera).
    /// </summary>
    public void LookFrom(Vector3 eye, Vector3 forward, float fov, float distance)
    {
      Distance = distance;
      Target = eye + forward * distance;
      Yaw = MathF.Atan2(-forward.Y, -forward.X);
      Pitch = MathF.Asin(Math.Clamp(-forward.Z, -1, 1));
      Fov = fov;
    }

    /// <summary>
    /// Looks at a box (min, max) from the side and a little above (the page's fit): a long one from its long side, a
    /// little turned; a flat one from high up; as far as its corners need in a view of <paramref name="aspect"/>.
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
    }

    /// <summary>
    /// How far the camera must be, looking from its yaw and pitch, for the box's eight corners to fit the view with a
    /// little room (the page's fitDistance); <paramref name="close"/>: no lower limit of a 20th of the scene.
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
    /// The near clip for a far plane (the page's viewProjection for the free view): not nearer than needed, at least a
    /// thousandth of the distance; with a location (<paramref name="background"/>) at least a 2000th of the far plane,
    /// at most 10 cm.
    /// </summary>
    public float NearClip(float far, bool background)
    {
      float near = Math.Max(0.001f, Distance - SceneRadius * 4);
      float nearClip = Math.Max(Distance / 1000, near * 0.1f);
      if (background)
      {
        nearClip = Math.Max(nearClip, Math.Min(0.1f, far / 2000));
      }
      return nearClip;
    }

    /// <summary>The view and projection (right-handed, z up) for a picture of <paramref name="aspect"/>.</summary>
    public Matrix4x4 ViewProjection(float aspect, float near, float far) =>
      Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitZ) * Matrix4x4.CreatePerspectiveFieldOfView(Fov, aspect, near, far);

    /// <summary>The light without a time cycle: from a little above and to the left of the camera (the page's light).</summary>
    public Vector3 HeadLight()
    {
      var (s, u) = Axes();
      return Vector3.Normalize(-Forward + u * 0.55f - s * 0.35f);
    }
  }
}
