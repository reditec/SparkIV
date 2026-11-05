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
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using SparkIV.WinUI.Viewers;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// What a model draws: its meshes shown (a model's checked ones: its level of detail, a ped's variation), their
  /// materials and textures, and whether they move with bones (a cutscene's cast: each vertex's four bones and
  /// weights, from <see cref="SceneMesh.SkinBones"/>).
  /// </summary>
  public sealed record ModelSource(IReadOnlyList<SceneMesh> Meshes, IReadOnlyList<SceneMaterial> Materials,
                                   IReadOnlyDictionary<string, byte[]> Textures, bool Skinned)
  {
    /// <summary>A colour each material's texture is multiplied by, by material id (a vehicle's paint); white if none.</summary>
    public IReadOnlyDictionary<int, Vector3>? Tints { get; init; }

    /// <summary>
    /// Whether its light is baked into its vertex colours (a location's map objects, a cutscene's props: their red by
    /// day, green at night darken the light from around; without colours 0.45), else a ped's or a vehicle's.
    /// </summary>
    public bool Baked { get; init; }

    /// <summary>
    /// Whether it is a cutscene's cast (an actor's model or worn props): its decals keep their own light instead of
    /// taking the surface's below (the page's underDecal: not for actors), its see-through parts aren't the map's.
    /// </summary>
    public bool Actor { get; init; }

    /// <summary>What the model's owner sets on its materials, by material id (a vehicle's second paint, dirt, shine, lamps).</summary>
    public IReadOnlyDictionary<int, MaterialLook>? Looks { get; init; }

    /// <summary>A model's checked meshes (as the page shows them), moving with bones or not.</summary>
    public static ModelSource Shown(ModelScene scene, bool skinned, bool baked = false)
    {
      var shown = scene.Root.CheckedMeshes().ToHashSet();
      return new ModelSource(scene.Meshes.Where(m => shown.Contains(m.Id)).ToList(), scene.Materials, scene.Textures, skinned) { Baked = baked };
    }
  }

  /// <summary>
  /// What an instance's owner sets on a material beyond the model's own values (#112 step 4d; GTA IV (PC): a cutscene
  /// vehicle's, see CutsceneDrawing): the second paint layer's colour, how dirty, whether its lamps are on, its
  /// reflectivity and its second shine (colour, exponent factor, strength).
  /// </summary>
  public sealed record MaterialLook
  {
    public Vector4? Paint2 { get; init; }
    public float DirtLevel { get; init; }
    public bool LightsOn { get; init; }
    public float? ReflectivePower { get; init; }
    public Vector3 Specular2Color { get; init; }
    public float Specular2Exponent { get; init; }
    public float Specular2Strength { get; init; }
  }

  /// <summary>
  /// The native renderer's drawing so far (#112 steps 1–4a): models on the GPU, each in one vertex and one index buffer
  /// with a part per mesh (its index range, its material's texture and how it is drawn), drawn as instances (a
  /// model-to-scene matrix each, and for a skinned model its bones' matrices). First everything solid (cut-out texels
  /// clipped), then decals a little in front and see-through materials, blended without writing depth. Lit by Lambert
  /// and an ambient term; only the first texture layer; glass and hidden planes left out for now. The view-projection,
  /// the instance's matrix, the light and the part's material go as root constants, the texture as a descriptor
  /// table, the bones' matrices as a structured buffer (t1). Draws into any target. No WinUI here: the visual tests'
  /// cutgen builds it too.
  /// </summary>
  public sealed class MeshRenderer : IDisposable
  {
    // The light of a solid pixel as the page's outdoor path (spec: the page's GameGlsl outsideLight, used for every
    // cutscene with a location, inside too): the light from around (above to below by the normal's z) x the darkening
    // (AO) + the sun x saturate((n.l - 0.25) x 4/3); AO from the vertex colours (a map object's red by day, green at
    // night; a ped's red; a vehicle's luminance; a tree 0.5; glowing materials 1) x the actor's AmbientDownMult, then
    // scaled by AoMultiplier (and PedAoMultiplier for peds). Glowing materials over or onto it. Written as light (the
    // frame's light target; ToneMapper makes the picture).
    private const string Shader = @"
struct Draw
{
  row_major float4x4 World;
  float4 Instance; // x: AmbientDownMult (actors), y: in a room (1), z: bone count (skinned)
  float4 Material; // x: cut out (1), y: alpha from the dirt mask (1), z: kind (bits), w: glow multiplier
  float4 DirtMask;
  float4 Tint;     // rgb: the texture multiplied by it (a vehicle's paint)
  float4 Part;     // x: the part's number in its model's Parts
};
ConstantBuffer<Draw> d : register(b0);
struct Frame
{
  row_major float4x4 ViewProjection;
  float4 Eye;
  float4 SunDirection;
  float4 Sun;
  float4 AmbientAbove;
  float4 AmbientBelow;
  float4 Params;   // x: day/night, y: exposure, z: PedAoMultiplier, w: AoMultiplier
  float4 Screen;   // xy: the target's size in pixels, z: the texture number of the surface below the decals (0: none)
  float4 Params2;  // x: the sun's share in the shine (gDirectionalLight.w)
  float4 FogNear;  // rgb: the near fog's colour (light), w: FogStart
  float4 FogFar;   // rgb: the far fog's colour (light), w: where it ends (FarClip)
  float4 FogParams;// x: NearFogAmount, y: fog on (1), z: how far the camera sees (depth along the view; 0: no limit)
  // The sun's shadow (see SunShadow, FrameLight.SunShadow): the light's axes u and v across (U.w: the fade's scale), h
  // towards the sun (w: the heights' reference), the view's forward (w: the map's texture number, 0: no shadow), the
  // depths along it from which the next cascade is read (w: no shadow from this distance), each cascade's box.
  float4 SunU;
  float4 SunV;
  float4 SunH;
  float4 SunForward;
  float4 SunSplits;
  float4 SunBox[4];
  // The fog pass's depth effect (FrameLight.DepthFx: DesaturationFar, GammaFar, from, to; none unless to > from, nor
  // without the light's copy).
  float4 DepthFx;
};
ConstantBuffer<Frame> f : register(b1);

// The fog (the page's gameFogAmount / gameFog, GTA IV's rage_postfx program 21): how much of its colour at a distance,
// and the light under it.
float FogAmount(float distance)
{
  if (f.FogParams.y < 0.5)
  {
    return 0;
  }
  float start = f.FogNear.w;
  float nearRamp = saturate(distance / max(start, 1e-4));
  float farRamp = saturate((distance - start) / max(f.FogFar.w - start, 1));
  return lerp(farRamp, nearRamp, f.FogParams.x) + (start > 0 ? 0 : f.FogParams.x);
}

// The fog pass's depth effect, before the fog (the page's gameFog, gtaiv-pc.js ~304-311; GTA IV's gDepthFxParams):
// from DepthFx.z to .w the light desaturated towards DesaturationFar and x luma^(ramp (GammaFar - 1)), the luma in the
// game's light (without the exposure). None unless DepthFx.w > DepthFx.z.
float3 DepthEffect(float3 light, float distance)
{
  if (f.DepthFx.w <= f.DepthFx.z)
  {
    return light;
  }
  float ramp = saturate((distance - f.DepthFx.z) / (f.DepthFx.w - f.DepthFx.z));
  float luma = dot(light, float3(0.2125, 0.7154, 0.0721));
  return lerp(luma.xxx, light, lerp(1.0, f.DepthFx.x, ramp)) * pow(max(luma / f.Params.y, 0) + 1e-7, ramp * (f.DepthFx.y - 1));
}

float3 FogColour(float distance)
{
  float start = f.FogNear.w;
  return lerp(f.FogNear.rgb, f.FogFar.rgb, saturate((distance - start) / max(f.FogFar.w - start, 1)));
}
// All textures (TextureCache's heap), by number (0 white); a part's numbers in its PartData.
Texture2D Tex[] : register(t0, space1);
SamplerState Linear : register(s0);
struct Bone { row_major float4x4 m; };
StructuredBuffer<Bone> Palette : register(t1);

// A part's material (#112 step 4d): its textures and the numbers the shading takes (see MeshRenderer.PartData).
struct PartData
{
  uint4 Tex;      // albedo, normal map, specular map, environment
  uint4 Tex2;     // layer 1 (or the second paint layer), layer 2, layer 3, dirt
  uint4 Bits;     // x: flags (below), y: texture layers (1 to 4), z: which layers have a texture (bits)
  float4 Spec;    // specular power, intensity, bumpiness, parallax
  float4 SpecMask;// xyz: the specular map's channels, w: reflectivity
  float4 Paint2;  // the second paint layer's colour
  float4 Dirt;    // rgb: the dirt's colour, w: how dirty
  float4 VShine;  // a vehicle shader's shine numbers (intensity, power, kind)
  float4 Spec2;   // rgb: the second shine's colour, w: its exponent factor
  float4 Sub;     // rgb: the skin's subsurface colour, w: its wrap
  float4 Misc;    // x: the second shine's strength, y: the subsurface band's width, z: a fixed alpha, w: lamp glass glow
};
StructuredBuffer<PartData> Parts : register(t2);

static const uint Baked = 1, Ped = 2, Vehicle = 4, Tree = 8, Emissive = 16, Night = 32, Strong = 64, GlowLit = 128;
static const uint FNormal = 1, FSpec = 2, FSpecFlat = 4, FEnv = 8, FEnvSpec = 16, FPaint2 = 32, FDirt = 64, FStipple = 128,
  FVehGlass = 256, FVehLights = 512, FDecalBelow = 1024, FDecalAoBelow = 2048, FForwardShine = 4096, FTextureAlpha = 8192,
  FTextured = 16384, FFixedAlpha = 32768, FForwardLit = 65536;

// The stipple's share kept per level (stipple.wtd all_stips: of 64) and a vehicle lamp's dimmers (by its vertex alpha).
static const float Kept[16] = { 0, 6, 8, 12, 11, 12, 16, 24, 32, 40, 48, 52, 52, 56, 58, 64 };
static const float Dimmers[15] = { 10, 10, 10, 9, 9, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR;
              float4 tangent : TANGENT; float4 layerUv : TEXCOORD1; float2 layerUv3 : TEXCOORD2; float4 weights : TEXCOORD3; };
struct VsSkinIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR;
                  float4 tangent : TANGENT; float4 layerUv : TEXCOORD1; float2 layerUv3 : TEXCOORD2; float4 weights : TEXCOORD3;
                  float4 bones : BLENDINDICES; float4 boneWeights : BLENDWEIGHT; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR; float3 world : WORLD;
               float4 tangent : TANGENT; float4 layerUv : TEXCOORD1; float2 layerUv3 : TEXCOORD2; float4 weights : TEXCOORD3; };

// (The page's vertex stage: a tangent's normal and tangent normalised unless skinned; without a tangent the normal
// as it is.)
VsOut Finish(float4 local, float3 normal, float4 tangent, bool skinned, VsIn input)
{
  VsOut output;
  float4 world = mul(local, d.World);
  output.position = mul(world, f.ViewProjection);
  float3 n = mul(normal, (float3x3)d.World);
  float3 t = mul(tangent.xyz, (float3x3)d.World);
  bool hasTangent = dot(t, t) > 1e-8;
  if (hasTangent && !skinned)
  {
    n = normalize(n + 1e-5);
    t = normalize(t + 1e-5);
  }
  output.normal = n;
  output.tangent = hasTangent ? float4(t, tangent.w) : 0;
  output.uv = input.uv;
  output.alpha = input.alpha;
  output.color = input.color;
  output.world = world.xyz;
  output.layerUv = input.layerUv;
  output.layerUv3 = input.layerUv3;
  output.weights = input.weights;
  return output;
}

VsOut VSMain(VsIn input)
{
  return Finish(float4(input.position, 1), input.normal, input.tangent, false, input);
}

// A vertex moved by up to four bones (rest to now); one with no weights, or an instance without its bones, stays as
// it is. Bone numbers are kept within the instance's palette (root descriptors have no bounds check).
void Skin(float3 position, float3 normal, float3 tangent, float4 bones, float4 weights, out float4 p, out float3 n, out float3 t)
{
  p = float4(position, 1);
  n = normal;
  t = tangent;
  uint count = (uint)d.Instance.z;
  if (count > 0 && dot(weights, 1) > 0)
  {
    uint4 b = min((uint4)(bones + 0.5), count - 1);
    float4x4 m = Palette[b.x].m * weights.x + Palette[b.y].m * weights.y +
                 Palette[b.z].m * weights.z + Palette[b.w].m * weights.w;
    p = mul(p, m);
    n = mul(n, (float3x3)m);
    t = mul(t, (float3x3)m);
  }
}

VsOut VSSkinned(VsSkinIn input)
{
  float4 p;
  float3 n, t;
  Skin(input.position, input.normal, input.tangent.xyz, input.bones, input.boneWeights, p, n, t);
  VsIn plain;
  plain.position = input.position;
  plain.normal = input.normal;
  plain.uv = input.uv;
  plain.alpha = input.alpha;
  plain.color = input.color;
  plain.tangent = input.tangent;
  plain.layerUv = input.layerUv;
  plain.layerUv3 = input.layerUv3;
  plain.weights = input.weights;
  return Finish(p, n, float4(t, input.tangent.w), true, plain);
}

// A lamp's shadow map (#112 step 5), as GTA IV draws one (the page's lampShadowVertex/lampShadowFragment, gtaiv-pc.js;
// the TLAD trace GTAIV.3: VS 0x25afd220, PS 0x25d42d60): of a point's offset L from the lamp in the lamp's frame (the
// world's x the axis, y and -z across) and s = -side x L.z, the place L.xy / (|L| - s), the depth |L| / radius; the
// pixel keeps only what is on its half (s / radius at most 0.01) and stores |L| / radius as it came from the vertices.
// Here d.Tint holds the lamp (xyz) and its radius (w), d.Material.x the half (-1 or 1).
struct ShIn { float3 position : POSITION; };
struct ShSkinIn { float3 position : POSITION; float4 bones : BLENDINDICES; float4 boneWeights : BLENDWEIGHT; };
struct ShOut { float4 position : SV_Position; float2 lamp : LAMP; };

ShOut ShadowFinish(float4 local)
{
  float3 off = mul(local, d.World).xyz - d.Tint.xyz;
  float3 l = float3(off.y, -off.z, off.x);
  float len = length(l);
  float s = -d.Material.x * l.z;
  ShOut o;
  o.position = float4(l.xy / max(len - s, 1e-6), len / d.Tint.w, 1);
  o.lamp = float2(s, len) / d.Tint.w;
  return o;
}

ShOut VSShadow(ShIn input)
{
  return ShadowFinish(float4(input.position, 1));
}

ShOut VSShadowSkinned(ShSkinIn input)
{
  float4 p;
  float3 n, t;
  Skin(input.position, 0, 0, input.bones, input.boneWeights, p, n, t);
  return ShadowFinish(p);
}

float PSShadow(ShOut input) : SV_Target
{
  if (input.lamp.x > 0.01)
  {
    discard;
  }
  return input.lamp.y;
}

// Drawing into a cascade of the sun's shadow map (the page's shadowVertex / shadowFragment, gtaiv-pc.js; GTA IV's
// shadow VS 0x247AF240 and PS 0x23CFA7A0 in the TLAD trace): across, the point on the cascade's u and v (d.Tint: the
// box's centre and 1 / half its size), the depth from its height along the sun (the highest kept; 1 km either way of
// the reference, GL's -1..1 here 0..1), stored: that height less the cascade's bias (d.Material.x). A see-through, decal
// or cut-out material blended in by its texture's alpha (d.Material.y 1; else alpha 1).
struct SunIn { float3 position : POSITION; float2 uv : TEXCOORD0; };
struct SunSkinIn { float3 position : POSITION; float2 uv : TEXCOORD0; float4 bones : BLENDINDICES; float4 boneWeights : BLENDWEIGHT; };
struct SunOut { float4 position : SV_Position; float up : UP; float2 uv : TEXCOORD0; };

SunOut SunFinish(float4 local, float2 uv)
{
  float3 p = mul(local, d.World).xyz;
  float up0 = dot(p, f.SunH.xyz) - f.SunH.w;
  SunOut o;
  o.position = float4((dot(p, f.SunU.xyz) - d.Tint.x) * d.Tint.z, (dot(p, f.SunV.xyz) - d.Tint.y) * d.Tint.w,
                      clamp(-up0 / 1000.0, -1.0, 1.0) * 0.5 + 0.5, 1);
  o.up = up0;
  o.uv = uv;
  return o;
}

SunOut VSSun(SunIn input)
{
  return SunFinish(float4(input.position, 1), input.uv);
}

SunOut VSSunSkinned(SunSkinIn input)
{
  float4 p;
  float3 n, t;
  Skin(input.position, 0, 0, input.bones, input.boneWeights, p, n, t);
  return SunFinish(p, input.uv);
}

float4 PSSun(SunOut input) : SV_Target
{
  float a = 1;
  if (d.Material.y > 0.5)
  {
    a = saturate(Tex[Parts[(uint)d.Part.x].Tex.x].Sample(Linear, input.uv).a);
  }
  return float4(input.up - d.Material.x, 0, 0, a);
}

// The light target and the G-buffer the lamps are added over (#112 step 5): what the lamps' light is multiplied by
// (the albedo; a glowing material laid over by its alpha only by the rest), the normal and AO the light was made with,
// the place (w: 1 written, 2 written and baked: the game's stencil for its cutscene lights).
struct PsOut { float4 light : SV_Target0; float4 albedo : SV_Target1; float4 normal : SV_Target2; float4 place : SV_Target3; };

// The page's pedBumpedRaw (step 4d): a tangent-space normal map (DXT5nm's x in alpha found per pixel), z from the raw
// 0..1 values (0.707 when flat), bent by bumpiness along the tangent and binormal.
float3 PedBumped(float3 nIn, float4 t, float2 uv, float bumpiness, uint map)
{
  float4 raw = Tex[map].Sample(Linear, uv);
  float2 xy = (1 - raw.w - raw.x >= 0) ? raw.wy : raw.xy;
  float2 k = (xy - 0.5) * bumpiness;
  float z = sqrt(abs(1 - dot(xy, xy)));
  float3 b = cross(t.xyz, nIn) * t.w;
  return normalize(nIn * z + t.xyz * k.x + b * k.y + 1e-5);
}

// The page's gBufferRounded: the normal as GTA IV's G-buffer stores it (64 steps a channel, 10 bits).
float3 Rounded(float3 g)
{
  float3 s = round(floor(64 * (g + 1)) / 128 * 1023) / 1023;
  return normalize(s * 2 - 0.99999);
}

// How much of the sun reaches the point P (0 to 1), the page's gameSunShadow (gtaiv-pc.js; GTA IV's
// deferred_lighting.fxc sun pass, program 21): the cascade by the point's depth along the view (SunSplits), its place
// in that cascade's quarter of the map, twelve taps round it (the game's c8 to c13, x 1 / 2048 of the map), each lit
// where the point is as high along the sun as what is stored there or higher; their share plus FadeScale (distance /
// FadeEnd)², saturated; 1 from FadeEnd on. The map is the four cascades side by side, 2048 x 2048 each. The page's
// coordinates are GL's (a map's rows from the bottom): here its rows turned over (a cascade's NDC y up is row 0 at the
// bottom there, at the top here), the same texels read, clamped to the whole map's edge as the page's CLAMP_TO_EDGE.
static const float2 SunTaps[12] = { float2(-0.326212, -0.40581), float2(-0.840144, -0.07358), float2(-0.695914, 0.457137),
  float2(-0.203345, 0.620716), float2(0.96234, -0.194983), float2(0.473434, -0.480026), float2(0.519456, 0.767022),
  float2(0.185461, -0.893124), float2(0.507431, 0.064425), float2(0.89642, 0.412458), float2(-0.32194, -0.932615), float2(-0.791559, -0.59771) };
static const int SunMapSize = 2048;

float SunShadow(float3 P)
{
  uint map = (uint)(f.SunForward.w + 0.5);
  if (map == 0)
  {
    return 1;
  }
  float dist = length(P - f.Eye.xyz);
  if (dist >= f.SunSplits.w)
  {
    return 1;
  }
  float depth = dot(P - f.Eye.xyz, f.SunForward.xyz);
  int k = (depth >= f.SunSplits.x ? 1 : 0) + (depth >= f.SunSplits.y ? 1 : 0) + (depth >= f.SunSplits.z ? 1 : 0);
  float4 box = f.SunBox[k];
  float2 at = float2((dot(P, f.SunU.xyz) - box.x) * box.z, (dot(P, f.SunV.xyz) - box.y) * box.w) * 0.5 + 0.5;
  float up0 = dot(P, f.SunH.xyz) - f.SunH.w;
  float lit = 0;
  [unroll] for (int i = 0; i < 12; i++)
  {
    float2 t = at + SunTaps[i] / SunMapSize;
    int x = clamp((int)floor((k + t.x) * 0.25 * (4 * SunMapSize)), 0, 4 * SunMapSize - 1);
    int y = clamp((int)floor(t.y * SunMapSize), 0, SunMapSize - 1);
    float stored = Tex[map].Load(int3(x, SunMapSize - 1 - y, 0)).r;
    lit += up0 >= stored ? 1 : 0;
  }
  float away = dist / f.SunSplits.w;
  return saturate(lit / 12 + f.SunU.w * away * away);
}

// The mirrored surroundings in the direction r (the page's mirroredAround, gtaiv-pc.js ~324-342): the map (texture
// f.Params2.z; 0: none, nothing) at 0.5 + r.xy / (2 (1 + saturate(r.z))) - the page's GL rows from the bottom, here
// turned over -, level lod (0 to 3), x the game's darkening of its smaller levels (1, 0.8409, 0.6389, 0.4518 between
// them). Not ported: the page's + (1 - map alpha) x the sky's colour where the map has no alpha (only at its rim's
// blurred levels; the map's circle is the sky's, alpha 1).
SamplerState LinearClamp : register(s1);
float3 MirroredAround(float3 r, float lod)
{
  uint map = (uint)(f.Params2.z + 0.5);
  if (map == 0)
  {
    return 0;
  }
  float level = clamp(lod, 0, 3);
  float2 at = r.xy / (2 * (1 + saturate(r.z)));
  float4 m = Tex[map].SampleLevel(LinearClamp, float2(0.5 + at.x, 0.5 - at.y), level);
  float darker = level < 1 ? lerp(1.0, 0.8409, level) : level < 2 ? lerp(0.8409, 0.6389, level - 1) : lerp(0.6389, 0.4518, level - 2);
  return m.rgb * darker;
}

// The light of a surface (no albedo): the light from around by the normal's z x AO + the sun past n.l 0.25, x its
// shadow (the page's outsideLight: sunColor x sunLit x sunShadow).
float3 LitBy(float3 n, float ao, float sunShadow)
{
  float3 around = lerp(f.AmbientAbove.rgb, f.AmbientBelow.rgb, saturate(0.5 - 0.5 * n.z));
  float sunLit = saturate((dot(n, f.SunDirection.xyz) - 0.25) * 1.3333333);
  return around * ao + f.Sun.rgb * sunLit * sunShadow;
}

// The frame's lights (see DrawLights: place and range, axis and cos outer, colour and cone scale, model / in a room /
// shadow / mask), read by the lamps' pass and by the map's see-through materials lit forward (Shade).
struct Light { float4 At; float4 Dir; float4 Color; float4 Kind; };
StructuredBuffer<Light> Lights : register(t3);

