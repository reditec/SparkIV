using System.IO;
using RageLib.Common.ResourceTypes;

namespace RageLib.Models.Resource.Shaders
{
  /// <summary>
  /// A parameter of several vectors: its type byte is how many (gta_trees has two of 16 and one of 8; a
  /// matrix is 4 and has its own class, one vector 1, a texture 0).
  /// </summary>
  internal class ShaderParamVectorArray : IShaderParam
  {
    private readonly int _count;

    public ShaderParamVectorArray(int count)
    {
      _count = count;
    }

    public Vector4[] Data { get; private set; }

    public void Read(BinaryReader br)
    {
      Data = new Vector4[_count];
      for (int i = 0; i < _count; i++)
      {
        Data[i] = new Vector4(br);
      }
    }

    public void Write(BinaryWriter bw)
    {
    }
  }
}
