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

    /// <summary>Dirt decals (gta_decal_dirt): colour from the vertices, the texture only says where.</summary>
    public static bool IsDirtDecal(string shader) => shader.Contains("decal_dirt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The emissive shaders (gta_emissive*: texture x vertex colour x emissiveMultiplier, unlit).</summary>
    public static bool IsEmissive(string shader) => shader.Contains("emissive", StringComparison.OrdinalIgnoreCase);

    /// <summary>gta_emissivenight: only shines at night (x the time cycle's day/night effects).</summary>
    public static bool IsEmissiveAtNight(string shader) => shader.Contains("emissivenight", StringComparison.OrdinalIgnoreCase);

    public static readonly uint EmissiveMultiplier = GxtFile.Hash("emissiveMultiplier");

    public static readonly uint Specular2ColorIntensityED = GxtFile.Hash("specular2ColorIntensityED");

    // A vehicle's shaders: their dirt, its colour, how reflective.
    public static readonly uint DirtSampler = GxtFile.Hash("DirtSampler"), DirtColor = GxtFile.Hash("dirtColor"),
                                ReflectivePowerED = GxtFile.Hash("reflectivePowerED"), ReflectivePower = GxtFile.Hash("reflectivePower");

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
