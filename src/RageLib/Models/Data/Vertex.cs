using System.IO;
using RageLib.Common.ResourceTypes;

namespace RageLib.Models.Data
{
  public struct Vertex
  {
    public Vector3 Position { get; private set; }
    public Vector3 Normal { get; private set; }
    public uint DiffuseColor { get; private set; }
    public uint SpecularColor { get; private set; }
    public Vector2[] TextureCoordinates { get; private set; }

    public float[] BlendWeights { get; set; }
    public uint[] BlendIndices { get; set; }

    /// <summary>
    /// The tangent (x, y, z and, where stored, w: the binormal's side, as gta_ped's vertex program takes it: binormal =
    /// normal x tangent x w); null without one.
    /// </summary>
    public float[] Tangent { get; private set; }

    public const int MaxTextureCoordinates = 8;

    // To add: Binormal (sizes/etc??)
    // BlendIndices/BlendWeights: Type = Color ... stored as ARGB I think but presumably swizzled as RGBA

    internal Vertex(BinaryReader br, Mesh mesh) : this()
    {
      TextureCoordinates = new Vector2[MaxTextureCoordinates];

      VertexElement[] elements = mesh.VertexDeclaration.Elements;
      foreach (var element in elements)
      {
        if (element.Stream == -1)
        {
          break;
        }

        switch (element.Usage)
        {
          case VertexElementUsage.Position:
            Position = new Vector3(br);
            break;
          case VertexElementUsage.Normal:
            Normal = new Vector3(br);
            break;
          case VertexElementUsage.Tangent when element.UsageIndex == 0 && (element.Size == 12 || element.Size == 16):
            Tangent = new float[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), element.Size == 16 ? br.ReadSingle() : 1f };
            break;
          case VertexElementUsage.TextureCoordinate:
            TextureCoordinates[element.UsageIndex] = new Vector2(br);
            break;
          case VertexElementUsage.Color:
            if (element.UsageIndex == 0) // As per DirectX docs
            {
              DiffuseColor = br.ReadUInt32();
            }
            else if (element.UsageIndex == 1) // As per DirectX docs
            {
              SpecularColor = br.ReadUInt32();
            }
            else
            {
              br.ReadUInt32();
            }
            break;
          case VertexElementUsage.BlendWeight:
            BlendWeights = new float[4];
            uint tmpWeight = br.ReadUInt32();
            BlendWeights[0] = ((tmpWeight >> 16) & 0xFF) / 255.0f;
            BlendWeights[1] = ((tmpWeight >> 8) & 0xFF) / 255.0f;
            BlendWeights[2] = ((tmpWeight) & 0xFF) / 255.0f;
            BlendWeights[3] = ((tmpWeight >> 24) & 0xFF) / 255.0f;
            break;
          case VertexElementUsage.BlendIndices:
            BlendIndices = new uint[4];
            uint tmpIndices = br.ReadUInt32();
            BlendIndices[0] = (tmpIndices >> 16) & 0xFF;
            BlendIndices[1] = (tmpIndices >> 8) & 0xFF;
            BlendIndices[2] = (tmpIndices) & 0xFF;
            BlendIndices[3] = (tmpIndices >> 24) & 0xFF;
            break;
          default:
            br.BaseStream.Seek(element.Size, SeekOrigin.Current);
            break;
        }
      }
    }
  }
}