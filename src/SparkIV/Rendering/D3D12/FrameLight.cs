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
    };
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
