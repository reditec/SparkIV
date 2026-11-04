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
  /// The prototype's drawing (#112 step 1): the meshes of one <see cref="ModelScene"/> in one vertex and one index
  /// buffer on the GPU, drawn with position and normal only (Lambert and ambient, no textures) by a camera going
  /// slowly round the model (Z up, as in GTA). The view-projection matrix and the light go as root constants.
  /// </summary>
  public sealed class MeshRenderer : IDisposable
  {
    private const string Shader = @"
struct Constants
{
  row_major float4x4 ViewProjection;
  float4 Light; // xyz: towards the light, w: ambient
};
ConstantBuffer<Constants> c : register(b0);

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; };

VsOut VSMain(VsIn input)
{
  VsOut output;
  output.position = mul(float4(input.position, 1), c.ViewProjection);
  output.normal = input.normal;
  return output;
}

float4 PSMain(VsOut input) : SV_Target
{
  float3 n = normalize(input.normal);
  float diffuse = saturate(dot(n, normalize(c.Light.xyz)));
  float3 albedo = float3(0.72, 0.70, 0.66);
  return float4(albedo * (c.Light.w + (1 - c.Light.w) * diffuse), 1);
}
";

    private const int ConstantCount = 16 + 4;

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipeline;
    private ID3D12Resource? _vertexBuffer;
    private ID3D12Resource? _indexBuffer;
    private VertexBufferView _vertexView;
    private IndexBufferView _indexView;
    private uint _indexCount;
    private Vector3 _center;
    private float _radius = 1;

    public MeshRenderer(D3D12Device device)
    {
      _device = device;
      var d3d = device.Device;

      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
      };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters));

      var vertexShader = Compile(DxcShaderStage.Vertex, "VSMain");
      var pixelShader = Compile(DxcShaderStage.Pixel, "PSMain");
      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
      };
      var description = new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = vertexShader,
        PixelShader = pixelShader,
        InputLayout = new InputLayoutDescription(input),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        // The prototype draws both sides: which way GTA IV's triangles wind is for the port (step 4).
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.Opaque,
        DepthStencilState = DepthStencilDescription.Default,
        RenderTargetFormats = new[] { PanelSwapChain.BackBufferFormat },
        DepthStencilFormat = PanelSwapChain.DepthFormat,
        SampleDescription = new SampleDescription(1, 0),
      };
      _pipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(description);
    }

    /// <summary>Whether a model has been given (<see cref="SetModel"/>).</summary>
    public bool HasModel => _indexCount > 0;

    /// <summary>The number of triangles drawn.</summary>
    public uint Triangles => _indexCount / 3;

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
    /// The meshes of a model onto the GPU (on the render thread): positions and normals in one vertex buffer, the
    /// indices (32-bit, the meshes one after the other) in one index buffer, both copied into default heaps.
    /// </summary>
    public void SetModel(ModelScene scene)
    {
      var vertices = new List<float>();
      var indices = new List<uint>();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      foreach (var mesh in scene.Meshes)
      {
        uint first = (uint)(vertices.Count / 6);
        int count = mesh.Positions.Length / 3;
        for (int v = 0; v < count; v++)
        {
          var p = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
          min = Vector3.Min(min, p);
          max = Vector3.Max(max, p);
          vertices.Add(p.X);
          vertices.Add(p.Y);
          vertices.Add(p.Z);
          if (mesh.Normals != null)
          {
            vertices.Add(mesh.Normals[v * 3]);
            vertices.Add(mesh.Normals[v * 3 + 1]);
            vertices.Add(mesh.Normals[v * 3 + 2]);
          }
          else
          {
            vertices.Add(0);
            vertices.Add(0);
            vertices.Add(1);
          }
        }
        foreach (ushort index in mesh.Indices)
        {
          indices.Add(first + index);
        }
      }
      if (indices.Count == 0)
      {
        return;
      }
      _center = (min + max) * 0.5f;
      _radius = Math.Max(0.1f, (max - min).Length() * 0.5f);

      // Frames still on the GPU may use the old buffers.
      _device.WaitIdle();
      ReleaseBuffers();
      _vertexBuffer = Upload(vertices.ToArray(), out ulong vertexBytes);
      _indexBuffer = Upload(indices.ToArray(), out ulong indexBytes);
      _vertexView = new VertexBufferView(_vertexBuffer.GPUVirtualAddress, (uint)vertexBytes, 24);
      _indexView = new IndexBufferView(_indexBuffer.GPUVirtualAddress, (uint)indexBytes, Format.R32_UInt);
      _indexCount = (uint)indices.Count;
    }

    /// <summary>An array into a new buffer in a default heap, over an upload buffer (the GPU done before returning).</summary>
    private unsafe ID3D12Resource Upload<T>(T[] data, out ulong bytes) where T : unmanaged
    {
      var d3d = _device.Device;
      bytes = (ulong)(data.Length * sizeof(T));
      var buffer = d3d.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                                               ResourceDescription.Buffer(bytes), ResourceStates.Common);
      using var upload = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                                     ResourceDescription.Buffer(bytes), ResourceStates.GenericRead);
      void* mapped;
      upload.Map(0, null, &mapped).CheckError();
      fixed (T* source = data)
      {
        Buffer.MemoryCopy(source, mapped, (long)bytes, (long)bytes);
      }
      upload.Unmap(0);

      using var allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      list.ResourceBarrierTransition(buffer, ResourceStates.Common, ResourceStates.CopyDest);
      list.CopyBufferRegion(buffer, 0, upload, 0, bytes);
      list.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.VertexAndConstantBuffer | ResourceStates.IndexBuffer);
      list.Close();
      _device.Queue.ExecuteCommandList(list);
      _device.WaitIdle();
      return buffer;
    }

    /// <summary>Clears the frame and draws the model, seen from a camera at an angle (radians) round it.</summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, PanelSwapChain target, float angle)
    {
      list.OMSetRenderTargets(target.RenderTargetView, target.DepthView);
      list.ClearRenderTargetView(target.RenderTargetView, new Vortice.Mathematics.Color4(0.16f, 0.17f, 0.19f, 1f));
      list.ClearDepthStencilView(target.DepthView, ClearFlags.Depth, 1.0f, 0);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(target.Width, target.Height));
      list.RSSetScissorRect(target.Width, target.Height);
      if (!HasModel)
      {
        return;
      }

      float distance = _radius * 2.6f;
      var eye = _center + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, _radius * 0.6f);
      var view = Matrix4x4.CreateLookAt(eye, _center, Vector3.UnitZ);
      var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, target.Width / (float)target.Height,
                                                              Math.Max(0.01f, distance - _radius * 2), distance + _radius * 2);
      var constants = stackalloc float[ConstantCount];
      *(Matrix4x4*)constants = view * projection;
      var light = Vector3.Normalize(new Vector3(0.4f, -0.5f, 0.8f));
      constants[16] = light.X;
      constants[17] = light.Y;
      constants[18] = light.Z;
      constants[19] = 0.35f;

      list.SetGraphicsRootSignature(_rootSignature);
      list.SetPipelineState(_pipeline);
      list.SetGraphicsRoot32BitConstants(0, ConstantCount, constants, 0);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      list.IASetVertexBuffers(0, _vertexView);
      list.IASetIndexBuffer(_indexView);
      list.DrawIndexedInstanced(_indexCount, 1, 0, 0, 0);
    }

    private void ReleaseBuffers()
    {
      _vertexBuffer?.Dispose();
      _indexBuffer?.Dispose();
      _vertexBuffer = _indexBuffer = null;
      _indexCount = 0;
    }

    public void Dispose()
    {
      ReleaseBuffers();
      _pipeline.Dispose();
      _rootSignature.Dispose();
    }
  }
}
