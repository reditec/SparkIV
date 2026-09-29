/**********************************************************************\

 RageLib - Audio
 Copyright (C) 2009  Arushan/Aru <oneforaru at gmail.com>
 Copyright (C) 2009  DerPlaya78

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
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RageLib.Audio
{
  public partial class AudioView : UserControl
  {
    private static bool _PlayLooped;
    private static bool _Downmix = true;
    private static int _Volume = 100;
    private int _sortColumn = -1;

    private readonly Glyphs _glyphs;
    private bool _isPlaying;
    private bool _seeking;

    public AudioView()
    {
      InitializeComponent();

      _glyphs = Glyphs.Create();
      btnPlayPause.Font = _glyphs.Font;
      btnStop.Font = _glyphs.Font;
      btnStop.Text = _glyphs.Stop;
      SetPlayButton(false);

      chkPlayLooped.Checked = _PlayLooped;
      chkDownmix.Checked = _Downmix;
      chkDownmix.Enabled = false;
      trackVolume.Value = _Volume;
      UpdateVolumeToolTip();

      chkPlayLooped.CheckedChanged += delegate { _PlayLooped = chkPlayLooped.Checked; };
      chkDownmix.CheckedChanged += delegate { _Downmix = chkDownmix.Checked; };
      trackVolume.ValueChanged += delegate
                                    {
                                      _Volume = trackVolume.Value;
                                      UpdateVolumeToolTip();
                                    };
    }

    #region Events

    public event EventHandler PlayPauseClicked
    {
      add { btnPlayPause.Click += value; }
      remove { btnPlayPause.Click -= value; }
    }

    public event EventHandler StopClicked
    {
      add { btnStop.Click += value; }
      remove { btnStop.Click -= value; }
    }

    public event EventHandler ExportOriginalClicked
    {
      add { tsbExportOriginal.Click += value; }
      remove { tsbExportOriginal.Click -= value; }
    }

    public event EventHandler ExportWAVClicked
    {
      add { tsbExportWave.Click += value; }
      remove { tsbExportWave.Click -= value; }
    }

    public event EventHandler ExportMultichannelWAVClicked
    {
      add { tsbExportMultiChannel.Click += value; }
      remove { tsbExportMultiChannel.Click -= value; }
    }

    public event EventHandler SelectedTrackChanged
    {
      add { listAudioBlocks.SelectedIndexChanged += value; }
      remove { listAudioBlocks.SelectedIndexChanged -= value; }
    }

    /// <summary>
    /// Raised when a track is double-clicked or confirmed with Enter.
    /// </summary>
    public event EventHandler TrackActivated
    {
      add { listAudioBlocks.ItemActivate += value; }
      remove { listAudioBlocks.ItemActivate -= value; }
    }

    public event EventHandler PlayLoopedChanged
    {
      add { chkPlayLooped.CheckedChanged += value; }
      remove { chkPlayLooped.CheckedChanged -= value; }
    }

    public event EventHandler DownmixChanged
    {
      add { chkDownmix.CheckedChanged += value; }
      remove { chkDownmix.CheckedChanged -= value; }
    }

    public event EventHandler VolumeChanged
    {
      add { trackVolume.ValueChanged += value; }
      remove { trackVolume.ValueChanged -= value; }
    }

    /// <summary>
    /// Raised when the user moved the position slider. The target is in SeekPosition.
    /// </summary>
    public event EventHandler SeekRequested;

    #endregion

    #region Properties

    public bool PlayLooped
    {
      get { return chkPlayLooped.Checked; }
      set { chkPlayLooped.Checked = value; }
    }

    public bool Downmix
    {
      get { return chkDownmix.Checked; }
    }

    /// <summary>
    /// Downmixing only applies to tracks with more than two channels.
    /// </summary>
    public bool DownmixAvailable
    {
      set { chkDownmix.Enabled = value; }
    }

    /// <summary>
    /// Volume from 0 to 1.
    /// </summary>
    public float Volume
    {
      get { return trackVolume.Value / 100f; }
    }

    public TimeSpan SeekPosition
    {
      get { return TimeSpan.FromMilliseconds(trackPosition.Value); }
    }

    public bool SupportsMultichannelExport
    {
      set
      {
        tsbExportMultiChannel.Enabled = value;
      }
    }

    public bool SupportsOriginalExport
    {
      set
      {
        tsbExportOriginal.Enabled = value;
      }
    }

    public AudioTrack SelectedTrack
    {
      get
      {
        if (listAudioBlocks.SelectedItems.Count == 1)
        {
          return listAudioBlocks.SelectedItems[0].Tag as AudioTrack;
        }
        return null;
      }

      set
      {
        listAudioBlocks.SelectedItems.Clear();
        if (value != null)
        {
          foreach (ListViewItem item in listAudioBlocks.Items)
          {
            if (item.Tag == value)
            {
              item.Selected = true;
              item.Focused = true;
              item.EnsureVisible();
              break;
            }
          }
        }
      }
    }

    #endregion

    #region Track list

    public void ClearTracks()
    {
      listAudioBlocks.SelectedItems.Clear();
      listAudioBlocks.Items.Clear();
      listAudioBlocks.Groups.Clear();
    }

    /// <summary>
    /// Shows the tracks. Every multichannel track (stereo, LCR, 5.0) gets its own group together
    /// with the mono tracks it is made of, so it stays visible how the data is actually stored.
    /// </summary>
    public void SetTracks(IList<AudioTrack> groupTracks, IList<AudioTrack> monoTracks)
    {
      listAudioBlocks.BeginUpdate();
      ClearTracks();

      var monoOfWave = new Dictionary<AudioWave, AudioTrack>();
      foreach (var track in monoTracks)
      {
        monoOfWave[track.Waves[0]] = track;
      }

      var grouped = new HashSet<AudioTrack>();
      foreach (var track in groupTracks)
      {
        int count = track.Waves.Length;
        var group = new ListViewGroup(string.Format("{0}  –  {1}, stored as {2} mono tracks",
                                                    track.Name, track.LayoutName, count));
        listAudioBlocks.Groups.Add(group);

        AddItem(track, string.Format("{0} ({1} × Mono)", track.LayoutName, count), group, 0);

        // Members in speaker order: L, R, C, LS, RS
        for (int i = 0; i < count; i++)
        {
          AudioTrack mono;
          if (monoOfWave.TryGetValue(track.Waves[i], out mono))
          {
            AddItem(mono, "Mono (" + AudioTrack.GetChannelName(track.Channels[i]) + ")", group, 1);
            grouped.Add(mono);
          }
        }
      }

      ListViewGroup ungrouped = null;
      if (groupTracks.Count > 0)
      {
        ungrouped = new ListViewGroup("Mono tracks");
        listAudioBlocks.Groups.Add(ungrouped);
      }

      foreach (var track in monoTracks)
      {
        if (!grouped.Contains(track))
        {
          AddItem(track, "Mono", ungrouped, 0);
        }
      }

      listAudioBlocks.ShowGroups = groupTracks.Count > 0;
      listAudioBlocks.EndUpdate();
      FitNameColumn();
    }

    private void AddItem(AudioTrack track, string channels, ListViewGroup group, int indent)
    {
      var lvi = new ListViewItem(track.Name);
      lvi.Tag = track;
      lvi.Group = group;
      lvi.IndentCount = indent;
      lvi.ToolTipText = "Double-click to play";

      if (track.IsMultichannel)
      {
        lvi.Font = new Font(listAudioBlocks.Font, FontStyle.Bold);
      }
      else if (indent > 0)
      {
        lvi.ForeColor = SystemColors.GrayText;
      }

      var lvisub = new ListViewItem.ListViewSubItem();
      lvisub.Text = channels;
      lvi.SubItems.Add(lvisub);

      TimeSpan playTime = track.Length;
      lvisub = new ListViewItem.ListViewSubItem();
      lvisub.Tag = playTime;
      lvisub.Text = FormatTime(playTime);
      lvi.SubItems.Add(lvisub);

      lvisub = new ListViewItem.ListViewSubItem();
      lvisub.Tag = track.SamplesPerSecond;
      lvisub.Text = track.SamplesPerSecond + " Hz";
      lvi.SubItems.Add(lvisub);

      listAudioBlocks.Items.Add(lvi);
    }

    private void listAudioBlocks_ColumnClick(object sender, ColumnClickEventArgs e)
    {
      if (e.Column != _sortColumn)
      {
        _sortColumn = e.Column;
        listAudioBlocks.Sorting = SortOrder.Ascending;
      }
      else
      {
        listAudioBlocks.Sorting = listAudioBlocks.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
      }

      listAudioBlocks.ListViewItemSorter = new ListViewItemComparer(e.Column, listAudioBlocks.Sorting == SortOrder.Descending);

      listAudioBlocks.Sort();
    }

    private void listAudioBlocks_ClientSizeChanged(object sender, EventArgs e)
    {
      FitNameColumn();
    }

    private void FitNameColumn()
    {
      int others = listAudioBlocks.Columns.Cast<ColumnHeader>().Where(c => c != lvcName).Sum(c => c.Width);
      lvcName.Width = Math.Max(150, listAudioBlocks.ClientSize.Width - others);
    }

    #endregion

    #region Transport

    /// <summary>
    /// Updates the transport controls. Called periodically by the controller.
    /// </summary>
    public void UpdatePlayback(TimeSpan position, TimeSpan length, bool isPlaying)
    {
      int max = Math.Max(1, (int)length.TotalMilliseconds);
      if (trackPosition.Maximum != max)
      {
        trackPosition.Maximum = max;
        lblLength.Text = FormatTime(length);
      }

      if (!_seeking)
      {
        trackPosition.Value = Math.Min(max, Math.Max(0, (int)position.TotalMilliseconds));
        lblPosition.Text = FormatTime(position);
      }

      if (isPlaying != _isPlaying)
      {
        SetPlayButton(isPlaying);
      }
    }

    private void SetPlayButton(bool isPlaying)
    {
      _isPlaying = isPlaying;
      btnPlayPause.Text = isPlaying ? _glyphs.Pause : _glyphs.Play;
      btnPlayPause.AccessibleName = isPlaying ? "Pause" : "Play";
      toolTip.SetToolTip(btnPlayPause, isPlaying ? "Pause (Space)" : "Play (Space)");
    }

    private void UpdateVolumeToolTip()
    {
      toolTip.SetToolTip(trackVolume, "Volume: " + trackVolume.Value + " %");
    }

    private void trackPosition_MouseDown(object sender, MouseEventArgs e)
    {
      // SeekTrackBar has already moved the thumb to the clicked spot, the seek happens on MouseUp.
      _seeking = true;
      lblPosition.Text = FormatTime(SeekPosition);
    }

    private void trackPosition_MouseUp(object sender, MouseEventArgs e)
    {
      if (_seeking)
      {
        _seeking = false;
        OnSeekRequested();
      }
    }

    private void trackPosition_Scroll(object sender, EventArgs e)
    {
      lblPosition.Text = FormatTime(SeekPosition);

      // Mouse drags are committed on MouseUp, keyboard changes right away.
      if (!_seeking)
      {
        OnSeekRequested();
      }
    }

    private void OnSeekRequested()
    {
      if (SeekRequested != null)
      {
        SeekRequested(this, EventArgs.Empty);
      }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
      if (keyData == Keys.Space && !(ActiveControl is ButtonBase))
      {
        btnPlayPause.PerformClick();
        return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    private static string FormatTime(TimeSpan time)
    {
      if (time.TotalHours >= 1)
      {
        return string.Format("{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds);
      }
      return string.Format("{0}:{1:00}", (int)time.TotalMinutes, time.Seconds);
    }

    /// <summary>
    /// Button symbols from the Windows icon font, with a plain Unicode fallback on systems without it.
    /// </summary>
    private class Glyphs
    {
      public Font Font;
      public string Play;
      public string Pause;
      public string Stop;

      public static Glyphs Create()
      {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
          if (FontFamily.Families.Any(f => f.Name == name))
          {
            return new Glyphs { Font = new Font(name, 10f), Play = "", Pause = "", Stop = "" };
          }
        }
        return new Glyphs { Font = new Font("Segoe UI Symbol", 10f), Play = "▶", Pause = "❙❙", Stop = "■" };
      }
    }

    #endregion
  }
}
