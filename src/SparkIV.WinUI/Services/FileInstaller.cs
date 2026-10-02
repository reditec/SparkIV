using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Puts written files in place of game files. Where SparkIV may write it does that itself; into a
  /// protected folder (below Program Files) a second SparkIV with administrator rights does it, started
  /// for just that ("--install-files"): Windows asks once, SparkIV goes on as it is.
  /// </summary>
  internal static class FileInstaller
  {
    public const string HelperArgument = "--install-files";

    /// <summary>Whether putting files into these folders needs administrator rights.</summary>
    public static bool NeedsAdministrator(IEnumerable<string> targets) =>
      targets.Select(t => Path.GetDirectoryName(t)).Distinct(StringComparer.OrdinalIgnoreCase).Any(EditMode.NeedsAdministrator);

    /// <summary>
    /// Moves each written file (source, a temporary file) to its target, replacing it. Throws if that
    /// wasn't possible, also when the user said no to Windows' question; then no target was changed by
    /// the helper (it stops at the first file it can't place).
    /// </summary>
    public static void Install(IReadOnlyList<(string Source, string Target)> files)
    {
      if (files.Count == 0)
      {
        return;
      }

      if (!NeedsAdministrator(files.Select(f => f.Target)))
      {
        foreach (var (source, target) in files)
        {
          Place(source, target);
        }
        return;
      }

      // The list for the helper: one "source<TAB>target" per line.
      string list = Path.Combine(Path.GetTempPath(), "SparkIV-install-" + Guid.NewGuid().ToString("N") + ".txt");
      System.IO.File.WriteAllLines(list, files.Select(f => f.Source + "\t" + f.Target));
      try
      {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
          UseShellExecute = true,
          Verb = "runas",
          Arguments = HelperArgument + " \"" + list + "\"",
          WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? helper;
        try
        {
          helper = Process.Start(info);
        }
        catch (Win32Exception)
        {
          throw new OperationCanceledException("Windows didn't give SparkIV the administrator rights to write the files. Nothing was changed.");
        }
        if (helper == null)
        {
          throw new IOException("SparkIV couldn't start the helper that writes the files.");
        }
        helper.WaitForExit();
        if (helper.ExitCode != 0)
        {
          throw new IOException("The files couldn't be written (" + helper.ExitCode + "). The details are in " + ErrorFile + ".");
        }
      }
      finally
      {
        TryDelete(list);
      }
    }

    // What the helper couldn't do, for the message in the SparkIV that asked.
    private static string ErrorFile => Path.Combine(Path.GetTempPath(), "SparkIV-install-error.txt");

    /// <summary>
    /// The helper: SparkIV started as administrator with the list. Returns the exit code: 0 if all files
    /// are in place.
    /// </summary>
    public static int RunHelper(string list)
    {
      try
      {
        foreach (var line in System.IO.File.ReadAllLines(list))
        {
          string[] parts = line.Split('\t');
          if (parts.Length == 2)
          {
            Place(parts[0], parts[1]);
          }
        }
        return 0;
      }
      catch (Exception ex)
      {
        try
        {
          System.IO.File.WriteAllText(ErrorFile, ex.ToString());
        }
        catch (Exception)
        {
        }
        return 1;
      }
    }

    /// <summary>Copies next to the target first, then moves it over: the target is never half written.</summary>
    private static void Place(string source, string target)
    {
      string next = target + ".sparkiv-new";
      System.IO.File.Copy(source, next, overwrite: true);
      System.IO.File.Move(next, target, overwrite: true);
      TryDelete(source);
    }

    private static void TryDelete(string path)
    {
      try
      {
        System.IO.File.Delete(path);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
  }
}
