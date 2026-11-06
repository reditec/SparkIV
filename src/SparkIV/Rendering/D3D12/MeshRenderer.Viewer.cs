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
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using SparkIV.WinUI.Viewers;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// A light of a model shown as a marker in the model view (the page's setLights, ModelView.html ~1297-1346): where it
  /// is, its colour (0 to 1), a spot's direction and outer angle (degrees), the meshes of its drawable (shown while one
  /// of them is).
  /// </summary>
  public sealed record ViewerLight(Vector3 Position, Vector3 Color, bool Spot, Vector3 Direction, float Falloff, IReadOnlyList<int> Meshes);

  /// <summary>
  /// What a frame of the model or animation view draws (#112 step 6): one model (<see cref="MeshRenderer.DrawViewer"/>),
  /// as ModelView.html draws a lone model - no time cycle, no location: lit from the camera (the page's light), the
  /// shaders' shine in that light, over the page's background, 4 samples a pixel (the page's WebGL context:
  /// antialias true, ModelView.html ~45).
  /// </summary>
  public sealed class ViewerFrame
  {
    public Matrix4x4 ViewProjection = Matrix4x4.Identity;
    public Vector3 Eye;
    /// <summary>The page's vp.light: from a little above and to the left of the camera (OrbitCamera.HeadLight).</summary>
    public Vector3 Light = Vector3.UnitZ;
    /// <summary>The page's vp.toEye: minus the view's forward.</summary>
    public Vector3 ToEye = Vector3.UnitZ;
    /// <summary>The model drawn (MeshRenderer's number), -1 none.</summary>
    public int Model = -1;
    /// <summary>Its bones' matrices (rest to now), for a skinned model; null: drawn at rest.</summary>
    public Matrix4x4[]? Palette;
    /// <summary>The meshes shown, in the page's order (its visible set: the tree's checked meshes).</summary>
    public IReadOnlyList<int> Visible = Array.Empty<int>();
    /// <summary>The page's options: textures (else each material's plain colour), the wire frame, the lights' markers.</summary>
    public bool Textures = true;
    public bool Wireframe;
    public bool ShowLights;
    /// <summary>The page's backgrounds: 0 the gradient, 1 grey, 2 black, 3 white (ModelView.html ~24-27).</summary>
    public int Background;
    /// <summary>A texture moved by the clip (the page's uvNow), by material id: its matrix's two rows.</summary>
    public IReadOnlyDictionary<int, (Vector3 Row0, Vector3 Row1)>? UvRows;
    /// <summary>The model's lights (markers while <see cref="ShowLights"/>).</summary>
    public IReadOnlyList<ViewerLight>? Lights;
    /// <summary>A grid on the ground at this height while the ped moves through the world (the page's createGrid); null none.</summary>
    public float? GridHeight;
  }

  // The model and animation views' drawing (#112 step 6): the page's path for a lone model (ModelView.html's fragment
  // shader with outdoor, grade and baked all false, no cutscene lights; its draw() ~3166-3990 without a sky, location or
  // cast), forward, into a 4-sample target resolved into the caller's. The models are MeshRenderer's (Prepare/Register),
  // drawn by mesh id in the page's order with the page's states per mesh. The GTA IV (PC) parts the page takes from
  // gtaiv-pc.js for this path (vehicleShine's model-view branch, the stipple) are ported here with it, as the cutscene
  // shader does.
  public sealed partial class MeshRenderer
  {
    /// <summary>PNG textures made with their smaller levels (the page's generateMipmap; see TextureCache.ImageMipmaps).</summary>
    public bool ImageMipmaps
    {
      get => _textures.ImageMipmaps;
      set => _textures.ImageMipmaps = value;
    }

    private const string ViewerShader = @"
struct VDraw
{
  row_major float4x4 World;
  float4 Flags;    // x: bits (see the C# side), y: the part's number in its model's Parts, z: bone count, w: emissive scale
  float4 Base;     // rgb: the material's colour without its texture (the page's mat.color), w: the page's alpha uniform
  float4 DirtMask; // rgb: a dirt decal's channels
  float4 Uv0;      // xyz: the texture matrix's first row (a clip moving the texture)
  float4 Uv1;      // xyz: its second row
  float4 Marker;   // xyz: a light marker's place, w: its scale (0: not a marker)
};
ConstantBuffer<VDraw> d : register(b0);
struct VFrame
{
  row_major float4x4 ViewProjection;
  float4 Eye;      // xyz, w: the target's height in pixels
  float4 Light;    // xyz: the page's light (from a little above and left of the camera)
  float4 ToEye;    // xyz: the page's toEye
  float4 Line;     // rgb: the wire frame's colour
  float4 Top;      // rgb: the gradient's top colour
  float4 Bottom;   // rgb: its bottom colour
};
ConstantBuffer<VFrame> f : register(b1);
Texture2D Tex[] : register(t0, space1);
SamplerState Linear : register(s0);
struct Bone { row_major float4x4 m; };
StructuredBuffer<Bone> Palette : register(t1);
struct PartData
{
  uint4 Tex;      // albedo, normal map, specular map, environment
  uint4 Tex2;     // layer 1 (or the second paint layer), layer 2, layer 3, dirt
  uint4 Bits;     // x: flags, y: texture layers (1 to 4), z: which layers have a texture (bits)
  float4 Spec;    // specular power, intensity, bumpiness, parallax
  float4 SpecMask;// xyz: the specular map's channels, w: reflectivity
  float4 Paint2;
  float4 Dirt;
  float4 VShine;  // a vehicle shader's shine numbers (intensity, power, kind)
  float4 Spec2;
  float4 Sub;
  float4 Misc;
};
StructuredBuffer<PartData> Parts : register(t2);

static const uint FNormal = 1, FSpec = 2, FEnv = 8, FEnvSpec = 16, FPaint2 = 32;
static const uint BTexture = 1, BCutout = 2, BTextureAlpha = 4, BStipple = 8, BDirtDecal = 16, BTree = 32, BVehicle = 64,
  BEmissive = 128, BUvAnim = 256, BNormals = 512, BVehicleLight = 1024, BTangents = 2048, BLayered = 4096;

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR;
              float4 tangent : TANGENT; float4 layerUv : TEXCOORD1; float2 layerUv3 : TEXCOORD2; float4 weights : TEXCOORD3; };
struct VsSkinIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR;
                  float4 tangent : TANGENT; float4 layerUv : TEXCOORD1; float2 layerUv3 : TEXCOORD2; float4 weights : TEXCOORD3;
                  float4 bones : BLENDINDICES; float4 boneWeights : BLENDWEIGHT; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float3 color : COLOR; float3 world : WORLD;
               float4 tangent : TANGENT; float4 layerUv : TEXCOORD1; float2 layerUv3 : TEXCOORD2; float4 weights : TEXCOORD3; };

