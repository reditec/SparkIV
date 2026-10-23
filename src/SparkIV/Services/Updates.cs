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
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Updates from the releases on GitHub (Velopack). An alpha or beta installation also gets alpha and beta
  /// releases, a release installation only releases. Only an installed SparkIV updates, not one run from
  /// its build folder. For testing, the environment variable SPARKIV_UPDATE_SOURCE can name a local folder
  /// with packed releases instead.
  /// </summary>
  internal static class Updates
  {
    private const string Repository = "https://github.com/reditec/SparkIV";

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    /// <summary>The newer version, or null if there is none, SparkIV isn't installed or the check failed.</summary>
    public static async Task<string?> CheckAsync()
    {
      try
      {
        string? folder = Environment.GetEnvironmentVariable("SPARKIV_UPDATE_SOURCE");
        // Whether this is a pre-release is only known once the manager has read the installed version.
        var probe = new UpdateManager(new GithubSource(Repository, null, false));
        if (!probe.IsInstalled)
        {
          return null;
        }

        bool prerelease = probe.CurrentVersion?.IsPrerelease ?? false;
        IUpdateSource source = string.IsNullOrEmpty(folder)
          ? new GithubSource(Repository, null, prerelease)
          : new SimpleFileSource(new System.IO.DirectoryInfo(folder));
        _manager = new UpdateManager(source);
        _pending = await _manager.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease.Version.ToString();
      }
      catch (Exception)
      {
        // No connection or no release yet: just no update.
        return null;
      }
    }

    /// <summary>Downloads the update found by <see cref="CheckAsync"/>, then restarts SparkIV into it.</summary>
    public static async Task InstallAndRestartAsync()
    {
      if (_manager == null || _pending == null)
      {
        return;
      }
      await _manager.DownloadUpdatesAsync(_pending);
      _manager.ApplyUpdatesAndRestart(_pending);
    }
  }
}
