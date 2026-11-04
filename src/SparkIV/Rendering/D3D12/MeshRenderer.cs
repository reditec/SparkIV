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
  /// The native renderer's drawing so far (#112 steps 1–2): models (<see cref="ModelScene"/>) on the GPU, each in one
  /// vertex and one index buffer, drawn as instances (a model-to-scene matrix each) with position and normal only:
  /// Lambert and ambient, no textures. Meshes of hidden materials are left out. The view-projection, the instance's
  /// matrix and the light go as root constants. Draws into any target (the panel's swap chain, an offscreen texture).
  /// No WinUI here: the visual tests' cutgen builds it too.
  /// </summary>
  public sealed class MeshRenderer : IDisposable
  {
    private const string Shader = @"
struct Constants
{
  row_major float4x4 ViewProjection;
  row_major float4x4 World;
  float4 Light; // xyz: towards the light, w: ambient
};
ConstantBuffer<Constants> c : register(b0);

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; };

VsOut VSMain(VsIn input)
{
  VsOut output;
  float4 world = mul(float4(input.position, 1), c.World);
  output.position = mul(world, c.ViewProjection);
  output.normal = mul(input.normal, (float3x3)c.World);
  return output;
}

float4 PSMain(VsOut input, bool front : SV_IsFrontFace) : SV_Target
{
  float3 n = normalize(input.normal);
  float diffuse = abs(dot(n, normalize(c.Light.xyz)));
  float3 albedo = float3(0.72, 0.70, 0.66);
  return float4(albedo * (c.Light.w + (1 - c.Light.w) * diffuse), 1);
}
";

    private const int ConstantCount = 16 + 16 + 4;

    private sealed class Model : IDisposable
    {
      public ID3D12Resource Vertices = null!;
      public ID3D12Resource Indices = null!;
      public VertexBufferView VertexView;
      public IndexBufferView IndexView;
      public uint IndexCount;
      public Vector3 Min, Max;

      public void Dispose()
      {
        Vertices.Dispose();
        Indices.Dispose();
      }
    }

    private readonly D3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipeline;
    private readonly List<Model?> _models = new List<Model?>();
    private readonly List<(int Model, Matrix4x4 World)> _instances = new List<(int, Matrix4x4)>();

    public MeshRenderer(D3D12Device device, Format targetFormat)
    {
      _device = device;
      var d3d = device.Device;

      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
      };
      _rootSignature = d3d.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters));

      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
      };
      var description = new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = Compile(DxcShaderStage.Vertex, "VSMain"),
        PixelShader = Compile(DxcShaderStage.Pixel, "PSMain"),
        InputLayout = new InputLayoutDescription(input),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        // Both faces for now (lit from either side): which way GTA IV's triangles wind is for the port (step 4).
        RasterizerState = RasterizerDescription.CullNone,
        BlendState = BlendDescription.Opaque,
        DepthStencilState = DepthStencilDescription.Default,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      };
      _pipeline = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(description);
    }

    /// <summary>The number of instances and of the triangles they draw.</summary>
    public int InstanceCount => _instances.Count;
    public long Triangles => _instances.Sum(i => (long)(_models[i.Model]?.IndexCount ?? 0) / 3);

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
    /// Models onto the GPU, all copies in one command list (the GPU done before returning): per model its meshes'
    /// positions and normals in one vertex buffer and their indices (32-bit) in one index buffer. Returns each
    /// model's number for <see cref="AddInstance"/>; a model with nothing to draw still gets one (drawing nothing).
    /// </summary>
    public int[] AddModels(IReadOnlyList<ModelScene> scenes)
    {
      var d3d = _device.Device;
      var numbers = new int[scenes.Count];
      var uploads = new List<ID3D12Resource>();
      using var allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      for (int s = 0; s < scenes.Count; s++)
      {
        numbers[s] = _models.Count;
        _models.Add(Build(scenes[s], list, uploads));
      }
      list.Close();
      _device.Queue.ExecuteCommandList(list);
      _device.WaitIdle();
      foreach (var upload in uploads)
      {
        upload.Dispose();
      }
      return numbers;
    }

    private Model? Build(ModelScene scene, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      var hidden = new HashSet<int>(scene.Materials.Where(m => m.Hidden).Select(m => m.Id));
      var vertices = new List<float>();
      var indices = new List<uint>();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      foreach (var mesh in scene.Meshes)
      {
        if (hidden.Contains(mesh.Material) || mesh.Indices.Length == 0)
        {
          continue;
        }
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
          bool normal = mesh.Normals != null && mesh.Normals.Length >= v * 3 + 3;
          vertices.Add(normal ? mesh.Normals![v * 3] : 0);
          vertices.Add(normal ? mesh.Normals![v * 3 + 1] : 0);
          vertices.Add(normal ? mesh.Normals![v * 3 + 2] : 1);
        }
        foreach (ushort index in mesh.Indices)
        {
          indices.Add(first + index);
        }
      }
      if (indices.Count == 0)
      {
        return null;
      }
      var model = new Model { Min = min, Max = max, IndexCount = (uint)indices.Count };
      model.Vertices = Upload(vertices.ToArray(), list, uploads, out ulong vertexBytes);
      model.Indices = Upload(indices.ToArray(), list, uploads, out ulong indexBytes);
      model.VertexView = new VertexBufferView(model.Vertices.GPUVirtualAddress, (uint)vertexBytes, 24);
      model.IndexView = new IndexBufferView(model.Indices.GPUVirtualAddress, (uint)indexBytes, Format.R32_UInt);
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

    /// <summary>A model drawn with a model-to-scene matrix.</summary>
    public void AddInstance(int model, Matrix4x4 world) => _instances.Add((model, world));

    /// <summary>A model's box (its own space), or null when it draws nothing.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds(int model) =>
      model >= 0 && model < _models.Count && _models[model] is Model m ? (m.Min, m.Max) : null;

    /// <summary>All models and instances gone (the GPU done with them first).</summary>
    public void Clear()
    {
      _device.WaitIdle();
      foreach (var model in _models)
      {
        model?.Dispose();
      }
      _models.Clear();
      _instances.Clear();
    }

    /// <summary>Clears the target and draws every instance as seen with the view-projection.</summary>
    public unsafe void Draw(ID3D12GraphicsCommandList list, CpuDescriptorHandle renderTarget, CpuDescriptorHandle depth,
                            int width, int height, Matrix4x4 viewProjection, Vector3 toLight, float ambient)
    {
      list.OMSetRenderTargets(renderTarget, depth);
      list.ClearRenderTargetView(renderTarget, new Vortice.Mathematics.Color4(0.16f, 0.17f, 0.19f, 1f));
      list.ClearDepthStencilView(depth, ClearFlags.Depth, 1.0f, 0);
      list.RSSetViewport(new Vortice.Mathematics.Viewport(width, height));
      list.RSSetScissorRect(width, height);
      if (_instances.Count == 0)
      {
        return;
      }

      var constants = stackalloc float[ConstantCount];
      *(Matrix4x4*)constants = viewProjection;
      var light = Vector3.Normalize(toLight);
      constants[32] = light.X;
      constants[33] = light.Y;
      constants[34] = light.Z;
      constants[35] = ambient;

      list.SetGraphicsRootSignature(_rootSignature);
      list.SetPipelineState(_pipeline);
      list.SetGraphicsRoot32BitConstants(0, 32 + 4, constants, 0);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
      int last = -1;
      foreach (var (number, world) in _instances)
      {
        if (_models[number] is not Model model)
        {
          continue;
        }
        if (number != last)
        {
          list.IASetVertexBuffers(0, model.VertexView);
          list.IASetIndexBuffer(model.IndexView);
          last = number;
        }
        var w = world;
        list.SetGraphicsRoot32BitConstants(0, 16, &w, 16);
        list.DrawIndexedInstanced(model.IndexCount, 1, 0, 0, 0);
      }
    }

    public void Dispose()
    {
      Clear();
      _pipeline.Dispose();
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
