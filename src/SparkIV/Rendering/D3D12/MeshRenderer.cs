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
  /// The native renderer's drawing so far (#112 steps 1–3): models (<see cref="ModelScene"/>) on the GPU, each in one
  /// vertex and one index buffer with a part per mesh (its index range, its material's texture and how it is drawn),
  /// drawn as instances (a model-to-scene matrix each). First everything solid (cut-out texels clipped), then what
  /// blends over it (see-through materials, decals a little in front, dirt decals by their texture's dirt mask),
  /// without writing depth. Lit by Lambert and an ambient term; only the first texture layer; glass and hidden
  /// planes are left out for now. The view-projection, the instance's matrix, the light and the part's material go
  /// as root constants, its texture as a descriptor table. Draws into any target. No WinUI here: the visual tests'
  /// cutgen builds it too.
  /// </summary>
  public sealed class MeshRenderer : IDisposable
  {
    private const string Shader = @"
struct Constants
{
  row_major float4x4 ViewProjection;
  row_major float4x4 World;
  float4 Light;    // xyz: towards the light, w: ambient
  float4 Material; // x: cut out (1), y: alpha from the dirt mask (1)
  float4 DirtMask;
};
ConstantBuffer<Constants> c : register(b0);
Texture2D Albedo : register(t0);
SamplerState Linear : register(s0);

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; };

VsOut VSMain(VsIn input)
{
  VsOut output;
  float4 world = mul(float4(input.position, 1), c.World);
  output.position = mul(world, c.ViewProjection);
  output.normal = mul(input.normal, (float3x3)c.World);
  output.uv = input.uv;
  output.alpha = input.alpha;
  return output;
}

