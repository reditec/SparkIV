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

    /// <summary>No tone mapping to speak of (scale 1 for a luminance of 1).</summary>
    public static ToneParams Plain => new ToneParams
    {
      MidGrey = 1, Saturation = 1, ColorCorrect = new Vector3(0.5f), Gamma = 1, AdaptedLumMin = 1, AdaptedLumMax = 1,
    };
  }
}
