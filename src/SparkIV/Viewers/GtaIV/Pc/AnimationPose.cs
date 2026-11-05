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
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using SparkIV.WinUI.Rendering.D3D12;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>A track of a clip for the animation view: a bone's place (3 values a frame) or turn (4 values a frame).</summary>
  public sealed record ClipTrack(int Bone, bool Position, float[] Values);

  /// <summary>A facial clip the body clip starts (the page's clip.facials): from its frame on, on the face's bones.</summary>
  public sealed record FacialClip(int Start, int Frames, float Fps, IReadOnlyList<ClipTrack> Tracks);

  /// <summary>A clip moving a material's texture (the page's clip.uv): the texture matrix's two rows, 3 values a frame each.</summary>
  public sealed record UvTrack(int Material, float[] Row0, float[] Row1);

  /// <summary>
  /// A skeleton as the animation view gives the page (its "skeleton" message): per bone its parent, its place (3) and turn
  /// (4) relative to it, its name.
  /// </summary>
  public sealed record ViewerSkeleton(int[] Parent, float[] Position, float[] Rotation, string[] Names);

  /// <summary>A clip as the animation view gives the page (its "clip" message), the values as floats.</summary>
  public sealed record ViewerClip(int Frames, float Fps, IReadOnlyList<ClipTrack> Tracks, ClipTrack? MoverPosition, ClipTrack? MoverRotation,
                                  IReadOnlyList<FacialClip> Facials, IReadOnlyList<UvTrack> Uv);

  /// <summary>
  /// A ped's or map object's pose in the animation view for the native renderer (#112 step 6), GTA IV (PC): ModelView.html's
  /// sampledPose (~1659-1709, with the facial clips), findFollowers (~1577-1614), gameHelper (~1624-1637), worldMatrices,
  /// pose (~2840-2864: the clip's movement through the world), moverPath (~2869-2882) and updateUv (~2910-2920) in C#.
  /// Row vectors (System.Numerics): world = local x parent's world. Not thread-safe (the render thread uses it).
  /// </summary>
  public sealed class AnimationPose : IViewerAnimation
  {
    private readonly ViewerSkeleton? _skeleton;
    private readonly ViewerClip? _clip;
    private readonly Matrix4x4[] _inverseRest = Array.Empty<Matrix4x4>();
    private readonly List<(int Bone, int Leader, int TwistFrom)> _followers = new List<(int, int, int)>();
    private readonly List<(int Bone, string Kind, int A, int B)> _helpers = new List<(int, string, int, int)>();
    private readonly float[]? _moverPath;

    public AnimationPose(ViewerSkeleton? skeleton, ViewerClip? clip)
    {
      _skeleton = skeleton;
      _clip = clip;
      if (skeleton != null)
      {
        _inverseRest = World(skeleton.Position, skeleton.Rotation).Select(m => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();
        FindFollowers();
      }
      if (clip?.MoverPosition != null || clip?.MoverRotation != null)
      {
        _moverPath = MoverPath(clip!);
      }
    }

    public int Frames => _clip?.Frames ?? 1;
    public float Fps => _clip?.Fps ?? 30;
    public bool HasClip => _clip != null;
    public bool HasMover => _clip != null && (_clip.MoverPosition != null || _clip.MoverRotation != null);

    /// <inheritdoc/>
    public Matrix4x4[]? SkinAt(double? frame, bool facials, bool move, out Vector3? moverAt)
    {
      moverAt = null;
      if (_skeleton == null)
      {
        return null;
      }
      var (position, rotation) = SampledPose(frame, facials);
      var world = World(position, rotation);
      if (_clip != null && frame is double f && move && HasMover)
      {
        // The whole ped moved through the world (the page's mover: its path, its turn).
        var p = Vector3.Zero;
        var q = Quaternion.Identity;
        if (_moverPath != null)
        {
          p = SamplePosition(_moverPath, f, _clip.Frames);
        }
        if (_clip.MoverRotation != null)
        {
          q = SampleRotation(_clip.MoverRotation.Values, f, _clip.Frames);
        }
        var mover = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);
        for (int i = 0; i < world.Length; i++)
        {
          world[i] *= mover;
        }
        moverAt = p;
      }
      var skin = new Matrix4x4[world.Length];
      for (int i = 0; i < world.Length; i++)
      {
        skin[i] = _inverseRest[i] * world[i];
      }
      return skin;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, (Vector3 Row0, Vector3 Row1)>? UvAt(double frame)
    {
      if (_clip == null || _clip.Uv.Count == 0)
      {
        return null;
      }
      // Frame by frame, not in between (the page's updateUv: many switch between pictures side by side).
      int whole = (int)Math.Floor(frame + 1e-6);
      var rows = new Dictionary<int, (Vector3, Vector3)>();
      foreach (var u in _clip.Uv)
      {
        rows[u.Material] = (SamplePosition(u.Row0, whole, _clip.Frames), SamplePosition(u.Row1, whole, _clip.Frames));
      }
      return rows;
    }

    // The page's sampledPose: the clip's tracks over the rest, the facial clip playing then, the helper bones.
    private (Vector3[] Position, Quaternion[] Rotation) SampledPose(double? frame, bool facials)
    {
      var s = _skeleton!;
      int n = s.Parent.Length;
      var position = new Vector3[n];
      var rotation = new Quaternion[n];
      for (int i = 0; i < n; i++)
      {
        position[i] = RestPosition(i);
        rotation[i] = RestRotation(i);
      }
      if (_clip == null || frame is not double f)
      {
        return (position, rotation);
      }
      void Apply(IReadOnlyList<ClipTrack> tracks, double at, int frames)
      {
        foreach (var t in tracks)
        {
          if (t.Bone < 0 || t.Bone >= n)
          {
            continue;
          }
          if (t.Position)
          {
            position[t.Bone] = SamplePosition(t.Values, at, frames);
          }
          else
          {
            rotation[t.Bone] = SampleRotation(t.Values, at, frames);
          }
        }
      }
      Apply(_clip.Tracks, f, _clip.Frames);
      // Facial clips: the one started last at or before this frame plays on the face's bones until it ends.
      if (facials && _clip.Facials.Count > 0)
      {
        FacialClip? facial = null;
        foreach (var c in _clip.Facials)
        {
          if (c.Start <= f)
          {
            facial = c;
          }
        }
        if (facial != null)
        {
          double local = (f - facial.Start) / _clip.Fps * facial.Fps;
          if (local <= facial.Frames - 1)
          {
            Apply(facial.Tracks, local, facial.Frames);
          }
        }
      }
      foreach (var (bone, leader, twistFrom) in _followers)
      {
        var q = Mul(Mul(rotation[leader], Quaternion.Conjugate(RestRotation(leader))), RestRotation(bone));
        if (twistFrom >= 0)
        {
          var d = Mul(rotation[twistFrom], Quaternion.Conjugate(RestRotation(twistFrom)));
          var twist = Normalized(new Quaternion(d.X, 0, 0, d.W));
          if (twist.W < 0)
          {
            twist = Quaternion.Negate(twist);
          }
          q = Mul(q, Normalized(new Quaternion(twist.X, 0, 0, twist.W + 1)));
        }
        rotation[bone] = Normalized(q);
      }
      static double EulerX(Quaternion q) => Math.Atan2(2.0 * (q.W * q.X + q.Y * q.Z), 1 - 2.0 * (q.X * q.X + q.Y * q.Y));
      static Quaternion Rx(double a) => new Quaternion((float)Math.Sin(a / 2), 0, 0, (float)Math.Cos(a / 2));
      foreach (var (bone, kind, a, b) in _helpers)
      {
        Quaternion q;
        switch (kind)
        {
          case "armRoll":
            q = Mul(rotation[a], Rx(-0.25 * EulerX(rotation[a])));
            break;
          case "upperArmRoll":
            q = Rx(Math.PI + 0.15 * EulerX(rotation[a]));
            break;
          case "foreTwist":
            double diff = EulerX(rotation[b]) - EulerX(RestRotation(b));
            // (Math.round of the page: halves up.)
            diff -= 2 * Math.PI * Math.Floor(diff / (2 * Math.PI) + 0.5);
            q = Mul(rotation[a], Rx(0.5 * diff));
            break;
          case "neckRoll":
            q = rotation[a];
            break;
          default:
            q = Quaternion.Identity;
            break;
        }
        rotation[bone] = Normalized(q);
      }
      return (position, rotation);
    }

    private Vector3 RestPosition(int i) => new Vector3(_skeleton!.Position[i * 3], _skeleton.Position[i * 3 + 1], _skeleton.Position[i * 3 + 2]);

    private Quaternion RestRotation(int i) =>
      new Quaternion(_skeleton!.Rotation[i * 4], _skeleton.Rotation[i * 4 + 1], _skeleton.Rotation[i * 4 + 2], _skeleton.Rotation[i * 4 + 3]);

    // The page's findFollowers: helper bones (named roll or twist) without a track follow the nearest tracked sibling
    // within 3 cm; the ped helper bones by the game's rules (gameHelper) when the clip doesn't turn them (a forearm's
    // twist bone always).
    private void FindFollowers()
    {
      if (_clip == null)
      {
        return;
      }
      var s = _skeleton!;
      var tracked = new HashSet<int>(_clip.Tracks.Where(t => !t.Position).Select(t => t.Bone));
      int n = s.Parent.Length;
      for (int b = 0; b < n; b++)
      {
        string name = s.Names.Length > b ? s.Names[b] ?? "" : "";
        if ((tracked.Contains(b) && !Regex.IsMatch(name, "foretwist", RegexOptions.IgnoreCase)) || !Regex.IsMatch(name, "roll|twist", RegexOptions.IgnoreCase))
        {
          continue;
        }
        int leader = -1;
        double nearest = 0.03;
        var pb = RestPosition(b);
        for (int c = 0; c < n; c++)
        {
          if (c == b || !tracked.Contains(c) || s.Parent[c] != s.Parent[b])
          {
            continue;
          }
          var pc = RestPosition(c);
          double d = Math.Sqrt((double)(pb.X - pc.X) * (pb.X - pc.X) + (double)(pb.Y - pc.Y) * (pb.Y - pc.Y) + (double)(pb.Z - pc.Z) * (pb.Z - pc.Z));
          if (d <= nearest && Math.Sqrt((double)pc.X * pc.X + (double)pc.Y * pc.Y + (double)pc.Z * pc.Z) >= 0.03)
          {
            nearest = d;
            leader = c;
          }
        }
        if (leader < 0 || GameHelper(b) != null)
        {
          continue;
        }
        int twistFrom = -1;
        if (Regex.IsMatch(name, "twist", RegexOptions.IgnoreCase))
        {
          for (int c = 0; c < n; c++)
          {
            if (s.Parent[c] == leader && tracked.Contains(c))
            {
              twistFrom = c;
              break;
            }
          }
        }
        _followers.Add((b, leader, twistFrom));
      }
      for (int b = 0; b < n; b++)
      {
        if (GameHelper(b) is var (kind, a, c) && (kind == "foreTwist" || !tracked.Contains(b)))
        {
          _helpers.Add((b, kind, a, c));
        }
      }
    }

    private int BoneNamed(string name) => Array.FindIndex(_skeleton!.Names, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    // The helper bones of GTA IV's peds as the game turns them (the page's gameHelper).
    private (string Kind, int A, int B)? GameHelper(int b)
    {
      string name = _skeleton!.Names.Length > b ? _skeleton.Names[b] ?? "" : "";
      Match m;
      if ((m = Regex.Match(name, "^([LR])_ArmRoll$", RegexOptions.IgnoreCase)).Success)
      {
        int up = BoneNamed("Char_" + m.Groups[1].Value.ToUpperInvariant() + "_UpperArm");
        return up >= 0 ? ("armRoll", up, -1) : null;
      }
      if ((m = Regex.Match(name, "^([LR])_UpperArmRoll$", RegexOptions.IgnoreCase)).Success)
      {
        int up = BoneNamed("Char_" + m.Groups[1].Value.ToUpperInvariant() + "_UpperArm");
        return up >= 0 ? ("upperArmRoll", up, -1) : null;
      }
      if ((m = Regex.Match(name, "^Char_([LR])_ForeTwist$", RegexOptions.IgnoreCase)).Success)
      {
        string side = m.Groups[1].Value.ToUpperInvariant();
        int fore = BoneNamed("Char_" + side + "_Forearm"), hand = BoneNamed("Char_" + side + "_Hand");
        return fore >= 0 && hand >= 0 ? ("foreTwist", fore, hand) : null;
      }
      if (Regex.IsMatch(name, "^Neck_Roll$", RegexOptions.IgnoreCase))
      {
        int head = BoneNamed("Char_Head");
        return head >= 0 ? ("neckRoll", head, -1) : null;
      }
      if (Regex.IsMatch(name, "^[LR]_Calf_Roll$", RegexOptions.IgnoreCase))
      {
        return ("calfRoll", -1, -1);
      }
      return null;
    }

    private Matrix4x4[] World(float[] positions, float[] rotations)
    {
      int n = _skeleton!.Parent.Length;
      var position = new Vector3[n];
      var rotation = new Quaternion[n];
      for (int i = 0; i < n; i++)
      {
        position[i] = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
        rotation[i] = new Quaternion(rotations[i * 4], rotations[i * 4 + 1], rotations[i * 4 + 2], rotations[i * 4 + 3]);
      }
      return World(position, rotation);
    }

    private Matrix4x4[] World(Vector3[] position, Quaternion[] rotation)
    {
      var parent = _skeleton!.Parent;
      int n = parent.Length;
      var world = new Matrix4x4?[n];
      Matrix4x4 Of(int i)
      {
        if (world[i] is Matrix4x4 known)
        {
          return known;
        }
        var local = Matrix4x4.CreateFromQuaternion(rotation[i]) * Matrix4x4.CreateTranslation(position[i]);
        int p = parent[i];
        var m = p >= 0 && p < n && p != i ? local * Of(p) : local;
        world[i] = m;
        return m;
      }
      var result = new Matrix4x4[n];
      for (int i = 0; i < n; i++)
      {
        result[i] = Of(i);
      }
      return result;
    }

    // The page's moverPath: the mover's position track adds up its steps in the ped's own frame, each turned by the
    // heading the ped has while making it (its turn half a frame before).
    private static float[] MoverPath(ViewerClip clip)
    {
      int frames = clip.Frames;
      var path = new float[frames * 3];
      if (clip.MoverPosition == null)
      {
        return path;
      }
      var p = clip.MoverPosition.Values;
      for (int f = 1; f < frames; f++)
      {
        var q = clip.MoverRotation != null ? SampleRotation(clip.MoverRotation.Values, f - 0.5, frames) : Quaternion.Identity;
        var step = new Vector3(p[f * 3] - p[(f - 1) * 3], p[f * 3 + 1] - p[(f - 1) * 3 + 1], p[f * 3 + 2] - p[(f - 1) * 3 + 2]);
        var turned = Mul(Mul(q, new Quaternion(step, 0)), Quaternion.Conjugate(q));
        for (int k = 0; k < 3; k++)
        {
          path[f * 3 + k] = path[(f - 1) * 3 + k] + (k == 0 ? turned.X : k == 1 ? turned.Y : turned.Z);
        }
      }
      return path;
    }

    // The page's qmul (a then applied after b: a x b in its column convention), as quaternions.
    private static Quaternion Mul(Quaternion a, Quaternion b) => new Quaternion(
      a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
      a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
      a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
      a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    // The page's qnorm (a zero length taken as 1).
    private static Quaternion Normalized(Quaternion q)
    {
      float l = q.Length();
      if (l == 0)
      {
        l = 1;
      }
      return new Quaternion(q.X / l, q.Y / l, q.Z / l, q.W / l);
    }

    // The page's sample: a track's value between frames, positions linearly, turns by normalized linear interpolation.
    private static Vector3 SamplePosition(float[] v, double frame, int frames)
    {
      int f0 = Math.Max(0, Math.Min((int)Math.Floor(frame), frames - 1)), f1 = Math.Min(f0 + 1, frames - 1);
      float t = (float)Math.Clamp(frame - f0, 0, 1);
      if (f1 * 3 + 2 >= v.Length)
      {
        return f0 * 3 + 2 < v.Length ? new Vector3(v[f0 * 3], v[f0 * 3 + 1], v[f0 * 3 + 2]) : Vector3.Zero;
      }
      return new Vector3(v[f0 * 3] * (1 - t) + v[f1 * 3] * t, v[f0 * 3 + 1] * (1 - t) + v[f1 * 3 + 1] * t, v[f0 * 3 + 2] * (1 - t) + v[f1 * 3 + 2] * t);
    }

    private static Quaternion SampleRotation(float[] v, double frame, int frames)
    {
      int f0 = Math.Max(0, Math.Min((int)Math.Floor(frame), frames - 1)), f1 = Math.Min(f0 + 1, frames - 1);
      float t = (float)Math.Clamp(frame - f0, 0, 1);
      if (f1 * 4 + 3 >= v.Length)
      {
        return Quaternion.Identity;
      }
      var a = new Quaternion(v[f0 * 4], v[f0 * 4 + 1], v[f0 * 4 + 2], v[f0 * 4 + 3]);
      var b = new Quaternion(v[f1 * 4], v[f1 * 4 + 1], v[f1 * 4 + 2], v[f1 * 4 + 3]);
      float sign = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W < 0 ? -1 : 1;
      var q = new Quaternion(a.X * (1 - t) + sign * b.X * t, a.Y * (1 - t) + sign * b.Y * t, a.Z * (1 - t) + sign * b.Z * t, a.W * (1 - t) + sign * b.W * t);
      return Normalized(q);
    }
  }
}
