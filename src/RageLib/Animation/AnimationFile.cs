/**********************************************************************\

 RageLib
 Animation dictionaries (.wad) of GTA IV and the Episodes.

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

\**********************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RageLib.Common.Resources;

namespace RageLib.Animation
{
  /// <summary>What a track moves.</summary>
  public enum AnimationTrackKind
  {
    BonePosition = 0,
    BoneRotation = 1,
    ModelPosition = 5,
    ModelRotation = 6,
    ActionFlags = 128,

    // Texture coordinates moved: two rows of the texture matrix (map objects' UV animations); as whole
    // numbers in ped animations: 130 flags, 131 the sound played (the hash of its name, as AudioEvent).
    UV0 = 130,
    UV1 = 131,

    /// <summary>Which facial animation plays (the hash of its name, "yawn").</summary>
    FacialAnimation = 134,

    // A cutscene's camera (camera_0 in cuts.img's dictionaries), named as OpenIV's export names them: its
    // place and turn (looking along its y, z up), field of view in degrees, depth of field (3 numbers), and
    // two whose meaning isn't known (the control one -1 or 1).
    CameraPosition = 7,
    CameraRotation = 8,
    CameraFov = 136,
    CameraDof = 137,
    CameraMatrixRotateFactor = 138,
    CameraControl = 144,
  }

  /// <summary>
  /// An animation dictionary (.wad): its animations ("clips"), each with tracks for the bones it moves.
  /// Read from the resource's system part, following its pointers (0x5xxxxxxx). Checked value for value
  /// against OpenIV's .onim export of ped animations.
  /// </summary>
  public class AnimationFile
  {
    private byte[] _data;

    public List<Animation> Animations { get; private set; }

    public void Open(Stream stream)
    {
      var resource = new ResourceFile();
      resource.Read(stream);
      _data = resource.SystemMemData;

      // pgDictionary: vtable, block map, parent, usage, hashes {ptr, count, capacity}, entries {ptr, count, capacity}.
      int entries = Pointer(0x18);
      int count = UShort(0x1C);
      Animations = new List<Animation>(count);
      for (int i = 0; i < count; i++)
      {
        Animations.Add(ReadAnimation(Pointer(entries + i * 4)));
      }
    }

    #region Reading

    private uint U(int offset) => BitConverter.ToUInt32(_data, offset);
    private int UShort(int offset) => BitConverter.ToUInt16(_data, offset);
    private float F(int offset) => BitConverter.ToSingle(_data, offset);
    private int Pointer(int offset) => (int)(U(offset) & 0x0FFFFFFF);
    private bool IsPointer(int offset) => (U(offset) & 0xF0000000) == 0x50000000;

    private string String(int offset)
    {
      int end = offset;
      while (end < _data.Length && _data[end] != 0)
      {
        end++;
      }
      return Encoding.ASCII.GetString(_data, offset, end - offset);
    }

    // crAnimation: vtable, u16 ?, u16 flags, u16 frames, u16 frames per block, f32 duration, u32 ?,
    // ptr ?, u16/u16 ?, ptr name ("pack:/walk.anim"), tracks {ptr, count, capacity}.
    private Animation ReadAnimation(int at)
    {
      var animation = new Animation
      {
        Flags = UShort(at + 6),
        FrameCount = UShort(at + 8),
        SequenceFrameLimit = UShort(at + 0x0A),
        Duration = F(at + 0x0C),
        Name = IsPointer(at + 0x1C) ? String(Pointer(at + 0x1C)) : "",
      };
      int tracks = Pointer(at + 0x20);
      int count = UShort(at + 0x24);
      for (int i = 0; i < count; i++)
      {
        animation.Tracks.Add(ReadTrack(Pointer(tracks + i * 4)));
      }
      return animation;
    }

    // Track: u8 kind, u8 data type, u16 bone id, u32 frames per block, blocks {ptr, count, capacity}.
    // Block: u32 head (as the track's), up to 4 channel pointers.
    private AnimationTrack ReadTrack(int at)
    {
      uint head = U(at);
      var track = new AnimationTrack((AnimationTrackKind)(head & 0xFF), (int)(head >> 8) & 0xFF, (int)(head >> 16));
      int blocks = Pointer(at + 8);
      int count = UShort(at + 0x0C);
      for (int b = 0; b < count; b++)
      {
        int block = Pointer(blocks + b * 4);
        if (track.DataType == 3)
        {
          // A whole number (flags, a facial animation's name hash): one channel, kept exact.
          track.IntegerBlocks.Add(IsPointer(block + 4) ? ReadIntegerChannel(Pointer(block + 4)) : null);
          continue;
        }
        var channels = new List<float[]>();
        bool known = true;
        for (int c = 0; c < 4 && IsPointer(block + 4 + c * 4); c++)
        {
          var values = ReadChannel(Pointer(block + 4 + c * 4));
          if (values == null)
          {
            known = false;
            break;
          }
          channels.AddRange(values);
        }
        track.Blocks.Add(known ? channels.ToArray() : null);
      }
      return track;
    }

    /// <summary>The values of a whole-number channel (one value where it is fixed); null for a kind not known.</summary>
    private uint[] ReadIntegerChannel(int at)
    {
      int kind = (int)(U(at + 4) >> 8) & 0xFF;
      switch (kind)
      {
        case 0x7: // raw {ptr, count}
        {
          int data = Pointer(at + 8), n = UShort(at + 12);
          var values = new uint[n];
          for (int i = 0; i < n; i++)
          {
            values[i] = U(data + i * 4);
          }
          return values;
        }
        case 0xB: // fixed
          return new[] { U(at + 8) };
        case 0xC: // runs: the values in the order they come {ptr, count}, then how many frames each holds
        {
          // Each length: q zeros up to a one, then k + 1 bits (LSB first) r; length = q * 2^k + r.
          int table = Pointer(at + 8), runs = UShort(at + 12);
          int stream = Pointer(at + 16), bitCount = (int)U(at + 20), k = (int)U(at + 24);
          var values = new List<uint>();
          int pos = 0;
          for (int run = 0; run < runs; run++)
          {
            int q = 0;
            while (pos < bitCount && Bit(stream, pos++) == 0)
            {
              q++;
            }
            int r = 0;
            for (int b = 0; b <= k; b++)
            {
              r |= Bit(stream, pos++) << b;
            }
            uint value = U(table + run * 4);
            int length = (q << k) + r;
            for (int i = 0; i < length; i++)
            {
              values.Add(value);
            }
          }
          return values.ToArray();
        }
        default:
          return null;
      }
    }

    private int Bit(int data, int index) => _data[data + index / 8] >> (index % 8) & 1;

    /// <summary>The values of a channel object by component; one value where it is fixed. Null for a kind not known.</summary>
    private float[][] ReadChannel(int at)
    {
      int kind = (int)(U(at + 4) >> 8) & 0xFF;
      switch (kind)
      {
        case 0x1: // raw floats {ptr, count}
        {
          int data = Pointer(at + 8), n = UShort(at + 12);
          var values = new float[n];
          for (int i = 0; i < n; i++)
          {
            values[i] = F(data + i * 4);
          }
          return new[] { values };
        }
        case 0x4: // one float
          return new[] { new[] { F(at + 8) } };
        case 0x6: // quantized: ptr bits, bit count, value count, scale, offset; the bits LSB first
        {
          int data = Pointer(at + 8), bits = (int)U(at + 12), n = (int)U(at + 16);
          float scale = F(at + 20), offset = F(at + 24);
          var values = new float[n];
          for (int i = 0; i < n; i++)
          {
            int v = 0;
            for (int b = 0; b < bits; b++)
            {
              int bit = i * bits + b;
              if ((_data[data + bit / 8] >> (bit % 8) & 1) != 0)
              {
                v |= 1 << b;
              }
            }
            values[i] = offset + v * scale;
          }
          return new[] { values };
        }
        case 0x9: // a fixed quaternion (x, y, z, w) elsewhere
        {
          int q = Pointer(at + 8);
          return new[] { new[] { F(q) }, new[] { F(q + 4) }, new[] { F(q + 8) }, new[] { F(q + 12) } };
        }
        case 0xD: // a fixed vector (x, y, z) elsewhere
        {
          int v = Pointer(at + 8);
          return new[] { new[] { F(v) }, new[] { F(v + 4) }, new[] { F(v + 8) } };
        }
        default:
          return null;
      }
    }

    #endregion
  }

  public class Animation
  {
    /// <summary>"pack:/walk.anim".</summary>
    public string Name { get; internal set; }

    /// <summary>The name without "pack:/" and ".anim" ("walk").</summary>
    public string ShortName
    {
      get
      {
        string name = Name ?? "";
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
          name = name.Substring(slash + 1);
        }
        return name.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 5) : name;
      }
    }

    public int Flags { get; internal set; }
    public int FrameCount { get; internal set; }

    /// <summary>Long animations are stored in blocks: block b starts at frame b × this (blocks overlap by one frame).</summary>
    public int SequenceFrameLimit { get; internal set; }

    /// <summary>In seconds.</summary>
    public float Duration { get; internal set; }

    public List<AnimationTrack> Tracks { get; } = new List<AnimationTrack>();
  }

  public class AnimationTrack
  {
    internal AnimationTrack(AnimationTrackKind kind, int dataType, int boneId)
    {
      Kind = kind;
      DataType = dataType;
      BoneId = boneId;
    }

    public AnimationTrackKind Kind { get; }

    /// <summary>0 three floats, 1 four floats (a quaternion x, y, z, w), 3 a whole number.</summary>
    public int DataType { get; }

    /// <summary>The bone's id as in the skeleton (Bone.Id); 0 for the root and the model tracks.</summary>
    public int BoneId { get; }

    /// <summary>Per block the values by component (one value where it is fixed); null for a block not decoded.</summary>
    internal List<float[][]> Blocks { get; } = new List<float[][]>();

    /// <summary>For a whole-number track (data type 3): per block its values (one where it is fixed).</summary>
    internal List<uint[]> IntegerBlocks { get; } = new List<uint[]>();

    /// <summary>Whether all its values could be read.</summary>
    public bool IsDecoded
    {
      get { return Blocks.TrueForAll(b => b != null) && IntegerBlocks.TrueForAll(b => b != null); }
    }

    /// <summary>The whole number at a frame (flags, a facial animation's name hash), or null.</summary>
    public uint? GetInteger(int frame, int sequenceFrameLimit)
    {
      if (IntegerBlocks.Count == 0)
      {
        return null;
      }
      int block = sequenceFrameLimit > 0 ? Math.Min(frame / sequenceFrameLimit, IntegerBlocks.Count - 1) : 0;
      var values = IntegerBlocks[block];
      if (values == null || values.Length == 0)
      {
        return null;
      }
      return values[Math.Min(Math.Max(0, frame - block * sequenceFrameLimit), values.Length - 1)];
    }

    /// <summary>The value at a frame (3 or 4 components, or 1 for a whole number), or null if not decoded.</summary>
    public float[] GetValue(int frame, int sequenceFrameLimit)
    {
      if (Blocks.Count == 0)
      {
        return null;
      }
      int block = sequenceFrameLimit > 0 ? Math.Min(frame / sequenceFrameLimit, Blocks.Count - 1) : 0;
      var channels = Blocks[block];
      if (channels == null)
      {
        return null;
      }
      int index = Math.Max(0, frame - block * sequenceFrameLimit);
      // A rotation may be stored with x, y and z only (food.wad's ..._obj clips; OpenIV calls these
      // "UnknownType_3"): w follows from them, as the length of a rotation is 1.
      bool withoutW = DataType == 1 && channels.Length == 3;
      var value = new float[withoutW ? 4 : channels.Length];
      for (int c = 0; c < channels.Length; c++)
      {
        var values = channels[c];
        value[c] = values.Length == 0 ? 0 : values[Math.Min(index, values.Length - 1)];
      }
      if (withoutW)
      {
        value[3] = (float)Math.Sqrt(Math.Max(0, 1 - value[0] * value[0] - value[1] * value[1] - value[2] * value[2]));
      }
      return value;
    }
  }
}
