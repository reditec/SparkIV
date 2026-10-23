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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SparkIV.WinUI.Services
{
  /// <summary>Which releases an installation updates to; each channel also gets the ones after it.</summary>
  internal enum UpdateChannel
  {
    /// <summary>Alpha, beta and release versions.</summary>
    Alpha,
    /// <summary>Beta and release versions.</summary>
    Beta,
    /// <summary>Release versions only.</summary>
    Release,
  }

  /// <summary>
  /// Updates from the releases on GitHub (Velopack), in the channel chosen in the settings. Without a choice,
  /// the channel of the running version applies (0.9.0-alpha.1: Alpha). Only an installed SparkIV updates,
  /// not one run from its build folder. For testing, the environment variable SPARKIV_UPDATE_SOURCE can name
  /// a local folder with packed releases instead.
  /// </summary>
  internal static class Updates
  {
    private const string Repository = "https://github.com/reditec/SparkIV";

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    /// <summary>The running version, e.g. "0.9.0-alpha.1" (without the commit the build appends).</summary>
    public static string CurrentVersion { get; } =
      (typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static UpdateChannel Channel
    {
      get => Enum.TryParse(AppSettings.UpdateChannel, out UpdateChannel channel) ? channel : ChannelOf(CurrentVersion);
      set => AppSettings.UpdateChannel = value.ToString();
    }

    /// <summary>The channel a version belongs to: by its label, "0.9.0-alpha.1" Alpha, "0.9.0-beta.2" Beta, "0.9.0" Release.</summary>
    public static UpdateChannel ChannelOf(string version)
    {
      int dash = version.IndexOf('-');
      if (dash < 0)
      {
        return UpdateChannel.Release;
      }
      return version.Substring(dash + 1).StartsWith("alpha", StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Alpha : UpdateChannel.Beta;
    }

    /// <summary>Whether a version is offered in a channel: Alpha takes all, Beta betas and releases, Release only releases.</summary>
    public static bool Offers(UpdateChannel channel, string version) => ChannelOf(version) >= channel;

    /// <summary>The newer version, or null if there is none, SparkIV isn't installed or the check failed.</summary>
    public static async Task<string?> CheckAsync()
    {
      try
      {
        _manager = null;
        _pending = null;
        if (!new UpdateManager(new GithubSource(Repository, null, false)).IsInstalled)
        {
          return null;
        }

        UpdateChannel channel = Channel;
        string? folder = Environment.GetEnvironmentVariable("SPARKIV_UPDATE_SOURCE");
        IUpdateSource source = string.IsNullOrEmpty(folder)
          ? new ChannelGithubSource(Repository, channel)
          : new SimpleFileSource(new System.IO.DirectoryInfo(folder));
        var manager = new UpdateManager(new ChannelFilter(source, channel));
        _pending = await manager.CheckForUpdatesAsync();
        _manager = manager;
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

    /// <summary>
    /// The GitHub releases of a channel. GitHub only tells pre-releases from releases, so alpha and beta are told
    /// apart by the version in the package names ("reditec.SparkIV-0.9.0-alpha.1-full.nupkg").
    /// </summary>
    private sealed class ChannelGithubSource : GithubSource
    {
      private readonly UpdateChannel _channel;

      public ChannelGithubSource(string repository, UpdateChannel channel)
        : base(repository, null, channel != UpdateChannel.Release)
      {
        _channel = channel;
      }

      protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
      {
        var releases = await base.GetReleases(includePrereleases);
        return releases.Where(release => VersionOf(release) is string version && Offers(_channel, version)).ToArray();
      }

      private static string? VersionOf(GithubRelease release)
      {
        const string Suffix = "-full.nupkg";
        string? name = release.Assets?.Select(asset => asset.Name).FirstOrDefault(n => n != null && n.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
          return null;
        }
        // "<id>-<version>-full.nupkg"; the id has no dash, the version may have one.
        int start = name.IndexOf('-') + 1;
        return name.Substring(start, name.Length - Suffix.Length - start);
      }
    }

    /// <summary>Leaves the versions of other channels out of a feed (also for the local test folder).</summary>
    private sealed class ChannelFilter : IUpdateSource
    {
      private readonly IUpdateSource _source;
      private readonly UpdateChannel _channel;

      public ChannelFilter(IUpdateSource source, UpdateChannel channel)
      {
        _source = source;
        _channel = channel;
      }

      public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
      {
        var feed = await _source.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        return new VelopackAssetFeed { Assets = feed.Assets.Where(asset => Offers(_channel, asset.Version.ToString())).ToArray() };
      }

      public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
        _source.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
  }
}