// deferred: the solid pass and the decals (G-buffer); else the see-through pass (forward, blended).
PsOut Shade(VsOut input, bool deferred, bool solid = false)
{
  // Beyond the camera's reach nothing (the page's drawReach: the depth along the view, here the clip w).
  if (f.FogParams.z > 0 && input.position.w > f.FogParams.z)
  {
    discard;
  }
  PartData p = Parts[(uint)d.Part.x];
  uint bits = p.Bits.x;
  uint kind = (uint)(d.Material.z + 0.5);
  bool baked = (kind & Baked) != 0, ped = (kind & Ped) != 0, vehicle = (kind & Vehicle) != 0, tree = (kind & Tree) != 0;
  bool emissive = (kind & Emissive) != 0;
  bool textured = (bits & FTextured) != 0, normalMap = (bits & FNormal) != 0, specMap = (bits & FSpec) != 0;
  bool textureAlpha = (bits & FTextureAlpha) != 0;
  bool tangentBranch = dot(input.tangent.xyz, input.tangent.xyz) > 0.25;
  // Parallax (gta_parallax_specmap): the texture and the normal map at uv + (height x s - 0.5 s) x V.xy.
  float2 uvP = input.uv;
  if (p.Spec.w > 0 && normalMap && tangentBranch)
  {
    float3 pn = normalize(input.normal), pt = normalize(input.tangent.xyz), pe = f.Eye.xyz - input.world;
    float3 pv = float3(dot(pt, pe), dot(cross(pt, pn) * input.tangent.w, pe), dot(pn, pe));
    uvP += (Tex[p.Tex.y].Sample(Linear, input.uv).a * p.Spec.w - 0.5 * p.Spec.w) * pv.xy / max(length(pv), 1e-5);
  }
  float4 texel = Tex[p.Tex.x].Sample(Linear, uvP);
  float3 albedo = texel.rgb * d.Tint.rgb;
  float baseA = texel.a;
  // gta_vehicle_paint3's second layer over it by its alpha (on the second texture coordinates).
  if ((bits & FPaint2) != 0)
  {
    float4 l = Tex[p.Tex2.x].Sample(Linear, input.layerUv.xy) * p.Paint2;
    albedo = lerp(albedo, l.rgb, saturate(l.a));
  }
  float3 vc = input.color;
  if (vehicle)
  {
    albedo *= vc;
  }
  // A vehicle's dirt: towards the dirt colour by the dirt texture's red x how dirty.
  float dirtAmount = 0;
  if (vehicle && (bits & FDirt) != 0 && p.Dirt.w > 0)
  {
    float dd = Tex[p.Tex2.w].Sample(Linear, input.uv).r;
    dirtAmount = dd * p.Dirt.w;
    if (dd > 0)
    {
      albedo = lerp(albedo, p.Dirt.rgb, dirtAmount);
    }
  }
  // Terrain: each layer over the ones below by its alpha x its weight (gta_terrain_va_*lyr).
  uint layers = p.Bits.y;
  if (layers > 1)
  {
    if ((p.Bits.z & 1) != 0)
    {
      float4 l = Tex[p.Tex2.x].Sample(Linear, input.layerUv.xy);
      albedo = lerp(albedo, l.rgb, saturate(l.a * input.weights.y));
    }
    if (layers > 2 && (p.Bits.z & 2) != 0)
    {
      float4 l = Tex[p.Tex2.y].Sample(Linear, input.layerUv.zw);
      albedo = lerp(albedo, l.rgb, saturate(l.a * input.weights.z));
    }
    if (layers > 3 && (p.Bits.z & 4) != 0)
    {
      float4 l = Tex[p.Tex2.z].Sample(Linear, input.layerUv3);
      albedo = lerp(albedo, l.rgb, saturate(l.a * input.weights.w));
    }
    baseA = 1;
  }
  float alpha = baseA * input.alpha;
  // A dirt decal: the vertices' colour where the texture's channels in the mask say.
  if (d.Material.y > 0.5)
  {
    alpha = dot(texel.rgb, d.DirtMask.rgb) * input.alpha;
    baseA = alpha;
    albedo = vc;
  }
  // Cut out: the page's test (texture alpha x vertex alpha at most 45/255 goes; a tree by half, for its stipple).
  if (d.Material.x > 0.5)
  {
    clip(alpha - (tree ? 0.5 : 0.17648));
  }
  // The normal: peds and vehicles turned to the camera (seen from inside: the side facing it), then bent by the
  // normal map: with tangents the tangent frame (and, written to the G-buffer, rounded as the game stores it);
  // without (vehicles, map meshes without tangents) the page's screen-space frame, whose bumpiness is 0 there: it
  // comes down to the normal x sign(2 blue - 1) (the page's NaN at blue 0.5 kept as the normal).
  float3 n = normalize(input.normal + 1e-6);
  if (!tree && !baked && dot(n, f.Eye.xyz - input.world) < 0)
  {
    n = -n;
  }
  float3 envN = n;
  if (normalMap)
  {
    if (tangentBranch)
    {
      envN = PedBumped(dot(n, input.normal) < 0 ? -input.normal : input.normal, input.tangent, uvP, p.Spec.z, p.Tex.y);
      n = textureAlpha ? envN : Rounded(envN);
    }
    else
    {
      float b = Tex[p.Tex.y].Sample(Linear, uvP).b;
      n *= 2 * b - 1 < 0 ? -1 : 1;
      envN = n;
    }
  }
  float3 view = normalize(input.world - f.Eye.xyz);
  float4 spec = specMap ? Tex[p.Tex.z].Sample(Linear, input.uv) : 1;
  float reflective = p.SpecMask.w;
  // A map *_reflect shader's environment texture added to the albedo (by the specular map x intensity, or alone) x
  // reflectivity; see-through, divided by the alpha first.
  // (DebugView 99: the environment texture left out, for comparisons.)
  if ((bits & FEnv) != 0 && !vehicle && (uint)(f.Params2.w + 0.5) != 99)
  {
    float3 r = reflect(view, envN);
    float3 env = Tex[p.Tex.w].Sample(Linear, -0.5 * (r.xz / max(length(r), 1e-5) + 1)).rgb
               * ((bits & FEnvSpec) != 0 ? dot(spec.rgb, p.SpecMask.rgb) * p.Spec.y : 1) * reflective;
    if (textureAlpha)
    {
      float seen = baseA * input.alpha;
      env *= seen >= 0.01 ? 1 / seen : 100;
    }
    albedo += env;
  }
  float dayNight = f.Params.x;
  float ao = tree ? 0.5
           : emissive ? 1.0
           : vehicle ? saturate(dot(vc, float3(0.2125, 0.7154, 0.0721)))
           : saturate(baked ? dayNight * vc.r + (1 - dayNight) * vc.g : vc.r);
  ao *= d.Instance.x;
  if (!tree && !vehicle && !emissive)
  {
    ao = 1 + (ped ? f.Params.z : 1) * f.Params.w * (ao - 1);
  }
  // A decal under the surface's light (its normal and AO there), or only its AO (normal decals).
  float4 below = 0;
  if ((bits & (FDecalBelow | FDecalAoBelow)) != 0 && f.Screen.z > 0.5)
  {
    below = Tex[(uint)f.Screen.z].Load(int3(input.position.xy, 0));
    if ((bits & FDecalAoBelow) != 0)
    {
      ao = below.w;
    }
  }
  // The sun's shadow at the point (1 without one), on the sun's light and its shine (the page's sunShadow).
  float sunShadow = SunShadow(input.world);
  float3 lit = LitBy(n, ao, sunShadow);
  if ((bits & FDecalBelow) != 0 && f.Screen.z > 0.5)
  {
    lit = LitBy(normalize(below.xyz + 1e-6), below.w, sunShadow);
  }
  // The sun's shine (the page's vehicleShine and pedShine, sun parts; the mirrored surroundings and the lamps' shine
  // not yet).
  float3 R = reflect(view, n);
  float sunR = saturate(dot(R, f.SunDirection.xyz));
  float shineScale = f.Params2.x;
  float3 vehicleShine = 0, shine = 0;
  // The lamps' shine into the G-buffer (the page's lampShine with 2 x shineAmount, gtaiv-pc.js ~234-237 and
  // ModelView.html ~558-660): its amount (albedo's alpha) and power (in the place's w), read by PSLight.
  float gShine = 0, gPower = 0;
  bool vehicleGlassShader = p.VShine.z > 0.5 && p.VShine.z < 1.5;
  if (vehicle && (vehicleGlassShader || (specMap && textured)) && reflective > 0)
  {
    float4 vs = specMap && textured ? spec : 1;
    float strength = dot(vs.rgb, p.SpecMask.rgb) * p.VShine.x * reflective;
    if (p.VShine.z > 1.5)
    {
      strength = min(strength, 1);
    }
    float power = vs.a * p.VShine.y * reflective;
    float3 sunShine = f.Sun.rgb * sunShadow * pow(sunR + 1e-4, power + 1e-4);
    // The mirrored surroundings (the page's vehicleShine, gtaiv-pc.js ~399-453): x the vertex colour's luminance,
    // globalScalars.w (f.Params2.y).
    float vehicleAo = dot(vc, float3(0.2125, 0.7154, 0.0721));
    if (vehicleGlassShader || p.VShine.z > 2.5)
    {
      float fr = pow(1 - abs(dot(view, n)), 4);
      float s = min(strength, 1) * (1 - dirtAmount) * saturate(0.5 * fr + 0.5);
      vehicleShine = s * (vehicleGlassShader ? 4 : 1) * shineScale * sunShine
                   + s * s * vehicleAo * MirroredAround(R, 0) * saturate(R.z * 5) * (1.6 * fr + 0.4)
                     * (vehicleGlassShader ? 36 * f.Params2.y : 18) * reflective;
    }
    else
    {
      float away = length(input.world - f.Eye.xyz);
      float3 mapDir = normalize(R + view * saturate(away * 0.02));
      float fresnel = 0.75 * pow(1 - abs(dot(view, n)), 4) + 0.25;
      vehicleShine = strength * (1 - dirtAmount) * (sunShine * 2 * shineScale
                   + 10 * f.Params2.y * vehicleAo * fresnel * MirroredAround(mapDir, 4 - 4 * saturate(power / 230))
                     * saturate(mapDir.z * 5) * (1 - saturate(away * 0.01)));
    }
    // gta_vehicle_paint2's second shine, into the colour (lit with it).
    if (p.Misc.x > 0)
    {
      float e = max(p.Spec2.w * vs.a * reflective, 1);
      float glint = dot(vs.rgb, p.SpecMask.rgb) * reflective * p.Misc.x * pow(max(dot(R, f.SunDirection.xyz), 0), e);
      albedo += p.Spec2.rgb * glint;
    }
  }
  if (!vehicle && (specMap || (bits & FSpecFlat) != 0) && textured)
  {
    float amount = (specMap ? dot(spec.rgb, p.SpecMask.rgb) : 1) * p.Spec.y;
    if ((bits & FForwardShine) != 0)
    {
      // Forward (glass, see-through map materials, a ped's see-through parts): no halving, no 0.75 on the power.
      shine = amount * f.Sun.rgb * sunShadow * pow(sunR + 1e-4, spec.a * p.Spec.x + 1e-4) * shineScale;
    }
    else
    {
      // The G-buffer's (deferred_lighting's sun pass): 2 x GB2.x x the sun x 2 gDirectionalLight.w.
      float power = spec.a * p.Spec.x * (baked ? 1 : 0.75);
      gShine = 2 * (baked ? 0.5 : 0.25) * amount;
      gPower = power;
      // + the mirrored surroundings (the page's pedShine, gtaiv-pc.js ~215-235): 10 x globalScalars.w x AO x Fresnel x
      // the map along R + v x saturate(distance x 0.02), blurred by the power, x saturate(5 R'.z) x (1 - saturate(d / 100)).
      float away = length(input.world - f.Eye.xyz);
      float3 mapDir = normalize(R + view * saturate(away * 0.02));
      float fresnel = 0.75 * pow(1 - abs(dot(view, n)), 4) + 0.25;
      shine = 2 * (baked ? 0.5 : 0.25) * amount * (f.Sun.rgb * sunShadow * pow(sunR + 1e-4, power + 1e-4) * 2 * shineScale
            + 10 * f.Params2.y * ao * fresnel * MirroredAround(mapDir, 4 - 4 * saturate(power / 230)) * saturate(mapDir.z * 5)
              * (1 - saturate(away * 0.01)));
    }
  }
  // A map's see-through material drawn forward (the page's forwardLit, ModelView.html ~558-660 and ~3497-3508): the
  // location's lamps reaching more than 4 m, not a cutscene's own lights (GTAIV.exe 0xAC5670 / 0xABD8A0), each its
  // colour x reach x spot x max(n.l, 0) onto the light, its shine x saturate(n.l) x |l.R|^(power / 4) x the shine's
  // amount onto the shine. f.FogParams.w: the frame's lights (0 in the mirror).
  if (!deferred && (bits & FForwardLit) != 0 && f.FogParams.w > 0.5)
  {
    float3 lampMirrored = reflect(view, n);
    bool shiny = !vehicle && (specMap || (bits & FSpecFlat) != 0) && textured;
    float lampPower = shiny ? (specMap ? spec.a : 1) * p.Spec.x * (baked ? 1 : 0.75) : 0;
    float3 lampLit = 0, lampShine = 0;
    uint count = (uint)(f.FogParams.w + 0.5);
    for (uint li = 0; li < count; li++)
    {
      Light l = Lights[li];
      float range = l.At.w;
      if ((uint)(l.Kind.x + 0.5) == 2 || range <= 4)
      {
        continue;
      }
      float3 L = l.At.xyz - input.world;
      float dl = length(L);
      if (dl >= range || dl < 1e-4)
      {
        continue;
      }
      L /= dl;
      float al = saturate(1 - dl * dl / (range * range));
      float reach = max((al * al * al * al - 0.1) / 0.9, 0);
      float spotL = l.Dir.w > -1 ? saturate((dot(-L, l.Dir.xyz) - l.Dir.w) * l.Color.w) : 1;
      float3 lampLight = l.Color.rgb * reach * spotL;
      lampLit += lampLight * max(dot(n, L), 0);
      if (lampPower > 0)
      {
        lampShine += lampLight * saturate(dot(n, L)) * pow(abs(dot(L, lampMirrored)), lampPower * 0.25);
      }
    }
    lit += lampLit;
    if (shiny)
    {
      shine += (specMap ? dot(spec.rgb, p.SpecMask.rgb) : 1) * p.Spec.y * lampShine;
    }
  }
  // How much of it shows (the see-through pass and decals): the texture's alpha x the vertex's (a vehicle lamp's
  // vertex alpha is its lamp's number), a person's see-through part by its stipple's share; a fixed alpha (0.45) for
  // glass without its own.
  float a = alpha;
  if (!deferred && (bits & FFixedAlpha) != 0)
  {
    a = p.Misc.z;
  }
  else if (textureAlpha)
  {
    a = (bits & FVehLights) != 0 ? baseA : alpha;
    if (a < 0.01)
    {
      discard;
    }
    if ((bits & FStipple) != 0)
    {
      float level = min(a, 1) * 3.996;
      int index = (int)floor(level) * 4 + (int)floor(frac(level) * 4);
      a *= Kept[index] / 64;
    }
  }
  // Skin (gta_ped_skin): SubColor x the texture's alpha in a band at the edge of the sun's light.
  if (p.Misc.y > 0)
  {
    float x = max((dot(n, -f.SunDirection.xyz) + p.Sub.w) / (1 + p.Sub.w), 0);
    float band = smoothstep(0, p.Misc.y, x) * (1 - smoothstep(p.Misc.y, 2 * p.Misc.y, x));
    albedo += 0.5 * band * p.Sub.rgb * baseA;
  }
  // A vehicle's glass: its shine divided by its alpha (the blend multiplies it back: it shows whole).
  float glassShine = (bits & FVehGlass) != 0 ? (a >= 0.01 ? 1 / a : 100) : 1;
  float3 rgb = albedo * lit + vehicleShine * glassShine + shine;
  float3 lampAlbedo = albedo;
  if (emissive)
  {
    // gta_emissive*: texture x vertex colour x its multiplier x the exposure (night ones only at night, not in a room);
    // gta_emissive laid over by the texture's alpha, the strong and night ones added.
    bool night = (kind & Night) != 0;
    float glow = d.Material.w * f.Params.y * (night ? (d.Instance.y > 0.5 ? 0 : 1 - dayNight) : 1);
    float3 lightOf = texel.rgb * d.Tint.rgb * vc * glow;
    if (!night && (kind & Strong) == 0)
    {
      float over = saturate(texel.a * input.alpha);
      rgb = lerp(rgb, lightOf, over);
      // (The page's mix(base x lit, glow, a): the lamps' light too only by 1 - a.)
      lampAlbedo *= 1 - over;
    }
    else
    {
      rgb += lightOf;
    }
  }
  // A see-through gta_emissive (its glow drawn apart, PSGlow): its lit draw takes the page's mix(lit, glow 0, alpha)
  // (ModelView.html ~704 with emissiveGlow 0, ~3645), so the lit surface only by 1 - its alpha.
  if ((kind & GlowLit) != 0)
  {
    rgb *= 1 - saturate(texel.a * input.alpha);
  }
  // A vehicle's lamp glass (gta_vehicle_lightsemissive): texture x vertex colour x its lamp's dimmer x the glow
  // (lamps on: emissiveMultiplier) x the exposure.
  if ((bits & FVehLights) != 0)
  {
    rgb += albedo * vc * Dimmers[clamp((int)(input.alpha * 255 + 0.5), 0, 14)] * p.Misc.w * f.Params.y;
  }
  // (Debug views, MeshRenderer.DebugView in f.Params2.w: 1 the albedo, 2 the normal (0.5 + 0.5 n), 3 AO, 4 the light
  // without albedo, 5 the texture's alpha x the vertex's, 6 the vertex colour - each x the exposure, for the picture.)
  uint debugView = (uint)(f.Params2.w + 0.5);
  if (debugView != 0 && debugView != 99)
  {
    float3 dv = debugView == 1 ? albedo : debugView == 2 ? 0.5 + 0.5 * n : debugView == 3 ? ao.xxx : debugView == 4 ? lit
              : debugView == 5 ? alpha.xxx : debugView == 6 ? vc : debugView == 7 ? texel.rgb
              : debugView == 9 ? Tex[p.Tex.x].SampleLevel(Linear, uvP, 0).rgb : debugView == 10 ? float3(frac(input.uv), 0)
              : float3((bits & FPaint2) != 0 ? 1 : 0, (bits & FEnv) != 0 ? 1 : 0, layers > 1 ? 1 : 0);
    rgb = dv * (debugView == 4 ? 1 : f.Params.y);
  }
  PsOut o;
  o.light = float4(rgb, deferred ? alpha : a);
  // (A decal blends its albedo by its alpha into the surface's, keeping the surface's normal, AO and place: as the
  // game's decals write GB0's colour only, the page's outBelow.)
  // The solid pass also writes the lamps' shine (albedo's alpha: its amount; place.w = 1 or 2 (baked) + 4 x the power
  // in 64ths, see PSLight); a decal's alpha blends its albedo and keeps the surface's shine x (1 - its alpha).
  o.albedo = float4(lampAlbedo, solid ? gShine : a);
  o.normal = float4(n, ao);
  o.place = float4(input.world, (baked ? 2 : 1) + 4 * floor(min(gPower, 60000) * 64));
  return o;
}

PsOut PSMain(VsOut input)
{
  return Shade(input, true, true);
}

PsOut PSDecal(VsOut input)
{
  return Shade(input, true, false);
}

// The see-through materials and glass: the light target only (no lamps on them yet: the game lights them forward,
// its lamps over 4 m only; not ported).
float4 PSBlend(VsOut input) : SV_Target
{
  float4 light = Shade(input, false).light;
  // The fog on each (the page's gameFog per fragment; the solid scene's comes after the lamps, DrawFog).
  float distance = length(f.Eye.xyz - input.world);
  light.rgb = DepthEffect(light.rgb, distance);
  light.rgb += FogAmount(distance) * (FogColour(distance) - light.rgb);
  return light;
}

// A see-through glowing part's glow, drawn apart after its lit draw (the page's glowOnly draw, ModelView.html ~264-266,
// ~700, ~719, ~734, ~3709-3734): pushed in depth by the game's zShift (d.Part.y: +0.0004 gta_emissivestrong, -0.0004
// the others; the page's 2 x zShift on GL's -w..w is this on Direct3D's 0..w); texture x tint x vertex colour x the
// glow (d.Material.w: emissiveMultiplier x exposure, a night one's outside a room x (1 - day/night)), x (1 - the fog's
// amount) (the game adds its glows before its fog pass), premultiplied by the part's alpha as its see-through draw has it.
VsOut VSGlow(VsIn input)
{
  VsOut o = VSMain(input);
  o.position.z += d.Part.y;
  return o;
}

VsOut VSGlowSkinned(VsSkinIn input)
{
  VsOut o = VSSkinned(input);
  o.position.z += d.Part.y;
  return o;
}

// A lamp's corona (#112; the page's gameCoronas, gtaiv-pc.js ~1721-1825; the game's deferred_lighting technique corona,
// the bc_1 trace GTAIV.trace draw 114115754): a quad facing the camera, half a side d.Tint.w round d.DirtMask.xyz (the
// camera's right, up and forward in d.World's rows 0-2), no depth test. Nearer to the camera than its size: x
// saturate(0.5 (f / size + 1)) for f, its depth along the view, below its size, and moved out to 0.1 m in front; else
// 12 points of the Poisson disc 2 pixels round its middle against the solid scene's depth (texture d.Part.y), x their
// share in front, collapsed to a point at 0.01 or less. The pixel: the sprite (texture d.Part.x) x colour (d.Tint.rgb:
// colour x strength x exposure), added (ONE / ONE). The page's GL depth (z / w x 0.5 + 0.5) is Direct3D's z / w here.
struct CoronaOut { float4 position : SV_Position; float2 uv : TEXCOORD0; float3 tint : COLOR; };
static const float2 CoronaCorners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(1, 1), float2(-1, 1), float2(-1, -1) };

CoronaOut VSCorona(uint id : SV_VertexID)
{
  float2 corner = CoronaCorners[id];
  float3 at = d.DirtMask.xyz;
  float size = d.Tint.w;
  float3 right = d.World[0].xyz, up = d.World[1].xyz, forward = d.World[2].xyz;
  float3 place = at + (right * corner.x + up * corner.y) * size;
  float seen = 1;
  if (length(at - f.Eye.xyz) < size)
  {
    float fd = dot(forward, at - f.Eye.xyz);
    if (fd < size)
    {
      seen = saturate(0.5 * (fd / size + 1));
      if (fd < 0.1)
      {
        place += forward * (0.1 - fd);
      }
    }
  }
  else
  {
    float4 clip = mul(float4(at, 1), f.ViewProjection);
    float3 ndc = clip.xyz / clip.w;
    float2 pixel = 1 / f.Screen.xy;
    uint depth = (uint)d.Part.y;
    seen = 0;
    [unroll] for (int i = 0; i < 12; i++)
    {
      float2 p = clamp(ndc.xy + pixel * SunTaps[i] * 2, -0.997, 0.997);
      int2 px = int2((p.x * 0.5 + 0.5) * f.Screen.x, (0.5 - 0.5 * p.y) * f.Screen.y);
      if (ndc.z < Tex[depth].Load(int3(px, 0)).r)
      {
        seen += 1.0 / 12;
      }
    }
    if (clip.w <= 0 || seen <= 0.01)
    {
      seen = 0;
      place = at;
    }
  }
  CoronaOut o;
  o.position = mul(float4(place, 1), f.ViewProjection);
  o.position.z = 0;
  o.tint = d.Tint.rgb * seen;
  o.uv = corner * 0.5 + 0.5;
  return o;
}

float4 PSCorona(CoronaOut input) : SV_Target
{
  return float4(Tex[(uint)d.Part.x].Sample(Linear, input.uv).rgb * input.tint, 0);
}

// A lamp's visible light volume (#112; the page's gameLightVolumes, gtaiv-pc.js ~1539-1710; GTA IV's deferred_lighting
// lightShafts, vs07 with ps18-ps21): the game's unit mesh (MeshRenderer.LightVolumeMesh) mapped round the lamp (d.World
// row 0: the lamp, w the outer angle in radians, 0 a point lamp; row 1: its axis, w the mesh's c213 scale; row 2: its
// second axis, w the size: scale x radius x 0.662; row 3: the eye from the lamp, w the radius), per pixel along the ray
// from the eye to the face, cut where the scene's depth (texture d.Part.x) is nearer: by the lookup texture (d.Part.y;
// d.Tint.w 1) or the chord through a sphere of 0.66 x the radius; x colour (d.Tint.rgb), the far faces adding, the
// near ones taking off (the faces' winding as the page's: GL's CCW front is FrontCounterClockwise here).
struct VolumeOut { float4 position : SV_Position; float3 ray : RAY; float w : CLIPW; };

VolumeOut VSVolume(float3 unit : POSITION)
{
  float3 lampAt = d.World[0].xyz, lampDir = d.World[1].xyz, across = d.World[2].xyz;
  float cone = d.World[0].w, scale = d.World[1].w, size = d.World[2].w;
  float3 dd;
  if (cone <= 0)
  {
    dd = normalize(unit + 1e-5);
  }
  else
  {
    float xy = dot(unit.xy, unit.xy);
    if (xy >= 1e-4)
    {
      float a = cone * (1 - saturate(-unit.z));
      float c = cos(a);
      float k = sqrt((1 - c * c) / (xy + 1e-4)) * scale;
      dd = cross(across, lampDir) * (k * unit.y) + across * (k * unit.x) + lampDir * c;
    }
    else
    {
      dd = abs(unit.z) >= 1e-4 ? lampDir : 0;
    }
  }
  float3 world = lampAt + dd * size;
  VolumeOut o;
  o.ray = world - f.Eye.xyz;
  o.position = mul(float4(world, 1), f.ViewProjection);
  o.w = o.position.w;
  return o;
}

float4 PSVolume(VolumeOut input, bool front : SV_IsFrontFace) : SV_Target
{
  // The scene's depth along the view (the page's from GL's depth and the near and far planes; here from the
  // view-projection's z and w columns: z = k w + b, so w = b / (depth - k)).
  float depth = Tex[(uint)d.Part.x].Load(int3(input.position.xy, 0)).r;
  float3 cz = float3(f.ViewProjection[0][2], f.ViewProjection[1][2], f.ViewProjection[2][2]);
  float3 cw = float3(f.ViewProjection[0][3], f.ViewProjection[1][3], f.ViewProjection[2][3]);
  float k = dot(cz, cw) / dot(cw, cw);
  float b = f.ViewProjection[3][2] - k * f.ViewProjection[3][3];
  float sceneZ = b / (depth - k);
  float t = saturate(sceneZ / input.w);
  float len = length(input.ray);
  float3 dir = input.ray / max(len, 1e-5);
  float3 eyeFromLamp = d.World[3].xyz;
  float radius = d.World[3].w;
  float sEye = dot(dir, eyeFromLamp);
  float h = length(eyeFromLamp - dir * sEye);
  float fv;
  if (d.Tint.w > 0.5)
  {
    // ps18 / ps19: the eye and the point seen from the lamp, scaled by sqrt(K / (1 + K h²)), K = min(16 / radius², 16);
    // the angle between them looked up (u 0.125, v 0.5 + 0.5 cos), x π / (1.5 sqrt(1 + K h²)).
    float kk = min(16 / (radius * radius), 16);
    float s = sqrt(1 + kk * h * h);
    float3 p = (eyeFromLamp + input.ray * t) * (sqrt(kk) / s), e = eyeFromLamp * (sqrt(kk) / s);
    float c = dot(p, e) * rsqrt(dot(e, e) + 1e-5) * rsqrt(dot(p, p) + 1e-5);
    fv = Tex[(uint)d.Part.y].Sample(Linear, float2(0.125, c * 0.5 + 0.5)).r * 3.14159274 / (1.5 * s);
  }
  else
  {
    // ps20 / ps21.
    float sFace = len * t + sEye;
    float r = 0.66 * radius;
    float c = h < r ? sqrt(r * r - h * h) : 0;
    float x = clamp(sFace, -c, c) - clamp(sEye, -c, c);
    fv = x * x / (3 * r * r);
  }
  return float4(d.Tint.rgb * fv * (front ? -1 : 1), 0);
}

