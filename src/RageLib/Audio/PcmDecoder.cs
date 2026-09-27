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
using System.IO;
using RageLib.Audio.SoundBank;

namespace RageLib.Audio
{
  /// <summary>
  /// Streams an AudioTrack as interleaved 16 bit PCM. Every channel is decoded independently
  /// (own ADPCM state and block cursor) and the output is cut to the track length so the
  /// channels of a stereo pair never drift apart, even when looping.
  /// </summary>
  internal class PcmDecoder
  {
    private class ChannelCursor
    {
      public AudioWave Wave;
      public DviAdpcmDecoder.AdpcmState State;
      public int NextBlock;
      public byte[] Pending;
      public int PendingOffset;

      // Decoded size in bytes of every block seen so far, so seeking doesn't have to decode them again.
      public List<int> BlockBytes = new List<int>();
    }

    private readonly AudioFile _file;
    private readonly ChannelCursor[] _channels;
    private readonly int _totalFrames;
    private int _frame;

    public PcmDecoder(AudioFile file, AudioTrack track)
    {
      _file = file;
      _totalFrames = track.NumberOfFrames;

      _channels = new ChannelCursor[track.Waves.Length];
      for (int i = 0; i < _channels.Length; i++)
      {
        _channels[i] = new ChannelCursor { Wave = track.Waves[i] };
      }

      Seek(0);
    }

    public int Channels
    {
      get { return _channels.Length; }
    }

    public int TotalFrames
    {
      get { return _totalFrames; }
    }

    public bool Loop { get; set; }

    public bool EndOfTrack
    {
      get { return _frame >= _totalFrames; }
    }

    /// <summary>
    /// Seeks to an exact frame. Blocks can be very long (FUNERAL.ivaud: ~15 s), so the block
    /// containing the frame is decoded from its start and the samples before the frame are skipped.
    /// </summary>
    /// <returns>The frame playback will resume from.</returns>
    public int Seek(int frame)
    {
      frame = Math.Max(0, Math.Min(frame, _totalFrames));

      foreach (var channel in _channels)
      {
        PositionChannel(channel, (long)frame * 2);
      }

      _frame = frame;
      return _frame;
    }

    private void PositionChannel(ChannelCursor channel, long targetByte)
    {
      channel.State = new DviAdpcmDecoder.AdpcmState();
      channel.Pending = null;
      channel.PendingOffset = 0;

      long position = 0;
      int block = 0;
      while (block < channel.Wave.BlockCount)
      {
        byte[] data = null;
        if (block >= channel.BlockBytes.Count)
        {
          // Length unknown yet, decode in order (keeps the ADPCM state continuous).
          data = DecodeBlock(channel, block);
        }

        int length = channel.BlockBytes[block];
        if (position + length > targetByte)
        {
          if (data == null)
          {
            // The decoder state is restored from the block's stored ADPCM state.
            channel.State = new DviAdpcmDecoder.AdpcmState();
            data = DecodeBlock(channel, block);
          }

          channel.Pending = data;
          channel.PendingOffset = (int)(targetByte - position);
          channel.NextBlock = block + 1;
          return;
        }

        position += length;
        block++;
      }

      channel.NextBlock = block;
    }

    private byte[] DecodeBlock(ChannelCursor channel, int block)
    {
      var ms = new MemoryStream();
      _file.SoundBank.ExportWaveBlockAsPCM(channel.Wave.Index, block, ref channel.State, _file.Stream, ms);
      byte[] data = ms.ToArray();

      if (block == channel.BlockBytes.Count)
      {
        channel.BlockBytes.Add(data.Length);
      }
      return data;
    }

    /// <summary>
    /// Fills the buffer with interleaved samples. Past the end of the track (and not looping)
    /// the remainder is filled with silence.
    /// </summary>
    /// <returns>Number of frames that contain audio.</returns>
    public int Read(short[] buffer, int frames)
    {
      int channels = _channels.Length;
      int written = 0;

      for (int f = 0; f < frames; f++)
      {
        if (_frame >= _totalFrames)
        {
          if (!Loop || _totalFrames == 0)
          {
            Array.Clear(buffer, f * channels, (frames - f) * channels);
            break;
          }
          Seek(0);
        }

        for (int c = 0; c < channels; c++)
        {
          buffer[f * channels + c] = NextSample(_channels[c]);
        }

        _frame++;
        written++;
      }

      return written;
    }

    private short NextSample(ChannelCursor channel)
    {
      while (channel.Pending == null || channel.PendingOffset + 2 > channel.Pending.Length)
      {
        if (channel.NextBlock >= channel.Wave.BlockCount)
        {
          // Channel ran out of data slightly before the track length.
          return 0;
        }

        channel.Pending = DecodeBlock(channel, channel.NextBlock);
        channel.NextBlock++;
        channel.PendingOffset = 0;
      }

      short sample = BitConverter.ToInt16(channel.Pending, channel.PendingOffset);
      channel.PendingOffset += 2;
      return sample;
    }
  }
}