// The page's vertex shader (ModelView.html ~240-273): skinned by four bones; a tangent's normal and tangent normalised
// unless skinned; a texture moved by a clip (uvAnim: its matrix's rows).
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
  }
  output.normal = n;
  output.tangent = hasTangent ? float4(skinned ? t : normalize(t + 1e-5), tangent.w) : 0;
  uint bits = (uint)d.Flags.x;
  output.uv = (bits & BUvAnim) != 0 ? float2(dot(d.Uv0.xyz, float3(input.uv, 1)), dot(d.Uv1.xyz, float3(input.uv, 1))) : input.uv;
  output.alpha = input.alpha;
  output.color = input.color;
  output.world = world.xyz;
  output.layerUv = input.layerUv;
  output.layerUv3 = input.layerUv3;
  output.weights = input.weights;
  return output;
}

VsOut VSViewer(VsIn input)
{
  return Finish(float4(input.position, 1), input.normal, input.tangent, false, input);
}

VsOut VSViewerSkinned(VsSkinIn input)
{
  float4 p = float4(input.position, 1);
  float3 n = input.normal;
  float3 t = input.tangent.xyz;
  uint count = (uint)d.Flags.z;
  if (count > 0 && dot(input.boneWeights, 1) > 0)
  {
    uint4 b = min((uint4)(input.bones + 0.5), count - 1);
    float4x4 m = Palette[b.x].m * input.boneWeights.x + Palette[b.y].m * input.boneWeights.y +
                 Palette[b.z].m * input.boneWeights.z + Palette[b.w].m * input.boneWeights.w;
    p = mul(p, m);
    n = mul(n, (float3x3)m);
    t = mul(t, (float3x3)m);
  }
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
  return Finish(p, n, float4(t, input.tangent.w), count > 0, plain);
}

// The page's bumped (ModelView.html ~348-359): the normal map's direction turned into the surface by the screen's
// derivatives of the place and the texture coordinates (GL's dFdy is up, Direct3D's ddy down: turned round).
float3 Bumped(float3 n, float3 p, float2 uv, uint normalTex, float bumpiness)
{
  float3 m = Tex[normalTex].Sample(Linear, uv).xyz * 2 - 1;
  m.xy *= bumpiness;
  m.y = -m.y;
  float3 dp1 = ddx(p), dp2 = -ddy(p);
  float2 duv1 = ddx(uv), duv2 = -ddy(uv);
  float3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
  float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
  float3 b = dp2perp * duv1.y + dp1perp * duv2.y;
  float scale = rsqrt(max(max(dot(t, t), dot(b, b)), 1e-20));
  return normalize(t * scale * m.x + b * scale * m.y + n * m.z);
}

// The page's pedBumpedRaw (ModelView.html ~369-378).
float3 PedBumpedRaw(float3 n, float4 t, float2 uv, uint normalTex, float bumpiness)
{
  float4 raw = Tex[normalTex].Sample(Linear, uv);
  float2 xy = 1.0 - raw.w - raw.x >= 0.0 ? raw.wy : raw.xy;
  float2 k = (xy - 0.5) * bumpiness;
  float z = sqrt(abs(1.0 - dot(xy, xy)));
  float3 b = cross(t.xyz, n) * t.w;
  return normalize(n * z + t.xyz * k.x + b * k.y + 1e-5);
}

// The page's gBufferRounded (ModelView.html ~381-385): as the G-buffer stores it, unless drawn see-through.
float3 Rounded(float3 g, bool textureAlpha)
{
  if (textureAlpha)
  {
    return g;
  }
  float3 stored = round(floor(64.0 * (g + 1.0)) / 128.0 * 1023.0) / 1023.0;
  return normalize(stored * 2.0 - 0.99999);
}

static const float Kept[16] = { 0, 6, 8, 12, 11, 12, 16, 24, 32, 40, 48, 52, 52, 56, 58, 64 };

