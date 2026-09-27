/**********************************************************************\

 RageLib - Audio
 Copyright (C) 2009  Arushan/Aru <oneforaru at gmail.com>

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
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RageLib.Audio.WaveFile;

namespace RageLib.Audio
{
  public class AudioViewController
  {
    private AudioView _view;
    private AudioFile _file;
    private AudioPlayer _player;
    private Timer _timer;
    private string _lastSaveDirectory;
    private bool _playOnFirstSelection;

    private byte[] _originalData;
    private string _originalFileName;

    public AudioViewController(AudioView view)
    {
      _view = view;
      _view.PlayPauseClicked += View_PlayPauseClicked;
      _view.StopClicked += View_StopClicked;
      _view.SeekRequested += View_SeekRequested;
      _view.PlayLoopedChanged += View_PlayLoopedChanged;
      _view.VolumeChanged += View_VolumeChanged;
      _view.DownmixChanged += View_DownmixChanged;
      _view.ExportOriginalClicked += View_ExportOriginalClicked;
      _view.ExportWAVClicked += View_ExportWAVClicked;
      _view.ExportMultichannelWAVClicked += View_ExportMultichannelWAVClicked;
      _view.SelectedTrackChanged += View_SelectedTrackChanged;
      _view.TrackActivated += View_TrackActivated;
      _view.Load += View_Load;
      _view.Disposed += View_Disposed;

      _player = new AudioPlayer();
      _player.Looped = _view.PlayLooped;
      _player.Volume = _view.Volume;
      _player.Downmix = _view.Downmix;

      _view.SupportsOriginalExport = false;

      _timer = new Timer { Interval = 100 };
      _timer.Tick += Timer_Tick;
      _timer.Start();
    }

    public AudioFile AudioFile
    {
      get { return _file; }
      set
      {
        _file = value;
        UpdateView();
      }
    }

    /// <summary>
    /// The unmodified file as stored in the archive. Exporting it is the default, because
    /// only these bytes can be edited externally and imported back into the game.
    /// </summary>
    public void SetOriginalFile(byte[] data, string fileName)
    {
      _originalData = data;
      _originalFileName = fileName;
      _view.SupportsOriginalExport = data != null;
    }

    private void UpdateView()
    {
      _view.SupportsMultichannelExport = _file.SupportsMultichannelExport;

      var groupTracks = AudioTrack.FindChannelGroups(_file);
      var monoTracks = _file.Select(AudioTrack.FromMono).ToList();

      _view.SetTracks(groupTracks, monoTracks);

      AudioTrack first = groupTracks.FirstOrDefault() ?? monoTracks.FirstOrDefault();
      _view.SelectedTrack = first;
    }

    #region Playback

    private void View_PlayPauseClicked(object sender, EventArgs e)
    {
      if (_player.IsPlaying)
      {
        _player.Pause();
        UpdatePlaybackView();
        return;
      }

      if (!_player.IsPaused)
      {
        // Stopped: start the selected track (paused playback just resumes).
        AudioTrack selected = _view.SelectedTrack;
        if (selected != null && selected != _player.Track)
        {
          _player.Initialize(_file, selected);
        }
      }

      StartPlayback();
    }

    private void View_StopClicked(object sender, EventArgs e)
    {
      _player.Stop();
      UpdatePlaybackView();
    }

    private void View_SeekRequested(object sender, EventArgs e)
    {
      try
      {
        _player.Seek(_view.SeekPosition);
      }
      catch
      {
        ShowPlayError();
      }

      UpdatePlaybackView();
    }

    private void View_PlayLoopedChanged(object sender, EventArgs e)
    {
      _player.Looped = _view.PlayLooped;
    }

    private void View_DownmixChanged(object sender, EventArgs e)
    {
      try
      {
        _player.Downmix = _view.Downmix;
      }
      catch
      {
        ShowPlayError();
      }
      UpdatePlaybackView();
    }

    private void View_VolumeChanged(object sender, EventArgs e)
    {
      _player.Volume = _view.Volume;
      _player.Downmix = _view.Downmix;
    }

    // Opening a file starts playing the first track as soon as the preview is shown.
    private void View_Load(object sender, EventArgs e)
    {
      if (_view.SelectedTrack != null)
      {
        PlayTrack(_view.SelectedTrack);
      }
      else
      {
        // The list reports its selection once its handle exists; start then.
        _playOnFirstSelection = true;
      }
    }

    // Double-click / Enter: play that track from the start.
    private void View_TrackActivated(object sender, EventArgs e)
    {
      if (_view.SelectedTrack != null)
      {
        PlayTrack(_view.SelectedTrack);
      }
    }

    // A single click only selects. Playback continues; when nothing is playing the transport
    // bar already shows the selected track so Play starts it.
    private void View_SelectedTrackChanged(object sender, EventArgs e)
    {
      AudioTrack track = _view.SelectedTrack;
      if (track == null || track == _player.Track)
      {
        return;
      }

      if (_playOnFirstSelection)
      {
        _playOnFirstSelection = false;
        PlayTrack(track);
        return;
      }

      if (!_player.IsPlaying && !_player.IsPaused)
      {
        _player.Initialize(_file, track);
        UpdatePlaybackView();
      }
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
      catch
      {
        ShowPlayError();
      }
      UpdatePlaybackView();
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
      if (_player.HasEnded)
      {
        _player.Stop();
      }

      UpdatePlaybackView();
    }

    private void UpdatePlaybackView()
    {
      _view.UpdatePlayback(_player.Position, _player.Length, _player.IsPlaying);
      _view.DownmixAvailable = _player.Track != null && _player.Track.Waves.Length > 2;
    }

    private static void ShowPlayError()
    {
      MessageBox.Show("Audio play error.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    #endregion

    #region Export

    private void View_ExportOriginalClicked(object sender, EventArgs e)
    {
      if (_originalData == null)
      {
        return;
      }

      string extension = Path.GetExtension(_originalFileName);
      var sfd = new SaveFileDialog
                  {
                    AddExtension = true,
                    OverwritePrompt = true,
                    Title = "Export Original",
                    Filter = string.Format("Original file (*{0})|*{0}|All files (*.*)|*.*", extension),
                    InitialDirectory = _lastSaveDirectory,
                    FileName = _originalFileName
                  };

      if (sfd.ShowDialog() == DialogResult.OK)
      {
        File.WriteAllBytes(sfd.FileName, _originalData);
        _lastSaveDirectory = new FileInfo(sfd.FileName).Directory.FullName;

        MessageBox.Show("Original file exported.\n\nTo put an edited version back, use Import in the main window. " +
                        "The file name must stay \"" + _originalFileName + "\".",
                        "Export Original", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
    }

    private void View_ExportWAVClicked(object sender, EventArgs e)
    {
      AudioTrack track = _view.SelectedTrack;
      if (track == null)
      {
        return;
      }

      var sfd = new SaveFileDialog
                  {
                    AddExtension = true,
                    OverwritePrompt = true,
                    Title = track.IsMultichannel ? "Convert Selected to " + track.LayoutName + " WAV (" + track.Waves.Length + " channels)" : "Convert Selected to WAV",
                    Filter = "WAV Audio File (*.wav)|*.wav",
                    InitialDirectory = _lastSaveDirectory,
                    FileName = track.Name + ".wav"
                  };

      if (sfd.ShowDialog() == DialogResult.OK)
      {
        // Playback and export share the sound bank stream.
        _player.Stop();

        using (var f = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write))
        {
          if (track.IsMultichannel)
          {
            WaveExport.ExportChannels(_file, track, f);
          }
          else
          {
            WaveExport.Export(_file, track.Waves[0], f);
          }
        }

        _lastSaveDirectory = new FileInfo(sfd.FileName).Directory.FullName;
        UpdatePlaybackView();

        MessageBox.Show("Audio exported.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
    }

    private void View_ExportMultichannelWAVClicked(object sender, EventArgs e)
    {
      var sfd = new SaveFileDialog
      {
        AddExtension = true,
        OverwritePrompt = true,
        Title = "Convert All Channels to WAV",
        Filter = "WAV Audio File (*.wav)|*.wav",
        InitialDirectory = _lastSaveDirectory,
        FileName = _file.Name + ".wav",
      };

      if (sfd.ShowDialog() == DialogResult.OK)
      {
        _player.Stop();

        using (var f = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write))
        {
          WaveExport.ExportMultichannel(_file, f);
        }

        _lastSaveDirectory = new FileInfo(sfd.FileName).Directory.FullName;
        UpdatePlaybackView();

        MessageBox.Show("Audio exported.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
    }

    #endregion

    private void View_Disposed(object sender, EventArgs e)
    {
      _timer.Stop();
      _timer.Dispose();

      _player.Stop();

      if (_file != null)
      {
        _file.Dispose();
        _file = null;
      }
    }
  }
}
