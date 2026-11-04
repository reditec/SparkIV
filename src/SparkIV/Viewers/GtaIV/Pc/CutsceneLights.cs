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
using RageLib.GtaIV.Pc.Animation;
using SparkIV.WinUI.Rendering.D3D12;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// The lights of a GTA IV (PC) cutscene frame for the native renderer (#112 step 5), as the page's sceneLightsNow
  /// (ModelView.html) picks them, but without its cap of 80 (issue #106: the game draws each lamp as a light volume over
  /// its G-buffer): the section's own lights (cutLightsNow: the 8 strongest), the tail lamps of the vehicles whose lamps
  /// the mission's script turns on (vehicleLightsNow), and the location's lamps (its objects' 2dfx lights) that are on at
  /// the hour (gtaiv-pc.js lampOn / outdoorLamp), seen (the camera's interior and the rooms seen through its portals on
  /// screen: seenRooms), whose sphere reaches into the view, faded by their FadeDistance; the shadow lamps nearest to
  /// the camera get a shadow map (LampShadowSlots, the page's choice; drawn from the cast). Not yet: light volumes and
  /// coronas, the !outside fallback of the page (MapLightScale: irrelevant with a time cycle).
  /// </summary>
  public sealed class CutsceneLights
  {
    /// <summary>A cutscene's own light: its clip's tracks per frame (the page's sceneLights; see CutsceneMessages.Section).</summary>
    public sealed class CutLight
    {
      public string Name = "";
      public int Frames = 1;
      public float Fps = 30;
      public float[] Position = Array.Empty<float>();
      public float[]? Color, Strength, Range, Cone, Direction;
    }

    private sealed class Lamp
    {
      public Vector3 At, Dir, Rgb;
      public float Radius, Strength, CosOuter, ConeScale, Fade;
      public uint Flags;
      public bool Shadows, Filler;
      public int Room;
    }

    private readonly List<Lamp> _lamps = new List<Lamp>();
    private readonly IReadOnlyList<LocationRoom> _rooms;
    private List<CutLight> _cut = new List<CutLight>();

    /// <summary>The lamps of a location's objects (none without one).</summary>
    public CutsceneLights(CutsceneLocation? location)
    {
      _rooms = location?.Rooms ?? new List<LocationRoom>();
      if (location == null)
      {
        return;
      }
      // As CutsceneLocation.LightsMessage sends them (all objects) and the page's setMapLights takes them (range > 0).
      foreach (var obj in location.Objects)
      {
        foreach (var light in location.Models[obj.Model].Lights)
        {
          float range = light.Distance > 0 ? light.Distance : light.Range;
          if (!(range > 0))
          {
            continue;
          }
          var at = Vector3.Transform(light.Position, obj.Place);
          var way = Vector3.TransformNormal(light.Direction, obj.Place);
          way = way.LengthSquared() > 1e-8f ? Vector3.Normalize(way) : new Vector3(0, 0, -1);
          float cone = light.Spot ? light.Falloff : 0;
          float hotSpot = light.Spot ? light.HotSpot : 0;
          // The model's angles are from the spot's axis; the game fades from the outer to the inner: scale =
          // 1 / (cos inner - cos outer), the outer kept within 1° and 85° (the page's setMapLights).
          float cosOuter = cone > 0 ? MathF.Cos(Math.Clamp(cone, 1, 85) * MathF.PI / 180) : -1;
          _lamps.Add(new Lamp
          {
            At = at,
            Dir = way,
            Rgb = new Vector3(light.Red, light.Green, light.Blue),
            // As GTAIV.exe hands it to the deferred light (0xC1E200 -> 0xABCCD0): the radius is Distance, the
            // strength Range.
            Radius = light.Distance,
            Strength = light.Range,
            CosOuter = cosOuter,
            ConeScale = cone > 0 ? 1 / MathF.Max(1e-3f, MathF.Cos(MathF.Min(cone - 0.5f, MathF.Max(0, hotSpot)) * MathF.PI / 180) - cosOuter) : 0,
            Flags = light.Flags,
            // Drawn by the lighting's shadow program: its own flag 0x8000 and its object's instance flag 0x100.
            Shadows = obj.LampShadows && (light.Flags & 0x8000) != 0,
            // The 2dfx flag 0x100000 (the game's light flag 0x10): the filler family.
            Filler = (light.Flags & 0x100000) != 0,
            Fade = light.FadeDistance,
            Room = CutsceneLighting.RoomOf(_rooms, at),
          });
        }
      }
    }

    /// <summary>The location's lamps (lighting anything at all).</summary>
    public int LampCount => _lamps.Count;

    /// <summary>The section's own lights from now on (see <see cref="TracksOf"/>).</summary>
    public void SetSection(List<CutLight> lights) => _cut = lights;

    /// <summary>
    /// A section's own lights: per light (its clip in the section's animations, skipped without a decoded place) the
    /// tracks as CutsceneMessages.Section sends them: track 140 (OpenIV's "LightRange") the strength, 141
    /// ("LightIntensity1") the range, 142 ("LightIntensity2") the cone's outer angle from its axis in degrees.
    /// </summary>
    public static List<CutLight> TracksOf(CutSection section, AnimationFile? wad)
    {
      var list = new List<CutLight>();
      if (wad == null)
      {
        return list;
      }
      var clips = wad.Animations.GroupBy(a => a.ShortName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
      foreach (string name in section.Lights)
      {
        if (!clips.TryGetValue(name, out var clip))
        {
          continue;
        }
        AnimationTrack? Track(AnimationTrackKind kind) => clip.Tracks.FirstOrDefault(t => t.Kind == kind && t.IsDecoded);
        int frames = Math.Max(1, clip.FrameCount);
        float[]? Sample(AnimationTrackKind kind, int components)
        {
          if (Track(kind) is not AnimationTrack track)
          {
            return null;
          }
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
        if (Sample(AnimationTrackKind.BonePosition, 3) is not float[] place)
        {
          continue;
        }
        list.Add(new CutLight
        {
          Name = name,
          Frames = frames,
          Fps = CutsceneMessages.FramesPerSecond(clip),
          Position = place,
          Color = Sample(AnimationTrackKind.LightColor, 3),
          Strength = Sample(AnimationTrackKind.LightRange, 1),
          Range = Sample(AnimationTrackKind.LightIntensity1, 1),
          Cone = Sample(AnimationTrackKind.LightIntensity2, 1),
          Direction = Sample(AnimationTrackKind.LightDirection, 3),
        });
      }
      return list;
    }

    /// <summary>
    /// When a lamp is on (gtaiv-pc.js lampOn, GTAIV.exe 0xC1F9A0) by its 2dfx flags and the night factor: 0x20 by day,
    /// 0x40 by night, both always; with 0x1 it fades (by day x (1 - N) up to N 0.999, by night x N from 0.001).
    /// </summary>
    public static float LampOn(uint flags, float night)
    {
      bool day = (flags & 0x20) != 0, dark = (flags & 0x40) != 0;
      if (day && dark)
      {
        return 1;
      }
      if ((flags & 1) != 0)
      {
        return day && night <= 0.999f ? 1 - night : dark && night >= 0.001f ? night : 0;
      }
      return (day && night < 0.25f) || (dark && night >= 0.25f) ? 1 : 0;
    }

    /// <summary>The time cycle's night factor of an hour (TimeCycle.Message's nightFactor).</summary>
    public static float NightFactor(float hour) => hour < 6 ? 1 : hour < 7 ? 7 - hour : hour < 19 ? 0 : hour < 20 ? hour - 19 : 1;

    // A vehicle's tail lamps (GTAIV.exe 0xA42B10, visualSettings.dat car.taillight): one spot half way between them,
    // pointing back along the car, red, strength 4 x 2.5, reaching 4 m, from 45° to 89° (the page's TailLamp).
    private const float TailStrength = 4 * 2.5f, TailRange = 4, TailInner = 45, TailOuter = 89;
    private readonly Dictionary<CutActor, float> _rearBehind = new Dictionary<CutActor, float>();

    /// <summary>
    /// The lights of a frame (<paramref name="timelineFrame"/> of the section's timeline at <paramref name="timelineFps"/>)
    /// seen from <paramref name="eye"/> through <paramref name="viewProjection"/> (Direct3D's: z from 0 at the near
    /// plane), at an hour, with the time cycle's exposure there; the cast posed at the frame (their tail lamps).
    /// <paramref name="cap"/>: at most that many lights in the page's order (cutscene lights, tail lamps, then the
    /// nearest lamps), for comparisons with the page's 80; null for all.
    /// </summary>
    public List<DeferredLight> Now(double timelineFrame, float timelineFps, Vector3 eye, Matrix4x4 viewProjection, float hour,
                                   float exposure, IEnumerable<CutscenePose> cast, int? cap = null)
    {
      var now = new List<DeferredLight>();
      CutLightsNow(now, timelineFrame, timelineFps, exposure);
      TailLampsNow(now, cast, exposure);
      if (_lamps.Count == 0)
      {
        return Capped(now, cap, eye);
      }

      // With the camera in an interior, not the lamps of another interior; inside only those of the rooms seen from
      // the camera's (seenRooms: the outside's never; the ship's galley in the intro, et01_aa's lounge).
      int camRoom = _rooms.Count > 0 ? CutsceneLighting.RoomOf(_rooms, eye) : -1;
      Vector3? camAt = camRoom >= 0 ? _rooms[camRoom].At : null;
      var seen = camRoom >= 0 ? SeenRooms(camRoom, viewProjection) : null;
      var planes = FrustumPlanes(viewProjection);
      float night = NightFactor(hour);
      var near = new List<(DeferredLight Light, float D)>();
      foreach (var m in _lamps)
      {
        bool otherInterior = camAt is Vector3 at0 && m.Room >= 0 && _rooms[m.Room].At != at0;
        if (otherInterior || (seen != null && !seen.Contains(m.Room)))
        {
          continue;
        }
        // outdoorLamp: on at the hour, with a radius and a strength; colour x strength x on x exposure.
        float on = LampOn(m.Flags, night);
        if (on <= 0 || !(m.Radius > 0.001f) || !(m.Strength >= 0.001f))
        {
          continue;
        }
        float range = m.Radius;
        var color = m.Rgb * (m.Strength * on * exposure);
        float d = Vector3.Distance(m.At, eye);
        // Only one whose light reaches into the view (its sphere against the frustum's planes).
        bool inView = true;
        foreach (var p in planes)
        {
          if (p.X * m.At.X + p.Y * m.At.Y + p.Z * m.At.Z + p.W < -range)
          {
            inView = false;
            break;
          }
        }
        if (!inView)
        {
          continue;
        }
        // Fading out from its FadeDistance on over its radius (GTAIV.exe 0xC1DB60).
        if (m.Fade >= 0 && range > 0)
        {
          float fade = Math.Clamp((range - (d - m.Fade)) / range, 0, 1);
          if (fade <= 0)
          {
            continue;
          }
          if (fade < 1)
          {
            color *= fade;
          }
        }
        near.Add((new DeferredLight
        {
          Position = m.At,
          Range = range,
          Direction = m.Dir,
          CosOuter = m.CosOuter,
          ConeScale = m.ConeScale,
          Color = color,
          Model = m.Filler ? DeferredLightModel.Filler : DeferredLightModel.Lamp,
          Mask = DeferredLightMask.All,
          // The lamp's room decides the pass (ps07 in an interior: no AO; ps06/ps08 outside): its own, not the pixel's.
          InRoom = !m.Filler && m.Room >= 0,
          Shadowed = m.Shadows,
        }, d));
      }
      near.Sort((a, b) => a.D.CompareTo(b.D));
      now.AddRange(near.Select(n => n.Light));
      return Capped(now, cap, eye);
    }

    /// <summary>How many lamps' shadow maps a frame draws (the page's LampShadowSlots; the game's atlas holds 16 tiles).</summary>
    public const int LampShadowSlots = 4;

    // At most the cap, then the shadow lamps' maps given out (the page's choice, an estimate: the game's is not known):
    // the lamps with a shadow nearest to the camera by their distance less their radius, as many as there are slots;
    // the others keep the shadow programs' formulas, fully lit.
    private static List<DeferredLight> Capped(List<DeferredLight> list, int? cap, Vector3 eye)
    {
      var now = cap is int c && list.Count > c ? list.GetRange(0, c) : list;
      var chosen = Enumerable.Range(0, now.Count)
        .Where(i => now[i].Shadowed && now[i].Model != DeferredLightModel.Cut)
        .OrderBy(i => Vector3.Distance(now[i].Position, eye) - now[i].Range)
        .Take(Math.Min(LampShadowSlots, MeshRenderer.ShadowSlots))
        .ToList();
      for (int slot = 0; slot < chosen.Count; slot++)
      {
        var l = now[chosen[slot]];
        l.ShadowSlot = slot;
        now[chosen[slot]] = l;
      }
      return now;
    }

    // cutLightsNow: each light's tracks at the frame (whole frames of its clip, no easing between them), its strength
    // over 0.15; colour x strength x exposure; a spot from its outer angle (1° to 89°) to half its track's angle; the 8
    // strongest. A light named env... or shd... (GTAIV.exe 0x9CA7E0: flag 0x100) lights only the location, the others
    // only peds and vehicles (the game's stencil; see the page's shader).
    private void CutLightsNow(List<DeferredLight> now, double timelineFrame, float timelineFps, float exposure)
    {
      var list = new List<(DeferredLight Light, float Strength)>();
      foreach (var l in _cut)
      {
        int f = Math.Min((int)Math.Floor(timelineFrame / timelineFps * l.Fps + 1e-6), l.Frames - 1);
        f = Math.Max(0, f);
        Vector3? V3(float[]? a) => a != null && a.Length >= f * 3 + 3 ? new Vector3(a[f * 3], a[f * 3 + 1], a[f * 3 + 2]) : null;
        float V1(float[]? a, float otherwise) => a != null && a.Length > 0 ? a[Math.Min(f, a.Length - 1)] : otherwise;
        float strength = V1(l.Strength, 1);
        float range = V1(l.Range, 10);
        if (strength <= 0.15f || range <= 0)
        {
          continue;
        }
        var color = (V3(l.Color) ?? Vector3.One) * (strength * exposure);
        var dir = V3(l.Direction);
        bool spot = l.Cone != null && dir != null;
        float cosOuter = spot ? MathF.Cos(Math.Clamp(V1(l.Cone, 89), 1, 89) * MathF.PI / 180) : -1;
        // Its inner angle half the track's angle as it is (0x9CA7E0 hands 0xABCC50 the value x 0.5; GTAIV.4 trace).
        float cosInner = spot ? MathF.Cos(MathF.Max(1, V1(l.Cone, 89)) * 0.5f * MathF.PI / 180) : 1;
        var way = dir is Vector3 w && w.LengthSquared() > 0 ? Vector3.Normalize(w) : new Vector3(0, 0, -1);
        bool env = Regex.IsMatch(l.Name, "^(env|shd)", RegexOptions.IgnoreCase);
        list.Add((new DeferredLight
        {
          Position = V3(l.Position) ?? Vector3.Zero,
          Range = range,
          Direction = way,
          CosOuter = cosOuter,
          ConeScale = 1 / MathF.Max(cosInner - cosOuter, 1e-3f),
          Color = color,
          Model = DeferredLightModel.Cut,
          Mask = env ? DeferredLightMask.BakedOnly : DeferredLightMask.NotBaked,
        }, strength));
      }
      // (Stable, as the page's sort: equal strengths keep their order.)
      now.AddRange(list.Select((x, i) => (x, i)).OrderByDescending(x => x.x.Strength).ThenBy(x => x.i).Take(8).Select(x => x.x.Light));
    }

    // vehicleLightsNow: a vehicle with its lamps on (CutActor.LightsOn) and both bones taillight_l and taillight_r.
    private void TailLampsNow(List<DeferredLight> now, IEnumerable<CutscenePose> cast, float exposure)
    {
      foreach (var pose in cast)
      {
        var actor = pose.Actor;
        if (!actor.LightsOn || pose.LastWorld is not Matrix4x4[] world || world.Length == 0)
        {
          continue;
        }
        int left = pose.Bone("taillight_l"), right = pose.Bone("taillight_r");
        if (left < 0 || right < 0 || left >= world.Length || right >= world.Length)
        {
          continue;
        }
        // Pointing back along the car (its root bone's -y).
        var body = world[0];
        var dir = Vector3.Normalize(-new Vector3(body.M21, body.M22, body.M23));
        // Across and up half way between the lamps, along the car at its rear end: 0xA42B10 takes the y from the
        // collision bound's box (its least y), without one the meshes' rear.
        if (!_rearBehind.TryGetValue(actor, out float behind))
        {
          float rear = actor.Scene?.BodyBoundsMin is float[] min && min.Length >= 2 ? min[1] : float.PositiveInfinity;
          if (actor.Scene?.BodyBoundsMin == null && actor.Scene != null)
          {
            foreach (var mesh in actor.Scene.Meshes)
            {
              for (int i = 1; i < mesh.Positions.Length; i += 3)
              {
                rear = MathF.Min(rear, mesh.Positions[i]);
              }
            }
          }
          behind = float.IsFinite(rear) ? MathF.Max(0, (pose.RestPlace(left).Y + pose.RestPlace(right).Y) / 2 - rear) : 0;
          _rearBehind[actor] = behind;
        }
        var at = (world[left].Translation + world[right].Translation) / 2 + dir * behind;
        float cosOuter = MathF.Cos(TailOuter * MathF.PI / 180), cosInner = MathF.Cos(TailInner * MathF.PI / 180);
        now.Add(new DeferredLight
        {
          Position = at,
          Range = TailRange,
          Direction = dir,
          CosOuter = cosOuter,
          ConeScale = 1 / (cosInner - cosOuter),
          Color = new Vector3(1, 0, 0) * (TailStrength * exposure),
          Model = DeferredLightModel.Lamp,
          Mask = DeferredLightMask.All,
          InRoom = CutsceneLighting.RoomOf(_rooms, at) >= 0,
        });
      }
    }

    // The view's six planes (Direct3D: 0 <= z <= w), each normalised: n.p + w >= 0 inside.
    private static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
      var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
      var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
      var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
      var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
      var planes = new[] { c3 + c0, c3 - c0, c3 + c1, c3 - c1, c2, c3 - c2 };
      for (int i = 0; i < planes.Length; i++)
      {
        float l = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
        planes[i] /= l > 0 ? l : 1;
      }
      return planes;
    }

    // The rooms seen from the camera's room (the page's seenRooms): it and those behind its portals on screen, and on
    // through theirs (GTAIV.exe makes a lamp's light only where its room is seen: Light_Add 0xABCCD0 -> 0xA0FE70).
    private HashSet<int> SeenRooms(int room, Matrix4x4 viewProjection)
    {
      var seen = new HashSet<int> { room };
      var todo = new Stack<int>();
      todo.Push(room);
      while (todo.Count > 0)
      {
        var r = _rooms[todo.Pop()];
        foreach (var q in r.Portals)
        {
          int to = q.To >= 0 && q.To < _rooms.Count ? q.To : -1;
          if (to < 0 || seen.Contains(to) || PortalShare(r, q.Corners, viewProjection) <= 0)
          {
            continue;
          }
          seen.Add(to);
          todo.Push(to);
        }
      }
      return seen;
    }

    // How much of the screen a portal covers (the page's portalShare): its corners (the interior's) into the scene,
    // clipped to the view (|x|, |y| <= w, z >= 0), the area of what is left in normalised coordinates / 2 / 4.
    internal static float PortalShare(LocationRoom r, Vector3[]? corners, Matrix4x4 viewProjection)
    {
      if (corners == null || corners.Length < 4)
      {
        return 0;
      }
      var back = Quaternion.Conjugate(r.Turn);
      var poly = new List<Vector4>(8);
      for (int k = 0; k < 4; k++)
      {
        var p = Vector3.Transform(corners[k], back) + r.At;
        poly.Add(Vector4.Transform(new Vector4(p, 1), viewProjection));
      }
      Func<Vector4, float>[] inside =
      {
        c => c.W - c.X, c => c.W + c.X, c => c.W - c.Y, c => c.W + c.Y, c => c.Z,
      };
      foreach (var test in inside)
      {
        var output = new List<Vector4>(8);
        for (int i = 0; i < poly.Count; i++)
        {
          var a = poly[i];
          var b = poly[(i + 1) % poly.Count];
          float da = test(a), db = test(b);
          if (da >= 0)
          {
            output.Add(a);
          }
          if ((da >= 0) != (db >= 0))
          {
            output.Add(Vector4.Lerp(a, b, da / (da - db)));
          }
        }
        poly = output;
        if (poly.Count < 3)
        {
          return 0;
        }
      }
      float area = 0;
      for (int i = 0; i < poly.Count; i++)
      {
        var a = poly[i];
        var b = poly[(i + 1) % poly.Count];
        area += a.X / a.W * (b.Y / b.W) - b.X / b.W * (a.Y / a.W);
      }
      return MathF.Min(1, MathF.Abs(area) / 2 / 4);
    }
  }
}
