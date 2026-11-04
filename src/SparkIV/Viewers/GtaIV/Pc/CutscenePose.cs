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

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A GTA IV (PC) cutscene actor's pose at any frame, for the native renderer (#112 step 4a): its bones' matrices
  /// from rest to now in the scene (what skinned vertices move by). The same as the page (ModelView.html:
  /// sampledPose, findFollowers, gameHelper, worldMatrices, poseCast): the clip's tracks over the rest pose (positions
  /// linearly, turns by normalized linear interpolation), helper bones that follow a tracked sibling, the ped helper
  /// bones by the game's rules, the clip's model tracks placing a ped in the scene, removed bones (and what hangs on
  /// them) as nothing. Row vectors (System.Numerics): world = local × parent's world.
  /// </summary>
  public sealed class CutscenePose
  {
    private readonly CutsceneMessages.CastMotion _motion;
    private readonly int[] _parent;
    private readonly Vector3[] _restPosition;
    private readonly Quaternion[] _restRotation;
    private readonly string[] _names;
    private readonly Matrix4x4[] _inverseRest;
    private readonly bool[] _removed;
    private readonly List<(int Bone, int Leader, int TwistFrom)> _followers = new List<(int, int, int)>();
    private readonly List<(int Bone, string Kind, int A, int B)> _helpers = new List<(int, string, int, int)>();
    private readonly int _frames;

    public CutscenePose(CutsceneMessages.CastMotion motion)
    {
      _motion = motion;
      var bones = motion.Bones;
      int n = bones.Count == 0 ? 0 : bones.Max(b => b.Index) + 1;
      _parent = Enumerable.Repeat(-1, n).ToArray();
      _restPosition = new Vector3[n];
      _restRotation = Enumerable.Repeat(Quaternion.Identity, n).ToArray();
      _names = Enumerable.Repeat("", n).ToArray();
      foreach (var b in bones)
      {
        _parent[b.Index] = b.Parent;
        _restPosition[b.Index] = new Vector3(b.X, b.Y, b.Z);
        _restRotation[b.Index] = new Quaternion(b.Quaternion[0], b.Quaternion[1], b.Quaternion[2], b.Quaternion[3]);
        _names[b.Index] = b.Name ?? "";
      }
      _inverseRest = World(_restPosition, _restRotation).Select(m => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();
      _frames = motion.Clip != null ? Math.Max(1, motion.Clip.FrameCount) : 1;

      // A removed bone takes the bones below it along (the game's hardtop glass hangs on misc_a, a child of extra_1).
      _removed = new bool[n];
      foreach (int r in motion.Removed.Where(r => r >= 0 && r < n))
      {
        _removed[r] = true;
      }
      for (int i = 0; i < n; i++)
      {
        for (int p = _parent[i]; p >= 0 && p < n; p = _parent[p] == p ? -1 : _parent[p])
        {
          if (_removed[p])
          {
            _removed[i] = true;
            break;
          }
        }
      }
      FindFollowers();
    }

    public int BoneCount => _parent.Length;

    /// <summary>Where the actor is at the frame last posed (<see cref="SkinAt"/>): its model tracks' place, else its root bone's.</summary>
    public Vector3 Place { get; private set; }

    /// <summary>The clip's frames a second (30 in GTA IV), or 30 without a clip.</summary>
    public float FramesPerSecond => _motion.Clip != null ? CutsceneMessages.FramesPerSecond(_motion.Clip) : 30;

    // Helper bones the game turns itself (twist and roll bones): see the page's findFollowers.
    private void FindFollowers()
    {
      if (_motion.Clip == null)
      {
        return;
      }
      var tracked = new HashSet<int>(_motion.Rotations.Keys);
      int n = _parent.Length;
      for (int b = 0; b < n; b++)
      {
        string name = _names[b];
        if ((tracked.Contains(b) && !Regex.IsMatch(name, "foretwist", RegexOptions.IgnoreCase)) || !Regex.IsMatch(name, "roll|twist", RegexOptions.IgnoreCase))
        {
          continue;
        }
        int leader = -1;
        float nearest = 0.03f;
        for (int s = 0; s < n; s++)
        {
          if (s == b || !tracked.Contains(s) || _parent[s] != _parent[b])
          {
            continue;
          }
          float d = Vector3.Distance(_restPosition[b], _restPosition[s]);
          if (d <= nearest && _restPosition[s].Length() >= 0.03f)
          {
            nearest = d;
            leader = s;
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
            if (_parent[c] == leader && tracked.Contains(c))
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

    private int BoneNamed(string name) => Array.FindIndex(_names, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    // The helper bones of GTA IV's peds as the game turns them (see the page's gameHelper for how they were read).
    private (string Kind, int A, int B)? GameHelper(int b)
    {
      string name = _names[b];
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

    /// <summary>
    /// The skinning matrices at a frame of the timeline (its rate <paramref name="timelineFps"/>): per bone rest to
    /// now in the scene (inverse rest × world × the actor's place), nothing for removed bones.
    /// </summary>
    public Matrix4x4[] SkinAt(double timelineFrame, float timelineFps)
    {
      double? frame = _motion.Clip != null ? Math.Min(timelineFrame / timelineFps * FramesPerSecond, _frames - 1) : null;
      var position = (Vector3[])_restPosition.Clone();
      var rotation = (Quaternion[])_restRotation.Clone();
      if (frame is double f)
      {
        foreach (var (bone, values) in _motion.Positions)
        {
          if (bone < position.Length)
          {
            position[bone] = SamplePosition(values, f);
          }
        }
        foreach (var (bone, values) in _motion.Rotations)
        {
          if (bone < rotation.Length)
          {
            rotation[bone] = SampleRotation(values, f);
          }
        }
        // Followers: the leader's turn away from its rest, applied to the follower's rest (twist bones: and half
        // the hand's turn about the arm's long axis, x).
        foreach (var (bone, leader, twistFrom) in _followers)
        {
          var q = Mul(Mul(rotation[leader], Quaternion.Conjugate(_restRotation[leader])), _restRotation[bone]);
          if (twistFrom >= 0)
          {
            var d = Mul(rotation[twistFrom], Quaternion.Conjugate(_restRotation[twistFrom]));
            var twist = Quaternion.Normalize(new Quaternion(d.X, 0, 0, d.W));
            if (twist.W < 0)
            {
              twist = Quaternion.Negate(twist);
            }
            q = Mul(q, Quaternion.Normalize(new Quaternion(twist.X, 0, 0, twist.W + 1)));
          }
          rotation[bone] = Quaternion.Normalize(q);
        }
        static float EulerX(Quaternion q) => MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        static Quaternion Rx(float a) => new Quaternion(MathF.Sin(a / 2), 0, 0, MathF.Cos(a / 2));
        foreach (var (bone, kind, a, b) in _helpers)
        {
          Quaternion q;
          switch (kind)
          {
            case "armRoll":
              q = Mul(rotation[a], Rx(-0.25f * EulerX(rotation[a])));
              break;
            case "upperArmRoll":
              q = Rx(MathF.PI + 0.15f * EulerX(rotation[a]));
              break;
            case "foreTwist":
              float diff = EulerX(rotation[b]) - EulerX(_restRotation[b]);
              // (Math.round of the page: halves up.)
              diff -= 2 * MathF.PI * MathF.Floor(diff / (2 * MathF.PI) + 0.5f);
              q = Mul(rotation[a], Rx(0.5f * diff));
              break;
            case "neckRoll":
              q = rotation[a];
              break;
            default:
              q = Quaternion.Identity;
              break;
          }
          rotation[bone] = Quaternion.Normalize(q);
        }
      }

      var world = World(position, rotation);
      if (frame is double rf && (_motion.RootPosition != null || _motion.RootRotation != null))
      {
        var at = _motion.RootPosition != null ? SamplePosition(_motion.RootPosition, rf) : Vector3.Zero;
        var turn = _motion.RootRotation != null ? SampleRotation(_motion.RootRotation, rf) : Quaternion.Identity;
        var place = Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(at);
        for (int i = 0; i < world.Length; i++)
        {
          world[i] *= place;
        }
        Place = at;
      }
      else
      {
        // A prop without model tracks: where its root bone puts it (the page's poseCast).
        Place = world.Length > 0 ? world[0].Translation : Vector3.Zero;
      }
      var skin = new Matrix4x4[world.Length];
      for (int i = 0; i < world.Length; i++)
      {
        skin[i] = _removed[i] ? default : _inverseRest[i] * world[i];
      }
      return skin;
    }

    // The page's qmul (a then applied after b: a × b in its column convention), as quaternions.
    private static Quaternion Mul(Quaternion a, Quaternion b) => new Quaternion(
      a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
      a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
      a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
      a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private Matrix4x4[] World(Vector3[] position, Quaternion[] rotation)
    {
      int n = _parent.Length;
      var world = new Matrix4x4?[n];
      Matrix4x4 Of(int i)
      {
        if (world[i] is Matrix4x4 known)
        {
          return known;
        }
        var local = Matrix4x4.CreateFromQuaternion(rotation[i]) * Matrix4x4.CreateTranslation(position[i]);
        int p = _parent[i];
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

    // A track's value between frames, as the page's sample: positions linearly, turns by normalized linear interpolation.
    private Vector3 SamplePosition(float[] v, double frame)
    {
      int frames = Math.Max(1, Math.Min(_frames, v.Length / 3));
      int f0 = Math.Max(0, Math.Min((int)Math.Floor(frame), frames - 1)), f1 = Math.Min(f0 + 1, frames - 1);
      float t = (float)Math.Clamp(frame - f0, 0, 1);
      return Vector3.Lerp(new Vector3(v[f0 * 3], v[f0 * 3 + 1], v[f0 * 3 + 2]), new Vector3(v[f1 * 3], v[f1 * 3 + 1], v[f1 * 3 + 2]), t);
    }

    private Quaternion SampleRotation(float[] v, double frame)
    {
      int frames = Math.Max(1, Math.Min(_frames, v.Length / 4));
      int f0 = Math.Max(0, Math.Min((int)Math.Floor(frame), frames - 1)), f1 = Math.Min(f0 + 1, frames - 1);
      float t = (float)Math.Clamp(frame - f0, 0, 1);
      var a = new Quaternion(v[f0 * 4], v[f0 * 4 + 1], v[f0 * 4 + 2], v[f0 * 4 + 3]);
      var b = new Quaternion(v[f1 * 4], v[f1 * 4 + 1], v[f1 * 4 + 2], v[f1 * 4 + 3]);
      float sign = Quaternion.Dot(a, b) < 0 ? -1 : 1;
      var q = new Quaternion(a.X * (1 - t) + sign * b.X * t, a.Y * (1 - t) + sign * b.Y * t, a.Z * (1 - t) + sign * b.Z * t, a.W * (1 - t) + sign * b.W * t);
      float length = q.Length();
      if (length == 0)
      {
        length = 1;
      }
      return new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }
  }
}
