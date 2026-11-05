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
  /// The sun's glint and light x its shadow (the page's waterShadow) from the map MeshRenderer drew this frame.
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
  /// - the stencil "no water inside boats" (bucket 30 planes).
  /// - the page's haze edge (a page device) and its culling.
  ///
  /// Deterministic, unlike the page (spec open question 13): the water at a section time is a pure function of that
  /// time (the same whether the frame is drawn alone or after others). Wall-clock time is replaced by the section's
  /// time, stepped at fixed rates from a warmed-up base (the wave texture: 60 steps a second; the swell: 50, from flat),
  /// Math.random by a seeded System.Random (the wave noise) and per-step numbers from the step's index (SplitMix64: the
  /// wave step's shift, the swell's kicks).
  /// </summary>
  public sealed class CutsceneWater : IDisposable
  {
    // The wave texture (waterTex.fxc): 128 << water quality, 1024 in GTAIV.1; RGBA8 ping-ponged.
    private const int SimSize = 1024;
    private const Format SimFormat = Format.R8G8B8A8_UNorm;
    // The page's warm-up: 20000 steps of 16 ms with kick 0.25 [PAGE, tuned to the trace's spread at bc_3 s1 f79]. Made
    // once (the same for every location: the noise is seeded) and kept as the base every section time starts from.
    private const int WarmUpSteps = 20000;
    private const float WarmUpKick = 0.25f;
    private static readonly float WarmUpStep = MathF.Round(16) * 0.02f;
    // At most this many steps (warm-up and catch-up together) in one list: more could run the GPU past its timeout.
    private const int StepsPerList = 2000;
    // The playback after the base: a step every 1/60 s of the section's time (the page's 60 a second: round(16.67 ms)
    // x 0.02), at most the last SimCap of them (5 minutes; older ones no longer show after the damping).
    private const double SimRate = 60.0;
    private static readonly float SimStep = MathF.Round(1000f / 60) * 0.02f;
    private const int SimCap = 18000;
    // A small step back while playing (the sound's sync nudging the clock) is held, not recomputed.
    private const int HoldSteps = 12;
    // The sea swell: a step of 0.02 s from the section's start, at most the last 1500 (30 s, as the page's warm-up).
    private const double SwellStepSeconds = 0.02;
    private const int SwellCap = 1500;

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
    private const ulong WarmSeed = 0x5EA2;
    private const ulong SwellSeed = 0x5EA3;
    private const ulong PlaySeed = 0x5EA4;

    // A counter-based random number (SplitMix64 of the seed and the step's index): the same for a step however it is
    // reached, so a frame drawn alone and one drawn after others hold the same water.
    private static ulong Mix(ulong seed, long k)
    {
      ulong z = (seed << 32) ^ (ulong)k;
      z += 0x9E3779B97F4A7C15;
      z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
      z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
      return z ^ (z >> 31);
    }

    // A wave step's shift of its noise lookup, two numbers in [0, 1) (24 bits each).
    private static (float X, float Y) Shift(ulong seed, long k)
    {
      ulong z = Mix(seed, k);
      return ((z >> 40) / 16777216f, ((z >> 16) & 0xFFFFFF) / 16777216f);
    }

    // Each frame's slot of the upload ring: the constants, the grid's vertices, the swell's heights, the mask.
    private const int ConstantBytes = 768;
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
  // The sun's shadow as MeshRenderer reads it (FrameLight.SunShadow): u (w: the fade's scale), v, h (w: the heights'
  // reference), the view's forward (w: 1 with a map), the cascades' splits (w: the fade's end), each cascade's box.
  float4 SunU;
  float4 SunV;
  float4 SunH;
  float4 SunForward;
  float4 SunSplits;
  float4 SunBox[4];
  float4 Mirror;       // xy: the picture's size in pixels, z: the mirror drawn this frame (1), w: the scene's depth behind (1)
  row_major float4x4 InverseViewProjection;
};
ConstantBuffer<Water> w : register(b1);
Texture2D<float> SunMap : register(t4);   // the sun's shadow map (MeshRenderer.SunShadowMap)
Texture2D MirrorTex : register(t5);       // the mirrored view's light (premultiplied, alpha what was drawn)
Texture2D<float> Behind : register(t6);   // the scene's depth before the water (a copy)
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

// The page's gameSunShadow (gtaiv-pc.js) as MeshRenderer's SunShadow reads the same map: the cascade by the depth along
// the view, twelve taps 1 / 2048 of the map apart, lit where the point is at least as high along the sun as what is
// stored, + 1.5 (distance / 175)², none from 175 m; the page's GL rows turned over.
static const float2 SunTaps[12] = { float2(-0.326212, -0.40581), float2(-0.840144, -0.07358), float2(-0.695914, 0.457137),
  float2(-0.203345, 0.620716), float2(0.96234, -0.194983), float2(0.473434, -0.480026), float2(0.519456, 0.767022),
  float2(0.185461, -0.893124), float2(0.507431, 0.064425), float2(0.89642, 0.412458), float2(-0.32194, -0.932615), float2(-0.791559, -0.59771) };

