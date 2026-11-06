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

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// Lines over the finished picture (after the tone mapping): the cutscene view's camera in the free view, as the page's
  /// drawFilmCamera (ModelView.html ~4015-4058). Drawn twice: faint without the depth test (where something hides
  /// them), then in full colour against the scene's depth (LESSEQUAL, not written).
  /// </summary>
  public sealed class LineOverlay : IDisposable
  {
    private const string Shader = @"
struct Constants
{
  row_major float4x4 ViewProjection;
  float4 Color;
};
ConstantBuffer<Constants> c : register(b0);

float4 VSMain(float3 position : POSITION) : SV_Position
{
  return mul(float4(position, 1), c.ViewProjection);
}

float4 PSMain() : SV_Target
{
  return c.Color;
}

// The background where nothing was drawn (a full-screen triangle at depth 1, tested EQUAL against the cleared depth):
// row 0 of the constants its top colour, row 1 its bottom colour, row 2's x the picture's height.
float4 VSBackground(uint id : SV_VertexID) : SV_Position
{
  float2 uv = float2((id << 1) & 2, id & 2);
  return float4(uv * float2(2, -2) + float2(-1, 1), 1, 1);
}

float4 PSBackground(float4 position : SV_Position) : SV_Target
{
  return float4(lerp(c.ViewProjection[0].rgb, c.ViewProjection[1].rgb, saturate(position.y / c.ViewProjection[2].x)), 1);
}
";

    // The page's colours: (0.45, 0.32, 0.12) where hidden, (1.0, 0.62, 0.15) where seen.
    private static readonly Vector4 HiddenColor = new Vector4(0.45f, 0.32f, 0.12f, 1);
    private static readonly Vector4 SeenColor = new Vector4(1.0f, 0.62f, 0.15f, 1);

    // The buffers in flight: one per frame of the swap chain's latency and a spare.
    private const int Slots = 4;

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _hidden;
    private readonly ID3D12PipelineState _seen;
    private readonly ID3D12Resource?[] _buffers = new ID3D12Resource?[Slots];
    private readonly int[] _capacity = new int[Slots];
    private int _slot;

    public LineOverlay(D3D12Device device, Format targetFormat, Format depthFormat)
    {
      _device = device;
      var d3d = device.Device;
      var parameters = new[] { new RootParameter1(new RootConstants(0, 0, 20), ShaderVisibility.All) };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters));
      var vs = Compile(DxcShaderStage.Vertex, "VSMain");
      var ps = Compile(DxcShaderStage.Pixel, "PSMain");
      GraphicsPipelineStateDescription Pipeline(DepthStencilDescription depth) => new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = vs,
        PixelShader = ps,
        InputLayout = new InputLayoutDescription(new[] { new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0) }),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Line,
        RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid),
        BlendState = BlendDescription.Opaque,
        DepthStencilState = depth,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = depthFormat,
        SampleDescription = new SampleDescription(1, 0),
      };
      _hidden = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(DepthStencilDescription.None));
      _seen = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(DepthStencilDescription.Read with { DepthFunc = ComparisonFunction.LessEqual }));
      var background = Pipeline(DepthStencilDescription.Read with { DepthFunc = ComparisonFunction.Equal });
      background.VertexShader = Compile(DxcShaderStage.Vertex, "VSBackground");
      background.PixelShader = Compile(DxcShaderStage.Pixel, "PSBackground");
      background.InputLayout = default;
      background.PrimitiveTopologyType = PrimitiveTopologyType.Triangle;
      _background = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(background);
    }

    private readonly ID3D12PipelineState _background;

    /// <summary>
    /// The view's background where nothing was drawn (the page's body background, ModelView.html ~24-27): 0 the gradient
    /// #68788a to #22252a down the view, 1 grey (#808080), 2 black, 3 white.
    /// </summary>
    public unsafe void DrawBackground(ID3D12GraphicsCommandList list, CpuDescriptorHandle target, CpuDescriptorHandle depth, int width, int height, int kind)
    {
      Vector3 top, bottom;
      switch (kind)
      {
        case 1:
          top = bottom = new Vector3(128 / 255f);
          break;
        case 2:
          top = bottom = Vector3.Zero;
          break;
        case 3:
          top = bottom = Vector3.One;
          break;
        default:
          top = new Vector3(0x68, 0x78, 0x8a) / 255f;
          bottom = new Vector3(0x22, 0x25, 0x2a) / 255f;
          break;
      }
      var constants = stackalloc float[20];
      for (int k = 0; k < 20; k++)
      {
        constants[k] = 0;
      }
      *(Vector3*)constants = top;
      *(Vector3*)(constants + 4) = bottom;
      constants[8] = height;
      list.OMSetRenderTargets(target, depth);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetGraphicsRootSignature(_rootSignature);
      list.SetPipelineState(_background);
      list.SetGraphicsRoot32BitConstants(0, 20, constants, 0);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      list.DrawInstanced(3, 1, 0, 0);
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

    /// <summary>
    /// Draws <paramref name="lines"/> (pairs of points in the scene) into <paramref name="target"/>, tested against
    /// <paramref name="depth"/> (the scene's depth, DEPTH_WRITE).
    /// </summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, CpuDescriptorHandle target, CpuDescriptorHandle depth, int width, int height,
                            Matrix4x4 viewProjection, IReadOnlyList<Vector3> lines)
    {
      int count = lines.Count & ~1;
      if (count == 0)
      {
        return;
      }
      _slot = (_slot + 1) % Slots;
      if (_buffers[_slot] == null || _capacity[_slot] < count)
      {
        _buffers[_slot]?.Dispose();
        _capacity[_slot] = Math.Max(count, 64);
        _buffers[_slot] = _device.Device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
          ResourceDescription.Buffer((ulong)_capacity[_slot] * 12), ResourceStates.GenericRead);
      }
      var buffer = _buffers[_slot]!;
      void* mapped;
      buffer.Map(0, null, &mapped).CheckError();
      var points = (Vector3*)mapped;
      for (int i = 0; i < count; i++)
      {
        points[i] = lines[i];
      }
      buffer.Unmap(0);

      list.OMSetRenderTargets(target, depth);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      list.SetGraphicsRootSignature(_rootSignature);
      list.IASetPrimitiveTopology(PrimitiveTopology.LineList);
      list.IASetVertexBuffers(0, new VertexBufferView(buffer.GPUVirtualAddress, (uint)(count * 12), 12));
      var constants = stackalloc float[20];
      *(Matrix4x4*)constants = viewProjection;
      foreach (var (pipeline, color) in new[] { (_hidden, HiddenColor), (_seen, SeenColor) })
      {
        *(Vector4*)(constants + 16) = color;
        list.SetPipelineState(pipeline);
        list.SetGraphicsRoot32BitConstants(0, 20, constants, 0);
        list.DrawInstanced((uint)count, 1, 0, 0);
      }
    }

    /// <summary>
    /// The page's camera as lines (drawFilmCamera): a body 12 by 10 cm and 20 cm deep behind the eye, the edges of what it
    /// sees (its vertical field of view at 16:9, 60 cm long) and a triangle above the top edge for which way is up.
    /// </summary>
    public static List<Vector3> CameraLines(Vector3 eye, Vector3 forward, Vector3 up, float fov)
    {
      var f = Vector3.Normalize(forward);
      var s = Vector3.Normalize(Vector3.Cross(f, up));
      var u = Vector3.Normalize(Vector3.Cross(s, f));
      Vector3 At(float a, float b, float d) => eye + s * a + u * b + f * d;
      const float L = 0.6f;
      float h = L * MathF.Tan(fov / 2), w = h * 16 / 9;
      var lines = new List<Vector3>(64);
      void Line(Vector3 p, Vector3 q)
      {
        lines.Add(p);
        lines.Add(q);
      }
      var box = new List<Vector3>(8);
      foreach (float d in new[] { 0f, -0.2f })
      {
        foreach (var (a, b) in new[] { (-0.06f, -0.05f), (0.06f, -0.05f), (0.06f, 0.05f), (-0.06f, 0.05f) })
        {
          box.Add(At(a, b, d));
        }
      }
      for (int i = 0; i < 4; i++)
      {
        Line(box[i], box[(i + 1) % 4]);
        Line(box[4 + i], box[4 + (i + 1) % 4]);
        Line(box[i], box[4 + i]);
      }
      var corners = new[] { At(-w, -h, L), At(w, -h, L), At(w, h, L), At(-w, h, L) };
      for (int i = 0; i < 4; i++)
      {
        Line(eye, corners[i]);
        Line(corners[i], corners[(i + 1) % 4]);
      }
      var top = new[] { At(-w * 0.3f, h * 1.08f, L), At(w * 0.3f, h * 1.08f, L), At(0, h * 1.45f, L) };
      Line(top[0], top[1]);
      Line(top[1], top[2]);
      Line(top[2], top[0]);
      return lines;
    }

    public void Dispose()
    {
      _device.WaitIdle();
      foreach (var b in _buffers)
      {
        b?.Dispose();
      }
      _hidden.Dispose();
      _seen.Dispose();
      _background.Dispose();
      _rootSignature.Dispose();
    }
  }
}
