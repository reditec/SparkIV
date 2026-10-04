using RageLib.Common.ResourceTypes;
using RageLib.Models.Resource.Shaders;

namespace RageLib.Models.Data
{
  /// <summary>A material parameter of several vectors (the trees' wind and branch settings).</summary>
  public class MaterialParamVectorArray : MaterialParam
  {
    public Vector4[] Values { get; private set; }

    internal MaterialParamVectorArray(uint hash, ShaderParamVectorArray vectors)
        : base(hash)
    {
      Values = vectors.Data;
    }
  }
}