float4 PSMain(VsOut input) : SV_Target
{
  float4 texel = Albedo.Sample(Linear, input.uv);
  float alpha = texel.a * input.alpha;
  float3 albedo = texel.rgb;
  if (c.Material.y > 0.5)
  {
    alpha = dot(texel.rgb, c.DirtMask.rgb) * input.alpha;
    albedo = float3(0.23, 0.22, 0.2);
  }
  if (c.Material.x > 0.5)
  {
    clip(alpha - 0.5);
  }
  float3 n = normalize(input.normal);
  float diffuse = abs(dot(n, normalize(c.Light.xyz)));
  return float4(albedo * (c.Light.w + (1 - c.Light.w) * diffuse), alpha);
}
";

    private const int ConstantCount = 16 + 16 + 4 + 4 + 4;
    private const int VertexFloats = 9;

    private enum Pass : byte { Solid, Blend, Decal }

    private readonly struct Part
    {
      public Part(uint start, uint count, int texture, Pass pass, bool cutout, Vector3? dirtMask)
      {
        Start = start;
        Count = count;
        Texture = texture;
        Pass = pass;
        Cutout = cutout;
        DirtMask = dirtMask;
      }

      public uint Start { get; }
      public uint Count { get; }
      public int Texture { get; }
      public Pass Pass { get; }
      public bool Cutout { get; }
      public Vector3? DirtMask { get; }
    }

    private sealed class Model : IDisposable
    {
      public ID3D12Resource Vertices = null!;
      public ID3D12Resource Indices = null!;
      public VertexBufferView VertexView;
      public IndexBufferView IndexView;
      public uint IndexCount;
      public Part[] Parts = Array.Empty<Part>();
      public Vector3 Min, Max;

      public void Dispose()
      {
        Vertices.Dispose();
        Indices.Dispose();
      }
    }

    private readonly D3D12Device _device;
    private readonly TextureCache _textures;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _solid;
    private readonly ID3D12PipelineState _blend;
    private readonly ID3D12PipelineState _decal;
    private readonly List<Model?> _models = new List<Model?>();
    private readonly List<(int Model, Matrix4x4 World)> _instances = new List<(int, Matrix4x4)>();

    public MeshRenderer(D3D12Device device, Format targetFormat)
    {
      _device = device;
      _textures = new TextureCache(device);
      var d3d = device.Device;

      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)), ShaderVisibility.Pixel),
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
      var pixelShader = Compile(DxcShaderStage.Pixel, "PSMain");
      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        new InputElementDescription("ALPHA", 0, Format.R32_Float, 32, 0),
      };
      GraphicsPipelineStateDescription Pipeline(BlendDescription blend, DepthStencilDescription depth, RasterizerDescription rasterizer) => new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = vertexShader,
        PixelShader = pixelShader,
        InputLayout = new InputLayoutDescription(input),
        SampleMask = uint.MaxValue,
        PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
        RasterizerState = rasterizer,
        BlendState = blend,
        DepthStencilState = depth,
        RenderTargetFormats = new[] { targetFormat },
        DepthStencilFormat = PanelSwapChainFormats.Depth,
        SampleDescription = new SampleDescription(1, 0),
      };
      // Both faces for now (lit from either side): which way GTA IV's triangles wind is for the port (step 4).
      var both = RasterizerDescription.CullNone;
      _solid = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(BlendDescription.Opaque, DepthStencilDescription.Default, both));
      _blend = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(BlendDescription.NonPremultiplied, DepthStencilDescription.Read, both));
      // A decal a little in front of the surface it lies on (nearer is less depth here).
      var pulled = both with { DepthBias = -16, SlopeScaledDepthBias = -1.0f };
      _decal = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(BlendDescription.NonPremultiplied, DepthStencilDescription.Read, pulled));
    }

    /// <summary>The number of instances and of the triangles they draw.</summary>
    public int InstanceCount => _instances.Count;
    public long Triangles => _instances.Sum(i => (long)(_models[i.Model]?.IndexCount ?? 0) / 3);

    /// <summary>The textures made, those that couldn't be (drawn white), and their bytes on the GPU.</summary>
    public (int Made, int Failed, long Bytes) TextureStats => (_textures.Count - 1, _textures.Failed, _textures.Bytes);

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
    /// Models onto the GPU with their textures (the GPU done before returning): per model its meshes' positions,
    /// normals, texture coordinates and alpha in one vertex buffer, their indices (32-bit) in one index buffer, a
    /// part per mesh. Returns each model's number for <see cref="AddInstance"/>; a model with nothing to draw still
    /// gets one (drawing nothing).
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
      _textures.Flush();
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
      var materials = scene.Materials.ToDictionary(m => m.Id);
      var vertices = new List<float>();
      var indices = new List<uint>();
      var parts = new List<Part>();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      foreach (var mesh in scene.Meshes)
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
        }
        uint start = (uint)indices.Count;
        foreach (ushort index in mesh.Indices)
        {
          indices.Add(first + index);
        }
        byte[]? blob = material.TextureKey != null && scene.Textures.TryGetValue(material.TextureKey, out var found) ? found : null;
        var pass = material.Decal || material.DirtMask != null ? Pass.Decal : material.Blend ? Pass.Blend : Pass.Solid;
        var dirt = material.DirtMask is { Length: >= 3 } d ? new Vector3(d[0], d[1], d[2]) : (Vector3?)null;
        parts.Add(new Part(start, (uint)mesh.Indices.Length, _textures.Get(blob), pass, material.Cutout || material.Stipple, dirt));
      }
      if (indices.Count == 0)
      {
        return null;
      }
      var model = new Model { Min = min, Max = max, IndexCount = (uint)indices.Count, Parts = parts.ToArray() };
      model.Vertices = Upload(vertices.ToArray(), list, uploads, out ulong vertexBytes);
      model.Indices = Upload(indices.ToArray(), list, uploads, out ulong indexBytes);
      model.VertexView = new VertexBufferView(model.Vertices.GPUVirtualAddress, (uint)vertexBytes, VertexFloats * 4);
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

    /// <summary>All models, instances and textures gone (the GPU done with them first).</summary>
    public void Clear()
    {
      _device.WaitIdle();
      foreach (var model in _models)
      {
        model?.Dispose();
      }
      _models.Clear();
      _instances.Clear();
      _textures.Reset();
    }

    /// <summary>Clears the target and draws every instance as seen with the view-projection: solid, then blended.</summary>
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
      list.SetDescriptorHeaps(_textures.Heap);
      list.SetGraphicsRoot32BitConstants(0, 16, constants, 0);
      list.SetGraphicsRoot32BitConstants(0, 4, constants + 32, 32);
      list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

      DrawPass(list, Pass.Solid, _solid, constants);
      DrawPass(list, Pass.Decal, _decal, constants);
      DrawPass(list, Pass.Blend, _blend, constants);
    }

    private unsafe void DrawPass(ID3D12GraphicsCommandList list, Pass pass, ID3D12PipelineState pipeline, float* constants)
    {
      list.SetPipelineState(pipeline);
      int lastModel = -1, lastTexture = -1;
      var material = stackalloc float[8];
      foreach (var (number, world) in _instances)
      {
        if (_models[number] is not Model model || !model.Parts.Any(p => p.Pass == pass))
        {
          continue;
        }
        if (number != lastModel)
        {
          list.IASetVertexBuffers(0, model.VertexView);
          list.IASetIndexBuffer(model.IndexView);
          lastModel = number;
        }
        var w = world;
        list.SetGraphicsRoot32BitConstants(0, 16, &w, 16);
        foreach (var part in model.Parts)
        {
          if (part.Pass != pass)
          {
            continue;
          }
          var mask = part.DirtMask ?? Vector3.Zero;
          material[0] = part.Cutout ? 1 : 0;
          material[1] = part.DirtMask != null ? 1 : 0;
          material[4] = mask.X;
          material[5] = mask.Y;
          material[6] = mask.Z;
          list.SetGraphicsRoot32BitConstants(0, 8, material, 36);
          if (part.Texture != lastTexture)
          {
            list.SetGraphicsRootDescriptorTable(1, _textures.Handle(part.Texture));
            lastTexture = part.Texture;
          }
          list.DrawIndexedInstanced(part.Count, 1, part.Start, 0, 0);
        }
      }
    }

    public void Dispose()
    {
      Clear();
      _textures.Dispose();
      _solid.Dispose();
      _blend.Dispose();
      _decal.Dispose();
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
