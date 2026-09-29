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

using System;
using System.Runtime.InteropServices;
using WaveLib;

namespace RageLib.Audio
{
  class AudioPlayer
  {
    private const int FramesPerBuffer = 4096;
    private const int BufferCount = 3;

    private AudioTrack _track;
    private PcmDecoder _decoder;

    private const float MinusThreeDb = 0.70710678f;

    private WaveOutPlayer _player;
    private WaveFormat _format;
    private int _channelMask;
    private short[] _decoded;  // interleaved, one channel per wave of the track
    private short[] _samples;  // interleaved, as sent to the device
    private float[] _mixLeft;  // stereo downmix coefficients per source channel
    private float[] _mixRight;
    private volatile bool _downmix = true;

    private int _startFrame;
    private bool _decoderPositioned; // decoder already sits at _startFrame (after Seek)
    private bool _paused;
    private volatile bool _looped;
    private volatile float _volume = 1.0f;

    public void Initialize(AudioFile file, AudioTrack track)
    {
      Stop();

      _track = track;
      _decoder = new PcmDecoder(file, track);
      _decoded = new short[FramesPerBuffer * _decoder.Channels];
      ConfigureOutput();
      _startFrame = 0;
      _decoderPositioned = true;
    }

    /// <summary>
    /// For tracks with more than two channels (LCR, 5.0): true mixes them down to stereo,
    /// false sends every channel to Windows with its speaker assignment. Can be switched while playing.
    /// </summary>
    public bool Downmix
    {
      get { return _downmix; }
      set
      {
        if (_downmix == value)
        {
          return;
        }
        _downmix = value;

        if (_track == null || _track.Waves.Length <= 2)
        {
          return;
        }

        // The device has to be reopened with the other channel count.
        bool wasPlaying = IsPlaying;
        bool wasPaused = IsPaused;
        CloseDevice();
        ConfigureOutput();
        _startFrame = _decoder.Seek(_startFrame);
        _decoderPositioned = true;

        if (wasPlaying || wasPaused)
        {
          Play();
          if (wasPaused)
          {
            Pause();
          }
        }
      }
    }

    /// <summary>
    /// Number of channels sent to the device.
    /// </summary>
    public int OutputChannels
    {
      get { return _format == null ? 0 : _format.nChannels; }
    }

    private void ConfigureOutput()
    {
      int sourceChannels = _track.Waves.Length;
      bool downmix = _downmix && sourceChannels > 2;
      int outputChannels = downmix ? 2 : sourceChannels;

      _format = new WaveFormat(_track.SamplesPerSecond, 16, outputChannels);
      _channelMask = outputChannels > 2 ? (int)_track.ChannelMask : 0;
      _samples = new short[FramesPerBuffer * outputChannels];

      _mixLeft = new float[sourceChannels];
      _mixRight = new float[sourceChannels];
      if (downmix)
      {
        // Centre goes to both sides, each surround to its side, both at -3 dB. Normalized so a
        // full scale signal on every channel can't clip.
        float gain = 1f / (1f + (_track.HasChannel(AudioChannel.Centre) ? MinusThreeDb : 0f) +
                            (_track.HasChannel(AudioChannel.LeftSurround) ? MinusThreeDb : 0f));
        for (int i = 0; i < sourceChannels; i++)
        {
          switch (_track.Channels[i])
          {
            case AudioChannel.Left:
              _mixLeft[i] = gain;
              break;
            case AudioChannel.Right:
              _mixRight[i] = gain;
              break;
            case AudioChannel.Centre:
              _mixLeft[i] = _mixRight[i] = gain * MinusThreeDb;
              break;
            case AudioChannel.LeftSurround:
              _mixLeft[i] = gain * MinusThreeDb;
              break;
            case AudioChannel.RightSurround:
              _mixRight[i] = gain * MinusThreeDb;
              break;
          }
        }
      }
    }

    public AudioTrack Track
    {
      get { return _track; }
    }

    public bool IsPlaying
    {
      get { return _player != null && !_paused; }
    }

    public bool IsPaused
    {
      get { return _player != null && _paused; }
    }

