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
  /// What the native renderer draws of a GTA IV (PC) cutscene (#112), shared by the app's player and the visual tests'
  /// cutgen: an actor's models and a location's placed objects.
  /// </summary>
  public static class CutsceneDrawing
  {
    /// <summary>
    /// An actor's models: its shown meshes (moving with its bones when it has a skeleton) in its paint (a vehicle's
    /// materials' paint slots take its colours, as the page's "paint"), and the props it wears, fixed to their bones.
    /// </summary>
    public static List<ModelSource> SourcesOf(CutActor actor)
    {
      var sources = new List<ModelSource>();
      if (actor.Scene is not ModelScene scene)
      {
        return sources;
      }
      bool skinned = scene.Bones.Count > 0;
      var paint = actor.Paint;
      var tints = paint == null ? null : scene.Materials.Where(m => m.PaintSlot > 0 && m.PaintSlot <= paint.Length)
        .ToDictionary(m => m.Id, m => new Vector3(paint[m.PaintSlot - 1][0], paint[m.PaintSlot - 1][1], paint[m.PaintSlot - 1][2]));
      // Peds lit as they are; props (a door) as the location is (their light baked into vertex colours); vehicles never baked.
      sources.Add(ModelSource.Shown(scene, skinned, baked: !actor.IsPed) with { Tints = tints, Actor = true, Looks = LooksOf(actor, scene) });
      var worn = CutsceneMessages.WornProps(actor);
      if (worn.Count > 0 && skinned && actor.PropScene != null)
      {
        sources.Add(new ModelSource(worn, actor.PropScene.Materials, actor.PropScene.Textures, true) { Actor = true });
      }
      return sources;
    }

    /// <summary>
    /// What a cutscene vehicle sets on its materials, as the page's scene message (Cutscene.Scene): the second paint
    /// layer's vehicle colour, its dirt, its lamps on or off, the reflectivity the game gives it (Shine[4]) and
    /// gta_vehicle_paint2's second shine (the sixth colour, exponent factor 6.8 x specular2FactorED / specularFactorED,
    /// strength specular2ColorIntensityED² / specularColorFactorED). Null for peds and props.
    /// </summary>
    private static Dictionary<int, MaterialLook>? LooksOf(CutActor actor, ModelScene scene)
    {
      if (!scene.Materials.Any(m => m.Vehicle || m.Paint2Slot > 0))
      {
        return null;
      }
      var paint = actor.Paint;
      var looks = new Dictionary<int, MaterialLook>();
      foreach (var m in scene.Materials)
      {
        var look = new MaterialLook();
        if (paint != null && m.Paint2Slot > 0 && m.Paint2Slot <= paint.Length)
        {
          var c = paint[m.Paint2Slot - 1];
          look = look with { Paint2 = new Vector4(c[0], c[1], c[2], 1) };
        }
        if (m.Vehicle)
        {
          look = look with { DirtLevel = actor.Dirt, LightsOn = actor.LightsOn };
          if (actor.Shine is { Length: >= 5 } shine)
          {
            look = look with { ReflectivePower = shine[4] };
            if (m.HasSpecular2 && paint != null && paint.Length >= 6)
            {
              look = look with
              {
                Specular2Color = new Vector3(paint[5][0], paint[5][1], paint[5][2]),
                Specular2Exponent = 6.8f * shine[2] / shine[0],
                Specular2Strength = shine[3] * shine[3] / shine[1],
              };
            }
          }
        }
        looks[m.Id] = look;
      }
      return looks;
    }

    /// <summary>A location's models, their light baked into their vertex colours.</summary>
    public static List<ModelSource> LocationSources(CutsceneLocation location) =>
      location.Models.Select(m => ModelSource.Shown(m, false, baked: true)).ToList();

    /// <summary>A location's models and its objects to draw (all but those only for the mirrored surroundings' map).</summary>
    public static IEnumerable<LocationObject> DrawnObjects(CutsceneLocation location) => location.Objects.Where(o => !o.MirrorOnly);

    /// <summary>When true, the far plane only from the draw distance (at least 1000 m), as before 2026-11-05. For comparisons.</summary>
    public static bool FarClipWithoutReach { get; set; }

    /// <summary>
    /// The cutscene camera's far plane: max(1000 m, the .cut's draw distance x 2), and beyond the location as the page
    /// keeps it (ModelView.html ~1037 and ~3102: backgroundReach = 1.8 x the largest |coordinate| of the location's
    /// meshes in the scene's places, the film's far plane at least twice that). With 1000 m, et01_ba f1292.25 lost the
    /// far city's SuprLOD_Ks_01 / _02 (1100-1200 m) through the gaps under the bridge, which the game and the page show.
    /// </summary>
    public static float FarClip(CutsceneLocation? location, float drawDistance)
    {
      float far = Math.Max(1000f, drawDistance * 2);
      if (location == null || FarClipWithoutReach)
      {
        return far;
      }
      float reach = 0;
      var modelReach = new Dictionary<(int, Matrix4x4), float>();
      foreach (var o in location.Objects)
      {
        if (modelReach.TryGetValue((o.Model, o.Place), out float r))
        {
          reach = Math.Max(reach, r);
          continue;
        }
        r = 0;
        foreach (var mesh in location.Models[o.Model].Meshes)
        {
          var p = mesh.Positions;
          if (p == null)
          {
            continue;
          }
          for (int i = 0; i + 2 < p.Length; i += 3)
          {
            var v = Vector3.Transform(new Vector3(p[i], p[i + 1], p[i + 2]), o.Place);
            r = Math.Max(r, Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))));
          }
        }
        modelReach[(o.Model, o.Place)] = r;
        reach = Math.Max(reach, r);
      }
      return Math.Max(far, reach * 1.8f * 2);
    }
  }
}
