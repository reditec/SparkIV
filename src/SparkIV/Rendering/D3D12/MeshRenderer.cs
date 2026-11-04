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
  /// What a model draws: its meshes shown (a model's checked ones: its level of detail, a ped's variation), their
  /// materials and textures, and whether they move with bones (a cutscene's cast: each vertex's four bones and
  /// weights, from <see cref="SceneMesh.SkinBones"/>).
  /// </summary>
  public sealed record ModelSource(IReadOnlyList<SceneMesh> Meshes, IReadOnlyList<SceneMaterial> Materials,
                                   IReadOnlyDictionary<string, byte[]> Textures, bool Skinned)
  {
    /// <summary>A colour each material's texture is multiplied by, by material id (a vehicle's paint); white if none.</summary>
    public IReadOnlyDictionary<int, Vector3>? Tints { get; init; }

    /// <summary>A model's checked meshes (as the page shows them), moving with bones or not.</summary>
    public static ModelSource Shown(ModelScene scene, bool skinned)
    {
      var shown = scene.Root.CheckedMeshes().ToHashSet();
      return new ModelSource(scene.Meshes.Where(m => shown.Contains(m.Id)).ToList(), scene.Materials, scene.Textures, skinned);
    }
  }

  /// <summary>
  /// The native renderer's drawing so far (#112 steps 1–4a): models on the GPU, each in one vertex and one index buffer
  /// with a part per mesh (its index range, its material's texture and how it is drawn), drawn as instances (a
  /// model-to-scene matrix each, and for a skinned model its bones' matrices). First everything solid (cut-out texels
  /// clipped), then decals a little in front and see-through materials, blended without writing depth. Lit by Lambert
  /// and an ambient term; only the first texture layer; glass and hidden planes left out for now. The view-projection,
  /// the instance's matrix, the light and the part's material go as root constants, the texture as a descriptor
  /// table, the bones' matrices as a structured buffer (t1). Draws into any target. No WinUI here: the visual tests'
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
  float4 Material; // x: cut out (1), y: alpha from the dirt mask (1), z: the instance's bone count (skinned)
  float4 DirtMask;
  float4 Tint;     // rgb: the texture multiplied by it (a vehicle's paint)
};
ConstantBuffer<Constants> c : register(b0);
Texture2D Albedo : register(t0);
SamplerState Linear : register(s0);
struct Bone { row_major float4x4 m; };
StructuredBuffer<Bone> Palette : register(t1);

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; };
struct VsSkinIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; float4 bones : BLENDINDICES; float4 weights : BLENDWEIGHT; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; float2 uv : TEXCOORD0; float alpha : ALPHA; };

VsOut Finish(float4 local, float3 normal, float2 uv, float alpha)
{
  VsOut output;
  float4 world = mul(local, c.World);
  output.position = mul(world, c.ViewProjection);
  output.normal = mul(normal, (float3x3)c.World);
  output.uv = uv;
  output.alpha = alpha;
  return output;
}

VsOut VSMain(VsIn input)
{
  return Finish(float4(input.position, 1), input.normal, input.uv, input.alpha);
}

// A vertex moved by up to four bones (rest to now); one with no weights, or an instance without its bones, stays as
// it is. Bone numbers are kept within the instance's palette (root descriptors have no bounds check).
VsOut VSSkinned(VsSkinIn input)
{
  float4 p = float4(input.position, 1);
  float3 n = input.normal;
  uint count = (uint)c.Material.z;
  if (count > 0 && dot(input.weights, 1) > 0)
  {
    uint4 b = min((uint4)(input.bones + 0.5), count - 1);
    float4x4 m = Palette[b.x].m * input.weights.x + Palette[b.y].m * input.weights.y +
                 Palette[b.z].m * input.weights.z + Palette[b.w].m * input.weights.w;
    p = mul(p, m);
    n = mul(n, (float3x3)m);
  }
  return Finish(p, n, input.uv, input.alpha);
}

