using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using RageLib.Audio;
using RageLib.Audio.WaveFile;
using SparkIV.WinUI.Model;
using VirtualKey = Windows.System.VirtualKey;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// An audio bank (.ivaud): its tracks, playback and export. Waves named NAME_LEFT / NAME_RIGHT
  /// (/ _CENTRE / _LS / _RS) also play as one stereo, LCR or 5.0 track.
  /// </summary>
  public sealed partial class AudioView : UserControl, IDisposable
  {
    // Remembered while SparkIV runs, like in the old audio viewer.
    private static bool s_looped;
    private static bool s_downmix = true;
    private static double s_volume = 100;

    private readonly ViewerContext _context;
    private readonly AudioFile _file;
    private readonly AudioPlayer _player = new AudioPlayer();
    private readonly DispatcherQueueTimer _timer;
    private readonly List<AudioTrackGroup> _groups = new List<AudioTrackGroup>();

    private bool _started;
    private bool _isPlaying;
    private bool _seeking;
    private bool _updatingSlider;
    private string _sortKey = "";
    private bool _ascending = true;

    public AudioView(ViewerContext context, AudioFile file)
    {
      InitializeComponent();
      _context = context;
      _file = file;

      LoopButton.IsChecked = s_looped;
      DownmixButton.IsChecked = s_downmix;
      VolumeSlider.Value = s_volume;
      _player.Looped = s_looped;
      _player.Downmix = s_downmix;
      _player.Volume = (float)(s_volume / 100);

      ExportAllButton.IsEnabled = file.SupportsMultichannelExport;

      // The slider seeks when the mouse lets go of it, not on every step of a drag.
      PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((s, e) => _seeking = true), true);
      PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((s, e) => EndSeek()), true);
      PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((s, e) => EndSeek()), true);

      _timer = DispatcherQueue.CreateTimer();
      _timer.Interval = TimeSpan.FromMilliseconds(100);
      _timer.Tick += (s, e) =>
      {
        if (_player.HasEnded)
        {
          _player.Stop();
        }
        UpdatePlayback();
      };

      BuildTracks();
      Loaded += AudioView_Loaded;
    }

    // Opening a bank plays its first track right away.
    private void AudioView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;
      _timer.Start();

      var first = _groups.SelectMany(g => g).FirstOrDefault();
      if (first != null)
      {
        TrackList.SelectedItem = first;
        PlayTrack(first.Track);
      }
      TrackList.Focus(FocusState.Programmatic);
    }

    #region Track list

    private void BuildTracks()
    {
      var groupTracks = AudioTrack.FindChannelGroups(_file);
      var monoTracks = _file.Select(AudioTrack.FromMono).ToList();
      var monoOfWave = monoTracks.ToDictionary(t => t.Waves[0]);
      var grouped = new HashSet<AudioTrack>();

      _groups.Clear();
      foreach (var track in groupTracks)
      {
        int count = track.Waves.Length;
        var group = new AudioTrackGroup(track.Name + "  \u2013  " + track.LayoutName + ", stored as " + count + " mono tracks", isMultichannel: true);
        group.Add(new AudioTrackRow(track, track.LayoutName + " (" + count + " \u00D7 Mono)", AudioRowKind.Multichannel));
        // Members in speaker order: L, R, C, LS, RS
        for (int i = 0; i < count; i++)
        {
          if (monoOfWave.TryGetValue(track.Waves[i], out var mono))
          {
            group.Add(new AudioTrackRow(mono, "Mono (" + AudioTrack.GetChannelName(track.Channels[i]) + ")", AudioRowKind.Member));
            grouped.Add(mono);
          }
        }
        _groups.Add(group);
      }

      var rest = new AudioTrackGroup(groupTracks.Count > 0 ? "Mono tracks" : "Tracks", isMultichannel: false);
      foreach (var track in monoTracks.Where(t => !grouped.Contains(t)))
      {
        rest.Add(new AudioTrackRow(track, "Mono", AudioRowKind.Mono));
      }
      if (rest.Count > 0)
      {
        _groups.Add(rest);
      }

      ShowGroups();
    }

    private void ShowGroups()
    {
      TrackSource.Source = _groups;
      TrackList.ItemsSource = TrackSource.View;
    }

    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
      string key = (string)((Button)sender).Tag;
      _ascending = key != _sortKey || !_ascending;
      _sortKey = key;

      Func<AudioTrackRow, IComparable> by = key switch
      {
        "Channels" => r => r.Channels,
        "Length" => r => r.Track.Length,
        "Rate" => r => r.Track.SamplesPerSecond,
        _ => r => r.Name,
      };

      var selected = TrackList.SelectedItem;

      // Multichannel groups keep their members in speaker order and are sorted by their track;
      // the mono tracks are sorted among themselves.
      var multichannel = _groups.Where(g => g.IsMultichannel);
      multichannel = _ascending ? multichannel.OrderBy(g => by(g[0])) : multichannel.OrderByDescending(g => by(g[0]));
      var sorted = multichannel.ToList();
      foreach (var group in _groups.Where(g => !g.IsMultichannel))
      {
        var rows = _ascending ? group.OrderBy(by).ToList() : group.OrderByDescending(by).ToList();
        group.Clear();
        group.AddRange(rows);
        sorted.Add(group);
      }
      _groups.Clear();
      _groups.AddRange(sorted);

      ShowGroups();
      TrackList.SelectedItem = selected;
    }

    private AudioTrack? SelectedTrack => (TrackList.SelectedItem as AudioTrackRow)?.Track;

    // A single click only selects. Playback continues; when nothing is playing, Play starts the selected track.
    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var track = SelectedTrack;
      if (track != null && track != _player.Track && !_player.IsPlaying && !_player.IsPaused)
      {
        _player.Initialize(_file, track);
        UpdatePlayback();
      }
    }

    // Double-click / Enter: play that track from the start.
    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
      if (SelectedTrack is AudioTrack track)
      {
        PlayTrack(track);
      }
    }

    private void TrackList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
      if (e.Key == VirtualKey.Enter && SelectedTrack is AudioTrack track)
      {
        e.Handled = true;
        PlayTrack(track);
      }
    }

    #endregion

    #region Playback

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
      // Space plays and pauses, except on a button, which Space presses.
      if (e.Key == VirtualKey.Space && !(FocusManager.GetFocusedElement(XamlRoot) is ButtonBase))
      {
        e.Handled = true;
        TogglePlay();
      }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
      if (_player.IsPlaying)
      {
        _player.Pause();
        UpdatePlayback();
        return;
      }

      // Stopped: start the selected track (paused playback just resumes).
      if (!_player.IsPaused && SelectedTrack is AudioTrack selected && selected != _player.Track)
      {
        _player.Initialize(_file, selected);
      }
      StartPlayback();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
      _player.Stop();
      UpdatePlayback();
    }

    private void PlayTrack(AudioTrack track)
    {
      _player.Initialize(_file, track);
      StartPlayback();
    }

    private void StartPlayback()
    {
      try
      {
        _player.Play();
      }
      catch (Exception ex)
      {
        ShowPlayError(ex);
      }
      UpdatePlayback();
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
      if (_updatingSlider)
      {
        return;
      }
      PositionText.Text = FormatTime(TimeSpan.FromMilliseconds(e.NewValue));
      // Keyboard changes seek right away, mouse drags when the button is released.
      if (!_seeking)
      {
        Seek();
      }
    }

    private void EndSeek()
    {
      if (_seeking)
      {
        _seeking = false;
        Seek();
      }
    }

    private void Seek()
    {
      try
      {
        _player.Seek(TimeSpan.FromMilliseconds(PositionSlider.Value));
      }
      catch (Exception ex)
      {
        ShowPlayError(ex);
      }
      UpdatePlayback();
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
      s_looped = LoopButton.IsChecked == true;
      _player.Looped = s_looped;
    }

    private void Downmix_Click(object sender, RoutedEventArgs e)
    {
      s_downmix = DownmixButton.IsChecked == true;
      try
      {
        _player.Downmix = s_downmix;
      }
      catch (Exception ex)
      {
        ShowPlayError(ex);
      }
      UpdatePlayback();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
      s_volume = e.NewValue;
      _player.Volume = (float)(e.NewValue / 100);
      ToolTipService.SetToolTip(VolumeSlider, "Volume: " + (int)e.NewValue + " %");
    }

    private void UpdatePlayback()
    {
      var track = _player.Track;
      TimeSpan length = _player.Length;
      TimeSpan position = _player.Position;

      _updatingSlider = true;
      double max = Math.Max(1, length.TotalMilliseconds);
      if (PositionSlider.Maximum != max)
      {
        PositionSlider.Maximum = max;
        LengthText.Text = FormatTime(length);
      }
      if (!_seeking)
      {
        PositionSlider.Value = Math.Min(max, Math.Max(0, position.TotalMilliseconds));
        PositionText.Text = FormatTime(position);
      }
      _updatingSlider = false;

      bool playing = _player.IsPlaying;
      if (playing != _isPlaying)
      {
        _isPlaying = playing;
        PlayIcon.Glyph = playing ? "\uE769" : "\uE768";
        ToolTipService.SetToolTip(PlayButton, playing ? "Pause (Space)" : "Play (Space)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
      }

      DownmixButton.IsEnabled = track != null && track.Waves.Length > 2;
      NowPlaying.Text = track == null ? "" : track.Name + "   \u00B7   " + track.LayoutName + ", " + track.SamplesPerSecond + " Hz"
                                             + (playing ? "" : _player.IsPaused ? "   \u00B7   paused" : "");
    }

    private void ShowPlayError(Exception ex)
    {
      _context.ShowMessage("Could not play the audio", ex.Message, true);
    }

    private static string FormatTime(TimeSpan time)
    {
      return time.TotalHours >= 1
               ? string.Format("{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds)
               : string.Format("{0}:{1:00}", (int)time.TotalMinutes, time.Seconds);
    }

    #endregion

    #region Export

    private async void ExportWave_Click(object sender, RoutedEventArgs e)
    {
      if (SelectedTrack is not AudioTrack track)
      {
        _context.ShowMessage("Nothing selected", "Select the track to export.", true);
        return;
      }

      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, track.Name + ".wav", ".wav");
      if (path == null)
      {
        return;
      }

      await ExportAsync(path, stream =>
      {
        if (track.IsMultichannel)
        {
          WaveExport.ExportChannels(_file, track, stream);
        }
        else
        {
          WaveExport.Export(_file, track.Waves[0], stream);
        }
      });
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, Path.GetFileNameWithoutExtension(_context.File.Name) + ".wav", ".wav");
      if (path != null)
      {
        await ExportAsync(path, stream => WaveExport.ExportMultichannel(_file, stream));
      }
    }

    private async Task ExportAsync(string path, Action<Stream> write)
    {
      // Playback and export read the same sound bank stream.
      _player.Stop();
      UpdatePlayback();
      try
      {
        await Task.Run(() =>
        {
          using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
          write(stream);
        });
        _context.ShowMessage("Exported", path, false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the audio", ex.Message, true);
      }
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _context.File.Name;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path + ". To put an edited version back, use Import in the main window; the file name must stay \"" + name + "\".", false);
      }
    }

    #endregion

    public void Dispose()
    {
      _timer.Stop();
      _player.Stop();
      _file.Dispose();
    }
  }

  /// <summary>Audio banks (.ivaud).</summary>
  internal sealed class AudioViewer : IViewer
  {
    public object Load(RageLib.FileSystem.Common.File file, byte[] data)
    {
      var audio = new AudioFile();
      audio.Open(new MemoryStream(data));
      return audio;
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new AudioView(context, (AudioFile)loaded);
  }
}
