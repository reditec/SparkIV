//  THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
//  KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
//  IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR
//  PURPOSE.
//
//  This material may not be duplicated in whole or in part, except for 
//  personal use, without the express written consent of the author. 
//
//  Email:  ianier@hotmail.com
//
//  Copyright (C) 1999-2003 Ianier Munoz. All Rights Reserved.

using System;
using System.Runtime.InteropServices;

namespace WaveLib
{
  public enum WaveFormats
  {
    Pcm = 1,
    Float = 3
  }

  [StructLayout(LayoutKind.Sequential)]
  public class WaveFormat
  {
    public short wFormatTag;
    public short nChannels;
    public int nSamplesPerSec;
    public int nAvgBytesPerSec;
    public short nBlockAlign;
    public short wBitsPerSample;
    public short cbSize;

    public WaveFormat(int rate, int bits, int channels)
    {
      wFormatTag = (short)WaveFormats.Pcm;
      nChannels = (short)channels;
      nSamplesPerSec = rate;
      wBitsPerSample = (short)bits;
      cbSize = 0;

      nBlockAlign = (short)(channels * (bits / 8));
      nAvgBytesPerSec = nSamplesPerSec * nBlockAlign;
    }
  }

  // WAVEFORMATEXTENSIBLE, needed for more than two channels so Windows knows the speaker of each channel.
  [StructLayout(LayoutKind.Sequential, Pack = 2)]
  public class WaveFormatExtensible
  {
    public short wFormatTag;
    public short nChannels;
    public int nSamplesPerSec;
    public int nAvgBytesPerSec;
    public short nBlockAlign;
    public short wBitsPerSample;
    public short cbSize;
    public short wValidBitsPerSample;
    public int dwChannelMask;
    public Guid SubFormat;

    private static readonly Guid KsDataFormatSubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

    public WaveFormatExtensible(WaveFormat format, int channelMask)
    {
      wFormatTag = unchecked((short)0xFFFE);
      nChannels = format.nChannels;
      nSamplesPerSec = format.nSamplesPerSec;
      nAvgBytesPerSec = format.nAvgBytesPerSec;
      nBlockAlign = format.nBlockAlign;
      wBitsPerSample = format.wBitsPerSample;
      cbSize = 22;
      wValidBitsPerSample = format.wBitsPerSample;
      dwChannelMask = channelMask;
      SubFormat = KsDataFormatSubtypePcm;
    }
  }

  internal class WaveNative
  {
    // consts
    public const int MMSYSERR_NOERROR = 0; // no error

    public const int MM_WOM_OPEN = 0x3BB;
    public const int MM_WOM_CLOSE = 0x3BC;
    public const int MM_WOM_DONE = 0x3BD;

    public const int CALLBACK_FUNCTION = 0x00030000;    // dwCallback is a FARPROC 

    public const int TIME_MS = 0x0001;  // time in milliseconds 
    public const int TIME_SAMPLES = 0x0002;  // number of wave samples 
    public const int TIME_BYTES = 0x0004;  // current byte offset 

    // callbacks
    // dwInstance/dwParam are DWORD_PTR. dwParam1 is the WAVEHDR, which must stay at a fixed native address.
    public delegate void WaveDelegate(IntPtr hdrvr, int uMsg, IntPtr dwInstance, IntPtr wavhdr, IntPtr dwParam2);

    // structs 

    [StructLayout(LayoutKind.Sequential)]
    public struct WaveHdr
    {
      public IntPtr lpData; // pointer to locked data buffer
      public int dwBufferLength; // length of data buffer
      public int dwBytesRecorded; // used for input only
      public IntPtr dwUser; // for client's use
      public int dwFlags; // assorted flags (see defines)
      public int dwLoops; // loop control counter
      public IntPtr lpNext; // PWaveHdr, reserved for driver
      public IntPtr reserved; // reserved for driver (DWORD_PTR)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MmTime
    {
      public int wType;
      public int u; // sample or byte count, depending on wType
      public int padding; // the union is 8 bytes wide (SMPTE)
    }

    private const string mmdll = "winmm.dll";

    // native calls
    [DllImport(mmdll)]
    public static extern int waveOutGetNumDevs();
    [DllImport(mmdll)]
    public static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport(mmdll)]
    public static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport(mmdll)]
    public static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport(mmdll)]
    public static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, WaveFormat lpFormat, WaveDelegate dwCallback, IntPtr dwInstance, int dwFlags);
    [DllImport(mmdll)]
    public static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, WaveFormatExtensible lpFormat, WaveDelegate dwCallback, IntPtr dwInstance, int dwFlags);
    [DllImport(mmdll)]
    public static extern int waveOutReset(IntPtr hWaveOut);
    [DllImport(mmdll)]
    public static extern int waveOutClose(IntPtr hWaveOut);
    [DllImport(mmdll)]
    public static extern int waveOutPause(IntPtr hWaveOut);
    [DllImport(mmdll)]
    public static extern int waveOutRestart(IntPtr hWaveOut);
    [DllImport(mmdll)]
    public static extern int waveOutGetPosition(IntPtr hWaveOut, ref MmTime lpInfo, int uSize);
    [DllImport(mmdll)]
    public static extern int waveOutSetVolume(IntPtr hWaveOut, int dwVolume);
    [DllImport(mmdll)]
    public static extern int waveOutGetVolume(IntPtr hWaveOut, out int dwVolume);
  }
}
