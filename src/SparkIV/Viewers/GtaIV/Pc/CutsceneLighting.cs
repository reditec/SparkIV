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
using SparkIV.WinUI.Rendering.D3D12;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// A GTA IV (PC) cutscene's light for the native renderer (#112 step 4c), as CutsceneView.PostSky and the page give it:
  /// the time cycle of the weather and hour under the map's modifier boxes where the camera is (TimeCycle.WithBoxes), and
  /// in a room of the location's interiors that room's modifier (TimeCycle.With). The room the camera is in by the
  /// page's roomOf; an actor's share of the light from around (AmbientDownMult) by the page's actorAmbientAt. Given the
  /// view, the outside's eased towards the room's by how much of the screen the room's portals cover (the page's
  /// roomStrength / mixSky, <see cref="RoomStrength"/>; checked: intro s3 f569.75, strength 0.94, the same tone and
  /// light as the page's). The fog of the time cycle goes with the frame's light (FrameLight.Fog).
  /// </summary>
  public sealed class CutsceneLighting
  {
    private readonly TimeCycle _outside;
    private readonly IReadOnlyDictionary<uint, float[]>? _modifiers;
    private readonly CutsceneLocation? _location;
    private readonly float _hour;
    private readonly Vector3 _origin;
    private string? _boxesKey;
    private TimeCycle _boxed;
    private TimeCycle?[] _rooms = Array.Empty<TimeCycle?>();

    private CutsceneLighting(TimeCycle outside, IReadOnlyDictionary<uint, float[]>? modifiers, CutsceneLocation? location, float hour, Vector3 origin)
    {
      _outside = outside;
      _boxed = outside;
      _modifiers = modifiers;
      _location = location;
      _hour = hour;
      _origin = origin;
    }

    /// <summary>The light of a weather and hour around a location (its origin: the section's [OFFSET]); null without the time cycle.</summary>
    public static CutsceneLighting? Load(string gameFolder, string weather, float hour, CutsceneLocation? location, Vector3 origin)
    {
      if (TimeCycle.Load(gameFolder, weather, hour) is not TimeCycle cycle)
      {
        return null;
      }
      var lighting = new CutsceneLighting(cycle, TimeCycle.LoadModifiers(gameFolder), location, hour, origin);
      lighting.MakeRooms();
      return lighting;
    }

    public float Hour => _hour;

    // The rooms' time cycles from the outside's under the boxes (as PostSky: made anew when the boxes' strengths change).
    private void MakeRooms()
    {
      var rooms = _location?.Rooms ?? new List<LocationRoom>();
      _rooms = rooms.Select(r => _modifiers != null && _modifiers.TryGetValue(r.Modifier, out var modifier) ? _boxed.With(modifier) : null).ToArray();
      // (Their sun and light from around apart, for the easing: the modifier blended in whole, as the page's parts.)
      _roomParts = rooms.Select(r => _modifiers != null && _modifiers.TryGetValue(r.Modifier, out var modifier) ? _boxed.Blended(modifier, 1) : null).ToArray();
      _mixed = null;
    }

    private TimeCycle?[] _roomParts = Array.Empty<TimeCycle?>();
    private (int Room, float Strength, TimeCycle Cycle)? _mixed;

    private void UnderBoxes(Vector3 eyeScene)
    {
      if (_location == null || _modifiers == null)
      {
        return;
      }
      var world = eyeScene + _origin;
      string key = string.Join(",", TimeCycle.StrengthsAt(_location.ModifierBoxes, world, _hour).OrderBy(b => b.Key).Select(b => b.Key + ":" + (int)(b.Value * 50)));
      if (key == _boxesKey)
      {
        return;
      }
      _boxesKey = key;
      _boxed = _outside.WithBoxes(_location.ModifierBoxes, _modifiers, world, _hour);
      MakeRooms();
    }

    /// <summary>
    /// The light and tone of a frame seen from <paramref name="eyeScene"/> (in the scene's places) through
    /// <paramref name="viewProjection"/> (Direct3D's; null: the camera's room taken whole).
    /// </summary>
    public (FrameLight Light, ToneParams Tone) At(Vector3 eyeScene, Matrix4x4? viewProjection = null)
    {
      var c = CycleAt(eyeScene, viewProjection);
      return (LightOf(c), ToneOf(c));
    }

    /// <summary>
    /// The time cycle a frame seen from <paramref name="eyeScene"/> through <paramref name="viewProjection"/> takes
    /// (for the sky: CutsceneSky): in a room with a modifier the outside's eased towards the room's by the room's
    /// strength (<see cref="RoomStrength"/>, rounded to hundredths as the page's skyAtCamera); without a view the
    /// room's whole.
    /// </summary>
    public TimeCycle CycleAt(Vector3 eyeScene, Matrix4x4? viewProjection = null)
    {
      UnderBoxes(eyeScene);
      int room = RoomOf(eyeScene);
      if (room < 0 || room >= _rooms.Length || _rooms[room] is not TimeCycle inRoom)
      {
        return _boxed;
      }
      if (viewProjection is not Matrix4x4 vp)
      {
        return inRoom;
      }
      float strength = MathF.Round(RoomStrength(room, vp) * 100) / 100;
      if (strength >= 1)
      {
        return inRoom;
      }
      if (_mixed is not { } m || m.Room != room || m.Strength != strength)
      {
        _mixed = m = (room, strength, _boxed.Mixed(inRoom, _roomParts[room] ?? inRoom, strength));
      }
      return m.Cycle;
    }

    /// <summary>
    /// How much of a room's modifier the game takes seen through a view (the page's roomStrength; GTAIV.exe 0xAEE7E0
    /// with 0xA0E3C0 and 0xA0E490): a list of the room itself (strength 1) and of each of its portals on screen by its
    /// share of the screen (CutsceneLights.PortalShare), one through to another room with that room's blend, then that
    /// room's portals once more (their share x the first's, not the way back); a portal outside, or to a room without a
    /// modifier, as the outside. In the list's order the rooms ease R = s x blend + (1 - s) x R, the outside O = s x
    /// blend + (1 - s) x O; the room's strength (1 - O) x R. A portal linking to another interior (flag 0x2) leads to
    /// the room with a linking portal in the same place (the page's linkedRoom); none there: left out.
    /// </summary>
    public float RoomStrength(int room, Matrix4x4 viewProjection)
    {
      var rooms = _location?.Rooms;
      if (rooms == null || room < 0 || room >= rooms.Count)
      {
        return 1;
      }
      bool NoModifier(int i) => i >= _rooms.Length || _rooms[i] == null;
      var entries = new List<(bool Outside, float Blend, float S)> { (NoModifier(room), rooms[room].Blend, 1) };
      void Walk(LocationRoom r, float parent, Vector3? from, bool deep)
      {
        foreach (var q in r.Portals)
        {
          if (q.Corners == null || q.Corners.Length < 4)
          {
            continue;
          }
          var centre = PortalCentre(r, q.Corners);
          if (from is Vector3 f && Vector3.Distance(centre, f) < 0.01f)
          {
            continue;
          }
          int to = q.Link ? LinkedRoom(rooms, r, centre) : q.To;
          if (q.Link && to < 0)
          {
            continue;
          }
          float s = CutsceneLights.PortalShare(r, q.Corners, viewProjection) * parent;
          if (s <= 0.001f)
          {
            continue;
          }
          bool toRoom = to >= 0 && to < rooms.Count;
          entries.Add(toRoom ? (NoModifier(to), rooms[to].Blend, s) : (true, 1, s));
          if (!deep && toRoom)
          {
            Walk(rooms[to], s, centre, true);
          }
        }
      }
      Walk(rooms[room], 1, null, false);
      float outside = 0, inside = 0;
      foreach (var (isOutside, blend, s) in entries)
      {
        if (isOutside)
        {
          outside = outside * (1 - s) + blend * s;
        }
        else
        {
          inside = s * blend + (1 - s) * inside;
        }
      }
      return Math.Clamp((1 - outside) * inside, 0, 1);
    }

    // A portal's middle in the scene (half way between its corners 0 and 2, the interior's, turned and moved).
    private static Vector3 PortalCentre(LocationRoom r, Vector3[] corners)
    {
      var back = Quaternion.Conjugate(r.Turn);
      return (Vector3.Transform(corners[0], back) + Vector3.Transform(corners[2], back)) / 2 + r.At;
    }

    // The room of another interior with a linking portal in the same place (within 0.1 m); -1 for none.
    private static int LinkedRoom(IReadOnlyList<LocationRoom> rooms, LocationRoom r, Vector3 centre)
    {
      int linked = -1;
      for (int i = 0; i < rooms.Count; i++)
      {
        var other = rooms[i];
        if (other.At == r.At && other.Turn == r.Turn)
        {
          continue;
        }
        foreach (var q in other.Portals)
        {
          if (q.Link && q.Corners != null && q.Corners.Length >= 4 && Vector3.Distance(PortalCentre(other, q.Corners), centre) < 0.1f)
          {
            linked = i;
          }
        }
      }
      return linked;
    }

    /// <summary>Whether a place is outside every room (the sky shows there; inside, the game's world beyond is dark).</summary>
    public bool Outside(Vector3 eyeScene) => RoomOf(eyeScene) < 0;

    private FrameLight LightOf(TimeCycle c) => new FrameLight
    {
      SunDirection = TimeCycle.SunDirection(_hour),
      Sun = c.Sun * c.SunMultiplier * c.Exposure,
      AmbientAbove = c.AmbientAbove * c.AmbientMultiplierAbove * c.Exposure,
      AmbientBelow = c.AmbientBelow * c.AmbientMultiplierBelow * c.Exposure,
      // gDayNightEffects.x: up to noon the hour / 12, then back down (TimeCycle.Message).
      DayNight = _hour <= 12 ? Math.Max(_hour, 0) / 12f : Math.Max(0, 1 - (_hour - 12) / 12f),
      Exposure = c.Exposure,
      PedAoScale = c.PedAoMultiplier,
      AoScale = c.AoMultiplier,
      // gDirectionalLight.w (the page's pedShineScale.x).
      ShineScale = c.DirectionalLightW,
      // globalScalars.w (the page's pedShineScale.y, gtaiv-pc.js ~989): the mirrored surroundings' strength in the shine.
      GlobalScalarsW = c.GlobalScalarsW,
      // The fog (#112, the page's gameFog with TimeCycle.Message's fog): globalFogColorN = the modifier's near fog colour
      // or the sky bottom x SkyLightMult; globalFogColor = (SkyColor + AzimuthStrength x AzimuthColor) x SkyLightMult
      // (the page's farFogLight; without the sky's columns its haze's colour, here the near colour); FogStart; FarClip;
      // NearFogAmount - all in the light's units (x Exposure); the same pass's depth effect before it (gDepthFxParams,
      // the page's depthFx, gtaiv-pc.js ~304-311 and ~994: TimeCycle columns 48, 50, 51, 52).
      Fog = true,
      DepthFx = c.DepthFx,
      FogNear = (c.NearFogColor ?? c.SkyBottom * c.SkyLightMultiplier) * c.Exposure,
      FogFar = (c.SkyColor == Vector3.Zero ? c.NearFogColor ?? c.SkyBottom * c.SkyLightMultiplier
                                           : (c.SkyColor + c.AzimuthStrength * c.AzimuthColor) * c.SkyLightMultiplier) * c.Exposure,
      FogStart = c.FogStart,
      FogEnd = Math.Max(c.FarClip, c.FogStart + 1),
      FogNearAmount = c.NearFogAmount,
    };

    private static ToneParams ToneOf(TimeCycle c) => new ToneParams
    {
      MidGrey = c.MidGrey,
      Saturation = c.Saturation,
      ColorCorrect = c.ColorCorrect,
      ColorShift = c.ColorShift,
      Gamma = c.Gamma,
      AdaptedLumMin = c.AdaptedLumMin,
      AdaptedLumMax = c.AdaptedLumMax,
      AdaptTime = c.AdaptTime,
      // #112 4h, as the page's tone (TimeCycle.Message): ToneMapParams.z = IntensityBloom (column 38) x 0.5, the
      // bright pass (column 36), and the film grain NoiseParams.z = column 10 / 64 (TLAD only), x Exposure as the
      // light here is the game's x Exposure.
      Bloom = c.Bloom * 0.5f,
      BrightPass = c.BrightPass,
      Grain = c.Noise / 64f * c.Exposure,
    };

    /// <summary>
    /// The room a place is in (the page's roomOf): the first room in the list whose box holds it; where the boxes of two
    /// interiors hold it, the one whose floor it is nearest above. -1 for none.
    /// </summary>
    public int RoomOf(Vector3 p) => RoomOf(_location?.Rooms, p);

    /// <summary>The room of a list a place is in (see <see cref="RoomOf(Vector3)"/>); -1 for none.</summary>
    public static int RoomOf(IReadOnlyList<LocationRoom>? rooms, Vector3 p)
    {
      if (rooms == null)
      {
        return -1;
      }
      int best = -1;
      float bestHeight = float.PositiveInfinity;
      Vector3? bestAt = null;
      for (int i = 0; i < rooms.Count; i++)
      {
        var r = rooms[i];
        var v = Vector3.Transform(p - r.At, r.Turn);
        if (v.X < r.Min.X || v.X > r.Max.X || v.Y < r.Min.Y || v.Y > r.Max.Y || v.Z < r.Min.Z || v.Z > r.Max.Z)
        {
          continue;
        }
        float height = v.Z - r.Min.Z;
        bool sameInterior = bestAt is Vector3 at && at == r.At;
        if (best < 0 || (!sameInterior && height < bestHeight))
        {
          best = i;
          bestHeight = height;
          bestAt = r.At;
        }
      }
      return best;
    }

    /// <summary>
    /// What of the light from around reaches an actor at a place (the page's actorAmbientAt): its room's AmbientDownMult,
    /// eased over the room's portals (within a portal's sphere by 1 - d²/r², towards 1 where it leads outside, towards
    /// the other room's where that is more); 1 outside.
    /// </summary>
    public float ActorAmbient(Vector3 at)
    {
      int room = RoomOf(at);
      var rooms = _location?.Rooms;
      if (room < 0 || rooms == null)
      {
        return 1;
      }
      float Own(int i) => i >= 0 && i < _rooms.Length && _rooms[i] is TimeCycle c ? c.ActorAmbient : 1;
      var r = rooms[room];
      float value = Own(room);
      var v = Vector3.Transform(at - r.At, r.Turn);
      foreach (var q in r.Portals)
      {
        float d2 = Vector3.DistanceSquared(v, q.Centre);
        float t = d2 < q.Radius * q.Radius ? 1 - d2 / (q.Radius * q.Radius) : 0;
        if (q.To < 0)
        {
          value += (1 - value) * t;
        }
        else if (value < Own(q.To))
        {
          value += (Own(q.To) - value) * t;
        }
      }
      return value;
    }
  }

  /// <summary>
  /// The sun's shadow cascades of a GTA IV (PC) cutscene's frame for the native renderer (#112), as the page's
  /// sunShadowCascades (gtaiv-pc.js) lays them out after the TLAD trace GTAIV.4 (frame 9350): across the sun, u is the
  /// camera's forward laid flat onto the plane square to the sun, v = u x sun; four cascades over the view's depth from
  /// its near plane to 9.5, 20, 50 and 175 m (gFacetCentre), each the box round its slice of the view on u and v (measured
  /// there 10 x 14, 13 x 29, 37 x 73 and 147 x 254 m: the slices' corners give the same); the stored heights each lower
  /// than the sun pass's by its bias (the cascades' gShadowMatrix fourth constants against the sun pass's: 0.14, 0.24,
  /// 0.5, 0.75 m). Read as deferred_lighting.fxc's sun pass (program 21) reads it: the cascade by the depth along the
  /// view, the shadow fading out to none at 175 m (+ 1.5 (distance / 175)², c53.w). (Here with the light, not in a file
  /// of its own: the visual tests' cutgen compiles CutsceneLighting.cs.)
  /// </summary>
  public static class CutsceneSunShadow
  {
    private static readonly float[] Splits = { 0.1f, 9.5f, 20f, 50f, 175f };
    private static readonly float[] Biases = { 0.14f, 0.24f, 0.5f, 0.75f };
    private const float FadeScale = 1.5f;

    /// <summary>
    /// The cascades for a camera at <paramref name="eye"/> looking along <paramref name="forward"/> (its up
    /// <paramref name="up"/>, made square to forward as the page's view does), its vertical opening <paramref name="fov"/>
    /// (radians) and <paramref name="aspect"/>, and the sun towards <paramref name="sun"/>.
    /// </summary>
    public static SunShadow Cascades(Vector3 eye, Vector3 forward, Vector3 up, float fov, float aspect, Vector3 sun,
                                     float casterReach = float.PositiveInfinity)
    {
      var f = Vector3.Normalize(forward);
      // The page's viewProjection: s = norm(f x up), u = s x f.
      var right = Unit(Vector3.Cross(f, up));
      var upright = Vector3.Cross(right, f);
      var h = Unit(sun);
      var u = Unit(f - h * Vector3.Dot(f, h));
      if (MathF.Abs(Vector3.Dot(f, h)) > 0.999f)
      {
        u = Unit(new Vector3(1 - h.X * h.X, -h.X * h.Y, -h.X * h.Z));
      }
      var v = Vector3.Cross(u, h);
      float tanV = MathF.Tan(fov / 2), tanH = tanV * aspect;
      var shadow = new SunShadow
      {
        U = u,
        V = v,
        H = h,
        Reference = Vector3.Dot(eye, h),
        ViewForward = f,
        Splits = new Vector3(Splits[1], Splits[2], Splits[3]),
        FadeEnd = Splits[4],
        FadeScale = FadeScale,
        Eye = eye,
        CasterReach = casterReach,
      };
      for (int k = 0; k < MeshRenderer.SunCascades; k++)
      {
        float u0 = float.PositiveInfinity, u1 = float.NegativeInfinity, v0 = float.PositiveInfinity, v1 = float.NegativeInfinity;
        foreach (float d in new[] { Splits[k], Splits[k + 1] })
        {
          for (int a = -1; a <= 1; a += 2)
          {
            for (int b = -1; b <= 1; b += 2)
            {
              var p = eye + (f + right * (a * tanH) + upright * (b * tanV)) * d;
              float pu = Vector3.Dot(p, u), pv = Vector3.Dot(p, v);
              u0 = MathF.Min(u0, pu);
              u1 = MathF.Max(u1, pu);
              v0 = MathF.Min(v0, pv);
              v1 = MathF.Max(v1, pv);
            }
          }
        }
        shadow.Boxes[k] = new Vector4((u0 + u1) / 2, (v0 + v1) / 2, 2 / MathF.Max(u1 - u0, 1e-3f), 2 / MathF.Max(v1 - v0, 1e-3f));
        shadow.Biases[k] = Biases[k];
      }
      return shadow;
    }

    /// <summary>
    /// How far from the camera the map's objects cast (SunShadow.CasterReach), as the page culls them (ModelView.html
    /// drawMeshes' seen, outside: nothing wholly beyond hazeEnd(), nor beyond drawDistanceNow()): the haze's end - the
    /// light's fog end (the time cycle's FarClip), or nearer the farthest of the location's objects (<paramref
    /// name="locationReach"/>, see <see cref="LocationReach"/>) - and the shot's draw distance (<paramref
    /// name="drawSteps"/>: CutFile.DrawDistanceSteps, [frame, metres]) at <paramref name="frame"/>.
    /// </summary>
    public static float CasterReach(float fogEnd, float locationReach, IReadOnlyList<float[]>? drawSteps, double frame)
    {
      float reach = MathF.Min(fogEnd > 0 ? fogEnd : float.PositiveInfinity, locationReach);
      return MathF.Min(reach, DrawDistance(drawSteps, frame));
    }

    /// <summary>
    /// The shot's draw distance at <paramref name="frame"/> (<paramref name="drawSteps"/>: CutFile.DrawDistanceSteps,
    /// [frame, metres]; the last step at or before the frame), infinity without one - the page's drawDistanceNow
    /// (ModelView.html), which GTA IV makes the cutscene camera's far clip (0xD53280).
    /// </summary>
    public static float DrawDistance(IReadOnlyList<float[]>? drawSteps, double frame)
    {
      float metres = float.PositiveInfinity;
      if (drawSteps != null)
      {
        foreach (var step in drawSteps)
        {
          if (step.Length < 2 || step[0] > frame + 1e-3)
          {
            break;
          }
          metres = step[1];
        }
      }
      return metres;
    }

    /// <summary>
    /// The page's hazeReach: the farthest of a location's objects across the ground from its origin (each by the
    /// centre of its model's box placed; <paramref name="bounds"/>: a model's box by its number in the location, null
    /// when it draws nothing); infinity without any.
    /// </summary>
    public static float LocationReach(CutsceneLocation location, Func<int, (Vector3 Min, Vector3 Max)?> bounds)
    {
      float farthest = 0;
      foreach (var o in location.Objects)
      {
        if (bounds(o.Model) is not var (min, max))
        {
          continue;
        }
        var c = Vector3.Transform((min + max) * 0.5f, o.Place);
        farthest = MathF.Max(farthest, MathF.Sqrt(c.X * c.X + c.Y * c.Y));
      }
      return farthest > 0 ? farthest : float.PositiveInfinity;
    }

    private static Vector3 Unit(Vector3 a)
    {
      float l = a.Length();
      return l > 0 ? a / l : a;
    }
  }
}
