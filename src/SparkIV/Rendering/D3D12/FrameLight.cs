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

using System.Numerics;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The light a frame is drawn in (#112 step 4c): the sun (towards it, its colour) and the light from around (above,
  /// below), all already times the exposure; how far through the day it is (0 at midnight, 1 at noon: what a map
  /// object's vertex colours take their day or night darkening by); the exposure (for glowing materials); and the
  /// darkening scales for peds and everything else. A game's module fills it (GTA IV (PC): CutsceneLighting).
  /// </summary>
  public struct FrameLight
  {
    public Vector3 SunDirection;
    public Vector3 Sun;
    public Vector3 AmbientAbove;
    public Vector3 AmbientBelow;
    public float DayNight;
    public float Exposure;
    public float PedAoScale;
    public float AoScale;
    /// <summary>
    /// The sun's share in the shine (#112 step 4d; GTA IV's gDirectionalLight.w, the page's pedShineScale.x): the
    /// sun's shine on specular surfaces is the sun x this.
    /// </summary>
    public float ShineScale;

    /// <summary>
    /// The fog laid over the light of everything drawn but the sky, before the tone mapping (GTA IV's rage_postfx
    /// program 21, the page's gameFog): its amount at a distance d lerp(far, near, <see cref="FogNearAmount"/>) (+
    /// <see cref="FogNearAmount"/> where <see cref="FogStart"/> is 0 or less), near = saturate(d / FogStart), far =
    /// saturate((d - FogStart) / (FogEnd - FogStart)); its colour from <see cref="FogNear"/> to <see cref="FogFar"/> by
    /// far (both in the light's units, x the exposure). None when <see cref="Fog"/> is false.
    /// </summary>
    public bool Fog;
    public Vector3 FogNear;
    public Vector3 FogFar;
    public float FogStart;
    public float FogEnd;
    public float FogNearAmount;

    /// <summary>
    /// The fog pass's depth effect before the fog (GTA IV's gDepthFxParams, the page's depthFx in gameFog): x
    /// DesaturationFar, y GammaFar, z where it starts, w where it is whole (none unless w &gt; z). From z to w the light
    /// desaturated towards x and multiplied by (luma / exposure)^(ramp x (y - 1)).
    /// </summary>
    public Vector4 DepthFx;

    /// <summary>The sun's shadow (cascaded maps; see the class SunShadow); null: none (the sun everywhere).</summary>
    public SunShadow? SunShadow;

    /// <summary>
    /// How far the camera sees, as depth along its view (0: no limit): every solid and see-through pixel beyond it is
    /// left out (water, sky and the shadow maps are not) - the page's drawReach (a cutscene's draw distance as the
    /// camera's far clip). A game's module sets it (GTA IV (PC): CutscenePlayer from the .cut's draw distance).
    /// </summary>
    public float DrawReach;

    /// <summary>A plain light for views without a game's light (the camera's side, a little ambient).</summary>
    public static FrameLight Plain(Vector3 toLight) => new FrameLight
    {
      SunDirection = Vector3.Normalize(toLight),
      Sun = new Vector3(0.65f),
      AmbientAbove = new Vector3(0.35f),
      AmbientBelow = new Vector3(0.35f),
      DayNight = 1,
      Exposure = 1,
      PedAoScale = 1,
      AoScale = 1,
      ShineScale = 1,
    };
  }

  /// <summary>
  /// The sun's shadow over a frame, in cascades (#112; MeshRenderer draws and reads it, a game's module lays it out -
  /// GTA IV (PC): CutsceneSunShadow). Across the sun the axes <see cref="U"/> and <see cref="V"/>, <see cref="H"/>
  /// towards it; heights along it from <see cref="Reference"/>. Each of the <see cref="MeshRenderer.SunCascades"/> cascades
  /// covers a box on u and v (<see cref="Boxes"/>: its centre on u and v, 1 / half its width on u and v) drawn into its
  /// quarter of the map, the heights stored less its <see cref="Biases"/> entry. A point is read from the cascade picked by
  /// its depth along <see cref="ViewForward"/> (the next from each of <see cref="Splits"/>' x, y, z on), lit where it is
  /// at least as high as what is stored round it (twelve taps); plus <see cref="FadeScale"/> x (distance /
  /// <see cref="FadeEnd"/>)², none from <see cref="FadeEnd"/> on. Drawn from what casts (see MeshRenderer.AddInstance).
  /// </summary>
  public sealed class SunShadow
  {
    public Vector3 U, V, H;
    public float Reference;
    public Vector4[] Boxes = new Vector4[MeshRenderer.SunCascades];
    public float[] Biases = new float[MeshRenderer.SunCascades];
    public Vector3 ViewForward;
    public Vector3 Splits;
    public float FadeEnd;
    public float FadeScale;

    /// <summary>
    /// The camera, and how far from it the map's objects cast at all (each part by its bounding sphere: left out where
    /// its nearest point lies farther; infinity: no limit) - the page's caster culling, see CutsceneSunShadow.
    /// </summary>
    public Vector3 Eye;
    public float CasterReach = float.PositiveInfinity;
  }

  /// <summary>Which of the deferred light's formulas a light takes (see <see cref="DeferredLight"/>).</summary>
  public enum DeferredLightModel
  {
    /// <summary>GTA IV's "light family" (deferred_lighting.fxc lightVolumePoint, ps06-ps09; with a shadow ps14).</summary>
    Lamp = 0,
    /// <summary>GTA IV's "filler" family (fillerVolumePoint, ps10-ps12; with a shadow ps15).</summary>
    Filler = 1,
    /// <summary>A cutscene's own light (ps09 / program 29): no AO factor.</summary>
    Cut = 2,
  }

  /// <summary>Which pixels a light reaches, by whether their light is baked into vertex colours (the game's stencil).</summary>
  public enum DeferredLightMask
  {
    All = 0,
    /// <summary>Only peds and vehicles (a cutscene's own light).</summary>
    NotBaked = 1,
    /// <summary>Only the location and the cast's props (a cutscene's env*/shd* light).</summary>
    BakedOnly = 2,
  }

  /// <summary>
  /// A lamp's corona (#112): a sprite facing the camera, half a side <see cref="Size"/> metres, at <see cref="At"/> (already
  /// moved towards the camera), added to the light after the scene; <see cref="Color"/> its colour x strength (the
  /// renderer multiplies by the exposure). A game's module makes the list (GTA IV (PC): CutsceneLights.CoronasNow).
  /// </summary>
  public struct Corona
  {
    public Vector3 At;
    public Vector3 Color;
    public float Size;
  }

  /// <summary>
  /// A lamp's visible light volume (#112): its light in the air, added after the scene (the page's gameLightVolumes). At
  /// the lamp, along <see cref="Direction"/> (a spot's axis; <see cref="Across"/> its second axis), reaching
  /// <see cref="Radius"/>; <see cref="Cone"/> a spot's outer angle in radians (0: a point lamp, all round);
  /// <see cref="Color"/> its colour x strength x 0.25 (the renderer multiplies by the exposure); <see cref="Lookup"/>:
  /// by the lookup texture (lamps without the filler flag) instead of the sphere's chord.
  /// </summary>
  public struct LightVolume
  {
    public Vector3 At;
    public Vector3 Direction;
    public Vector3 Across;
    public Vector3 Color;
    public float Radius;
    public float Cone;
    public bool Lookup;
  }

  /// <summary>
  /// A light added over the G-buffer by its own light volume (#112 step 5): the location's lamps, a cutscene's own lights,
  /// a vehicle's tail lamps - as many as there are (no cap: the game draws each lamp as a volume). Its colour already
  /// includes its strength and the exposure; a spot fades from <see cref="CosOuter"/> by <see cref="ConeScale"/>
  /// (saturate((cos - CosOuter) x ConeScale)), CosOuter -1 lights all round. A game's module makes the list (GTA IV
  /// (PC): CutsceneLights).
  /// </summary>
  public struct DeferredLight
  {
    public Vector3 Position;
    public float Range;
    public Vector3 Direction;
    public float CosOuter;
    public Vector3 Color;
    public float ConeScale;
    public DeferredLightModel Model;
    public DeferredLightMask Mask;
    /// <summary>A lamp in an interior's room: no AO factor (GTA IV's ps07); outside saturate(AO² + 0.5) (ps06/ps08).</summary>
    public bool InRoom;
    /// <summary>Drawn by the shadow programs (GTA IV's ps14/ps15): fully lit unless it has a <see cref="ShadowSlot"/>.</summary>
    public bool Shadowed;
    /// <summary>
    /// A shadowed light's slot in the shadow atlas (0 to <see cref="MeshRenderer.ShadowSlots"/> - 1): its map drawn this
    /// frame from the instances that cast (see <see cref="MeshRenderer.AddInstance"/>); null: none (fully lit).
    /// </summary>
    public int? ShadowSlot;
  }

  /// <summary>
  /// How the light of a frame becomes its picture (#112 step 4c), as GTA IV's post effects do it (the page's composite):
  /// the scale MidGrey / the adapted luminance, saturation, colour shift, colour correction (x 2) and gamma on the
  /// luminance; the adapted luminance kept within AdaptedLumMin and AdaptedLumMax and eased over AdaptTime.
  /// </summary>
  public struct ToneParams
  {
    public float MidGrey;
    public float Saturation;
    public Vector3 ColorCorrect;
    public Vector3 ColorShift;
    public float Gamma;
    public float AdaptedLumMin;
    public float AdaptedLumMax;
    public float AdaptTime;
    /// <summary>
    /// The bloom's strength (step 4h; the page's tone.bloom): what of the blurred quarter copy lies above
    /// <see cref="BrightPass"/> (in the picture's units) is added x this x 0.25. 0: none.
    /// </summary>
    public float Bloom;
    /// <summary>The bloom's threshold in the picture's units (after the scale MidGrey / adapted).</summary>
    public float BrightPass;
    /// <summary>The film grain's strength in the light's units (0: none): the grain texture's blue - 0.5, x this x 1.1.</summary>
    public float Grain;

    /// <summary>No tone mapping to speak of (scale 1 for a luminance of 1).</summary>
    public static ToneParams Plain => new ToneParams
    {
      MidGrey = 1, Saturation = 1, ColorCorrect = new Vector3(0.5f), Gamma = 1, AdaptedLumMin = 1, AdaptedLumMax = 1,
    };
  }

  /// <summary>
  /// The post effects of a frame besides the tone mapping (#112 step 4h): the depth of field (sharp between
  /// <see cref="FocusNear"/> and <see cref="FocusFar"/>, metres from the camera, fully blurred half a metre nearer or
  /// beyond; the depth read through the projection's <see cref="NearClip"/> and <see cref="FarClip"/>) and a fade to
  /// black (0 none, 1 black).
  /// </summary>
  public struct PostParams
  {
    public bool DepthOfField;
    public float NearClip, FarClip, FocusNear, FocusFar;
    public float Fade;

    public static PostParams None => default;
  }
}
