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
  /// page's roomOf; an actor's share of the light from around (AmbientDownMult) by the page's actorAmbientAt. Not yet:
  /// the page's easing between the outside and a room by how much of the screen its portals cover (roomStrength /
  /// mixSky): the room is taken whole.
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
    }

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

    /// <summary>The light and tone of a frame seen from <paramref name="eyeScene"/> (in the scene's places).</summary>
    public (FrameLight Light, ToneParams Tone) At(Vector3 eyeScene)
    {
      var c = CycleAt(eyeScene);
      return (LightOf(c), ToneOf(c));
    }

    /// <summary>The time cycle a frame seen from <paramref name="eyeScene"/> takes (for the sky: CutsceneSky).</summary>
    public TimeCycle CycleAt(Vector3 eyeScene)
    {
      UnderBoxes(eyeScene);
      int room = RoomOf(eyeScene);
      return room >= 0 && room < _rooms.Length && _rooms[room] is TimeCycle inRoom ? inRoom : _boxed;
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
}