    public bool Looped
    {
      get { return _looped; }
      set
      {
        _looped = value;
        if (_decoder != null)
        {
          _decoder.Loop = value;
        }
      }
    }

    /// <summary>
    /// Output volume from 0 to 1. Applied to the samples so other applications are unaffected.
    /// </summary>
    public float Volume
    {
      get { return _volume; }
      set { _volume = Math.Max(0f, Math.Min(1f, value)); }
    }

    public TimeSpan Length
    {
      get { return _track == null ? TimeSpan.Zero : _track.Length; }
    }

    public TimeSpan Position
    {
      get
      {
        if (_track == null)
        {
          return TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds((double)PositionFrames / _track.SamplesPerSecond);
      }
    }

    /// <summary>
    /// True once a non-looped track has been played to the end by the device.
    /// </summary>
    public bool HasEnded
    {
      get { return _player != null && !_looped && RawPositionFrames >= _decoder.TotalFrames; }
    }

    private long RawPositionFrames
    {
      get { return _startFrame + (_player == null ? 0 : _player.GetPlayedFrames()); }
    }

    private int PositionFrames
    {
      get
      {
        int total = _decoder == null ? 0 : _decoder.TotalFrames;
        if (total == 0)
        {
          return 0;
        }

        long position = RawPositionFrames;
        return (int)(_looped ? position % total : Math.Min(position, total));
      }
    }

    private void Filler(IntPtr data, int size)
    {
      int sourceChannels = _decoder.Channels;
      int outputChannels = _format.nChannels;
      int frames = size / (2 * outputChannels);
      float volume = _volume;

      _decoder.Read(_decoded, frames);

      if (outputChannels == sourceChannels)
      {
        for (int i = 0; i < frames * sourceChannels; i++)
        {
          _samples[i] = (short)(_decoded[i] * volume);
        }
      }
      else
      {
        for (int f = 0; f < frames; f++)
        {
          float left = 0f, right = 0f;
          for (int c = 0; c < sourceChannels; c++)
          {
            float sample = _decoded[f * sourceChannels + c];
            left += sample * _mixLeft[c];
            right += sample * _mixRight[c];
          }
          _samples[f * 2] = Clamp(left * volume);
          _samples[f * 2 + 1] = Clamp(right * volume);
        }
      }

      Marshal.Copy(_samples, 0, data, frames * outputChannels);
    }

    private static short Clamp(float value)
    {
      return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
    }

    public void Play()
    {
      if (_decoder == null)
      {
        return;
      }

      if (IsPaused)
      {
        _player.Resume();
        _paused = false;
        return;
      }

      if (IsPlaying)
      {
        return;
      }

      if (_startFrame >= _decoder.TotalFrames)
      {
        _startFrame = 0;
        _decoderPositioned = false;
      }

      if (!_decoderPositioned)
      {
        _startFrame = _decoder.Seek(_startFrame);
      }
      _decoderPositioned = false;
      _decoder.Loop = _looped;
      _paused = false;
      _player = new WaveOutPlayer(-1, _format, _channelMask, FramesPerBuffer * _format.nBlockAlign, BufferCount, Filler);
    }

    public void Pause()
    {
      if (IsPlaying)
      {
        _player.Pause();
        _paused = true;
      }
    }

    public void Seek(TimeSpan position)
    {
      if (_decoder == null)
      {
        return;
      }

      bool wasPlaying = IsPlaying;
      CloseDevice();

      _startFrame = (int)(position.TotalSeconds * _track.SamplesPerSecond);
      _startFrame = _decoder.Seek(_startFrame);
      _decoderPositioned = true;

      if (wasPlaying)
      {
        Play();
      }
    }

    public void Stop()
    {
      CloseDevice();
      _startFrame = 0;
      _decoderPositioned = false;
    }

    private void CloseDevice()
    {
      if (_player != null)
      {
        // Keep the position where playback was, so a following Seek/Play continues from there.
        _startFrame = PositionFrames;
        _player.Dispose();
        _player = null;
      }
      _paused = false;
    }
  }
}
