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

namespace RageLib.Audio
{
    partial class AudioView
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AudioView));
            this.listAudioBlocks = new System.Windows.Forms.ListView();
            this.lvcName = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.lvcChannels = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.lvcPlayTime = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.lvcSampleRate = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.imlIndent = new System.Windows.Forms.ImageList(this.components);
            this.tlpTransport = new System.Windows.Forms.TableLayoutPanel();
            this.btnPlayPause = new System.Windows.Forms.Button();
            this.btnStop = new System.Windows.Forms.Button();
            this.lblPosition = new System.Windows.Forms.Label();
            this.trackPosition = new RageLib.Audio.SeekTrackBar();
            this.lblLength = new System.Windows.Forms.Label();
            this.chkPlayLooped = new System.Windows.Forms.CheckBox();
            this.chkDownmix = new System.Windows.Forms.CheckBox();
            this.lblVolume = new System.Windows.Forms.Label();
            this.trackVolume = new RageLib.Audio.SeekTrackBar();
            this.toolTip = new System.Windows.Forms.ToolTip(this.components);
            this.tsContainer = new System.Windows.Forms.ToolStripContainer();
            this.tsToolbar = new System.Windows.Forms.ToolStrip();
            this.tsbExportOriginal = new System.Windows.Forms.ToolStripButton();
            this.tssExport = new System.Windows.Forms.ToolStripSeparator();
            this.tsbExportWave = new System.Windows.Forms.ToolStripButton();
            this.tsbExportMultiChannel = new System.Windows.Forms.ToolStripButton();
            this.tlpTransport.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackPosition)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackVolume)).BeginInit();
            this.tsContainer.ContentPanel.SuspendLayout();
            this.tsContainer.TopToolStripPanel.SuspendLayout();
            this.tsContainer.SuspendLayout();
            this.tsToolbar.SuspendLayout();
            this.SuspendLayout();
            //
            // listAudioBlocks
            //
            this.listAudioBlocks.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.lvcName,
            this.lvcChannels,
            this.lvcPlayTime,
            this.lvcSampleRate});
            this.listAudioBlocks.Dock = System.Windows.Forms.DockStyle.Fill;
            this.listAudioBlocks.FullRowSelect = true;
            this.listAudioBlocks.HideSelection = false;
            this.listAudioBlocks.Location = new System.Drawing.Point(0, 0);
            this.listAudioBlocks.MultiSelect = false;
            this.listAudioBlocks.Name = "listAudioBlocks";
            this.listAudioBlocks.Size = new System.Drawing.Size(619, 392);
            this.listAudioBlocks.SmallImageList = this.imlIndent;
            this.listAudioBlocks.TabIndex = 0;
            this.listAudioBlocks.UseCompatibleStateImageBehavior = false;
            this.listAudioBlocks.View = System.Windows.Forms.View.Details;
            this.listAudioBlocks.ColumnClick += new System.Windows.Forms.ColumnClickEventHandler(this.listAudioBlocks_ColumnClick);
            this.listAudioBlocks.ShowItemToolTips = true;
            this.listAudioBlocks.ClientSizeChanged += new System.EventHandler(this.listAudioBlocks_ClientSizeChanged);
            //
            // lvcName
            //
            this.lvcName.Text = "Name";
            this.lvcName.Width = 240;
            //
            // lvcChannels
            //
            this.lvcChannels.Text = "Channels";
            this.lvcChannels.Width = 150;
            //
            // lvcPlayTime
            //
            this.lvcPlayTime.Text = "Play Time";
            this.lvcPlayTime.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            this.lvcPlayTime.Width = 80;
            //
            // lvcSampleRate
            //
            this.lvcSampleRate.Text = "Sample Rate";
            this.lvcSampleRate.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            this.lvcSampleRate.Width = 90;
            //
            // imlIndent
            //
            this.imlIndent.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit;
            this.imlIndent.ImageSize = new System.Drawing.Size(16, 16);
            this.imlIndent.TransparentColor = System.Drawing.Color.Transparent;
            //
            // tlpTransport
            //
            this.tlpTransport.ColumnCount = 9;
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tlpTransport.Controls.Add(this.btnPlayPause, 0, 0);
            this.tlpTransport.Controls.Add(this.btnStop, 1, 0);
            this.tlpTransport.Controls.Add(this.lblPosition, 2, 0);
            this.tlpTransport.Controls.Add(this.trackPosition, 3, 0);
            this.tlpTransport.Controls.Add(this.lblLength, 4, 0);
            this.tlpTransport.Controls.Add(this.chkPlayLooped, 5, 0);
            this.tlpTransport.Controls.Add(this.chkDownmix, 6, 0);
            this.tlpTransport.Controls.Add(this.lblVolume, 7, 0);
            this.tlpTransport.Controls.Add(this.trackVolume, 8, 0);
            this.tlpTransport.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.tlpTransport.Location = new System.Drawing.Point(0, 392);
            this.tlpTransport.Name = "tlpTransport";
            this.tlpTransport.Padding = new System.Windows.Forms.Padding(4, 4, 4, 4);
            this.tlpTransport.RowCount = 1;
            this.tlpTransport.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tlpTransport.Size = new System.Drawing.Size(619, 44);
            this.tlpTransport.TabIndex = 1;
            //
            // btnPlayPause
            //
            this.btnPlayPause.AccessibleName = "Play";
            this.btnPlayPause.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.btnPlayPause.Location = new System.Drawing.Point(7, 7);
            this.btnPlayPause.Name = "btnPlayPause";
            this.btnPlayPause.Size = new System.Drawing.Size(36, 30);
            this.btnPlayPause.TabIndex = 0;
            this.btnPlayPause.Text = "Play";
            this.toolTip.SetToolTip(this.btnPlayPause, "Play (Space)");
            this.btnPlayPause.UseVisualStyleBackColor = true;
            //
            // btnStop
            //
            this.btnStop.AccessibleName = "Stop";
            this.btnStop.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.btnStop.Location = new System.Drawing.Point(49, 7);
            this.btnStop.Name = "btnStop";
            this.btnStop.Size = new System.Drawing.Size(36, 30);
            this.btnStop.TabIndex = 1;
            this.btnStop.Text = "Stop";
            this.toolTip.SetToolTip(this.btnStop, "Stop");
            this.btnStop.UseVisualStyleBackColor = true;
            //
            // lblPosition
            //
            this.lblPosition.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.lblPosition.AutoSize = true;
            this.lblPosition.Location = new System.Drawing.Point(91, 15);
            this.lblPosition.Margin = new System.Windows.Forms.Padding(6, 0, 0, 0);
            this.lblPosition.MinimumSize = new System.Drawing.Size(34, 0);
            this.lblPosition.Name = "lblPosition";
            this.lblPosition.Size = new System.Drawing.Size(34, 13);
            this.lblPosition.TabIndex = 2;
            this.lblPosition.Text = "0:00";
            this.lblPosition.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            //
            // trackPosition
            //
            this.trackPosition.AccessibleName = "Position";
            this.trackPosition.AutoSize = false;
            this.trackPosition.Dock = System.Windows.Forms.DockStyle.Fill;
            this.trackPosition.LargeChange = 10000;
            this.trackPosition.Location = new System.Drawing.Point(128, 7);
            this.trackPosition.Maximum = 1;
            this.trackPosition.Name = "trackPosition";
            this.trackPosition.Size = new System.Drawing.Size(202, 30);
            this.trackPosition.SmallChange = 1000;
            this.trackPosition.TabIndex = 3;
            this.trackPosition.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackPosition.Scroll += new System.EventHandler(this.trackPosition_Scroll);
            this.trackPosition.MouseDown += new System.Windows.Forms.MouseEventHandler(this.trackPosition_MouseDown);
            this.trackPosition.MouseUp += new System.Windows.Forms.MouseEventHandler(this.trackPosition_MouseUp);
            //
            // lblLength
            //
            this.lblLength.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.lblLength.AutoSize = true;
            this.lblLength.Location = new System.Drawing.Point(333, 15);
            this.lblLength.Margin = new System.Windows.Forms.Padding(0, 0, 6, 0);
            this.lblLength.MinimumSize = new System.Drawing.Size(34, 0);
            this.lblLength.Name = "lblLength";
            this.lblLength.Size = new System.Drawing.Size(34, 13);
            this.lblLength.TabIndex = 4;
            this.lblLength.Text = "0:00";
            //
            // chkPlayLooped
            //
            this.chkPlayLooped.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkPlayLooped.AutoSize = true;
            this.chkPlayLooped.Location = new System.Drawing.Point(376, 13);
            this.chkPlayLooped.Name = "chkPlayLooped";
            this.chkPlayLooped.Size = new System.Drawing.Size(50, 17);
            this.chkPlayLooped.TabIndex = 5;
            this.chkPlayLooped.Text = "Loop";
            this.chkPlayLooped.UseVisualStyleBackColor = true;
            //
            // chkDownmix
            //
            this.chkDownmix.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkDownmix.AutoSize = true;
            this.chkDownmix.Checked = true;
            this.chkDownmix.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkDownmix.Name = "chkDownmix";
            this.chkDownmix.TabIndex = 6;
            this.chkDownmix.Text = "Downmix to Stereo";
            this.toolTip.SetToolTip(this.chkDownmix, "LCR and 5.0 tracks: mix centre and surround channels into stereo (-3 dB).\r\nOff: send every channel to Windows with its speaker assignment.");
            this.chkDownmix.UseVisualStyleBackColor = true;
            //
            // lblVolume
            //
            this.lblVolume.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.lblVolume.AutoSize = true;
            this.lblVolume.Location = new System.Drawing.Point(509, 15);
            this.lblVolume.Margin = new System.Windows.Forms.Padding(6, 0, 0, 0);
            this.lblVolume.Name = "lblVolume";
            this.lblVolume.Size = new System.Drawing.Size(42, 13);
            this.lblVolume.TabIndex = 7;
            this.lblVolume.Text = "Volume";
            //
            // trackVolume
            //
            this.trackVolume.AccessibleName = "Volume";
            this.trackVolume.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.trackVolume.AutoSize = false;
            this.trackVolume.LargeChange = 10;
            this.trackVolume.Location = new System.Drawing.Point(554, 7);
            this.trackVolume.Maximum = 100;
            this.trackVolume.Name = "trackVolume";
            this.trackVolume.Size = new System.Drawing.Size(90, 30);
            this.trackVolume.SmallChange = 5;
            this.trackVolume.TabIndex = 8;
            this.trackVolume.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackVolume.Value = 100;
            //
            // tsContainer
            //
            //
            // tsContainer.ContentPanel
            //
            this.tsContainer.ContentPanel.Controls.Add(this.listAudioBlocks);
            this.tsContainer.ContentPanel.Controls.Add(this.tlpTransport);
            this.tsContainer.ContentPanel.Size = new System.Drawing.Size(619, 436);
            this.tsContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tsContainer.Location = new System.Drawing.Point(0, 0);
            this.tsContainer.Name = "tsContainer";
            this.tsContainer.Size = new System.Drawing.Size(619, 461);
            this.tsContainer.TabIndex = 2;
            this.tsContainer.Text = "toolStripContainer1";
            //
            // tsContainer.TopToolStripPanel
            //
            this.tsContainer.TopToolStripPanel.Controls.Add(this.tsToolbar);
            //
            // tsToolbar
            //
            this.tsToolbar.Dock = System.Windows.Forms.DockStyle.None;
            this.tsToolbar.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;
            this.tsToolbar.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.tsbExportOriginal,
            this.tssExport,
            this.tsbExportWave,
            this.tsbExportMultiChannel});
            this.tsToolbar.Location = new System.Drawing.Point(0, 0);
            this.tsToolbar.Name = "tsToolbar";
            this.tsToolbar.Size = new System.Drawing.Size(619, 25);
            this.tsToolbar.Stretch = true;
            this.tsToolbar.TabIndex = 0;
            //
            // tsbExportOriginal
            //
            this.tsbExportOriginal.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.tsbExportOriginal.Image = ((System.Drawing.Image)(resources.GetObject("tsbExportWave.Image")));
            this.tsbExportOriginal.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.tsbExportOriginal.Name = "tsbExportOriginal";
            this.tsbExportOriginal.Size = new System.Drawing.Size(150, 22);
            this.tsbExportOriginal.Text = "Export Original (.ivaud)";
            this.tsbExportOriginal.ToolTipText = "Export the file exactly as stored in the archive.\r\nEdit it externally and put it back with Import in the main window.";
            //
            // tssExport
            //
            this.tssExport.Name = "tssExport";
            this.tssExport.Size = new System.Drawing.Size(6, 25);
            //
            // tsbExportWave
            //
            this.tsbExportWave.Image = ((System.Drawing.Image)(resources.GetObject("tsbExportWave.Image")));
            this.tsbExportWave.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.tsbExportWave.Name = "tsbExportWave";
            this.tsbExportWave.Size = new System.Drawing.Size(150, 22);
            this.tsbExportWave.Text = "Convert Selected to WAV";
            this.tsbExportWave.ToolTipText = "Decode the selected track to a WAV file (conversion, not the original data)";
            //
            // tsbExportMultiChannel
            //
            this.tsbExportMultiChannel.Image = ((System.Drawing.Image)(resources.GetObject("tsbExportMultiChannel.Image")));
            this.tsbExportMultiChannel.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.tsbExportMultiChannel.Name = "tsbExportMultiChannel";
            this.tsbExportMultiChannel.Size = new System.Drawing.Size(170, 22);
            this.tsbExportMultiChannel.Text = "Convert All Channels to WAV";
            this.tsbExportMultiChannel.ToolTipText = "Decode all channels into one multichannel WAV file (conversion, not the original data)";
            //
            // AudioView
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.tsContainer);
            this.Name = "AudioView";
            this.Size = new System.Drawing.Size(619, 461);
            this.tlpTransport.ResumeLayout(false);
            this.tlpTransport.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackPosition)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackVolume)).EndInit();
            this.tsContainer.ContentPanel.ResumeLayout(false);
            this.tsContainer.TopToolStripPanel.ResumeLayout(false);
            this.tsContainer.TopToolStripPanel.PerformLayout();
            this.tsContainer.ResumeLayout(false);
            this.tsContainer.PerformLayout();
            this.tsToolbar.ResumeLayout(false);
            this.tsToolbar.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.ListView listAudioBlocks;
        private System.Windows.Forms.ColumnHeader lvcName;
        private System.Windows.Forms.ColumnHeader lvcChannels;
        private System.Windows.Forms.ColumnHeader lvcPlayTime;
        private System.Windows.Forms.ColumnHeader lvcSampleRate;
        private System.Windows.Forms.ImageList imlIndent;
        private System.Windows.Forms.TableLayoutPanel tlpTransport;
        private System.Windows.Forms.Button btnPlayPause;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Label lblPosition;
        private RageLib.Audio.SeekTrackBar trackPosition;
        private System.Windows.Forms.Label lblLength;
        private System.Windows.Forms.CheckBox chkPlayLooped;
        private System.Windows.Forms.CheckBox chkDownmix;
        private System.Windows.Forms.Label lblVolume;
        private RageLib.Audio.SeekTrackBar trackVolume;
        private System.Windows.Forms.ToolTip toolTip;
        private System.Windows.Forms.ToolStripContainer tsContainer;
        private System.Windows.Forms.ToolStrip tsToolbar;
        private System.Windows.Forms.ToolStripButton tsbExportOriginal;
        private System.Windows.Forms.ToolStripSeparator tssExport;
        private System.Windows.Forms.ToolStripButton tsbExportWave;
        private System.Windows.Forms.ToolStripButton tsbExportMultiChannel;
    }
}
