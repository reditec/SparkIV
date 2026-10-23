/**********************************************************************\

 RageLib
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
using System.Runtime.InteropServices;
using System.Threading;

namespace RageLib.Audio.Playback
{
  /// <summary>Fills a buffer of the given size in bytes with the next 16-bit PCM samples.</summary>
  public delegate void BufferFillEventHandler(IntPtr data, int size);

  /// <summary>A 16-bit PCM format: sample rate, bits per sample and channels.</summary>
  public class WaveFormat
  {
    public short nChannels;
    public int nSamplesPerSec;
    public short wBitsPerSample;
    public short nBlockAlign;
    public int nAvgBytesPerSec;

    public WaveFormat(int rate, int bits, int channels)
    {
      nChannels = (short)channels;
      nSamplesPerSec = rate;
      wBitsPerSample = (short)bits;
      nBlockAlign = (short)(channels * bits / 8);
      nAvgBytesPerSec = rate * nBlockAlign;
    }
  }

  /// <summary>
  /// Plays PCM through the Windows waveOut API: a ring of buffers, each filled by the given handler and handed to
  /// the device; a thread waits on the device's event and refills the buffers the device has finished with.
  /// </summary>
  public sealed class WaveOutPlayer : IDisposable
  {
    private const int WaveMapper = -1;
    private const int CallbackEvent = 0x00050000;
    private const int WhdrDone = 0x00000001;
    private const int TimeSamples = 0x0002;
    private const short FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
      public IntPtr Data;
      public uint BufferLength;
      public uint BytesRecorded;
      public IntPtr User;
      public uint Flags;
      public uint Loops;
      public IntPtr Next;
      public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
      public ushort FormatTag;
      public ushort Channels;
      public uint SamplesPerSec;
      public uint AvgBytesPerSec;
      public ushort BlockAlign;
      public ushort BitsPerSample;
      public ushort ExtraSize;
      public ushort ValidBitsPerSample;
      public uint ChannelMask;
      public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
      [FieldOffset(0)] public uint Type;
      [FieldOffset(4)] public uint Value;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormatExtensible format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr device, ref MmTime time, int size);
    [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    private readonly BufferFillEventHandler _fill;
    private readonly AutoResetEvent _bufferDone = new AutoResetEvent(false);
    private readonly IntPtr[] _headers;
    private readonly IntPtr[] _data;
    private readonly int _bufferSize;
    private readonly Thread _feeder;
    private IntPtr _device;
    private volatile bool _stopping;

    public static int DeviceCount => waveOutGetNumDevs();

    public WaveOutPlayer(int device, WaveFormat format, int bufferSize, int bufferCount, BufferFillEventHandler fillProc)
      : this(device, format, 0, bufferSize, bufferCount, fillProc)
    {
    }

    /// <param name="device">The output device, -1 for the system's choice.</param>
    /// <param name="channelMask">Which speakers the channels go to (WAVEFORMATEXTENSIBLE), 0 for the plain layout.</param>
    public WaveOutPlayer(int device, WaveFormat format, int channelMask, int bufferSize, int bufferCount, BufferFillEventHandler fillProc)
    {
      _fill = fillProc;
      _bufferSize = bufferSize;

      var waveFormat = new WaveFormatExtensible
      {
        FormatTag = channelMask != 0 ? FormatExtensible : (ushort)FormatPcm,
        Channels = (ushort)format.nChannels,
        SamplesPerSec = (uint)format.nSamplesPerSec,
        AvgBytesPerSec = (uint)format.nAvgBytesPerSec,
        BlockAlign = (ushort)format.nBlockAlign,
        BitsPerSample = (ushort)format.wBitsPerSample,
        ExtraSize = channelMask != 0 ? (ushort)22 : (ushort)0,
        ValidBitsPerSample = (ushort)format.wBitsPerSample,
        ChannelMask = (uint)channelMask,
        SubFormat = SubtypePcm,
      };
      Check(waveOutOpen(out _device, device < 0 ? WaveMapper : device, ref waveFormat,
                        _bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent));

      _headers = new IntPtr[bufferCount];
      _data = new IntPtr[bufferCount];
      for (int i = 0; i < bufferCount; i++)
      {
        _data[i] = Marshal.AllocHGlobal(bufferSize);
        _headers[i] = Marshal.AllocHGlobal(HeaderSize);
        Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = (uint)bufferSize }, _headers[i], false);
        Check(waveOutPrepareHeader(_device, _headers[i], HeaderSize));
      }

      // The first round of buffers queued at once, the rest refilled as the device hands them back.
      foreach (IntPtr header in _headers)
      {
        Queue(header);
      }
      _feeder = new Thread(Feed) { IsBackground = true, Name = "WaveOutPlayer" };
      _feeder.Start();
    }

    private void Queue(IntPtr header)
    {
      int index = Array.IndexOf(_headers, header);
      _fill(_data[index], _bufferSize);
      Marshal.WriteInt32(header, FlagsOffset, Marshal.ReadInt32(header, FlagsOffset) & ~WhdrDone);
      Check(waveOutWrite(_device, header, HeaderSize));
    }

    private void Feed()
    {
      while (!_stopping)
      {
        _bufferDone.WaitOne();
        if (_stopping)
        {
          break;
        }
        foreach (IntPtr header in _headers)
        {
          if (!_stopping && (Marshal.ReadInt32(header, FlagsOffset) & WhdrDone) != 0)
          {
            Queue(header);
          }
        }
      }
    }

    public void Pause()
    {
      waveOutPause(_device);
    }

    public void Resume()
    {
      waveOutRestart(_device);
    }

    /// <summary>How many sample frames the device has played since it was opened.</summary>
    public long GetPlayedFrames()
    {
      var time = new MmTime { Type = TimeSamples };
      return waveOutGetPosition(_device, ref time, Marshal.SizeOf<MmTime>()) == 0 && time.Type == TimeSamples ? time.Value : 0;
    }

    public void Dispose()
    {
      if (_device == IntPtr.Zero)
      {
        return;
      }
      _stopping = true;
      waveOutReset(_device);
      _bufferDone.Set();
      _feeder.Join();
      for (int i = 0; i < _headers.Length; i++)
      {
        waveOutUnprepareHeader(_device, _headers[i], HeaderSize);
        Marshal.FreeHGlobal(_headers[i]);
        Marshal.FreeHGlobal(_data[i]);
      }
      waveOutClose(_device);
      _device = IntPtr.Zero;
      _bufferDone.Dispose();
    }

    private static void Check(int result)
    {
      if (result != 0)
      {
        throw new InvalidOperationException("waveOut error " + result);
      }
    }
  }
}
