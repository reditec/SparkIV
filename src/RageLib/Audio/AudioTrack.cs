/**********************************************************************\

 RageLib - Audio
 Copyright (C) 2026  SparkIV contributors

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
using RageLib.Audio.WaveFile;

namespace RageLib.Audio
{
  /// <summary>
  /// Speaker a mono wave belongs to, in WAV channel order (the order of the WAVE_FORMAT_EXTENSIBLE mask bits).
  /// </summary>
  public enum AudioChannel
  {
    Mono = -1,
    Left = 0,
    Right = 1,
    Centre = 2,
    LeftSurround = 3,
    RightSurround = 4,
  }

  /// <summary>
  /// A playable unit: either a single mono wave, or several mono waves (NAME_LEFT, NAME_RIGHT,
  /// NAME_CENTRE, ...) that together form one stereo, LCR or 5.0 track.
  /// </summary>
  public class AudioTrack
  {
    public string Name { get; private set; }

    /// <summary>
    /// The underlying mono waves in WAV channel order (L, R, C, LS, RS).
    /// </summary>
    public AudioWave[] Waves { get; private set; }

    /// <summary>
    /// Speaker of each wave in Waves.
    /// </summary>
    public AudioChannel[] Channels { get; private set; }

    public bool IsMultichannel
    {
      get { return Waves.Length > 1; }
    }

    /// <summary>
    /// "Stereo", "LCR", "5.0" etc. for multichannel tracks, "Mono" otherwise.
    /// </summary>
    public string LayoutName
    {
      get
      {
        switch (Waves.Length)
        {
          case 1:
            return "Mono";
          case 2:
            return "Stereo";
          case 3:
            return HasChannel(AudioChannel.Centre) ? "LCR" : "3.0";
          case 4:
            return "Quad";
          default:
            return "5.0";
        }
      }
    }

    internal ChannelMask ChannelMask
    {
      get
      {
        var mask = ChannelMask.Invalid;
        foreach (var channel in Channels)
        {
          mask |= ToMask(channel);
        }
        return mask;
      }
    }

    public int SamplesPerSecond
    {
      get { return Waves[0].SamplesPerSecond; }
    }

    /// <summary>
    /// Number of sample frames. The shortest channel wins so all channels stay in sync.
    /// </summary>
    public int NumberOfFrames
    {
      get
      {
        int frames = Waves[0].NumberOfSamples;
        for (int i = 1; i < Waves.Length; i++)
        {
          frames = Math.Min(frames, Waves[i].NumberOfSamples);
        }
        return frames;
      }
    }

    public TimeSpan Length
    {
      get { return TimeSpan.FromSeconds((double)NumberOfFrames / SamplesPerSecond); }
    }

    private AudioTrack(string name, AudioWave[] waves, AudioChannel[] channels)
    {
      Name = name;
      Waves = waves;
      Channels = channels;
    }

    public static AudioTrack FromMono(AudioWave wave)
    {
      return new AudioTrack(wave.ToString(), new[] { wave }, new[] { AudioChannel.Mono });
    }

    public bool HasChannel(AudioChannel channel)
    {
      return Array.IndexOf(Channels, channel) >= 0;
    }

    /// <summary>
    /// Speaker a wave name belongs to, from its suffix (NAME_LEFT, NAME.L, NAME_CENTRE, NAME.LS ...).
    /// </summary>
    public static AudioChannel GetChannel(string name, out string baseName)
    {
      baseName = name;
      if (string.IsNullOrEmpty(name))
      {
        return AudioChannel.Mono;
      }

      int pos = Math.Max(name.LastIndexOf('_'), name.LastIndexOf('.'));
      if (pos <= 0 || pos == name.Length - 1)
      {
        return AudioChannel.Mono;
      }

      AudioChannel channel;
      switch (name.Substring(pos + 1).ToUpperInvariant())
      {
        case "LEFT":
        case "L":
          channel = AudioChannel.Left;
          break;
        case "RIGHT":
        case "R":
          channel = AudioChannel.Right;
          break;
        case "CENTRE":
        case "CENTER":
        case "C":
          channel = AudioChannel.Centre;
          break;
        case "LS":
          channel = AudioChannel.LeftSurround;
          break;
        case "RS":
          channel = AudioChannel.RightSurround;
          break;
        default:
          return AudioChannel.Mono;
      }

      baseName = name.Substring(0, pos);
      return channel;
    }

    /// <summary>
    /// Groups mono waves that share a base name into multichannel tracks. A group needs a left and
    /// a right channel; centre is optional, surround only counts as a left/right pair. All members
    /// must have the same sample rate and block layout. Each wave is used in at most one group.
    /// </summary>
    public static List<AudioTrack> FindChannelGroups(IEnumerable<AudioWave> waves)
    {
      var groups = new Dictionary<string, Dictionary<AudioChannel, AudioWave>>(StringComparer.OrdinalIgnoreCase);
      var order = new List<string>();

      foreach (var wave in waves)
      {
        string baseName;
        AudioChannel channel = GetChannel(wave.ToString(), out baseName);
        if (channel == AudioChannel.Mono)
        {
          continue;
        }

        Dictionary<AudioChannel, AudioWave> group;
        if (!groups.TryGetValue(baseName, out group))
        {
          group = new Dictionary<AudioChannel, AudioWave>();
          groups.Add(baseName, group);
          order.Add(baseName);
        }

        if (!group.ContainsKey(channel))
        {
          group.Add(channel, wave);
        }
      }

      var tracks = new List<AudioTrack>();
      foreach (var baseName in order)
      {
        var group = groups[baseName];
        if (!group.ContainsKey(AudioChannel.Left) || !group.ContainsKey(AudioChannel.Right))
        {
          continue;
        }

        bool surround = group.ContainsKey(AudioChannel.LeftSurround) && group.ContainsKey(AudioChannel.RightSurround);

        var members = new List<AudioWave>();
        var channels = new List<AudioChannel>();
        foreach (AudioChannel channel in Enum.GetValues(typeof(AudioChannel)))
        {
          if (channel == AudioChannel.Mono || !group.ContainsKey(channel))
          {
            continue;
          }
          if (!surround && (channel == AudioChannel.LeftSurround || channel == AudioChannel.RightSurround))
          {
            continue;
          }
          members.Add(group[channel]);
          channels.Add(channel);
        }

        bool compatible = true;
        foreach (var wave in members)
        {
          compatible &= wave.SamplesPerSecond == members[0].SamplesPerSecond &&
                        wave.BlockCount == members[0].BlockCount;
        }

        if (compatible)
        {
          tracks.Add(new AudioTrack(baseName, members.ToArray(), channels.ToArray()));
        }
      }

      return tracks;
    }

    public static string GetChannelName(AudioChannel channel)
    {
      switch (channel)
      {
        case AudioChannel.Left:
          return "Left";
        case AudioChannel.Right:
          return "Right";
        case AudioChannel.Centre:
          return "Centre";
        case AudioChannel.LeftSurround:
          return "Left surround";
        case AudioChannel.RightSurround:
          return "Right surround";
        default:
          return "Mono";
      }
    }

    private static ChannelMask ToMask(AudioChannel channel)
    {
      switch (channel)
      {
        case AudioChannel.Left:
          return ChannelMask.SpeakerFrontLeft;
        case AudioChannel.Right:
          return ChannelMask.SpeakerFrontRight;
        case AudioChannel.Centre:
          return ChannelMask.SpeakerFrontCenter;
        case AudioChannel.LeftSurround:
          return ChannelMask.SpeakerBackLeft;
        case AudioChannel.RightSurround:
          return ChannelMask.SpeakerBackRight;
        default:
          return ChannelMask.Invalid;
      }
    }

    public override string ToString()
    {
      return Name;
    }
  }
}
