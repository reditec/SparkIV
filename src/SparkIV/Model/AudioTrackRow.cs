using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using RageLib.Audio;

namespace SparkIV.WinUI.Model
{  /// <summary>What a row in the track list stands for.</summary>
  internal enum AudioRowKind
  {
    Mono,
    Multichannel,
    Member,
  }

  /// <summary>One row in the track list.</summary>
  public sealed class AudioTrackRow
  {
    internal AudioTrackRow(AudioTrack track, string channels, AudioRowKind kind)

    {
      Track = track;
      Channels = channels;
      RowKind = kind;
    }

    public AudioTrack Track { get; }
    internal AudioRowKind RowKind { get; }

    public string Name => Track.Name;
    public string Channels { get; }
    public string DurationText => FormatLength(Track.Length);
    public string RateText => Track.SamplesPerSecond.ToString("N0") + " Hz";

    public string Glyph => RowKind == AudioRowKind.Multichannel ? "\uE7F5" : "\uE8D6";
    public Windows.UI.Text.FontWeight NameWeight => RowKind == AudioRowKind.Multichannel ? FontWeights.SemiBold : FontWeights.Normal;

    // The mono waves a multichannel track is stored as are indented and dimmed.
    public Thickness NamePadding => new Thickness(RowKind == AudioRowKind.Member ? 32 : 8, 0, 8, 0);
    public double RowOpacity => RowKind == AudioRowKind.Member ? 0.7 : 1.0;

    private static string FormatLength(TimeSpan time) =>
      time.TotalHours >= 1
        ? string.Format("{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds)
        : string.Format("{0}:{1:00}.{2}", (int)time.TotalMinutes, time.Seconds, time.Milliseconds / 100);
  }

  /// <summary>A group in the track list: a multichannel track with its mono waves, or the other mono tracks.</summary>
  public sealed class AudioTrackGroup : List<AudioTrackRow>
  {
    public AudioTrackGroup(string header, bool isMultichannel)
    {
      Title = header;
      IsMultichannel = isMultichannel;
    }

    public string Title { get; }
    public bool IsMultichannel { get; }
    public string Header => IsMultichannel ? Title : Title + " (" + Count + ")";
  }
}
