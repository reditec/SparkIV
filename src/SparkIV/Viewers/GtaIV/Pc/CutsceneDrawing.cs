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
      sources.Add(ModelSource.Shown(scene, skinned, baked: !actor.IsPed) with { Tints = tints });
      var worn = CutsceneMessages.WornProps(actor);
      if (worn.Count > 0 && skinned && actor.PropScene != null)
      {
        sources.Add(new ModelSource(worn, actor.PropScene.Materials, actor.PropScene.Textures, true));
      }
      return sources;
    }

    /// <summary>A location's models, their light baked into their vertex colours.</summary>
    public static List<ModelSource> LocationSources(CutsceneLocation location) =>
      location.Models.Select(m => ModelSource.Shown(m, false, baked: true)).ToList();

    /// <summary>A location's models and its objects to draw (all but those only for the mirrored surroundings' map).</summary>
    public static IEnumerable<LocationObject> DrawnObjects(CutsceneLocation location) => location.Objects.Where(o => !o.MirrorOnly);
  }
}
