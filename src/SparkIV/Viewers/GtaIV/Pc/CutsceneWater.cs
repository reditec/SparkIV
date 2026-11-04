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
using SparkIV.WinUI.Rendering.D3D12;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// What a GTA IV (PC) frame's water is drawn with (#112 step 4f), in the page's light units (the game's HDR x the time
  /// cycle's Exposure): water.fxc's waterColour (x SkyLightMult, as bottomSkyColour.w), waterReflectionScale, the sun's
  /// shine scale (gDirectionalLight.w), the fog (rage_postfx program 21, 0xAF3A50) and the sky the empty mirror shows,
  /// and the wind the two simulations are kicked by.
  /// </summary>
  public struct WaterParams
  {
    /// <summary>waterColour (columns 31 to 33, bytes floored / 256) x SkyLightMult (column 60) x the exposure.</summary>
    public Vector3 WaterColor;
    /// <summary>The water's alpha (column 34): how deep the see-through water looks (unused while there's no scene depth).</summary>
    public float WaterAlpha;
    /// <summary>waterReflectionScale: column 125 (0-based), 1 where it is 0 (0xAEFEB0, 0xAD7EF0).</summary>
    public float Reflection;
    /// <summary>gDirectionalLight.w: max(1, (SkyLightMult + column 61) / column 57) (0xAC30F0); scales the glint.</summary>
    public float ShineScale;
    /// <summary>The near and far fog's light (globalFogColorN, globalFogColor) x the exposure, as the sky's.</summary>
    public Vector3 FogNear;
    public Vector3 FogFar;
    /// <summary>globalFogParams: FogStart (column 24), the fog's end max(FarClip (23), FogStart + 1), NearFogAmount (11 / 255).</summary>
    public float FogStart;
    public float FogEnd;
    public float NearFogAmount;
    /// <summary>gDepthFxParams: DesaturationFar, GammaFar, DepthFxNear, DepthFxFar (columns 48, 50, 51, 52).</summary>
    public Vector4 DepthFx;
    /// <summary>The page's exposure (column 35; 1 where it is 0): the depth effect's luminance is divided by it.</summary>
    public float Exposure;
    /// <summary>The frame's sky: what the mirror shows while it holds nothing else (see <see cref="CutsceneWater"/>).</summary>
    public SkyParams Sky;
    /// <summary>The wave texture's wind (the page's WaterWind) and the sea swell's (SwellWind).</summary>
    public float WaterWind;
    public float SwellWind;
    /// <summary>The sea swell's window centre (the mission marker, scene-local), or null: round the camera.</summary>
    public Vector3? SwellAt;

    /// <summary>
    /// The water's parameters of a time cycle (outside's, a room's, or a mix) as the page's "sky" message has them, with
    /// the frame's <paramref name="sky"/> (its fog's colours, and its sky for the empty mirror). <paramref name="hour"/>:
    /// as <see cref="SkyParams.From"/>'s; the water's sun comes from the frame's <see cref="FrameLight"/>, as the meshes'.
    /// <paramref name="wind"/>: the mission script's FORCE_WIND (the "water" message): then both winds are it, else the
    /// page's breeze 0.25 for the waves and 0 for the swell. <paramref name="swellAt"/>: the mission marker, scene-local.
    /// </summary>
    public static WaterParams From(TimeCycle c, float hour, SkyParams sky, float? wind = null, Vector3? swellAt = null)
    {
      float exposure = c.Exposure;
      return new WaterParams
      {
        WaterColor = new Vector3(c.Water.X, c.Water.Y, c.Water.Z) * c.SkyLightMultiplier * exposure,
        WaterAlpha = c.Water.W,
        Reflection = c.WaterReflection != 0 ? c.WaterReflection : 1f,
        ShineScale = c.DirectionalLightW,
        FogNear = sky.FogNear,
        FogFar = sky.FogFar,
        FogStart = c.FogStart,
        FogEnd = Math.Max(c.FarClip, c.FogStart + 1),
        NearFogAmount = c.NearFogAmount,
        DepthFx = c.DepthFx,
        Exposure = exposure != 0 ? exposure : 1f,
        Sky = sky,
        WaterWind = wind ?? 0.25f,
        SwellWind = wind ?? 0f,
        SwellAt = swellAt,
      };
    }
  }

  /// <summary>
  /// A GTA IV (PC) cutscene's water for the native renderer (#112 step 4f), as the page's (gtaiv-pc.js, ModelView.html)
  /// after the game's water.fxc (GTAIV.1 PS 0x24641060 and 0x2dbbdd80): the location's water.dat quads, flat, and outside
  /// the sea grid (CWater::UpdateDynamicWaterTask: 100 x 100 points 2 m apart round the camera or the mission marker,
  /// swelling, simulated on the CPU) where the quads lie inside its window, both shaded by the game's water: three
  /// sizes of ripples from the wave texture (waterTex.fxc, 1024 x 1024, simulated on the GPU), the swell's slope, the
  /// Fresnel mix of the water's colour and the mirror, the sun's glint, the foam of the near field, then the game's fog.
  /// Drawn after the solid meshes and before the see-through ones, into the light target, blended premultiplied
  /// (ONE, ONE_MINUS_SRC_ALPHA), depth tested LESS_EQUAL and written, both faces.
  ///
  /// Not yet (the page has them):
  /// - the mirrored scene (the reflection pass, spec 7): the mirror shows only the game's sky in the bounced direction
  ///   (gta_atmoscatt_clouds' light without clouds and without the near fog, as the mirror pass draws it first) - what
  ///   the page's HDR mirror shows where no mirrored mesh covers it. Without the game's sky columns: the far fog's
  ///   light (the page tone-unmaps its picture colours horizon / zenith instead). The ripples' shift of the mirror's
  ///   lookup is taken as the bounce off the steep normal.
  /// - the scene's depth behind the water (see-through shallows, the shore's normal bend): taken as 1 m everywhere, and
  ///   the water opaque (a = 1), as the page without its depth copy.
  /// - the sun's shadow on the water (gameSunShadow): 1.
  /// - the stencil "no water inside boats" (bucket 30 planes).
  /// - the page's haze edge (a page device) and its culling.
  ///
  /// Deterministic, unlike the page (spec open question 13): wall-clock time is replaced by the given time in seconds,
  /// Math.random by seeded System.Random (the wave noise, its per-step shift, the swell's kicks).
  /// </summary>
  public sealed class CutsceneWater : IDisposable
  {
    // The wave texture (waterTex.fxc): 128 << water quality, 1024 in GTAIV.1; RGBA8 ping-ponged.
    private const int SimSize = 1024;
    private const Format SimFormat = Format.R8G8B8A8_UNorm;
    // The page's warm-up: 20000 steps of 16 ms with kick 0.25 [PAGE, tuned to the trace's spread at bc_3 s1 f79]. Done
    // a share per frame (all in one list could run the GPU past its timeout).
    private const int WarmUpSteps = 20000;
    private const int WarmUpPerFrame = 2000;

    // The sea swell (0xADBA40, 0xADB770): 100 x 100 points 2 m apart, a 200 m window.
    private const int SwellSize = 100;
    private const int SwellCells = SwellSize * SwellSize;
    private const int GridSide = SwellSize + 1;
    private const int GridVertexFloats = 6;
    private const int GridIndexCount = SwellSize * SwellSize * 6;

    // The water mask (the page's 512 x 512 R8): a byte per texel, read as uints by the shader.
    private const int MaskSize = 512;

    private const int QuadVertexFloats = 6;

    private const int NoiseSeed = 0x5EA1;
    private const int StepSeed = 0x5EA2;
    private const int SwellSeed = 0x5EA3;

    // Each frame's slot of the upload ring: the constants, the grid's vertices, the swell's heights, the mask.
    private const int ConstantBytes = 512;
    private const int GridBytes = GridSide * GridSide * GridVertexFloats * 4;
    private const int SwellBytes = SwellCells * 4;
    private const int GridOffset = ConstantBytes;
    private const int SwellOffset = GridOffset + (GridBytes + 255) / 256 * 256;
    private const int MaskOffset = SwellOffset + (SwellBytes + 255) / 256 * 256;
    private const int SlotBytes = MaskOffset + MaskSize * MaskSize;
    private const int RingSlots = 4;

    private const string Shader = @"
struct PassConstants
{
  float4 v;   // water: x the sea grid (1), y the sea on (1), z ripples on (1); wave step: x kick, y step, zw the noise's shift
};
ConstantBuffer<PassConstants> pc : register(b0);
struct Water
{
  row_major float4x4 ViewProjection;
  float4 Eye;          // xyz: the eye, w: WaterOriginZ (the scene origin's world z)
  float4 SunDirection; // xyz: the light's sun, w: gDirectionalLight.w
  float4 Sun;          // rgb: the sun's light, w: waterReflectionScale
  float4 AmbientAbove; // w: the water's alpha (column 34)
  float4 AmbientBelow; // w: the water level (the grid's)
  float4 WaterColor;   // rgb: waterColour x SkyLightMult x exposure, w: the sea window's x0
  float4 Forward;      // xyz: the view's forward, w: the sea window's y0
  float4 FogNear;      // rgb, w: the depth effect's exposure
  float4 FogFar;       // rgb
  float4 FogParams;    // FogStart, the fog's end, NearFogAmount
  float4 DepthFx;      // DesaturationFar, GammaFar, DepthFxNear, DepthFxFar
  float4 SkySun;       // xyz: the sky's sun, w: the game's sky on (1)
  float4 SkyColor;     // rgb, w: AzimuthHeight
  float4 Azimuth;      // rgb, w: AzimuthStrength
  float4 AzimuthEast;  // rgb, w: the page's exposure
  float4 SkySunColor;  // rgb
  float4 Hdr;          // HDRExposure, HDRSunExposure.xy, HDRExposureClamp
};
ConstantBuffer<Water> w : register(b1);
Texture2D Sim : register(t0);             // the wave texture: r height, g speed, ba slope
Texture2D Noise : register(t3);           // the wave step's random bytes
StructuredBuffer<float> Swell : register(t1);  // the swell's heights, [ring y][ring x]
StructuredBuffer<uint> Mask : register(t2);    // the water mask, a byte per texel
SamplerState Linear : register(s0);

// ---- The wave texture's step (waterTex.fxc, to the bit: spec 6.1) ----

struct VsFull { float4 position : SV_Position; };

VsFull VSFull(uint id : SV_VertexID)
{
  VsFull o;
  float2 uv = float2((id << 1) & 2, id & 2);
  o.position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}

float SimHeight(int2 at)
{
  return Sim.Load(int3(at & 1023, 0)).r;
}

// Texel rows grow with v in both APIs (the water samples it by world xy as uv), so up and down are as the page's.
float4 PSStep(VsFull i) : SV_Target
{
  int2 at = int2(i.position.xy);
  // The page's nearest read at uv + shift: texel at + floor(0.5 + shift x 1024), wrapped.
  int2 shift = int2(floor(pc.v.zw * 1024 + 0.5));
  float4 n = Noise.Load(int3((at + shift) & 1023, 0));
  float kick = pc.v.x, dt = pc.v.y;
  float disturb = n.z - 0.2 >= 0 ? 0 : (n.x * 2 - 1) * kick * 0.5;   // Threshold 0.2, Kick 0.5
  float left = SimHeight(at + int2(-1, 0)), right = SimHeight(at + int2(1, 0));
  float down = SimHeight(at + int2(0, -1)), up = SimHeight(at + int2(0, 1));
  float corners = SimHeight(at + int2(-1, -1)) + SimHeight(at + int2(1, -1)) + SimHeight(at + int2(-1, 1)) + SimHeight(at + int2(1, 1));
  float around = (left + right + down + up + corners * 0.707106769) * 0.146446615;
  float4 c = Sim.Load(int3(at, 0));
  float a = ((around - c.x) * 1.0 + (0.5 - c.x) * 0.1 + disturb) * 0.5;   // Pull 1, Rest 0.1, Accel 0.5
  float speed = c.y + a * dt;
  float height = c.x + (c.y - 0.5) * 0.5 * dt + a * dt * dt * 0.5;        // Speed 0.5
  return float4(height, speed, (right - left) * 0.5 + 0.5, (up - down) * 0.5 + 0.5);
}

// ---- The water (water.fxc: spec 5.3, 5.4) ----

struct VsIn { float3 position : POSITION; float3 color : COLOR; };
struct VsOut { float4 position : SV_Position; float3 world : WORLD; float3 color : COLOR; };

// The quads' corners and the grid's points are both in the scene's places already (the grid's heights from the CPU).
VsOut VSWater(VsIn input)
{
  VsOut o;
  o.position = mul(float4(input.position, 1), w.ViewProjection);
  o.world = input.position;
  o.color = input.color;
  return o;
}

// The swell's height at a texture coordinate (the page's 100 x 100 R16F, LINEAR, REPEAT), filtered here.
float SwellAt(int2 i)
{
  i = ((i % 100) + 100) % 100;
  return Swell[i.y * 100 + i.x];
}

float Dyn(float2 uv)
{
  float2 t = uv * 100 - 0.5;
  float2 t0 = floor(t);
  float2 f = t - t0;
  int2 i = int2(t0);
  return lerp(lerp(SwellAt(i), SwellAt(i + int2(1, 0)), f.x), lerp(SwellAt(i + int2(0, 1)), SwellAt(i + int2(1, 1)), f.x), f.y);
}

// The water mask at q (0..1 over the window; texel row by world y, as written on the CPU: no flip to mind).
float MaskAt(float2 q)
{
  uint2 t = min(uint2(q * 512), 511);
  uint index = t.y * 512 + t.x;
  return ((Mask[index >> 2] >> ((index & 3) * 8)) & 0xFF) != 0 ? 1 : 0;
}

float Pow32(float a)
{
  a *= a; a *= a; a *= a; a *= a;
  return a * a;
}

// What the empty mirror shows in a direction: gta_atmoscatt_clouds' light as CutsceneSky's PSSky draws it, without the
// clouds and without the near fog (the mirror pass's skyNearFog is 0); the far fog's light without the game's sky.
float3 SkyLight(float3 dir)
{
  if (w.SkySun.w < 0.5)
  {
    return w.FogFar.rgb;
  }
  float3 sunDir = w.SkySun.xyz;
  float3 skyV = w.SkyColor.rgb + lerp(w.AzimuthEast.rgb, w.Azimuth.rgb, 0.5 * dir.x + 0.5)
                                 * (1 - saturate(dir.z * w.SkyColor.w)) * w.Azimuth.w;
  float c = dot(dir, sunDir);
  float a = saturate(saturate(1 - w.Hdr.z) * (12.58 * c - 11.6163721));
  float b = saturate(0.9375 - 0.0625 * c);
  float sun = saturate((Pow32(a) - b * b * b * saturate(0.6 + sunDir.z) + saturate(0.5 * c - 0.0625)) * 0.5) * 4;
  float3 sunLight = sun * w.SkySunColor.rgb;
  float3 sunLight2 = sunLight * sunLight;
  float3 glow = (sunLight + sunLight2 * sunLight2) * w.Hdr.y + saturate(saturate(1 - sun) * w.Hdr.y * skyV);
  float3 light = min(glow * w.Hdr.x, w.Hdr.w) * w.AzimuthEast.w;
  if (dir.z < 0)
  {
    float t = rsqrt(dot(dir.xy, dir.xy) * 1e-8 + dir.z * dir.z / 1.6e7);
    light = lerp(light, w.FogFar.rgb, saturate(-dir.z * t * 0.00025));
  }
  return light;
}

// rage_postfx program 21 (0xAF3A50): the fog's share at a distance; + NearFogAmount only when FogStart <= 0.
float GameFogAmount(float d)
{
  float4 f = w.FogParams;
  float nearRamp = saturate(d / max(f.x, 1e-6));
  float farRamp = saturate((d - f.x) / max(f.y - f.x, 1));
  return lerp(farRamp, nearRamp, f.z) + (f.x > 0 ? 0 : f.z);
}

// The depth effect first (GTAIV.1 PS at 158854264), then the fog.
float3 GameFog(float3 light, float d)
{
  float4 fx = w.DepthFx;
  if (fx.w > fx.z)
  {
    float ramp = saturate((d - fx.z) / (fx.w - fx.z));
    float luma = dot(light, float3(0.2125, 0.7154, 0.0721));
    light = lerp(luma.xxx, light, lerp(1, fx.x, ramp)) * pow(max(luma / w.FogNear.w, 0) + 1e-7, ramp * (fx.y - 1));
  }
  float farRamp = saturate((d - w.FogParams.x) / max(w.FogParams.y - w.FogParams.x, 1));
  float3 colour = lerp(w.FogNear.rgb, w.FogFar.rgb, farRamp);
  return light + GameFogAmount(d) * (colour - light);
}

float4 PSWater(VsOut i) : SV_Target
{
  bool grid = pc.v.x > 0.5, seaOn = pc.v.y > 0.5, waves = pc.v.z > 0.5;
  float2 dynamicMin = float2(w.WaterColor.w, w.Forward.w);
  float3 eye = w.Eye.xyz;
  float3 p = i.world;

  // Split between the flat quads and the sea grid by the mask (spec 5.3).
  if (seaOn)
  {
    float2 q = (p.xy - dynamicMin) / 200;
    bool inside = all(q > 0) && all(q < 1);
    float isWater = inside ? MaskAt(q) : 0;
    if (grid ? isWater < 0.5 : isWater >= 0.5)
    {
      discard;
    }
  }

  float3 lifted = p + float3(0, 0, 1);              // the VS passes place + (0, 0, 1)
  float3 view = normalize(lifted - eye);
  float3 lightNormal = float3(0, 0, 1);
  float3 mirrorNormal = lightNormal;
  float nearWeight = i.color.b * 0.25;
  float3 fromEye = eye - lifted;
  float liftedD2 = dot(fromEye, fromEye);
  float nearField = max(1 - liftedD2 / 900, 0) * nearWeight;   // within 30 m
  if (waves)
  {
    float2 xy = p.xy;
    float near = max(1 - liftedD2 * 0.0004, 0);                 // within 50 m
    float2 big = Sim.SampleLevel(Linear, xy * 0.002, 0).zw - 0.5;               // every 500 m
    float2 middle = Sim.SampleLevel(Linear, xy * 0.01, 0).zw - 0.5;             // every 100 m
    float2 small = (Sim.SampleLevel(Linear, xy * 0.0454545, 0).zw - 0.5) * near; // every 22 m
    float2 lightSlope = big * 0.0512 * (1 - near) + middle * 0.256 + small * 0.116363637;
    float2 mirrorSlope = big * 0.0512 * (1 - near) + middle * 1.024 + small * 0.465454549;
    if (seaOn)
    {
      // The sea swell's slope within the 200 m window (the game's grid's vertex normals).
      float2 inWindow = xy - dynamicMin;
      float metres = floor(min(min(inWindow.x, 200 - inWindow.x), min(inWindow.y, 200 - inWindow.y)));
      if (metres >= 1)
      {
        float edge = metres < 10 ? (metres + 1) * 0.1 : 1;
        float2 at = xy / 200 + 0.005;
        float2 swell = float2(Dyn(at - float2(0.01, 0)) - Dyn(at + float2(0.01, 0)),
                              Dyn(at - float2(0, 0.01)) - Dyn(at + float2(0, 0.01))) * edge * near;
        lightSlope += swell;
        mirrorSlope += swell;
      }
    }
    mirrorSlope *= 1 - nearField;                   // the near field drawn towards flat
    lightNormal = normalize(float3(lightSlope, 1.00001));
    mirrorNormal = normalize(float3(mirrorSlope, 1.00001));
  }

  // TODO: the scene's depth behind the water (its linear depth less the water's: z = n f / (f - d (f - n)) for a
  // 0..1 depth buffer). Without it 1 m, as the page without its depth copy.
  float behindDepth = 1;
  float viewDepth = max(dot(p - eye, w.Forward.xyz), 1e-3);    // clip w, not distance
  float shore = 1 - saturate(behindDepth / (saturate(pow(viewDepth, 1.125) * 0.02) + 0.05));
  shore *= shore;
  float3 gentleNormal = waves
    ? normalize(float3(lightNormal.xy / max(lightNormal.z, 1e-4) - 0.5 * view.xy * shore, 1.00001 - 0.5 * view.z * shore))
    : lightNormal;
  float cosine = max(-dot(view, gentleNormal), 0);  // the game's cmp: back-facing -> 0 -> Fresnel 0.8
  float fresnel = pow(1 - cosine, 5) * 0.6 + 0.2;
  float3 bounce = reflect(view, mirrorNormal);

  // TODO: the mirrored scene (the reflection pass). For now the mirror shows the sky alone, in the bounced direction.
  float3 mirroredLight = SkyLight(normalize(bounce));

  float3 sunDir = w.SunDirection.xyz;
  float3 sunColor = w.Sun.rgb;
  float waterShadow = 1;                            // TODO: gameSunShadow at the lifted point
  float glint = pow(saturate(dot(bounce, sunDir)) + 1e-4, 512) * waterShadow * w.SunDirection.w
              * (10 - 9 * pow(nearField, 0.25));
  float upHigh = saturate((p.z + w.Eye.w - 1) * 0.5);           // VS o1.w (waterRenderSimParam.z = 0 in the traces)
  float3 mirroredWater = lerp(mirroredLight * w.Sun.w, w.FogFar.rgb, upHigh);
  float3 waterLight = w.WaterColor.rgb * 0.5 + 0.5 * fresnel * (mirroredWater + sunColor * glint)
                    + sunColor * saturate((dot(mirrorNormal, sunDir) - 0.25) * 1.3333333) * waterShadow * 0.05;
  if (waves && nearField > 0)
  {
    // The near field's foam (ps r0.z).
    float2 f = Sim.SampleLevel(Linear, p.xy * 0.05, 0).xy * (0.25 * nearWeight + 0.25) + 0.25;
    float3 foamSun = sunColor * saturate((dot(mirrorNormal, sunDir) - 0.25) * 1.3333333) * waterShadow;
    float3 foamAround = lerp(w.AmbientAbove.rgb, w.AmbientBelow.rgb, saturate(0.5 - 0.5 * mirrorNormal.z));
    waterLight = lerp(waterLight, 3 * (foamSun * f.x + foamAround * f.y), nearField);
  }
  float3 light = GameFog(waterLight, length(eye - p));   // the distance from the unlifted point
  // TODO: with the scene's depth a = saturate(pow(behindDepth x waterAlpha x 0.25, 0.25) + 0.125); opaque without.
  float alpha = 1;
  return float4(light * alpha, alpha);
}
";

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _stepPipeline;
    private readonly ID3D12PipelineState _waterPipeline;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly uint _srvSize, _rtvSize;

    // The wave texture's two states (slots 0 and 1 of both heaps), the one holding the latest, and the noise (slot 2).
    private readonly ID3D12Resource[] _sim = new ID3D12Resource[2];
    private readonly ResourceStates[] _simState = { ResourceStates.RenderTarget, ResourceStates.RenderTarget };
    private int _simCurrent;
    private bool _simCleared;
    private int _warmUpLeft;
    private double? _simLast;
    private Random _stepRandom = new Random(StepSeed);
    private ID3D12Resource? _noise;

    // The location's quads.
    private ID3D12Resource? _quadVertices;
    private ID3D12Resource? _quadIndices;
    private VertexBufferView _quadVertexView;
    private IndexBufferView _quadIndexView;
    private uint _quadIndexCount;
    private readonly List<(float X0, float Y0, float X1, float Y1)> _boxes = new List<(float, float, float, float)>();
    private float? _level;
    private bool _outdoors;
    private bool _waves;
    private float _waterZ;

    // The sea grid's indices (static, in an upload heap), and the upload ring of the per-frame data.
    private readonly ID3D12Resource _gridIndices;
    private readonly IndexBufferView _gridIndexView;
    private readonly ID3D12Resource _ring;
    private readonly unsafe byte* _ringMapped;
    private int _ringSlot;

    // The sea swell, by ring index [y * 100 + x] (world-anchored: index(c) = trunc(c / 2) mod 100).
    private readonly float[] _height = new float[SwellCells];
    private readonly float[] _speed = new float[SwellCells];
    private bool _swellStarted;
    private int _swellX0, _swellY0;
    private double? _swellLast;
    private Random _swellRandom = new Random(SwellSeed);

    // The water mask (a byte per texel, row by world y), and the window it was made for.
    private readonly byte[] _mask = new byte[MaskSize * MaskSize];
    private int _maskX0 = int.MinValue, _maskY0 = int.MinValue;

    public unsafe CutsceneWater(D3D12Device device, Format lightFormat, Format depthFormat)
    {
      _device = device;
      var d3d = device.Device;
      // b0: the pass's four constants; b1: the frame's; t0: the wave texture; t3: the noise; t1: the swell's heights;
      // t2: the mask (both in the upload ring).
      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, 4), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)), ShaderVisibility.Pixel),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 3, 0, 0)), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.Pixel),
      };
      // The page's wave texture: LINEAR, REPEAT, one level (the game reads one level: far specks).
      var samplers = new[]
      {
        new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Wrap,
          AddressV = TextureAddressMode.Wrap,
          AddressW = TextureAddressMode.Wrap,
          MaxLOD = float.MaxValue,
        },
      };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers));

      _stepPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSFull"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSStep"),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.Opaque,
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { SimFormat },
        SampleDescription = new SampleDescription(1, 0),
      });
      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 12, 0),
      };
      // The page's water pass: premultiplied (the game's SRCALPHA / INVSRCALPHA), LEQUAL with depth written, no culling.
      _waterPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSWater"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSWater"),
        InputLayout = new InputLayoutDescription(input),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.AlphaBlend,
        DepthStencilState = new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual),
        RenderTargetFormats = new[] { lightFormat },
        DepthStencilFormat = depthFormat,
        SampleDescription = new SampleDescription(1, 0),
      });

      _srvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, DescriptorHeapFlags.ShaderVisible));
      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));
      _srvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      _rtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

      // The wave texture's states, all 128 at the start (cleared on first use).
      var rest = new Vortice.Mathematics.Color4(128 / 255f, 128 / 255f, 128 / 255f, 128 / 255f);
      for (int k = 0; k < 2; k++)
      {
        _sim[k] = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
          ResourceDescription.Texture2D(SimFormat, SimSize, SimSize, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
          ResourceStates.RenderTarget, new ClearValue(SimFormat, rest));
        d3d.CreateRenderTargetView(_sim[k], null, _rtvHeap.GetCPUDescriptorHandleForHeapStart() + k * (int)_rtvSize);
        d3d.CreateShaderResourceView(_sim[k], null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + k * (int)_srvSize);
      }
      // The noise's slot shows the first state until the noise is made (never read before: no steps without it).
      d3d.CreateShaderResourceView(_sim[0], null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 2 * (int)_srvSize);

      // The sea grid's indices: per cell a = j*101+i, b = a+1, c = a+101, d = c+1: (a, b, c), (b, d, c); 16-bit.
      var indices = new ushort[GridIndexCount];
      int n = 0;
      for (int j = 0; j < SwellSize; j++)
      {
        for (int i = 0; i < SwellSize; i++)
        {
          int a = j * GridSide + i, b = a + 1, c = a + GridSide, d = c + 1;
          indices[n++] = (ushort)a;
          indices[n++] = (ushort)b;
          indices[n++] = (ushort)c;
          indices[n++] = (ushort)b;
          indices[n++] = (ushort)d;
          indices[n++] = (ushort)c;
        }
      }
      _gridIndices = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                                 ResourceDescription.Buffer((ulong)GridIndexCount * 2), ResourceStates.GenericRead);
      void* gridMapped;
      _gridIndices.Map(0, null, &gridMapped).CheckError();
      fixed (ushort* source = indices)
      {
        Buffer.MemoryCopy(source, gridMapped, GridIndexCount * 2L, GridIndexCount * 2L);
      }
      _gridIndices.Unmap(0);
      _gridIndexView = new IndexBufferView(_gridIndices.GPUVirtualAddress, (uint)(GridIndexCount * 2), Format.R16_UInt);

      // The per-frame data, a slot per frame in flight.
      _ring = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                          ResourceDescription.Buffer((ulong)RingSlots * SlotBytes), ResourceStates.GenericRead);
      void* mapped;
      _ring.Map(0, null, &mapped).CheckError();
      _ringMapped = (byte*)mapped;
    }

    private static byte[] Compile(DxcShaderStage stage, string entryPoint)
    {
      using IDxcResult result = DxcCompiler.Compile(stage, Shader, entryPoint);
      if (result.GetStatus().Failure)
      {
        throw new InvalidOperationException("HLSL " + entryPoint + ": " + result.GetErrors());
      }
      return result.GetObjectBytecodeArray();
    }

    /// <summary>The water level (the most frequent corner height, to 0.1 m, scene-local), or null without water.</summary>
    public float? Level => _level;

    /// <summary>The wave texture's warm-up steps still to do (a share each frame); 0 once settled.</summary>
    public int WarmUpLeft => _outdoors && _level.HasValue && _waves && _noise != null ? _warmUpLeft : 0;

    /// <summary>
    /// The location's water quads onto the GPU (positions, water.dat corner colours), their copies recorded into
    /// <paramref name="list"/>, the upload buffers added to <paramref name="uploads"/> for the caller to free once the
    /// list is done (nothing without water); the water level, the mask's boxes, and the simulations started afresh (the
    /// wave noise made on first use). Ones before are freed (the GPU done with them first).
    /// </summary>
    public unsafe void SetWater(CutsceneLocation location, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      if (_quadVertices != null || _quadIndices != null)
      {
        _device.WaitIdle();
        _quadVertices?.Dispose();
        _quadIndices?.Dispose();
        _quadVertices = null;
        _quadIndices = null;
      }
      _quadIndexCount = 0;
      _boxes.Clear();
      _level = null;
      _outdoors = location.Outdoors;
      _waves = location.WaterSurface != null;   // the page's waterWaves (the texture itself is never sampled)
      _waterZ = location.WaterOriginZ;
      _swellStarted = false;
      _swellLast = null;
      _swellRandom = new Random(SwellSeed);
      Array.Clear(_height);
      Array.Clear(_speed);
      _maskX0 = _maskY0 = int.MinValue;
      _simCleared = false;
      _simLast = null;
      _warmUpLeft = WarmUpSteps;
      _stepRandom = new Random(StepSeed);

      var water = location.Water;
      if (water.Count < 4)
      {
        return;
      }
      int quads = water.Count / 4;
      var vertices = new float[quads * 4 * QuadVertexFloats];
      var indices = new uint[quads * 6];
      for (int q = 0; q < quads; q++)
      {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int k = 0; k < 4; k++)
        {
          int v = q * 4 + k;
          var p = water[v];
          // The sea's colour 0 0 1 where water.dat has none.
          var c = v < location.WaterColors.Count ? location.WaterColors[v] : new Vector3(0, 0, 1);
          vertices[v * QuadVertexFloats] = p.X;
          vertices[v * QuadVertexFloats + 1] = p.Y;
          vertices[v * QuadVertexFloats + 2] = p.Z;
          vertices[v * QuadVertexFloats + 3] = c.X;
          vertices[v * QuadVertexFloats + 4] = c.Y;
          vertices[v * QuadVertexFloats + 5] = c.Z;
          x0 = Math.Min(x0, p.X);
          y0 = Math.Min(y0, p.Y);
          x1 = Math.Max(x1, p.X);
          y1 = Math.Max(y1, p.Y);
        }
        _boxes.Add((x0, y0, x1, y1));
        // The page's (q, q+1, q+2, q+1, q+3, q+2).
        uint b = (uint)(q * 4);
        indices[q * 6] = b;
        indices[q * 6 + 1] = b + 1;
        indices[q * 6 + 2] = b + 2;
        indices[q * 6 + 3] = b + 1;
        indices[q * 6 + 4] = b + 3;
        indices[q * 6 + 5] = b + 2;
      }
      _quadVertices = Upload(vertices, list, uploads, out ulong vertexBytes);
      _quadIndices = Upload(indices, list, uploads, out ulong indexBytes);
      _quadVertexView = new VertexBufferView(_quadVertices.GPUVirtualAddress, (uint)vertexBytes, QuadVertexFloats * 4);
      _quadIndexView = new IndexBufferView(_quadIndices.GPUVirtualAddress, (uint)indexBytes, Format.R32_UInt);
      _quadIndexCount = (uint)indices.Length;

      // The page's waterLevel: the most frequent corner height rounded to 0.1 m (the first such on a tie).
      _level = water.Take(quads * 4).GroupBy(p => MathF.Round(p.Z * 10) / 10).OrderByDescending(g => g.Count()).First().Key;

      if (_noise == null && _waves)
      {
        _noise = MakeNoise(list, uploads);
      }
    }

    // The wave step's noise: 1024 x 1024 random bytes (the page's Math.random; seeded here), read nearest, wrapped.
    private unsafe ID3D12Resource MakeNoise(ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      var d3d = _device.Device;
      var texture = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(SimFormat, SimSize, SimSize, 1, 1), ResourceStates.CopyDest);
      const int pitch = SimSize * 4;   // a multiple of 256 already
      var bytes = new byte[pitch * SimSize];
      new Random(NoiseSeed).NextBytes(bytes);
      var upload = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                               ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.GenericRead);
      void* mapped;
      upload.Map(0, null, &mapped).CheckError();
      fixed (byte* source = bytes)
      {
        Buffer.MemoryCopy(source, mapped, bytes.Length, bytes.Length);
      }
      upload.Unmap(0);
      var footprint = new PlacedSubresourceFootPrint
      {
        Offset = 0,
        Footprint = new SubresourceFootPrint(SimFormat, SimSize, SimSize, 1, pitch),
      };
      list.CopyTextureRegion(new TextureCopyLocation(texture, 0), 0, 0, 0, new TextureCopyLocation(upload, footprint), null);
      list.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
      uploads.Add(upload);
      d3d.CreateShaderResourceView(texture, null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 2 * (int)_srvSize);
      return texture;
    }

    /// <summary>An array into a new buffer in a default heap, its copy from an upload buffer recorded in the list (as MeshRenderer's).</summary>
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

    // ---- The wave texture (waterTex.fxc, spec 6.1) ----

    // The page's waterSimStep: the warm-up's share, then one step of the time passed (round(ms) x 0.02; 33 ms after a
    // jump back or of 200 ms and more, as the page's; nothing while the time stands: the page keeps moving on the wall
    // clock, here a paused frame stays as it is).
    private unsafe void Simulate(ID3D12GraphicsCommandList list, WaterParams p, double seconds)
    {
      if (!_simCleared)
      {
        for (int k = 0; k < 2; k++)
        {
          if (_simState[k] != ResourceStates.RenderTarget)
          {
            list.ResourceBarrierTransition(_sim[k], _simState[k], ResourceStates.RenderTarget);
            _simState[k] = ResourceStates.RenderTarget;
          }
          list.ClearRenderTargetView(_rtvHeap.GetCPUDescriptorHandleForHeapStart() + k * (int)_rtvSize,
                                     new Vortice.Mathematics.Color4(128 / 255f, 128 / 255f, 128 / 255f, 128 / 255f));
          list.ResourceBarrierTransition(_sim[k], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
          _simState[k] = ResourceStates.PixelShaderResource;
        }
        _simCurrent = 0;
        _simCleared = true;
      }
      list.RSSetViewport(new Vortice.Mathematics.Viewport(SimSize, SimSize));
      list.RSSetScissorRect(SimSize, SimSize);
      list.SetPipelineState(_stepPipeline);
      if (_warmUpLeft > 0)
      {
        int steps = Math.Min(_warmUpLeft, WarmUpPerFrame);
        for (int k = 0; k < steps; k++)
        {
          Step(list, 0.25f, MathF.Round(16) * 0.02f);
        }
        _warmUpLeft -= steps;
        _simLast = seconds;
        return;
      }
      double ms = _simLast is double last ? (seconds - last) * 1000 : 33;
      if (ms < 0 || ms >= 200)
      {
        ms = 33;
      }
      _simLast = seconds;
      float step = (float)Math.Round(ms) * 0.02f;
      if (step > 0)
      {
        // The kick of the wind (0xADC280): (WaterWind x 0.75 + 0.25) x 0.1.
        Step(list, (p.WaterWind * 0.75f + 0.25f) * 0.1f, step);
      }
    }

    // One step: the latest state read, the other drawn, then swapped (both kept readable between steps).
    private unsafe void Step(ID3D12GraphicsCommandList list, float kick, float step)
    {
      int target = 1 - _simCurrent;
      list.ResourceBarrierTransition(_sim[target], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      var constants = stackalloc float[4];
      constants[0] = kick;
      constants[1] = step;
      constants[2] = (float)_stepRandom.NextDouble();
      constants[3] = (float)_stepRandom.NextDouble();
      var gpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();
      list.OMSetRenderTargets(_rtvHeap.GetCPUDescriptorHandleForHeapStart() + target * (int)_rtvSize);
      list.SetGraphicsRoot32BitConstants(0, 4, constants, 0);
      list.SetGraphicsRootDescriptorTable(2, gpu + _simCurrent * (int)_srvSize);
      list.SetGraphicsRootDescriptorTable(3, gpu + 2 * (int)_srvSize);
      list.DrawInstanced(3, 1, 0, 0);
      list.ResourceBarrierTransition(_sim[target], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      _simCurrent = target;
    }

    // ---- The sea swell (CWater::UpdateDynamicWaterTask, spec 6.2), on the CPU ----

    private static int Ring(int c) => ((c / 2 % SwellSize) + SwellSize) % SwellSize;

    private static int EvenDown(float c)
    {
      int t = (int)MathF.Truncate(c) - 100;
      return t - t % 2;
    }

    private void ClearColumn(int ring)
    {
      for (int j = 0; j < SwellSize; j++)
      {
        _height[j * SwellSize + ring] = 0;
        _speed[j * SwellSize + ring] = 0;
      }
    }

    private void ClearRow(int ring)
    {
      Array.Clear(_height, ring * SwellSize, SwellSize);
      Array.Clear(_speed, ring * SwellSize, SwellSize);
    }

    // The window round the mission marker or the eye (the game's player, 0x93F090), moved and stepped. The page's real
    // time is the given time here: dt = the time passed (at most 0.13 s; 1/30 the first time; nothing while it stands
    // or goes back); a new or jumped window starts flat and warms up the cutscene's time so far (at most 30 s) in steps
    // of 0.02 s (the game's grid started flat when the cutscene camera came, 0xAD4E80).
    private void Swell(WaterParams p, Vector3 eye, double seconds)
    {
      var centre = p.SwellAt ?? eye;
      int x0 = EvenDown(centre.X), y0 = EvenDown(centre.Y);
      int warm = 0;
      if (!_swellStarted || Math.Abs(x0 - _swellX0) > 200 || Math.Abs(y0 - _swellY0) > 200)
      {
        Array.Clear(_height);
        Array.Clear(_speed);
        warm = (int)Math.Round(Math.Clamp(seconds, 0, 30) / 0.02);
        _swellStarted = true;
      }
      else
      {
        for (int i = 0; i < SwellSize; i++)
        {
          int x = x0 + i * 2;
          if (x < _swellX0 || x >= _swellX0 + 200)
          {
            ClearColumn(Ring(x));
          }
          int y = y0 + i * 2;
          if (y < _swellY0 || y >= _swellY0 + 200)
          {
            ClearRow(Ring(y));
          }
        }
      }
      _swellX0 = x0;
      _swellY0 = y0;
      // The kicks and the swell of the wind.
      float kick = (p.SwellWind * 0.7f + 0.23f) * 10;
      float swell = p.SwellWind * 0.55f + 0.18f;
      for (int k = 0; k < warm; k++)
      {
        Advance(0.02f, (seconds - (warm - k) * 0.02) * 1000, kick, swell);
      }
      float dt = _swellLast is double last ? (float)Math.Min(seconds - last, 0.13) : 1 / 30f;
      if (dt > 0)
      {
        Advance(dt, seconds * 1000, kick, swell);
      }
      _swellLast = seconds;
    }

    // The page's advance (PC 842-865): random kicks, the rows' sine push, then the springs (the window's edges held to
    // themselves, not wrapped).
    private void Advance(float dt, double ms, float kick, float swell)
    {
      for (int k = 0; k < 2000; k++)
      {
        int at = _swellRandom.Next(SwellCells);
        _speed[at] += (float)(_swellRandom.NextDouble() * 2 - 1) * kick * 0.5f * dt;
      }
      float phase = (float)(ms % 4000 * 2 * Math.PI / 4000);
      int ci = Ring(_swellX0), cj = Ring(_swellY0);
      for (int r = 0; r < SwellSize; r++)
      {
        float push = MathF.Sin((_swellY0 + r * 2) * 0.15f + phase) * swell * dt;
        int row = (cj + r) % SwellSize * SwellSize;
        for (int i = 0; i < SwellSize; i++)
        {
          _speed[row + i] += push;
        }
      }
      float pull = 40 * dt, rest = 0.5f * dt, damp = MathF.Pow(0.7f, dt);
      for (int wj = 0; wj < SwellSize; wj++)
      {
        int jOn = (cj + wj) % SwellSize * SwellSize;
        int jBack = (cj + Math.Max(wj - 1, 0)) % SwellSize * SwellSize;
        int jNext = (cj + Math.Min(wj + 1, SwellSize - 1)) % SwellSize * SwellSize;
        for (int wi = 0; wi < SwellSize; wi++)
        {
          int iOn = (ci + wi) % SwellSize;
          int iBack = (ci + Math.Max(wi - 1, 0)) % SwellSize;
          int iNext = (ci + Math.Min(wi + 1, SwellSize - 1)) % SwellSize;
          int at = jOn + iOn;
          float sides = _height[jOn + iBack] + _height[jOn + iNext] + _height[jBack + iOn] + _height[jNext + iOn];
          float corners = _height[jBack + iBack] + _height[jBack + iNext] + _height[jNext + iBack] + _height[jNext + iNext];
          _speed[at] = ((_speed[at] - (_height[at] - (sides * 0.17f + corners * 0.08f)) * pull) - _height[at] * rest) * damp;
        }
      }
      for (int k = 0; k < SwellCells; k++)
      {
        _height[k] += _speed[k] * dt;
      }
    }

    // The swell faded in over the window's outer 10 m (the code's; its comment says 20 m: spec open question 11).
    private static float SeaEdge(float wx, float wy)
    {
      float m = MathF.Floor(Math.Min(Math.Min(wx, 200 - wx), Math.Min(wy, 200 - wy)) + 0.001f);
      return m < 1 ? 0 : m < 10 ? (m + 1) * 0.1f : 1;
    }

    // ---- The water mask (spec 6.3) ----

    // The page draws the water meshes top-down into a 512 x 512 mask of the window; here each quad's xy box is marked
    // on the CPU (water.dat's quads are boxes in xy; a slanted quad's box is taken whole). Texel (x, y) covers
    // [x0 + 200 x / 512, ...) by world x and y: row by world y, as the shader reads it.
    private void MakeMask(int x0, int y0)
    {
      Array.Clear(_mask);
      const float perMetre = MaskSize / 200f;
      foreach (var b in _boxes)
      {
        int tx0 = Math.Max(0, (int)MathF.Ceiling((b.X0 - x0) * perMetre - 0.5f));
        int tx1 = Math.Min(MaskSize - 1, (int)MathF.Floor((b.X1 - x0) * perMetre - 0.5f));
        int ty0 = Math.Max(0, (int)MathF.Ceiling((b.Y0 - y0) * perMetre - 0.5f));
        int ty1 = Math.Min(MaskSize - 1, (int)MathF.Floor((b.Y1 - y0) * perMetre - 0.5f));
        for (int ty = ty0; ty <= ty1; ty++)
        {
          for (int tx = tx0; tx <= tx1; tx++)
          {
            _mask[ty * MaskSize + tx] = 255;
          }
        }
      }
      _maskX0 = x0;
      _maskY0 = y0;
    }

    /// <summary>
    /// The water into the light target (in RENDER_TARGET state) with the scene's depth (the solid meshes drawn), after
    /// the solid meshes and before the see-through ones; <paramref name="seconds"/>: the time the simulations run by
    /// (the cutscene's; standing still while paused). First the wave texture's step(s) and the swell's, then the flat
    /// quads and (outside, with a water level) the sea grid. Nothing without water. Leaves the light target and the
    /// depth bound, and this class's root signature and descriptor heap set (the see-through pass sets its own again).
    /// </summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, CpuDescriptorHandle lightTarget, CpuDescriptorHandle depth,
                            int width, int height, Matrix4x4 viewProjection, Vector3 eye, FrameLight light, WaterParams p,
                            double seconds)
    {
      if (_quadIndexCount == 0 || _quadVertices == null)
      {
        return;
      }
      // The page's seaOn (outside and a water level): the sea grid, the swell and the mask.
      bool seaOn = _outdoors && _level.HasValue;
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_srvHeap);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      if (seaOn && _waves && _noise != null)
      {
        Simulate(list, p, seconds);
      }
      bool waves = _waves && _simCleared;

      _ringSlot = (_ringSlot + 1) % RingSlots;
      byte* slot = _ringMapped + (long)_ringSlot * SlotBytes;
      ulong gpuSlot = _ring.GPUVirtualAddress + (ulong)((long)_ringSlot * SlotBytes);

      float level = _level ?? 0;
      if (seaOn)
      {
        Swell(p, eye, seconds);
        if (_swellX0 != _maskX0 || _swellY0 != _maskY0)
        {
          MakeMask(_swellX0, _swellY0);
        }
        fixed (byte* mask = _mask)
        {
          Buffer.MemoryCopy(mask, slot + MaskOffset, _mask.Length, _mask.Length);
        }
        fixed (float* heights = _height)
        {
          Buffer.MemoryCopy(heights, slot + SwellOffset, SwellBytes, SwellBytes);
        }
        // The grid's points (the page's VS: the window's corner + (i, j) x 2 m, at the level + the swell x its edge),
        // each with the sea's colour 0 0 1 (the game's; the page's grid has no colour attribute: spec open question 9).
        var g = (float*)(slot + GridOffset);
        int ci = Ring(_swellX0), cj = Ring(_swellY0);
        int k = 0;
        for (int j = 0; j < GridSide; j++)
        {
          int row = (cj + j) % SwellSize * SwellSize;
          for (int i = 0; i < GridSide; i++)
          {
            float h = _height[row + (ci + i) % SwellSize];
            g[k++] = _swellX0 + i * 2;
            g[k++] = _swellY0 + j * 2;
            g[k++] = level + h * SeaEdge(i * 2, j * 2);
            g[k++] = 0;
            g[k++] = 0;
            g[k++] = 1;
          }
        }
      }

      // The frame's constants (the layout of the shader's Water; row_major: System.Numerics' matrix as it is).
      var f = (float*)slot;
      *(Matrix4x4*)f = viewProjection;
      // The view's forward: the direction clip w grows in (w = x M14 + y M24 + z M34 + M44).
      var forward = new Vector3(viewProjection.M14, viewProjection.M24, viewProjection.M34);
      forward = forward.LengthSquared() > 0 ? Vector3.Normalize(forward) : new Vector3(0, 1, 0);
      var sky = p.Sky;
      Put(f, 16, eye, _waterZ);
      Put(f, 20, Vector3.Normalize(light.SunDirection), p.ShineScale);
      Put(f, 24, light.Sun, p.Reflection);
      Put(f, 28, light.AmbientAbove, p.WaterAlpha);
      Put(f, 32, light.AmbientBelow, level);
      Put(f, 36, p.WaterColor, seaOn ? _swellX0 : 0);
      Put(f, 40, forward, seaOn ? _swellY0 : 0);
      Put(f, 44, p.FogNear, p.Exposure);
      Put(f, 48, p.FogFar, 0);
      Put(f, 52, new Vector3(p.FogStart, p.FogEnd, p.NearFogAmount), 0);
      Put(f, 56, new Vector3(p.DepthFx.X, p.DepthFx.Y, p.DepthFx.Z), p.DepthFx.W);
      Put(f, 60, sky.SunDirection, sky.HdrExposure > 0 ? 1 : 0);
      Put(f, 64, sky.SkyColor, sky.AzimuthHeight);
      Put(f, 68, sky.AzimuthColor, sky.AzimuthStrength);
      Put(f, 72, sky.AzimuthColorEast, sky.PageExposure);
      Put(f, 76, sky.SunColor, 0);
      Put(f, 80, new Vector3(sky.HdrExposure, sky.HdrSunExposure.X, sky.HdrSunExposure.Y), sky.HdrClamp);

      var gpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();
      list.OMSetRenderTargets(lightTarget, depth);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetPipelineState(_waterPipeline);
      list.SetGraphicsRootConstantBufferView(1, gpuSlot);
      list.SetGraphicsRootDescriptorTable(2, gpu + _simCurrent * (int)_srvSize);
      list.SetGraphicsRootDescriptorTable(3, gpu + 2 * (int)_srvSize);
      list.SetGraphicsRootShaderResourceView(4, gpuSlot + SwellOffset);
      list.SetGraphicsRootShaderResourceView(5, gpuSlot + MaskOffset);

      var constants = stackalloc float[4];
      constants[0] = 0;
      constants[1] = seaOn ? 1 : 0;
      constants[2] = waves ? 1 : 0;
      constants[3] = 0;
      list.SetGraphicsRoot32BitConstants(0, 4, constants, 0);
      list.IASetVertexBuffers(0, _quadVertexView);
      list.IASetIndexBuffer(_quadIndexView);
      list.DrawIndexedInstanced(_quadIndexCount, 1, 0, 0, 0);

      // The sea grid with the same states (the page draws it right after each water mesh).
      if (seaOn)
      {
        constants[0] = 1;
        list.SetGraphicsRoot32BitConstants(0, 4, constants, 0);
        list.IASetVertexBuffers(0, new VertexBufferView(gpuSlot + GridOffset, (uint)GridBytes, GridVertexFloats * 4));
        list.IASetIndexBuffer(_gridIndexView);
        list.DrawIndexedInstanced(GridIndexCount, 1, 0, 0, 0);
      }
    }

    private static unsafe void Put(float* f, int at, Vector3 v, float w)
    {
      f[at] = v.X;
      f[at + 1] = v.Y;
      f[at + 2] = v.Z;
      f[at + 3] = w;
    }

    public void Dispose()
    {
      _device.WaitIdle();
      _quadVertices?.Dispose();
      _quadIndices?.Dispose();
      _noise?.Dispose();
      _sim[0].Dispose();
      _sim[1].Dispose();
      _gridIndices.Dispose();
      _ring.Unmap(0);
      _ring.Dispose();
      _srvHeap.Dispose();
      _rtvHeap.Dispose();
      _stepPipeline.Dispose();
      _waterPipeline.Dispose();
      _rootSignature.Dispose();
    }
  }
}