float4 PSGlow(VsOut input) : SV_Target
{
  if (f.FogParams.z > 0 && input.position.w > f.FogParams.z)
  {
    discard;
  }
  PartData p = Parts[(uint)d.Part.x];
  uint bits = p.Bits.x;
  float4 texel = Tex[p.Tex.x].Sample(Linear, input.uv);
  float a = 1;
  if ((bits & FFixedAlpha) != 0)
  {
    a = p.Misc.z;
  }
  else if ((bits & FTextureAlpha) != 0)
  {
    a = texel.a * input.alpha;
    if (a < 0.01)
    {
      discard;
    }
  }
  float3 rgb = texel.rgb * d.Tint.rgb * input.color * d.Material.w;
  rgb *= 1 - FogAmount(length(f.Eye.xyz - input.world));
  return float4(rgb * a, a);
}

// Into the water's mirror (DrawMirror; the page's reflection pass, ModelView.html ~423-505): the view mirrored in the
// water's level (f.Eye the mirrored eye), nothing under f.FogParams.w (the level less 0.515 m), nothing beyond the
// shot's draw distance f.FogParams.z along the camera's own forward f.Params2.yzw from the mirrored eye (the page's
// drawReach with its eyeAt mirrored, toEye not). The surface as the main pass takes it (texture x tint, layers, a dirt
// decal's alpha, the cut-out test), then lit as the game's ReflectTexture pass (GTAIV.1, PS 0x1A3F8A40): the texture x
// (the light from around x AO + the sun x saturate((n.l - 0.25) x 4/3)) by the vertex normal, AO from the vertex
// colours alone (a map object's red by day, green at night; else red), no shadow, no shine, no lamps, the far fog over
// it by the distance from the mirrored eye; premultiplied by texture alpha x vertex alpha (nothing where that is 0).
// toMap: into the mirrored surroundings' map instead (the page's parabolicFragment, gtaiv-pc.js ~126-145): nothing
// below the camera (f.FogParams.w its height), no draw distance, the same light and far fog, and as alpha the texture's
// x the vertex's x saturate((64 - distance) x 0.25), not premultiplied (blended SRCALPHA / INVSRCALPHA over the sky).
float4 MirrorSurface(VsOut input, bool toMap)
{
  if (input.world.z < f.FogParams.w)
  {
    discard;
  }
  if (!toMap && f.FogParams.z > 0 && dot(input.world - f.Eye.xyz, f.Params2.yzw) > f.FogParams.z)
  {
    discard;
  }
  PartData p = Parts[(uint)d.Part.x];
  uint bits = p.Bits.x;
  uint kind = (uint)(d.Material.z + 0.5);
  bool baked = (kind & Baked) != 0, tree = (kind & Tree) != 0;
  bool normalMap = (bits & FNormal) != 0;
  float2 uvP = input.uv;
  if (p.Spec.w > 0 && normalMap && dot(input.tangent.xyz, input.tangent.xyz) > 0.25)
  {
    float3 pn = normalize(input.normal), pt = normalize(input.tangent.xyz), pe = f.Eye.xyz - input.world;
    float3 pv = float3(dot(pt, pe), dot(cross(pt, pn) * input.tangent.w, pe), dot(pn, pe));
    uvP += (Tex[p.Tex.y].Sample(Linear, input.uv).a * p.Spec.w - 0.5 * p.Spec.w) * pv.xy / max(length(pv), 1e-5);
  }
  float4 texel = Tex[p.Tex.x].Sample(Linear, uvP);
  float3 albedo = texel.rgb * d.Tint.rgb;
  float baseA = texel.a;
  if ((bits & FPaint2) != 0)
  {
    float4 l = Tex[p.Tex2.x].Sample(Linear, input.layerUv.xy) * p.Paint2;
    albedo = lerp(albedo, l.rgb, saturate(l.a));
  }
  uint layers = p.Bits.y;
  if (layers > 1)
  {
    if ((p.Bits.z & 1) != 0)
    {
      float4 l = Tex[p.Tex2.x].Sample(Linear, input.layerUv.xy);
      albedo = lerp(albedo, l.rgb, saturate(l.a * input.weights.y));
    }
    if (layers > 2 && (p.Bits.z & 2) != 0)
    {
      float4 l = Tex[p.Tex2.y].Sample(Linear, input.layerUv.zw);
      albedo = lerp(albedo, l.rgb, saturate(l.a * input.weights.z));
    }
    if (layers > 3 && (p.Bits.z & 4) != 0)
    {
      float4 l = Tex[p.Tex2.z].Sample(Linear, input.layerUv3);
      albedo = lerp(albedo, l.rgb, saturate(l.a * input.weights.w));
    }
    baseA = 1;
  }
  float alpha = baseA * input.alpha;
  if (d.Material.y > 0.5)
  {
    alpha = dot(texel.rgb, d.DirtMask.rgb) * input.alpha;
    albedo = input.color;
  }
  if (d.Material.x > 0.5)
  {
    clip(alpha - (tree ? 0.5 : 0.17648));
  }
  // The vertex normal (the page's n before any bump), or the face's without one (Direct3D's screen y down: the
  // page's cross(dFdx, dFdy) turned round).
  float3 n = dot(input.normal, input.normal) > 1e-12 ? normalize(input.normal) : normalize(cross(ddy(input.world), ddx(input.world)));
  float3 around = lerp(f.AmbientAbove.rgb, f.AmbientBelow.rgb, saturate(0.5 - 0.5 * n.z));
  float sunM = saturate((dot(n, f.SunDirection.xyz) - 0.25) * 1.3333333);
  float dayNight = f.Params.x;
  float3 vc = input.color;
  float ao = saturate(baked ? dayNight * vc.r + (1 - dayNight) * vc.g : vc.r);
  float farM = saturate((length(input.world - f.Eye.xyz) - f.FogNear.w) / max(f.FogFar.w - f.FogNear.w, 1));
  float3 lit = lerp(albedo * (around * ao + f.Sun.rgb * sunM), f.FogFar.rgb, farM);
  float a = saturate(alpha);
  if (toMap)
  {
    a *= saturate((64 - length(input.world - f.Eye.xyz)) * 0.25);
  }
  if (a <= 0)
  {
    discard;
  }
  return toMap ? float4(lit, a) : float4(lit * a, a);
}

float4 PSMirror(VsOut input) : SV_Target
{
  return MirrorSurface(input, false);
}

// The mirrored surroundings' map (the page's parabolicVertex, gtaiv-pc.js ~115-125; the game's VS 0x22EC8A80): of a
// point's direction d from the camera (f.Eye) and its distance len, the place d.xy / (len + d.z), the depth 1 - 1 / (len
// + 1) (GL's 1 - 2 / (len + 1) on -1..1).
VsOut VSParabolic(VsIn input)
{
  VsOut o = VSMain(input);
  float3 dd = o.world - f.Eye.xyz;
  float len = length(dd);
  o.position = float4(dd.xy / max(len + dd.z, 1e-4), 1 - 1 / (len + 1), 1);
  return o;
}

float4 PSParabolic(VsOut input) : SV_Target
{
  return MirrorSurface(input, true);
}

