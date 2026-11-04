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

using System.Runtime.InteropServices;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// A mouse drag read on the render thread: the cursor's place taken right before each frame is built (not the UI
  /// thread's pointer events, which come in bursts and a frame late), so every frame turns by the mouse's whole move since
  /// the last one. Begun and ended from the UI thread's press and release (their cursor places passed in); the moves in
  /// device-independent pixels (the page's CSS pixels). Not thread-safe: <see cref="Begin"/>, <see cref="End"/> and
  /// <see cref="Take"/> on the one thread (the render thread, through the scene's orders).
  /// </summary>
  public sealed class CursorDrag
  {
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
      public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>The cursor on the screen, in physical pixels.</summary>
    public static (int X, int Y) Cursor() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>Whether Shift is held now.</summary>
    public static bool ShiftDown => (GetAsyncKeyState(0x10) & 0x8000) != 0;

    private bool _active;
    private int _button;
    private double _scale = 1;
    private (int X, int Y) _last;
    private (int X, int Y)? _end;

    /// <summary>A press: its button (0 left, 1 middle, 2 right), the screen's pixels per DIP, the cursor then.</summary>
    public void Begin(int button, double scale, (int X, int Y) at)
    {
      _active = true;
      _button = button;
      _scale = scale > 0 ? scale : 1;
      _last = at;
      _end = null;
    }

    /// <summary>The release: the cursor then (the last move taken up to there).</summary>
    public void End((int X, int Y) at)
    {
      if (_active)
      {
        _end = at;
      }
    }

    /// <summary>The move since the last frame (in DIPs) and the drag's button; false without a drag.</summary>
    public bool Take(out float dx, out float dy, out int button)
    {
      button = _button;
      if (!_active)
      {
        dx = dy = 0;
        return false;
      }
      var at = _end ?? Cursor();
      dx = (float)((at.X - _last.X) / _scale);
      dy = (float)((at.Y - _last.Y) / _scale);
      _last = at;
      if (_end != null)
      {
        _active = false;
        _end = null;
      }
      return true;
    }
  }
}
