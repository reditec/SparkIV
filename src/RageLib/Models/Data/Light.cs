using RageLib.Common.ResourceTypes;
using RageLib.Models.Resource;

namespace RageLib.Models.Data
{
  /// <summary>A light of a model (a lamp, a neon sign, a spot): where it is, where it shines, how far.</summary>
  public class Light
  {
    public Vector3 Position { get; private set; }
    public Vector3 Direction { get; private set; }

    /// <summary>A spot's second axis (OpenIV: Origin).</summary>
    public Vector3 Tangent { get; private set; }

    /// <summary>The colour's four bytes as stored (OpenIV prints them in this order).</summary>
    public byte[] Color { get; private set; }

    /// <summary>Whether it is a spot (shining in Direction within Falloff); else it shines all round.</summary>
    public bool IsSpot { get; private set; }

    /// <summary>How far it is seen (OpenIV: Range; Bernie's ceiling lamps 10 to 14 m).</summary>
    public float Range { get; private set; }

    /// <summary>How far it lights (OpenIV: Distance; the same lamps 3.3 to 4 m); 0 where not given.</summary>
    public float Distance { get; private set; }

    /// <summary>How strong (OpenIV: _f58; 1 for most lamps, 0.4 for the track spots in Bernie's flat, up to 7).</summary>
    public float Intensity { get; private set; }

    /// <summary>A spot's inner and outer angle, in degrees, from its axis (half the cone: 50 is 100° across).</summary>
    public float HotSpot { get; private set; }
    public float Falloff { get; private set; }

    public float CoronaSize { get; private set; }
    public uint Flags { get; private set; }

    /// <summary>The bone it moves with (an id of the skeleton).</summary>
    public int BoneId { get; private set; }

    internal Light(LightAttributes attributes)
    {
      Position = attributes.Position;
      Direction = attributes.Direction;
      Tangent = attributes.Tangent;
      Color = attributes.Color;
      IsSpot = attributes.Type == 2;
      Range = attributes.Range;
      Distance = attributes.Distance;
      Intensity = attributes.Unknown58;
      HotSpot = attributes.HotSpot;
      Falloff = attributes.Falloff;
      CoronaSize = attributes.CoronaSize;
      Flags = attributes.Flags;
      BoneId = attributes.BoneId;
    }
  }
}
