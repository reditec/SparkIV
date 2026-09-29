/**********************************************************************\

 RageLib - Audio
 Copyright (C) 2009  Arushan/Aru <oneforaru at gmail.com>

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

using System.IO;
using RageLib.Audio.SoundBank;

namespace RageLib.Audio.WaveFile
{
  static class WaveExport
  {
    public static void Export(AudioFile file, AudioWave wave, Stream outStream)
    {
      WaveHeader header = new WaveHeader();

      // Skip the header
      outStream.Seek(header.HeaderSize, SeekOrigin.Begin);

      // Write the data
      file.SoundBank.ExportAsPCM(wave.Index, file.Stream, outStream);

      // Create header and write it
      outStream.Seek(0, SeekOrigin.Begin);
      header.FileSize = (int)outStream.Length;
      header.SamplesPerSecond = wave.SamplesPerSecond;
      header.Write(new BinaryWriter(outStream));
    }

    /// <summary>
    /// Writes all channels of a multichannel track (stereo, LCR, 5.0) into one WAV file with speaker mask.
    /// </summary>
    public static void ExportChannels(AudioFile file, AudioTrack track, Stream outStream)
    {
      WaveHeader header = new WaveHeader(true);

      // Skip the header
      outStream.Seek(header.HeaderSize, SeekOrigin.Begin);

      // Write the data, channels interleaved in WAV order (L, R, C, LS, RS)
      var decoder = new PcmDecoder(file, track);
      var samples = new short[4096 * decoder.Channels];
      var bytes = new byte[samples.Length * 2];
      while (!decoder.EndOfTrack)
      {
        int frames = decoder.Read(samples, 4096);
        System.Buffer.BlockCopy(samples, 0, bytes, 0, frames * decoder.Channels * 2);
        outStream.Write(bytes, 0, frames * decoder.Channels * 2);
      }

      // Create header and write it
      outStream.Seek(0, SeekOrigin.Begin);
      header.FileSize = (int)outStream.Length;
      header.SamplesPerSecond = track.SamplesPerSecond;
      header.ChannelMask = track.ChannelMask;
      header.Write(new BinaryWriter(outStream));
    }

    public static void ExportMultichannel(AudioFile file, Stream outStream)
    {
      WaveHeader header = new WaveHeader(true);

      // Skip the header
      outStream.Seek(header.HeaderSize, SeekOrigin.Begin);

      // Write the data
      IMultichannelSound sound = file.SoundBank as IMultichannelSound;
      sound.ExportMultichannelAsPCM(file.Stream, outStream);

      // Create header and write it
      outStream.Seek(0, SeekOrigin.Begin);
      header.FileSize = (int)outStream.Length;
      header.SamplesPerSecond = sound.CommonSamplesPerSecond;
      header.ChannelMask = sound.ChannelMask;
      header.Write(new BinaryWriter(outStream));
    }
  }
}