// The page's fragment shader for a lone model (ModelView.html ~421-736 with outdoor, grade and baked false, no scene
// lights; gtaiv-pc.js vehicleShine's model-view branch ~381-469 and stipple ~472-481).
float4 PSViewer(VsOut input) : SV_Target
{
  uint bits = (uint)d.Flags.x;
  PartData p = Parts[(uint)d.Flags.y];
  bool useTexture = (bits & BTexture) != 0;
  bool useNormalMap = useTexture && (p.Bits.x & FNormal) != 0;
  bool useSpecMap = useTexture && (p.Bits.x & FSpec) != 0;
  bool textureAlpha = (bits & BTextureAlpha) != 0;
  bool tree = (bits & BTree) != 0;
  bool vehicle = (bits & BVehicle) != 0;
  bool tangents = (bits & BTangents) != 0;
  float3 eye = f.Eye.xyz;
  float alphaU = d.Base.w;
  // (bumpiness and parallax only for a mesh with tangents: the page's m.pedBumps.)
  float bumpiness = tangents ? p.Spec.z : 0;
  float parallax = tangents ? p.Spec.w : 0;

  float2 uvP = input.uv;
  if (parallax > 0 && useNormalMap && dot(input.tangent.xyz, input.tangent.xyz) > 0.25)
  {
    float3 pn = normalize(input.normal), pt = normalize(input.tangent.xyz), pe = eye - input.world;
    float3 pv = float3(dot(pt, pe), dot(cross(pt, pn) * input.tangent.w, pe), dot(pn, pe));
    uvP += (Tex[p.Tex.y].Sample(Linear, input.uv).a * parallax - 0.5 * parallax) * pv.xy / max(length(pv), 1e-5);
  }
  float4 base = useTexture ? Tex[p.Tex.x].Sample(Linear, uvP) : float4(d.Base.rgb, 1);
  // gta_vehicle_paint3's second layer (the page's paint2On: a layered mesh, textured, not terrain).
  bool layered = (bits & BLayered) != 0;
  int layers = layered && useTexture ? (int)p.Bits.y : 1;
  if (layered && useTexture && layers <= 1 && (p.Bits.x & FPaint2) != 0)
  {
    float4 l = Tex[p.Tex2.x].Sample(Linear, input.layerUv.xy) * p.Paint2;
    base.rgb = lerp(base.rgb, l.rgb, saturate(l.a));
  }
  if (vehicle)
  {
    base.rgb *= input.color;
  }
  if (layers > 1)
  {
    if ((p.Bits.z & 1) != 0) { float4 l = Tex[p.Tex2.x].Sample(Linear, input.layerUv.xy); base.rgb = lerp(base.rgb, l.rgb, saturate(l.a * input.weights.y)); }
    if (layers > 2 && (p.Bits.z & 2) != 0) { float4 l = Tex[p.Tex2.y].Sample(Linear, input.layerUv.zw); base.rgb = lerp(base.rgb, l.rgb, saturate(l.a * input.weights.z)); }
    if (layers > 3 && (p.Bits.z & 4) != 0) { float4 l = Tex[p.Tex2.z].Sample(Linear, input.layerUv3); base.rgb = lerp(base.rgb, l.rgb, saturate(l.a * input.weights.w)); }
    base.a = 1;
  }
  if ((bits & BDirtDecal) != 0 && useTexture)
  {
    base = float4(input.color, dot(base.rgb, d.DirtMask.xyz));
  }
  if ((bits & BCutout) != 0 && !tree && alphaU >= 1 && base.a * input.alpha <= 0.17647)
  {
    discard;
  }
  if (tree && alphaU >= 1)
  {
    // (GL's gl_FragCoord counts rows from the bottom: the same pattern from the same rows.)
    float level = saturate(base.a) * 3.996;
    int index = (int)floor(level) * 4 + (int)floor(frac(level) * 4.0);
    int2 q = int2((int)input.position.x, (int)f.Eye.w - 1 - (int)input.position.y) & 7;
    int qx = q.x ^ q.y;
    int order = ((qx & 1) << 5) | ((q.x & 1) << 4) | ((qx & 2) << 2) | ((q.x & 2) << 1) | ((qx & 4) >> 1) | ((q.x & 4) >> 2);
    if ((float)order >= Kept[index])
    {
      discard;
    }
  }
  // Without normals in the mesh, the face's own (GL: cross(dFdx, dFdy); Direct3D's ddy the other way).
  float3 n = (bits & BNormals) != 0 ? normalize(input.normal) : normalize(-cross(ddx(input.world), ddy(input.world)));
  if (!tree && dot(n, eye - input.world) < 0)
  {
    n = -n;
  }
  float3 envN = n;
  if (useNormalMap)
  {
    if (dot(input.tangent.xyz, input.tangent.xyz) > 0.25)
    {
      envN = PedBumpedRaw(dot(n, input.normal) < 0 ? -input.normal : input.normal, input.tangent, uvP, p.Tex.y, bumpiness);
      n = Rounded(envN, textureAlpha);
    }
    else
    {
      n = Bumped(n, input.world, uvP, p.Tex.y, bumpiness);
      envN = n;
    }
  }
  float4 specTexel = useSpecMap ? Tex[p.Tex.z].Sample(Linear, input.uv) : float4(1, 1, 1, 1);
  if (useTexture && (p.Bits.x & FEnv) != 0)
  {
    float3 r = reflect(normalize(input.world - eye), envN);
    float3 env = Tex[p.Tex.w].Sample(Linear, -0.5 * (r.xz / max(length(r), 1e-5) + 1.0)).rgb *
                 ((p.Bits.x & FEnvSpec) != 0 ? dot(specTexel.rgb, p.SpecMask.xyz) * p.Spec.y : 1.0) * p.SpecMask.w;
    if (textureAlpha)
    {
      float seen = base.a * input.alpha;
      env *= seen >= 0.01 ? 1.0 / seen : 100.0;
    }
    base.rgb += env;
  }
  // The light from around (terrain: dimmed by the vertex colour's red) and from the side: 0.35 + 0.65 n.l.
  float around = layers > 1 ? input.color.r : 1.0;
  float lit = 0.35 * around + 0.65 * max(dot(n, f.Light.xyz), 0.0);
  // The shine, where the surface faces half way between the light and the camera (gta_spec's).
  float shine = 0;
  if (p.Spec.x > 0 && p.Spec.y > 0)
  {
    float where = useSpecMap ? dot(specTexel.rgb, p.SpecMask.xyz) : 1.0;
    shine = pow(max(dot(n, normalize(f.Light.xyz + f.ToEye.xyz)), 0.0), max(p.Spec.x * specTexel.a, 1.0)) * p.Spec.y * where;
  }
  // A vehicle's shine in the view's light (gtaiv-pc.js vehicleShine: the glass's forward draw, else the model view's).
  float3 vehicleShine = 0;
  float3 vs = p.VShine.xyz;
  bool vehicleGlass = vs.z > 0.5 && vs.z < 1.5;
  float reflectivePower = p.SpecMask.w;
  if (vehicle && (vehicleGlass || useSpecMap) && reflectivePower > 0)
  {
    float strength = dot(specTexel.rgb, p.SpecMask.xyz) * vs.x * reflectivePower;
    if (vs.z > 1.5)
    {
      strength = min(strength, 1.0);
    }
    float power = specTexel.a * vs.y * reflectivePower;
    float3 view = normalize(input.world - eye);
    float3 mirrored = reflect(view, n);
    if (vehicleGlass)
    {
      float fr = pow(1.0 - abs(dot(view, n)), 4.0);
      float s = min(strength, 1.0) * saturate(0.5 * fr + 0.5);
      vehicleShine = s * 4.0 * pow(saturate(dot(mirrored, f.Light.xyz)) + 1e-4, power + 1e-4);
    }
    else
    {
      float3 h = normalize(f.Light.xyz - view);
      vehicleShine = strength * pow(max(dot(n, h), 0.0), max(power, 1.0));
    }
  }
  float a = alphaU < 1 ? alphaU : 1;
  if (textureAlpha)
  {
    a = base.a * ((bits & BVehicleLight) != 0 ? 1.0 : input.alpha);
    if (a < 0.01)
    {
      discard;
    }
    if ((bits & BStipple) != 0)
    {
      float level = min(a, 1.0) * 3.996;
      int index = (int)floor(level) * 4 + (int)floor(frac(level) * 4.0);
      a *= Kept[index] / 64.0;
    }
  }
  float glassShine = vehicleGlass ? (a >= 0.01 ? 1.0 / a : 100.0) : 1.0;
  float3 rgb = (bits & BEmissive) != 0 ? (d.Flags.w > 0 ? base.rgb * input.color * d.Flags.w : base.rgb)
                                       : base.rgb * lit + shine + vehicleShine * glassShine;
  return float4(rgb * a, a);
}

// The wire frame (the page's lines in its colour, ModelView.html ~3971-3982).
float4 PSWire(VsOut input) : SV_Target
{
  return float4(f.Line.rgb, 1);
}

// The lights' markers and the ground grid: lines of their own (a marker made round the origin, put at the light as
// large as the camera is far; ModelView.html ~241).
float4 VSLine(float3 position : POSITION) : SV_Position
{
  float3 p = d.Marker.w > 0 ? d.Marker.xyz + position * d.Marker.w : position;
  return mul(float4(p, 1), f.ViewProjection);
}

