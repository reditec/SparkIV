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

    /// <summary>A model's checked meshes (as the page shows them), moving with bones or not.</summary>
    public static ModelSource Shown(ModelScene scene, bool skinned, bool baked = false)
    {
      var shown = scene.Root.CheckedMeshes().ToHashSet();
      return new ModelSource(scene.Meshes.Where(m => shown.Contains(m.Id)).ToList(), scene.Materials, scene.Textures, skinned) { Baked = baked };
    }
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
  float4 Screen;   // xy: the target's size in pixels
};
ConstantBuffer<Frame> f : register(b1);
Texture2D Albedo : register(t0);
SamplerState Linear : register(s0);
struct Bone { row_major float4x4 m; };
StructuredBuffer<Bone> Palette : register(t1);

static const uint Baked = 1, Ped = 2, Vehicle = 4, Tree = 8, Emissive = 16, Night = 32, Strong = 64;

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR; };
struct VsSkinIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR; float4 bones : BLENDINDICES; float4 weights : BLENDWEIGHT; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR; float3 world : WORLD; };

VsOut Finish(float4 local, float3 normal, float2 uv, float alpha, float3 color)
{
  VsOut output;
  float4 world = mul(local, d.World);
  output.position = mul(world, f.ViewProjection);
  output.normal = mul(normal, (float3x3)d.World);
  output.uv = uv;
  output.alpha = alpha;
  output.color = color;
  output.world = world.xyz;
  return output;
}

VsOut VSMain(VsIn input)
{
  return Finish(float4(input.position, 1), input.normal, input.uv, input.alpha, input.color);
}

// A vertex moved by up to four bones (rest to now); one with no weights, or an instance without its bones, stays as
// it is. Bone numbers are kept within the instance's palette (root descriptors have no bounds check).
void Skin(VsSkinIn input, out float4 p, out float3 n)
{
  p = float4(input.position, 1);
  n = input.normal;
  uint count = (uint)d.Instance.z;
  if (count > 0 && dot(input.weights, 1) > 0)
  {
    uint4 b = min((uint4)(input.bones + 0.5), count - 1);
    float4x4 m = Palette[b.x].m * input.weights.x + Palette[b.y].m * input.weights.y +
                 Palette[b.z].m * input.weights.z + Palette[b.w].m * input.weights.w;
    p = mul(p, m);
    n = mul(n, (float3x3)m);
  }
}

VsOut VSSkinned(VsSkinIn input)
{
  float4 p;
  float3 n;
  Skin(input, p, n);
  return Finish(p, n, input.uv, input.alpha, input.color);
}

// A lamp's shadow map (#112 step 5), as GTA IV draws one (the page's lampShadowVertex/lampShadowFragment, gtaiv-pc.js;
// the TLAD trace GTAIV.3: VS 0x25afd220, PS 0x25d42d60): of a point's offset L from the lamp in the lamp's frame (the
// world's x the axis, y and -z across) and s = -side x L.z, the place L.xy / (|L| - s), the depth |L| / radius; the
// pixel keeps only what is on its half (s / radius at most 0.01) and stores |L| / radius as it came from the vertices.
// Here d.Tint holds the lamp (xyz) and its radius (w), d.Material.x the half (-1 or 1).
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

ShOut VSShadow(VsIn input)
{
  return ShadowFinish(float4(input.position, 1));
}

