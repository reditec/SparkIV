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
  }
}
