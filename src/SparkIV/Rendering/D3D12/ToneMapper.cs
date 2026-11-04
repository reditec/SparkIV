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
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The frame's light into its picture (#112 steps 4c and 4h), as the page's post composite (gtaiv-pc.js, after GTA
  /// IV's rage_postfx): the scene is drawn into a float light target (R16G16B16A16); a quarter-size copy (each texel the
  /// mean of four bilinear reads, its 4 x 4 block) is read back for the mean luminance, which the eye adapts to (kept
  /// within AdaptedLumMin and AdaptedLumMax, eased over AdaptTime while playing); the same copy, blurred across and
  /// down (the page's blur passes), is the bloom. The composite then takes the depth of field (from the depth buffer),
  /// adds the film grain, scales the light by MidGrey / the adapted luminance, adds the bloom above the bright pass,
  /// applies saturation, colour shift, colour correction and gamma on the luminance, and fades to black.
  /// </summary>
  public sealed class ToneMapper : IDisposable
  {
    public const Format LightFormat = Format.R16G16B16A16_Float;

    private const int Constants = 28;

    private const string Shader = @"
struct Constants
{
  float4 Scale;      // x: tone scale, y: saturation, z: gamma, w: unused
  float4 Correct;    // rgb: colour correction (x 2 in the shader)
  float4 Shift;      // rgb: colour shift, w: 1 / (its luminance + 0.001)
  float4 Texel;      // xy: 1 / the read texture's size
  float4 Dof;        // near clip, far clip, focus near, focus far (y 0: no depth of field)
  float4 Grain;      // xy: the grain's random offset, z: its strength, w: the fade to black
  float4 Bloom;      // x: bloom scale, y: bright pass, zw: the blur's direction
};
ConstantBuffer<Constants> c : register(b0);
Texture2D Light : register(t0);
Texture2D Blurred : register(t1);
Texture2D Unused : register(t2);
Texture2D<float> Depth : register(t3);
Texture2D Noise : register(t4);
SamplerState Linear : register(s0);

static const float3 Luma = float3(0.2125, 0.7154, 0.0721);

struct VsOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };

VsOut VSMain(uint id : SV_VertexID)
{
  VsOut o;
  float2 uv = float2((id << 1) & 2, id & 2);
  o.uv = uv;
  o.position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}

float3 LightAt(float2 uv)
{
  float3 v = Light.SampleLevel(Linear, uv, 0).rgb;
  return all(abs(v) < 65000) ? v : 0;
}

// The quarter copy: four bilinear reads, the mean of the 4 x 4 block (the page's down pass).
float4 PSDown(VsOut i) : SV_Target
{
  float2 t = c.Texel.xy;
  float3 v = LightAt(i.uv + float2(-t.x, -t.y)) + LightAt(i.uv + float2(t.x, -t.y)) + LightAt(i.uv + float2(-t.x, t.y)) + LightAt(i.uv + float2(t.x, t.y));
  return float4(v * 0.25, 1);
}

// The bloom's blur (the page's blur pass, once across, once down): what lies above the texel three texels either way,
// weighted 0.9, 0.8, 0.7, x 0.172413796, added.
float4 PSBlur(VsOut i) : SV_Target
{
  float3 col = Light.SampleLevel(Linear, i.uv, 0).rgb;
  float3 sum = 0;
  const float w[3] = { 0.9, 0.8, 0.7 };
  [unroll] for (int k = 1; k <= 3; k++)
  {
    float3 a = Light.SampleLevel(Linear, i.uv + c.Bloom.zw * c.Texel.xy * k, 0).rgb - col;
    float3 b = Light.SampleLevel(Linear, i.uv - c.Bloom.zw * c.Texel.xy * k, 0).rgb - col;
    sum += (max(a, 0) + max(b, 0)) * w[k - 1];
  }
  return float4(col + sum * 0.172413796, 1);
}

// The composite (the page's gamePostShader composite).
float4 PSComposite(VsOut i) : SV_Target
{
  float2 t = c.Texel.xy;
  float3 hdr = LightAt(i.uv);
  if (c.Dof.y > 0)
  {
    // The depth of field (rage_postfx's last pass): 0 between the camera's DOF near and far, rising to 1 half a metre
    // nearer than near and half a metre beyond far, squared; four filtered reads around (the game's offsets, Direct3D's
    // texture space), and 1 also where the point stands out from them; the point itself halfway to the read at
    // (-1.5, 0.5) where anything was drawn (the G-buffer's GB2.w); then lerp(point, their mean).
    float d = Depth.Load(int3(i.position.xy, 0));
    float z = c.Dof.x * c.Dof.y / (c.Dof.y - d * (c.Dof.y - c.Dof.x));
    float b = min(max(max(c.Dof.z - z, z - c.Dof.w), 0) * 2.0, 1.0);
    b *= b;
    float3 s0 = LightAt(i.uv + t * float2(-0.5, -1.5)), s1 = LightAt(i.uv + t * float2(1.5, -0.5));
    float3 s2 = LightAt(i.uv + t * float2(0.5, 1.5)), s3 = LightAt(i.uv + t * float2(-1.5, 0.5));
    float4 l = float4(dot(s0, Luma), dot(s1, Luma), dot(s2, Luma), dot(s3, Luma));
    float mean = dot(l, 0.25);
    float4 off = l - mean;
    float centre = dot(hdr, Luma) - mean;
    if (centre * centre - dot(off, off) >= 0) b = 1;
    if (d < 1) hdr = 0.5 * hdr + 0.5 * s3;
    hdr = hdr * (1 - b) + (s0 + s1 + s2 + s3) * (b * 0.25);
  }
  // The film grain: the noise texture's blue at frac(uv x (3.2, 1.8) + a random offset), - 0.5, x strength x 1.1.
  if (c.Grain.z > 0)
  {
    hdr += (Noise.SampleLevel(Linear, frac(i.uv * float2(3.2, 1.8) + c.Grain.xy), 0).b - 0.5) * c.Grain.z * 1.1;
  }
  // The bloom only above the bright pass.
  float3 x = hdr * c.Scale.x + max(Blurred.SampleLevel(Linear, i.uv, 0).rgb * c.Scale.x - c.Bloom.y, 0) * c.Bloom.x;
  float L = dot(x, Luma);
  float3 col = lerp(L.xxx, x, c.Scale.y);
  float s = L * c.Shift.w;
  col = saturate(s) * (col - c.Shift.rgb * s) + c.Shift.rgb * s;
  col *= c.Correct.rgb * 2.0;
  return float4(saturate(col * pow(clamp(L, 1e-5, 1.0), c.Scale.z - 1.0)) * (1 - c.Grain.w), 1);
}
";

    private const int NoiseSize = 1024;
    private const int SlotLight = 0, SlotQuarter = 1, SlotBlurred = 2, SlotDepth = 3, SlotNoise = 4;
    private const int MappingDefault = 0 | (1 << 3) | (2 << 6) | (3 << 9) | (1 << 12);

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _down;
    private readonly ID3D12PipelineState _blur;
    private readonly ID3D12PipelineState _composite;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly uint _srvSize, _rtvSize;
    private ID3D12Resource? _light;
    private ID3D12Resource? _quarter;
    private ID3D12Resource? _blurred;
    private ID3D12Resource? _readback;
    private ID3D12Resource? _noise;
    private ID3D12Resource? _noiseUpload;
    private ID3D12Resource? _depthViewed;
    private uint _readbackPitch;
    private int _qw, _qh;
    private ulong _readbackMark;
    private bool _readbackPending;
    private bool _downDone;
    private ResourceStates _lightState = ResourceStates.RenderTarget;
    // The grain's offsets: random each frame as the game's, from a fixed seed (the same pictures each run).
    private readonly Random _random = new Random(4711);

    public ToneMapper(D3D12Device device, Format targetFormat)
    {
      _device = device;
      var d3d = device.Device;
      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, Constants), ShaderVisibility.Pixel),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)), ShaderVisibility.Pixel),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, 1, 0, 0)), ShaderVisibility.Pixel),
      };
      var samplers = new[]
      {
        new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
          MaxLOD = float.MaxValue,
        },
      };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters, samplers));
      var vs = Compile(DxcShaderStage.Vertex, "VSMain");
      GraphicsPipelineStateDescription Pipeline(string ps, Format format) => new GraphicsPipelineStateDescription
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
        SampleDescription = new SampleDescription(1, 0),
      };
      _down = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSDown", LightFormat));
      _blur = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSBlur", LightFormat));
      _composite = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSComposite", targetFormat));
      _srvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 5, DescriptorHeapFlags.ShaderVisible));
      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 3));
      _srvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      _rtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
      // Empty views until there are depth and grain textures (the composite's table always holds all four).
      d3d.CreateShaderResourceView(null, View(Format.R32_Float), SrvAt(SlotDepth));
      d3d.CreateShaderResourceView(null, View(Format.R8G8B8A8_UNorm), SrvAt(SlotNoise));
    }

    private static ShaderResourceViewDescription View(Format format) => new ShaderResourceViewDescription
    {
      Format = format,
      ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
      Shader4ComponentMapping = MappingDefault,
      Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
    };

    private CpuDescriptorHandle SrvAt(int slot) => _srvHeap.GetCPUDescriptorHandleForHeapStart() + slot * (int)_srvSize;

    private static byte[] Compile(DxcShaderStage stage, string entryPoint)
    {
      using IDxcResult result = DxcCompiler.Compile(stage, Shader, entryPoint);
      if (result.GetStatus().Failure)
      {
        throw new InvalidOperationException("HLSL " + entryPoint + ": " + result.GetErrors());
      }
      return result.GetObjectBytecodeArray();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The light target's view (draw the scene into it, between <see cref="BeginLight"/> and <see cref="EndLight"/>).</summary>
    public CpuDescriptorHandle LightView => _rtvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>The light target itself (RENDER_TARGET while drawing; MeshRenderer copies it for the fog's depth effect).</summary>
    public ID3D12Resource? LightTexture => _light;
    private CpuDescriptorHandle QuarterView => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)_rtvSize;
    private CpuDescriptorHandle BlurredView => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + 2 * (int)_rtvSize;

    /// <summary>The adapted luminance now (null before the first measure), and the last one measured.</summary>
    public float? Adapted { get; private set; }
    public float? Measured { get; private set; }

    /// <summary>The light target (and its quarter copies) at a size; made again when the size changes (the GPU done first).</summary>
    public void Resize(int width, int height)
    {
      if (width == Width && height == Height && _light != null)
      {
        return;
      }
      _device.WaitIdle();
      _light?.Dispose();
      _quarter?.Dispose();
      _blurred?.Dispose();
      _readback?.Dispose();
      Width = Math.Max(1, width);
      Height = Math.Max(1, height);
      _qw = Math.Max(1, Width >> 2);
      _qh = Math.Max(1, Height >> 2);
      var d3d = _device.Device;
      var clear = new ClearValue(LightFormat, new Vortice.Mathematics.Color4(0, 0, 0, 0));
      _light = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(LightFormat, (uint)Width, (uint)Height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget, clear);
      _quarter = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(LightFormat, (uint)_qw, (uint)_qh, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget, clear);
      _blurred = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(LightFormat, (uint)_qw, (uint)_qh, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget, clear);
      _readbackPitch = (uint)((_qw * 8 + 255) & ~255);
      _readback = d3d.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
        ResourceDescription.Buffer((ulong)_readbackPitch * (ulong)_qh), ResourceStates.CopyDest);
      _lightState = ResourceStates.RenderTarget;
      _readbackPending = false;
      _downDone = false;
      d3d.CreateRenderTargetView(_light, null, LightView);
      d3d.CreateRenderTargetView(_quarter, null, QuarterView);
      d3d.CreateRenderTargetView(_blurred, null, BlurredView);
      d3d.CreateShaderResourceView(_light, null, SrvAt(SlotLight));
      d3d.CreateShaderResourceView(_quarter, null, SrvAt(SlotQuarter));
      d3d.CreateShaderResourceView(_blurred, null, SrvAt(SlotBlurred));
    }

    /// <summary>Before the scene: the light target ready to draw into, cleared to no light.</summary>
    public void BeginLight(ID3D12GraphicsCommandList list)
    {
      if (_lightState != ResourceStates.RenderTarget)
      {
        list.ResourceBarrierTransition(_light!, _lightState, ResourceStates.RenderTarget);
        _lightState = ResourceStates.RenderTarget;
      }
      _downDone = false;
      list.ClearRenderTargetView(LightView, new Vortice.Mathematics.Color4(0, 0, 0, 0));
    }

    /// <summary>After the scene: the light target to be read.</summary>
    public void EndLight(ID3D12GraphicsCommandList list)
    {
      list.ResourceBarrierTransition(_light!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      _lightState = ResourceStates.PixelShaderResource;
    }

    private unsafe void Bind(ID3D12GraphicsCommandList list, ID3D12PipelineState pipeline, int texture, float* constants)
    {
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetPipelineState(pipeline);
      list.SetDescriptorHeaps(_srvHeap);
      list.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart() + texture * (int)_srvSize);
      list.SetGraphicsRootDescriptorTable(2, _srvHeap.GetGPUDescriptorHandleForHeapStart() + SlotQuarter * (int)_srvSize);
      list.SetGraphicsRoot32BitConstants(0, Constants, constants, 0);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    // The quarter copy of the light (once a frame).
    private unsafe void Down(ID3D12GraphicsCommandList list)
    {
      var constants = stackalloc float[Constants];
      constants[12] = 1f / Width;
      constants[13] = 1f / Height;
      list.OMSetRenderTargets(QuarterView);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(_qw, _qh));
      list.RSSetScissorRect(_qw, _qh);
      Bind(list, _down, SlotLight, constants);
      list.DrawInstanced(3, 1, 0, 0);
      _downDone = true;
    }

    /// <summary>
    /// The quarter copy made and copied into the readback buffer (when none is waiting to be read), the mark the GPU
    /// will have reached when it is there. After <see cref="EndLight"/>.
    /// </summary>
    public bool Measure(ID3D12GraphicsCommandList list)
    {
      if (_readbackPending)
      {
        return false;
      }
      if (!_downDone)
      {
        Down(list);
      }
      list.ResourceBarrierTransition(_quarter!, ResourceStates.RenderTarget, ResourceStates.CopySource);
      var footprint = new PlacedSubresourceFootPrint
      {
        Offset = 0,
        Footprint = new SubresourceFootPrint(LightFormat, (uint)_qw, (uint)_qh, 1, _readbackPitch),
      };
      list.CopyTextureRegion(new TextureCopyLocation(_readback!, footprint), 0, 0, 0, new TextureCopyLocation(_quarter!, 0), null);
      list.ResourceBarrierTransition(_quarter!, ResourceStates.CopySource, ResourceStates.RenderTarget);
      _readbackPending = true;
      _readbackMark = ulong.MaxValue;
      return true;
    }

    /// <summary>The mark after which the measure copy is done (set by the caller once the list is sent).</summary>
    public void MeasureSent(ulong mark)
    {
      if (_readbackPending && _readbackMark == ulong.MaxValue)
      {
        _readbackMark = mark;
      }
    }

    /// <summary>
    /// The measure read once the GPU is done with it (<paramref name="wait"/>: waits for it): the mean luminance of the
    /// quarter copy; null while not there yet.
    /// </summary>
    public unsafe float? ReadMeasure(bool wait)
    {
      if (!_readbackPending || _readbackMark == ulong.MaxValue)
      {
        return null;
      }
      if (wait)
      {
        _device.Wait(_readbackMark);
      }
      else if (!_device.IsDone(_readbackMark))
      {
        return null;
      }
      void* mapped;
      _readback!.Map(0, null, &mapped).CheckError();
      double sum = 0;
      try
      {
        for (int y = 0; y < _qh; y++)
        {
          var row = (Half*)((byte*)mapped + (long)y * _readbackPitch);
          for (int x = 0; x < _qw; x++)
          {
            float r = (float)row[x * 4], g = (float)row[x * 4 + 1], b = (float)row[x * 4 + 2];
            float l = r * 0.2125f + g * 0.7154f + b * 0.0721f;
            if (float.IsFinite(l))
            {
              sum += l;
            }
          }
        }
      }
      finally
      {
        _readback.Unmap(0, null);
      }
      _readbackPending = false;
      Measured = (float)(sum / (_qw * _qh));
      return Measured;
    }

    /// <summary>
    /// The eye's adaptation towards the last measure (the page's gamePostAdapt): while playing eased by the time
    /// passed over sqrt(2 x AdaptTime) seconds, else at once; kept within AdaptedLumMin and AdaptedLumMax.
    /// </summary>
    public float Adapt(ToneParams tone, bool playing, double seconds)
    {
      float target = Measured ?? Adapted ?? 1;
      float adapted = target;
      if (playing && Adapted is float before && tone.AdaptTime >= 1)
      {
        adapted = before + (target - before) * (float)Math.Min(seconds / Math.Sqrt(2 * tone.AdaptTime), 1);
      }
      adapted = Math.Clamp(adapted, tone.AdaptedLumMin, tone.AdaptedLumMax > 0 ? tone.AdaptedLumMax : float.MaxValue);
      Adapted = adapted;
      return adapted;
    }

    // The grain's texture as the game makes it (a 1024 x 1024 texture of random bytes, read filtered, clamped), once.
    private unsafe void MakeNoise(ID3D12GraphicsCommandList list)
    {
      var d3d = _device.Device;
      _noise = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
        ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, NoiseSize, NoiseSize, 1, 1), ResourceStates.CopyDest);
      int pitch = NoiseSize * 4;
      _noiseUpload = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
        ResourceDescription.Buffer((ulong)pitch * NoiseSize), ResourceStates.GenericRead);
      var bytes = new byte[pitch * NoiseSize];
      new Random(1024).NextBytes(bytes);
      void* mapped;
      _noiseUpload.Map(0, null, &mapped).CheckError();
      fixed (byte* source = bytes)
      {
        Buffer.MemoryCopy(source, mapped, bytes.Length, bytes.Length);
      }
      _noiseUpload.Unmap(0);
      var footprint = new PlacedSubresourceFootPrint
      {
        Offset = 0,
        Footprint = new SubresourceFootPrint(Format.R8G8B8A8_UNorm, NoiseSize, NoiseSize, 1, (uint)pitch),
      };
      list.CopyTextureRegion(new TextureCopyLocation(_noise, 0), 0, 0, 0, new TextureCopyLocation(_noiseUpload, footprint), null);
      list.ResourceBarrierTransition(_noise, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
      d3d.CreateShaderResourceView(_noise, View(Format.R8G8B8A8_UNorm), SrvAt(SlotNoise));
    }

    /// <summary>
    /// The composite into the target (after <see cref="EndLight"/>): the bloom blurred from the quarter copy, then the
    /// light through the depth of field, grain and tone mapping, faded. <paramref name="depth"/>: the frame's depth
    /// buffer (D32, in DepthWrite; read for the depth of field), or null.
    /// </summary>
    public unsafe void Composite(ID3D12GraphicsCommandList list, CpuDescriptorHandle target, int width, int height, ToneParams tone,
                                 PostParams post = default, ID3D12Resource? depth = null)
    {
      if (!_downDone)
      {
        Down(list);
      }
      var constants = stackalloc float[Constants];
      bool bloom = tone.Bloom > 0;
      if (bloom)
      {
        // Across into the second copy, down back into the first.
        constants[12] = 1f / _qw;
        constants[13] = 1f / _qh;
        list.RSSetViewport(new Vortice.Mathematics.Viewport(_qw, _qh));
        list.RSSetScissorRect(_qw, _qh);
        list.ResourceBarrierTransition(_quarter!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        list.OMSetRenderTargets(BlurredView);
        constants[26] = 1;
        constants[27] = 0;
        Bind(list, _blur, SlotQuarter, constants);
        list.DrawInstanced(3, 1, 0, 0);
        list.ResourceBarrierTransition(_blurred!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        list.ResourceBarrierTransition(_quarter!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
        list.OMSetRenderTargets(QuarterView);
        constants[26] = 0;
        constants[27] = 1;
        Bind(list, _blur, SlotBlurred, constants);
        list.DrawInstanced(3, 1, 0, 0);
        list.ResourceBarrierTransition(_blurred!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      }
      list.ResourceBarrierTransition(_quarter!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
      bool dof = post.DepthOfField && depth != null && post.FarClip > post.NearClip;
      if (dof)
      {
        if (!ReferenceEquals(depth, _depthViewed))
        {
          _device.Device.CreateShaderResourceView(depth, View(Format.R32_Float), SrvAt(SlotDepth));
          _depthViewed = depth;
        }
        list.ResourceBarrierTransition(depth!, ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);
      }
      if (tone.Grain > 0 && _noise == null)
      {
        MakeNoise(list);
      }

      float adapted = Adapted ?? Math.Max(tone.AdaptedLumMin, 1e-4f);
      constants[0] = tone.MidGrey / Math.Max(adapted, 1e-6f);
      constants[1] = tone.Saturation;
      constants[2] = tone.Gamma > 0 ? tone.Gamma : 1;
      var correct = tone.ColorCorrect == Vector3.Zero ? new Vector3(0.5f) : tone.ColorCorrect;
      constants[4] = correct.X;
      constants[5] = correct.Y;
      constants[6] = correct.Z;
      constants[8] = tone.ColorShift.X;
      constants[9] = tone.ColorShift.Y;
      constants[10] = tone.ColorShift.Z;
      constants[11] = 1f / (Vector3.Dot(tone.ColorShift, new Vector3(0.2125f, 0.7154f, 0.0721f)) + 0.001f);
      constants[12] = 1f / Width;
      constants[13] = 1f / Height;
      constants[16] = dof ? post.NearClip : 0;
      constants[17] = dof ? post.FarClip : 0;
      constants[18] = post.FocusNear;
      constants[19] = post.FocusFar;
      constants[20] = (float)_random.NextDouble();
      constants[21] = (float)_random.NextDouble();
      constants[22] = tone.Grain;
      constants[23] = Math.Clamp(post.Fade, 0, 1);
      constants[24] = bloom ? tone.Bloom * 0.25f : 0;
      constants[25] = tone.BrightPass;
      constants[26] = constants[27] = 0;
      list.OMSetRenderTargets(target);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      Bind(list, _composite, SlotLight, constants);
      list.DrawInstanced(3, 1, 0, 0);
      list.ResourceBarrierTransition(_quarter!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
      if (dof)
      {
        list.ResourceBarrierTransition(depth!, ResourceStates.PixelShaderResource, ResourceStates.DepthWrite);
      }
    }

    public void Dispose()
    {
      _device.WaitIdle();
      _light?.Dispose();
      _quarter?.Dispose();
      _blurred?.Dispose();
      _readback?.Dispose();
      _noise?.Dispose();
      _noiseUpload?.Dispose();
      _srvHeap.Dispose();
      _rtvHeap.Dispose();
      _down.Dispose();
      _blur.Dispose();
      _composite.Dispose();
      _rootSignature.Dispose();
    }
  }
}
