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
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RageLib.Audio
{
  /// <summary>
  /// A horizontal TrackBar that jumps straight to the clicked position instead of moving by
  /// LargeChange, and keeps dragging from there while the button is held.
  /// </summary>
  internal class SeekTrackBar : TrackBar
  {
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int TBM_GETTHUMBRECT = 0x0400 + 25;
    private const int TBM_GETCHANNELRECT = 0x0400 + 26;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
      public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

    protected override void WndProc(ref Message m)
    {
      if (m.Msg == WM_LBUTTONDOWN && Orientation == Orientation.Horizontal)
      {
        int x = (short)((long)m.LParam & 0xFFFF);
        int y = (short)(((long)m.LParam >> 16) & 0xFFFF);

        var thumb = new RECT();
        var channel = new RECT();
        SendMessage(Handle, TBM_GETTHUMBRECT, IntPtr.Zero, ref thumb);
        SendMessage(Handle, TBM_GETCHANNELRECT, IntPtr.Zero, ref channel);

        bool onThumb = x >= thumb.Left && x < thumb.Right && y >= thumb.Top && y < thumb.Bottom;
        if (!onThumb)
        {
          Value = ValueFromPixel(x, thumb, channel);
        }
      }

      // With the thumb now under the cursor the native control starts a normal drag.
      base.WndProc(ref m);
    }

    // Same mapping the common control uses to place the thumb.
    private int ValueFromPixel(int x, RECT thumb, RECT channel)
    {
      double halfThumb = (thumb.Right - thumb.Left) / 2.0;
      double width = channel.Right - channel.Left - halfThumb * 2 - 1;
      if (width <= 0)
      {
        return Value;
      }

      double position = (Maximum - Minimum) * (x - channel.Left - halfThumb) / width + Minimum;
      return (int)Math.Max(Minimum, Math.Min(Maximum, Math.Round(position)));
    }
  }
}
