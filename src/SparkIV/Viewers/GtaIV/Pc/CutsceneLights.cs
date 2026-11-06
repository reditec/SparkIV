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
  /// the camera get a shadow map (LampShadowSlots, the page's choice; drawn from the cast). The lamps' coronas
  /// (<see cref="CoronasNow"/>) and visible light volumes (<see cref="VolumesNow"/>) as the page's coronasNow and
  /// lightVolumesNow. Not: the !outside fallback of the page (MapLightScale: irrelevant with a time cycle).
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
      public Vector3 At, Dir, Rgb, Across;
      public float Radius, Strength, CosOuter, ConeScale, Fade, ConeDegrees;
      // Its light volume (2dfx flag 0x80000): strength (its own x Range) and size (x the radius); 0 without.
      public float VolumeStrength, VolumeSize;
      public uint Flags;
      public bool Shadows, Filler;
      public int Room;
      // The room of the object it is on, as its interior's .ide lists it (-1: the outside or the walls, limbo).
      public int ObjectRoom;
    }

    private readonly List<Lamp> _lamps = new List<Lamp>();
    // The lamps' coronas (place, colour, half a side, intensity, 2dfx flags, room).
    private readonly List<(Vector3 At, Vector3 Rgb, float Size, float Intensity, uint Flags, int Room)> _coronas = new();
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
          // Its corona (the page's setMapLights: mapCoronas, ModelView.html ~2462-2463), also of a light that lights
          // nothing: CoronaSize > 0 and Intensity > 0, half a side CoronaSize x 2.5.
          if (light.CoronaSize > 0 && light.Intensity > 0)
          {
            var cAt = Vector3.Transform(light.Position, obj.Place);
            _coronas.Add((cAt, new Vector3(light.Red, light.Green, light.Blue), light.CoronaSize * 2.5f, light.Intensity, light.Flags,
                           RoomOfObject(obj, cAt)));
          }
          float range = light.Distance > 0 ? light.Distance : light.Range;
          if (!(range > 0))
          {
            continue;
          }
          // GTA IV (PC): a lamp with the 2dfx flag 0x10000 lights nothing in the cutscenes. GTAIV.exe gives it its own
          // path (Light_FromEffect 0xC1E200: colour and strength x a runtime table entry, 0x15E88F0 + DAT_01174790 x
          // 16, via 0xC1DAA0), and the game draws none of them: GTAIV.3.trace, intro s2 f682.5 (calls
          // 27894936-27909727), 24 on screen within 10 m of the camera, and f863 (34228429-34242275), all 25 of the
          // ship's; the only such lamps in the cutscenes compared (intro's ship; none in bc_1, et01_aa, intro s4).
          if ((light.Flags & 0x10000) != 0 && !KeepFlag10000Lamps)
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
            Room = RoomOfObject(obj, at),
            ObjectRoom = RoomOfObject(obj, at),
            ConeDegrees = cone,
            Across = Vector3.TransformNormal(light.Tangent, obj.Place),
            // As CutsceneLocation.LightsMessage's volume (GTAIV.exe 0xAC2270): flag 0x80000, strength its own x Range.
            VolumeStrength = (light.Flags & 0x80000) != 0 && light.VolumeIntensity != 0 && light.VolumeSize != 0 ? light.VolumeIntensity * light.Range : 0,
            VolumeSize = (light.Flags & 0x80000) != 0 && light.VolumeIntensity != 0 && light.VolumeSize != 0 ? light.VolumeSize : 0,
          });
        }
      }
    }

    // A lamp's room (#113): its object's, as the interior's .ide lists it; a world object's none (-1), also where it
    // stands in a room's box (the intro ship's 0xac060 lamps on the bridge's outer wall, which the game draws from
    // outside as the outside's: GTAIV.3.trace s2 f71.25, f682.5, f863); an object of the interior's limbo by the box
    // its lamp is in, as before.
    private int RoomOfObject(LocationObject obj, Vector3 at) => obj.Room >= 0 ? obj.Room : obj.Limbo ? CutsceneLighting.RoomOf(_rooms, at) : -1;

    /// <summary>
    /// With the camera outside, every room's lamps (as before 2026-11-06, the page's) instead of only those of the rooms
    /// seen through a near portal to the outside on screen (SeenRooms from -1, see OutsidePortalReach). For comparisons.
    /// </summary>
    public static bool AllRoomsFromOutside { get; set; }

    /// <summary>
    /// GTA IV (PC), #113: from outside the game draws a room's lamps only when a portal of it to the outside is on screen
    /// and its centre within this many metres of the camera - fitted to GTAIV.3.trace, intro s2 (camera outside the
    /// ship): f682.5 the bridge's 45 lamps drawn (its portals 7.5-13.7 m away, the big window 48 % of the screen), f863
    /// none of any room (the hold's hatch 16.8 m away, 2.6 % of the screen; the bridge's 62 m), f71.25 and f352.75 none
    /// (all portals over 60 m away, or off screen). The game's own test isn't read (it lies between 13.7 and 16.8 m, or
    /// is a share of the screen between 2.6 % and 48 %, or the camera's distance from the interior).
    /// </summary>
    public const float OutsidePortalReach = 15f;

    /// <summary>The lamps with the 2dfx flag 0x10000 kept (as before 2026-11-05, the page's). For comparisons.</summary>
    public static bool KeepFlag10000Lamps { get; set; }

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
    /// nearest lamps), for comparisons with the page's 80; null for all. <paramref name="farClip"/>: the game camera's
    /// far clip, the shot's draw distance (CutsceneSunShadow.DrawDistance; infinity without one or off the cutscene's
    /// camera): GTAIV.exe's Light_Add (0xABCCD0) makes only a light whose sphere lies wholly before it (see
    /// <see cref="BeforeFarClip"/>).
    /// </summary>
    public List<DeferredLight> Now(double timelineFrame, float timelineFps, Vector3 eye, Matrix4x4 viewProjection, float hour,
                                   float exposure, IEnumerable<CutscenePose> cast, int? cap = null, float farClip = float.PositiveInfinity)
    {
      var now = new List<DeferredLight>();
      CutLightsNow(now, timelineFrame, timelineFps, exposure, eye, FrustumPlanes(viewProjection), farClip);
      TailLampsNow(now, cast, exposure);
      if (_lamps.Count == 0)
      {
        return Capped(now, cap, eye);
      }

      // With the camera in an interior, not the lamps of another interior; inside only those of the rooms seen from
      // the camera's (seenRooms: the outside's never; the ship's galley in the intro, et01_aa's lounge).
      int camRoom = _rooms.Count > 0 ? CutsceneLighting.RoomOf(_rooms, eye) : -1;
      Vector3? camAt = camRoom >= 0 ? _rooms[camRoom].At : null;
      var seen = camRoom >= 0 || (_rooms.Count > 0 && !AllRoomsFromOutside) ? SeenRooms(camRoom, viewProjection, eye) : null;
      var planes = FrustumPlanes(viewProjection);
      float night = NightFactor(hour);
      var near = new List<(DeferredLight Light, float D)>();
      bool dump = Environment.GetEnvironmentVariable("D3D12LAMPDUMP") == "1";
      foreach (var m in _lamps)
      {
        bool otherInterior = camAt is Vector3 at0 && m.Room >= 0 && _rooms[m.Room].At != at0;
        if (dump)
        {
          Console.WriteLine($"D3D12 lamp at {m.At} radius {m.Radius:0.00} strength {m.Strength:0.00} rgb {m.Rgb} flags 0x{m.Flags:x} objroom {m.ObjectRoom} room {m.Room} other {otherInterior} seen {seen?.Contains(m.Room)} on {LampOn(m.Flags, NightFactor(hour))} fade {m.Fade:0.0} d {Vector3.Distance(m.At, eye):0.00} beforeFar {BeforeFarClip(m.At, m.Radius, planes, farClip)}");
        }
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
        // Nor one reaching past the far clip (Light_Add makes the lamps too: see InViewBeforeFar).
        if (!inView || (!KeepCulledLights && !BeforeFarClip(m.At, range, planes, farClip)))
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

    /// <summary>
    /// The lamps' coronas to draw now (the page's coronasNow, ModelView.html ~2599-2620; gtaiv-pc.js gameCoronas): those
    /// on at the hour (LampOn) and not of another interior than the camera's nor of a room not seen from it; a lamp of an
    /// interior moved 1 m towards the camera, as strong as its intensity, any other its size, x the time cycle's
    /// SprBght (<paramref name="spriteBrightness"/>). Colours without the exposure.
    /// </summary>
    public List<Corona> CoronasNow(Vector3 eye, Matrix4x4 viewProjection, float hour, float spriteBrightness)
    {
      var list = new List<Corona>();
      if (_coronas.Count == 0)
      {
        return list;
      }
      int camRoom = _rooms.Count > 0 ? CutsceneLighting.RoomOf(_rooms, eye) : -1;
      Vector3? camAt = camRoom >= 0 ? _rooms[camRoom].At : null;
      var seen = camRoom >= 0 || (_rooms.Count > 0 && !AllRoomsFromOutside) ? SeenRooms(camRoom, viewProjection, eye) : null;
      float night = NightFactor(hour);
      foreach (var m in _coronas)
      {
        float on = LampOn(m.Flags, night);
        bool otherInterior = (camAt is Vector3 at0 && m.Room >= 0 && _rooms[m.Room].At != at0) || (seen != null && !seen.Contains(m.Room));
        if (on <= 0 || otherInterior)
        {
          continue;
        }
        bool inside = m.Room >= 0;
        var away = eye - m.At;
        float d = away.Length();
        if (d < 1e-3f)
        {
          continue;
        }
        float pull = (inside ? 1 : m.Size) / d;
        list.Add(new Corona { At = m.At + away * pull, Color = m.Rgb * (on * m.Intensity * (inside ? 1 : spriteBrightness)), Size = m.Size });
      }
      return list;
    }

    /// <summary>
    /// The lamps' visible light volumes to draw now (the page's lightVolumesNow, ModelView.html ~2576-2597): those with a
    /// volume, not of another interior nor of a room not seen, on at the hour with a radius and strength (outdoorLamp),
    /// within the volume's reach (radius x size) + 35 m (LampReach) of the camera. Without the filler flag 0x100000 by
    /// the lookup texture (none drawn without it: <paramref name="hasLookup"/>). Colours x 0.25, without the exposure.
    /// </summary>
    public List<LightVolume> VolumesNow(Vector3 eye, Matrix4x4 viewProjection, float hour, bool hasLookup)
    {
      var list = new List<LightVolume>();
      if (!_lamps.Any(l => l.VolumeStrength > 0))
      {
        return list;
      }
      int camRoom = _rooms.Count > 0 ? CutsceneLighting.RoomOf(_rooms, eye) : -1;
      Vector3? camAt = camRoom >= 0 ? _rooms[camRoom].At : null;
      var seen = camRoom >= 0 || (_rooms.Count > 0 && !AllRoomsFromOutside) ? SeenRooms(camRoom, viewProjection, eye) : null;
      float night = NightFactor(hour);
      foreach (var m in _lamps)
      {
        bool lookup = (m.Flags & 0x100000) == 0;
        if (!(m.VolumeStrength > 0 && m.VolumeSize > 0) || (lookup && !hasLookup) || !(m.Radius > 0))
        {
          continue;
        }
        bool otherInterior = (camAt is Vector3 at0 && m.Room >= 0 && _rooms[m.Room].At != at0) || (seen != null && !seen.Contains(m.Room));
        if (otherInterior || LampOn(m.Flags, night) <= 0 || !(m.Radius > 0.001f) || !(m.Strength >= 0.001f))
        {
          continue;
        }
        float reach = m.Radius * m.VolumeSize;
        if (Vector3.Distance(m.At, eye) > reach + 35)
        {
          continue;
        }
        list.Add(new LightVolume
        {
          At = m.At, Direction = m.Dir, Across = m.Across, Color = m.Rgb * (m.VolumeStrength * 0.25f),
          Radius = reach, Cone = m.ConeDegrees > 0 ? m.ConeDegrees * MathF.PI / 180 : 0, Lookup = lookup,
        });
      }
      return list;
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

    /// <summary>The section's own lights out of view and every light reaching past the far clip kept (as before
    /// 2026-11-05, the page's). For comparisons.</summary>
    public static bool KeepCulledLights { get; set; }

    /// <summary>The section's own lights as before 2026-11-06 (#116): env* only on the location, the others on peds and
    /// vehicles. For comparisons.</summary>
    public static bool CutLightMasksBefore116 { get; set; }

    /// <summary>
    /// GTA IV (PC): whether GTAIV.exe's Light_Add (0xABCCD0) makes a light at <paramref name="at"/> reaching
    /// <paramref name="range"/>: its sphere reaches into the view (Light_SphereVisible 0x431E40, against the view's
    /// planes: <paramref name="planes"/>) and lies wholly before the far plane of the viewport (+0x340: n.p + w &gt;
    /// range), the camera's far clip <paramref name="farClip"/> metres ahead (the shot's draw distance, 0xD53280).
    /// GTAIV.3.trace (intro): s1 f399.5 (calls 3675143-3681352, far 15 m) skips filnko_0 (r 27.5); s3 f569.75
    /// (52556107-52564305, far 15 m) draws 3 of 8 (skips hall_spt_0, fil_wlk_0, rim_0 past the far plane,
    /// hall_spt_2_0 and fil_hall_0 out of view); s2 f682.5 (27894936-27909727, far 1000 m) and f863 (34228429-34242275)
    /// skip only those out of view. Also GTAIV.trace bc_1 s1 f144 and GTAIV.2.trace et01_aa s3 f49.75.
    /// </summary>
    public static bool InViewBeforeFar(Vector3 at, float range, Vector4[] planes, float farClip)
    {
      foreach (var p in planes)
      {
        if (p.X * at.X + p.Y * at.Y + p.Z * at.Z + p.W < -range)
        {
          return false;
        }
      }
      return BeforeFarClip(at, range, planes, farClip);
    }

    // Whether a sphere lies wholly before the far clip, farClip metres ahead of the camera along the view (the near
    // plane's normal, planes[4]; its w the camera's place along it, the near clip aside).
    private static bool BeforeFarClip(Vector3 at, float range, Vector4[] planes, float farClip)
    {
      if (!float.IsFinite(farClip) || farClip <= 0)
      {
        return true;
      }
      var p = planes[4];
      return farClip - (p.X * at.X + p.Y * at.Y + p.Z * at.Z + p.W) > range;
    }

    // cutLightsNow: each light's tracks at the frame (whole frames of its clip, no easing between them), its strength
    // over 0.15; colour x strength x exposure; a spot from its outer angle (1° to 89°) to half its track's angle; only
    // those the game makes (InViewBeforeFar); the 8 strongest of them. A light named env... or shd... (GTAIV.exe
    // 0x9CA7E0: flag 0x100) lights only the location, the others only peds and vehicles (the game's stencil; see the
    // page's shader).
    private void CutLightsNow(List<DeferredLight> now, double timelineFrame, float timelineFps, float exposure, Vector3 eye, Vector4[] planes, float farClip)
    {
      var list = new List<(DeferredLight Light, float Strength)>();
      bool dump = Environment.GetEnvironmentVariable("D3D12LAMPDUMP") == "1";
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
        // As GTAIV.exe's Light_Add (0xABCCD0) takes it (0x9CA7E0 hands each light to it): only a light whose sphere
        // reaches into the view and lies wholly before the camera's far clip (InViewBeforeFar).
        var at = V3(l.Position) ?? Vector3.Zero;
        bool kept = KeepCulledLights || InViewBeforeFar(at, range, planes, farClip);
        if (dump)
        {
          Console.WriteLine($"D3D12 cutlight {l.Name} f {f} strength {strength:0.000} range {range:0.00} d {Vector3.Distance(at, eye):0.00} far {farClip:0.0} kept {kept}");
        }
        if (!kept)
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
        // How the game draws it (#116): 0x9CA7E0 gives every cut light flag 0x80, an env... one 0x100 as well, a shd...
        // one also 4 (a shadow). Without 0x100 Light_Add's light goes to deferred_lighting's ps09, which lights only
        // where the stencil mod 8 is 1 (characters; a vehicle's is 2): NotBaked, peds only. An env... light the game
        // draws with its lamps' program (GTAIV.3.trace intro s4 f420.5: env_car_0 and env_title_0 by PS 0x2d592840, as
        // the map's lamps, no stencil test, lighting the car on the crane): the lamp's formula on everything. A shd...
        // light goes to the shadowed lamps' ps14 with its shadow map, which isn't made for cut lights here: kept on the
        // location only (the cook in s3 f569.75 gets nothing from shd_env_kit in the game).
        bool envLamp = !CutLightMasksBefore116 && Regex.IsMatch(l.Name, "^env", RegexOptions.IgnoreCase);
        list.Add((new DeferredLight
        {
          Position = at,
          Range = range,
          Direction = way,
          CosOuter = cosOuter,
          ConeScale = 1 / MathF.Max(cosInner - cosOuter, 1e-3f),
          Color = color,
          Model = envLamp ? DeferredLightModel.CutLamp : DeferredLightModel.Cut,
          Mask = envLamp ? DeferredLightMask.All : env ? DeferredLightMask.BakedOnly
               : CutLightMasksBefore116 ? DeferredLightMask.NotBakedWithVehicles : DeferredLightMask.NotBaked,
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
    private HashSet<int> SeenRooms(int room, Matrix4x4 viewProjection, Vector3 eye)
    {
      var seen = new HashSet<int> { room };
      var todo = new Stack<int>();
      todo.Push(room);
      // From outside (room -1): the outside itself, and the rooms with a portal to the outside on screen and within
      // OutsidePortalReach of the camera, on from there as from a room (GTAIV.3.trace, intro s2 f682.5, calls
      // 27906440-27907845: the game draws the lamps of the ship's bridge, whose windows are on screen 7.5 m away, and
      // none of the holds' and cabins' below deck; f863: none, the hold's hatch on screen 16.8 m away).
      if (room < 0)
      {
        bool dump = Environment.GetEnvironmentVariable("D3D12LAMPDUMP") == "1";
        bool Near(LocationRoom r, (Vector3 Centre, float Radius, int To, Vector3[] Corners, bool Link) q)
        {
          float d = Vector3.Distance(Vector3.Transform(q.Centre, Quaternion.Conjugate(r.Turn)) + r.At, eye);
          float share = PortalShare(r, q.Corners, viewProjection);
          // (D3D12LAMPDUMP=1: each portal to the outside - its distance from the eye, its share of the screen.)
          if (dump)
          {
            Console.WriteLine($"D3D12 outside portal: room {r.Name} radius {q.Radius:0.00} distance {d:0.0} share {share:0.0000}");
          }
          return share > 0 && d <= OutsidePortalReach;
        }
        todo.Clear();
        for (int i = 0; i < _rooms.Count; i++)
        {
          if (!seen.Contains(i) && _rooms[i].Portals.Where(q => q.To < 0 || q.To >= _rooms.Count).Select(q => Near(_rooms[i], q)).ToList().Any(n => n))
          {
            seen.Add(i);
            todo.Push(i);
          }
        }
      }
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