float SunShadow(float3 P)
{
  if (w.SunForward.w < 0.5)
  {
    return 1;
  }
  float dist = length(P - w.Eye.xyz);
  if (dist >= w.SunSplits.w)
  {
    return 1;
  }
  float depth = dot(P - w.Eye.xyz, w.SunForward.xyz);
  int k = (depth >= w.SunSplits.x ? 1 : 0) + (depth >= w.SunSplits.y ? 1 : 0) + (depth >= w.SunSplits.z ? 1 : 0);
  float4 box = w.SunBox[k];
  float2 at = float2((dot(P, w.SunU.xyz) - box.x) * box.z, (dot(P, w.SunV.xyz) - box.y) * box.w) * 0.5 + 0.5;
  float up0 = dot(P, w.SunH.xyz) - w.SunH.w;
  float lit = 0;
  [unroll] for (int s = 0; s < 12; s++)
  {
    float2 t = at + SunTaps[s] / 2048;
    int x = clamp((int)floor((k + t.x) * 2048), 0, 8191);
    int y = clamp((int)floor(t.y * 2048), 0, 2047);
    lit += up0 >= SunMap.Load(int3(x, 2047 - y, 0)) ? 1 : 0;
  }
  float away = dist / w.SunSplits.w;
  return saturate(lit / 12 + w.SunU.w * away * away);
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

  // How much water lies in front of what is behind it (the page's waterBehind, gtaiv-pc.js ~531-537): the view depth of
  // the scene's point at the pixel (its depth copied before the water, taken back through the inverse view-projection)
  // less the water's; 1 where the scene is nearer, and 1 m without the copy.
  float viewDepth = max(dot(p - eye, w.Forward.xyz), 1e-3);    // clip w, not distance
  float behindDepth = 1;
  if (w.Mirror.w > 0.5)
  {
    float depthBehind = Behind.Load(int3(i.position.xy, 0));
    float2 ndc = float2(i.position.x / w.Mirror.x * 2 - 1, 1 - i.position.y / w.Mirror.y * 2);
    float4 q = mul(float4(ndc, depthBehind, 1), w.InverseViewProjection);
    behindDepth = dot(q.xyz / q.w - eye, w.Forward.xyz) - dot(p - eye, w.Forward.xyz);
    if (behindDepth < 0)
    {
      behindDepth = 1;
    }
  }
  float shore = 1 - saturate(behindDepth / (saturate(pow(viewDepth, 1.125) * 0.02) + 0.05));
  shore *= shore;
  float3 gentleNormal = waves
    ? normalize(float3(lightNormal.xy / max(lightNormal.z, 1e-4) - 0.5 * view.xy * shore, 1.00001 - 0.5 * view.z * shore))
    : lightNormal;
  float cosine = max(-dot(view, gentleNormal), 0);  // the game's cmp: back-facing -> 0 -> Fresnel 0.8
  float fresnel = pow(1 - cosine, 5) * 0.6 + 0.2;
  float3 bounce = reflect(view, mirrorNormal);

  // The mirror (the page's, gtaiv-pc.js reflectOn / reflectLight): what the mirrored view drew (see BeginMirror) where it
  // is, shifted by the waves as water.fxc does - their slope turned to the picture (across, up) x 0.5 / (0.03 x the
  // view depth), at most twice - and kept on the water's side of the horizon (the point 2000 m ahead, level, on the
  // water's plane: v at least 0.5 (1 - its y) + 0.05 x near + 0.005, Direct3D's v down); the sky in the bounced
  // direction where nothing was drawn (or without a mirror).
  float3 mirroredLight = SkyLight(normalize(bounce));
  if (w.Mirror.z > 0.5)
  {
    float3 across = normalize(cross(view, float3(0, 0, 1)) + 1e-5);
    float2 ahead = normalize(view.xy + 1e-5);
    float shift = min(0.5 / (0.03 * viewDepth), 2.0);
    float2 at = i.position.xy / w.Mirror.xy + float2(dot(gentleNormal.xy, across.xy), -dot(gentleNormal.xy, ahead)) * shift;
    float2 ahead2 = normalize(w.Forward.xy + 1e-5);
    float4 farAt = mul(float4(eye.xy + ahead2 * 2000, p.z, 1), w.ViewProjection);
    if (farAt.w > 0)
    {
      at.y = max(at.y, 0.5 - 0.5 * farAt.y / farAt.w + 0.05 * max(1 - dot(eye - p, eye - p) * 0.0004, 0) + 0.005);
    }
    float4 scene = MirrorTex.SampleLevel(Linear, clamp(at, 0.001, 0.999), 0);
    mirroredLight = mirroredLight * (1 - saturate(scene.a)) + scene.rgb;
  }

  float3 sunDir = w.SunDirection.xyz;
  float3 sunColor = w.Sun.rgb;
  // The sun's shadow where water.fxc reads it (the page's waterShadow): at the point a metre up, that metre x
  // (1 - 0.0004 d²) (its r0.z), in the world's heights (the scene's z + WaterOriginZ).
  float shadowNear = max(1 - liftedD2 * 0.0004, 0);
  float waterShadow = SunShadow(float3(lifted.xy, (lifted.z + w.Eye.w) * shadowNear - w.Eye.w));
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
  // See-through by how much water lies in front (the page's, gtaiv-pc.js ~606: water.fxc's alpha, blended over the lit
  // scene): saturate((d x waterColour.w x 0.25)^0.25 + 0.125); opaque without the scene's depth.
  float alpha = w.Mirror.w > 0.5 ? saturate(pow(max(behindDepth * w.AmbientAbove.w * 0.25, 0), 0.25) + 0.125) : 1;
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
    private int _warmUpLeft = WarmUpSteps;
    // The warmed-up base (kept in COPY_SOURCE once made), and the playback the latest state holds: its steps done
    // (base + steps [_simStart, _simN); -1: none yet), and the wind they were made with.
    private readonly ID3D12Resource _simBase;
    private ResourceStates _baseState = ResourceStates.CopyDest;
    private bool _baseReady;
    private int _simN = -1;
    private int _simStart;
    private float _simWind;
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
    // The swell's steps done (steps [_swellStart, _swellM) from flat; -1: none yet).
    private int _swellM = -1;
    private int _swellStart;

    // The water mask (a byte per texel, row by world y), and the window it was made for.
    private readonly byte[] _mask = new byte[MaskSize * MaskSize];
    private int _maskX0 = int.MinValue, _maskY0 = int.MinValue;
    // The sun's shadow map whose view is in the heap's fourth slot.
    private ID3D12Resource? _sunMapSeen;

    // The mirror (the page's reflectionTarget: half the picture's size, the light's format): drawn into with
    // MeshRenderer.MirrorSamples samples a pixel (its own depth), resolved into the texture the water reads; that one's
    // state, and whether it was drawn for the coming Draw.
    private readonly Format _lightFormat;
    private readonly Format _depthFormat;
    private ID3D12Resource? _mirror;
    private ID3D12Resource? _mirrorSamples;
    private ID3D12Resource? _mirrorDepth;
    private readonly ID3D12DescriptorHeap _mirrorDsv;
    private int _mirrorWidth, _mirrorHeight;
    private ResourceStates _mirrorState;
    private bool _mirrorDrawn;
    // The scene's depth before the water (a copy of the depth buffer, the page's gameKeepSolidDepth), its size.
    private ID3D12Resource? _behind;
    private int _behindWidth, _behindHeight;

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
        // t4: the sun's shadow map (its view in the heap's fourth slot).
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 4, 0, 0)), ShaderVisibility.Pixel),
        // t5: the mirror (its view in the heap's fifth slot).
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 5, 0, 0)), ShaderVisibility.Pixel),
        // t6: the scene's depth before the water (its view in the heap's sixth slot).
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 6, 0, 0)), ShaderVisibility.Pixel),
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

      _lightFormat = lightFormat;
      _depthFormat = depthFormat;
      _mirrorDsv = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
      _srvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, DescriptorHeapFlags.ShaderVisible));
      // (The third render target view: the mirror's.)
      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 3));
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
      // The warmed-up base: copied from and to the states, never drawn into.
      _simBase = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(SimFormat, SimSize, SimSize, 1, 1), ResourceStates.CopyDest);
      // The noise's slot shows the first state until the noise is made (never read before: no steps without it).
      d3d.CreateShaderResourceView(_sim[0], null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 2 * (int)_srvSize);
      // The sun's shadow map's slot: empty until one is given (never read without: SunForward.w 0).
      d3d.CreateShaderResourceView(null, new ShaderResourceViewDescription
      {
        Format = Format.R16_Float,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
      }, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 3 * (int)_srvSize);
      // The mirror's slot: empty until it is made (never read before: Mirror.z 0).
      d3d.CreateShaderResourceView(null, new ShaderResourceViewDescription
      {
        Format = lightFormat,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
      }, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 4 * (int)_srvSize);
      // The depth copy's slot: empty until it is made (never read before: Mirror.w 0).
      d3d.CreateShaderResourceView(null, new ShaderResourceViewDescription
      {
        Format = Format.R32_Float,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
      }, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 5 * (int)_srvSize);

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
      // The noise's bytes and the cached base read here, on the thread the water is made on (the player's: another one).
      _cachedBase = new Lazy<byte[]?>(ReadCachedBase);
      _ = _noiseBytes.Value;
      _ = _cachedBase.Value;
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

    /// <summary>The device it draws with.</summary>
    public D3D12Device Device => _device;

    // Whether Draw runs the wave texture's simulation (water quads, outside, a water level, waves, the noise made).
    private bool SimActive => _quadIndexCount > 0 && _quadVertices != null && _outdoors && _level.HasValue && _waves && _noise != null;

    /// <summary>
    /// The wave texture's steps still to do before it holds its state at <paramref name="seconds"/> of the section (the
    /// base's warm-up left and the playback's catch-up; at most <c>StepsPerList</c> are done per Draw); 0 where Draw
    /// runs no simulation (inside, no waves). Taken with the wind of the Draw before, as Draw with
    /// <paramref name="exact"/> takes it (not exact: a small step back held, a run past the cap continued).
    /// </summary>
    public int StepsLeft(double seconds, bool exact = true)
    {
      if (!SimActive)
      {
        return 0;
      }
      int warm = _baseReady ? 0 : _warmUpLeft;
      int n = PlaySteps(seconds), s = Math.Max(0, n - SimCap);
      bool same = _baseReady && _simN >= 0;
      if (same && n >= _simN && (s == _simStart || !exact))
      {
        return warm + n - _simN;
      }
      if (same && !exact && n < _simN && _simN - n <= HoldSteps)
      {
        return warm;
      }
      return warm + n - s;
    }

    private static int PlaySteps(double seconds) => (int)Math.Floor(Math.Max(seconds, 0) * SimRate + 1e-6);

    /// <summary>
    /// The location's water quads onto the GPU (positions, water.dat corner colours), their copies recorded into
    /// <paramref name="list"/>, the upload buffers added to <paramref name="uploads"/> for the caller to free once the
    /// list is done (nothing without water); the water level, the mask's boxes, and the simulations started afresh (the
    /// wave noise made on first use; the wave texture's warmed-up base kept). Ones before are freed (the GPU done with
    /// them first).
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
      Take(MakeQuads(location, list, uploads));
      if (_noise == null && _waves)
      {
        _noise = MakeNoise(list, uploads);
      }
    }

    /// <summary>A location's water made on the GPU ahead (see <see cref="Prepare"/>), taken by <see cref="Use"/>.</summary>
    public sealed class Prepared : IDisposable
    {
      /// <summary>Its buffers freed (one never used; the GPU done with it).</summary>
      public void Dispose()
      {
        Vertices?.Dispose();
        Indices?.Dispose();
        Vertices = Indices = null;
      }

      internal ID3D12Resource? Vertices, Indices;
      internal VertexBufferView VertexView;
      internal IndexBufferView IndexView;
      internal uint IndexCount;
      internal List<(float X0, float Y0, float X1, float Y1)> Boxes = new List<(float, float, float, float)>();
      internal float? Level;
      internal bool Outdoors, Waves;
      internal float WaterZ;
    }

    /// <summary>
    /// A location's water quads made on the GPU from any thread (a loading one: copied on the copy queue and waited for
    /// there), for <see cref="Use"/> on the drawing thread: nothing is waited for when the location is shown.
    /// </summary>
    public Prepared Prepare(CutsceneLocation location)
    {
      var d3d = _device.Device;
      using var allocator = d3d.CreateCommandAllocator(CommandListType.Copy);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Copy, allocator, null);
      var uploads = new List<ID3D12Resource>();
      var prepared = MakeQuads(location, list, uploads);
      list.Close();
      _device.CopyAndWait(list);
      foreach (var u in uploads)
      {
        u.Dispose();
      }
      return prepared;
    }

    /// <summary>
    /// The drawing thread: a prepared water shown from now on (the simulations started afresh, as by SetWater); the
    /// quads before freed once the GPU is past them (D3D12Device.Retire, not waited for).
    /// </summary>
    public void Use(Prepared water)
    {
      var oldVertices = _quadVertices;
      var oldIndices = _quadIndices;
      if (oldVertices != null || oldIndices != null)
      {
        _device.Retire(() =>
        {
          oldVertices?.Dispose();
          oldIndices?.Dispose();
        });
      }
      Take(water);
    }

    // The quads' buffers (copies recorded into the list) and the water's figures.
    private Prepared MakeQuads(CutsceneLocation location, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      var made = new Prepared
      {
        Outdoors = location.Outdoors,
        Waves = location.WaterSurface != null,   // the page's waterWaves (the texture itself is never sampled)
        WaterZ = location.WaterOriginZ,
      };
      var water = location.Water;
      if (water.Count < 4)
      {
        return made;
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
        made.Boxes.Add((x0, y0, x1, y1));
        // The page's (q, q+1, q+2, q+1, q+3, q+2).
        uint b = (uint)(q * 4);
        indices[q * 6] = b;
        indices[q * 6 + 1] = b + 1;
        indices[q * 6 + 2] = b + 2;
        indices[q * 6 + 3] = b + 1;
        indices[q * 6 + 4] = b + 3;
        indices[q * 6 + 5] = b + 2;
      }
      made.Vertices = Upload(vertices, list, uploads, out ulong vertexBytes);
      made.Indices = Upload(indices, list, uploads, out ulong indexBytes);
      made.VertexView = new VertexBufferView(made.Vertices.GPUVirtualAddress, (uint)vertexBytes, QuadVertexFloats * 4);
      made.IndexView = new IndexBufferView(made.Indices.GPUVirtualAddress, (uint)indexBytes, Format.R32_UInt);
      made.IndexCount = (uint)indices.Length;
      // The page's waterLevel: the most frequent corner height rounded to 0.1 m (the first such on a tie).
      made.Level = water.Take(quads * 4).GroupBy(p => MathF.Round(p.Z * 10) / 10).OrderByDescending(g => g.Count()).First().Key;
      return made;
    }

    // A made water taken (the drawing thread): its quads and figures, the simulations started afresh (the wave noise and
    // the warmed-up base stay: the same for every location).
    private void Take(Prepared water)
    {
      _quadVertices = water.Vertices;
      _quadIndices = water.Indices;
      _quadVertexView = water.VertexView;
      _quadIndexView = water.IndexView;
      _quadIndexCount = water.IndexCount;
      _boxes.Clear();
      _boxes.AddRange(water.Boxes);
      _level = water.Level;
      _outdoors = water.Outdoors;
      _waves = water.Waves;
      _waterZ = water.WaterZ;
      _swellStarted = false;
      _swellM = -1;
      Array.Clear(_height);
      Array.Clear(_speed);
      _maskX0 = _maskY0 = int.MinValue;
      // The playback starts again from the base (which, as the noise, stays: the same for every location).
      _simN = -1;
      _advanced = null;
    }
    // The noise's bytes (made on first use: the water's constructor touches them on the thread it is made on).
    private readonly Lazy<byte[]> _noiseBytes = new Lazy<byte[]>(() =>
    {
      var bytes = new byte[SimSize * 4 * SimSize];
      new Random(NoiseSeed).NextBytes(bytes);
      return bytes;
    });

    // The wave step's noise: 1024 x 1024 random bytes (the page's Math.random; seeded here), read nearest, wrapped.
    private unsafe ID3D12Resource MakeNoise(ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      var d3d = _device.Device;
      var texture = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(SimFormat, SimSize, SimSize, 1, 1), ResourceStates.CopyDest);
      const int pitch = SimSize * 4;   // a multiple of 256 already
      // (The bytes made ahead with the water, on its thread: NoiseBytes.)
      var bytes = _noiseBytes.Value;
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
      // (On the copy queue left to decay to COMMON: the direct queue promotes it.)
      if (list.CommandListType != CommandListType.Copy)
      {
        list.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.VertexAndConstantBuffer | ResourceStates.IndexBuffer);
      }
      return buffer;
    }

    // ---- The wave texture (waterTex.fxc, spec 6.1) ----

    // The page's waterSimStep, made a pure function of the section's time: the state at t is the warmed-up base (made
    // once, a share per list) + floor(t x 60) steps of 1/60 s from the section's start (at most the last SimCap), each
    // step's noise shift by its index. Continued step by step where it can (the same as computed afresh), else the
    // base restored and stepped again; at most StepsPerList steps per list (the rest in the lists after). Not exact
    // (while playing): a small step back is held, and a run past the cap goes on instead of starting again.
    private unsafe void Simulate(ID3D12GraphicsCommandList list, WaterParams p, double seconds, bool exact, int budget = StepsPerList)
    {
      if (!_baseReady && !_simCleared)
      {
        ClearSims(list);
      }
      list.RSSetViewport(new Vortice.Mathematics.Viewport(SimSize, SimSize));
      list.RSSetScissorRect(SimSize, SimSize);
      list.SetPipelineState(_stepPipeline);
      if (!_baseReady)
      {
        int steps = Math.Min(_warmUpLeft, budget);
        for (int k = 0; k < steps; k++)
        {
          Step(list, WarmUpKick, WarmUpStep, Shift(WarmSeed, WarmUpSteps - _warmUpLeft + k));
        }
        _warmUpLeft -= steps;
        budget -= steps;
        if (_warmUpLeft > 0)
        {
          return;
        }
        CopySimToBase(list);
        SaveBase(list);
        _baseReady = true;
        _simN = 0;
        _simStart = 0;
        _simWind = p.WaterWind;
        if (budget == 0)
        {
          return;
        }
      }
      float wind = p.WaterWind;
      int n = PlaySteps(seconds), s = Math.Max(0, n - SimCap);
      bool same = _simN >= 0 && _simWind == wind;
      if (same && n >= _simN && s == _simStart)
      {
        // Going on from the state held: the same steps as from the base.
      }
      else if (same && !exact && n < _simN && _simN - n <= HoldSteps)
      {
        // A small step back while playing: held.
        return;
      }
      else if (same && !exact && n >= _simN)
      {
        // Playing on past the cap.
        _simStart = s;
      }
      else
      {
        RestoreBaseToSim(list);
        _simN = s;
        _simStart = s;
        _simWind = wind;
      }
      int todo = Math.Min(n - _simN, budget);
      // The kick of the wind (0xADC280): (WaterWind x 0.75 + 0.25) x 0.1.
      float kick = (wind * 0.75f + 0.25f) * 0.1f;
      for (int k = 0; k < todo; k++)
      {
        Step(list, kick, SimStep, Shift(PlaySeed, _simN + k));
      }
      _simN += todo;
    }

    // Both states cleared to 128 (the page's start), left PIXEL_SHADER_RESOURCE.
    private void ClearSims(ID3D12GraphicsCommandList list)
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

    // ---- Ahead of the drawing (the app's player) ----

    // While playing, at most this many of the base's warm-up steps a frame (about 3 ms on an RTX 2060), so no frame waits
    // on the 20000; the playback's steps (one a frame) not limited further. Drawn by a capture (exact) as before.
    private const int PlayingWarmUpSteps = 32;
    // While playing, at most this many catch-up steps a frame (after a jump; a step a frame otherwise): with the copy
    // engine busy uploading a location a step took up to 0.5 ms instead of 0.07.
    private const int PlayingSteps = 16;
    // A stopped frame's (exact) wave steps a frame in the app (~28 ms): a seek takes at most a hitch, the rest after.
    private const int StillSteps = 400;
    // The swell's steps a frame in the app: playing (~5 ms on the CPU), stopped; a step back held while playing.
    private const int PlayingSwellSteps = 60;
    private const int StillSwellSteps = 300;
    private const int SwellHoldSteps = 25;

    // The section's time and exactness the simulations were last brought to by Advance (Draw then leaves them).
    private (double Seconds, bool Exact)? _advanced;

    /// <summary>
    /// The simulations brought to <paramref name="seconds"/> of the section ahead of the drawing (the drawing thread,
    /// into the frame's list, every frame from the section's start - also while the location isn't shown yet - so they
    /// go on a step a frame instead of catching up seconds at once when the water first shows): the wave noise made on
    /// first use, the warmed-up base read from the disk cache or warmed up (while playing <c>PlayingWarmUpSteps</c> a
    /// frame), the wave texture's steps, the swell. The state at a time is the same as Draw alone makes it.
    /// </summary>
    public void Advance(ID3D12GraphicsCommandList list, WaterParams p, double seconds, Vector3 eye, bool exact, int width, int height)
    {
      // (Draw leaves the simulations to this call this frame, also when it does only one of the things ahead below.)
      _advanced = (seconds, exact);
      PollBaseCache();
      if (_quadIndexCount == 0 || _quadVertices == null)
      {
        return;
      }
      // The targets, the noise and the cached base each in a frame of their own first.
      if (MakeTargets(width, height))
      {
        return;
      }
      if (_waves && _noise == null)
      {
        var uploads = new List<ID3D12Resource>();
        _noise = MakeNoise(list, uploads);
        _device.Retire(() => uploads.ForEach(u => u.Dispose()));
        return;
      }
      if (!_baseReady && !_baseTried)
      {
        LoadBase(list);
        return;
      }
      var profiler = _device.Profiler;
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_srvHeap);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      if (SimActive)
      {
        profiler?.Begin(list, "water: sim");
        Simulate(list, p, seconds, exact, exact ? StillSteps : _baseReady ? PlayingSteps : PlayingWarmUpSteps);
        profiler?.End(list, "water: sim");
      }
      if (_outdoors && _level.HasValue)
      {
        Swell(p, eye, seconds, exact, exact ? StillSwellSteps : PlayingSwellSteps);
      }
    }

    // The warmed-up base on disk (it is the same for every location and every run: seeded noise, fixed steps): read
    // instead of the 20000 steps, written once made. Keyed by the adapter, the shader and the warm-up's numbers.
    // SPARKIV_D3D12_WATERCACHE=0: neither.
    private static readonly bool s_baseCacheOn = Environment.GetEnvironmentVariable("SPARKIV_D3D12_WATERCACHE") != "0";
    private bool _baseTried;
    private ID3D12Resource? _baseReadback;
    private ulong _baseReadbackMark;

    private string BaseCachePath()
    {
      var key = System.Text.Encoding.UTF8.GetBytes(string.Join("|", _device.AdapterName, Shader, SimSize, WarmUpSteps, WarmUpKick, WarmUpStep, WarmSeed, NoiseSeed));
      string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key)).Substring(0, 16).ToLowerInvariant();
      return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkIV", "D3D12", "water-base-" + hash + ".bin");
    }

    // The cache's bytes, read once (null: none).
    private Lazy<byte[]?> _cachedBase = null!;

    private byte[]? ReadCachedBase()
    {
      try
      {
        string path = BaseCachePath();
        return s_baseCacheOn && System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
      }
      catch (Exception)
      {
        return null;
      }
    }

    // The base from the cache into the list (made ready at once), when there.
    private unsafe void LoadBase(ID3D12GraphicsCommandList list)
    {
      _baseTried = true;
      if (!s_baseCacheOn)
      {
        return;
      }
      // (Read ahead on the water's own thread: CachedBaseBytes.)
      if (_cachedBase.Value is not byte[] bytes)
      {
        return;
      }
      const int pitch = SimSize * 4;
      if (bytes.Length != pitch * SimSize)
      {
        return;
      }
      var upload = _device.Device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                                          ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.GenericRead);
      void* mapped;
      upload.Map(0, null, &mapped).CheckError();
      fixed (byte* source = bytes)
      {
        Buffer.MemoryCopy(source, mapped, bytes.Length, bytes.Length);
      }
      upload.Unmap(0);
      if (_baseState != ResourceStates.CopyDest)
      {
        list.ResourceBarrierTransition(_simBase, _baseState, ResourceStates.CopyDest);
      }
      var footprint = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(SimFormat, SimSize, SimSize, 1, pitch) };
      list.CopyTextureRegion(new TextureCopyLocation(_simBase, 0), 0, 0, 0, new TextureCopyLocation(upload, footprint), null);
      list.ResourceBarrierTransition(_simBase, ResourceStates.CopyDest, ResourceStates.CopySource);
      _baseState = ResourceStates.CopySource;
      _device.Retire(upload.Dispose);
      if (!_simCleared)
      {
        ClearSims(list);
      }
      _warmUpLeft = 0;
      _baseReady = true;
      _simN = -1;
    }

    // The base just made copied for the cache (read once the GPU is done: PollBaseCache).
    private void SaveBase(ID3D12GraphicsCommandList list)
    {
      if (!s_baseCacheOn || _baseReadback != null)
      {
        return;
      }
      const int pitch = SimSize * 4;
      _baseReadback = _device.Device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
                                                             ResourceDescription.Buffer((ulong)pitch * SimSize), ResourceStates.CopyDest);
      var footprint = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(SimFormat, SimSize, SimSize, 1, pitch) };
      list.CopyTextureRegion(new TextureCopyLocation(_baseReadback, footprint), 0, 0, 0, new TextureCopyLocation(_simBase, 0), null);
      _baseReadbackMark = 0;
    }

    // At the next call (the list with the copy sent by then): a mark; once the GPU is past it, the file written (another thread).
    private unsafe void PollBaseCache()
    {
      if (_baseReadback == null)
      {
        return;
      }
      if (_baseReadbackMark == 0)
      {
        _baseReadbackMark = _device.Signal();
        return;
      }
      if (!_device.IsDone(_baseReadbackMark))
      {
        return;
      }
      var readback = _baseReadback;
      _baseReadback = null;
      const int length = SimSize * 4 * SimSize;
      var bytes = new byte[length];
      void* mapped;
      readback.Map(0, null, &mapped).CheckError();
      fixed (byte* target = bytes)
      {
        Buffer.MemoryCopy(mapped, target, length, length);
      }
      readback.Unmap(0);
      readback.Dispose();
      string path = BaseCachePath();
      System.Threading.Tasks.Task.Run(() =>
      {
        try
        {
          System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
          // (Made again with a cache there - by a capture, which always warms up itself: checked against it.)
          if (System.IO.File.Exists(path))
          {
            bool same = System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
            D3D12Device.Log("water base made again: " + (same ? "the same as the cache" : "DIFFERENT from the cache, written anew"));
            if (same)
            {
              return;
            }
          }
          string temp = path + ".tmp";
          System.IO.File.WriteAllBytes(temp, bytes);
          System.IO.File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
          D3D12Device.Log("water base cache not written: " + ex.Message);
        }
      });
    }
    // The latest state into the base (both states stay PIXEL_SHADER_RESOURCE between steps, the base COPY_SOURCE).
    private void CopySimToBase(ID3D12GraphicsCommandList list)
    {
      var sim = _sim[_simCurrent];
      list.ResourceBarrierTransition(sim, ResourceStates.PixelShaderResource, ResourceStates.CopySource);
      if (_baseState != ResourceStates.CopyDest)
      {
        list.ResourceBarrierTransition(_simBase, _baseState, ResourceStates.CopyDest);
      }
      list.CopyResource(_simBase, sim);
      list.ResourceBarrierTransition(sim, ResourceStates.CopySource, ResourceStates.PixelShaderResource);
      list.ResourceBarrierTransition(_simBase, ResourceStates.CopyDest, ResourceStates.CopySource);
      _baseState = ResourceStates.CopySource;
    }

    // The base into the first state, made the latest.
    private void RestoreBaseToSim(ID3D12GraphicsCommandList list)
    {
      var sim = _sim[0];
      list.ResourceBarrierTransition(sim, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
      list.CopyResource(sim, _simBase);
      list.ResourceBarrierTransition(sim, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
      _simCurrent = 0;
    }

    // One step: the latest state read, the other drawn, then swapped (both kept readable between steps); the noise's
    // lookup shifted by <shift>.
    private unsafe void Step(ID3D12GraphicsCommandList list, float kick, float step, (float X, float Y) shift)
    {
      int target = 1 - _simCurrent;
      list.ResourceBarrierTransition(_sim[target], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      var constants = stackalloc float[4];
      constants[0] = kick;
      constants[1] = step;
      constants[2] = shift.X;
      constants[3] = shift.Y;
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

    // The window round the mission marker or the eye (the game's player, 0x93F090), moved and stepped, as a pure
    // function of the section's time: flat at the window, then m = floor(t / 0.02) steps of 0.02 s from the section's
    // start (at most the last 1500, 30 s), step j at (j + 1) x 20 ms with its kicks' numbers by j (the game's grid
    // started flat when the cutscene camera came, 0xAD4E80). Continued step by step where it can (the same window, on
    // in time), else made afresh at the window. Not exact (while playing): a moved window clears what it leaves (as the
    // game's) and goes on (at most 7 steps made up after a step back or a jump on).
    private void Swell(WaterParams p, Vector3 eye, double seconds, bool exact, int budget = int.MaxValue)
    {
      var centre = p.SwellAt ?? eye;
      int x0 = EvenDown(centre.X), y0 = EvenDown(centre.Y);
      int m = (int)Math.Floor(Math.Max(seconds, 0) / SwellStepSeconds + 1e-6), s = Math.Max(0, m - SwellCap);
      bool moved = _swellStarted && (x0 != _swellX0 || y0 != _swellY0);
      bool jumped = !_swellStarted || Math.Abs(x0 - _swellX0) > 200 || Math.Abs(y0 - _swellY0) > 200;
      if (_swellM == m && !moved && !jumped)
      {
        return;
      }
      // (Playing: a small step back - the sound's sync nudging the clock - held, as the wave texture's.)
      if (!exact && !moved && !jumped && _swellM >= 0 && m < _swellM && _swellM - m <= SwellHoldSteps)
      {
        return;
      }
      // The kicks and the swell of the wind.
      float kick = (p.SwellWind * 0.7f + 0.23f) * 10;
      float swell = p.SwellWind * 0.55f + 0.18f;
      bool cont = _swellM >= 0 && !jumped && !moved && m >= _swellM && s == _swellStart;
      int from;
      if (!cont && (exact || jumped || _swellM < 0 || m < _swellM))
      {
        Array.Clear(_height);
        Array.Clear(_speed);
        _swellStarted = true;
        _swellX0 = x0;
        _swellY0 = y0;
        from = s;
        _swellStart = s;
      }
      else
      {
        if (moved)
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
        from = cont ? _swellM : Math.Max(_swellM, m - 7);
        _swellX0 = x0;
        _swellY0 = y0;
        if (!cont)
        {
          _swellStart = s;
        }
      }
      // At most budget steps (the drawing thread's Advance: the rest in the frames after, the swell catching up);
      // a capture's Draw: all.
      int to = (int)Math.Min(m, (long)from + budget);
      for (int j = from; j < to; j++)
      {
        Advance((float)SwellStepSeconds, (j + 1) * 20.0, kick, swell, j);
      }
      _swellM = to;
    }
    // The page's advance (PC 842-865): random kicks, the rows' sine push, then the springs (the window's edges held to
    // themselves, not wrapped). The kicks' numbers from step <j>'s own generator.
    private void Advance(float dt, double ms, float kick, float swell, int j)
    {
      var rng = new Random(unchecked((int)Mix(SwellSeed, j)));
      for (int k = 0; k < 2000; k++)
      {
        int at = rng.Next(SwellCells);
        _speed[at] += (float)(rng.NextDouble() * 2 - 1) * kick * 0.5f * dt;
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
    /// The view mirrored in the water's level, where the page draws its reflection pass (ModelView.html: outside with
    /// water, p' = (x, y, 2 level - z), the view-projection times that mirror, the eye mirrored likewise); false where
    /// it has none (inside, no water level, no water).
    /// </summary>
    public bool MirrorOf(Matrix4x4 viewProjection, Vector3 eye, out Matrix4x4 mirroredViewProjection, out Vector3 mirroredEye)
    {
      mirroredViewProjection = viewProjection;
      mirroredEye = eye;
      if (_quadIndexCount == 0 || _quadVertices == null || !_outdoors || !_level.HasValue)
      {
        return false;
      }
      float level = _level.Value;
      // (Row vectors: p' = p x M, then x the view-projection.)
      var mirror = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 2 * level, 1);
      mirroredViewProjection = mirror * viewProjection;
      mirroredEye = new Vector3(eye.X, eye.Y, 2 * level - eye.Z);
      return true;
    }

    /// <summary>
    /// The mirror made ready to draw into (the page's reflectionTarget: max(1, width / 2) x max(1, height / 2) of the
    /// picture's, the light's format, cleared to nothing): the view and depth to draw into, with
    /// <see cref="MeshRenderer.MirrorSamples"/> samples a pixel as the page's (both in RENDER_TARGET / DEPTH_WRITE
    /// state), and its size. What is drawn into it (the mirrored sky first, then MeshRenderer.DrawMirror; see
    /// <see cref="MirrorOf"/>) the water shows; <see cref="EndMirror"/> after resolves it.
    /// </summary>
    public (CpuDescriptorHandle Target, CpuDescriptorHandle Depth, int Width, int Height) BeginMirror(ID3D12GraphicsCommandList list, int width, int height)
    {
      _device.Profiler?.Begin(list, "water: mirror");
      int mw = Math.Max(1, width >> 1), mh = Math.Max(1, height >> 1);
      var rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart() + 2 * (int)_rtvSize;
      var dsv = _mirrorDsv.GetCPUDescriptorHandleForHeapStart();
      SizeMirror(mw, mh);
      list.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
      list.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
      return (rtv, dsv, mw, mh);
    }

    /// <summary>
    /// The mirror's and the depth copy's targets made for a picture of this size ahead (the drawing thread, while the
    /// water isn't drawn yet), not by the first frame that draws the water: one a call.
    /// </summary>
    public bool MakeTargets(int width, int height)
    {
      // (One a call: true when one was made, the frame's time spent.)
      int mw = Math.Max(1, width >> 1), mh = Math.Max(1, height >> 1);
      if (_mirror == null || _mirrorWidth != mw || _mirrorHeight != mh)
      {
        SizeMirror(mw, mh);
        return true;
      }
      if (_outdoors && (_behind == null || _behindWidth != width || _behindHeight != height))
      {
        SizeBehind(width, height);
        return true;
      }
      return false;
    }

    // The scene's depth copy at the size (read as R32F, slot 5; left readable).
    private void SizeBehind(int width, int height)
    {
      if (_behind != null && _behindWidth == width && _behindHeight == height)
      {
        return;
      }
      var d3d = _device.Device;
      if (_behind != null)
      {
        _device.WaitIdle();
        _behind.Dispose();
      }
      _behind = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.D32_Float, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
        ResourceStates.PixelShaderResource, new ClearValue(Format.D32_Float, 1.0f, 0));
      _behindWidth = width;
      _behindHeight = height;
      d3d.CreateShaderResourceView(_behind, new ShaderResourceViewDescription
      {
        Format = Format.R32_Float,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
      }, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 5 * (int)_srvSize);
    }

    // The mirror's targets at the size (mw x mh).
    private void SizeMirror(int mw, int mh)
    {
      var rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart() + 2 * (int)_rtvSize;
      var dsv = _mirrorDsv.GetCPUDescriptorHandleForHeapStart();
      if (_mirror == null || _mirrorWidth != mw || _mirrorHeight != mh)
      {
        if (_mirror != null)
        {
          _device.WaitIdle();
          _mirror.Dispose();
          _mirrorSamples?.Dispose();
          _mirrorDepth?.Dispose();
        }
        var d3d = _device.Device;
        uint samples = MeshRenderer.MirrorSamples;
        _mirror = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
          ResourceDescription.Texture2D(_lightFormat, (uint)mw, (uint)mh, 1, 1), ResourceStates.PixelShaderResource);
        _mirrorState = ResourceStates.PixelShaderResource;
        _mirrorSamples = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
          ResourceDescription.Texture2D(_lightFormat, (uint)mw, (uint)mh, 1, 1, samples, 0, ResourceFlags.AllowRenderTarget),
          ResourceStates.RenderTarget, new ClearValue(_lightFormat, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
        _mirrorDepth = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
          ResourceDescription.Texture2D(_depthFormat, (uint)mw, (uint)mh, 1, 1, samples, 0, ResourceFlags.AllowDepthStencil),
          ResourceStates.DepthWrite, new ClearValue(_depthFormat, 1.0f, 0));
        _mirrorWidth = mw;
        _mirrorHeight = mh;
        d3d.CreateRenderTargetView(_mirrorSamples, null, rtv);
        d3d.CreateDepthStencilView(_mirrorDepth, null, dsv);
        d3d.CreateShaderResourceView(_mirror, null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 4 * (int)_srvSize);
      }
    }

    /// <summary>The mirror drawn: its samples resolved into the texture the water reads, shown by the next <see cref="Draw"/>.</summary>
    public void EndMirror(ID3D12GraphicsCommandList list)
    {
      if (_mirror == null || _mirrorSamples == null)
      {
        _device.Profiler?.End(list, "water: mirror");
        return;
      }
      list.ResourceBarrierTransition(_mirrorSamples, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
      list.ResourceBarrierTransition(_mirror, _mirrorState, ResourceStates.ResolveDest);
      list.ResolveSubresource(_mirror, 0, _mirrorSamples, 0, _lightFormat);
      list.ResourceBarrierTransition(_mirror, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
      list.ResourceBarrierTransition(_mirrorSamples, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
      _mirrorState = ResourceStates.PixelShaderResource;
      _mirrorDrawn = true;
      _device.Profiler?.End(list, "water: mirror");
    }

    /// <summary>
    /// The water into the light target (in RENDER_TARGET state) with the scene's depth (the solid meshes drawn), after
    /// the solid meshes and before the see-through ones; <paramref name="seconds"/>: the section's time the simulations
    /// show (their state a pure function of it: a frame drawn alone holds the same water as one drawn after others;
    /// the wave texture may need several Draws to get there, see <see cref="StepsLeft"/>). <paramref name="exact"/>
    /// false while playing: small steps back held, a moving swell window carried on. First the wave texture's steps and
    /// the swell's, then the flat quads and (outside, with a water level) the sea grid. Nothing without water. Leaves
    /// the light target and the depth bound, and this class's root signature and descriptor heap set (the see-through
    /// pass sets its own again).
    /// </summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, CpuDescriptorHandle lightTarget, CpuDescriptorHandle depth,
                            int width, int height, Matrix4x4 viewProjection, Vector3 eye, FrameLight light, WaterParams p,
                            double seconds, ID3D12Resource? sunShadowMap = null, bool exact = true, ID3D12Resource? depthBuffer = null)
    {
      if (_quadIndexCount == 0 || _quadVertices == null)
      {
        return;
      }
      var profiler = _device.Profiler;
      profiler?.Begin(list, "water: draw");
      // Outside, the scene's depth copied first (the page's water pass: gameKeepSolidDepth, then waterBehind; only with its
      // post effects, outside): <paramref name="depthBuffer"/> (D32, DEPTH_WRITE, the size of the picture) into a copy the
      // water reads, so it is see-through by how much water lies in front of the scene.
      bool behindOn = false;
      if (depthBuffer != null && _outdoors && Environment.GetEnvironmentVariable("D3D12NOWATERBEHIND") != "1")
      {
        SizeBehind(width, height);
        list.ResourceBarrierTransition(_behind!, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
        list.ResourceBarrierTransition(depthBuffer, ResourceStates.DepthWrite, ResourceStates.CopySource);
        list.CopyResource(_behind, depthBuffer);
        list.ResourceBarrierTransition(depthBuffer, ResourceStates.CopySource, ResourceStates.DepthWrite);
        list.ResourceBarrierTransition(_behind, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        behindOn = true;
      }
      // The page's seaOn (outside and a water level): the sea grid, the swell and the mask.
      bool seaOn = _outdoors && _level.HasValue;
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_srvHeap);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      PollBaseCache();
      // (Brought there by Advance this frame already: not again.)
      bool advanced = _advanced is { } done && done.Seconds == seconds && done.Exact == exact;
      _advanced = null;
      if (SimActive && !advanced)
      {
        profiler?.End(list, "water: draw");
        profiler?.Begin(list, "water: sim");
        Simulate(list, p, seconds, exact);
        profiler?.End(list, "water: sim");
        profiler?.Begin(list, "water: draw");
      }
      bool waves = _waves && _simCleared;

      _ringSlot = (_ringSlot + 1) % RingSlots;
      byte* slot = _ringMapped + (long)_ringSlot * SlotBytes;
      ulong gpuSlot = _ring.GPUVirtualAddress + (ulong)((long)_ringSlot * SlotBytes);

      float level = _level ?? 0;
      if (seaOn)
      {
        if (!advanced)
        {
          Swell(p, eye, seconds, exact);
        }
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
        // each with the colour 0 0 0 as the page's: its grid has no colour attribute (gtaiv-pc.js seaGridVao), so its
        // vColor.b - the near field's weight, nearWeight = vColor.b x 0.25 - is WebGL's generic value, no foam on the
        // grid. Measured at bc_3 s3 f140.5: the near water 0 0 1 (weight 0.25) ~150/255 against the game's and the
        // page's ~100, with 0 the same as theirs (mean difference 13.5 -> 7.3). The game's own grid colour stays open
        // (spec open question 9).
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
            g[k++] = 0;
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
      // The sun's shadow (the frame's, read from the map MeshRenderer drew this frame; none without either).
      new Span<float>(f + 84, 36).Clear();
      if (light.SunShadow is SunShadow sun && sunShadowMap != null)
      {
        if (!ReferenceEquals(sunShadowMap, _sunMapSeen))
        {
          _sunMapSeen = sunShadowMap;
          _device.Device.CreateShaderResourceView(sunShadowMap, null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + 3 * (int)_srvSize);
        }
        Put(f, 84, sun.U, sun.FadeScale);
        Put(f, 88, sun.V, 0);
        Put(f, 92, sun.H, sun.Reference);
        Put(f, 96, sun.ViewForward, 1);
        Put(f, 100, sun.Splits, sun.FadeEnd);
        for (int c = 0; c < MeshRenderer.SunCascades; c++)
        {
          *(Vector4*)(f + 104 + c * 4) = sun.Boxes[c];
        }
      }
      // The mirror, when drawn for this Draw (BeginMirror / EndMirror).
      Put(f, 120, new Vector3(width, height, _mirrorDrawn ? 1 : 0), behindOn ? 1 : 0);
      _mirrorDrawn = false;
      // (The view-projection's inverse: the scene's depth behind taken back to its point.)
      *(Matrix4x4*)(f + 124) = Matrix4x4.Invert(viewProjection, out var inverse) ? inverse : Matrix4x4.Identity;

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
      list.SetGraphicsRootDescriptorTable(6, gpu + 3 * (int)_srvSize);
      list.SetGraphicsRootDescriptorTable(7, gpu + 4 * (int)_srvSize);
      list.SetGraphicsRootDescriptorTable(8, gpu + 5 * (int)_srvSize);

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
      _device.Profiler?.End(list, "water: draw");
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
      _mirror?.Dispose();
      _mirrorSamples?.Dispose();
      _mirrorDepth?.Dispose();
      _mirrorDsv.Dispose();
      _behind?.Dispose();
      _sim[0].Dispose();
      _sim[1].Dispose();
      _simBase.Dispose();
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
