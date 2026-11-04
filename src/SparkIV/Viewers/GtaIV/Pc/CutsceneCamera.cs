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
using System.Linq;
using System.Numerics;
using RageLib.GtaIV.Pc.Animation;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A GTA IV (PC) cutscene's camera (its section's camera_0 clip) for the native renderer (#112): where it is, which
  /// way it looks (along its y, z up) and its field of view, at any frame. The same as the page's filmCameraNow
  /// (ModelView.html): positions linearly between frames, the turn by normalized linear interpolation, the field of
  /// view of the whole frame; not smoothed across a cut (more than 25 cm or 5° from one frame to the next).
  /// </summary>
  public sealed class CutsceneCamera
  {
    private readonly float[] _positions;
    private readonly float[] _rotations;
    private readonly float[]? _fov;

    private CutsceneCamera(int frames, float fps, float[] positions, float[] rotations, float[]? fov)
    {
      Frames = frames;
      FramesPerSecond = fps;
      _positions = positions;
      _rotations = rotations;
      _fov = fov;
    }

    public int Frames { get; }
    public float FramesPerSecond { get; }

    /// <summary>The camera of a clip, or null without its position and rotation tracks.</summary>
    public static CutsceneCamera? From(Animation clip)
    {
      var position = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.CameraPosition && t.IsDecoded);
      var rotation = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.CameraRotation && t.IsDecoded);
      var fov = clip.Tracks.FirstOrDefault(t => t.Kind == AnimationTrackKind.CameraFov && t.IsDecoded);
      if (position == null || rotation == null)
      {
        return null;
      }
      int frames = Math.Max(1, clip.FrameCount);
      return new CutsceneCamera(frames, CutsceneMessages.FramesPerSecond(clip), Sample(clip, position, 3, frames),
                                Sample(clip, rotation, 4, frames), fov == null ? null : Sample(clip, fov, 1, frames));
    }

    private static float[] Sample(Animation clip, AnimationTrack track, int components, int frames)
    {
      var values = new float[frames * components];
      for (int f = 0; f < frames; f++)
      {
        var value = track.GetValue(f, clip.SequenceFrameLimit);
        for (int c = 0; c < components && value != null && c < value.Length; c++)
        {
          values[f * components + c] = value[c];
        }
      }
      return values;
    }

    /// <summary>The camera at a frame of its clip: eye, forward, up and the vertical field of view in radians.</summary>
    public (Vector3 Eye, Vector3 Forward, Vector3 Up, float Fov) At(double frame)
    {
      frame = Math.Clamp(frame, 0, Frames - 1);
      int whole = (int)Math.Floor(frame + 1e-6);
      var a = Position(whole);
      var b = Position(whole + 1);
      var qa = Rotation(whole);
      var qb = Rotation(whole + 1);
      bool cut = Vector3.Distance(a, b) > 0.25f || Math.Abs(Quaternion.Dot(qa, qb)) < MathF.Cos(5 * MathF.PI / 180);
      double at = cut ? whole : frame;
      int f0 = Math.Min((int)Math.Floor(at), Frames - 1);
      float t = (float)Math.Clamp(at - f0, 0, 1);
      var eye = Vector3.Lerp(Position(f0), Position(f0 + 1), t);
      var q0 = Rotation(f0);
      var q1 = Rotation(f0 + 1);
      if (Quaternion.Dot(q0, q1) < 0)
      {
        q1 = Quaternion.Negate(q1);
      }
      var q = Quaternion.Normalize(new Quaternion(q0.X * (1 - t) + q1.X * t, q0.Y * (1 - t) + q1.Y * t,
                                                  q0.Z * (1 - t) + q1.Z * t, q0.W * (1 - t) + q1.W * t));
      float degrees = _fov != null ? _fov[Math.Min(whole, _fov.Length - 1)] : 45;
      return (eye, Vector3.Normalize(Vector3.Transform(Vector3.UnitY, q)), Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q)),
              Math.Clamp(degrees, 1, 170) * MathF.PI / 180);
    }

    private Vector3 Position(int frame)
    {
      int f = Math.Clamp(frame, 0, Frames - 1) * 3;
      return new Vector3(_positions[f], _positions[f + 1], _positions[f + 2]);
    }

    private Quaternion Rotation(int frame)
    {
      int f = Math.Clamp(frame, 0, Frames - 1) * 4;
      return new Quaternion(_rotations[f], _rotations[f + 1], _rotations[f + 2], _rotations[f + 3]);
    }

    /// <summary>
    /// The view-projection through the camera at a frame, for a picture of that aspect: right-handed, depth 0 to 1
    /// (Direct3D), near 0.1 m as the page's (a cutscene's location reaches far), far as given.
    /// </summary>
    public Matrix4x4 ViewProjection(double frame, float aspect, float far)
    {
      var (eye, forward, up, fov) = At(frame);
      var view = Matrix4x4.CreateLookAt(eye, eye + forward, up);
      return view * Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.1f, far);
    }
  }
}