float4 PSLine(float4 position : SV_Position) : SV_Target
{
  return float4(d.Base.rgb, 1);
}

// The page's background (ModelView.html ~24: linear-gradient(#68788a, #22252a) down the view).
float4 VSFull(uint id : SV_VertexID) : SV_Position
{
  float2 uv = float2((id << 1) & 2, id & 2);
  return float4(uv * float2(2, -2) + float2(-1, 1), 1, 1);
}

float4 PSGradient(float4 position : SV_Position) : SV_Target
{
  return float4(lerp(f.Top.rgb, f.Bottom.rgb, saturate(position.y / f.Eye.w)), 1);
}
";

    // The draw's bits (the shader's B... constants).
    [Flags]
    private enum ViewerBits
    {
      Texture = 1, Cutout = 2, TextureAlpha = 4, Stipple = 8, DirtDecal = 16, Tree = 32, Vehicle = 64, Emissive = 128, UvAnim = 256,
      Normals = 512, VehicleLight = 1024, Tangents = 2048, Layered = 4096,
    }

    private const int ViewerDrawConstants = 16 + 4 * 6;
    private const int ViewerFrameBytes = 256;
    private const int ViewerSamples = 4;

    // Made at the first DrawViewer (none of it for the cutscenes).
    private ID3D12RootSignature? _viewerSignature;
    private readonly Dictionary<int, ID3D12PipelineState> _viewerPipelines = new Dictionary<int, ID3D12PipelineState>();
    private byte[]? _viewerVS, _viewerSkinnedVS, _viewerPS, _viewerWirePS, _viewerLineVS, _viewerLinePS, _viewerFullVS, _viewerGradientPS;
    private ID3D12Resource? _viewerFrame;
    private unsafe byte* _viewerFrameMapped;
    private ID3D12Resource? _viewerColor, _viewerDepth;
    private ID3D12DescriptorHeap? _viewerRtv, _viewerDsv;
    private int _viewerWidth, _viewerHeight;
    // Per model: its parts by mesh id, and its wire frame (an index buffer of each triangle's edges once, per part a range).
    private readonly Dictionary<int, Dictionary<int, List<int>>> _viewerPartsOfMesh = new Dictionary<int, Dictionary<int, List<int>>>();
    private readonly Dictionary<int, (ID3D12Resource Buffer, IndexBufferView View, (uint Start, uint Count)[] Ranges)> _viewerWires =
      new Dictionary<int, (ID3D12Resource, IndexBufferView, (uint, uint)[])>();
    // The lights' markers (by the list they were made for) and the grid (by its height): line vertices in upload heaps.
    private IReadOnlyList<ViewerLight>? _markersFor;
    private ID3D12Resource? _markerBuffer;
    private (int First, int Count)[] _markerRanges = Array.Empty<(int, int)>();
    private float? _gridFor;
    private ID3D12Resource? _gridBuffer;
    private int _gridCount;

    /// <summary>The model's wire frame, from its meshes (any thread, before it is shown); see <see cref="DrawViewer"/>.</summary>
    public Func<int, SceneMesh?>? ViewerMeshes { get; set; }

    // The marker's size on screen: a 1/0.012 of its distance (the page's MarkerSize).
    private const float MarkerSize = 0.012f;

    private void ViewerSetUp()
    {
      if (_viewerSignature != null)
      {
        return;
      }
      var d3d = _device.Device;
      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, ViewerDrawConstants), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, uint.MaxValue, 0, 1, 0, DescriptorRangeFlags.DescriptorsVolatile)), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex),
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.Pixel),
      };
      // The page's textures: LINEAR_MIPMAP_LINEAR, REPEAT, anisotropic up to 16 (ModelView.html ~1196-1200).
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
      _viewerSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers));
      _viewerVS = ViewerCompile(DxcShaderStage.Vertex, "VSViewer");
      _viewerSkinnedVS = ViewerCompile(DxcShaderStage.Vertex, "VSViewerSkinned");
      _viewerPS = ViewerCompile(DxcShaderStage.Pixel, "PSViewer");
      _viewerWirePS = ViewerCompile(DxcShaderStage.Pixel, "PSWire");
      _viewerLineVS = ViewerCompile(DxcShaderStage.Vertex, "VSLine");
      _viewerLinePS = ViewerCompile(DxcShaderStage.Pixel, "PSLine");
      _viewerFullVS = ViewerCompile(DxcShaderStage.Vertex, "VSFull");
      _viewerGradientPS = ViewerCompile(DxcShaderStage.Pixel, "PSGradient");
      unsafe
      {
        _viewerFrame = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                                   ResourceDescription.Buffer((ulong)PaletteSlots * ViewerFrameBytes), ResourceStates.GenericRead);
        void* mapped;
        _viewerFrame.Map(0, null, &mapped).CheckError();
        _viewerFrameMapped = (byte*)mapped;
      }
      _viewerRtv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
      _viewerDsv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
    }

    private static byte[] ViewerCompile(DxcShaderStage stage, string entryPoint)
    {
      using IDxcResult result = DxcCompiler.Compile(stage, ViewerShader, entryPoint);
      if (result.GetStatus().Failure)
      {
        throw new InvalidOperationException("HLSL " + entryPoint + ": " + result.GetErrors());
      }
      return result.GetObjectBytecodeArray();
    }

    // The pipelines by kind: 0-63 the meshes (skinned 1, back faces culled 2, pulled 4 (-2) or 8 (-4), 16 see-through
    // (ONE / INVSRCALPHA, no depth written), 32 see-through writing depth); 64/65 the wire frame (skinned); 66 lines with
    // the depth test, 67 without; 68 the gradient.
    private ID3D12PipelineState ViewerPipeline(int key)
    {
      if (_viewerPipelines.TryGetValue(key, out var made))
      {
        return made;
      }
      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        new InputElementDescription("ALPHA", 0, Format.R32_Float, 32, 0),
        new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 36, 0),
        new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 2),
        new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 16, 2),
        new InputElementDescription("TEXCOORD", 2, Format.R32G32_Float, 32, 2),
        new InputElementDescription("TEXCOORD", 3, Format.R32G32B32A32_Float, 40, 2),
      };
      var skinnedInput = new List<InputElementDescription>(input)
      {
        new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 0, 1),
        new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 16, 1),
      }.ToArray();
      var description = new GraphicsPipelineStateDescription
      {
        RootSignature = _viewerSignature,
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true, MultisampleEnable = true },
        BlendState = BlendDescription.Opaque,
        // The page's depth test less or equal (ModelView.html ~3184).
        DepthStencilState = DepthStencilDescription.Default with { DepthFunc = ComparisonFunction.LessEqual },
        RenderTargetFormats = new[] { PanelSwapChainFormats.Color },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(ViewerSamples, 0),
      };
      if (key < 64)
      {
        bool skinned = (key & 1) != 0;
        description.VertexShader = skinned ? _viewerSkinnedVS : _viewerVS;
        description.PixelShader = _viewerPS;
        description.InputLayout = new InputLayoutDescription(skinned ? skinnedInput : input);
        var raster = description.RasterizerState;
        if ((key & 2) != 0)
        {
          raster = raster with { CullMode = CullMode.Back };
        }
        // The page's polygonOffset(-0.25, -2) for a blended material that isn't glass, (-0.25, -4) for a decal
        // (ModelView.html ~3528-3530): the slope's factor, and units of the depth's smallest step (2^-24 both).
        if ((key & 4) != 0)
        {
          raster = raster with { DepthBias = -2, SlopeScaledDepthBias = -0.25f };
        }
        if ((key & 8) != 0)
        {
          raster = raster with { DepthBias = -4, SlopeScaledDepthBias = -0.25f };
        }
        description.RasterizerState = raster;
        if ((key & 48) != 0)
        {
          // The see-through pass: blendFunc(ONE, ONE_MINUS_SRC_ALPHA), depth written only where the page sets depthMask
          // (glassDepth; ModelView.html ~3342, ~3701-3702).
          var blend = BlendDescription.Opaque;
          blend.RenderTarget[0] = blend.RenderTarget[0] with
          {
            BlendEnable = true,
            SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
          };
          description.BlendState = blend;
          if ((key & 32) == 0)
          {
            description.DepthStencilState = DepthStencilDescription.Read with { DepthFunc = ComparisonFunction.LessEqual };
          }
        }
      }
      else if (key < 66)
      {
        bool skinned = key == 65;
        description.VertexShader = skinned ? _viewerSkinnedVS : _viewerVS;
        description.PixelShader = _viewerWirePS;
        description.InputLayout = new InputLayoutDescription(skinned ? skinnedInput : input);
        description.PrimitiveTopologyType = PrimitiveTopologyType.Line;
        description.RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid);
      }
      else if (key < 68)
      {
        description.VertexShader = _viewerLineVS;
        description.PixelShader = _viewerLinePS;
        description.InputLayout = new InputLayoutDescription(new[] { new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0) });
        description.PrimitiveTopologyType = PrimitiveTopologyType.Line;
        description.RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid);
        if (key == 67)
        {
          description.DepthStencilState = DepthStencilDescription.None;
        }
      }
      else
      {
        description.VertexShader = _viewerFullVS;
        description.PixelShader = _viewerGradientPS;
        description.InputLayout = default;
        description.DepthStencilState = DepthStencilDescription.None;
      }
      var pipeline = _device.Device.CreateGraphicsPipelineState<ID3D12PipelineState>(description);
      _viewerPipelines[key] = pipeline;
      return pipeline;
    }

    private void ViewerTargets(int width, int height)
    {
      if (_viewerColor != null && _viewerWidth == width && _viewerHeight == height)
      {
        return;
      }
      if (_viewerColor != null)
      {
        // (Still read by the frame in flight: freed once the GPU is past it.)
        var color = _viewerColor;
        var depth = _viewerDepth;
        _device.Retire(() =>
        {
          color.Dispose();
          depth?.Dispose();
        });
      }
      var d3d = _device.Device;
      _viewerColor = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(PanelSwapChainFormats.Color, (uint)width, (uint)height, 1, 1, ViewerSamples, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.RenderTarget, new ClearValue(PanelSwapChainFormats.Color, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
      _viewerDepth = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(PanelSwapChainFormats.Depth, (uint)width, (uint)height, 1, 1, ViewerSamples, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.DepthWrite, new ClearValue(PanelSwapChainFormats.Depth, 1.0f, 0));
      d3d.CreateRenderTargetView(_viewerColor, null, _viewerRtv!.GetCPUDescriptorHandleForHeapStart());
      d3d.CreateDepthStencilView(_viewerDepth, null, _viewerDsv!.GetCPUDescriptorHandleForHeapStart());
      _viewerWidth = width;
      _viewerHeight = height;
    }

    // A model's parts by their mesh's id (made once).
    private Dictionary<int, List<int>> PartsOfMesh(int number, Model model)
    {
      if (!_viewerPartsOfMesh.TryGetValue(number, out var map))
      {
        map = new Dictionary<int, List<int>>();
        for (int i = 0; i < model.Parts.Length; i++)
        {
          if (!map.TryGetValue(model.Parts[i].MeshId, out var list))
          {
            map[model.Parts[i].MeshId] = list = new List<int>();
          }
          list.Add(i);
        }
        _viewerPartsOfMesh[number] = map;
      }
      return map;
    }

    // A model's wire frame (the page's wireOf, ModelView.html ~1051-1070: each edge once per mesh), made when first shown;
    // in an upload heap (only while the wire frame is on).
    private unsafe (ID3D12Resource Buffer, IndexBufferView View, (uint Start, uint Count)[] Ranges)? WireOf(int number, Model model)
    {
      if (_viewerWires.TryGetValue(number, out var made))
      {
        return made;
      }
      if (ViewerMeshes == null)
      {
        return null;
      }
      var edges = new List<uint>();
      var ranges = new (uint, uint)[model.Parts.Length];
      for (int i = 0; i < model.Parts.Length; i++)
      {
        var part = model.Parts[i];
        uint start = (uint)edges.Count;
        if (ViewerMeshes(part.MeshId) is SceneMesh mesh)
        {
          var seen = new HashSet<long>();
          var indices = mesh.Indices;
          for (int t = 0; t + 2 < indices.Length; t += 3)
          {
            for (int k = 0; k < 3; k++)
            {
              uint a = indices[t + k], b = indices[t + (k + 1) % 3];
              long key = a < b ? a * 65536L + b : b * 65536L + a;
              if (seen.Add(key))
              {
                edges.Add(part.VertexBase + a);
                edges.Add(part.VertexBase + b);
              }
            }
          }
        }
        ranges[i] = (start, (uint)edges.Count - start);
      }
      if (edges.Count == 0)
      {
        edges.Add(0);
        edges.Add(0);
      }
      ulong bytes = (ulong)edges.Count * 4;
      var buffer = _device.Device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None, ResourceDescription.Buffer(bytes), ResourceStates.GenericRead);
      void* mapped;
      buffer.Map(0, null, &mapped).CheckError();
      var array = edges.ToArray();
      fixed (uint* source = array)
      {
        Buffer.MemoryCopy(source, mapped, (long)bytes, (long)bytes);
      }
      buffer.Unmap(0);
      var wire = (buffer, new IndexBufferView(buffer.GPUVirtualAddress, (uint)bytes, Format.R32_UInt), ranges);
      _viewerWires[number] = wire;
      return wire;
    }

    private unsafe ID3D12Resource LineBuffer(List<float> floats)
    {
      if (floats.Count == 0)
      {
        floats.AddRange(new float[6]);
      }
      ulong bytes = (ulong)floats.Count * 4;
      var buffer = _device.Device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None, ResourceDescription.Buffer(bytes), ResourceStates.GenericRead);
      void* mapped;
      buffer.Map(0, null, &mapped).CheckError();
      var array = floats.ToArray();
      fixed (float* source = array)
      {
        Buffer.MemoryCopy(source, mapped, (long)bytes, (long)bytes);
      }
      buffer.Unmap(0);
      return buffer;
    }

    // The lights' markers as lines (the page's setLights, ModelView.html ~1297-1346): an omni light a diamond 2 across, a
    // spot a small dot with its cone 5 long, 12 points round its rim, every third joined to the tip.
    private void MakeMarkers(IReadOnlyList<ViewerLight> lights)
    {
      if (ReferenceEquals(_markersFor, lights) && _markerBuffer != null)
      {
        return;
      }
      if (_markerBuffer != null)
      {
        var old = _markerBuffer;
        _device.Retire(() => old.Dispose());
      }
      var floats = new List<float>();
      var ranges = new (int, int)[lights.Count];
      void Line(Vector3 a, Vector3 b)
      {
        floats.Add(a.X); floats.Add(a.Y); floats.Add(a.Z);
        floats.Add(b.X); floats.Add(b.Y); floats.Add(b.Z);
      }
      for (int i = 0; i < lights.Count; i++)
      {
        var l = lights[i];
        int first = floats.Count / 3;
        const float size = 1;
        if (!l.Spot)
        {
          var corners = new[] { new Vector3(size, 0, 0), new Vector3(-size, 0, 0), new Vector3(0, size, 0), new Vector3(0, -size, 0), new Vector3(0, 0, size), new Vector3(0, 0, -size) };
          foreach (var (a, b) in new[] { (0, 4), (1, 4), (2, 4), (3, 4), (0, 5), (1, 5), (2, 5), (3, 5), (0, 2), (2, 1), (1, 3), (3, 0) })
          {
            Line(corners[a], corners[b]);
          }
        }
        else
        {
          float dot = size * 0.35f;
          Line(new Vector3(-dot, 0, 0), new Vector3(dot, 0, 0));
          Line(new Vector3(0, -dot, 0), new Vector3(0, dot, 0));
          Line(new Vector3(0, 0, -dot), new Vector3(0, 0, dot));
          var direction = l.Direction.LengthSquared() > 0 ? Vector3.Normalize(l.Direction) : Vector3.UnitZ;
          float length = size * 5;
          var side = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
          var up = Vector3.Cross(side, direction);
          float half = Math.Max(1, Math.Min(89, l.Falloff > 0 ? l.Falloff : 30)) * MathF.PI / 180;
          float r = MathF.Tan(half) * length;
          var end = direction * length;
          var rim = new Vector3[12];
          for (int k = 0; k < 12; k++)
          {
            float angle = k / 12f * MathF.PI * 2;
            rim[k] = end + side * MathF.Cos(angle) * r + up * MathF.Sin(angle) * r;
          }
          Line(Vector3.Zero, end);
          for (int k = 0; k < 12; k++)
          {
            Line(rim[k], rim[(k + 1) % 12]);
            if (k % 3 == 0)
            {
              Line(Vector3.Zero, rim[k]);
            }
          }
        }
        ranges[i] = (first, floats.Count / 3 - first);
      }
      _markerBuffer = LineBuffer(floats);
      _markerRanges = ranges;
      _markersFor = lights;
    }

    // Lines every half metre, 40 m across, at the height given (the page's createGrid, ModelView.html ~3130-3147).
    private void MakeGrid(float ground)
    {
      if (_gridFor == ground && _gridBuffer != null)
      {
        return;
      }
      if (_gridBuffer != null)
      {
        var old = _gridBuffer;
        _device.Retire(() => old.Dispose());
      }
      var floats = new List<float>();
      for (int i = -40; i <= 40; i++)
      {
        float v = i * 0.5f;
        floats.AddRange(new[] { -20f, v, ground, 20f, v, ground, v, -20f, ground, v, 20f, ground });
      }
      _gridBuffer = LineBuffer(floats);
      _gridCount = floats.Count / 3;
      _gridFor = ground;
    }

    /// <summary>
    /// Draws a frame of the model or animation view (render thread) into <paramref name="target"/> (B8G8R8A8, in
    /// RENDER_TARGET, left so): the page's background, then the model's meshes shown, solid first, then the see-through
    /// ones (dirt decals first), then the wire frame, the ground grid and the lights' markers; all at 4 samples a pixel,
    /// resolved into the target.
    /// </summary>
    public unsafe void DrawViewer(ID3D12GraphicsCommandList list, ID3D12Resource target, int width, int height, ViewerFrame frame)
    {
      ViewerSetUp();
      ViewerTargets(width, height);
      DrawCalls = 0;
      var rtv = _viewerRtv!.GetCPUDescriptorHandleForHeapStart();
      var dsv = _viewerDsv!.GetCPUDescriptorHandleForHeapStart();
      list.OMSetRenderTargets(rtv, dsv);
      list.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);

      // The frame's constants and the bones into this frame's slots.
      _paletteSlot = (_paletteSlot + 1) % PaletteSlots;
      var constants = (float*)(_viewerFrameMapped + _paletteSlot * ViewerFrameBytes);
      *(Matrix4x4*)constants = frame.ViewProjection;
      void Put(int at, Vector3 v, float w = 0)
      {
        constants[at] = v.X;
        constants[at + 1] = v.Y;
        constants[at + 2] = v.Z;
        constants[at + 3] = w;
      }
      Put(16, frame.Eye, height);
      Put(20, frame.Light);
      Put(24, frame.ToEye);
      // The wire frame's colour (0.95, 0.75, 0.25), the gradient's #68788a to #22252a.
      Put(28, new Vector3(0.95f, 0.75f, 0.25f));
      Put(32, new Vector3(0x68, 0x78, 0x8a) / 255f);
      Put(36, new Vector3(0x22, 0x25, 0x2a) / 255f);
      ulong frameAddress = _viewerFrame!.GPUVirtualAddress + (ulong)(_paletteSlot * ViewerFrameBytes);

      // The background: the gradient, grey (#808080), black or white (ModelView.html ~24-27).
      if (frame.Background == 0)
      {
        list.SetGraphicsRootSignature(_viewerSignature);
        list.SetGraphicsRootConstantBufferView(3, frameAddress);
        list.SetPipelineState(ViewerPipeline(68));
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        list.DrawInstanced(3, 1, 0, 0);
      }
      else
      {
        float grey = frame.Background == 1 ? 128 / 255f : frame.Background == 2 ? 0 : 1;
        list.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(grey, grey, grey, 1));
      }

      list.SetDescriptorHeaps(_textures.Heap);
      list.SetGraphicsRootSignature(_viewerSignature);
      list.SetGraphicsRootDescriptorTable(1, _textures.Handle(0));
      list.SetGraphicsRootConstantBufferView(3, frameAddress);
      var draw = stackalloc float[ViewerDrawConstants];
      for (int k = 0; k < ViewerDrawConstants; k++)
      {
        draw[k] = 0;
      }
      *(Matrix4x4*)draw = Matrix4x4.Identity;

      if (frame.Model >= 0 && frame.Model < _models.Count && _models[frame.Model] is Model model)
      {
        bool skinned = model.Skin != null;
        int bones = 0;
        ulong paletteAddress = _palette.GPUVirtualAddress;
        if (skinned && frame.Palette is Matrix4x4[] palette && palette.Length > 0 && palette.Length <= PaletteCapacity)
        {
          long at = (long)_paletteSlot * PaletteCapacity;
          fixed (Matrix4x4* source = palette)
          {
            Buffer.MemoryCopy(source, _paletteMapped + at, palette.Length * 64L, palette.Length * 64L);
          }
          paletteAddress = _palette.GPUVirtualAddress + (ulong)at * 64;
          bones = palette.Length;
        }
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        list.IASetVertexBuffers(0, model.VertexView);
        if (skinned)
        {
          list.IASetVertexBuffers(1, model.SkinView);
          list.SetGraphicsRootShaderResourceView(2, paletteAddress);
        }
        list.IASetVertexBuffers(2, model.ExtrasView);
        list.SetGraphicsRootShaderResourceView(4, model.PartData.GPUVirtualAddress);
        list.IASetIndexBuffer(model.IndexView);
        draw[18] = bones;
        var partsOf = PartsOfMesh(frame.Model, model);

        // The page's per-mesh decisions (drawMeshes, ModelView.html ~3381-3707, outside false, nothing baked or an actor).
        bool SeeThrough(Part part) => part.Material is SceneMaterial m && (m.Glass || (m.Blend && frame.Textures && part.Texture != 0));
        int lastKey = -1;
        void DrawPart(Part part, bool glassPass)
        {
          var m = part.Material!;
          bool textured = frame.Textures && part.Texture != 0;
          var vs = m.VehicleShine is { Length: >= 3 } v ? v[2] : 0;
          bool vehicleGlass = glassPass && m.Glass && m.Vehicle && textured && vs > 0.5f && vs < 1.5f;
          bool mapGlass = glassPass && m.Glass && !m.Vehicle && textured && part.HasAlphas;
          bool textureAlpha = (glassPass && !m.Glass) || vehicleGlass || mapGlass;
          float alpha = glassPass && m.Glass && !vehicleGlass && !mapGlass ? 0.45f : 1f;
          bool glassDepth = glassPass && m.Glass && (m.Vehicle || mapGlass);
          // Culled: only a map's glass here (the page's mapGlass; the other rules need a location or the cast).
          bool culled = mapGlass;
          int key = (skinned ? 1 : 0) | (culled ? 2 : 0) | (m.Decal ? 8 : m.Blend && !m.Glass ? 4 : 0) |
                    (glassPass ? (glassDepth ? 32 : 16) : 0);
          var bits = (textured ? ViewerBits.Texture : 0) | (m.Cutout ? ViewerBits.Cutout : 0) | (textureAlpha ? ViewerBits.TextureAlpha : 0) |
                     (m.Stipple ? ViewerBits.Stipple : 0) | (m.DirtMask != null ? ViewerBits.DirtDecal : 0) | (m.Tree ? ViewerBits.Tree : 0) |
                     (m.Vehicle ? ViewerBits.Vehicle : 0) | (m.Emissive && textured ? ViewerBits.Emissive : 0) |
                     (part.HasNormals ? ViewerBits.Normals : 0) | (m.VehicleLights ? ViewerBits.VehicleLight : 0) |
                     (part.HasTangents ? ViewerBits.Tangents : 0) | (IsLayeredMesh(part) ? ViewerBits.Layered : 0);
          if (frame.UvRows != null && frame.UvRows.TryGetValue(m.Id, out var rows))
          {
            bits |= ViewerBits.UvAnim;
            draw[28] = rows.Row0.X; draw[29] = rows.Row0.Y; draw[30] = rows.Row0.Z;
            draw[32] = rows.Row1.X; draw[33] = rows.Row1.Y; draw[34] = rows.Row1.Z;
          }
          draw[16] = (float)bits;
          draw[17] = part.Index;
          // gta_emissivestrong's multiplier (not the night ones': the page's emissiveScale, ModelView.html ~1148).
          draw[19] = m.EmissiveMultiplier is float em && em != 0 && !m.EmissiveAtNight ? em : 0;
          var color = ViewerColor(m);
          draw[20] = color.X; draw[21] = color.Y; draw[22] = color.Z;
          draw[23] = alpha;
          var mask = m.DirtMask is { Length: >= 3 } dm ? new Vector3(dm[0], dm[1], dm[2]) : Vector3.Zero;
          draw[24] = mask.X; draw[25] = mask.Y; draw[26] = mask.Z;
          if (key != lastKey)
          {
            list.SetPipelineState(ViewerPipeline(key));
            lastKey = key;
          }
          list.SetGraphicsRoot32BitConstants(0, ViewerDrawConstants, draw, 0);
          list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
          DrawCalls++;
        }

        foreach (int id in frame.Visible)
        {
          if (partsOf.TryGetValue(id, out var parts))
          {
            foreach (int p in parts)
            {
              if (!SeeThrough(model.Parts[p]))
              {
                DrawPart(model.Parts[p], false);
              }
            }
          }
        }
        // The see-through pass: the dirt decals first (the page's dirtFirst), then the rest, each in the visible order.
        for (int sweep = 0; sweep < 2; sweep++)
        {
          foreach (int id in frame.Visible)
          {
            if (partsOf.TryGetValue(id, out var parts))
            {
              foreach (int p in parts)
              {
                var part = model.Parts[p];
                if (SeeThrough(part) && (part.Material!.DirtMask != null) == (sweep == 0))
                {
                  DrawPart(part, true);
                }
              }
            }
          }
        }

        // The wire frame: each mesh's edges in its colour, the depth test less or equal (ModelView.html ~3971-3982).
        if (frame.Wireframe && WireOf(frame.Model, model) is var (_, wireView, ranges))
        {
          list.SetPipelineState(ViewerPipeline(skinned ? 65 : 64));
          lastKey = -1;
          list.IASetPrimitiveTopology(PrimitiveTopology.LineList);
          list.IASetIndexBuffer(wireView);
          draw[16] = 0;
          list.SetGraphicsRoot32BitConstants(0, ViewerDrawConstants, draw, 0);
          foreach (int id in frame.Visible)
          {
            if (partsOf.TryGetValue(id, out var parts))
            {
              foreach (int p in parts)
              {
                if (ranges[p].Count > 0)
                {
                  list.DrawIndexedInstanced(ranges[p].Count, 1, ranges[p].Start, 0, 0);
                  DrawCalls++;
                }
              }
            }
          }
        }
      }

      // The ground grid while the ped moves through the world (ModelView.html ~3962-3969), in (0.55, 0.6, 0.68).
      list.IASetPrimitiveTopology(PrimitiveTopology.LineList);
      if (frame.GridHeight is float ground)
      {
        MakeGrid(ground);
        list.SetPipelineState(ViewerPipeline(66));
        list.IASetVertexBuffers(0, new VertexBufferView(_gridBuffer!.GPUVirtualAddress, (uint)(_gridCount * 12), 12));
        for (int k = 0; k < ViewerDrawConstants; k++)
        {
          draw[k] = 0;
        }
        draw[20] = 0.55f; draw[21] = 0.6f; draw[22] = 0.68f;
        list.SetGraphicsRoot32BitConstants(0, ViewerDrawConstants, draw, 0);
        list.DrawInstanced((uint)_gridCount, 1, 0, 0);
        DrawCalls++;
      }

      // The lights while a mesh of their drawable is shown: faint where the model hides them, in their colour where in
      // sight (ModelView.html ~3985-4003; a black one grey, ~1340-1341).
      if (frame.ShowLights && frame.Lights is { Count: > 0 } lights)
      {
        MakeMarkers(lights);
        var visible = new HashSet<int>(frame.Visible);
        list.IASetVertexBuffers(0, new VertexBufferView(_markerBuffer!.GPUVirtualAddress, (uint)(_markerRanges.Length == 0 ? 24 : (_markerRanges[^1].First + _markerRanges[^1].Count) * 12), 12));
        for (int pass = 0; pass < 2; pass++)
        {
          bool hidden = pass == 0;
          list.SetPipelineState(ViewerPipeline(hidden ? 67 : 66));
          for (int i = 0; i < lights.Count; i++)
          {
            var l = lights[i];
            bool shown = false;
            foreach (int id in l.Meshes)
            {
              if (visible.Contains(id))
              {
                shown = true;
                break;
              }
            }
            if (!shown)
            {
              continue;
            }
            var c = l.Color.X > 0.05f || l.Color.Y > 0.05f || l.Color.Z > 0.05f ? l.Color : new Vector3(0.6f);
            if (hidden)
            {
              c = c * 0.3f + new Vector3(0.12f);
            }
            for (int k = 0; k < ViewerDrawConstants; k++)
            {
              draw[k] = 0;
            }
            draw[20] = c.X; draw[21] = c.Y; draw[22] = c.Z;
            draw[36] = l.Position.X; draw[37] = l.Position.Y; draw[38] = l.Position.Z;
            draw[39] = Vector3.Distance(l.Position, frame.Eye) * MarkerSize;
            list.SetGraphicsRoot32BitConstants(0, ViewerDrawConstants, draw, 0);
            list.DrawInstanced((uint)_markerRanges[i].Count, 1, (uint)_markerRanges[i].First, 0);
            DrawCalls++;
          }
        }
      }

      // The samples into the target.
      list.ResourceBarrierTransition(_viewerColor!, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
      list.ResourceBarrierTransition(target, ResourceStates.RenderTarget, ResourceStates.ResolveDest);
      list.ResolveSubresource(target, 0, _viewerColor!, 0, PanelSwapChainFormats.Color);
      list.ResourceBarrierTransition(target, ResourceStates.ResolveDest, ResourceStates.RenderTarget);
      list.ResourceBarrierTransition(_viewerColor!, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
    }

    // Whether a part's mesh has the page's layered attributes (layer coordinates, weights and colours: m.layered).
    private bool IsLayeredMesh(Part part) => ViewerMeshes?.Invoke(part.MeshId) is SceneMesh mesh && mesh.LayerUvs != null && mesh.LayerWeights != null && mesh.Colors != null;

    /// <summary>
    /// A material's colour without its texture (the page's addMaterial: m.paint || m.color || hsl((id x 0.618) mod 1,
    /// 0.25, 0.62), ModelView.html ~1132, ~1140, ~1285-1288; the material message's color: Color, or black for a render
    /// target, ModelScene ~1608).
    /// </summary>
    public static Vector3 ViewerColor(SceneMaterial m)
    {
      if (m.Color is { Length: >= 3 } c)
      {
        return new Vector3(c[0], c[1], c[2]);
      }
      if (m.RenderTarget)
      {
        return Vector3.Zero;
      }
      double h = (m.Id * 0.618) % 1, s = 0.25, l = 0.62;
      float F(double n)
      {
        double k = (n + h * 12) % 12;
        return (float)(l - s * Math.Min(l, 1 - l) * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
      }
      return new Vector3(F(0), F(8), F(4));
    }

    private void DisposeViewer()
    {
      foreach (var p in _viewerPipelines.Values)
      {
        p.Dispose();
      }
      _viewerPipelines.Clear();
      foreach (var w in _viewerWires.Values)
      {
        w.Buffer.Dispose();
      }
      _viewerWires.Clear();
      _markerBuffer?.Dispose();
      _gridBuffer?.Dispose();
      _viewerColor?.Dispose();
      _viewerDepth?.Dispose();
      _viewerRtv?.Dispose();
      _viewerDsv?.Dispose();
      if (_viewerFrame != null)
      {
        _viewerFrame.Unmap(0);
        _viewerFrame.Dispose();
      }
      _viewerSignature?.Dispose();
    }

    /// <summary>A model's wire frame and parts by mesh forgotten (its number given up: Clear or RemoveGroup), render thread.</summary>
    public void ForgetViewerModel(int number)
    {
      _viewerPartsOfMesh.Remove(number);
      if (_viewerWires.Remove(number, out var wire))
      {
        _device.Retire(() => wire.Buffer.Dispose());
      }
    }
  }
}
