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
using RageLib.GtaIV.Pc.Text;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// How GTA IV (PC) draws a model's material: what its draw bucket and its shader's name mean, and the hashes of
  /// the shader parameters the viewer reads. Another game or engine version numbers its buckets and names its
  /// shaders and parameters differently.
  /// </summary>
  internal static class GtaIVPcMaterials
  {
    /// <summary>A material's way of being drawn, from its draw bucket (and shader) as GTA IV (PC) draws it.</summary>
    public readonly record struct Drawing(bool Cutout, bool Blend, bool Stipple, bool Decal, bool Hidden, bool NoWater, bool Emissive);

    /// <summary>
    /// The draw bucket (RageLib's Material.DrawBucket): 0 opaque, 1 see-through, 2 decal, 3 cut out, 4 and 5
    /// emissive (5 see-through), 29 and 30 vehicles' no-splash and no-water planes.
    /// </summary>
    public static Drawing DrawingOf(int drawBucket, string shader, bool glass) => new Drawing(
      // See-through texels cut out (leaves, fences, hair): the cut-out bucket. Opaque ones keep them (peds'
      // skin: its alpha holds something else, the face would be gone).
      Cutout: drawBucket == 3,
      // Drawn over what is behind it by its texture's alpha (see-through, decals); glass has its own way.
      Blend: !glass && drawBucket is 1 or 2 or 5,
      // A person's see-through part (gta_ped, bucket 1), drawn as the game draws it: gta_ped.fxc's technique
      // "draw" (forward, after the deferred pass) with its own render states AlphaTest on, AlphaRef 100,
      // AlphaBlend on (seen live in x32dbg while the game drew peds' props: ALPHAREF 100 then back to 0), and
      // its pixel shader keeps only part of the pixels by alpha before blending them (the stipple texture
      // platform:/textures/stipple.wtd all_stips, 16 levels of 8 x 8, level = alpha x 3.996). bc_3: Florian's
      // glasses (p_eyes_diff_000_a), lenses alpha 124: level 7, 24 of 64 pixels kept, blended at 0.486 -
      // about 18 % in all (blended whole they looked like sunglasses).
      Stipple: drawBucket == 1 && string.Equals(shader, "gta_ped", StringComparison.OrdinalIgnoreCase),
      // Lies on another surface (a puddle, dirt, a poster): drawn a little in front of it.
      Decal: drawBucket == 2,
      // Planes the game doesn't draw (a vehicle's no-splash and no-water planes, keeping water out).
      Hidden: IsNeverDrawn(drawBucket),
      // The no-water plane: where the game keeps the water out (a boat's cockpit).
      NoWater: drawBucket == 30,
      // Glowing (lamp shades, signs, windows at night): shown at full brightness, not lit.
      Emissive: drawBucket is 4 or 5);

    /// <summary>The buckets of planes the game never draws (boats' no-splash and no-water planes): no real textures.</summary>
    public static bool IsNeverDrawn(int drawBucket) => drawBucket is 29 or 30;

    /// <summary>Glass is drawn see-through; it is named so in the shader (gta_vehicle_vehglass, gta_glass ...).</summary>
    public static bool IsGlass(string shader) => shader.Contains("glass", StringComparison.OrdinalIgnoreCase);

    /// <summary>A vehicle's shaders (gta_vehicle_paint1 ..., _mesh, _vehglass).</summary>
    public static bool IsVehicle(string shader) => shader.StartsWith("gta_vehicle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How a vehicle shader turns its spec map into shine, read from its pixel programs (win32_30 .fxc):
    /// (intensity factor, power factor, forward glass). The shine's strength is dot(spec map rgb,
    /// specMapIntMask) x reflectivePowerED x the intensity factor x (1 - dirt), its power spec map alpha x
    /// reflectivePowerED x the power factor.
    /// - gta_vehicle_paint1/2/3, deferred_draw (paint1 program 13): GB2.x = 0.5 x dot x reflectivePowerED x 0.15
    ///   (c0.z) x (1 - dirt) x matDiffuseColor2.w, GB2.y = sqrt(spec.a x reflectivePowerED x 190 (c0.y) x
    ///   matDiffuseColor2.w / 512); deferred_lighting.fxc's sun pass (program 21) takes 2 x GB2.x and GB2.y² x 512
    ///   back: (0.15, 190). The constants 0.15 and 190 are in all three paint .fxc files.
    /// - gta_vehicle_mesh, deferred_draw (program 13): GB2.x = 0.5 x dot x reflectivePowerED (c76) x (1 - dirt) x
    ///   matDiffuseColor2.w, GB2.y = sqrt(spec.a x reflectivePowerED x matDiffuseColor2.w x 0.09765625): (1, 50).
    /// - gta_vehicle_vehglass, technique draw (forward, program 8): its own lighting, see the page (the third
    ///   number 1): min(dot x reflectivePowerED x 0.225 (c1.z), 1), power spec.a x reflectivePowerED x 190 (c0.w).
    /// - gta_vehicle_interior and gta_vehicle_interior2, deferred_draw (program 5 of both): GB2.x = dot x
    ///   reflectivePowerED x matDiffuseColor2.w x 0.0025, GB2.y = sqrt(spec.a x reflectivePowerED x matDiffuseColor2.w
    ///   x 0.01953125): (0.005, 10), almost no shine. With paint's numbers instead, et01_ba's Presidente showed light
    ///   grey seats behind Jim where the game's are near black (#14; the TLAD trace's frame Present 202699634).
    /// - gta_vehicle_tire, deferred_draw: GB2.x = 0.5 x dot x reflectivePowerED x 1.4 (c1.x) x (1 - dirt) x
    ///   matDiffuseColor2.w, GB2.y = sqrt(spec.a x reflectivePowerED x matDiffuseColor2.w x 0.09765625): (1.4, 50).
    /// - gta_vehicle_shuts, deferred_draw: GB2.x = dot x reflectivePowerED x matDiffuseColor2.w x 0.075, GB2.y =
    ///   sqrt(spec.a x reflectivePowerED x matDiffuseColor2.w x 0.37109375): (0.15, 190), as paint's.
    /// - gta_vehicle_badges and gta_vehicle_lightsemissive, deferred_draw: GB2.x = 0.5 x min(dot x reflectivePowerED x
    ///   0.225, 1) (x (1 - dirt)) x matDiffuseColor2.w, GB2.y as shuts': (0.225, 190), the strength at most 1 (the third
    ///   number 2).
    /// - gta_vehicle_chrome, deferred_draw: GB2.x = 0.5 x dot x specularColorFactor x reflectivePowerED x (1 - dirt) x
    ///   matDiffuseColor2.w, GB2.y = sqrt(spec.a x specularFactor x reflectivePowerED x matDiffuseColor2.w / 512): the
    ///   material's own (specularColorFactor, specularFactor).
    /// matDiffuseColor2.w is taken as its .fxc default, 1 (what the game sets at run time is not traced).
    /// (All read with tools/fxc-dump.py, docs/GtaIV/Pc/formats/fxc.md.) Null for the other vehicle shaders.
    /// </summary>
    public static float[]? VehicleShineOf(string shader, float specularColorFactor, float specularFactor) => shader.ToLowerInvariant() switch
    {
      "gta_vehicle_paint1" or "gta_vehicle_paint2" or "gta_vehicle_paint3" or "gta_vehicle_shuts" => new[] { 0.15f, 190f, 0f },
      "gta_vehicle_mesh" => new[] { 1f, 50f, 0f },
      "gta_vehicle_tire" => new[] { 1.4f, 50f, 0f },
      "gta_vehicle_interior" or "gta_vehicle_interior2" => new[] { 0.005f, 10f, 0f },
      "gta_vehicle_badges" or "gta_vehicle_lightsemissive" => new[] { 0.225f, 190f, 2f },
      "gta_vehicle_chrome" => new[] { specularColorFactor, specularFactor, 0f },
      "gta_vehicle_vehglass" => new[] { 0.225f, 190f, 1f },
      _ => null,
    };

    /// <summary>
    /// The ped shaders (gta_ped, gta_ped_skin, gta_ped_reflect ...): their vertex red is the AO the game's sun pass
    /// takes the light from around by (gta_ped.fxc's vertex shader: 1 + gs2.y x gs2.z x (red x gs.z - 1)).
    /// </summary>
    public static bool IsPed(string shader) => shader.StartsWith("gta_ped", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// gta_normal and rage_default: shine without a specular map. Their G-buffer pass (deferred_draw) writes GB2.x =
    /// 0.5 x specularColorFactor and GB2.y = sqrt(specularFactor / 512), as gta_normal_spec does with a white
    /// specular map (tools/fxc-dump.py, docs/GtaIV/Pc/formats/fxc.md).
    /// </summary>
    public static bool IsFlatSpecular(string shader) =>
      shader.Equals("gta_normal", StringComparison.OrdinalIgnoreCase) || shader.Equals("rage_default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dirt decals (gta_decal_dirt): colour from the vertices, the texture only says where.</summary>
    public static bool IsDirtDecal(string shader) => shader.Contains("decal_dirt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The emissive shaders (gta_emissive*: texture x vertex colour x emissiveMultiplier, unlit).</summary>
    public static bool IsEmissive(string shader) => shader.Contains("emissive", StringComparison.OrdinalIgnoreCase);

    /// <summary>gta_emissivenight: only shines at night (x the time cycle's day/night effects).</summary>
    public static bool IsEmissiveAtNight(string shader) => shader.Contains("emissivenight", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// gta_emissivestrong: its vertex shaders push it back by zShift (add o0.z, r0.z, c210.x), gta_emissive's and
    /// gta_emissivenight's pull it forward (-c210.x) - all of their vertex programs (win32_30 .fxc). Seen in the
    /// traces: bc_3's caged wall lamp (GTAIV.1, draw 84178518) keeps its bars, bc_1's spot lens (GTAIV.2, draw
    /// 73906237) shows over its housing.
    /// </summary>
    public static bool IsEmissiveStrong(string shader) => shader.Contains("emissivestrong", StringComparison.OrdinalIgnoreCase);

    public static readonly uint EmissiveMultiplier = GxtFile.Hash("emissiveMultiplier");

    // gta_ped_skin's light through the skin (its G-buffer pass, program 10): a colour, how far the light wraps
    // round and how wide the band at the shadow's edge is.
    public static readonly uint SubColor = GxtFile.Hash("SubColor");
    public static readonly uint SubScatWrap = GxtFile.Hash("SubScatWrap");
    public static readonly uint SubScatWidth = GxtFile.Hash("SubScatWidth");

    public static readonly uint Specular2ColorIntensityED = GxtFile.Hash("specular2ColorIntensityED");

    // A vehicle's shaders: their dirt, its colour, how reflective.
    public static readonly uint DirtSampler = GxtFile.Hash("DirtSampler"), DirtColor = GxtFile.Hash("dirtColor"),
                                ReflectivePowerED = GxtFile.Hash("reflectivePowerED"), ReflectivePower = GxtFile.Hash("reflectivePower");

    // The map's *_reflect shaders' mirrored surroundings: a texture of their own (gta_spec_reflect's G-buffer
    // pass, the TLAD trace GTAIV.4's pixel shader 0x22F3F120: EnvironmentSampler s2).
    public static readonly uint EnvironmentSampler = GxtFile.Hash("EnvironmentSampler");

    /// <summary>
    /// A map shader that adds its environment texture to the albedo (gta_spec_reflect, gta_normal_spec_reflect, ...; and
    /// plain gta_glass, whatever its name says: Bernie's bedroom pane, the GTA IV trace GTAIV.trace, draw 114115710, PS
    /// 0x2cc5d340). Not gta_glass_spec, which has none, nor gta_glass_emissive: it has the sampler, but the game draws it
    /// only as its glow (the same trace's lamp glass km_bernilite, draw 114115061: texture x vertex colour x multiplier).
    /// </summary>
    public static bool IsMapReflect(string shader) =>
      (shader.Contains("_reflect", StringComparison.OrdinalIgnoreCase) || shader.Equals("gta_glass", StringComparison.OrdinalIgnoreCase)) && !IsVehicle(shader);

    // A vehicle's "matdiffusecolor": (2, slot, slot) a placeholder for one of the vehicle's colours (the
    // game's shader multiplies the texture by it: gta_vehicle_mesh's pixel shader, texture x c66).
    public static readonly uint MatDiffuseColor = GxtFile.Hash("matDiffuseColor");

    // "dirtdecalmask": which of a dirt decal's texture channels says where the dirt is (gta_decal_dirt).
    public static readonly uint DirtDecalMask = GxtFile.Hash("dirtDecalMask");

    // The hashes of the map shaders' parameters for bumps and shine, named by hashing their names
    // ("bumpsampler" ...) and checked against the materials of eleven exported map archives.
    public const uint BumpSampler = 0x46b7c64f, SpecSampler = 0x608799c6, Bumpiness = 0xf6712b81,
                      SpecularFactor = 0x166e0fd1, SpecularColorFactor = 0x484a5ebd, SpecMapIntMask = 0xff11711d;

    // Terrain (gta_terrain_va_2lyr ... 4lyr): its texture layers' samplers, the first the ground the others
    // are laid over.
    public const uint TerrainLayer0 = 0xd52b11df;
    public static readonly uint[] TerrainLayers = { TerrainLayer0, 0x2420afd1, 0x31934ab6, 0x78b758fd };

    /// <summary>Rockstar's placeholder for a texture never given (some vehicles' normal maps, never-drawn planes).</summary>
    public const string NoTexture = "givemechecker";
  }
}