float4 PSMain(VsOut input) : SV_Target
{
  float4 texel = Albedo.Sample(Linear, input.uv);
  float alpha = texel.a * input.alpha;
  float3 albedo = texel.rgb * c.Tint.rgb;
  if (c.Material.y > 0.5)
  {
    alpha = dot(texel.rgb, c.DirtMask.rgb) * input.alpha;
    albedo = float3(0.23, 0.22, 0.2);
  }
  if (c.Material.x > 0.5)
  {
    clip(alpha - 0.5);
  }
  float3 nn = normalize(input.normal + 1e-6);
  float diffuse = abs(dot(nn, normalize(c.Light.xyz)));
  return float4(albedo * (c.Light.w + (1 - c.Light.w) * diffuse), alpha);
}
";

    private const int ConstantCount = 16 + 16 + 4 + 4 + 4 + 4;
    private const int MaterialFloats = 12;
    private const int VertexFloats = 9;
    private const int SkinFloats = 8;

    // Bone matrices a frame (for all skinned instances), and how many frames' worth are kept (frames in flight).
    private const int PaletteCapacity = 65536;
    private const int PaletteSlots = 4;

    private enum Pass : byte { Solid, Blend, Decal }

    private readonly struct Part
    {
      public Part(uint start, uint count, int texture, Pass pass, bool cutout, Vector3? dirtMask, Vector3 tint)
      {
        Start = start;
        Count = count;
        Texture = texture;
        Pass = pass;
        Cutout = cutout;
        DirtMask = dirtMask;
        Tint = tint;
      }

      public Vector3 Tint { get; }

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
      public ID3D12Resource? Skin;
      public VertexBufferView VertexView;
      public VertexBufferView SkinView;
      public IndexBufferView IndexView;
      public uint IndexCount;
      public Part[] Parts = Array.Empty<Part>();
      public Vector3 Min, Max;

      public void Dispose()
      {
        Vertices.Dispose();
        Indices.Dispose();
        Skin?.Dispose();
      }
    }

    private sealed class Instance
    {
      public int Model;
      public Matrix4x4 World;
      public Matrix4x4[]? Palette;
      public ulong PaletteAddress;
    }

    private readonly D3D12Device _device;
    private readonly TextureCache _textures;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState[] _pipelines = new ID3D12PipelineState[6];
    private readonly List<Model?> _models = new List<Model?>();
    private readonly List<Instance> _instances = new List<Instance>();
    private readonly ID3D12Resource _palette;
    private readonly unsafe Matrix4x4* _paletteMapped;
    private int _paletteSlot;

    public unsafe MeshRenderer(D3D12Device device, Format targetFormat)
    {
      _device = device;
      _textures = new TextureCache(device);
      var d3d = device.Device;

      var parameters = new[]
      {
        new RootParameter1(new RootConstants(0, 0, ConstantCount), ShaderVisibility.All),
        new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0)), ShaderVisibility.Pixel),
        new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex),
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
      var skinnedShader = Compile(DxcShaderStage.Vertex, "VSSkinned");
      var pixelShader = Compile(DxcShaderStage.Pixel, "PSMain");
      var input = new[]
      {
        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        new InputElementDescription("ALPHA", 0, Format.R32_Float, 32, 0),
      };
      var skinnedInput = input.Concat(new[]
      {
        new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 0, 1),
        new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 16, 1),
      }).ToArray();
      GraphicsPipelineStateDescription Pipeline(bool skinned, BlendDescription blend, DepthStencilDescription depth, RasterizerDescription rasterizer) => new GraphicsPipelineStateDescription
      {
        RootSignature = _rootSignature,
        VertexShader = skinned ? skinnedShader : vertexShader,
        PixelShader = pixelShader,
        InputLayout = new InputLayoutDescription(skinned ? skinnedInput : input),
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
      // A decal a little in front of the surface it lies on (nearer is less depth here).
      var pulled = both with { DepthBias = -16, SlopeScaledDepthBias = -1.0f };
      for (int s = 0; s < 2; s++)
      {
        _pipelines[s * 3 + (int)Pass.Solid] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.Opaque, DepthStencilDescription.Default, both));
        _pipelines[s * 3 + (int)Pass.Blend] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, DepthStencilDescription.Read, both));
        _pipelines[s * 3 + (int)Pass.Decal] = d3d.CreateGraphicsPipelineState<ID3D12PipelineState>(Pipeline(s == 1, BlendDescription.NonPremultiplied, DepthStencilDescription.Read, pulled));
      }

      // The bones' matrices, written each frame into a slot of their own (the GPU may still read the last frames').
      _palette = d3d.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                                             ResourceDescription.Buffer((ulong)PaletteSlots * PaletteCapacity * 64), ResourceStates.GenericRead);
      void* mapped;
      _palette.Map(0, null, &mapped).CheckError();
      _paletteMapped = (Matrix4x4*)mapped;
    }

    /// <summary>The number of instances and of the triangles they draw.</summary>
    public int InstanceCount => _instances.Count;
    public long Triangles => _instances.Sum(i => (long)(_models[i.Model]?.IndexCount ?? 0) / 3);

    /// <summary>Texture keys materials name that their model's textures don't hold (drawn white).</summary>
    public SortedSet<string> MissingTextures { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Models' checked meshes (not moving with bones) onto the GPU; see <see cref="AddModels(IReadOnlyList{ModelSource})"/>.</summary>
    public int[] AddModels(IReadOnlyList<ModelScene> scenes) => AddModels(scenes.Select(s => ModelSource.Shown(s, false)).ToList());

    /// <summary>
    /// Models onto the GPU with their textures (the GPU done before returning): per model its meshes' positions,
    /// normals, texture coordinates and alpha in one vertex buffer (a skinned model's bones and weights in a second),
    /// their indices (32-bit) in one index buffer, a part per mesh. Returns each model's number for
    /// <see cref="AddInstance"/>; a model with nothing to draw still gets one (drawing nothing).
    /// </summary>
    public int[] AddModels(IReadOnlyList<ModelSource> sources)
    {
      var d3d = _device.Device;
      var numbers = new int[sources.Count];
      var uploads = new List<ID3D12Resource>();
      using var allocator = d3d.CreateCommandAllocator(CommandListType.Direct);
      using var list = d3d.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
      for (int s = 0; s < sources.Count; s++)
      {
        numbers[s] = _models.Count;
        _models.Add(Build(sources[s], list, uploads));
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

    private Model? Build(ModelSource source, ID3D12GraphicsCommandList list, List<ID3D12Resource> uploads)
    {
      var materials = new Dictionary<int, SceneMaterial>();
      foreach (var m in source.Materials)
      {
        materials[m.Id] = m;
      }
      var vertices = new List<float>();
      var skin = source.Skinned ? new List<float>() : null;
      var indices = new List<uint>();
      var parts = new List<Part>();
      var min = new Vector3(float.MaxValue);
      var max = new Vector3(float.MinValue);
      foreach (var mesh in source.Meshes)
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
        bool skinned = mesh.SkinBones != null && mesh.SkinWeights != null && mesh.SkinBones.Length >= count * 4 && mesh.SkinWeights.Length >= count * 4;
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
          if (skin != null)
          {
            for (int k = 0; k < 4; k++)
            {
              skin.Add(skinned ? mesh.SkinBones![v * 4 + k] : 0);
            }
            for (int k = 0; k < 4; k++)
            {
              skin.Add(skinned ? mesh.SkinWeights![v * 4 + k] : 0);
            }
          }
        }
        uint start = (uint)indices.Count;
        foreach (ushort index in mesh.Indices)
        {
          indices.Add(first + index);
        }
        byte[]? blob = material.TextureKey != null && source.Textures.TryGetValue(material.TextureKey, out var found) ? found : null;
        if (material.TextureKey != null && blob == null)
        {
          MissingTextures.Add(material.TextureKey);
        }
        var pass = material.Decal || material.DirtMask != null ? Pass.Decal : material.Blend ? Pass.Blend : Pass.Solid;
        var dirt = material.DirtMask is { Length: >= 3 } d ? new Vector3(d[0], d[1], d[2]) : (Vector3?)null;
        var tint = source.Tints != null && source.Tints.TryGetValue(material.Id, out var t) ? t : Vector3.One;
        parts.Add(new Part(start, (uint)mesh.Indices.Length, _textures.Get(blob), pass, material.Cutout || material.Stipple, dirt, tint));
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
      if (skin != null)
      {
        model.Skin = Upload(skin.ToArray(), list, uploads, out ulong skinBytes);
        model.SkinView = new VertexBufferView(model.Skin.GPUVirtualAddress, (uint)skinBytes, SkinFloats * 4);
      }
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

    /// <summary>A model drawn with a model-to-scene matrix (and, skinned, its bones' matrices); its instance's number.</summary>
    public int AddInstance(int model, Matrix4x4 world, Matrix4x4[]? palette = null)
    {
      _instances.Add(new Instance { Model = model, World = world, Palette = palette });
      return _instances.Count - 1;
    }

    /// <summary>A skinned instance's bones' matrices now (rest to now; see CutscenePose).</summary>
    public void SetPalette(int instance, Matrix4x4[] palette) => _instances[instance].Palette = palette;

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

      // The skinned instances' bones into this frame's slot of the palette buffer.
      _paletteSlot = (_paletteSlot + 1) % PaletteSlots;
      int used = 0;
      foreach (var instance in _instances)
      {
        instance.PaletteAddress = 0;
        if (instance.Palette is Matrix4x4[] palette && palette.Length > 0 && used + palette.Length <= PaletteCapacity)
        {
          long at = (long)_paletteSlot * PaletteCapacity + used;
          fixed (Matrix4x4* source = palette)
          {
            Buffer.MemoryCopy(source, _paletteMapped + at, palette.Length * 64L, palette.Length * 64L);
          }
          instance.PaletteAddress = _palette.GPUVirtualAddress + (ulong)at * 64;
          used += palette.Length;
        }
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

      foreach (var pass in new[] { Pass.Solid, Pass.Decal, Pass.Blend })
      {
        DrawPass(list, pass);
      }
    }

    private unsafe void DrawPass(ID3D12GraphicsCommandList list, Pass pass)
    {
      int lastPipeline = -1, lastTexture = -1;
      var material = stackalloc float[MaterialFloats];
      foreach (var instance in _instances)
      {
        if (_models[instance.Model] is not Model model || !model.Parts.Any(p => p.Pass == pass))
        {
          continue;
        }
        // A skinned model without its bones (none given, or no room for them this frame): drawn at rest.
        bool skinned = model.Skin != null;
        int pipeline = (skinned ? 3 : 0) + (int)pass;
        if (pipeline != lastPipeline)
        {
          list.SetPipelineState(_pipelines[pipeline]);
          lastPipeline = pipeline;
        }
        list.IASetVertexBuffers(0, model.VertexView);
        if (skinned)
        {
          list.IASetVertexBuffers(1, model.SkinView);
          list.SetGraphicsRootShaderResourceView(2, instance.PaletteAddress != 0 ? instance.PaletteAddress : _palette.GPUVirtualAddress);
        }
        list.IASetIndexBuffer(model.IndexView);
        var w = instance.World;
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
          material[2] = instance.PaletteAddress != 0 ? instance.Palette!.Length : 0;
          material[4] = mask.X;
          material[5] = mask.Y;
          material[6] = mask.Z;
          material[8] = part.Tint.X;
          material[9] = part.Tint.Y;
          material[10] = part.Tint.Z;
          list.SetGraphicsRoot32BitConstants(0, MaterialFloats, material, 36);
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
      _palette.Unmap(0);
      _palette.Dispose();
      foreach (var pipeline in _pipelines)
      {
        pipeline.Dispose();
      }
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
