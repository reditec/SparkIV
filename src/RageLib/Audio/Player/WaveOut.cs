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
using System.Threading;
using System.Runtime.InteropServices;

namespace WaveLib
{
  internal class WaveOutHelper
  {
    public static void Try(int err)
    {
      if (err != WaveNative.MMSYSERR_NOERROR)
        throw new Exception(err.ToString());
    }
  }

  public delegate void BufferFillEventHandler(IntPtr data, int size);

  internal class WaveOutBuffer : IDisposable
  {
    public WaveOutBuffer NextBuffer;

    private AutoResetEvent m_PlayEvent = new AutoResetEvent(false);
    private IntPtr m_WaveOut;

    // The driver keeps using the WAVEHDR (and the sample data) until the buffer is done, so
    // both live in native memory. A header inside a managed object gets moved by the GC, which
    // makes the driver skip or replay buffers.
    private static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveNative.WaveHdr));
    private static readonly int UserOffset = (int)Marshal.OffsetOf(typeof(WaveNative.WaveHdr), "dwUser");

    private IntPtr m_Header;
    private IntPtr m_Data;
    private int m_Size;
    private GCHandle m_Self;

    // True from waveOutWrite until the driver reported MM_WOM_DONE for this buffer.
    private volatile bool m_Playing;

    internal static void WaveOutProc(IntPtr hdrvr, int uMsg, IntPtr dwInstance, IntPtr wavhdr, IntPtr dwParam2)
    {
      if (uMsg != WaveNative.MM_WOM_DONE || wavhdr == IntPtr.Zero)
        return;

      try
      {
        IntPtr user = Marshal.ReadIntPtr(wavhdr, UserOffset);
        if (user == IntPtr.Zero)
          return;

        var buf = GCHandle.FromIntPtr(user).Target as WaveOutBuffer;
        if (buf != null)
          buf.OnCompleted();
      }
      catch
      {
        // Never let an exception escape into the winmm callback thread.
      }
    }

    public WaveOutBuffer(IntPtr waveOutHandle, int size)
    {
      m_WaveOut = waveOutHandle;
      m_Size = size;
      m_Self = GCHandle.Alloc(this);

      m_Data = Marshal.AllocHGlobal(size);
      m_Header = Marshal.AllocHGlobal(HeaderSize);

      var header = new WaveNative.WaveHdr
                     {
                       lpData = m_Data,
                       dwBufferLength = size,
                       dwUser = GCHandle.ToIntPtr(m_Self)
                     };
      Marshal.StructureToPtr(header, m_Header, false);

      WaveOutHelper.Try(WaveNative.waveOutPrepareHeader(m_WaveOut, m_Header, HeaderSize));
    }
    ~WaveOutBuffer()
    {
      Dispose();
    }
    public void Dispose()
    {
      if (m_Playing)
      {
        // The driver may still call back with this header. Leaking a few KB is better than
        // letting it touch freed memory.
        GC.SuppressFinalize(this);
        return;
      }

      if (m_Header != IntPtr.Zero)
      {
        WaveNative.waveOutUnprepareHeader(m_WaveOut, m_Header, HeaderSize);
        Marshal.FreeHGlobal(m_Header);
        m_Header = IntPtr.Zero;
      }
      if (m_Data != IntPtr.Zero)
      {
        Marshal.FreeHGlobal(m_Data);
        m_Data = IntPtr.Zero;
      }
      if (m_Self.IsAllocated)
        m_Self.Free();
      m_PlayEvent.Close();
      GC.SuppressFinalize(this);
    }

    public int Size
    {
      get { return m_Size; }
    }

    public IntPtr Data
    {
      get { return m_Data; }
    }

    public bool Play()
    {
      lock (this)
      {
        m_PlayEvent.Reset();
        // Set before writing: the done callback can arrive before waveOutWrite returns.
        m_Playing = true;
        if (WaveNative.waveOutWrite(m_WaveOut, m_Header, HeaderSize) != WaveNative.MMSYSERR_NOERROR)
          m_Playing = false;
        return m_Playing;
      }
    }
    public void WaitFor()
    {
      if (m_Playing)
      {
        m_PlayEvent.WaitOne();
      }
      else
      {
        Thread.Sleep(0);
      }
    }
    /// <returns>False if the driver didn't release the buffer in time.</returns>
    public bool WaitForCompletion(int timeout)
    {
      return !m_Playing || m_PlayEvent.WaitOne(timeout) || !m_Playing;
    }
    public void OnCompleted()
    {
      m_Playing = false;
      m_PlayEvent.Set();
    }
  }

  public class WaveOutPlayer : IDisposable
  {
    private IntPtr m_WaveOut;
    private WaveOutBuffer m_Buffers; // linked list
    private WaveOutBuffer m_CurrentBuffer;
    private Thread m_Thread;
    private BufferFillEventHandler m_FillProc;
    private volatile bool m_Finished;
    private byte m_zero;
    private int m_BlockAlign;

    private WaveNative.WaveDelegate m_BufferProc = new WaveNative.WaveDelegate(WaveOutBuffer.WaveOutProc);

    public static int DeviceCount
    {
      get { return WaveNative.waveOutGetNumDevs(); }
    }

    public WaveOutPlayer(int device, WaveFormat format, int bufferSize, int bufferCount, BufferFillEventHandler fillProc)
      : this(device, format, 0, bufferSize, bufferCount, fillProc)
    {
    }
    /// <param name="channelMask">Speaker mask (WAVE_FORMAT_EXTENSIBLE) for more than two channels, 0 for plain PCM.</param>
    public WaveOutPlayer(int device, WaveFormat format, int channelMask, int bufferSize, int bufferCount, BufferFillEventHandler fillProc)
    {
      m_BlockAlign = format.nBlockAlign;
      m_zero = format.wBitsPerSample == 8 ? (byte)128 : (byte)0;
      m_FillProc = fillProc;
      if (channelMask != 0)
        WaveOutHelper.Try(WaveNative.waveOutOpen(out m_WaveOut, device, new WaveFormatExtensible(format, channelMask), m_BufferProc, IntPtr.Zero, WaveNative.CALLBACK_FUNCTION));
      else
        WaveOutHelper.Try(WaveNative.waveOutOpen(out m_WaveOut, device, format, m_BufferProc, IntPtr.Zero, WaveNative.CALLBACK_FUNCTION));
      AllocateBuffers(bufferSize, bufferCount);
      m_Thread = new Thread(new ThreadStart(ThreadProc));
      m_Thread.Start();
    }
    ~WaveOutPlayer()
    {
      Dispose();
    }
    public void Dispose()
    {
      if (m_Thread != null)
        try
        {
          m_Finished = true;
          if (m_WaveOut != IntPtr.Zero)
            WaveNative.waveOutReset(m_WaveOut);
          m_Thread.Join();
          m_FillProc = null;
          FreeBuffers();
          if (m_WaveOut != IntPtr.Zero)
            WaveNative.waveOutClose(m_WaveOut);
        }
        finally
        {
          m_Thread = null;
          m_WaveOut = IntPtr.Zero;
        }
      //GC.SuppressFinalize(this);
    }
    public void Pause()
    {
      if (m_WaveOut != IntPtr.Zero)
        WaveNative.waveOutPause(m_WaveOut);
    }
    public void Resume()
    {
      if (m_WaveOut != IntPtr.Zero)
        WaveNative.waveOutRestart(m_WaveOut);
    }
    /// <summary>
    /// Sample frames played by the device since it was opened.
    /// </summary>
    public long GetPlayedFrames()
    {
      if (m_WaveOut == IntPtr.Zero)
        return 0;

      var time = new WaveNative.MmTime { wType = WaveNative.TIME_SAMPLES };
      if (WaveNative.waveOutGetPosition(m_WaveOut, ref time, Marshal.SizeOf(time)) != WaveNative.MMSYSERR_NOERROR)
        return 0;

      // The driver may fall back to another format than the one requested.
      if (time.wType == WaveNative.TIME_BYTES)
        return (uint)time.u / m_BlockAlign;
      return (uint)time.u;
    }
    private void ThreadProc()
    {
      while (!m_Finished)
      {
        Advance();
        if (m_FillProc != null && !m_Finished)
          m_FillProc(m_CurrentBuffer.Data, m_CurrentBuffer.Size);
        else
        {
          // zero out buffer
          byte v = m_zero;
          byte[] b = new byte[m_CurrentBuffer.Size];
          for (int i = 0; i < b.Length; i++)
            b[i] = v;
          Marshal.Copy(b, 0, m_CurrentBuffer.Data, b.Length);

        }
        // Don't queue anything after Dispose has reset the device.
        if (!m_Finished)
          m_CurrentBuffer.Play();
      }
      WaitForAllBuffers();
    }
    private void AllocateBuffers(int bufferSize, int bufferCount)
    {
      FreeBuffers();
      if (bufferCount > 0)
      {
        m_Buffers = new WaveOutBuffer(m_WaveOut, bufferSize);
        WaveOutBuffer Prev = m_Buffers;
        try
        {
          for (int i = 1; i < bufferCount; i++)
          {
            WaveOutBuffer Buf = new WaveOutBuffer(m_WaveOut, bufferSize);
            Prev.NextBuffer = Buf;
            Prev = Buf;
          }
        }
        finally
        {
          Prev.NextBuffer = m_Buffers;
        }
      }
    }
    private void FreeBuffers()
    {
      m_CurrentBuffer = null;
      if (m_Buffers != null)
      {
        WaveOutBuffer First = m_Buffers;
        m_Buffers = null;

        WaveOutBuffer Current = First;
        do
        {
          WaveOutBuffer Next = Current.NextBuffer;
          Current.Dispose();
          Current = Next;
        } while (Current != First);
      }
    }
    private void Advance()
    {
      m_CurrentBuffer = m_CurrentBuffer == null ? m_Buffers : m_CurrentBuffer.NextBuffer;
      m_CurrentBuffer.WaitFor();
    }
    // Every queued buffer gets exactly one MM_WOM_DONE; wait for all of them (the old loop
    // skipped the last buffer) so FreeBuffers never frees memory the driver still uses.
    private void WaitForAllBuffers()
    {
      WaveOutBuffer Buf = m_Buffers;
      do
      {
        Buf.WaitForCompletion(2000);
        Buf = Buf.NextBuffer;
      } while (Buf != m_Buffers);
    }
  }
}
