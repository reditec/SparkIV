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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// How long opening something took and its steps, appended to SparkIV-load.log beside SparkIV.exe: a
  /// block per opening (when, what, each step's seconds and notes, the total). Steps measured in parts (the
  /// textures of hundreds of models) are added up under one name.
  /// </summary>
  public sealed class LoadLog
  {
    private readonly string _what;
    private readonly DateTime _started = DateTime.Now;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _step = Stopwatch.StartNew();
    private readonly List<(string Name, double Seconds, string? Note)> _steps = new List<(string, double, string?)>();
    private bool _written;

    public LoadLog(string what) => _what = what;

    /// <summary>The step since the last one (or the start) is done.</summary>
    public void Step(string name, string? note = null)
    {
      // Locked: a view's steps and a background load's (the cutscene's cast) may come at the same time.
      lock (_steps)
      {
        _steps.Add((name, _step.Elapsed.TotalSeconds, note));
        _step.Restart();
      }
    }

    /// <summary>A step measured elsewhere (seconds), not counted against the next one.</summary>
    public void Add(string name, double seconds, string? note = null)
    {
      lock (_steps)
      {
        _steps.Add((name, seconds, note));
      }
    }

    /// <summary>Starts timing the next step from now (after waiting for something not to be counted).</summary>
    public void Restart() => _step.Restart();

    /// <summary>Appends the block to the log (once); errors writing it are ignored.</summary>
    public void Write()
    {
      if (_written)
      {
        return;
      }
      _written = true;
      var text = new StringBuilder();
      text.AppendLine(_started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + _what);
      int width = Math.Max(10, _steps.Select(s => s.Name.Length).DefaultIfEmpty(0).Max());
      foreach (var (name, seconds, note) in _steps)
      {
        text.AppendLine("  " + name.PadRight(width) + "  " + seconds.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(7) + " s" + (note != null ? "   " + note : ""));
      }
      text.AppendLine("  " + "total".PadRight(width) + "  " + _total.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(7) + " s");
      text.AppendLine();
      try
      {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SparkIV-load.log"), text.ToString());
      }
      catch (Exception)
      {
        // A folder not to be written in: no log.
      }
    }
  }
}
