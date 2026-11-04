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
  /// The frame's light into its picture (#112 step 4c), as the page's post composite (gtaiv-pc.js, after GTA IV's
  /// rage_postfx): the scene is drawn into a float light target (R16G16B16A16); a quarter-size copy (each texel the mean
  /// of four bilinear reads, its 4 x 4 block) is read back for the mean luminance, which the eye adapts to (kept
  /// within AdaptedLumMin and AdaptedLumMax, eased over AdaptTime while playing); the composite then scales the light
  /// by MidGrey / the adapted luminance and applies saturation, colour shift, colour correction and gamma on the
  /// luminance into the target. No bloom, depth of field or grain yet (step 4h).
  /// </summary>
  public sealed class ToneMapper : IDisposable
  {
    public const Format LightFormat = Format.R16G16B16A16_Float;

    private const string Shader = @"
struct Constants
{
  float4 Scale;      // x: tone scale, y: saturation, z: gamma, w: unused
  float4 Correct;    // rgb: colour correction (x 2 in the shader)
  float4 Shift;      // rgb: colour shift, w: 1 / (its luminance + 0.001)
  float4 Texel;      // xy: 1 / the light's size
};
ConstantBuffer<Constants> c : register(b0);
Texture2D Light : register(t0);
SamplerState Linear : register(s0);

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

// The composite's tone part (the page's gamePostShader composite, without bloom, DOF and grain).
float4 PSComposite(VsOut i) : SV_Target
{
  float3 x = LightAt(i.uv) * c.Scale.x;
  float L = dot(x, float3(0.2125, 0.7154, 0.0721));
  float3 col = lerp(L.xxx, x, c.Scale.y);
  float s = L * c.Shift.w;
  col = saturate(s) * (col - c.Shift.rgb * s) + c.Shift.rgb * s;
  col *= c.Correct.rgb * 2.0;
  return float4(saturate(col * pow(clamp(L, 1e-5, 1.0), c.Scale.z - 1.0)), 1);
}
";

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _down;
    private readonly ID3D12PipelineState _composite;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly uint _srvSize, _rtvSize;
    private ID3D12Resource? _light;
    private ID3D12Resource? _quarter;
    private ID3D12Resource? _readback;
    private uint _readbackPitch;
    private int _qw, _qh;
    private ulong _readbackMark;
    private bool _readbackPending;
    private ResourceStates _lightState = ResourceStates.RenderTarget;

    public ToneMapper(D3D12Device device, Format targetFormat)
    {
      _device = device;
      var d3d = device.Device;
      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, 16), ShaderVisibility.Pixel),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)), ShaderVisibility.Pixel),
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
      _composite = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline("PSComposite", targetFormat));
      _srvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
      _rtvHeap = d3d.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2));
      _srvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
      _rtvSize = d3d.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
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

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The light target's view (draw the scene into it, between <see cref="BeginLight"/> and <see cref="EndLight"/>).</summary>
    public CpuDescriptorHandle LightView => _rtvHeap.GetCPUDescriptorHandleForHeapStart();
    private CpuDescriptorHandle QuarterView => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)_rtvSize;

    /// <summary>The adapted luminance now (null before the first measure), and the last one measured.</summary>
    public float? Adapted { get; private set; }
    public float? Measured { get; private set; }

    /// <summary>The light target (and its quarter copy) at a size; made again when the size changes (the GPU done first).</summary>
    public void Resize(int width, int height)
    {
      if (width == Width && height == Height && _light != null)
      {
        return;
      }
      _device.WaitIdle();
      _light?.Dispose();
      _quarter?.Dispose();
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
      _readbackPitch = (uint)((_qw * 8 + 255) & ~255);
      _readback = d3d.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
        ResourceDescription.Buffer((ulong)_readbackPitch * (ulong)_qh), ResourceStates.CopyDest);
      _lightState = ResourceStates.RenderTarget;
      _readbackPending = false;
      d3d.CreateRenderTargetView(_light, null, LightView);
      d3d.CreateRenderTargetView(_quarter, null, QuarterView);
      d3d.CreateShaderResourceView(_light, null, _srvHeap.GetCPUDescriptorHandleForHeapStart());
      d3d.CreateShaderResourceView(_quarter, null, _srvHeap.GetCPUDescriptorHandleForHeapStart() + (int)_srvSize);
    }

    /// <summary>Before the scene: the light target ready to draw into, cleared to no light.</summary>
    public void BeginLight(ID3D12GraphicsCommandList list)
    {
      if (_lightState != ResourceStates.RenderTarget)
      {
        list.ResourceBarrierTransition(_light!, _lightState, ResourceStates.RenderTarget);
        _lightState = ResourceStates.RenderTarget;
      }
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
      list.SetGraphicsRoot32BitConstants(0, 16, constants, 0);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    /// <summary>
    /// The quarter copy made and copied into the readback buffer (when none is waiting to be read), the mark the GPU
    /// will have reached when it is there. After <see cref="EndLight"/>.
    /// </summary>
    public unsafe bool Measure(ID3D12GraphicsCommandList list)
    {
      if (_readbackPending)
      {
        return false;
      }
      var constants = stackalloc float[16];
      constants[12] = 1f / Width;
      constants[13] = 1f / Height;
      list.OMSetRenderTargets(QuarterView);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(_qw, _qh));
      list.RSSetScissorRect(_qw, _qh);
      Bind(list, _down, 0, constants);
      list.DrawInstanced(3, 1, 0, 0);
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

    /// <summary>The composite into the target (after <see cref="EndLight"/>): the light tone mapped by the adapted luminance.</summary>
    public unsafe void Composite(ID3D12GraphicsCommandList list, CpuDescriptorHandle target, int width, int height, ToneParams tone)
    {
      float adapted = Adapted ?? Math.Max(tone.AdaptedLumMin, 1e-4f);
      var constants = stackalloc float[16];
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
      list.OMSetRenderTargets(target);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      Bind(list, _composite, 0, constants);
      list.DrawInstanced(3, 1, 0, 0);
    }

    public void Dispose()
    {
      _device.WaitIdle();
      _light?.Dispose();
      _quarter?.Dispose();
      _readback?.Dispose();
      _srvHeap.Dispose();
      _rtvHeap.Dispose();
      _down.Dispose();
      _composite.Dispose();
      _rootSignature.Dispose();
    }
  }
}