ShOut VSShadowSkinned(VsSkinIn input)
{
  float4 p;
  float3 n;
  Skin(input, p, n);
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

// The light target and the G-buffer the lamps are added over (#112 step 5): what the lamps' light is multiplied by
// (the albedo; a glowing material laid over by its alpha only by the rest), the normal and AO the light was made with,
// the place (w: 1 written, 2 written and baked: the game's stencil for its cutscene lights).
struct PsOut { float4 light : SV_Target0; float4 albedo : SV_Target1; float4 normal : SV_Target2; float4 place : SV_Target3; };

PsOut Shade(VsOut input)
{
  uint kind = (uint)(d.Material.z + 0.5);
  bool baked = (kind & Baked) != 0, ped = (kind & Ped) != 0, vehicle = (kind & Vehicle) != 0, tree = (kind & Tree) != 0;
  bool emissive = (kind & Emissive) != 0;
  float4 texel = Albedo.Sample(Linear, input.uv);
  float alpha = texel.a * input.alpha;
  float3 albedo = texel.rgb * d.Tint.rgb;
  if (d.Material.y > 0.5)
  {
    alpha = dot(texel.rgb, d.DirtMask.rgb) * input.alpha;
    albedo = float3(0.23, 0.22, 0.2);
  }
  // Cut out: the page's test (texture alpha x vertex alpha at most 45/255 goes; a tree by half, for its stipple).
  if (d.Material.x > 0.5)
  {
    clip(alpha - (tree ? 0.5 : 0.17648));
  }
  float3 vc = input.color;
  if (vehicle)
  {
    albedo *= vc;
  }
  // The normal: peds and vehicles turned to the camera (seen from inside: the side facing it).
  float3 n = normalize(input.normal + 1e-6);
  if (!tree && !baked && dot(n, f.Eye.xyz - input.world) < 0)
  {
    n = -n;
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
  float3 around = lerp(f.AmbientAbove.rgb, f.AmbientBelow.rgb, saturate(0.5 - 0.5 * n.z));
  float sunLit = saturate((dot(n, f.SunDirection.xyz) - 0.25) * 1.3333333);
  float3 rgb = albedo * (around * ao + f.Sun.rgb * sunLit);
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
  PsOut o;
  o.light = float4(rgb, alpha);
  // (A decal blends its albedo by its alpha into the surface's, keeping the surface's normal, AO and place: as the
  // game's decals write GB0's colour only, the page's outBelow.)
  o.albedo = float4(lampAlbedo, alpha);
  o.normal = float4(n, ao);
  o.place = float4(input.world, baked ? 2 : 1);
  return o;
}

PsOut PSMain(VsOut input)
{
  return Shade(input);
}

// The see-through materials: the light target only (no lamps on them yet: the game lights them forward, its
// lamps over 4 m only; not ported).
float4 PSBlend(VsOut input) : SV_Target
{
  return Shade(input).light;
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
struct Light { float4 At; float4 Dir; float4 Color; float4 Kind; };
StructuredBuffer<Light> Lights : register(t3);
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
  bool baked = place.w > 1.5;
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
  }
  return float4(GAlbedo.Load(px).rgb * lit, 0);
}
";

    private const int DrawConstants = 16 + 4 + 4 + 4 + 4;
    private const int FrameFloats = 16 + 4 * 7;

    /// <summary>
    /// How many lights' shadow maps a frame can draw (the atlas's rows; see <see cref="DeferredLight.ShadowSlot"/>): each
    /// light two halves of 1024 x 1024, R32F (the shader's ShadowSize).
    /// </summary>
    public const int ShadowSlots = 4;
    private const int ShadowSize = 1024;
    private const int FrameSlotBytes = 256;
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

    internal enum Pass : byte { Solid, Blend, Decal }

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

      public void Dispose()
      {
        Vertices.Dispose();
        Indices.Dispose();
        Skin?.Dispose();
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
    private readonly ID3D12PipelineState[] _pipelines = new ID3D12PipelineState[12];
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

    /// <summary>The shadow maps drawn last frame (lights).</summary>
    public int ShadowMapsDrawn { get; private set; }

    /// <summary>The lights added last frame.</summary>
    public int LightsDrawn { get; private set; }

    public unsafe MeshRenderer(D3D12Device device, Format targetFormat)
    {
      _device = device;
      _textures = new TextureCache(device);
      var d3d = device.Device;

      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, DrawConstants), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex),
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),
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
      };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers));

      var vertexShader = Compile(DxcShaderStage.Vertex, "VSMain");
      var skinnedShader = Compile(DxcShaderStage.Vertex, "VSSkinned");
      var pixelShader = Compile(DxcShaderStage.Pixel, "PSMain");
      var blendShader = Compile(DxcShaderStage.Pixel, "PSBlend");
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
      decalBlend.RenderTarget[1] = decalBlend.RenderTarget[0] with { RenderTargetWriteMask = ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue };
      decalBlend.RenderTarget[2] = decalBlend.RenderTarget[0] with { BlendEnable = false, RenderTargetWriteMask = 0 };
      decalBlend.RenderTarget[3] = decalBlend.RenderTarget[2];
      // Both faces, or the back ones culled: the page's front faces wind counter-clockwise (GL's default), and so they
      // do here (checked: with Direct3D's default, clockwise, bc_1's hall showed the next room's wallpaper through
      // its walls).
      var both = RasterizerDescription.CullNone;
      var front = new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true };
      // A decal a little in front of the surface it lies on (nearer is less depth here).
      var pulled = both with { DepthBias = -16, SlopeScaledDepthBias = -1.0f };
      for (int s = 0; s < 2; s++)
      {
        for (int cull = 0; cull < 2; cull++)
        {
          int at = Pipe(s == 1, Pass.Solid, cull == 1);
          _pipelines[at] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.Opaque, DepthStencilDescription.Default, cull == 1 ? front : both, true));
          _pipelines[Pipe(s == 1, Pass.Blend, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, DepthStencilDescription.Read, cull == 1 ? front : both, false));
          _pipelines[Pipe(s == 1, Pass.Decal, cull == 1)] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, decalBlend, DepthStencilDescription.Read, cull == 1 ? front with { DepthBias = -16, SlopeScaledDepthBias = -1.0f } : pulled, true));
        }
      }

      // The lamps' pass (step 5): the frame's constants, the lights, the G-buffer.
      _lightSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, new[]
      {
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, 4, 0, 0)), ShaderVisibility.Pixel),
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
      _lights = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                            ResourceDescription.Buffer((ulong)PaletteSlots * LightCapacity * LightBytes), ResourceStates.GenericRead);
      void* lightsMapped;
      _lights.Map(0, null, &lightsMapped).CheckError();
      _lightsMapped = (byte*)lightsMapped;
      _gBufferRtvs = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 3));
      _gBufferSrvs = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, DescriptorHeapFlags.ShaderVisible));

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
            InputLayout = new InputLayoutDescription(s == 1 ? skinnedInput : input),
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
      // The frame's constants (camera, light), a slot per frame in flight.
      _frame = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                           ResourceDescription.Buffer((ulong)PaletteSlots * FrameSlotBytes), ResourceStates.GenericRead);
      void* frameMapped;
      _frame.Map(0, null, &frameMapped).CheckError();
      _frameMapped = (byte*)frameMapped;

      // The bones' matrices, written each frame into a slot of their own (the GPU may still read the last frames').
      _palette = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                             ResourceDescription.Buffer((ulong)PaletteSlots * PaletteCapacity * 64), ResourceStates.GenericRead);
      void* mapped;
      _palette.Map(0, null, &mapped).CheckError();
      _paletteMapped = (Matrix4x4*)mapped;
    }

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
      _gWidth = width;
      _gHeight = height;
    }

    private CpuDescriptorHandle GBufferView(int i) =>
      _gBufferRtvs.GetCPUDescriptorHandleForHeapStart() + i * (int)_device.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

    private static int Pipe(bool skinned, Pass pass, bool culled) => (skinned ? 6 : 0) + (int)pass * 2 + (culled ? 1 : 0);

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
      var d3d = _device.Device;
      var models = new List<Model?>(sources.Count);
      var uploads = new List<ID3D12Resource>();
      long pending = 0;
      var allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      void Send()
      {
        list.Close();
        _device.Queue.ExecuteCommandList(list);
        _device.WaitIdle();
        foreach (var upload in uploads)
        {
          upload.Dispose();
        }
        uploads.Clear();
        pending = 0;
      }
      try
      {
        foreach (var source in sources)
        {
          models.Add(Build(source, list, uploads, ref pending));
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
      }
      return new PreparedModels(models);
    }

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

    /// <summary>A group's models and their instances gone (the drawing thread; the GPU done with them first), their textures let go.</summary>
    public void RemoveGroup(int group)
    {
      _device.WaitIdle();
      for (int i = 0; i < _instances.Count; i++)
      {
        if (_instances[i] is Instance instance && _models[instance.Model] is Model model && model.Group == group)
        {
          _instances[i] = null;
        }
      }
      for (int i = 0; i < _models.Count; i++)
      {
        if (_models[i] is Model model && model.Group == group)
        {
          Free(model);
          _models[i] = null;
        }
      }
    }

    /// <summary>Prepared models never registered let go (the GPU is done with them: Prepare waited).</summary>
    public void Discard(PreparedModels prepared)
    {
      foreach (var model in prepared.Models)
      {
        if (model != null)
        {
          Free(model);
        }
      }
      prepared.Models.Clear();
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
      var indices = new List<uint>();
      var parts = new List<Part>();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      foreach (var mesh in source.Meshes)
      {
        materials.TryGetValue(mesh.Material, out var material);
        if (mesh.Indices.Length == 0 || material == null || material.Hidden || material.Glass)
        {
          continue;
        }
        uint first = (uint)(vertices.Count / VertexFloats);
        int count = mesh.Positions.Length / 3;
        bool normals = mesh.Normals != null && mesh.Normals.Length >= count * 3;
        bool uvs = mesh.Uvs != null && mesh.Uvs.Length >= count * 2;
        bool alphas = mesh.Alphas != null && mesh.Alphas.Length >= count;
        bool colors = mesh.Colors != null && mesh.Colors.Length >= count * 3;
        // Without vertex colours: a baked mesh 0.45 (the page's guess), others white (the page's defaults).
        float noColor = source.Baked && !material.Vehicle && !(material.EmissiveMultiplier != null) ? 0.45f : 1f;
        bool skinned = mesh.SkinBones != null && mesh.SkinWeights != null && mesh.SkinBones.Length >= count * 4 && mesh.SkinWeights.Length >= count * 4;
        for (int v = 0; v < count; v++)
        {
          var p = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
          min = Vector3.Min(min, p);
          max = Vector3.Max(max, p);
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
        }
        uint start = (uint)indices.Count;
        foreach (ushort index in mesh.Indices)
        {
          indices.Add(first + index);
        }
        byte[]? blob = material.TextureKey != null && source.Textures.TryGetValue(material.TextureKey, out var found) ? found : null;
        if (material.TextureKey != null && blob == null)
        {
          lock (MissingTextures)
          {
            MissingTextures.Add(material.TextureKey);
          }
        }
        var pass = material.Decal || material.DirtMask != null ? Pass.Decal : material.Blend ? Pass.Blend : Pass.Solid;
        var dirt = material.DirtMask is { Length: >= 3 } d ? new Vector3(d[0], d[1], d[2]) : (Vector3?)null;
        var tint = source.Tints != null && source.Tints.TryGetValue(material.Id, out var t) ? t : Vector3.One;
        int texture = _textures.Acquire(blob, list, uploads, out long textureBytes);
        pending += textureBytes;
        textures.Add(texture);
        // Its kind for the light (see the shader): vehicles are never baked; a glowing material only with a texture,
        // and only solid (the see-through ones glow apart: not yet).
        bool baked = source.Baked && !material.Vehicle;
        bool glowing = material.EmissiveMultiplier != null && blob != null && pass != Pass.Blend;
        int kind = (baked ? 1 : 0) | (material.Ped ? 2 : 0) | (material.Vehicle ? 4 : 0) | (material.Tree ? 8 : 0) |
                   (glowing ? 16 : 0) | (material.EmissiveAtNight ? 32 : 0) | (material.EmissiveStrong ? 64 : 0);
        parts.Add(new Part(start, (uint)mesh.Indices.Length, texture, pass, material.Cutout || material.Stipple, dirt, tint)
        {
          Kind = kind,
          Glow = glowing ? material.EmissiveMultiplier!.Value : 0,
          // The page culls the back faces of map objects' and props' solids (not trees').
          Culled = baked && !material.Tree && pass == Pass.Solid,
        });
      }
      if (indices.Count == 0)
      {
        foreach (int texture in textures)
        {
          _textures.Release(texture);
        }
        return null;
      }
      var model = new Model { Min = min, Max = max, IndexCount = (uint)indices.Count, Parts = parts.ToArray() };
      model.Textures.AddRange(textures);
      pending += (long)vertices.Count * 4 + (long)indices.Count * 4 + (skin?.Count ?? 0) * 4L;
      model.Vertices = Upload(vertices.ToArray(), list, uploads, out ulong vertexBytes);
      model.Indices = Upload(indices.ToArray(), list, uploads, out ulong indexBytes);
      model.VertexView = new VertexBufferView(model.Vertices.GPUVirtualAddress, (uint)vertexBytes, VertexFloats * 4);
      model.IndexView = new IndexBufferView(model.Indices.GPUVirtualAddress, (uint)indexBytes, Format.R32_UInt);
      if (skin != null)
      {
        model.Skin = Upload(skin.ToArray(), list, uploads, out ulong skinBytes);
        model.SkinView = new VertexBufferView(model.Skin.GPUVirtualAddress, (uint)skinBytes, SkinFloats * 4);
      }
      return model;
    }

    /// <summary>An array into a new buffer in a default heap, its copy from an upload buffer recorded in the list.</summary>
    private unsafe ID3D12Resource Upload<T>(T[] data, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads, out ulong bytes) where T : unmanaged
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
      list.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.VertexAndConstantBuffer | ResourceStates.IndexBuffer);
      return buffer;
    }

    /// <summary>
    /// A model drawn with a model-to-scene matrix (and, skinned, its bones' matrices); its instance's number.
    /// <paramref name="castsShadow"/>: its solid parts drawn into the lights' shadow maps (while visible).
    /// </summary>
    public int AddInstance(int model, Matrix4x4 world, Matrix4x4[]? palette = null, bool inRoom = false, bool castsShadow = false)
    {
      _instances.Add(new Instance { Model = model, World = world, Palette = palette, InRoom = inRoom, CastsShadow = castsShadow });
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

    /// <summary>All models and instances gone (the GPU done with them first), their textures let go.</summary>
    public void Clear()
    {
      _device.WaitIdle();
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
                            Action<ID3D12GraphicsCommandList>? afterSolid = null, IReadOnlyList<DeferredLight>? lights = null)
    {
      list.OMSetRenderTargets(renderTarget, depth);
      list.ClearDepthStencilView(depth, ClearFlags.Depth, 1.0f, 0);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      DrawCalls = 0;
      LightsDrawn = 0;
      if (_instances.Count == 0)
      {
        return;
      }
      SizeGBuffer(width, height);
      var gTargets = new[] { renderTarget, GBufferView(0), GBufferView(1), GBufferView(2) };
      for (int i = 1; i < gTargets.Length; i++)
      {
        list.ClearRenderTargetView(gTargets[i], new Vortice.Mathematics.Color4(0, 0, 0, 0));
      }

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
      frame[38] = light.PedAoScale;
      frame[39] = light.AoScale;
      frame[40] = width;
      frame[41] = height;
      frame[42] = 0;
      frame[43] = 0;

      // The shadowed lights' maps (step 5), before anything else is drawn.
      ShadowMapsDrawn = 0;
      if (lights != null)
      {
        DrawShadowMaps(list, lights);
      }

      ulong frameAddress = _frame.GPUVirtualAddress + (ulong)(_paletteSlot * FrameSlotBytes);
      void Bind(bool gBuffer)
      {
        if (gBuffer)
        {
          list.OMSetRenderTargets(gTargets, depth);
        }
        else
        {
          list.OMSetRenderTargets(renderTarget, depth);
        }
        list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
        list.RSSetScissorRect(width, height);
        list.SetGraphicsRootSignature(_rootSignature);
        list.SetDescriptorHeaps(_textures.Heap);
        list.SetGraphicsRootConstantBufferView(3, frameAddress);
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      }
      Bind(true);
      DrawPass(list, Pass.Solid);
      DrawPass(list, Pass.Decal);
      // The lamps over the solid scene and its decals (step 5); the water and see-through materials come after
      // (forward, without them for now).
      if (lights != null && lights.Count > 0)
      {
        DrawLights(list, renderTarget, depth, width, height, lights, frameAddress);
      }
      // What goes between the solid scene and the see-through materials (the water), with its own states.
      afterSolid?.Invoke(list);
      Bind(false);
      DrawPass(list, Pass.Blend);
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

    private unsafe void DrawPass(ID3D12GraphicsCommandList list, Pass pass)
    {
      int lastPipeline = -1, lastTexture = -1;
      var draw = stackalloc float[DrawConstants];
      foreach (var instance in _instances)
      {
        if (instance == null || !instance.Visible || _models[instance.Model] is not Model model || !model.Parts.Any(p => p.Pass == pass))
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
        list.IASetIndexBuffer(model.IndexView);
        *(Matrix4x4*)draw = instance.World;
        draw[16] = instance.ActorAmbient;
        draw[17] = instance.InRoom ? 1 : 0;
        draw[18] = instance.PaletteAddress != 0 ? instance.Palette!.Length : 0;
        draw[19] = 0;
        list.SetGraphicsRoot32BitConstants(0, 20, draw, 0);
        foreach (var part in model.Parts)
        {
          if (part.Pass != pass)
          {
            continue;
          }
          int pipeline = Pipe(skinned, pass, part.Culled);
          if (pipeline != lastPipeline)
          {
            list.SetPipelineState(_pipelines[pipeline]);
            lastPipeline = pipeline;
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
          list.SetGraphicsRoot32BitConstants(0, 12, draw + 20, 20);
          if (part.Texture != lastTexture)
          {
            list.SetGraphicsRootDescriptorTable(1, _textures.Handle(part.Texture));
            lastTexture = part.Texture;
          }
          list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
          DrawCalls++;
        }
      }
    }

    public void Dispose()
    {
      Clear();
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
      _lightPipeline.Dispose();
      _lightSignature.Dispose();
      foreach (var pipeline in _pipelines)
      {
        pipeline.Dispose();
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