// A level of the map from the one above (the page's generateMipmap): each texel the mean of its 2 x 2 (texture d.Part.x).
float4 VSFull(uint id : SV_VertexID) : SV_Position
{
  float2 uv = float2((id << 1) & 2, id & 2);
  return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PSHalve(float4 position : SV_Position) : SV_Target
{
  int2 at = int2(position.xy) * 2;
  uint src = (uint)d.Part.x;
  return (Tex[src].Load(int3(at, 0)) + Tex[src].Load(int3(at + int2(1, 0), 0)) +
          Tex[src].Load(int3(at + int2(0, 1), 0)) + Tex[src].Load(int3(at + int2(1, 1), 0))) * 0.25;
}

// The lamps (#112 step 5): one light volume per light, a box round its sphere drawn over the solid scene (its faces
// turned away from the camera only, with the depth test 'greater or equal': each pixel whose surface lies inside the
// box once), adding albedo x its light from the G-buffer - as GTA IV draws its lamps (deferred_lighting.fxc over its
// G-buffer, a volume per light; the page lit them forward, at most 80, issue #106). The formulas the page's solid
// shader has (ModelView.html, the light loop; gtaiv-pc.js lightFalloff): a = saturate(1 - d²/r²);
//  light family (ps06-ps09): (a⁴ - 0.1) / 0.9 x spot x saturate(n.l), a lamp outside x saturate(AO² + 0.5), past a
//    shadow (ps14) x saturate(0.5 AO⁴ + AO²), in a room no AO; a cutscene's light no AO, max(n.l, 0);
//  filler (ps10-ps12): 1.5 (a² - 1/3) x spot x saturate((n.l - 0.2) x 1.25), past a shadow (ps15) saturate(1.5 (n.l - 1/3)).
// A shadowed light with a map in the atlas: x what of it gets past (LampShadow). Not yet: the lamps' shine (no
// specular maps here yet).
// (The lights, struct Light and Lights at t3, are declared before Shade, which lights the map's see-through materials
// forward with them.)
Texture2D<float4> GAlbedo : register(t4);
Texture2D<float4> GNormal : register(t5);
Texture2D<float4> GPlace : register(t6);
// The shadow atlas: a row of 1024 x 1024 a slot (4), each light's halves side by side (the axis's negative side left).
Texture2D<float> LampShadows : register(t7);
static const int ShadowSize = 1024;

// How much of a lamp gets past its shadow at a point (the page's lampShadow, gtaiv-pc.js; deferred_lighting.fxc program
// 35): the point 0.1 m off along its normal in the lamp's frame, its place in its half's map, four reads round it turned
// by the pixel's own angle (frac(pixel . (3, 4.272)) of a turn) at 0.75, 0.25, 0.5 and 1 x 0.0029, each kept in the
// half; lit where the stored distance is at least the point's; their mean. The page's coordinates are GL's (its
// pixels' y and its maps' rows from the bottom): the same reads here, rows and the pixel's y turned over.
float LampShadow(int slot, float3 at, float range, float3 p, float3 n, float2 pixel)
{
  float3 off = p + n * 0.1 - at;
  float3 l = float3(off.y, -off.z, off.x);
  float len = length(l);
  float2 uv = l.xy / max(abs(l.z) + len, 1e-4) * 0.5 + 0.5;
  int side = l.z <= 0 ? 0 : 1;
  float2 g = float2(pixel.x, f.Screen.y - 1 - pixel.y);
  float turn = frac(dot(g, float2(3, 4.272)) * 0.159154937 + 0.5) * 6.28318548 - 3.14159274;
  float2 cs = float2(cos(turn), sin(turn)) * 0.0029296875;
  float2 taps[4] = { cs.yx * 0.75, -cs.yx * 0.25, float2(-cs.x, cs.y) * 0.5, float2(cs.x, -cs.y) };
  float past = 0;
  [unroll] for (int k = 0; k < 4; k++)
  {
    int2 texel = (int2)(clamp(uv + taps[k], 0, 0.999) * ShadowSize);
    float stored = LampShadows.Load(int3(side * ShadowSize + texel.x, slot * ShadowSize + ShadowSize - 1 - texel.y, 0));
    if (stored - len / range >= 0)
    {
      past += 0.25;
    }
  }
  return past;
}

struct LvOut { float4 position : SV_Position; nointerpolation uint light : LIGHT; };

static const float2 Quad[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

LvOut VSLight(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
  Light l = Lights[instance];
  uint face = vertex / 6, axis = face >> 1;
  float side = (face & 1) ? 1 : -1;
  float2 q = Quad[vertex % 6];
  float3 c = axis == 0 ? float3(side, q.x, q.y) : axis == 1 ? float3(q.y, side, q.x) : float3(q.x, q.y, side);
  float3 n = axis == 0 ? float3(side, 0, 0) : axis == 1 ? float3(0, side, 0) : float3(0, 0, side);
  float r = l.At.w * 1.01;
  LvOut o;
  o.position = mul(float4(l.At.xyz + c * r, 1), f.ViewProjection);
  // A face turned towards the camera: dropped (all its corners on one point), so either winding works.
  if (dot(n, l.At.xyz + n * r - f.Eye.xyz) <= 0)
  {
    o.position = float4(0, 0, 0.5, 1);
  }
  o.light = instance;
  return o;
}

float4 PSLight(LvOut input) : SV_Target
{
  int3 px = int3(input.position.xy, 0);
  float4 place = GPlace.Load(px);
  if (place.w < 0.5)
  {
    discard;
  }
  Light l = Lights[input.light];
  // (place.w: 1, or 2 where baked, + 4 x the shine's power in 64ths, see Shade.)
  bool baked = fmod(place.w, 4) > 1.5;
  float3 shineLight = 0;
  uint mask = (uint)(l.Kind.w + 0.5);
  if ((mask == 1 && baked) || (mask == 2 && !baked))
  {
    discard;
  }
  float3 L = l.At.xyz - place.xyz;
  float range = l.At.w;
  float d = length(L);
  if (d >= range || d < 1e-4)
  {
    discard;
  }
  L /= d;
  float4 nao = GNormal.Load(px);
  float3 n = normalize(nao.xyz + 1e-6);
  float ao = nao.w;
  float a = saturate(1 - d * d / (range * range));
  float spot = 1;
  if (l.Dir.w > -1)
  {
    spot = saturate((dot(-L, l.Dir.xyz) - l.Dir.w) * l.Color.w);
  }
  uint model = (uint)(l.Kind.x + 0.5);
  bool inRoom = l.Kind.y > 0.5, shadowed = l.Kind.z > 0.5;
  // (Kind.z: 0 no shadow, 1 shadowed without a map, 2 + its slot in the atlas.)
  int slot = (int)(l.Kind.z + 0.5) - 2;
  float past = slot >= 0 ? LampShadow(slot, l.At.xyz, range, place.xyz, n, floor(input.position.xy)) : 1;
  float nl = dot(n, L);
  float3 lit;
  if (model == 1)
  {
    float nearBy = max(1.5 * (a * a - 1.0 / 3.0), 0);
    lit = l.Color.rgb * nearBy * spot * (shadowed ? saturate(1.5 * (nl - 1.0 / 3.0)) * past : saturate((nl - 0.2) * 1.25));
  }
  else
  {
    float reach = max((a * a * a * a - 0.1) / 0.9, 0);
    float3 lampLight = l.Color.rgb * reach * spot * past;
    if (model == 0 && !inRoom)
    {
      lampLight *= shadowed ? saturate(0.5 * ao * ao * ao * ao + ao * ao) : saturate(ao * ao + 0.5);
    }
    lit = lampLight * saturate(nl);
    // Its shine (the page's lampShine, ModelView.html ~629, ~643, ~657-659; deferred_lighting's 2 x GB2.x x
    // saturate(L.R)^power): the G-buffer's amount (albedo's alpha, 2 x shineAmount) and power (place.w / 4 in 64ths),
    // R the view mirrored by the normal. Not the fillers'.
    float power = floor(place.w / 4) / 64;
    if (power > 0)
    {
      float3 R = reflect(normalize(place.xyz - f.Eye.xyz), n);
      shineLight = lampLight * pow(saturate(dot(L, R)), power);
    }
  }
  float4 albedo = GAlbedo.Load(px);
  return float4(albedo.rgb * lit + albedo.a * shineLight, 0);
}

// The fog over the solid scene (#112; the page's gameFog, GTA IV's rage_postfx program 21, which the game draws after
// the lights and the glows, before the see-through materials): a triangle over the screen; where the G-buffer has a
// surface (not the sky) the fog's colour blended over the light by its amount at the surface's distance (SRCALPHA /
// INVSRCALPHA: light + amount x (colour - light)). The see-through materials take it each in PSBlend.
float4 VSFog(uint id : SV_VertexID) : SV_Position
{
  float2 uv = float2((id << 1) & 2, id & 2);
  return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// The light as the lamps left it (copied before the fog), for the depth effect.
Texture2D<float4> LightCopy : register(t9);

float4 PSFog(float4 position : SV_Position) : SV_Target
{
  float4 place = GPlace.Load(int3(position.xy, 0));
  if (place.w < 0.5)
  {
    discard;
  }
  float distance = length(f.Eye.xyz - place.xyz);
  float amount = FogAmount(distance);
  // Before the fog the same pass's depth effect (the page's gameFog, gtaiv-pc.js ~304-314; GTA IV's trace GTAIV.1):
  // from DepthFx.z to .w the light desaturated towards DesaturationFar and x luma^(ramp (GammaFar - 1)), the luma in
  // the game's light (without the exposure); then the fog over it. Written whole (alpha 1).
  if (f.DepthFx.w > f.DepthFx.z && f.Screen.w > 0.5)
  {
    float3 light = DepthEffect(LightCopy.Load(int3(position.xy, 0)).rgb, distance);
    light += amount * (FogColour(distance) - light);
    return float4(light, 1);
  }
  if (amount <= 0)
  {
    discard;
  }
  return float4(FogColour(distance), amount);
}

// The sun's shadow map from a cascade's 4096 x 4096 work target (the page's shadowConvert, GTA IV's cascadeConvert ps 2:
// its four reads half a texel off, x 0.25): each texel of the cascade's quarter the mean of its 2 x 2 (the max of the
// map's objects and the cast is already in the work target, see DrawSunShadow).
Texture2D<float> SunWork : register(t8);

float4 PSSunConvert(float4 position : SV_Position) : SV_Target
{
  int2 at = (int2(position.xy) % SunMapSize) * 2;
  float sum = SunWork.Load(int3(at, 0)) + SunWork.Load(int3(at + int2(1, 0), 0)) +
              SunWork.Load(int3(at + int2(0, 1), 0)) + SunWork.Load(int3(at + int2(1, 1), 0));
  return float4(sum * 0.25, 0, 0, 1);
}
";

    private const int DrawConstants = 16 + 4 + 4 + 4 + 4 + 4;
    private const int FrameFloats = 16 + 4 * 21;
    // A part's material for the shader (PartData: 11 x 4 numbers), and the per-vertex extras (tangent, layer texture
    // coordinates, layer weights: 14 floats) of models that have any.
    private const int PartWords = 44;
    private const int ExtraFloats = 14;

    // The shader's PartData flags.
    [Flags]
    private enum PartFlags : uint
    {
      NormalMap = 1, SpecularMap = 2, SpecularFlat = 4, Environment = 8, EnvironmentSpecular = 16, Paint2 = 32, Dirt = 64,
      Stipple = 128, VehicleGlass = 256, VehicleLights = 512, DecalBelow = 1024, DecalAoBelow = 2048, ForwardShine = 4096,
      TextureAlpha = 8192, Textured = 16384, FixedAlpha = 32768, ForwardLit = 65536,
    }

    /// <summary>
    /// How many lights' shadow maps a frame can draw (the atlas's rows; see <see cref="DeferredLight.ShadowSlot"/>): each
    /// light two halves of 1024 x 1024, R32F (the shader's ShadowSize).
    /// </summary>
    public const int ShadowSlots = 4;
    private const int ShadowSize = 1024;
    // (The frame's constants, FrameFloats, rounded up to the constant buffers' 256-byte steps.)
    private const int FrameSlotBytes = (FrameFloats * 4 + 255) / 256 * 256;

    /// <summary>
    /// The sun's shadow map (see <see cref="SunShadow"/>): its cascades, each drawn at 4096 x 4096 (R32F, with a depth)
    /// and averaged 2 x 2 into its 2048 x 2048 quarter of the map read by the light (R16F), as the page's shadowTarget.
    /// </summary>
    public const int SunCascades = 4;
    private const int SunWorkSize = 4096;
    private const int SunMapSize = 2048;
    private const int VertexFloats = 12;
    private const int SkinFloats = 8;

    // Bone matrices a frame (for all skinned instances), and how many frames' worth are kept (frames in flight).
    private const int PaletteCapacity = 65536;
    private const int PaletteSlots = 4;

    // The G-buffer the lamps are added over (#112 step 5): albedo, normal + AO, place + kind (see the shader's PsOut).
    private static readonly Format[] GBufferFormats = { Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R32G32B32A32_Float };
    // Lights a frame (no cap of the game's or the page's kind; the harbour has some 150 lamps in view), 64 bytes each.
    private const int LightCapacity = 16384;
    private const int LightBytes = 64;

    // Glass: see-through, drawn with the others but writing depth (vehicle glass, the map's glass and see-through
    // materials: the page's glassDepth).
    internal enum Pass : byte { Solid, Blend, Decal, Glass }

    /// <summary>Models made on the GPU by <see cref="Prepare"/> (any thread), not yet drawn: <see cref="Register"/> adds them.</summary>
    public sealed class PreparedModels
    {
      internal PreparedModels(List<Model?> models) => Models = models;
      internal List<Model?> Models { get; }
    }

    internal readonly struct Part
    {
      public Part(uint start, uint count, int texture, Pass pass, bool cutout, Vector3? dirtMask, Vector3 tint)
      {
        Start = start;
        Count = count;
        Texture = texture;
        Pass = pass;
        Cutout = cutout;
        DirtMask = dirtMask;
        Tint = tint;
      }

      public Vector3 Tint { get; }

      /// <summary>Its kind (the shader's bits: baked 1, ped 2, vehicle 4, tree 8, glowing 16, at night 32, strong 64), its glow.</summary>
      public int Kind { get; init; }
      public float Glow { get; init; }

      /// <summary>Its back faces not drawn (a map object's or prop's solid: the page culls those).</summary>
      public bool Culled { get; init; }

      /// <summary>Its number in its model's PartData.</summary>
      public int Index { get; init; }

      /// <summary>See-through (the page's seeThrough: glass, or blended with a texture): a cast's not in the sun's shadow map.</summary>
      public bool SeeThrough { get; init; }

      /// <summary>
      /// Into the sun's shadow map blended by its texture's alpha, after the solid ones (the page's alphaShadow: what the
      /// game's own module marks so, see <see cref="SceneMaterial.ShadowByAlpha"/>).
      /// </summary>
      public bool AlphaShadow { get; init; }

      /// <summary>Layered terrain (its mesh has layer coordinates and its material more than one layer): not in the mirror.</summary>
      public bool Layered { get; init; }

      /// <summary>
      /// A decal drawn before the other decals (the page's dirtFirst, ModelView.html ~3363-3380): a dirt decal, or a decal
      /// of a location model with dirt decals (the page's dirtModel, CutsceneLocation's message).
      /// </summary>
      public bool DirtFirst { get; init; }

      /// <summary>A blended material that isn't glass: pulled towards the camera in the see-through pass (the page's polygonOffset(-0.25, -2)).</summary>
      public bool Pulled { get; init; }

      /// <summary>
      /// A glowing see-through part (the page's mat.emissive: draw bucket 4 or 5, textured, see-through or glass): its glow
      /// drawn apart (DrawPass, the page's glowApart). Its emissiveMultiplier (0 without), whether gta_emissivenight,
      /// gta_emissivestrong, glass, blended (the page's mat.blend: not glass).
      /// </summary>
      public bool GlowApart { get; init; }
      public float GlowMultiplier { get; init; }
      public bool GlowNight { get; init; }
      public bool GlowStrong { get; init; }
      public bool GlowGlass { get; init; }
      public bool GlowBlend { get; init; }

      /// <summary>Its bounding sphere in the model's space (its mesh's box: the centre, half the diagonal).</summary>
      public Vector3 Center { get; init; }
      public float Radius { get; init; }

      public uint Start { get; }
      public uint Count { get; }
      public int Texture { get; }
      public Pass Pass { get; }
      public bool Cutout { get; }
      public Vector3? DirtMask { get; }
    }

    internal sealed class Model : IDisposable
    {
      public int Group;
      public readonly List<int> Textures = new List<int>();
      public ID3D12Resource Vertices = null!;
      public ID3D12Resource Indices = null!;
      public ID3D12Resource? Skin;
      public VertexBufferView VertexView;
      public VertexBufferView SkinView;
      public IndexBufferView IndexView;
      public uint IndexCount;
      public Part[] Parts = Array.Empty<Part>();
      public Vector3 Min, Max;
      // A cutscene's cast (ModelSource.Actor): drawn into the sun's shadow map each frame with the cast, else with the map.
      public bool Actor;
      // The parts' materials (PartData, t2) and the vertices' extras (null: none, the shared zeros read instead).
      public ID3D12Resource PartData = null!;
      public ID3D12Resource? Extras;
      public VertexBufferView ExtrasView;

      public void Dispose()
      {
        Vertices.Dispose();
        Indices.Dispose();
        Skin?.Dispose();
        PartData.Dispose();
        Extras?.Dispose();
      }
    }

    private sealed class Instance
    {
      public int Model;
      public Matrix4x4 World;
      public Matrix4x4[]? Palette;
      public ulong PaletteAddress;
      public bool Visible = true;
      public float ActorAmbient = 1;
      public bool InRoom;
      public bool CastsShadow;
      public bool CastsSunShadow = true;
      // Drawn into the water's mirror (DrawMirror); drawn in the main view and the shadow maps (false: the mirror only).
      public bool Mirrored;
      public bool MainView = true;
    }

    /// <summary>An actor's share of the light from around (its room's AmbientDownMult; 1 outside).</summary>
    public void SetActorAmbient(int instance, float value)
    {
      if (_instances[instance] is Instance i)
      {
        i.ActorAmbient = value;
      }
    }

    /// <summary>The draw calls of the last frame.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Shows or hides an instance (an actor not in the section played).</summary>
    public void SetVisible(int instance, bool visible)
    {
      if (_instances[instance] is Instance i)
      {
        i.Visible = visible;
      }
    }

    private readonly D3D12Device _device;
    private readonly TextureCache _textures;
    private readonly ID3D12RootSignature _rootSignature;
    // Skinned or not x pass x back faces culled or not.
    // (By Pipe; from 16 on the blended non-glass materials' pulled variants of the see-through passes.)
    private readonly ID3D12PipelineState?[] _pipelines = new ID3D12PipelineState?[32];
    // The water's mirror: its solid parts, its see-through ones (see DrawMirror).
    private readonly ID3D12PipelineState[] _mirrorPipelines = new ID3D12PipelineState[2];
    // The see-through glows drawn apart (DrawGlow): by skinned (2) and added (1) instead of laid over.
    private readonly ID3D12PipelineState[] _glowPipelines = new ID3D12PipelineState[4];
    // The frame's day/night factor and exposure (FrameLight), for the glows' strength per instance.
    private float _dayNight, _exposure;
    // The coronas: their pipeline, the sprite's texture number (0: none yet), the solid scene's depth (before the water
    // and the see-through materials) and its texture number.
    private readonly ID3D12PipelineState _coronaPipeline;
    private int _coronaSlot;
    private readonly int _solidDepthSlot;
    private ID3D12Resource? _solidDepth;
    private int _solidDepthWidth, _solidDepthHeight;
    // The light volumes: their pipeline, the scene's whole depth (after the see-through materials) and its number, the
    // lookup texture's number (0: none), the spot's and the point lamp's unit meshes (upload heap) and c213 scales.
    private readonly ID3D12PipelineState _volumePipeline;
    private readonly int _fullDepthSlot;
    private ID3D12Resource? _fullDepth;
    private int _fullDepthWidth, _fullDepthHeight;
    private int _volumeLookupSlot;
    // The mirrored surroundings' map (see DrawParabolic): the texture, its depth, a target view per level, its number
    // (all levels) and each level's (for the next one's halving), the pipelines; whether it was drawn this frame.
    private const uint ParabolicSize = 1024;
    private const int ParabolicLevels = 4;
    private readonly ID3D12Resource _parabolic, _parabolicDepth;
    private readonly ID3D12DescriptorHeap _parabolicRtvs, _parabolicDsv;
    private readonly int _parabolicSlot;
    private readonly int[] _parabolicLevelSlots = new int[ParabolicLevels];
    private readonly ID3D12PipelineState _parabolicPipeline, _halvePipeline;
    private readonly ID3D12Resource _spotVolume, _pointVolume;
    private readonly int _spotVolumeCount, _pointVolumeCount;
    private readonly float _spotVolumeScale, _pointVolumeScale;

    /// <summary>The samples a pixel of the water's mirror (the page's reflectionTarget: 4); its target and depth must have as many.</summary>
    public const int MirrorSamples = 4;

    /// <summary>The mirror's draw calls in the last frame.</summary>
    public int MirrorDrawCalls { get; private set; }
    // Zeros read as the extras of models without any (a stride of 0: every vertex the same).
    private readonly ID3D12Resource _noExtras;
    // The surface's normal and AO under the decals (a copy of the G-buffer's after the solid pass), its texture number.
    private ID3D12Resource? _below;
    private readonly int _belowSlot;
    // The light target's copy before the fog (its depth effect reads it; t9 in the lamps' table) and its format.
    private ID3D12Resource? _lightCopy;
    private readonly Format _targetFormat;
    private readonly ID3D12Resource _frame;
    private readonly unsafe byte* _frameMapped;
    private readonly List<Model?> _models = new List<Model?>();
    // (Removed ones stay as null: numbers given out keep their meaning.)
    private readonly List<Instance?> _instances = new List<Instance?>();
    private readonly ID3D12Resource _palette;
    private readonly unsafe Matrix4x4* _paletteMapped;
    private int _paletteSlot;

    // The lamps' pass: its root signature (frame b1, lights t3, the G-buffer t4-t6), pipeline, light buffer, G-buffer.
    private readonly ID3D12RootSignature _lightSignature;
    private readonly ID3D12PipelineState _lightPipeline;
    private readonly ID3D12PipelineState _fogPipeline;
    private readonly ID3D12Resource _lights;
    private readonly unsafe byte* _lightsMapped;
    private readonly ID3D12DescriptorHeap _gBufferRtvs;
    private readonly ID3D12DescriptorHeap _gBufferSrvs;
    private readonly ID3D12Resource?[] _gBuffer = new ID3D12Resource?[3];
    private int _gWidth, _gHeight;

    // The lights' shadow maps (step 5): the atlas (kept readable by the light pass between frames), its depth, their
    // views, the pipelines (skinned or not x the half: -1 its front faces culled, +1 its back faces).
    private readonly ID3D12Resource _shadowAtlas;
    private readonly ID3D12Resource _shadowDepth;
    private readonly ID3D12DescriptorHeap _shadowRtv;
    private readonly ID3D12DescriptorHeap _shadowDsv;
    private readonly ID3D12PipelineState[] _shadowPipelines = new ID3D12PipelineState[4];
    private readonly int[] _slotOf = new int[ShadowSlots];

    // The sun's shadow map: a cascade's work target and its depth, the map of the four cascades (read by its number among
    // the textures), their views (made when first drawn), the pipelines drawing into a cascade (skinned or not x the map's
    // solid objects, its blended ones, the cast) and the one averaging a cascade into the map.
    private ID3D12Resource? _sunWork;
    private ID3D12Resource? _sunDepth;
    private ID3D12Resource? _sunMap;
    private readonly ID3D12DescriptorHeap _sunRtvs;
    private readonly ID3D12DescriptorHeap _sunDsv;
    private readonly ID3D12PipelineState[] _sunPipelines = new ID3D12PipelineState[6];
    private readonly ID3D12PipelineState _sunConvertPipeline;
    private readonly int _sunSlot;

    /// <summary>Whether the sun's shadow map was drawn last frame.</summary>
    public bool SunShadowDrawn { get; private set; }

    /// <summary>The shadow maps drawn last frame (lights).</summary>
    public int ShadowMapsDrawn { get; private set; }

    /// <summary>The lights added last frame.</summary>
    public int LightsDrawn { get; private set; }

    public unsafe MeshRenderer(D3D12Device device, Format targetFormat)
    {
      _device = device;
      _targetFormat = targetFormat;
      _textures = new TextureCache(device);
      var d3d = device.Device;

      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, DrawConstants), ShaderVisibility.All),
        // All the textures' views (t0 in space 1, unbounded; a part's numbers in its PartData). Volatile: the loading
        // thread adds views to other slots while frames are drawn.
        // (All stages: the coronas' vertex program reads the solid scene's depth.)
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, uint.MaxValue, 0, 1, 0, DescriptorRangeFlags.DescriptorsVolatile)), ShaderVisibility.All),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex),
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        // The model's parts' materials (t2).
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.Pixel),
        // The frame's lights (t3, as the lamps' pass reads them), for the map's see-through materials lit forward.
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.Pixel),
      };
      var samplers = new[]
      {
        new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
          Filter = Filter.Anisotropic,
          AddressU = TextureAddressMode.Wrap,
          AddressV = TextureAddressMode.Wrap,
          AddressW = TextureAddressMode.Wrap,
          MaxAnisotropy = 16,
          MaxLOD = float.MaxValue,
        },
        // The mirrored surroundings' map as the page reads it (LINEAR_MIPMAP_LINEAR, CLAMP_TO_EDGE; gtaiv-pc.js ~1214-1218).
        new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0)
        {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
          MaxLOD = float.MaxValue,
        },
      };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers));

      var vertexShader = Compile(DxcShaderStage.Vertex, "VSMain");
      var skinnedShader = Compile(DxcShaderStage.Vertex, "VSSkinned");
      var pixelShader = Compile(DxcShaderStage.Pixel, "PSMain");
      var blendShader = Compile(DxcShaderStage.Pixel, "PSBlend");
      var decalShader = Compile(DxcShaderStage.Pixel, "PSDecal");
      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        new InputElementDescription("ALPHA", 0, Format.R32_Float, 32, 0),
        new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 36, 0),
      };
      var skinnedInput = input.Concat(new[]
      {
        new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 0, 1),
        new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 16, 1),
      }).ToArray();
      // The extras (step 4d), stream 2: tangent (w the binormal's side), layers 1-2's and 3's texture coordinates, weights.
      var extras = new[]
      {
        new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 2),
        new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 16, 2),
        new InputElementDescription("TEXCOORD", 2, Format.R32G32_Float, 32, 2),
        new InputElementDescription("TEXCOORD", 3, Format.R32G32B32A32_Float, 40, 2),
      };
      var shadowInput = input;
      var shadowSkinnedInput = skinnedInput;
      input = input.Concat(extras).ToArray();
      skinnedInput = skinnedInput.Concat(extras).ToArray();
      // Solid and decals also write the G-buffer (step 5); the see-through materials the light target only.
      var withGBuffer = new[] { targetFormat }.Concat(GBufferFormats).ToArray();
      GraphicsPipelineStateDescription Pipeline(bool skinned, BlendDescription blend, DepthStencilDescription depth, RasterizerDescription rasterizer, bool gBuffer) => new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = skinned ? skinnedShader : vertexShader,
        PixelShader = gBuffer ? pixelShader : blendShader,
        InputLayout = new InputLayoutDescription(skinned ? skinnedInput : input),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = rasterizer,
        BlendState = blend,
        DepthStencilState = depth,
        RenderTargetFormats = gBuffer ? withGBuffer : new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      };
      // A decal: blended into the light and into the G-buffer's albedo (colour only), the normal, AO and place left.
      var decalBlend = BlendDescription.NonPremultiplied;
      decalBlend.IndependentBlendEnable = true;
      // (Its alpha, the surface's lamp shine, kept x (1 - the decal's alpha): the page blends the lit decal over the lit
      // surface, its shine with it.)
      decalBlend.RenderTarget[1] = decalBlend.RenderTarget[0] with { SourceBlendAlpha = Blend.Zero, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add };
      decalBlend.RenderTarget[2] = decalBlend.RenderTarget[0] with { BlendEnable = false, RenderTargetWriteMask = 0 };
      decalBlend.RenderTarget[3] = decalBlend.RenderTarget[2];
      // Both faces, or the back ones culled: the page's front faces wind counter-clockwise (GL's default), and so they
      // do here (checked: with Direct3D's default, clockwise, bc_1's hall showed the next room's wallpaper through
      // its walls).
      var both = RasterizerDescription.CullNone;
      // The depth test less or equal, as the page's (ModelView.html ~3180-3184: the game's ZFUNC 4): a surface laid on
      // another with the same vertices is drawn over it.
      var depthLe = DepthStencilDescription.Default with { DepthFunc = ComparisonFunction.LessEqual };
      var readLe = DepthStencilDescription.Read with { DepthFunc = ComparisonFunction.LessEqual };
      var front = new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true };
      // Pulled a little towards the camera as the page's polygonOffset (ModelView.html ~3510-3530): a decal by (-0.25, -4),
      // a blended material that isn't glass by (-0.25, -2) - factor x the slope, units x the depth's smallest step (the
      // page's 24-bit depth: 2^-24; this D32 float's step for depths 0.5 to 1, where nearly all lie: also 2^-24).
      // (Native pulled its decals by (-1, -16): the intro galley's floor shadow decal cargoflrsha, 1 mm under the rubber
      // mat mp_bada_mat, came through it and laid it black.)
      RasterizerDescription Pulled(RasterizerDescription r, int units) => r with { DepthBias = units, SlopeScaledDepthBias = -0.25f };
      for (int s = 0; s < 2; s++)
      {
        for (int cull = 0; cull < 2; cull++)
        {
          var raster = cull == 1 ? front : both;
          int at = Pipe(s == 1, Pass.Solid, cull == 1);
          _pipelines[at] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.Opaque, depthLe, raster, true));
          _pipelines[Pipe(s == 1, Pass.Blend, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, readLe, raster, false));
          var decalState = Pipeline(s == 1, decalBlend, readLe, Pulled(raster, -4), true);
          decalState.PixelShader = decalShader;
          _pipelines[Pipe(s == 1, Pass.Decal, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(decalState);
          _pipelines[Pipe(s == 1, Pass.Glass, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, depthLe, raster, false));
          // (The blended non-glass materials' pulled variants: Part.Pulled.)
          _pipelines[16 + Pipe(s == 1, Pass.Blend, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, readLe, Pulled(raster, -2), false));
          _pipelines[16 + Pipe(s == 1, Pass.Glass, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, depthLe, Pulled(raster, -2), false));
        }
      }

      // A see-through part's glow drawn apart (the page's glowOnly draw, ModelView.html ~3709-3734): back faces culled,
      // writing depth, LESSEQUAL; ONE / INVSRCALPHA for gta_emissive (glowOver), ONE / ONE for the others.
      var glowVertex = Compile(DxcShaderStage.Vertex, "VSGlow");
      var glowSkinnedVertex = Compile(DxcShaderStage.Vertex, "VSGlowSkinned");
      var glowPixel = Compile(DxcShaderStage.Pixel, "PSGlow");
      var glowDepth = DepthStencilDescription.Default with { DepthFunc = ComparisonFunction.LessEqual };
      for (int k = 0; k < 4; k++)
      {
        var blend = BlendDescription.Opaque;
        blend.RenderTarget[0] = blend.RenderTarget[0] with
        {
          BlendEnable = true,
          SourceBlend = Blend.One, DestinationBlend = (k & 1) != 0 ? Blend.One : Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
          SourceBlendAlpha = Blend.One, DestinationBlendAlpha = (k & 1) != 0 ? Blend.One : Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
        };
        var glowState = Pipeline((k & 2) != 0, blend, glowDepth, front, false);
        glowState.VertexShader = (k & 2) != 0 ? glowSkinnedVertex : glowVertex;
        glowState.PixelShader = glowPixel;
        _glowPipelines[k] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(glowState);
      }

      // The lamps' coronas (DrawCoronas): no vertices, no depth, added onto the light's colour.
      _coronaPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSCorona"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSCorona"),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = AddedLight(),
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      });
      _solidDepthSlot = _textures.Reserve();
      _coronaSlot = 0;
      // The lamps' light volumes (DrawVolumes): the unit meshes as strips, both faces (their winding adds or takes off),
      // no depth test, added onto the light's colour.
      _volumePipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSVolume"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSVolume"),
        InputLayout = new InputLayoutDescription(new[] { new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0) }),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true },
        BlendState = AddedLight(),
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      });
      _fullDepthSlot = _textures.Reserve();
      // The mirrored surroundings' map (the page's parabolicTarget, gtaiv-pc.js ~1207-1233): 1024 x 1024, the light's
      // format (A16B16G16R16F, as the game's), four levels, a depth; drawn into by DrawParabolic, each smaller level made
      // from the one above (PSHalve); read by the shine through MirroredAround (its number in f.Params2.z).
      _parabolic = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(targetFormat, ParabolicSize, ParabolicSize, 1, ParabolicLevels, 1, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.PixelShaderResource, new ClearValue(targetFormat, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
      _parabolicDepth = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(PanelSwapChainFormats.Depth, ParabolicSize, ParabolicSize, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.DepthWrite, new ClearValue(PanelSwapChainFormats.Depth, 1.0f, 0));
      _parabolicRtvs = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, ParabolicLevels));
      _parabolicDsv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
      uint parabolicRtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      _parabolicSlot = _textures.Reserve();
      d3d.CreateShaderResourceView(_parabolic, null, _textures.CpuHandle(_parabolicSlot));
      for (int level = 0; level < ParabolicLevels; level++)
      {
        d3d.CreateRenderTargetView(_parabolic, new RenderTargetViewDescription
        {
          Format = targetFormat,
          ViewDimension = RenderTargetViewDimension.Texture2D,
          Texture2D = new Texture2DRenderTargetView { MipSlice = (uint)level },
        }, _parabolicRtvs.GetCPUDescriptorHandleForHeapStart() + level * (int)parabolicRtvSize);
        _parabolicLevelSlots[level] = _textures.Reserve();
        d3d.CreateShaderResourceView(_parabolic, new ShaderResourceViewDescription
        {
          Format = targetFormat,
          ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
          Shader4ComponentMapping = ShaderComponentMapping.Default,
          Texture2D = new Texture2DShaderResourceView { MostDetailedMip = (uint)level, MipLevels = 1 },
        }, _textures.CpuHandle(_parabolicLevelSlots[level]));
      }
      d3d.CreateDepthStencilView(_parabolicDepth, null, _parabolicDsv.GetCPUDescriptorHandleForHeapStart());
      // Into it: the map's solid objects, both faces, writing depth (LESS), blended SRCALPHA / INVSRCALPHA, its alpha kept
      // (the page's blendFuncSeparate(SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ZERO, ONE), ModelView.html ~3775-3781).
      var parabolicBlend = BlendDescription.Opaque;
      parabolicBlend.RenderTarget[0] = parabolicBlend.RenderTarget[0] with
      {
        BlendEnable = true,
        SourceBlend = Blend.SourceAlpha, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
        SourceBlendAlpha = Blend.Zero, DestinationBlendAlpha = Blend.One, BlendOperationAlpha = BlendOperation.Add,
      };
      var parabolicState = Pipeline(false, parabolicBlend, depthLe, both, false);
      parabolicState.VertexShader = Compile(DxcShaderStage.Vertex, "VSParabolic");
      parabolicState.PixelShader = Compile(DxcShaderStage.Pixel, "PSParabolic");
      _parabolicPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(parabolicState);
      _halvePipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSFull"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSHalve"),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.Opaque,
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      });
      (_spotVolume, _spotVolumeCount, _spotVolumeScale) = VolumeBuffer(d3d, LightVolumeMesh(5, 4, true));
      (_pointVolume, _pointVolumeCount, _pointVolumeScale) = VolumeBuffer(d3d, LightVolumeMesh(4, 8, false));

      // The water's mirror (DrawMirror, the page's reflection pass): into the light's format only, MirrorSamples samples
      // a pixel (the page's reflectionTarget), every face culled but those facing the camera - mirrored, the winding
      // turns round (the page's frontFace(CW) with cullFace(BACK), ModelView.html ~3677-3689); the solid ones opaque
      // (writing their premultiplied light and alpha as they are, the page's blend off), the see-through ones after,
      // ONE / ONE_MINUS_SRC_ALPHA without writing depth (the page's glass pass).
      var mirrorPixel = Compile(DxcShaderStage.Pixel, "PSMirror");
      var mirrorCull = new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = false, MultisampleEnable = true };
      for (int k = 0; k < 2; k++)
      {
        _mirrorPipelines[k] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
        {
          RootSignature = _rootSignature,
          VertexShader = vertexShader,
          PixelShader = mirrorPixel,
          InputLayout = new InputLayoutDescription(input),
          SampleMask = uint.MaxValue,
          PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
          RasterizerState = mirrorCull,
          BlendState = k == 0 ? BlendDescription.Opaque : BlendDescription.AlphaBlend,
          DepthStencilState = k == 0 ? depthLe : readLe,
          RenderTargetFormats = new[] { targetFormat },
          DepthStencilFormat = PanelSwapChainFormats.Depth,
          SampleDescription = new SampleDescription(MirrorSamples, 0),
        });
      }

      // The lamps' pass (step 5): the frame's constants, the lights, the G-buffer.
      _lightSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, new[]
      {
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, 4, 0, 0)), ShaderVisibility.Pixel),
      }));
      _lightPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _lightSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSLight"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSLight"),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        // (The faces towards the camera are dropped in the vertex shader; not clipped at the far plane.)
        RasterizerState = RasterizerDescription.CullNone with { DepthClipEnable = false },
        BlendState = AddedLight(),
        DepthStencilState = new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual),
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      });
      // The fog over the solid scene (after the lamps): blended over the light by its amount, no depth.
      var fogBlend = BlendDescription.NonPremultiplied;
      fogBlend.RenderTarget[0] = fogBlend.RenderTarget[0] with { RenderTargetWriteMask = ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue };
      _fogPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _lightSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSFog"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSFog"),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = fogBlend,
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      });
      _lights = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                            ResourceDescription.Buffer((ulong)PaletteSlots * LightCapacity * LightBytes), ResourceStates.GenericRead);
      void* lightsMapped;
      _lights.Map(0, null, &lightsMapped).CheckError();
      _lightsMapped = (byte*)lightsMapped;
      _gBufferRtvs = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 3));
      // (The G-buffer's three, the lamps' shadow atlas, the sun's cascade work target: t4 to t8.)
      _gBufferSrvs = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, DescriptorHeapFlags.ShaderVisible));

      // The shadow atlas (t7, after the G-buffer's three) and its depth; the pipelines drawing into it.
      _shadowAtlas = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.R32_Float, ShadowSize * 2, ShadowSize * ShadowSlots, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.PixelShaderResource, new ClearValue(Format.R32_Float, new Vortice.Mathematics.Color4(1, 1, 1, 1)));
      _shadowDepth = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.D32_Float, ShadowSize * 2, ShadowSize * ShadowSlots, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.DepthWrite, new ClearValue(Format.D32_Float, 1.0f, 0));
      _shadowRtv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
      _shadowDsv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
      d3d.CreateRenderTargetView(_shadowAtlas, null, _shadowRtv.GetCPUDescriptorHandleForHeapStart());
      d3d.CreateDepthStencilView(_shadowDepth, null, _shadowDsv.GetCPUDescriptorHandleForHeapStart());
      d3d.CreateShaderResourceView(_shadowAtlas, null, _gBufferSrvs.GetCPUDescriptorHandleForHeapStart() +
        3 * (int)d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
      var shadowVertex = Compile(DxcShaderStage.Vertex, "VSShadow");
      var shadowSkinned = Compile(DxcShaderStage.Vertex, "VSShadowSkinned");
      var shadowPixel = Compile(DxcShaderStage.Pixel, "PSShadow");
      for (int s = 0; s < 2; s++)
      {
        for (int half = 0; half < 2; half++)
        {
          // Only the faces towards the lamp, as the page measured against the game's atlas (the GL front faces, wound
          // counter-clockwise as here, culled for the half -1, the back ones for +1).
          _shadowPipelines[s * 2 + half] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
          {
            RootSignature = _rootSignature,
            VertexShader = s == 1 ? shadowSkinned : shadowVertex,
            PixelShader = shadowPixel,
            InputLayout = new InputLayoutDescription(s == 1 ? shadowSkinnedInput : shadowInput),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = new RasterizerDescription(half == 0 ? CullMode.Front : CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true },
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Format.R32_Float },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
          });
        }
      }
      // The sun's shadow map (its targets made when first drawn, see SizeSun): into a cascade with the page's states - the
      // map's solid objects with the depth test (LEQUAL, writing: the highest along the sun kept), its see-through, decal
      // and cut-out materials blended SRCALPHA / INVSRCALPHA over them (writing depth, as the game's wd_draw), the cast
      // by MAX (the page's max of the map and the cast, each with its own depth: the same), all faces, nothing cut out.
      _sunRtvs = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));
      _sunDsv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
      d3d.CreateShaderResourceView(null, new ShaderResourceViewDescription
      {
        Format = Format.R32_Float,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
      }, _gBufferSrvs.GetCPUDescriptorHandleForHeapStart() + 4 * (int)d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
      var sunVertex = Compile(DxcShaderStage.Vertex, "VSSun");
      var sunSkinned = Compile(DxcShaderStage.Vertex, "VSSunSkinned");
      var sunPixel = Compile(DxcShaderStage.Pixel, "PSSun");
      var sunAlpha = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha);
      var sunMax = new BlendDescription(Blend.One, Blend.One);
      sunMax.RenderTarget[0] = sunMax.RenderTarget[0] with { BlendOperation = BlendOperation.Max, BlendOperationAlpha = BlendOperation.Max };
      var sunDepthTest = new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual);
      for (int s = 0; s < 2; s++)
      {
        for (int phase = 0; phase < 3; phase++)
        {
          _sunPipelines[s * 3 + phase] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
          {
            RootSignature = _rootSignature,
            VertexShader = s == 1 ? sunSkinned : sunVertex,
            PixelShader = sunPixel,
            InputLayout = new InputLayoutDescription(s == 1 ? shadowSkinnedInput : shadowInput),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = phase == 0 ? BlendDescription.Opaque : phase == 1 ? sunAlpha : sunMax,
            DepthStencilState = phase == 2 ? DepthStencilDescription.None : sunDepthTest,
            RenderTargetFormats = new[] { Format.R32_Float },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
          });
        }
      }
      _sunConvertPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _lightSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSFog"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSSunConvert"),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.Opaque,
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { Format.R16_Float },
        SampleDescription = new SampleDescription(1, 0),
      });
      // The frame's constants (camera, light), a slot per frame in flight; after them as many for the mirror's view, and
      // as many for the mirrored surroundings' map.
      _frame = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                           ResourceDescription.Buffer((ulong)PaletteSlots * 3 * FrameSlotBytes), ResourceStates.GenericRead);
      void* frameMapped;
      _frame.Map(0, null, &frameMapped).CheckError();
      _frameMapped = (byte*)frameMapped;

      // The bones' matrices, written each frame into a slot of their own (the GPU may still read the last frames').
      _palette = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                             ResourceDescription.Buffer((ulong)PaletteSlots * PaletteCapacity * 64), ResourceStates.GenericRead);
      void* mapped;
      _palette.Map(0, null, &mapped).CheckError();
      _paletteMapped = (Matrix4x4*)mapped;

      _noExtras = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                              ResourceDescription.Buffer(256), ResourceStates.GenericRead);
      void* zeros;
      _noExtras.Map(0, null, &zeros).CheckError();
      new Span<byte>(zeros, 256).Clear();
      _noExtras.Unmap(0);
      _belowSlot = _textures.Reserve();
      _sunSlot = _textures.Reserve();
    }

    // The sun's shadow map's targets, made when first drawn (some 160 MB: not for views without a sun's shadow).
    private void SizeSun()
    {
      if (_sunMap != null)
      {
        return;
      }
      var d3d = _device.Device;
      _sunWork = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.R32_Float, SunWorkSize, SunWorkSize, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.RenderTarget, new ClearValue(Format.R32_Float, new Vortice.Mathematics.Color4(-100, 0, 0, 0)));
      _sunDepth = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.D32_Float, SunWorkSize, SunWorkSize, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.DepthWrite, new ClearValue(Format.D32_Float, 1.0f, 0));
      _sunMap = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.R16_Float, SunMapSize * SunCascades, SunMapSize, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.PixelShaderResource, new ClearValue(Format.R16_Float, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
      uint rtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      d3d.CreateRenderTargetView(_sunWork, null, _sunRtvs.GetCPUDescriptorHandleForHeapStart());
      d3d.CreateRenderTargetView(_sunMap, null, _sunRtvs.GetCPUDescriptorHandleForHeapStart() + (int)rtvSize);
      d3d.CreateDepthStencilView(_sunDepth, null, _sunDsv.GetCPUDescriptorHandleForHeapStart());
      d3d.CreateShaderResourceView(_sunWork, null, _gBufferSrvs.GetCPUDescriptorHandleForHeapStart() +
        4 * (int)d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
      if (_sunSlot > 0)
      {
        d3d.CreateShaderResourceView(_sunMap, null, _textures.CpuHandle(_sunSlot));
      }
    }

    /// <summary>
    /// The sun's shadow map as last drawn (the four cascades side by side, R16F, readable by pixel shaders; see
    /// <see cref="SunShadow"/>), for what is drawn after the solid scene (the water); null before the first.
    /// </summary>
    public ID3D12Resource? SunShadowMap => _sunMap;

    // A light added onto the light target's colour (one + one), its alpha left.
    private static BlendDescription AddedLight()
    {
      var blend = new BlendDescription(Blend.One, Blend.One);
      blend.RenderTarget[0] = blend.RenderTarget[0] with { RenderTargetWriteMask = ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue };
      return blend;
    }

    // The G-buffer at the target's size (made again when it changes; the GPU done with the old one first).
    private void SizeGBuffer(int width, int height)
    {
      if (width == _gWidth && height == _gHeight && _gBuffer[0] != null)
      {
        return;
      }
      _device.WaitIdle();
      var d3d = _device.Device;
      uint rtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      uint srvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      for (int i = 0; i < _gBuffer.Length; i++)
      {
        _gBuffer[i]?.Dispose();
        var clear = new ClearValue(GBufferFormats[i], new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _gBuffer[i] = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
          ResourceDescription.Texture2D(GBufferFormats[i], (uint)Math.Max(1, width), (uint)Math.Max(1, height), 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
          ResourceStates.RenderTarget, clear);
        d3d.CreateRenderTargetView(_gBuffer[i], null, _gBufferRtvs.GetCPUDescriptorHandleForHeapStart() + i * (int)rtvSize);
        d3d.CreateShaderResourceView(_gBuffer[i], null, _gBufferSrvs.GetCPUDescriptorHandleForHeapStart() + i * (int)srvSize);
      }
      // The surface under the decals: the normal + AO target's copy, read by its number with the textures.
      _below?.Dispose();
      _below = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(GBufferFormats[1], (uint)Math.Max(1, width), (uint)Math.Max(1, height), 1, 1),
        ResourceStates.PixelShaderResource);
      if (_belowSlot > 0)
      {
        d3d.CreateShaderResourceView(_below, null, _textures.CpuHandle(_belowSlot));
      }
      // The light's copy for the fog's depth effect (t9).
      _lightCopy?.Dispose();
      _lightCopy = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(_targetFormat, (uint)Math.Max(1, width), (uint)Math.Max(1, height), 1, 1),
        ResourceStates.PixelShaderResource);
      d3d.CreateShaderResourceView(_lightCopy, null, _gBufferSrvs.GetCPUDescriptorHandleForHeapStart() + 5 * (int)srvSize);
      _gWidth = width;
      _gHeight = height;
    }

    private CpuDescriptorHandle GBufferView(int i) =>
      _gBufferRtvs.GetCPUDescriptorHandleForHeapStart() + i * (int)_device.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

    private static int Pipe(bool skinned, Pass pass, bool culled) => (skinned ? 8 : 0) + (int)pass * 2 + (culled ? 1 : 0);

    // Which parts a pass draws: the see-through pass both kinds of see-through (in the models' order, as the page).
    private static bool Drawn(Pass part, Pass pass) => pass == Pass.Blend ? part is Pass.Blend or Pass.Glass : part == pass;

    /// <summary>The number of instances and of the triangles they draw.</summary>
    public int InstanceCount => _instances.Count(i => i != null);
    public long Triangles => _instances.Sum(i => i == null ? 0 : (long)(_models[i.Model]?.IndexCount ?? 0) / 3);

    /// <summary>Texture keys materials name that their model's textures don't hold (drawn white).</summary>
    public SortedSet<string> MissingTextures { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The textures on the GPU, those that couldn't be made (drawn white), and their bytes.</summary>
    public (int Made, int Failed, long Bytes) TextureStats => (_textures.Count, _textures.Failed, _textures.Bytes);

    private static byte[] Compile(DxcShaderStage stage, string entryPoint)
    {
      using IDxcResult result = DxcCompiler.Compile(stage, Shader, entryPoint);
      if (result.GetStatus().Failure)
      {
        throw new InvalidOperationException("HLSL " + entryPoint + ": " + result.GetErrors());
      }
      return result.GetObjectBytecodeArray();
    }

    /// <summary>Models' checked meshes (not moving with bones) onto the GPU; see <see cref="AddModels(IReadOnlyList{ModelSource})"/>.</summary>
    public int[] AddModels(IReadOnlyList<ModelScene> scenes) => AddModels(scenes.Select(s => ModelSource.Shown(s, false)).ToList());

    /// <summary>
    /// Models onto the GPU with their textures and drawn from now on (<see cref="Prepare"/> and <see cref="Register"/>
    /// in one, on the drawing thread). Returns each model's number for <see cref="AddInstance"/>.
    /// </summary>
    public int[] AddModels(IReadOnlyList<ModelSource> sources, int group = 0) => Register(Prepare(sources), group);

    // The upload buffers sent and waited for after about this many bytes (a location: hundreds of MB).
    private const long FlushBytes = 256L << 20;

    /// <summary>
    /// Models made on the GPU with their textures, from any thread (a loading one: the drawing goes on meanwhile; the
    /// GPU done with the copies before returning): per model its meshes' positions, normals, texture coordinates and
    /// alpha in one vertex buffer (a skinned model's bones and weights in a second), their indices (32-bit) in one index
    /// buffer, a part per mesh. Not drawn until <see cref="Register"/>ed.
    /// </summary>
    public PreparedModels Prepare(IReadOnlyList<ModelSource> sources)
    {
      // The models spread over several threads (each its own copy list, sent when it holds FlushBytes), the order kept:
      // model i is the source i's. Textures shared between them are made once (TextureCache), the copies all done
      // before this returns. Below the render thread's priority.
      var models = new Model?[sources.Count];
      int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
      int chunk = (sources.Count + workers - 1) / Math.Max(1, workers);
      System.Threading.Tasks.Parallel.For(0, workers, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
      {
        int from = w * chunk, to = Math.Min(sources.Count, from + chunk);
        if (from >= to)
        {
          return;
        }
        var thread = System.Threading.Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = System.Threading.ThreadPriority.BelowNormal;
        var d3d = _device.Device;
        var uploads = new List<ID3D12Resource>();
        long pending = 0;
        // On the copy queue: the uploads run beside the frames, not between their lists (D3D12Device.CopyQueue).
        var allocator = d3d.CreateCommandAllocator(CommandListType.Copy);
        var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Copy, allocator, null);
        void Send()
        {
          list.Close();
          _device.CopyAndWait(list);
          foreach (var upload in uploads)
          {
            upload.Dispose();
          }
          uploads.Clear();
          pending = 0;
        }
        try
        {
          for (int i = from; i < to; i++)
          {
            models[i] = Build(sources[i], list, uploads, ref pending);
            if (pending >= FlushBytes)
            {
              Send();
              allocator.Reset();
              list.Reset(allocator, null);
            }
          }
          Send();
        }
        finally
        {
          list.Dispose();
          allocator.Dispose();
          thread.Priority = priority;
        }
      });
      return new PreparedModels(models.ToList());
    }

    /// <summary>
    /// The coronas' sprite (a texture blob as the models' are; GTA IV: coronas.wtd's "corona"), onto the GPU now (waited
    /// for); null: none, no coronas drawn.
    /// </summary>
    public void SetCoronaSprite(byte[]? raw)
    {
      if (_coronaSlot != 0)
      {
        _device.WaitIdle();
        _textures.Release(_coronaSlot);
        _coronaSlot = 0;
      }
      if (raw == null)
      {
        return;
      }
      var d3d = _device.Device;
      using var allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      var uploads = new List<ID3D12Resource>();
      _coronaSlot = _textures.Acquire(raw, list, uploads, out _);
      list.Close();
      _device.Queue.ExecuteCommandList(list);
      _device.WaitIdle();
      foreach (var upload in uploads)
      {
        upload.Dispose();
      }
    }

    /// <summary>The device it draws with.</summary>
    public D3D12Device Device => _device;

    /// <summary>
    /// A texture blob made on the GPU from any thread (a loading one: on the copy queue, waited for there); its number in
    /// the texture heap (0: none), for <see cref="UseCoronaSprite"/> or <see cref="UseVolumeLookup"/> on the drawing thread.
    /// </summary>
    public int PrepareTexture(byte[]? raw)
    {
      if (raw == null)
      {
        return 0;
      }
      var d3d = _device.Device;
      using var allocator = d3d.CreateCommandAllocator(CommandListType.Copy);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Copy, allocator, null);
      var uploads = new List<ID3D12Resource>();
      int slot = _textures.Acquire(raw, list, uploads, out _);
      list.Close();
      _device.CopyAndWait(list);
      foreach (var upload in uploads)
      {
        upload.Dispose();
      }
      return slot;
    }

    /// <summary>A prepared texture (PrepareTexture) as the coronas' sprite (the drawing thread; the one before let go once the GPU is past it).</summary>
    public void UseCoronaSprite(int slot)
    {
      int old = _coronaSlot;
      _coronaSlot = slot;
      if (old != 0)
      {
        _device.Retire(() => _textures.Release(old));
      }
    }

    /// <summary>A prepared texture (PrepareTexture) as the light volumes' lookup (the drawing thread; as UseCoronaSprite).</summary>
    public void UseVolumeLookup(int slot)
    {
      int old = _volumeLookupSlot;
      _volumeLookupSlot = slot;
      if (old != 0)
      {
        _device.Retire(() => _textures.Release(old));
      }
    }
    /// <summary>The coronas left out (for comparisons).</summary>
    public bool NoCoronas { get; set; }

    /// <summary>Prepared models drawn from now on (the drawing thread), in a group (see <see cref="RemoveGroup"/>); their numbers.</summary>
    public int[] Register(PreparedModels prepared, int group = 0)
    {
      var numbers = new int[prepared.Models.Count];
      for (int i = 0; i < numbers.Length; i++)
      {
        numbers[i] = _models.Count;
        if (prepared.Models[i] is Model model)
        {
          model.Group = group;
        }
        _models.Add(prepared.Models[i]);
      }
      return numbers;
    }

    /// <summary>
    /// A group's models and their instances gone (the drawing thread): no longer drawn from now on; their buffers and
    /// textures freed once the GPU is past the frames that may still read them (D3D12Device.Retire: not waited for here).
    /// </summary>
    public void RemoveGroup(int group)
    {
      for (int i = 0; i < _instances.Count; i++)
      {
        if (_instances[i] is Instance instance && _models[instance.Model] is Model model && model.Group == group)
        {
          _instances[i] = null;
        }
      }
      var gone = new List<Model>();
      for (int i = 0; i < _models.Count; i++)
      {
        if (_models[i] is Model model && model.Group == group)
        {
          gone.Add(model);
          _models[i] = null;
        }
      }
      if (gone.Count > 0)
      {
        _device.Retire(() => gone.ForEach(Free));
      }
    }

    /// <summary>
    /// Prepared models never registered let go, on another thread (the GPU is done with them: Prepare waited; freeing a
    /// location's thousands of buffers and textures took the render thread 1.9 s when a dropped location's load came in).
    /// </summary>
    public void Discard(PreparedModels prepared)
    {
      var models = prepared.Models.Where(m => m != null).ToList();
      prepared.Models.Clear();
      if (models.Count > 0)
      {
        _device.Retire(() => models.ForEach(m => Free(m!)));
      }
    }

    private void Free(Model model)
    {
      foreach (int texture in model.Textures)
      {
        _textures.Release(texture);
      }
      model.Dispose();
    }

    private Model? Build(ModelSource source, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, ref long pending)
    {
      var textures = new List<int>();
      var materials = new Dictionary<int, SceneMaterial>();
      foreach (var m in source.Materials)
      {
        materials[m.Id] = m;
      }
      var vertices = new List<float>();
      var skin = source.Skinned ? new List<float>() : null;
      // The extras only for a model with tangents or texture layers (step 4d).
      bool anyExtras = source.Meshes.Any(m => m.Tangents != null || m.LayerUvs != null);
      var extras = anyExtras ? new List<float>() : null;
      var indices = new List<uint>();
      var parts = new List<Part>();
      var partData = new List<uint>();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      long textureBytes = 0;
      int Texture(string? key)
      {
        byte[]? found = key != null && source.Textures.TryGetValue(key, out var b) ? b : null;
        if (key != null && found == null)
        {
          lock (MissingTextures)
          {
            MissingTextures.Add(key);
          }
        }
        int number = _textures.Acquire(found, list, uploads, out long bytes);
        textureBytes += bytes;
        textures.Add(number);
        return number;
      }
      // A model with dirt decals: all its decals drawn first (the page's dirtModel, CutsceneLocation.cs ~1332-1337).
      bool dirtModel = source.Materials.Any(x => x.Decal && x.DirtMask != null);
      foreach (var mesh in source.Meshes)
      {
        materials.TryGetValue(mesh.Material, out var material);
        // (A see-through glowing glass, gta_glass_emissive, as the page's: draw bucket 4 or 5 its glow alone (GlowApart),
        // else lit as glass.)
        if (mesh.Indices.Length == 0 || material == null || material.Hidden)
        {
          continue;
        }
        uint first = (uint)(vertices.Count / VertexFloats);
        int count = mesh.Positions.Length / 3;
        bool normals = mesh.Normals != null && mesh.Normals.Length >= count * 3;
        bool uvs = mesh.Uvs != null && mesh.Uvs.Length >= count * 2;
        bool alphas = mesh.Alphas != null && mesh.Alphas.Length >= count;
        bool colors = mesh.Colors != null && mesh.Colors.Length >= count * 3;
        bool tangents = mesh.Tangents != null && mesh.Tangents.Length >= count * 4;
        bool layerUvs = mesh.LayerUvs != null && mesh.LayerUvs.Length >= count * 6;
        bool layerWeights = mesh.LayerWeights != null && mesh.LayerWeights.Length >= count * 4;
        // Without vertex colours: a baked mesh 0.45 (the page's guess), others white (the page's defaults).
        float noColor = source.Baked && !material.Vehicle && !(material.EmissiveMultiplier != null) ? 0.45f : 1f;
        bool skinned = mesh.SkinBones != null && mesh.SkinWeights != null && mesh.SkinBones.Length >= count * 4 && mesh.SkinWeights.Length >= count * 4;
        var meshMin = new Vector3(float.MaxValue);
        var meshMax = new Vector3(float.MinValue);
        for (int v = 0; v < count; v++)
        {
          var p = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
          min = Vector3.Min(min, p);
          max = Vector3.Max(max, p);
          meshMin = Vector3.Min(meshMin, p);
          meshMax = Vector3.Max(meshMax, p);
          vertices.Add(p.X);
          vertices.Add(p.Y);
          vertices.Add(p.Z);
          vertices.Add(normals ? mesh.Normals![v * 3] : 0);
          vertices.Add(normals ? mesh.Normals![v * 3 + 1] : 0);
          vertices.Add(normals ? mesh.Normals![v * 3 + 2] : 1);
          vertices.Add(uvs ? mesh.Uvs![v * 2] : 0);
          vertices.Add(uvs ? mesh.Uvs![v * 2 + 1] : 0);
          vertices.Add(alphas ? mesh.Alphas![v] : 1);
          vertices.Add(colors ? mesh.Colors![v * 3] : noColor);
          vertices.Add(colors ? mesh.Colors![v * 3 + 1] : noColor);
          vertices.Add(colors ? mesh.Colors![v * 3 + 2] : noColor);
          if (skin != null)
          {
            for (int k = 0; k < 4; k++)
            {
              skin.Add(skinned ? mesh.SkinBones![v * 4 + k] : 0);
            }
            for (int k = 0; k < 4; k++)
            {
              skin.Add(skinned ? mesh.SkinWeights![v * 4 + k] : 0);
            }
          }
          if (extras != null)
          {
            for (int k = 0; k < 4; k++)
            {
              extras.Add(tangents ? mesh.Tangents![v * 4 + k] : 0);
            }
            for (int k = 0; k < 6; k++)
            {
              extras.Add(layerUvs ? mesh.LayerUvs![v * 6 + k] : 0);
            }
            for (int k = 0; k < 4; k++)
            {
              extras.Add(layerWeights ? mesh.LayerWeights![v * 4 + k] : 0);
            }
          }
        }
        uint start = (uint)indices.Count;
        foreach (ushort index in mesh.Indices)
        {
          indices.Add(first + index);
        }
        var look = source.Looks != null && source.Looks.TryGetValue(material.Id, out var found) ? found : null;
        int texture = Texture(material.TextureKey);
        bool textured = texture != 0;
        // Which pass (the page's: see-through = glass, or blended with a texture; untextured blended ones as before).
        var pass = material.Glass ? Pass.Glass : material.Decal || material.DirtMask != null ? Pass.Decal : material.Blend ? Pass.Blend : Pass.Solid;
        var flags = textured ? PartFlags.Textured : 0;
        var vehicleShine = material.VehicleShine is { Length: >= 3 } vs ? new Vector3(vs[0], vs[1], vs[2]) : new Vector3(0.15f, 190f, 0f);
        float fixedAlpha = 1;
        bool culled = false;
        if (material.Glass)
        {
          // A vehicle's glass (gta_vehicle_vehglass, textured) and the map's glass with vertex alpha: by the texture's
          // alpha, back faces culled, writing depth; other glass at 0.45, as a blended material (the page's).
          bool vehicleGlass = material.Vehicle && textured && vehicleShine.Z > 0.5f && vehicleShine.Z < 1.5f;
          bool mapGlass = !material.Vehicle && textured && alphas;
          if (vehicleGlass || mapGlass)
          {
            flags |= PartFlags.TextureAlpha | (vehicleGlass ? PartFlags.VehicleGlass : PartFlags.ForwardShine);
            culled = true;
          }
          else
          {
            flags |= PartFlags.FixedAlpha;
            fixedAlpha = 0.45f;
            pass = Pass.Blend;
          }
        }
        else if (pass == Pass.Blend || pass == Pass.Decal)
        {
          flags |= PartFlags.TextureAlpha;
          // The map's other see-through materials (the page's forwardMap): forward shine, culled, writing depth.
          if (pass == Pass.Blend && source.Baked && !source.Actor && !material.Vehicle && !material.Emissive)
          {
            // (With the lamps the game hands such a forward draw: the page's forwardLit, ModelView.html ~3497-3508.)
            flags |= PartFlags.ForwardShine | PartFlags.ForwardLit;
            pass = Pass.Glass;
            culled = true;
          }
          // A person's see-through part (gta_ped, bucket 1): its stipple's share of the alpha, forward shine.
          if (pass == Pass.Blend && material.Stipple)
          {
            flags |= PartFlags.Stipple | PartFlags.ForwardShine;
          }
          // A decal of the location takes the surface's light below (a normal decal only its AO); glue its own.
          if (pass == Pass.Decal && !source.Actor && !material.OwnLight)
          {
            flags |= material.DecalNormal ? PartFlags.DecalAoBelow : PartFlags.DecalBelow;
          }
        }
        if (material.VehicleLights)
        {
          flags |= PartFlags.VehicleLights;
        }
        // The maps: normal map, specular map, environment (each only with the texture itself).
        int normal = 0, specular = 0, environment = 0, dirtTexture = 0;
        var layer = new int[3];
        uint layersOn = 0;
        int layers = 1;
        if (textured)
        {
          normal = Texture(material.NormalKey);
          specular = Texture(material.SpecularKey);
          environment = material.Vehicle ? 0 : Texture(material.EnvironmentKey);
          flags |= (normal != 0 ? PartFlags.NormalMap : 0) | (specular != 0 ? PartFlags.SpecularMap : 0) |
                   (specular == 0 && material.SpecularFlat ? PartFlags.SpecularFlat : 0) |
                   (environment != 0 ? PartFlags.Environment : 0) | (material.EnvSpecular ? PartFlags.EnvironmentSpecular : 0);
          if (layerUvs && material.LayerCount > 1)
          {
            layers = material.LayerCount;
            for (int k = 0; k < material.LayerKeys.Count && k < 3; k++)
            {
              layer[k] = Texture(material.LayerKeys[k]);
              layersOn |= layer[k] != 0 ? 1u << k : 0;
            }
          }
          else if (layerUvs && material.Paint2Key != null)
          {
            layer[0] = Texture(material.Paint2Key);
            flags |= layer[0] != 0 ? PartFlags.Paint2 : 0;
          }
          if (material.Vehicle && material.DirtKey != null && look != null && look.DirtLevel > 0)
          {
            dirtTexture = Texture(material.DirtKey);
            flags |= dirtTexture != 0 ? PartFlags.Dirt : 0;
          }
        }
        var paint2 = look?.Paint2 ?? (material.Paint2 is { Length: >= 4 } p2 ? new Vector4(p2[0], p2[1], p2[2], p2[3]) : Vector4.One);
        var mask = material.SpecularMask is { Length: >= 3 } sm ? new Vector3(sm[0], sm[1], sm[2]) : Vector3.UnitX;
        var sub = material.SubSurface is { Length: >= 5 } ss ? ss : null;
        float lampGlow = material.VehicleLights && look != null && look.LightsOn ? material.EmissiveMultiplier ?? 0 : 0;
        int partIndex = parts.Count;
        AddPart(partData, new uint[] { (uint)texture, (uint)normal, (uint)specular, (uint)environment },
                new uint[] { (uint)layer[0], (uint)layer[1], (uint)layer[2], (uint)dirtTexture },
                new uint[] { (uint)flags, (uint)layers, layersOn, 0 },
                new float[]
                {
                  material.SpecularPower, material.SpecularIntensity, material.Bumpiness, material.Parallax,
                  mask.X, mask.Y, mask.Z, look?.ReflectivePower ?? material.ReflectivePower,
                  paint2.X, paint2.Y, paint2.Z, paint2.W,
                  material.DirtColor.Length >= 3 ? material.DirtColor[0] : 0.23f, material.DirtColor.Length >= 3 ? material.DirtColor[1] : 0.22f,
                  material.DirtColor.Length >= 3 ? material.DirtColor[2] : 0.2f, material.Vehicle ? look?.DirtLevel ?? 0 : 0,
                  vehicleShine.X, vehicleShine.Y, vehicleShine.Z, 0,
                  look?.Specular2Color.X ?? 0, look?.Specular2Color.Y ?? 0, look?.Specular2Color.Z ?? 0, look?.Specular2Exponent ?? 0,
                  sub?[0] ?? 0, sub?[1] ?? 0, sub?[2] ?? 0, sub?[3] ?? 0,
                  look?.Specular2Strength ?? 0, sub?[4] ?? 0, fixedAlpha, lampGlow,
                });
        var dirt = material.DirtMask is { Length: >= 3 } dm ? new Vector3(dm[0], dm[1], dm[2]) : (Vector3?)null;
        var tint = source.Tints != null && source.Tints.TryGetValue(material.Id, out var t) ? t : Vector3.One;
        // Without a texture a colour of its own, a render target (script_rt: a TV switched off) black - the page's
        // material color (ModelScene's message: Color ?? (RenderTarget ? 0 : null)), its base colour when untextured.
        if (!textured && (material.Color is { Length: >= 3 } || material.RenderTarget))
        {
          tint = material.Color is { Length: >= 3 } own ? new Vector3(own[0], own[1], own[2]) : Vector3.Zero;
        }
        // Its kind for the light (see the shader): vehicles are never baked; a glowing material only with a texture,
        // and only solid (the see-through ones glow apart: not yet).
        bool baked = source.Baked && !material.Vehicle;
        bool glowing = material.EmissiveMultiplier != null && textured && pass != Pass.Blend && pass != Pass.Glass;
        // A see-through one of draw bucket 4 or 5 (the page's mat.emissive, textured): its glow drawn apart, its lit draw
        // (when there is one) by 1 - its alpha if gta_emissive (the page's glowOver uniform: not strong, not night).
        bool glowApart = material.Emissive && textured && pass is Pass.Blend or Pass.Glass;
        bool glowLit = glowApart && !material.EmissiveStrong && !material.EmissiveAtNight;
        int kind = (baked ? 1 : 0) | (material.Ped ? 2 : 0) | (material.Vehicle ? 4 : 0) | (material.Tree ? 8 : 0) |
                   (glowing ? 16 : 0) | (material.EmissiveAtNight ? 32 : 0) | (material.EmissiveStrong ? 64 : 0) | (glowLit ? 128 : 0);
        parts.Add(new Part(start, (uint)mesh.Indices.Length, texture, pass, material.Cutout, dirt, tint)
        {
          Kind = kind,
          Glow = glowing ? material.EmissiveMultiplier!.Value : 0,
          // The page culls the back faces of map objects' and props' solids (not trees'), vehicle glass, the map's
          // glass and see-through materials.
          Culled = culled || (baked && !material.Tree && pass == Pass.Solid),
          Index = partIndex,
          SeeThrough = material.Glass || (material.Blend && textured),
          AlphaShadow = material.ShadowByAlpha,
          Layered = layerUvs && material.LayerCount > 1,
          DirtFirst = pass == Pass.Decal && (material.DirtMask != null || (material.Decal && source.Baked && !source.Actor && dirtModel)),
          GlowApart = glowApart,
          Pulled = material.Blend && !material.Glass && !material.Decal,
          GlowMultiplier = material.EmissiveMultiplier ?? 0,
          GlowNight = material.EmissiveAtNight,
          GlowStrong = material.EmissiveStrong,
          GlowGlass = material.Glass,
          GlowBlend = material.Blend,
          Center = count > 0 ? (meshMin + meshMax) * 0.5f : Vector3.Zero,
          Radius = count > 0 ? (meshMax - meshMin).Length() * 0.5f : float.PositiveInfinity,
        });
      }
      pending += textureBytes;
      if (indices.Count == 0)
      {
        foreach (int texture in textures)
        {
          _textures.Release(texture);
        }
        return null;
      }
      var model = new Model { Min = min, Max = max, IndexCount = (uint)indices.Count, Parts = parts.ToArray(), Actor = source.Actor };
      model.Textures.AddRange(textures);
      pending += (long)vertices.Count * 4 + (long)indices.Count * 4 + (skin?.Count ?? 0) * 4L + (extras?.Count ?? 0) * 4L + partData.Count * 4L;
      model.Vertices = Upload(vertices.ToArray(), list, uploads, out ulong vertexBytes);
      model.Indices = Upload(indices.ToArray(), list, uploads, out ulong indexBytes);
      model.VertexView = new VertexBufferView(model.Vertices.GPUVirtualAddress, (uint)vertexBytes, VertexFloats * 4);
      model.IndexView = new IndexBufferView(model.Indices.GPUVirtualAddress, (uint)indexBytes, Format.R32_UInt);
      if (skin != null)
      {
        model.Skin = Upload(skin.ToArray(), list, uploads, out ulong skinBytes);
        model.SkinView = new VertexBufferView(model.Skin.GPUVirtualAddress, (uint)skinBytes, SkinFloats * 4);
      }
      if (extras != null)
      {
        model.Extras = Upload(extras.ToArray(), list, uploads, out ulong extraBytes);
        model.ExtrasView = new VertexBufferView(model.Extras.GPUVirtualAddress, (uint)extraBytes, ExtraFloats * 4);
      }
      else
      {
        model.ExtrasView = new VertexBufferView(_noExtras.GPUVirtualAddress, 256, 0);
      }
      model.PartData = Upload(partData.ToArray(), list, uploads, out _, ResourceStates.AllShaderResource);
      return model;
    }

    // A part's PartData (see the shader): three uint4, then eight float4.
    private static void AddPart(List<uint> into, uint[] tex, uint[] tex2, uint[] bits, float[] numbers)
    {
      into.AddRange(tex);
      into.AddRange(tex2);
      into.AddRange(bits);
      foreach (float n in numbers)
      {
        into.Add(BitConverter.SingleToUInt32Bits(n));
      }
    }

    /// <summary>An array into a new buffer in a default heap, its copy from an upload buffer recorded in the list.</summary>
    private unsafe ID3D12Resource Upload<T>(T[] data, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, out ulong bytes,
                                           ResourceStates state = ResourceStates.VertexAndConstantBuffer | ResourceStates.IndexBuffer) where T : unmanaged
    {
      var d3d = _device.Device;
      bytes = (ulong)(data.Length * sizeof(T));
      var buffer = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                                               ResourceDescription.Buffer(bytes), ResourceStates.Common);
      var upload = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                               ResourceDescription.Buffer(bytes), ResourceStates.GenericRead);
      uploads.Add(upload);
      void* mapped;
      upload.Map(0, null, &mapped).CheckError();
      fixed (T* source = data)
      {
        Buffer.MemoryCopy(source, mapped, (long)bytes, (long)bytes);
      }
      upload.Unmap(0);
      list.ResourceBarrierTransition(buffer, ResourceStates.Common, ResourceStates.CopyDest);
      list.CopyBufferRegion(buffer, 0, upload, 0, bytes);
      // (On the copy queue the buffer is left to decay to COMMON, which the direct queue promotes to any read state.)
      if (list.CommandListType != CommandListType.Copy)
      {
        list.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, state);
      }
      return buffer;
    }

    /// <summary>
    /// A model drawn with a model-to-scene matrix (and, skinned, its bones' matrices); its instance's number.
    /// <paramref name="castsShadow"/>: its solid parts drawn into the lights' shadow maps (while visible);
    /// <paramref name="castsSunShadow"/>: drawn into the sun's shadow map (while visible; see <see cref="SunShadow"/>).
    /// <paramref name="mirrored"/>: drawn into the water's mirror (see <see cref="DrawMirror"/>); <paramref name="mainView"/>
    /// false: only there (not in the main view nor any shadow map).
    /// </summary>
    public int AddInstance(int model, Matrix4x4 world, Matrix4x4[]? palette = null, bool inRoom = false, bool castsShadow = false,
                           bool castsSunShadow = true, bool mirrored = false, bool mainView = true)
    {
      _instances.Add(new Instance
      {
        Model = model, World = world, Palette = palette, InRoom = inRoom, CastsShadow = castsShadow && mainView,
        CastsSunShadow = castsSunShadow && mainView, Mirrored = mirrored, MainView = mainView,
      });
      return _instances.Count - 1;
    }

    /// <summary>A skinned instance's bones' matrices now (rest to now; see CutscenePose).</summary>
    public void SetPalette(int instance, Matrix4x4[] palette)
    {
      if (_instances[instance] is Instance i)
      {
        i.Palette = palette;
      }
    }

    /// <summary>A model's box (its own space), or null when it draws nothing.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds(int model) =>
      model >= 0 && model < _models.Count && _models[model] is Model m ? (m.Min, m.Max) : null;

    /// <summary>The device's profiler when profiling is on, else null (see <see cref="D3D12Device.Profiler"/>).</summary>
    public GpuProfiler? Profiler => _device.Profiler;

    /// <summary>All models and instances gone (the GPU done with them first), their textures let go.</summary>
    public void Clear()
    {
      _device.WaitIdle();
      // (What RemoveGroup retired, freed now while the texture cache is still there.)
      _device.ReleaseRetiredNow();
      foreach (var model in _models)
      {
        if (model != null)
        {
          Free(model);
        }
      }
      _models.Clear();
      _instances.Clear();
    }

    /// <summary>
    /// Draws every instance into the light target (cleared by the caller) as seen from <paramref name="eye"/> with the
    /// view-projection, in the frame's light: solid (also into the G-buffer), decals, the <paramref name="lights"/>
    /// over them (a light volume each), what <paramref name="afterSolid"/> draws (water), then the see-through
    /// materials. Clears the depth.
    /// </summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, CpuDescriptorHandle renderTarget, CpuDescriptorHandle depth,
                            int width, int height, Matrix4x4 viewProjection, Vector3 eye, FrameLight light,
                            Action<ID3D12GraphicsCommandList>? afterSolid = null, IReadOnlyList<DeferredLight>? lights = null,
                            ID3D12Resource? lightTexture = null, IReadOnlyList<Corona>? coronas = null, ID3D12Resource? depthTexture = null,
                            IReadOnlyList<LightVolume>? volumes = null,
                            Action<ID3D12GraphicsCommandList, CpuDescriptorHandle, int>? parabolicSky = null, float parabolicHaze = 0)
    {
      list.OMSetRenderTargets(renderTarget, depth);
      list.ClearDepthStencilView(depth, ClearFlags.Depth, 1.0f, 0);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      DrawCalls = 0;
      LightsDrawn = 0;
      if (_instances.Count == 0)
      {
        // Nothing to draw yet (the scene still loading): the targets the drawing will need made now, one kind a frame,
        // not by the first frame that draws a location.
        MakeTargetsAhead(width, height, depthTexture);
        return;
      }
      var profiler = _device.Profiler;
      SizeGBuffer(width, height);
      var gTargets = new[] { renderTarget, GBufferView(0), GBufferView(1), GBufferView(2) };
      profiler?.Begin(list, "G-buffer clear");
      for (int i = 1; i < gTargets.Length; i++)
      {
        list.ClearRenderTargetView(gTargets[i], new Vortice.Mathematics.Color4(0, 0, 0, 0));
      }
      profiler?.End(list, "G-buffer clear");

      // The skinned instances' bones into this frame's slot of the palette buffer.
      _paletteSlot = (_paletteSlot + 1) % PaletteSlots;
      int used = 0;
      foreach (var instance in _instances)
      {
        if (instance == null)
        {
          continue;
        }
        instance.PaletteAddress = 0;
        if (instance.Visible && instance.Palette is Matrix4x4[] palette && palette.Length > 0 && used + palette.Length <= PaletteCapacity)
        {
          long at = (long)_paletteSlot * PaletteCapacity + used;
          fixed (Matrix4x4* source = palette)
          {
            Buffer.MemoryCopy(source, _paletteMapped + at, palette.Length * 64L, palette.Length * 64L);
          }
          instance.PaletteAddress = _palette.GPUVirtualAddress + (ulong)at * 64;
          used += palette.Length;
        }
      }

      // The frame's constants into this frame's slot.
      var frame = (float*)(_frameMapped + _paletteSlot * FrameSlotBytes);
      *(Matrix4x4*)frame = viewProjection;
      void Put(int at, Vector3 v, float w = 0)
      {
        frame[at] = v.X;
        frame[at + 1] = v.Y;
        frame[at + 2] = v.Z;
        frame[at + 3] = w;
      }
      Put(16, eye);
      Put(20, Vector3.Normalize(light.SunDirection));
      Put(24, light.Sun);
      Put(28, light.AmbientAbove);
      Put(32, light.AmbientBelow);
      frame[36] = light.DayNight;
      frame[37] = light.Exposure;
      _dayNight = light.DayNight;
      _exposure = light.Exposure;
      frame[38] = light.PedAoScale;
      frame[39] = light.AoScale;
      frame[40] = width;
      frame[41] = height;
      // The surface under the decals: its texture number (0: none).
      frame[42] = NoDecalBelow ? 0 : _belowSlot;
      frame[43] = 0;
      frame[44] = light.ShineScale;
      frame[45] = 0;
      frame[46] = 0;
      frame[47] = 0;
      // The fog (FrameLight.Fog).
      Put(48, light.FogNear, light.FogStart);
      Put(52, light.FogFar, light.FogEnd);
      frame[56] = light.FogNearAmount;
      frame[57] = light.Fog && !NoFog ? 1 : 0;
      frame[58] = light.DrawReach > 0 && float.IsFinite(light.DrawReach) ? light.DrawReach : 0;
      frame[59] = 0;
      // The sun's shadow (FrameLight.SunShadow): its axes, cascades and fade; the map's number once drawn (below).
      var sun = light.SunShadow;
      // Not drawn where the sun adds nothing: every read of the map is multiplied by the sun's colour (Shade's LitBy and
      // shine, the mirror's and the map's ReflectTexture light, the water's glint, light and foam), so with the sun 0 the
      // map's value can't reach a pixel - without it SunShadow is 1, and 0 x 1 = 0 x anything the map holds: the same
      // picture, bit for bit. (In a room the time cycle's sun x SunMultiplier x Exposure can be 0.)
      SunSkipped = sun != null && light.Sun == Vector3.Zero && !KeepDarkSunShadow;
      bool sunOn = sun != null && sun.Boxes.Length >= SunCascades && sun.Biases.Length >= SunCascades && _sunSlot > 0 && !SunSkipped;
      new Span<float>(frame + 60, FrameFloats - 60).Clear();
      if (sunOn)
      {
        Put(60, sun!.U, sun.FadeScale);
        Put(64, sun.V);
        Put(68, sun.H, sun.Reference);
        Put(72, sun.ViewForward, _sunSlot);
        Put(76, sun.Splits, sun.FadeEnd);
        for (int k = 0; k < SunCascades; k++)
        {
          *(Vector4*)(frame + 80 + k * 4) = sun.Boxes[k];
        }
      }
      // The fog's depth effect (the solid scene's only with the light's copy: Screen.w).
      bool depthFx = light.Fog && !NoFog && !NoDepthFx && light.DepthFx.W > light.DepthFx.Z;
      *(Vector4*)(frame + 96) = depthFx ? light.DepthFx : Vector4.Zero;
      frame[43] = depthFx && lightTexture != null && _lightCopy != null ? 1 : 0;

      ulong frameAddress = _frame.GPUVirtualAddress + (ulong)(_paletteSlot * FrameSlotBytes);
      ulong lightsAddress = _lights.GPUVirtualAddress + (ulong)((long)_paletteSlot * LightCapacity * LightBytes);

      // The heavy passes recorded on worker threads into command lists of their own while this thread records the rest:
      // the sun's cascades (a list each), the mirrored surroundings' map's objects, the solid pass (four lists of
      // consecutive draws), the decals, the see-through pass (two lists). Each is sent where its pass is drawn (Splice), so
      // the GPU gets the same draws in the same order (ParallelRecording off: drawn into this list as before).
      BeginRecorders();
      bool parabolicOn = parabolicSky != null && !NoParabolic;
      Task<Recorder>[]? sunLists = null, solidLists = null, decalLists = null, blendLists = null;
      Task<Recorder>? parabolicList = null;
      Task<Recorder>[] Chunks(string name, Pass pass, bool gBuffer, int chunks) =>
        Enumerable.Range(0, chunks).Select(c => Record(name, (l, r) =>
        {
          BindOn(l, gBuffer);
          r.Draws += DrawPass(l, pass, c, chunks);
        })).ToArray();
      if (ParallelRecording)
      {
        if (sunOn)
        {
          SizeSun();
          sunLists = Enumerable.Range(0, SunCascades).Select(k => Record("  worker: sun shadow", (l, r) => DrawCascade(l, sun!, frameAddress, k, r))).ToArray();
        }
        if (parabolicOn)
        {
          float reachNow = light.DrawReach;
          parabolicList = Record("  worker: parabolic map", (l, r) =>
          {
            SetupParabolic(l);
            DrawParabolicParts(l, r, eye, parabolicHaze, reachNow);
          });
        }
        solidLists = Chunks("  worker: G-buffer solid", Pass.Solid, true, 4);
        decalLists = Chunks("  worker: decals", Pass.Decal, true, 1);
        blendLists = Chunks("  worker: see-through + glows", Pass.Blend, false, 2);
      }
      // The shadowed lights' maps (step 5) and the sun's, before anything else is drawn.
      ShadowMapsDrawn = 0;
      if (lights != null)
      {
        profiler?.Begin(list, "lamp shadow atlas", DrawCalls);
        DrawShadowMaps(list, lights);
        profiler?.End(list, "lamp shadow atlas", DrawCalls);
      }
      SunShadowDrawn = false;
      profiler?.Count("sun shadow left out (sun 0)", SunSkipped ? 1 : 0);
      if (sunOn)
      {
        profiler?.Begin(list, "sun shadow", DrawCalls);
        DrawSunShadow(list, sun!, frameAddress, sunLists);
        profiler?.End(list, "sun shadow", DrawCalls);
        SunShadowDrawn = true;
      }
      // The mirrored surroundings' map (outside: the caller hands the sky's drawing into it), read by the shine.
      frame[45] = light.GlobalScalarsW;
      frame[46] = 0;
      frame[47] = DebugView;
      if (parabolicOn)
      {
        profiler?.Begin(list, "parabolic map", DrawCalls);
        DrawParabolic(list, eye, parabolicSky!, parabolicHaze, light.DrawReach, parabolicList);
        profiler?.End(list, "parabolic map", DrawCalls);
        frame[46] = _parabolicSlot;
      }
      // The main passes' states into a list (this frame's or a worker's).
      void BindOn(ID3D12GraphicsCommandList l, bool gBuffer)
      {
        if (gBuffer)
        {
          l.OMSetRenderTargets(gTargets, depth);
        }
        else
        {
          l.OMSetRenderTargets(renderTarget, depth);
        }
        l.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
        l.RSSetScissorRect(width, height);
        l.SetGraphicsRootSignature(_rootSignature);
        l.SetDescriptorHeaps(_textures.Heap);
        l.SetGraphicsRootConstantBufferView(3, frameAddress);
        l.SetGraphicsRootDescriptorTable(1, _textures.Handle(0));
        l.SetGraphicsRootShaderResourceView(5, lightsAddress);
        l.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      }
      void Bind(bool gBuffer) => BindOn(list, gBuffer);
      Bind(true);
      profiler?.Begin(list, "G-buffer solid", DrawCalls);
      if (solidLists != null)
      {
        Splice(list, solidLists);
      }
      else
      {
        Inline(list, (l, b) => b.Draws += DrawPass(l, Pass.Solid));
      }
      profiler?.End(list, "G-buffer solid", DrawCalls);
      profiler?.Begin(list, "decals", DrawCalls);
      // The surface's normal and AO as the solid pass left them, for the decals taking its light (step 4d).
      if (_below != null)
      {
        list.ResourceBarrierTransition(_gBuffer[1]!, ResourceStates.RenderTarget, ResourceStates.CopySource);
        list.ResourceBarrierTransition(_below, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
        list.CopyResource(_below, _gBuffer[1]!);
        list.ResourceBarrierTransition(_below, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        list.ResourceBarrierTransition(_gBuffer[1]!, ResourceStates.CopySource, ResourceStates.RenderTarget);
      }
      if (decalLists != null)
      {
        Splice(list, decalLists);
      }
      else
      {
        Inline(list, (l, b) => b.Draws += DrawPass(l, Pass.Decal));
      }
      profiler?.End(list, "decals", DrawCalls);
      // The lamps over the solid scene and its decals (step 5); the water and see-through materials come after
      // (forward, without them for now).
      if (lights != null && lights.Count > 0)
      {
        profiler?.Begin(list, "lamps (light pass)", DrawCalls);
        DrawLights(list, renderTarget, depth, width, height, lights, frameAddress);
        profiler?.End(list, "lamps (light pass)", DrawCalls);
      }
      // How many lights the map's see-through materials lit forward read (written before the list runs).
      frame[59] = NoForwardLamps ? 0 : LightsDrawn;
      // The fog over all of it (the see-through materials and the water take theirs as they are drawn).
      if (light.Fog && !NoFog)
      {
        profiler?.Begin(list, "fog + depth effect", DrawCalls);
        // (The light as it is now, for the depth effect.)
        if (frame[43] > 0.5f)
        {
          list.ResourceBarrierTransition(lightTexture!, ResourceStates.RenderTarget, ResourceStates.CopySource);
          list.ResourceBarrierTransition(_lightCopy!, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
          list.CopyResource(_lightCopy, lightTexture);
          list.ResourceBarrierTransition(_lightCopy!, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
          list.ResourceBarrierTransition(lightTexture!, ResourceStates.CopySource, ResourceStates.RenderTarget);
        }
        DrawFog(list, renderTarget, depth, width, height, frameAddress);
        profiler?.End(list, "fog + depth effect", DrawCalls);
      }
      // The solid scene's depth for the coronas (the page's gameKeepSolidDepth, before the water and the glass).
      bool coronasOn = coronas != null && coronas.Count > 0 && _coronaSlot != 0 && depthTexture != null && !NoCoronas;      if (coronasOn)
      {
        profiler?.Begin(list, "coronas", DrawCalls);
        KeepSolidDepth(list, depthTexture!, width, height);
        profiler?.End(list, "coronas", DrawCalls);
      }
      // What goes between the solid scene and the see-through materials (the water), with its own states.
      afterSolid?.Invoke(list);
      Bind(false);
      profiler?.Begin(list, "see-through + glows", DrawCalls);
      if (blendLists != null)
      {
        Splice(list, blendLists);
        // (The list goes on without states: the volumes and coronas below draw with the main passes' ones.)
        Bind(false);
      }
      else
      {
        Inline(list, (l, b) => b.Draws += DrawPass(l, Pass.Blend));
      }
      profiler?.End(list, "see-through + glows", DrawCalls);
      // After the scene, as the page's gamePostEnd (gtaiv-pc.js ~1504-1507): the lamps' light volumes against the whole
      // depth, then the coronas.
      if (volumes != null && volumes.Count > 0 && depthTexture != null && !NoVolumes)
      {
        profiler?.Begin(list, "volumes", DrawCalls);
        CopyDepth(list, depthTexture, width, height, ref _fullDepth, ref _fullDepthWidth, ref _fullDepthHeight, _fullDepthSlot);
        DrawVolumes(list, volumes, eye);
        profiler?.End(list, "volumes", DrawCalls);
      }
      if (coronasOn)
      {
        profiler?.Begin(list, "coronas", DrawCalls);
        DrawCoronas(list, coronas!, viewProjection);
        profiler?.End(list, "coronas", DrawCalls);
      }
    }

    // One kind of target a call, made ahead while nothing is drawn: the G-buffer (with the decals' and the light's
    // copies), the sun's shadow targets, the depth copies of the coronas and the light volumes.
    private void MakeTargetsAhead(int width, int height, ID3D12Resource? depthTexture)
    {
      if (_gBuffer[0] == null || _gWidth != width || _gHeight != height)
      {
        SizeGBuffer(width, height);
      }
      else if (_sunMap == null)
      {
        SizeSun();
      }
      else if (depthTexture != null && (_solidDepth == null || _solidDepthWidth != width || _solidDepthHeight != height))
      {
        MakeDepthCopy(width, height, ref _solidDepth, ref _solidDepthWidth, ref _solidDepthHeight, _solidDepthSlot);
      }
      else if (depthTexture != null && (_fullDepth == null || _fullDepthWidth != width || _fullDepthHeight != height))
      {
        MakeDepthCopy(width, height, ref _fullDepth, ref _fullDepthWidth, ref _fullDepthHeight, _fullDepthSlot);
      }
    }
    // The depth (D32, DEPTH_WRITE) copied into _solidDepth (read as R32F by its number in the texture heap).
    private void KeepSolidDepth(ID3D12GraphicsCommandList list, ID3D12Resource depth, int width, int height) =>
      CopyDepth(list, depth, width, height, ref _solidDepth, ref _solidDepthWidth, ref _solidDepthHeight, _solidDepthSlot);

    // The depth (D32, DEPTH_WRITE) copied into a copy of its own (made at the size; read as R32F by slot).
    private void CopyDepth(ID3D12GraphicsCommandList list, ID3D12Resource depth, int width, int height,
                           ref ID3D12Resource? copy, ref int copyWidth, ref int copyHeight, int slot)
    {
      if (copy == null || copyWidth != width || copyHeight != height)
      {
        MakeDepthCopy(width, height, ref copy, ref copyWidth, ref copyHeight, slot);
      }
      list.ResourceBarrierTransition(copy!, ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
      list.ResourceBarrierTransition(depth, ResourceStates.DepthWrite, ResourceStates.CopySource);
      list.CopyResource(copy, depth);
      list.ResourceBarrierTransition(depth, ResourceStates.CopySource, ResourceStates.DepthWrite);
      list.ResourceBarrierTransition(copy, ResourceStates.CopyDest, ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
    }

    // A depth copy at the size (the one before freed, the GPU done with it first), left readable (as after a copy).
    private void MakeDepthCopy(int width, int height, ref ID3D12Resource? copy, ref int copyWidth, ref int copyHeight, int slot)
    {
      var d3d = _device.Device;
      if (copy != null)
      {
        _device.WaitIdle();
        copy.Dispose();
      }
      copy = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.D32_Float, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource, new ClearValue(Format.D32_Float, 1.0f, 0));
      copyWidth = width;
      copyHeight = height;
      d3d.CreateShaderResourceView(copy, new ShaderResourceViewDescription
      {
        Format = Format.R32_Float,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
      }, _textures.CpuHandle(slot));
    }
    // The light volumes' unit meshes as GTAIV.exe makes them at its start (the page's gameLightVolumeMesh, gtaiv-pc.js
    // ~1563-1595; 0xABE3A0): a spot's (n 5, its lower four eighths, a fan at its rim), a point lamp's (n 4, all eight) as
    // triangle strips; and c213 (gDeferredVolumeRadiusScale) from n.
    private static (float[] Data, float Scale) LightVolumeMesh(int n, int eighths, bool fan)
    {
      var output = new List<float>();
      void Push(Vector3 p)
      {
        output.Add(p.X);
        output.Add(p.Y);
        output.Add(p.Z);
      }
      for (int b = 0; b < eighths; b++)
      {
        int lx = (b & 1) != 0 ? 1 : -1, ly = (b & 2) != 0 ? 1 : -1, z = (b & 4) != 0 ? 1 : -1;
        int u = lx == ly ? 1 : 0;
        if (z < 0)
        {
          u = 1 - u;
        }
        var a = u == 1 ? new Vector3(lx, 0, 0) : new Vector3(0, ly, 0);
        var bb = u == 1 ? new Vector3(0, ly, 0) : new Vector3(lx, 0, 0);
        Vector3 Side(Vector3 p, float t) => new Vector3(p.X - p.X * t, p.Y - p.Y * t, p.Z + (z - p.Z) * t);
        for (int k = 0; k < n; k++)
        {
          int m = n - k;
          Vector3 p0 = Side(a, k / (float)n), q0 = Side(bb, k / (float)n), p1 = Side(a, (k + 1) / (float)n), q1 = Side(bb, (k + 1) / (float)n);
          if (fan && k == 0)
          {
            for (int j = 0; j < m; j++)
            {
              if (j == 0)
              {
                Push(Vector3.Zero);
              }
              Push(Vector3.Zero);
              Push(Vector3.Lerp(p0, q0, j / (float)m));
              if (j == m - 1)
              {
                Push(q0);
                Push(q0);
                Push(q0);
              }
            }
          }
          for (int j = 0; j < m; j++)
          {
            var p = Vector3.Lerp(p0, q0, j / (float)m);
            if (j == 0)
            {
              Push(p);
            }
            Push(p);
            Push(Vector3.Lerp(p1, q1, m > 1 ? j / (float)(m - 1) : j / (float)m));
            if (j == m - 1)
            {
              Push(q0);
              Push(q0);
              Push(q0);
            }
          }
        }
      }
      double an = Math.PI / 2 / n, c = (2 * Math.Cos(an) + 1) / 3, s = Math.Sin(an) / 3;
      return (output.ToArray(), (float)(1 / (c * c + 2 * s * s)));
    }

    // A unit mesh into an upload-heap vertex buffer (small, read in place): the buffer, its vertex count, its scale.
    private static unsafe (ID3D12Resource, int, float) VolumeBuffer(ID3D12Device d3d, (float[] Data, float Scale) mesh)
    {
      var buffer = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                               ResourceDescription.Buffer((ulong)mesh.Data.Length * 4), ResourceStates.GenericRead);
      void* mapped;
      buffer.Map(0, null, &mapped);
      fixed (float* source = mesh.Data)
      {
        Buffer.MemoryCopy(source, mapped, mesh.Data.Length * 4L, mesh.Data.Length * 4L);
      }
      buffer.Unmap(0);
      return (buffer, mesh.Data.Length / 3, mesh.Scale);
    }

    /// <summary>
    /// The light volumes' lookup texture (a texture blob; GTA IV: lights_occluders.wtd's "volume"), onto the GPU now;
    /// null: none (the volumes that need it aren't drawn: CutsceneLights.VolumesNow's hasLookup).
    /// </summary>
    public void SetVolumeLookup(byte[]? raw)
    {
      if (_volumeLookupSlot != 0)
      {
        _device.WaitIdle();
        _textures.Release(_volumeLookupSlot);
        _volumeLookupSlot = 0;
      }
      if (raw == null)
      {
        return;
      }
      var d3d = _device.Device;
      using var allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      var uploads = new List<ID3D12Resource>();
      _volumeLookupSlot = _textures.Acquire(raw, list, uploads, out _);
      list.Close();
      _device.Queue.ExecuteCommandList(list);
      _device.WaitIdle();
      foreach (var upload in uploads)
      {
        upload.Dispose();
      }
    }

    /// <summary>The light volumes left out (for comparisons).</summary>
    public bool NoVolumes { get; set; }

    // The lamps' light volumes over the light (see VSVolume), the light target and the frame's constants bound.
    private unsafe void DrawVolumes(ID3D12GraphicsCommandList list, IReadOnlyList<LightVolume> volumes, Vector3 eye)
    {
      list.SetPipelineState(_volumePipeline);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
      var draw = stackalloc float[DrawConstants];
      new Span<float>(draw, DrawConstants).Clear();
      draw[32] = _fullDepthSlot;
      draw[33] = _volumeLookupSlot;
      foreach (var v in volumes)
      {
        bool spot = v.Cone > 0;
        float scale = spot ? _spotVolumeScale : _pointVolumeScale;
        var buffer = spot ? _spotVolume : _pointVolume;
        int count = spot ? _spotVolumeCount : _pointVolumeCount;
        draw[0] = v.At.X; draw[1] = v.At.Y; draw[2] = v.At.Z; draw[3] = v.Cone;
        draw[4] = v.Direction.X; draw[5] = v.Direction.Y; draw[6] = v.Direction.Z; draw[7] = scale;
        draw[8] = v.Across.X; draw[9] = v.Across.Y; draw[10] = v.Across.Z; draw[11] = scale * v.Radius * 0.662f;
        var fromLamp = eye - v.At;
        draw[12] = fromLamp.X; draw[13] = fromLamp.Y; draw[14] = fromLamp.Z; draw[15] = v.Radius;
        draw[28] = v.Color.X * _exposure; draw[29] = v.Color.Y * _exposure; draw[30] = v.Color.Z * _exposure;
        draw[31] = v.Lookup ? 1 : 0;
        list.SetGraphicsRoot32BitConstants(0, DrawConstants, draw, 0);
        list.IASetVertexBuffers(0, new VertexBufferView(buffer.GPUVirtualAddress, (uint)count * 12, 12));
        list.DrawInstanced((uint)count, 1, 0, 0);
        DrawCalls++;
      }
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    // The coronas over the light (see VSCorona), the light target and the frame's constants bound (Bind).
    private unsafe void DrawCoronas(ID3D12GraphicsCommandList list, IReadOnlyList<Corona> coronas, Matrix4x4 viewProjection)
    {
      // The camera's right, up and forward from the view-projection's columns (LookAt x perspective).
      var right = Vector3.Normalize(new Vector3(viewProjection.M11, viewProjection.M21, viewProjection.M31));
      var up = Vector3.Normalize(new Vector3(viewProjection.M12, viewProjection.M22, viewProjection.M32));
      var forward = Vector3.Normalize(new Vector3(viewProjection.M14, viewProjection.M24, viewProjection.M34));
      list.SetPipelineState(_coronaPipeline);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      var draw = stackalloc float[DrawConstants];
      new Span<float>(draw, DrawConstants).Clear();
      draw[0] = right.X; draw[1] = right.Y; draw[2] = right.Z;
      draw[4] = up.X; draw[5] = up.Y; draw[6] = up.Z;
      draw[8] = forward.X; draw[9] = forward.Y; draw[10] = forward.Z;
      draw[32] = _coronaSlot;
      draw[33] = _solidDepthSlot;
      foreach (var c in coronas)
      {
        draw[24] = c.At.X; draw[25] = c.At.Y; draw[26] = c.At.Z;
        draw[28] = c.Color.X * _exposure; draw[29] = c.Color.Y * _exposure; draw[30] = c.Color.Z * _exposure;
        draw[31] = c.Size;
        list.SetGraphicsRoot32BitConstants(0, DrawConstants, draw, 0);
        list.DrawInstanced(6, 1, 0, 0);
        DrawCalls++;
      }
    }

    // The shadow maps of the lights that have a slot (each slot's first; see DeferredLight.ShadowSlot): the atlas cleared
    // to 1 (nothing there), then per light both halves drawn from the visible instances that cast, their solid parts
    // (nothing cut out, as the page's), each into its tile. _slotOf: the light (its index) each slot was drawn for.
    private unsafe void DrawShadowMaps(ID3D12GraphicsCommandList list, IReadOnlyList<DeferredLight> lights)
    {
      Array.Fill(_slotOf, -1);
      bool any = false;
      for (int i = 0; i < lights.Count; i++)
      {
        if (lights[i].Shadowed && lights[i].ShadowSlot is int s && s >= 0 && s < ShadowSlots && _slotOf[s] < 0 && lights[i].Range > 0)
        {
          _slotOf[s] = i;
          any = true;
        }
      }
      if (!any)
      {
        return;
      }
      list.ResourceBarrierTransition(_shadowAtlas, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      var rtv = _shadowRtv.GetCPUDescriptorHandleForHeapStart();
      var dsv = _shadowDsv.GetCPUDescriptorHandleForHeapStart();
      list.OMSetRenderTargets(rtv, dsv);
      list.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(1, 1, 1, 1));
      list.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_textures.Heap);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      var draw = stackalloc float[DrawConstants];
      for (int slot = 0; slot < ShadowSlots; slot++)
      {
        if (_slotOf[slot] < 0)
        {
          continue;
        }
        var l = lights[_slotOf[slot]];
        ShadowMapsDrawn++;
        for (int half = 0; half < 2; half++)
        {
          list.RSSetViewport(new Vortice.Mathematics.Viewport(half * ShadowSize, slot * ShadowSize, ShadowSize, ShadowSize));
          list.RSSetScissorRect(new Vortice.Mathematics.RectI(half * ShadowSize, slot * ShadowSize, ShadowSize, ShadowSize));
          int lastPipeline = -1;
          foreach (var instance in _instances)
          {
            if (instance == null || !instance.Visible || !instance.CastsShadow || _models[instance.Model] is not Model model || !model.Parts.Any(p => p.Pass == Pass.Solid))
            {
              continue;
            }
            bool skinned = model.Skin != null;
            int pipeline = (skinned ? 2 : 0) + half;
            if (pipeline != lastPipeline)
            {
              list.SetPipelineState(_shadowPipelines[pipeline]);
              lastPipeline = pipeline;
            }
            list.IASetVertexBuffers(0, model.VertexView);
            if (skinned)
            {
              list.IASetVertexBuffers(1, model.SkinView);
              list.SetGraphicsRootShaderResourceView(2, instance.PaletteAddress != 0 ? instance.PaletteAddress : _palette.GPUVirtualAddress);
            }
            list.IASetIndexBuffer(model.IndexView);
            new Span<float>(draw, DrawConstants).Clear();
            *(Matrix4x4*)draw = instance.World;
            draw[18] = instance.PaletteAddress != 0 ? instance.Palette!.Length : 0;
            draw[20] = half == 0 ? -1 : 1;
            draw[28] = l.Position.X;
            draw[29] = l.Position.Y;
            draw[30] = l.Position.Z;
            draw[31] = l.Range;
            list.SetGraphicsRoot32BitConstants(0, DrawConstants, draw, 0);
            foreach (var part in model.Parts)
            {
              if (part.Pass == Pass.Solid)
              {
                list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
                DrawCalls++;
              }
            }
          }
        }
      }
      list.ResourceBarrierTransition(_shadowAtlas, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
    }

    // The sun's shadow map (the page's sunShadowCascades drawing, ModelView.html: castBox / shadowConvert): per cascade the
    // work target cleared to -100 (the game's cascadeConvert ps 1), the map's objects lying across the cascade's box
    // (solid, then those blended by their texture's alpha), the cast (each frame; max with the map), then its 2 x 2 mean
    // into the cascade's quarter of the map. The page keeps the map's objects' part per cascade and draws it again only
    // when the cascades change; drawn whole each frame here (the same map).
    private unsafe void DrawSunShadow(ID3D12GraphicsCommandList list, SunShadow sun, ulong frameAddress, Task<Recorder>[]? cascades)
    {
      var recording = System.Diagnostics.Stopwatch.StartNew();
      Array.Clear(SunTriangles);
      Array.Clear(SunParts);
      DrawSunShadowNow(list, sun, frameAddress, cascades);
      SunRecordMs = recording.Elapsed.TotalMilliseconds;
    }

    /// <summary>The sun's shadow drawn also where the sun is 0 (for comparisons: the picture is the same).</summary>
    public bool KeepDarkSunShadow { get; set; }

    /// <summary>Whether the last frame left the sun's shadow out because its sun was 0 (see Draw).</summary>
    public bool SunSkipped { get; private set; }

    /// <summary>Last frame's sun shadow: the map objects' triangles and parts drawn per cascade, the recording's time on the CPU.</summary>
    public long[] SunTriangles { get; } = new long[SunCascades];
    public int[] SunParts { get; } = new int[SunCascades];
    public double SunRecordMs { get; private set; }

    private unsafe void DrawSunShadowNow(ID3D12GraphicsCommandList list, SunShadow sun, ulong frameAddress, Task<Recorder>[]? cascades)
    {
      SizeSun();
      list.ResourceBarrierTransition(_sunMap!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      if (cascades != null)
      {
        Splice(list, cascades);
        for (int k = 0; k < SunCascades; k++)
        {
          SunTriangles[k] = cascades[k].Result.Triangles;
          SunParts[k] = cascades[k].Result.Parts;
        }
      }
      else
      {
        for (int k = 0; k < SunCascades; k++)
        {
          int kk = k;
          var stats = Inline(list, (l, r) => DrawCascade(l, sun, frameAddress, kk, r));
          SunTriangles[k] = stats.Triangles;
          SunParts[k] = stats.Parts;
        }
      }
      list.ResourceBarrierTransition(_sunMap!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
    }

    // One cascade (any thread, any list: every state it needs set here; the map in RENDER_TARGET): the work target cleared,
    // the three phases, the 2 x 2 mean into the cascade's quarter of the map. Its draws, triangles and parts counted.
    private unsafe void DrawCascade(ID3D12GraphicsCommandList list, SunShadow sun, ulong frameAddress, int k, Recorder stats)
    {
      var d3d = _device.Device;
      var workRtv = _sunRtvs.GetCPUDescriptorHandleForHeapStart();
      var mapRtv = workRtv + (int)d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      var dsv = _sunDsv.GetCPUDescriptorHandleForHeapStart();
      var profiler = _device.Profiler;
      list.OMSetRenderTargets(workRtv, dsv);
      int mark = profiler?.Open(list, "  sun: clear") ?? -1;
      list.ClearRenderTargetView(workRtv, new Vortice.Mathematics.Color4(-100, 0, 0, 0));
      list.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
      profiler?.Close(list, mark);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(SunWorkSize, SunWorkSize));
      list.RSSetScissorRect(SunWorkSize, SunWorkSize);
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_textures.Heap);
      list.SetGraphicsRootConstantBufferView(3, frameAddress);
      list.SetGraphicsRootDescriptorTable(1, _textures.Handle(0));
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      for (int phase = 0; phase < 3; phase++)
      {
        if (phase != 1)
        {
          mark = profiler?.Open(list, phase == 0 ? "  sun: map objects" : "  sun: cast", stats.Draws) ?? -1;
        }
        DrawSunPhase(list, sun, k, phase, stats);
        if (phase != 0)
        {
          profiler?.Close(list, mark, stats.Draws);
        }
      }
      mark = profiler?.Open(list, "  sun: convert", stats.Draws) ?? -1;
      list.ResourceBarrierTransition(_sunWork!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      list.OMSetRenderTargets(mapRtv, null);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(k * SunMapSize, 0, SunMapSize, SunMapSize));
      list.RSSetScissorRect(new Vortice.Mathematics.RectI(k * SunMapSize, 0, SunMapSize, SunMapSize));
      list.SetGraphicsRootSignature(_lightSignature);
      list.SetPipelineState(_sunConvertPipeline);
      list.SetDescriptorHeaps(_gBufferSrvs);
      list.SetGraphicsRootConstantBufferView(0, frameAddress);
      list.SetGraphicsRootShaderResourceView(1, _lights.GPUVirtualAddress);
      list.SetGraphicsRootDescriptorTable(2, _gBufferSrvs.GetGPUDescriptorHandleForHeapStart());
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      list.DrawInstanced(3, 1, 0, 0);
      stats.Draws++;
      list.ResourceBarrierTransition(_sunWork!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      profiler?.Close(list, mark, stats.Draws);
    }
    // One phase of a cascade (the page's drawMeshes with castBox): 0 the map's objects' parts not blended (all but those
    // marked AlphaShadow), 1 those blended by their texture's alpha (alpha 1 without a texture), 2 the cast's parts that
    // aren't see-through. Of the map's objects only the parts whose sphere lies across the cascade's box on u and v and
    // within SunShadow.CasterReach of the camera (see InCascade).
    // (Any thread: its draw calls, triangles and parts counted into the recorder's figures, its scratch the recorder's.)
    private unsafe void DrawSunPhase(ID3D12GraphicsCommandList list, SunShadow sun, int k, int phase, Recorder stats)
    {
      var draw = stackalloc float[DrawConstants];
      var box = sun.Boxes[k];
      int lastPipeline = -1;
      foreach (var instance in _instances)
      {
        if (instance == null || !instance.Visible || _models[instance.Model] is not Model model || model.Actor != (phase == 2))
        {
          continue;
        }
        float scale = 1;
        if (!model.Actor)
        {
          if (!instance.CastsSunShadow)
          {
            continue;
          }
          // (The whole model's sphere first: what lies outside the box draws nothing there.)
          scale = MathF.Max(new Vector3(instance.World.M11, instance.World.M12, instance.World.M13).Length(),
                  MathF.Max(new Vector3(instance.World.M21, instance.World.M22, instance.World.M23).Length(),
                            new Vector3(instance.World.M31, instance.World.M32, instance.World.M33).Length()));
          if (!InCascade(sun, box, Vector3.Transform((model.Min + model.Max) * 0.5f, instance.World), (model.Max - model.Min).Length() * 0.5f * scale, false))
          {
            continue;
          }
        }
        // The parts drawn: those of the phase; of a map object only those whose own sphere lies across the box and
        // within the caster reach (the page culls each mesh so).
        int drawn = 0;
        if (stats.PartOn.Length < model.Parts.Length)
        {
          stats.PartOn = new bool[model.Parts.Length * 2];
        }
        for (int i = 0; i < model.Parts.Length; i++)
        {
          var part = model.Parts[i];
          bool on = SunDrawn(part, phase) &&
                    (model.Actor || InCascade(sun, box, Vector3.Transform(part.Center, instance.World), part.Radius * scale, true));
          stats.PartOn[i] = on;
          drawn += on ? 1 : 0;
        }
        if (drawn == 0)
        {
          continue;
        }
        bool skinned = model.Skin != null;
        int pipeline = (skinned ? 3 : 0) + phase;
        if (pipeline != lastPipeline)
        {
          list.SetPipelineState(_sunPipelines[pipeline]);
          lastPipeline = pipeline;
        }
        list.IASetVertexBuffers(0, model.VertexView);
        if (skinned)
        {
          list.IASetVertexBuffers(1, model.SkinView);
          list.SetGraphicsRootShaderResourceView(2, instance.PaletteAddress != 0 ? instance.PaletteAddress : _palette.GPUVirtualAddress);
        }
        list.SetGraphicsRootShaderResourceView(4, model.PartData.GPUVirtualAddress);
        list.IASetIndexBuffer(model.IndexView);
        new Span<float>(draw, DrawConstants).Clear();
        *(Matrix4x4*)draw = instance.World;
        draw[18] = instance.PaletteAddress != 0 ? instance.Palette!.Length : 0;
        draw[20] = sun.Biases[k];
        draw[28] = box.X;
        draw[29] = box.Y;
        draw[30] = box.Z;
        draw[31] = box.W;
        for (int i = 0; i < model.Parts.Length; i++)
        {
          var part = model.Parts[i];
          if (!stats.PartOn[i])
          {
            continue;
          }
          draw[21] = phase == 1 && part.Texture != 0 ? 1 : 0;
          draw[32] = part.Index;
          list.SetGraphicsRoot32BitConstants(0, DrawConstants, draw, 0);
          list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
          stats.Draws++;
          if (phase < 2)
          {
            stats.Triangles += part.Count / 3;
            stats.Parts++;
          }
        }
      }
    }

    // Whether a map object's sphere (world centre, radius) casts into the cascade's box: across it on u and v (the page's
    // castBox test, ModelView.html drawMeshes' seen) and, with reach, not wholly farther from the camera than the
    // caster reach (the page's outside test there: beyond the haze's end or the shot's draw distance nothing is drawn).
    private static bool InCascade(SunShadow sun, Vector4 box, Vector3 center, float radius, bool reach)
    {
      if (MathF.Abs(Vector3.Dot(center, sun.U) - box.X) > 1 / box.Z + radius ||
          MathF.Abs(Vector3.Dot(center, sun.V) - box.Y) > 1 / box.W + radius)
      {
        return false;
      }
      return !reach || !(Vector3.Distance(center, sun.Eye) - radius > sun.CasterReach);
    }


    private static bool SunDrawn(Part part, int phase) => phase == 2 ? !part.SeeThrough : part.AlphaShadow == (phase == 1);

    // The lights into this frame's slot of the light buffer, then a box each (instanced) over the G-buffer.
    private unsafe void DrawLights(ID3D12GraphicsCommandList list, CpuDescriptorHandle renderTarget, CpuDescriptorHandle depth,
                                   int width, int height, IReadOnlyList<DeferredLight> lights, ulong frameAddress)
    {
      int count = 0;
      var into = (float*)(_lightsMapped + (long)_paletteSlot * LightCapacity * LightBytes);
      for (int index = 0; index < lights.Count; index++)
      {
        var l = lights[index];
        if (count >= LightCapacity)
        {
          break;
        }
        if (!(l.Range > 0) || l.Color == Vector3.Zero)
        {
          continue;
        }
        var p = into + count * (LightBytes / 4);
        p[0] = l.Position.X;
        p[1] = l.Position.Y;
        p[2] = l.Position.Z;
        p[3] = l.Range;
        p[4] = l.Direction.X;
        p[5] = l.Direction.Y;
        p[6] = l.Direction.Z;
        p[7] = l.CosOuter;
        p[8] = l.Color.X;
        p[9] = l.Color.Y;
        p[10] = l.Color.Z;
        p[11] = l.ConeScale;
        p[12] = (float)l.Model;
        p[13] = l.InRoom ? 1 : 0;
        // 0 no shadow, 1 shadowed without a map (fully lit), 2 + its slot when its map was drawn this frame.
        p[14] = !l.Shadowed ? 0 : l.ShadowSlot is int s && s >= 0 && s < ShadowSlots && _slotOf[s] == index ? 2 + s : 1;
        p[15] = (float)l.Mask;
        count++;
      }
      LightsDrawn = count;
      if (count == 0)
      {
        return;
      }
      foreach (var g in _gBuffer)
      {
        list.ResourceBarrierTransition(g!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      }
      list.OMSetRenderTargets(renderTarget, depth);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetGraphicsRootSignature(_lightSignature);
      list.SetPipelineState(_lightPipeline);
      list.SetDescriptorHeaps(_gBufferSrvs);
      list.SetGraphicsRootConstantBufferView(0, frameAddress);
      list.SetGraphicsRootShaderResourceView(1, _lights.GPUVirtualAddress + (ulong)((long)_paletteSlot * LightCapacity * LightBytes));
      list.SetGraphicsRootDescriptorTable(2, _gBufferSrvs.GetGPUDescriptorHandleForHeapStart());
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      list.DrawInstanced(36, (uint)count, 0, 0);
      DrawCalls++;
      foreach (var g in _gBuffer)
      {
        list.ResourceBarrierTransition(g!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      }
    }

    /// <summary>The fog left out (FrameLight.Fog ignored; for comparisons).</summary>
    public bool NoFog { get; set; }

    /// <summary>The lamps on the map's see-through materials drawn forward left out (for comparisons).</summary>
    public bool NoForwardLamps { get; set; }

    /// <summary>The fog's depth effect left out (FrameLight.DepthFx ignored; for comparisons).</summary>
    public bool NoDepthFx { get; set; }

    // The fog over the solid scene: a triangle over the screen reading the G-buffer's places (see PSFog).
    private void DrawFog(ID3D12GraphicsCommandList list, CpuDescriptorHandle renderTarget, CpuDescriptorHandle depth,
                         int width, int height, ulong frameAddress)
    {
      foreach (var g in _gBuffer)
      {
        list.ResourceBarrierTransition(g!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      }
      list.OMSetRenderTargets(renderTarget, depth);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetGraphicsRootSignature(_lightSignature);
      list.SetPipelineState(_fogPipeline);
      list.SetDescriptorHeaps(_gBufferSrvs);
      list.SetGraphicsRootConstantBufferView(0, frameAddress);
      list.SetGraphicsRootShaderResourceView(1, _lights.GPUVirtualAddress);
      list.SetGraphicsRootDescriptorTable(2, _gBufferSrvs.GetGPUDescriptorHandleForHeapStart());
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      list.DrawInstanced(3, 1, 0, 0);
      DrawCalls++;
      foreach (var g in _gBuffer)
      {
        list.ResourceBarrierTransition(g!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      }
    }

    /// <summary>
    /// The heavy passes recorded into command lists of their own on worker threads (default; SPARKIV_D3D12_PARALLEL=0:
    /// all on the calling thread into its list, as before), sent in the frame's order (see <see cref="Splice"/>). The same
    /// commands in the same order either way, only the states each list sets for itself added.
    /// </summary>
    public bool ParallelRecording { get; set; } = Environment.GetEnvironmentVariable("SPARKIV_D3D12_PARALLEL") != "0";

    // A command list recorded on a worker thread (or, without one, the figures of a pass drawn into the frame's list):
    // its draw calls, the sun's map triangles and parts, its scratch, the worker's time and the profiler's name for it.
    // Also the allocators the frame's list goes on with after a splice (List unused).
    private sealed class Recorder
    {
      public ID3D12CommandAllocator? Allocator;
      public ID3D12GraphicsCommandList? List;
      public int Draws;
      public long Triangles;
      public int Parts;
      public bool[] PartOn = new bool[64];
      public double Ms;
      public string Name = "";
    }

    // Per frame slot (_paletteSlot: the GPU is done with a slot's frame before it comes round again, as for the other
    // per-frame buffers: at most one frame is on the GPU while one is recorded) the recorders made so far, used again in
    // order each frame.
    private readonly List<Recorder>[] _recorders = Enumerable.Range(0, PaletteSlots).Select(_ => new List<Recorder>()).ToArray();
    private int _recorderNext;

    private void BeginRecorders() => _recorderNext = 0;

    // The next recorder of this frame's slot, its allocator reset (on the calling thread).
    private Recorder NextRecorder()
    {
      var slot = _recorders[_paletteSlot];
      if (_recorderNext == slot.Count)
      {
        var allocator = _device.Device.CreateCommandAllocator(CommandListType.Direct);
        var made = _device.Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
        made.Close();
        slot.Add(new Recorder { Allocator = allocator, List = made });
      }
      var r = slot[_recorderNext++];
      r.Allocator!.Reset();
      r.Draws = 0;
      r.Triangles = 0;
      r.Parts = 0;
      return r;
    }

    // A command list opened here and recorded by body on a worker thread (which sets every state it needs: a list
    // inherits none), then closed.
    private Task<Recorder> Record(string name, Action<ID3D12GraphicsCommandList, Recorder> body)
    {
      var r = NextRecorder();
      r.List!.Reset(r.Allocator, null);
      r.Name = name;
      return Task.Run(() =>
      {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        body(r.List!, r);
        r.List!.Close();
        r.Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return r;
      });
    }

    /// <summary>
    /// The frame's list closed and sent with the workers' lists after it (waited for, in their order), then opened again
    /// on an allocator of this renderer's (a list may be reset as soon as it is sent): the caller goes on with the same
    /// list object, which holds no state now. The workers' draws counted; their figures summed into the return.
    /// </summary>
    private Recorder Splice(ID3D12GraphicsCommandList list, IReadOnlyList<Task<Recorder>> parts)
    {
      var lists = new ID3D12CommandList[parts.Count + 1];
      lists[0] = list;
      var sum = _spliced;
      sum.Draws = 0;
      sum.Triangles = 0;
      sum.Parts = 0;
      var profiler = _device.Profiler;
      for (int i = 0; i < parts.Count; i++)
      {
        var r = parts[i].Result;
        lists[i + 1] = r.List!;
        sum.Draws += r.Draws;
        sum.Triangles += r.Triangles;
        sum.Parts += r.Parts;
        profiler?.Cpu(r.Name, r.Ms);
      }
      list.Close();
      _device.Queue.ExecuteCommandLists(lists);
      list.Reset(NextRecorder().Allocator, null);
      DrawCalls += sum.Draws;
      return sum;
    }

    private readonly Recorder _spliced = new Recorder();

    // Scratch figures for a pass drawn into the frame's list (ParallelRecording off).
    private readonly Recorder _inline = new Recorder();

    // A pass drawn into the frame's list now (ParallelRecording off): its draws counted, its figures returned.
    private Recorder Inline(ID3D12GraphicsCommandList list, Action<ID3D12GraphicsCommandList, Recorder> body)
    {
      var b = _inline;
      b.Draws = 0;
      b.Triangles = 0;
      b.Parts = 0;
      body(list, b);
      DrawCalls += b.Draws;
      return b;
    }

    // A pass's draws (any thread, any list with the main passes' states); how many. <paramref name="chunk"/> of
    // <paramref name="chunks"/>: that share of the pass's sequence of visits (sweep, group, instance), so the chunks played
    // one after the other draw exactly what the whole pass draws, in its order (each chunk sets its pipeline anew).
    private unsafe int DrawPass(ID3D12GraphicsCommandList list, Pass pass, int chunk = 0, int chunks = 1)
    {
      int draws = 0;
      int lastPipeline = -1;
      var draw = stackalloc float[DrawConstants];
      int count = _instances.Count;
      // The decals and the see-through parts in the page's order (ModelView.html ~3352-3380): first the dirt decals (and
      // the decals of a location model with dirt, dirtFirst) or the glowing see-through parts (glowFirst: gta_emissive*
      // of bucket 4/5, see-through, in a room with a multiplier over 0, outside gta_emissivenight), then the rest, each
      // in instance order.
      bool ordered = pass is Pass.Decal or Pass.Blend;
      static bool First(Part p, Instance i, Pass pass) => pass == Pass.Decal ? p.DirtFirst
        : p.GlowApart && p.GlowBlend && (i.InRoom ? p.GlowMultiplier > 0 : p.GlowNight);
      // Within each, the cast before the location, as the page's visible list (CutsceneView's PostVisible: the cast's
      // meshes, then the location's): a car's windscreen behind a toll booth's pane is drawn before the pane writes its
      // depth (et01_ba).
      long firstVisit = ordered ? 0 : 2L * count, visits = 4L * count - firstVisit;
      long from = firstVisit + visits * chunk / chunks, to = firstVisit + visits * (chunk + 1) / chunks;
      for (int sweep = ordered ? 0 : 1; sweep < 2; sweep++)
      for (int group = 0; group < 2; group++)
      for (int index = 0; index < count; index++)
      {
        long visit = (sweep * 2L + group) * count + index;
        if (visit < from || visit >= to)
        {
          continue;
        }
        var instance = _instances[index];
        if (instance == null || !instance.Visible || !instance.MainView || _models[instance.Model] is not Model model ||
            model.Actor != (group == 0))
        {
          continue;
        }
        bool any = false;
        foreach (var p in model.Parts)
        {
          if (Drawn(p.Pass, pass) && (!ordered || First(p, instance, pass) == (sweep == 0)))
          {
            any = true;
            break;
          }
        }
        if (!any)
        {
          continue;
        }
        // A skinned model without its bones (none given, or no room for them this frame): drawn at rest.
        bool skinned = model.Skin != null;
        list.IASetVertexBuffers(0, model.VertexView);
        if (skinned)
        {
          list.IASetVertexBuffers(1, model.SkinView);
          list.SetGraphicsRootShaderResourceView(2, instance.PaletteAddress != 0 ? instance.PaletteAddress : _palette.GPUVirtualAddress);
        }
        list.IASetVertexBuffers(2, model.ExtrasView);
        list.SetGraphicsRootShaderResourceView(4, model.PartData.GPUVirtualAddress);
        list.IASetIndexBuffer(model.IndexView);
        *(Matrix4x4*)draw = instance.World;
        draw[16] = instance.ActorAmbient;
        draw[17] = instance.InRoom ? 1 : 0;
        draw[18] = instance.PaletteAddress != 0 ? instance.Palette!.Length : 0;
        draw[19] = 0;
        list.SetGraphicsRoot32BitConstants(0, 20, draw, 0);
        foreach (var part in model.Parts)
        {
          if (!Drawn(part.Pass, pass) || (ordered && First(part, instance, pass) != (sweep == 0)))
          {
            continue;
          }
          int pipeline = Pipe(skinned, part.Pass, part.Culled) + (part.Pulled && part.Pass is Pass.Blend or Pass.Glass ? 16 : 0);
          // A glowing see-through part (the page's ModelView.html ~3622-3646, ~3707-3734): its glow, whether drawn apart
          // and whether its lit draw is left out (in a room, a see-through gta_emissivenight, a glowing glass).
          float glow = 0;
          bool apart = false, litDrawn = true;
          if (part.GlowApart)
          {
            glow = part.GlowMultiplier * _exposure * (part.GlowNight && !instance.InRoom ? 1 - _dayNight : 1);
            bool nightGlass = part.GlowNight && part.GlowBlend;
            bool glowGlass = !part.GlowNight && (part.GlowGlass || (part.GlowStrong && part.GlowBlend));
            apart = (glow > 0 || nightGlass) && (part.GlowBlend || glowGlass);
            litDrawn = !(apart && (instance.InRoom || nightGlass || glowGlass));
          }
          var mask = part.DirtMask ?? Vector3.Zero;
          draw[20] = part.Cutout ? 1 : 0;
          draw[21] = part.DirtMask != null ? 1 : 0;
          draw[22] = part.Kind;
          draw[23] = part.Glow;
          draw[24] = mask.X;
          draw[25] = mask.Y;
          draw[26] = mask.Z;
          draw[27] = 0;
          draw[28] = part.Tint.X;
          draw[29] = part.Tint.Y;
          draw[30] = part.Tint.Z;
          draw[31] = 1;
          draw[32] = part.Index;
          draw[33] = 0;
          draw[34] = 0;
          draw[35] = 0;
          if (litDrawn)
          {
            if (pipeline != lastPipeline)
            {
              list.SetPipelineState(_pipelines[pipeline]!);
              lastPipeline = pipeline;
            }
            list.SetGraphicsRoot32BitConstants(0, 16, draw + 20, 20);
            list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
            draws++;
          }
          if (apart && !NoGlow)
          {
            // gta_emissive's glow laid over (ONE / INVSRCALPHA), the others' added (ONE / ONE).
            bool over = !part.GlowStrong && !part.GlowNight && !part.GlowGlass;
            list.SetPipelineState(_glowPipelines[(skinned ? 2 : 0) + (over ? 0 : 1)]);
            lastPipeline = -1;
            draw[23] = glow;
            draw[33] = part.GlowStrong ? 0.0004f : -0.0004f;
            list.SetGraphicsRoot32BitConstants(0, 16, draw + 20, 20);
            list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
            draws++;
          }
        }
      }
      return draws;
    }

    /// <summary>The see-through parts' glows drawn apart left out (for comparisons).</summary>
    public bool NoGlow { get; set; }

    /// <summary>
    /// The instances marked <c>mirrored</c> drawn into the water's mirror (the page's reflection pass, ModelView.html
    /// drawMeshes(true, mirrored, mirroredEye): the target and depth with <see cref="MirrorSamples"/> samples, in
    /// RENDER_TARGET / DEPTH_WRITE state, the sky already in it). Only while <see cref="Draw"/> runs (from its
    /// afterSolid): the frame's constants are this frame's, taken over with the mirrored view-projection and eye.
    /// <paramref name="viewForward"/>: the camera's own forward (the draw distance is measured along it from the mirrored
    /// eye, as the page's); <paramref name="clipBelow"/>: nothing lower (the level less the game's 0.515 m);
    /// <paramref name="hazeReach"/>: a part wholly farther than it is left out (the page's hazeEnd(): the light's fog
    /// end, or nearer the location's reach). Per part as the page's <c>seen</c> per mesh: not layered terrain, not wholly
    /// under <paramref name="clipBelow"/>, not under 1 % of its distance across, not wholly beyond the haze or the draw
    /// distance (all from the mirrored eye). The solid parts first, then the see-through and decal ones over them.
    /// </summary>
    public unsafe void DrawMirror(ID3D12GraphicsCommandList list, CpuDescriptorHandle target, CpuDescriptorHandle depth, int width, int height,
                                  Matrix4x4 viewProjection, Vector3 eye, Vector3 viewForward, float clipBelow, float hazeReach, float drawReach)
    {
      MirrorDrawCalls = 0;
      _device.Profiler?.Begin(list, "  water: mirror meshes", 0);
      // The frame's constants, this frame's taken over: the mirrored view, the forward, the draw distance, the clip.
      var main = (float*)(_frameMapped + _paletteSlot * FrameSlotBytes);
      var frame = (float*)(_frameMapped + (PaletteSlots + _paletteSlot) * FrameSlotBytes);
      Buffer.MemoryCopy(main, frame, FrameSlotBytes, FrameFloats * 4);
      *(Matrix4x4*)frame = viewProjection;
      frame[16] = eye.X;
      frame[17] = eye.Y;
      frame[18] = eye.Z;
      frame[45] = viewForward.X;
      frame[46] = viewForward.Y;
      frame[47] = viewForward.Z;
      bool reachOn = drawReach > 0 && float.IsFinite(drawReach);
      frame[58] = reachOn ? drawReach : 0;
      frame[59] = clipBelow;
      ulong frameAddress = _frame.GPUVirtualAddress + (ulong)((PaletteSlots + _paletteSlot) * FrameSlotBytes);

      list.OMSetRenderTargets(target, depth);
      list.ClearDepthStencilView(depth, ClearFlags.Depth, 1.0f, 0);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_textures.Heap);
      list.SetGraphicsRootConstantBufferView(3, frameAddress);
      list.SetGraphicsRootDescriptorTable(1, _textures.Handle(0));
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

      // The meshes, into the frame's list (not recorded apart: from a bundle the 4-sample target came out a few pixels by 1
      // different from run to run).
      MirrorDrawCalls = DrawMirrorParts(list, eye, clipBelow, hazeReach, drawReach);
      _device.Profiler?.End(list, "  water: mirror meshes", MirrorDrawCalls);
    }

    // The mirrored meshes (see DrawMirror); how many draws.
    private unsafe int DrawMirrorParts(ID3D12GraphicsCommandList list, Vector3 eye, float clipBelow, float hazeReach, float drawReach)
    {
      int draws = 0;
      bool reachOn = drawReach > 0 && float.IsFinite(drawReach);
      float farthest = MathF.Min(hazeReach > 0 ? hazeReach : float.PositiveInfinity, reachOn ? drawReach : float.PositiveInfinity);
      var draw = stackalloc float[DrawConstants];
      for (int phase = 0; phase < 2; phase++)
      {
        list.SetPipelineState(_mirrorPipelines[phase]);
        foreach (var instance in _instances)
        {
          if (instance == null || !instance.Visible || !instance.Mirrored || _models[instance.Model] is not Model model || model.Skin != null)
          {
            continue;
          }
          float scale = MathF.Max(new Vector3(instance.World.M11, instance.World.M12, instance.World.M13).Length(),
                        MathF.Max(new Vector3(instance.World.M21, instance.World.M22, instance.World.M23).Length(),
                                  new Vector3(instance.World.M31, instance.World.M32, instance.World.M33).Length()));
          bool bound = false;
          foreach (var part in model.Parts)
          {
            if ((part.Pass == Pass.Solid) != (phase == 0) || part.Layered)
            {
              continue;
            }
            var center = Vector3.Transform(part.Center, instance.World);
            float radius = part.Radius * scale;
            float distance = Vector3.Distance(center, eye);
            if (center.Z + radius < clipBelow || radius / MathF.Max(distance, 1e-3f) < 0.01f || distance - radius > farthest)
            {
              continue;
            }
            if (!bound)
            {
              bound = true;
              list.IASetVertexBuffers(0, model.VertexView);
              list.IASetVertexBuffers(2, model.ExtrasView);
              list.SetGraphicsRootShaderResourceView(4, model.PartData.GPUVirtualAddress);
              list.IASetIndexBuffer(model.IndexView);
              new Span<float>(draw, DrawConstants).Clear();
              *(Matrix4x4*)draw = instance.World;
              draw[16] = 1;
              list.SetGraphicsRoot32BitConstants(0, 20, draw, 0);
            }
            var mask = part.DirtMask ?? Vector3.Zero;
            draw[20] = part.Cutout ? 1 : 0;
            draw[21] = part.DirtMask != null ? 1 : 0;
            draw[22] = part.Kind;
            draw[23] = part.Glow;
            draw[24] = mask.X;
            draw[25] = mask.Y;
            draw[26] = mask.Z;
            draw[27] = 0;
            draw[28] = part.Tint.X;
            draw[29] = part.Tint.Y;
            draw[30] = part.Tint.Z;
            draw[31] = 1;
            draw[32] = part.Index;
            draw[33] = 0;
            draw[34] = 0;
            draw[35] = 0;
            list.SetGraphicsRoot32BitConstants(0, 16, draw + 20, 20);
            list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
            draws++;
          }
        }
      }
      return draws;
    }

    /// <summary>A debug view of the solid and see-through passes (0 none; see the shader's Shade: 1 albedo, 2 normal, 3 AO, 4 light, 5 alpha, 6 vertex colour).</summary>
    public int DebugView { get; set; }

    /// <summary>The decals lit by their own normal and AO instead of the surface's below (for comparisons).</summary>
    public bool NoDecalBelow { get; set; }

    /// <summary>The mirrored surroundings' map left out (the shine without it; for comparisons).</summary>
    public bool NoParabolic { get; set; }

    // The mirrored surroundings' map from the camera's place (the page's ModelView.html ~3750-3794 with drawMeshes'
    // mapPass ~3287-3312 and gtaiv-pc.js parabolicVertex / parabolicFragment): cleared to 0, the sky first (the caller's
    // sky: its target, size), then the map's far versions' solid parts reaching above the camera (the sphere's top),
    // not under 0.003 of their distance across, within 330 m across the ground (less their radius), not wholly beyond
    // the haze (hazeReach) nor the shot's draw distance; then each smaller level halved from the one above. Called by
    // Draw with this frame's constants written (taken over into a slot of its own, the eye's height as the clip).
    private unsafe void DrawParabolic(ID3D12GraphicsCommandList list, Vector3 eye, Action<ID3D12GraphicsCommandList, CpuDescriptorHandle, int> sky,
                                      float hazeReach, float drawReach, Task<Recorder>? objects)
    {
      var main = (float*)(_frameMapped + _paletteSlot * FrameSlotBytes);
      var frame = (float*)(_frameMapped + (2 * PaletteSlots + _paletteSlot) * FrameSlotBytes);
      Buffer.MemoryCopy(main, frame, FrameSlotBytes, FrameFloats * 4);
      frame[58] = 0;
      frame[59] = eye.Z;
      var rtv = _parabolicRtvs.GetCPUDescriptorHandleForHeapStart();
      uint rtvSize = _device.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      var dsv = _parabolicDsv.GetCPUDescriptorHandleForHeapStart();
      list.ResourceBarrierTransition(_parabolic, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      list.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
      list.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
      sky(list, rtv, (int)ParabolicSize);

      if (objects != null)
      {
        Splice(list, new[] { objects });
      }
      else
      {
        SetupParabolic(list);
        Inline(list, (l, r) => DrawParabolicParts(l, r, eye, hazeReach, drawReach));
      }
      // (After a splice the list holds no states: the map's again for the halving.)
      SetupParabolic(list);
      var draw = stackalloc float[DrawConstants];
      // Each smaller level the mean of the one above (the page's generateMipmap; the game's own pass not derived).
      list.SetPipelineState(_halvePipeline);
      for (int level = 1; level < ParabolicLevels; level++)
      {
        list.ResourceBarrierTransition(_parabolic, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource, (uint)(level - 1));
        uint size = ParabolicSize >> level;
        list.OMSetRenderTargets(rtv + level * (int)rtvSize);
        list.RSSetViewport(new Vortice.Mathematics.Viewport(size, size));
        list.RSSetScissorRect((int)size, (int)size);
        new Span<float>(draw, DrawConstants).Clear();
        draw[32] = _parabolicLevelSlots[level - 1];
        list.SetGraphicsRoot32BitConstants(0, DrawConstants, draw, 0);
        list.DrawInstanced(3, 1, 0, 0);
      }
      list.ResourceBarrierTransition(_parabolic, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource, ParabolicLevels - 1);
    }

    // The mirrored surroundings' map's states into a list (this frame's or a worker's): its level 0 and depth, the main
    // root signature with the map's frame constants (DrawParabolic's slot), the textures.
    private void SetupParabolic(ID3D12GraphicsCommandList list)
    {
      ulong frameAddress = _frame.GPUVirtualAddress + (ulong)((2 * PaletteSlots + _paletteSlot) * FrameSlotBytes);
      list.OMSetRenderTargets(_parabolicRtvs.GetCPUDescriptorHandleForHeapStart(), _parabolicDsv.GetCPUDescriptorHandleForHeapStart());
      list.RSSetViewport(new Vortice.Mathematics.Viewport(ParabolicSize, ParabolicSize));
      list.RSSetScissorRect((int)ParabolicSize, (int)ParabolicSize);
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_textures.Heap);
      list.SetGraphicsRootConstantBufferView(3, frameAddress);
      list.SetGraphicsRootDescriptorTable(1, _textures.Handle(0));
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    // The map's objects drawn into the mirrored surroundings' map (any thread, any list with SetupParabolic's states; see
    // DrawParabolic): the far versions' solid parts reaching above the camera, near enough and not too small.
    private unsafe void DrawParabolicParts(ID3D12GraphicsCommandList list, Recorder stats, Vector3 eye, float hazeReach, float drawReach)
    {
      list.SetPipelineState(_parabolicPipeline);
      bool reachOn = drawReach > 0 && float.IsFinite(drawReach);
      var draw = stackalloc float[DrawConstants];
      foreach (var instance in _instances)
      {
        if (instance == null || !instance.Visible || !instance.Mirrored || _models[instance.Model] is not Model model || model.Skin != null)
        {
          continue;
        }
        float scale = MathF.Max(new Vector3(instance.World.M11, instance.World.M12, instance.World.M13).Length(),
                      MathF.Max(new Vector3(instance.World.M21, instance.World.M22, instance.World.M23).Length(),
                                new Vector3(instance.World.M31, instance.World.M32, instance.World.M33).Length()));
        bool bound = false;
        foreach (var part in model.Parts)
        {
          if (part.Pass != Pass.Solid)
          {
            continue;
          }
          var center = Vector3.Transform(part.Center, instance.World);
          float radius = part.Radius * scale;
          float distance = Vector3.Distance(center, eye);
          float across = MathF.Sqrt((center.X - eye.X) * (center.X - eye.X) + (center.Y - eye.Y) * (center.Y - eye.Y));
          float away = distance - radius;
          if (center.Z + radius < eye.Z || radius < 0.003f * distance || across - radius > 330 ||
              (hazeReach > 0 && away > hazeReach) || (reachOn && away > drawReach))
          {
            continue;
          }
          if (!bound)
          {
            bound = true;
            list.IASetVertexBuffers(0, model.VertexView);
            list.IASetVertexBuffers(2, model.ExtrasView);
            list.SetGraphicsRootShaderResourceView(4, model.PartData.GPUVirtualAddress);
            list.IASetIndexBuffer(model.IndexView);
            new Span<float>(draw, DrawConstants).Clear();
            *(Matrix4x4*)draw = instance.World;
            draw[16] = 1;
            list.SetGraphicsRoot32BitConstants(0, 20, draw, 0);
          }
          var mask = part.DirtMask ?? Vector3.Zero;
          draw[20] = part.Cutout ? 1 : 0;
          draw[21] = part.DirtMask != null ? 1 : 0;
          draw[22] = part.Kind;
          draw[23] = part.Glow;
          draw[24] = mask.X;
          draw[25] = mask.Y;
          draw[26] = mask.Z;
          draw[27] = 0;
          draw[28] = part.Tint.X;
          draw[29] = part.Tint.Y;
          draw[30] = part.Tint.Z;
          draw[31] = 1;
          draw[32] = part.Index;
          draw[33] = 0;
          draw[34] = 0;
          draw[35] = 0;
          list.SetGraphicsRoot32BitConstants(0, 16, draw + 20, 20);
          list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
          stats.Draws++;
        }
      }
    }

    public void Dispose()
    {
      Clear();
      foreach (var b in _recorders.SelectMany(s => s))
      {
        b.List?.Dispose();
        b.Allocator?.Dispose();
      }
      foreach (var p in _mirrorPipelines.Concat(_glowPipelines))
      {
        p?.Dispose();
      }
      _textures.Dispose();
      _palette.Unmap(0);
      _palette.Dispose();
      _frame.Unmap(0);
      _frame.Dispose();
      _lights.Unmap(0);
      _lights.Dispose();
      foreach (var g in _gBuffer)
      {
        g?.Dispose();
      }
      _below?.Dispose();
      _lightCopy?.Dispose();
      _solidDepth?.Dispose();
      _fullDepth?.Dispose();
      _coronaPipeline.Dispose();
      _volumePipeline.Dispose();
      _spotVolume.Dispose();
      _pointVolume.Dispose();
      _parabolic.Dispose();
      _parabolicDepth.Dispose();
      _parabolicRtvs.Dispose();
      _parabolicDsv.Dispose();
      _parabolicPipeline.Dispose();
      _halvePipeline.Dispose();
      _noExtras.Dispose();
      _gBufferRtvs.Dispose();
      _gBufferSrvs.Dispose();
      _shadowAtlas.Dispose();
      _shadowDepth.Dispose();
      _shadowRtv.Dispose();
      _shadowDsv.Dispose();
      foreach (var pipeline in _shadowPipelines)
      {
        pipeline.Dispose();
      }
      _sunWork?.Dispose();
      _sunDepth?.Dispose();
      _sunMap?.Dispose();
      _sunRtvs.Dispose();
      _sunDsv.Dispose();
      foreach (var pipeline in _sunPipelines)
      {
        pipeline.Dispose();
      }
      _sunConvertPipeline.Dispose();
      _lightPipeline.Dispose();
      _fogPipeline.Dispose();
      _lightSignature.Dispose();
      foreach (var pipeline in _pipelines)
      {
        pipeline?.Dispose();
      }
      _rootSignature.Dispose();
    }
  }

  /// <summary>The formats the targets share.</summary>
  public static class PanelSwapChainFormats
  {
    public const Format Color = Format.B8G8R8A8_UNorm;
    public const Format Depth = Format.D32_Float;
  }
}
