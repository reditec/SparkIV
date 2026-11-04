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
using SparkIV.WinUI.Rendering.D3D12;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// What a GTA IV (PC) frame's sky is drawn with (#112 step 4e), all in the page's light units (the game's HDR x the
  /// time cycle's Exposure): gta_atmoscatt_clouds' constants as GTAIV.exe sets them from the time cycle (0xDBBDF0,
  /// 0xDBC049), the fog's colours the sky is faded to, and the clouds' noise Phase (rage_perlinnoise).
  /// </summary>
  public struct SkyParams
  {
    /// <summary>The sky's sun (towards it): <see cref="TimeCycle.SkySunDirection"/>, not the light's.</summary>
    public Vector3 SunDirection;
    /// <summary>SkyColor (columns 64 to 66), AzimuthColor (67 to 69), AzimuthColorEast (70 to 72).</summary>
    public Vector3 SkyColor;
    public Vector3 AzimuthColor;
    public Vector3 AzimuthColorEast;
    /// <summary>AzimuthHeight (column 77), AzimuthStrength (78).</summary>
    public float AzimuthHeight;
    public float AzimuthStrength;
    /// <summary>The sky's SunColor (columns 103 to 105).</summary>
    public Vector3 SunColor;
    /// <summary>HDRExposure = SkyLightMult (column 60) x 2.85 (0xDBC049, 0xEF4224); no game sky while 0.</summary>
    public float HdrExposure;
    /// <summary>HDRSunExposure: x 0.35 (0xFE880C), y column 119.</summary>
    public Vector2 HdrSunExposure;
    /// <summary>HDRExposureClamp, 30 (0xDBC0A3).</summary>
    public float HdrClamp;
    /// <summary>The page's exposure (column 35): every light colour times it (1 where it is 0).</summary>
    public float PageExposure;
    /// <summary>The near fog's colour (globalFogColorN, 0xAF3A50) x the exposure, and its amount (column 11 / 255).</summary>
    public Vector3 FogNear;
    public float NearFogAmount;
    /// <summary>The far fog's colour (globalFogColor = the dome's gtaWaterColor) x the exposure: what the sky fades to below the horizon.</summary>
    public Vector3 FogFar;
    /// <summary>CloudColor (81 to 83), SunsetColor (73 to 75 x 106), TopCloudColor (99 to 101 + 73 to 75).</summary>
    public Vector3 CloudColor;
    public Vector3 SunsetColor;
    public Vector3 TopCloudColor;
    /// <summary>TopCloudBias, Detail, Threshold, Height (95 to 98).</summary>
    public Vector4 TopCloud;
    /// <summary>CloudThreshold (85), CloudBias (86), CloudShadowStrength (84), CloudInscatteringRange (88).</summary>
    public Vector4 CloudShape;
    /// <summary>The thickness (92), the detail's scale (90) and strength (91), CloudFadeOut (94).</summary>
    public Vector4 CloudDetail;
    /// <summary>CloudShadowOffset (column 87).</summary>
    public float CloudShadowOffset;
    /// <summary>Column 89: the shader's EdgeSmooth is 1 / it (0x615D98); 0 (TLAD): no edge factor.</summary>
    public float CloudEdge;
    /// <summary>rage_perlinnoise's Phase now (see <see cref="CutsceneSky.CloudPhase"/>).</summary>
    public float CloudPhase;

    /// <summary>
    /// The sky of a time cycle (outside's, a room's, or a mix) at an hour, as the page's "sky" message has it
    /// (TimeCycle.Message, skyHdr and fog). <paramref name="farFogFallback"/>: the far fog's light where the time cycle
    /// has no sky columns (the page then takes its haze colour back through the tone mapping, farFogLight; not done
    /// here: black without one).
    /// </summary>
    public static SkyParams From(TimeCycle c, float hour, float cloudPhase, Vector3? farFogFallback = null)
    {
      float exposure = c.Exposure;
      return new SkyParams
      {
        SunDirection = TimeCycle.SkySunDirection(hour),
        SkyColor = c.SkyColor,
        AzimuthColor = c.AzimuthColor,
        AzimuthColorEast = c.AzimuthColorEast,
        AzimuthHeight = c.AzimuthHeight,
        AzimuthStrength = c.AzimuthStrength,
        SunColor = c.SkySunColor,
        HdrExposure = c.SkyLightMultiplier * 2.85f,
        HdrSunExposure = new Vector2(0.35f, c.SkySunExposure),
        HdrClamp = 30f,
        PageExposure = exposure != 0 ? exposure : 1f,
        // globalFogColorN: a modifier's NearFog, else the sky bottom x SkyLightMult (0xAF3A50).
        FogNear = (c.NearFogColor ?? c.SkyBottom * c.SkyLightMultiplier) * exposure,
        NearFogAmount = c.NearFogAmount,
        // globalFogColor: (SkyColor + AzimuthStrength x AzimuthColor) x SkyLightMult (traces bc_1 GTAIV.trace,
        // et01_ba GTAIV.2.trace).
        FogFar = c.SkyColor != Vector3.Zero
          ? (c.SkyColor + c.AzimuthStrength * c.AzimuthColor) * c.SkyLightMultiplier * exposure
          : farFogFallback ?? Vector3.Zero,
        CloudColor = c.CloudColor,
        SunsetColor = c.SunsetColor,
        TopCloudColor = c.TopCloudColor,
        TopCloud = c.TopCloud,
        CloudShape = c.CloudShape,
        CloudDetail = c.CloudDetail,
        CloudShadowOffset = c.CloudShadowOffset,
        CloudEdge = c.CloudEdge,
        CloudPhase = cloudPhase,
      };
    }
  }

  /// <summary>
  /// A GTA IV (PC) cutscene's sky for the native renderer (#112 step 4e), as the page's drawSky (ModelView.html) after the
  /// game's gta_atmoscatt_clouds.fxc (GTA IV's trace GTAIV.1 PS 0x246411a0, program 8, instruction for instruction; TLAD's
  /// GTAIV.4): drawn first into the frame's light target, a full-screen triangle with no depth, each pixel's direction
  /// from the inverse view-projection. The sky's colour by the azimuth, the sun's glow, the clouds (the noise of
  /// rage_perlinnoise in a 256 x 256 target, made again only when its Phase moved, and the fine detail, laid out as
  /// on the game's dome), below the horizon faded to the far fog, then the near fog's share (the post fog's, without
  /// distance). The light only: the page's picture-colour fallback (no sky columns) is not drawn. Not yet: stars, moon,
  /// the sun disc term (SunCentre.z, 0 in the traces).
  /// </summary>
  public sealed class CutsceneSky : IDisposable
  {
    /// <summary>The page's Phase at the start: the TLAD trace's (GTAIV.4); the real one is the save's play time.</summary>
    public const float StartPhase = 593.2532f;

    private const int PerlinSize = 256;
    private const Format PerlinFormat = Format.R8G8B8A8_UNorm;
    // The shader's Sky: 76 floats (304 bytes), a slot of 512 (constant buffers start at multiples of 256).
    private const int ConstantSlotBytes = 512;
    private const int ConstantSlots = 8;

    // D3D12_ENCODE_SHADER_4_COMPONENT_MAPPING as TextureCache's: as they are, and L8's red as grey with alpha 1.
    private const int MappingDefault = 0 | (1 << 3) | (2 << 6) | (3 << 9) | (1 << 12);
    private const int MappingGrey = 0 | (0 << 3) | (0 << 6) | (5 << 9) | (1 << 12);

    private const string Shader = @"
struct PerlinConstants
{
  float4 Phase;      // x: rage_perlinnoise's Phase
};
ConstantBuffer<PerlinConstants> q : register(b0);
struct Sky
{
  row_major float4x4 InverseViewProjection;
  float4 Eye;           // xyz: the eye, w: clouds on (1)
  float4 SunDir;        // xyz: the sky's sun
  float4 SkyColor;      // rgb, w: AzimuthHeight
  float4 Azimuth;       // rgb, w: AzimuthStrength
  float4 AzimuthEast;   // rgb, w: the page's exposure
  float4 SunColor;      // rgb, w: the near fog's amount
  float4 Hdr;           // HDRExposure, HDRSunExposure.xy, HDRExposureClamp
  float4 FogNear;       // rgb: the near fog's light, w: CloudShadowOffset
  float4 FogFar;        // rgb: the far fog's light, w: column 89 (1 / EdgeSmooth)
  float4 CloudColor;
  float4 Sunset;
  float4 TopCloudColor;
  float4 TopCloud;      // bias, detail, threshold, height
  float4 CloudShape;    // threshold, bias, shadow strength, inscattering range
  float4 CloudDetail;   // thickness, detail scale, detail strength, fade out
};
ConstantBuffer<Sky> s : register(b1);
Texture2D Clouds : register(t0);     // baseperlinnoise of skydome.wtd
Texture2D Detail : register(t1);     // noise16_p of skydome.wtd
Texture2D Perlin : register(t2);     // rage_perlinnoise's target
SamplerState Wrap : register(s0);

struct VsOut { float4 position : SV_Position; float2 ndc : TEXCOORD0; };

VsOut VSMain(uint id : SV_VertexID)
{
  VsOut o;
  float2 uv = float2((id << 1) & 2, id & 2);
  o.ndc = uv * float2(2, -2) + float2(-1, 1);
  o.position = float4(o.ndc, 0, 1);
  return o;
}

// rage_perlinnoise (PS 0x23C2D220 in TLAD's GTAIV.4, the same constants in both traces): six octaves of two drifting
// reads of the base noise, warped round the middle. Texel (u, v) of the target is the noise at p = (u, v) - 0.5 (in
// GL its row 0 is ndc y -1, so p.y = v - 0.5 there as here).
static const float2 Velocity1 = float2(-0.01, 0.01);
static const float2 Velocity2 = float2(0.005, 0.02);
static const float Frequency1 = 1.9, Frequency2 = 1.0, Contrast = 1.1, Brightness = 0.76, NoiseWarp = 1.0;
static const float Persistance = 0.7692308, ClumpBump = 1.0;

float4 PSPerlin(VsOut i) : SV_Target
{
  float2 p = i.position.xy / 256.0 - 0.5;
  float l2 = max(dot(p, p), 1e-12);
  float2 at = p * rsqrt(l2) * pow(l2, NoiseWarp) * 1.5 + 0.0625;
  float phase = q.Phase.x;
  float2 sum = 0;
  float higher = 0, weight = 0.5, f1 = 1, f2 = 1;
  [unroll] for (int k = 0; k < 6; k++)
  {
    float2 both = (Clouds.Sample(Wrap, at * f1 + phase * Velocity1).rg + Clouds.Sample(Wrap, at * f2 + phase * Velocity2).rg) * weight;
    sum += both;
    if (k >= 2)
    {
      higher += both.x;
    }
    weight *= Persistance;
    f1 *= Frequency1;
    f2 *= Frequency2;
  }
  float3 v = saturate(float3(sum, 2 * higher) - Contrast) * Brightness;
  return float4(saturate(v.xy), saturate(ClumpBump * (v.z - 1) + 1), 1);
}

// The game's dome (17 rings, radius 2 cos(k pi/32), height 1.636 sin(k pi/32), scaled (10000, 2000, 10000) round the
// camera; ring 0 lowered by another 1 by the dome VS): per ring its radius, height and texture distance from 0.5.
static const float3 DOME[17] = {
  float3(2.0, -2.8, 0.5), float3(1.990369, 0.160377, 0.476628), float3(1.961571, 0.319210, 0.455850),
  float3(1.913881, 0.474967, 0.435314), float3(1.847759, 0.626151, 0.414661), float3(1.763843, 0.771311, 0.391533),
  float3(1.662939, 0.909041, 0.369135), float3(1.546021, 1.038011, 0.343182), float3(1.414214, 1.156980, 0.313923),
  float3(1.268786, 1.264809, 0.281642), float3(1.111140, 1.360474, 0.246648), float3(0.942793, 1.443023, 0.209279),
  float3(0.765367, 1.511670, 0.169894), float3(0.580569, 1.565755, 0.128873), float3(0.390181, 1.604780, 0.086611),
  float3(0.196034, 1.628340, 0.043515), float3(0.0, 1.636219, 0.0) };

// VS c75 in TLAD's trace; it moves with time in the game, fixed here.
static const float2 DetailOffset = 0.01091306;

// Where a view direction meets the dome, as its texture coordinate (dome y = world z, dome z = world -y).
float2 DomeUv(float3 d)
{
  float h = max(length(d.xy), 1e-6);
  float slope = 5 * d.z / h;
  float uvr = 0.5;
  [loop] for (int k = 0; k < 16; k++)
  {
    float3 a = DOME[k], b = DOME[k + 1];
    if (slope * b.x <= b.y)
    {
      uvr = lerp(a.z, b.z, saturate((slope * a.x - a.y) / ((b.y - a.y) - slope * (b.x - a.x))));
      break;
    }
  }
  return 0.5 + uvr * float2(d.x, -d.y) / h;
}

float Pow32(float a)
{
  a *= a; a *= a; a *= a; a *= a;
  return a * a;
}

// gta_atmoscatt_clouds program 8 (GTAIV.1 PS 0x246411a0, TLAD's GTAIV.4 PS 0x23CFA160), in light.
float4 PSSky(VsOut i) : SV_Target
{
  // The direction through the pixel: its point on the far plane (Direct3D's depth 1) less the eye.
  float4 farPoint = mul(float4(i.ndc, 1, 1), s.InverseViewProjection);
  float3 dir = normalize(farPoint.xyz / farPoint.w - s.Eye.xyz);
  float3 sunDir = s.SunDir.xyz;

  float3 skyV = s.SkyColor.rgb + lerp(s.AzimuthEast.rgb, s.Azimuth.rgb, 0.5 * dir.x + 0.5)
                                 * (1 - saturate(dir.z * s.SkyColor.w)) * s.Azimuth.w;
  float c = dot(dir, sunDir);
  float a = saturate(saturate(1 - s.Hdr.z) * (12.58 * c - 11.6163721));
  float b = saturate(0.9375 - 0.0625 * c);
  // SunSize 1 in both traces; the disc term x SunCentre.z (0 there) left out.
  float sun = saturate((Pow32(a) - b * b * b * saturate(0.6 + sunDir.z) + saturate(0.5 * c - 0.0625)) * 0.5) * 4;
  float3 sunLight = sun * s.SunColor.rgb;
  float3 sunLight2 = sunLight * sunLight;
  float3 glow = (sunLight + sunLight2 * sunLight2) * s.Hdr.y + saturate(saturate(1 - sun) * s.Hdr.y * skyV);

  if (s.Eye.w > 0.5)
  {
    float2 uv = DomeUv(dir);
    float2 fromMiddle = uv - 0.5;
    float2 edge = uv * 2 - 1;
    float r2 = dot(fromMiddle, fromMiddle);
    float2 shadowUv = uv + float2(sunDir.x - dir.x, dir.y - sunDir.y) * 0.1 * s.FogNear.w * (1 - dot(edge, edge));
    float2 detailUv = fromMiddle * rsqrt(max(r2, 1e-12)) * r2 * s.CloudDetail.y + DetailOffset;
    float2 topUv = fromMiddle * s.TopCloud.w + 0.5;
    float topFade = 1 - length(fromMiddle * s.TopCloud.w);

    float threshold = s.CloudShape.x, bias = s.CloudShape.y, strength = s.CloudDetail.z;
    float shadow = saturate((threshold * (Detail.Sample(Wrap, shadowUv * s.CloudDetail.y).r * strength * 0.2
                                          + Perlin.Sample(Wrap, shadowUv).r) - bias) * s.CloudShape.z);
    float4 cell = Perlin.Sample(Wrap, uv);
    float fine = Detail.Sample(Wrap, detailUv).r - 0.5;
    float clump = saturate(fine * 0.2 + cell.b);
    float amount = saturate(threshold * (fine * strength + cell.r) - bias);
    float edgeWidth = s.FogFar.w;
    float cover = amount > 0 ? pow(amount, s.CloudDetail.x) * (edgeWidth > 0 ? saturate(amount / edgeWidth) : 1) : 0;
    float litShare = (c * 0.25 + 0.35) * ((1 - shadow) * cover * clump + clump);
    float3 cloud = (c * c * max(1 - cover - 0.5 * shadow, 0) * s.CloudShape.w + 1) * s.CloudColor.rgb - shadow + s.Sunset.rgb * litShare;
    float top = saturate(s.TopCloud.z * (fine * strength * s.TopCloud.y + Perlin.Sample(Wrap, topUv).g) - s.TopCloud.x)
                * topFade * (1 - cover);
    float total = (1 - cover) * top + cover;
    glow = lerp(glow, lerp(cloud, s.TopCloudColor.rgb, top), saturate(dir.z * 4 - s.CloudDetail.w) * total);
  }

  float3 light = min(glow * s.Hdr.x, s.Hdr.w) * s.AzimuthEast.w;
  // Below the horizon: towards the far fog (gtaSkyDomeFade (0, 0.00025)) on an ellipsoid dome 10000 m wide, stretched
  // to 4000 m below (the dome VS's y - saturate(-y)).
  if (dir.z < 0)
  {
    float t = rsqrt(dot(dir.xy, dir.xy) * 1e-8 + dir.z * dir.z / 1.6e7);
    light = lerp(light, s.FogFar.rgb, saturate(-dir.z * t * 0.00025));
  }
  // rage_postfx's fog on the sky, without distance: only the near fog's share.
  light = lerp(light, s.FogNear.rgb, s.SunColor.w);
  return float4(light, 1);
}
";

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _perlinPipeline;
    private readonly ID3D12PipelineState _skyPipeline;
    private readonly ID3D12PipelineState _mirrorSkyPipeline;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly uint _srvSize;
    private readonly ID3D12Resource _perlin;
    private ResourceStates _perlinState = ResourceStates.PixelShaderResource;
    private float _perlinPhase = float.NaN;
    private readonly ID3D12Resource _constants;
    private readonly unsafe byte* _constantsMapped;
    private int _constantSlot;
    private ID3D12Resource? _clouds;
    private ID3D12Resource? _detail;

    public unsafe CutsceneSky(D3D12Device device, Format lightFormat)
    {
      _device = device;
      var d3d = device.Device;
      // b0: the perlin pass's Phase; b1: the sky's constants; t0, t1: the clouds' textures; t2: the perlin target (its
      // own table, so the perlin pass never has its own target bound).
      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, 4), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 0, 0, 0)), ShaderVisibility.Pixel),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 2, 0, 0)), ShaderVisibility.Pixel),
      };
      // The page's: REPEAT, mipmapped, anisotropic (the perlin target has no mips).
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
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters, samplers));
      var vs = Compile(DxcShaderStage.Vertex, "VSMain");
      GraphicsPipelineStateDescription Pipeline(string ps, Format format, uint samples = 1) => new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = vs,
        PixelShader = Compile(DxcShaderStage.Pixel, ps),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.Opaque,
        DepthStencilState = DepthStencilDescription.None,
        RenderTargetFormats = new[] { format },
        SampleDescription = new SampleDescription(samples, 0),
      };
      _perlinPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSPerlin", PerlinFormat));
      _skyPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSSky", lightFormat));
      // (Into the water's mirror: its target has MeshRenderer.MirrorSamples samples a pixel.)
      _mirrorSkyPipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSSky", lightFormat, (uint)MeshRenderer.MirrorSamples));

      _srvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, DescriptorHeapFlags.ShaderVisible));
      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
      _srvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

      // The perlin target (256 x 256 RGBA8, as the page's), kept to be read between its passes.
      _perlin = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(PerlinFormat, (uint)PerlinSize, (uint)PerlinSize, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
        ResourceStates.PixelShaderResource, new ClearValue(PerlinFormat, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
      d3d.CreateRenderTargetView(_perlin, null, _rtvHeap.GetCPUDescriptorHandleForHeapStart());
      // Every slot a valid view from the start: the clouds' slots show the perlin target until their textures come
      // (the sky draws no clouds without them, so they are never read).
      for (int slot = 0; slot < 3; slot++)
      {
        d3d.CreateShaderResourceView(_perlin, null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + slot * (int)_srvSize);
      }

      // The sky's constants, a slot per draw in flight.
      _constants = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                               ResourceDescription.Buffer((ulong)ConstantSlots * ConstantSlotBytes), ResourceStates.GenericRead);
      void* mapped;
      _constants.Map(0, null, &mapped).CheckError();
      _constantsMapped = (byte*)mapped;
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

    /// <summary>Whether both cloud textures are on the GPU (the clouds are drawn only then, as the page's).</summary>
    public bool HasClouds => _clouds != null && _detail != null;

    /// <summary>
    /// rage_perlinnoise's Phase after <paramref name="clipSeconds"/> of the section's clip (the page's): the Phase at the
    /// start (the page's <see cref="StartPhase"/>: no other is known) + CloudSpeed (column 107, per game hour: 0x62E110)
    /// x the game hours passed, taken as 120 s each [PAGE]; standing still while the clock is locked.
    /// </summary>
    public static float CloudPhase(TimeCycle c, double clipSeconds, bool clockLocked, float start = StartPhase)
      => start + (clockLocked ? 0f : (float)(c.CloudSpeed * clipSeconds / 120.0));

    /// <summary>
    /// The clouds' textures (CutsceneLocation.Clouds and CloudDetail, "SXT1" as ModelScene.Raw makes them) made on the GPU:
    /// their copies recorded into <paramref name="list"/>, the upload buffers added to <paramref name="uploads"/> for the
    /// caller to free once the list is done. Ones before are freed (the GPU done with them first). Null: no clouds.
    /// </summary>
    public void SetTextures(byte[]? clouds, byte[]? cloudDetail, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      if (_clouds != null || _detail != null)
      {
        _device.WaitIdle();
        _clouds?.Dispose();
        _detail?.Dispose();
        _clouds = null;
        _detail = null;
      }
      _clouds = Make(clouds, 0, list, uploads);
      _detail = Make(cloudDetail, 1, list, uploads);
      _perlinPhase = float.NaN;
    }

    // A texture of a "SXT1" blob (format 0 DXT1, 1 DXT3, 2 DXT5, 3 A8R8G8B8, 4 L8; width, height, levels; the levels'
    // data) with all its levels the data holds, its view in the slot; null if it can't be made. As TextureCache's.
    private unsafe ID3D12Resource? Make(byte[]? blob, int slot, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      if (blob == null || blob.Length < 20 || blob[0] != 'S' || blob[1] != 'X' || blob[2] != 'T' || blob[3] != '1')
      {
        return null;
      }
      int format = BitConverter.ToInt32(blob, 4);
      int width = BitConverter.ToInt32(blob, 8);
      int height = BitConverter.ToInt32(blob, 12);
      int levelCount = Math.Max(1, BitConverter.ToInt32(blob, 16));
      bool dxt = format <= 2;
      if (format < 0 || format > 4 || width <= 0 || height <= 0 || (dxt && (width % 4 != 0 || height % 4 != 0)))
      {
        D3D12Device.Log("sky texture not made: format " + format + ", " + width + " x " + height);
        return null;
      }
      int blockBytes = format == 0 ? 8 : 16;
      var levels = new List<(int Offset, int RowBytes, int Rows)>();
      int offset = 20;
      for (int level = 0; level < levelCount; level++)
      {
        int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
        int rowBytes = dxt ? (w + 3) / 4 * blockBytes : w * (format == 3 ? 4 : 1);
        int rows = dxt ? (h + 3) / 4 : h;
        if (offset + rowBytes * rows > blob.Length)
        {
          break;
        }
        levels.Add((offset, rowBytes, rows));
        offset += rowBytes * rows;
      }
      if (levels.Count == 0)
      {
        return null;
      }
      var dxgi = format switch
      {
        0 => Format.BC1_UNorm,
        1 => Format.BC2_UNorm,
        2 => Format.BC3_UNorm,
        3 => Format.B8G8R8A8_UNorm,
        _ => Format.R8_UNorm,
      };
      var d3d = _device.Device;
      var texture = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(dxgi, (uint)width, (uint)height, 1, (ushort)levels.Count), ResourceStates.CopyDest);

      // Each level's rows every 256 bytes, each level at a multiple of 512 bytes in the upload buffer.
      var offsets = new long[levels.Count];
      var pitches = new int[levels.Count];
      long total = 0;
      for (int l = 0; l < levels.Count; l++)
      {
        total = (total + 511) & ~511L;
        offsets[l] = total;
        pitches[l] = (levels[l].RowBytes + 255) & ~255;
        total += (long)pitches[l] * levels[l].Rows;
      }
      var upload = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                               ResourceDescription.Buffer((ulong)total), ResourceStates.GenericRead);
      void* mapped;
      upload.Map(0, null, &mapped).CheckError();
      fixed (byte* source = blob)
      {
        for (int l = 0; l < levels.Count; l++)
        {
          for (int r = 0; r < levels[l].Rows; r++)
          {
            Buffer.MemoryCopy(source + levels[l].Offset + (long)r * levels[l].RowBytes, (byte*)mapped + offsets[l] + (long)r * pitches[l], pitches[l], levels[l].RowBytes);
          }
        }
      }
      upload.Unmap(0);
      for (int l = 0; l < levels.Count; l++)
      {
        int w = Math.Max(1, width >> l), h = Math.Max(1, height >> l);
        var footprint = new PlacedSubresourceFootPrint
        {
          Offset = (ulong)offsets[l],
          Footprint = new SubresourceFootPrint(dxgi, (uint)w, (uint)h, 1, (uint)pitches[l]),
        };
        list.CopyTextureRegion(new TextureCopyLocation(texture, (uint)l), 0, 0, 0, new TextureCopyLocation(upload, footprint), null);
      }
      list.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
      uploads.Add(upload);
      var view = new ShaderResourceViewDescription
      {
        Format = dxgi,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        Shader4ComponentMapping = (uint)(format == 4 ? MappingGrey : MappingDefault),
        Texture2D = new Texture2DShaderResourceView { MipLevels = (uint)levels.Count },
      };
      d3d.CreateShaderResourceView(texture, view, _srvHeap.GetCPUDescriptorHandleForHeapStart() + slot * (int)_srvSize);
      return texture;
    }

    // rage_perlinnoise into its target (the page's perlinNoise: only when the Phase moved).
    private unsafe void DrawPerlin(ID3D12GraphicsCommandList list, float phase)
    {
      if (_perlinState != ResourceStates.RenderTarget)
      {
        list.ResourceBarrierTransition(_perlin, _perlinState, ResourceStates.RenderTarget);
        _perlinState = ResourceStates.RenderTarget;
      }
      var gpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();
      var constants = stackalloc float[4];
      constants[0] = phase;
      list.OMSetRenderTargets(_rtvHeap.GetCPUDescriptorHandleForHeapStart());
      list.RSSetViewport(new Vortice.Mathematics.Viewport(PerlinSize, PerlinSize));
      list.RSSetScissorRect(PerlinSize, PerlinSize);
      list.SetPipelineState(_perlinPipeline);
      list.SetGraphicsRoot32BitConstants(0, 4, constants, 0);
      list.SetGraphicsRootDescriptorTable(2, gpu);
      // t2 isn't read by this pass; the clouds' slot keeps the table from pointing at the target being drawn.
      list.SetGraphicsRootDescriptorTable(3, gpu);
      list.DrawInstanced(3, 1, 0, 0);
      list.ResourceBarrierTransition(_perlin, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      _perlinState = ResourceStates.PixelShaderResource;
      _perlinPhase = phase;
    }

    /// <summary>
    /// The sky into the light target (in RENDER_TARGET state, cleared; drawn first, before the meshes), as the page's
    /// main view; the perlin noise made again first when the Phase moved. Nothing without the game's sky columns
    /// (HDRExposure 0: the page masks the light off for its picture-only sky). Leaves the light target bound without
    /// depth. Not yet: the parabolic surroundings map (toMap: the direction from the map's ndc, the result also as its
    /// picture, no near fog) and the mirrored view for the water (the mirrored view-projection and eye, near fog 0).
    /// </summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, CpuDescriptorHandle lightTarget, int width, int height,
                            Matrix4x4 viewProjection, Vector3 eye, SkyParams p, bool mirror = false)
    {
      if (!(p.HdrExposure > 0) || !Matrix4x4.Invert(viewProjection, out var inverse))
      {
        return;
      }
      bool clouds = HasClouds;
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetDescriptorHeaps(_srvHeap);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      if (clouds && _perlinPhase != p.CloudPhase)
      {
        DrawPerlin(list, p.CloudPhase);
      }

      _constantSlot = (_constantSlot + 1) % ConstantSlots;
      var f = (float*)(_constantsMapped + _constantSlot * ConstantSlotBytes);
      // The layout of the shader's Sky (row_major: System.Numerics' matrix as it is).
      *(Matrix4x4*)f = inverse;
      Put(f, 16, eye, clouds ? 1 : 0);
      Put(f, 20, p.SunDirection, 0);
      Put(f, 24, p.SkyColor, p.AzimuthHeight);
      Put(f, 28, p.AzimuthColor, p.AzimuthStrength);
      Put(f, 32, p.AzimuthColorEast, p.PageExposure);
      Put(f, 36, p.SunColor, p.NearFogAmount);
      Put(f, 40, new Vector3(p.HdrExposure, p.HdrSunExposure.X, p.HdrSunExposure.Y), p.HdrClamp);
      Put(f, 44, p.FogNear, p.CloudShadowOffset);
      Put(f, 48, p.FogFar, p.CloudEdge);
      Put(f, 52, p.CloudColor, 0);
      Put(f, 56, p.SunsetColor, 0);
      Put(f, 60, p.TopCloudColor, 0);
      Put(f, 64, new Vector3(p.TopCloud.X, p.TopCloud.Y, p.TopCloud.Z), p.TopCloud.W);
      Put(f, 68, new Vector3(p.CloudShape.X, p.CloudShape.Y, p.CloudShape.Z), p.CloudShape.W);
      Put(f, 72, new Vector3(p.CloudDetail.X, p.CloudDetail.Y, p.CloudDetail.Z), p.CloudDetail.W);

      var gpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();
      list.OMSetRenderTargets(lightTarget);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetPipelineState(mirror ? _mirrorSkyPipeline : _skyPipeline);
      list.SetGraphicsRootConstantBufferView(1, _constants.GPUVirtualAddress + (ulong)(_constantSlot * ConstantSlotBytes));
      list.SetGraphicsRootDescriptorTable(2, gpu);
      list.SetGraphicsRootDescriptorTable(3, gpu + 2 * (int)_srvSize);
      list.DrawInstanced(3, 1, 0, 0);
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
      _clouds?.Dispose();
      _detail?.Dispose();
      _perlin.Dispose();
      _constants.Unmap(0);
      _constants.Dispose();
      _srvHeap.Dispose();
      _rtvHeap.Dispose();
      _perlinPipeline.Dispose();
      _skyPipeline.Dispose();
      _mirrorSkyPipeline.Dispose();
      _rootSignature.Dispose();
    }
  }
}
