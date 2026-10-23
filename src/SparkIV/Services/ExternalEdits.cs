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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using SparkIV.WinUI.Model;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Files opened in another program: each is written into a temporary folder and opened with the program
  /// Windows has for its type (or the "Open with" choice). SparkIV doesn't wait for the program; it watches
  /// the copy, and when it is saved there it tells (<see cref="Changed"/>), so the change can be taken
  /// into the archive (<see cref="Apply"/>) and saved from there like an import, with backup.
  /// </summary>
  internal sealed class ExternalEdits : IDisposable
  {
    public sealed class Edit
    {
      internal Edit(File file, Volume volume, string path)
      {
        File = file;
        Volume = volume;
        Path = path;
      }

      public File File { get; }
      public Volume Volume { get; }

      /// <summary>The copy the other program has open.</summary>
      public string Path { get; }

      /// <summary>Saved in the other program and not taken yet.</summary>
      public bool IsChanged { get; internal set; }

      internal FileSystemWatcher? Watcher { get; set; }
      internal DispatcherQueueTimer? Settle { get; set; }
    }

    private readonly DispatcherQueue _dispatcher;
    private readonly List<Edit> _edits = new List<Edit>();

    public ExternalEdits(DispatcherQueue dispatcher)
    {
      _dispatcher = dispatcher;
    }

    /// <summary>A copy was saved in the other program (on the UI thread).</summary>
    public event Action<Edit>? Changed;

    /// <summary>The copies saved and not taken yet.</summary>
    public IReadOnlyList<Edit> Pending => _edits.Where(e => e.IsChanged).ToList();

    /// <summary>Opens a file in the program for its type; the same file again opens the copy it has already.</summary>
    public void Open(File file, Volume volume)
    {
      var edit = _edits.FirstOrDefault(e => ReferenceEquals(e.File, file));
      if (edit == null)
      {
        // A folder of its own, so the copy keeps the file's name (the program shows it, and knows the type by it).
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SparkIV-edit", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, file.Name);
        System.IO.File.WriteAllBytes(path, file.GetData());
        edit = new Edit(file, volume, path);
        Watch(edit);
        _edits.Add(edit);
      }
      Launch(edit.Path);
    }

    private static void Launch(string path)
    {
      try
      {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
      }
      catch (Win32Exception)
      {
        // No program for this type (most game formats): Windows' "Open with" choice.
        Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false });
      }
    }

    private void Watch(Edit edit)
    {
      // Programs save in different ways (written in place, or a new file renamed over it); a short pause
      // after the last change, then it counts as saved.
      var watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(edit.Path)!, System.IO.Path.GetFileName(edit.Path))
      {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
      };
      var settle = _dispatcher.CreateTimer();
      settle.Interval = TimeSpan.FromMilliseconds(600);
      settle.IsRepeating = false;
      settle.Tick += (s, args) =>
      {
        if (_edits.Contains(edit))
        {
          edit.IsChanged = true;
          Changed?.Invoke(edit);
        }
      };
      void Touched(object sender, FileSystemEventArgs args) => _dispatcher.TryEnqueue(() =>
      {
        settle.Stop();
        settle.Start();
      });
      watcher.Changed += Touched;
      watcher.Created += Touched;
      watcher.Renamed += (s, args) => Touched(s, args);
      watcher.EnableRaisingEvents = true;
      edit.Watcher = watcher;
      edit.Settle = settle;
    }

    /// <summary>
    /// Takes the saved copy into the file (in the archive; written with Save). Returns false if the program
    /// still has it locked; then it can be tried again.
    /// </summary>
    public bool Apply(Edit edit)
    {
      byte[] data;
      try
      {
        data = System.IO.File.ReadAllBytes(edit.Path);
      }
      catch (IOException)
      {
        return false;
      }
      edit.File.SetData(data);
      edit.IsChanged = false;
      return true;
    }

    /// <summary>Stops watching the copies of a volume that is closed, and removes them.</summary>
    public void Forget(Func<Edit, bool> which)
    {
      foreach (var edit in _edits.Where(which).ToList())
      {
        edit.Watcher?.Dispose();
        edit.Settle?.Stop();
        _edits.Remove(edit);
        try
        {
          System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(edit.Path)!, recursive: true);
        }
        catch (IOException)
        {
          // Still open in the other program; the temporary folder is left for Windows to clean up.
        }
        catch (UnauthorizedAccessException)
        {
        }
      }
    }

    public void Dispose() => Forget(e => true);
  }
}
