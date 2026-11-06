/**********************************************************************\

 SparkIV
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

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// Chooses per frame how the render loop meets the swap chain's waitable object (#112), for the least input latency
  /// at the screen's rate:
  /// <list type="bullet">
  /// <item><b>Serial</b> (wait, then update, record, send, present): the input is read right after the screen took the
  /// last frame and shown at the next refresh - one refresh of latency - but only while the CPU's part (update and
  /// recording) and the GPU's together fit in a refresh.</item>
  /// <item><b>Pipelined</b> (update, record, send, then wait, present): the next frame is recorded while the GPU still
  /// draws the last, so the rate is set by the larger of the two, not their sum; the input is shown two refreshes
  /// later, which is the least possible once both together take longer than a refresh.</item>
  /// </list>
  /// Serial while the costliest of the last <see cref="Window"/> frames' CPU + GPU times stays under the refresh less
  /// <see cref="EnterMargin"/>; pipelined from there until it is under the refresh less <see cref="LeaveMargin"/> for a
  /// whole window. SPARKIV_D3D12_PACING=serial or pipelined fixes the mode.
  /// </summary>
  public sealed class FramePacer
  {
    public const int Window = 30;
    public const double EnterMargin = 1.5;
    public const double LeaveMargin = 3.0;

    private readonly double[] _costs = new double[Window];
    private int _next, _count, _calmFrames;
    private readonly bool? _fixed;
    private double _periodAt = double.NegativeInfinity;

    public FramePacer()
    {
      _fixed = Environment.GetEnvironmentVariable("SPARKIV_D3D12_PACING") switch
      {
        "serial" => false,
        "pipelined" => true,
        _ => null,
      };
      Pipelined = _fixed ?? false;
    }

    /// <summary>Whether the next frame is pipelined (wait before Present) or serial (wait before the update).</summary>
    public bool Pipelined { get; private set; }

    /// <summary>The screen's refresh period (ms; DWM's composition rate, 60 Hz when not known).</summary>
    public double PeriodMs { get; private set; } = 1000.0 / 60;

    /// <summary>The costliest CPU + GPU time of the window (ms).</summary>
    public double SerialCostMs { get; private set; }

    /// <summary>
    /// After a frame: its CPU time from the start of its update to its submission and the GPU time of a recent frame
    /// (ms); <paramref name="now"/> in seconds (the refresh period is read again once a second).
    /// </summary>
    public void Record(double cpuMs, double gpuMs, double now)
    {
      if (now - _periodAt >= 1)
      {
        _periodAt = now;
        PeriodMs = RefreshPeriodMs() ?? PeriodMs;
      }
      _costs[_next] = cpuMs + gpuMs;
      _next = (_next + 1) % Window;
      _count = Math.Min(_count + 1, Window);
      double worst = 0;
      for (int i = 0; i < _count; i++)
      {
        worst = Math.Max(worst, _costs[i]);
      }
      SerialCostMs = worst;
      if (_fixed != null)
      {
        return;
      }
      if (!Pipelined)
      {
        if (cpuMs + gpuMs > PeriodMs - EnterMargin)
        {
          Pipelined = true;
          _calmFrames = 0;
        }
      }
      else
      {
        _calmFrames = cpuMs + gpuMs < PeriodMs - LeaveMargin ? _calmFrames + 1 : 0;
        if (_calmFrames >= Window && worst < PeriodMs - LeaveMargin)
        {
          Pipelined = false;
        }
      }
    }

    // DWM's refresh rate (rateRefresh of DWM_TIMING_INFO: 292 bytes, packed; the ratio at offset 4).
    private static unsafe double? RefreshPeriodMs()
    {
      try
      {
        var info = stackalloc byte[292];
        new Span<byte>(info, 292).Clear();
        *(uint*)info = 292;
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, info) != 0)
        {
          return null;
        }
        uint numerator = *(uint*)(info + 4), denominator = *(uint*)(info + 8);
        if (numerator == 0 || denominator == 0)
        {
          return null;
        }
        double ms = 1000.0 * denominator / numerator;
        return ms is > 2 and < 100 ? ms : null;
      }
      catch (Exception)
      {
        return null;
      }
    }

    [DllImport("dwmapi.dll")]
    private static extern unsafe int DwmGetCompositionTimingInfo(IntPtr hwnd, byte* info);
  }
}
