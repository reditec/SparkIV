/**********************************************************************\

 Spark IV
 Copyright (C) 2008  Arushan/Aru <oneforaru at gmail.com>

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
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using RageLib.FileSystem;
using RageLib.FileSystem.Common;
using SparkIV.Editor;
using SparkIV.Viewer;
using RageLib.Common;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using Ookii.Dialogs.WinForms;
using SparkIV.Explorer;

namespace SparkIV
{
  public partial class MainForm : ExtendedForm
  {
    private static readonly Color CustomDataForeColor = SystemColors.HotTrack;
    private const int SizeColumn = 2;

    private FileSystem _fs;
    private int _sortColumn = -1;

    private string _lastOpenPath;
    private string _lastImportExportPath;

    private Directory _selectedDir;

    private readonly ShellIcons _icons = new ShellIcons();

    public MainForm()
    {
      InitializeComponent();

      Version ver = Assembly.GetExecutingAssembly().GetName().Version;
      tslAbout.Text = "SparkIV " + ver.Major + "." + ver.Minor + "." + ver.Build + " (Beta)" + "\n" +
                      "(C)2008-2019, Ahmed";

      tvDir.ImageList = _icons.ImageList;
      lvFiles.SmallImageList = _icons.ImageList;
      lvFiles.ShowGroups = true;

      // ListView.DoubleBuffered is protected; without it the Explorer theme flickers on hover.
      typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(lvFiles, true, null);

      SetInitialUIState();

      lvFiles.AllowDrop = true;
      lvFiles.DragDrop += lvFiles_DragDrop;
      lvFiles.DragEnter += lvFiles_DragEnter;
    }

    protected override void OnLoad(EventArgs e)
    {
      base.OnLoad(e);

      // The same look as Windows Explorer: hover and selection highlight, chevrons instead of +/- in the tree.
      NativeMethods.SetWindowTheme(lvFiles.Handle, "Explorer", null);
      NativeMethods.SetWindowTheme(tvDir.Handle, "Explorer", null);
      NativeMethods.SendMessage(tvDir.Handle, NativeMethods.TVM_SETEXTENDEDSTYLE,
                                (IntPtr)(NativeMethods.TVS_EX_DOUBLEBUFFER | NativeMethods.TVS_EX_FADEINOUTEXPANDOS),
                                (IntPtr)(NativeMethods.TVS_EX_DOUBLEBUFFER | NativeMethods.TVS_EX_FADEINOUTEXPANDOS));
      NativeMethods.SendMessage(tstFilterBox.TextBox.Handle, NativeMethods.EM_SETCUEBANNER, (IntPtr)1, "Filter");
    }

    private void lvFiles_DragEnter(object sender, DragEventArgs e)
    {
      if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    private void lvFiles_DragDrop(object sender, DragEventArgs e)
    {
      string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
      foreach (string file in files)
      {
        //lvFiles.Items.Add(file);
        System.IO.File.Copy(file, Path.Combine(KeyUtil.GetDir.Get(), Path.GetFileName(file)));
      }
      lvFiles.Refresh();
    }

    #region Helpers

    public void OpenFile(string filename, FileSystem fs)
    {
      if (fs == null)
      {
        if (filename.EndsWith(".rpf"))
        {
          fs = new RPFFileSystem();
        }
        else if (filename.EndsWith(".img"))
        {
          fs = new IMGFileSystem();
        }
        else if (IODirectory.Exists(filename))
        {
          fs = new RealFileSystem();
          filename = (new DirectoryInfo(filename)).FullName;
        }
      }

      if (fs != null)
      {
        if (IOFile.Exists(filename))
        {
          FileInfo fi = new FileInfo(filename);
          if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
          {
            DialogResult result =
                MessageBox.Show("The file you are trying to open appears to be read-only. " +
                    "Would you like to make it writable before opening this file?",
                    "Open", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
              fi.Attributes = fi.Attributes & ~FileAttributes.ReadOnly;
            }
          }
        }

        try
        {
          using (new WaitCursor(this))
          {
            fs.Open(filename);

            if (_fs != null)
            {
              _fs.Close();
            }
            _fs = fs;

            Text = Application.ProductName + " - " + new FileInfo(filename).Name;
          }

          PopulateUI();
        }
        catch (Exception ex)
        {
          fs.Close();
          MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
      }
    }

    private static string FriendlySize(long size)
    {
      // Like Explorer's details view: always whole kilobytes, rounded up.
      return (size == 0 ? 0 : (size + 1023) / 1024).ToString("N0") + " KB";
    }

    private void PopulateListView()
    {
      // Redisable some buttons (will be autoenabled based on selection)
      tsbPreview.Enabled = false;
      tsbEdit.Enabled = false;

      Directory dir = _selectedDir;

      string filterString = tstFilterBox.Text;

      List<string> selectedFileNames = new List<string>();
      foreach (var o in lvFiles.SelectedItems)
      {
        selectedFileNames.Add((o as ListViewItem).Text);
      }

      var comparer = lvFiles.ListViewItemSorter;
      lvFiles.ListViewItemSorter = null;

      lvFiles.BeginUpdate();

      lvFiles.Items.Clear();
      lvFiles.Groups.Clear();

      var groups = new Dictionary<string, ListViewGroup>();
      var groupOrder = new Dictionary<ListViewGroup, int>();

      using (new WaitCursor(this))
      {
        foreach (var item in dir)
        {
          if (filterString != "" && item.Name.IndexOf(filterString, StringComparison.OrdinalIgnoreCase) < 0)
          {
            continue;
          }

          FileType type = item.IsDirectory ? FileTypes.Folder : FileTypes.ForFile(item.Name);

          ListViewGroup group;
          if (!groups.TryGetValue(type.Group, out group))
          {
            group = new ListViewGroup(type.Group);
            groups.Add(type.Group, group);
            groupOrder.Add(group, type.GroupOrder);
          }

          ListViewItem lvi = new ListViewItem(item.Name, group);
          lvi.Tag = item;
          lvi.ImageKey = item.IsDirectory ? _icons.KeyForFolder() : _icons.KeyFor(type);
          lvi.SubItems.Add(type.Name);

          File file = item as File;
          if (file == null)
          {
            lvi.SubItems.Add("");
            lvi.SubItems.Add("");
          }
          else
          {
            lvi.SubItems.Add(FriendlySize(file.Size));

            /*
            string compressed = file.IsCompressed ? "Yes (" + FriendlySize(file.CompressedSize) + ")" : "No";
            lvi.SubItems.Add(compressed);
             */

            string resources = file.IsResource ? "Yes" : "No";
            if (file.IsResource)
            {
              string rscType = Enum.IsDefined(file.ResourceType.GetType(), file.ResourceType)
                                   ?
                                       file.ResourceType.ToString()
                                   : string.Format("Unknown 0x{0:x}", (int)file.ResourceType);
              resources += " (" + rscType + ")";
            }
            lvi.SubItems.Add(resources);

            if (file.IsCustomData)
            {
              lvi.ForeColor = CustomDataForeColor;
            }
          }

          lvFiles.Items.Add(lvi);

          if (selectedFileNames.Contains(item.Name))
          {
            lvi.Selected = true;
          }
        }
      }

      // Groups are shown in the order they were added: folders first, then the file types.
      var ordered = new List<ListViewGroup>(groups.Values);
      ordered.Sort((a, b) => groupOrder[a].CompareTo(groupOrder[b]));
      foreach (var group in ordered)
      {
        group.Header = group.Header + " (" + group.Items.Count + ")";
        lvFiles.Groups.Add(group);
      }

      lvFiles.EndUpdate();

      lvFiles.ListViewItemSorter = comparer;
      lvFiles.Sort();

      UpdateStatus();
    }

    private void UpdateStatus()
    {
      if (_fs == null)
      {
        tsslItems.Text = "";
        tsslSelection.Text = "";
        return;
      }

      tsslItems.Text = lvFiles.Items.Count == 1 ? "1 item" : lvFiles.Items.Count + " items";

      int count = lvFiles.SelectedItems.Count;
      if (count == 0)
      {
        tsslSelection.Text = "";
        return;
      }

      long size = 0;
      bool onlyFiles = true;
      foreach (ListViewItem item in lvFiles.SelectedItems)
      {
        File file = item.Tag as File;
        if (file != null)
        {
          size += file.Size;
        }
        else
        {
          onlyFiles = false;
        }
      }

      string text = count == 1 ? "1 item selected" : count + " items selected";
      if (onlyFiles)
      {
        text += "  " + FriendlySize(size);
      }
      tsslSelection.Text = text;
    }

    private void CreateDirectoryNode(TreeNode node, Directory dir)
    {
      node.Tag = dir;

      foreach (var item in dir)
      {
        if (item.IsDirectory)
        {
          Directory subdir = item as Directory;
          TreeNode subnode = node.Nodes.Add(subdir.Name);
          subnode.ImageKey = subnode.SelectedImageKey = _icons.KeyForFolder();
          CreateDirectoryNode(subnode, subdir);
        }
      }
    }

    private void OpenDirectory(Directory dir)
    {
      TreeNode node = FindNode(tvDir.Nodes, dir);
      if (node != null)
      {
        node.Expand();
        tvDir.SelectedNode = node;
      }
    }

    private static TreeNode FindNode(TreeNodeCollection nodes, Directory dir)
    {
      foreach (TreeNode node in nodes)
      {
        if (node.Tag == dir)
        {
          return node;
        }

        TreeNode found = FindNode(node.Nodes, dir);
        if (found != null)
        {
          return found;
        }
      }
      return null;
    }

    private void SetInitialUIState()
    {
      // Disable some buttons
      tsbOpen.Enabled = false;
      tsbSave.Enabled = false;
      tsbRebuild.Enabled = false;
      tsbExportAll.Enabled = false;
      tsbImport.Enabled = false;
      tsbExportSelected.Enabled = false;
      tsbPreview.Enabled = false;
      tsbEdit.Enabled = false;
      tstFilterBox.Enabled = false;
    }

    private void PopulateUI()
    {
      // Reenable some buttons
      tsbOpen.Enabled = true;
      tsbSave.Enabled = true;
      tsbRebuild.Enabled = _fs.SupportsRebuild;
      tsbExportAll.Enabled = true;
      tsbImport.Enabled = true;
      tsbExportSelected.Enabled = true;
      tstFilterBox.Enabled = true;

      // Redisable some buttons (will be autoenabled based on selection)
      tsbPreview.Enabled = false;
      tsbEdit.Enabled = false;

      // Sorted by name, like Explorer.
      _sortColumn = 0;
      lvFiles.Sorting = SortOrder.Ascending;
      lvFiles.ListViewItemSorter = new ListViewItemComparer(0, false);

      splitContainer.Panel1Collapsed = !_fs.HasDirectoryStructure;

      tvDir.Nodes.Clear();

      TreeNode root = tvDir.Nodes.Add(_fs.RootDirectory.Name);
      root.ImageKey = root.SelectedImageKey = _fs is RealFileSystem
                                                ? _icons.KeyForFolder()
                                                : _icons.KeyFor(FileTypes.ForFile(".rpf"));
      CreateDirectoryNode(root, _fs.RootDirectory);

      root.ExpandAll();
      root.EnsureVisible();

      tvDir.SelectedNode = root;
    }

    private File FindFileByName(string name)
    {
      foreach (var fsObject in _selectedDir)
      {
        File file = fsObject as File;
        if (file != null)
        {
          if (file.Name.ToLower() == name.ToLower())
          {
            return file;
          }
        }
      }
      return null;
    }

    private void ExtractToPath(Directory dir, string path)
    {
      foreach (var item in dir)
      {
        if (item.IsDirectory)
        {
          try
          {
            IODirectory.CreateDirectory(path + item.Name);
            ExtractToPath(item as Directory, path + item.Name + "\\");
          }
          catch (Exception ex)
          {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
          }
        }
        else
        {
          File file = item as File;
          byte[] data = file.GetData();
          IOFile.WriteAllBytes(Path.Combine(path, file.Name), data);
        }
      }
    }

    private void EditFile(File file)
    {
      if (Editors.HasEditor(file))
      {
        Editors.LaunchEditor(_fs, file);
        if (file.IsCustomData)
        {
          foreach (ListViewItem item in lvFiles.Items)
          {
            if (item.Tag == file)
            {
              item.ForeColor = CustomDataForeColor;
              break;
            }
          }
        }
      }
    }

    private void PreviewFile(File file)
    {
      if (Viewers.HasViewer(file))
      {
        Control viewerControl = Viewers.GetControl(file);
        if (viewerControl != null)
        {
          using (var form = new ViewerForm())
          {
            form.SetFilename(file.Name);
            form.SetControl(viewerControl);
            form.ShowDialog();
          }
        }
      }
    }

    private void PreviewOrEditFile(File file)
    {
      if (Viewers.HasViewer(file))
      {
        PreviewFile(file);
      }
      else if (Editors.HasEditor(file))
      {
        EditFile(file);
      }
    }

    /// <summary>
    /// GTA IV: The Complete Edition: one GTAIV.exe and the episodes in the TLAD and TBoGT subfolders.
    /// </summary>
    private static bool IsCompleteEdition(string gamePath)
    {
      return IOFile.Exists(Path.Combine(gamePath, "GTAIV.exe")) &&
             (IODirectory.Exists(Path.Combine(gamePath, "TBoGT")) || IODirectory.Exists(Path.Combine(gamePath, "TLAD")));
    }

    private void LoadGameDirectory(KeyUtil keyUtil, string gameName)
    {
      using (new WaitCursor(this))
      {
        FileSystem fs = new RealFileSystem();

        string gamePath = keyUtil.FindGameDirectory();

        // The Complete Edition has no EFLC.exe and only registers GTA IV: GTAIV.exe runs all three games and holds the same key.
        if (keyUtil is KeyUtilEFLC && (gamePath == null || !IOFile.Exists(Path.Combine(gamePath, keyUtil.ExecutableName))))
        {
          string ivPath = new KeyUtilGTAIV().FindGameDirectory();
          if (ivPath != null && IsCompleteEdition(ivPath))
          {
            gamePath = ivPath;
          }
        }

        if (gamePath != null && !IOFile.Exists(Path.Combine(gamePath, keyUtil.ExecutableName)))
        {
          if (keyUtil is KeyUtilEFLC && IsCompleteEdition(gamePath))
          {
            keyUtil = new KeyUtilGTAIV();
          }
          else
          {
            gamePath = null;
          }
        }

        while (gamePath == null)
        {
          var fbd = new VistaFolderBrowserDialog
          {
            Description =
                  "Could not find the " + gameName + " game directory. Please select the directory containing " + keyUtil.ExecutableName +
                  (keyUtil is KeyUtilEFLC ? " (or GTAIV.exe of the Complete Edition)" : ""),
            ShowNewFolderButton = false
          };

          if (fbd.ShowDialog() == DialogResult.Cancel)
          {
            MessageBox.Show(
                keyUtil.ExecutableName +
                " is required to extract cryptographic keys for this program to function. " +
                "SparkIV can not run without this file.", "Error", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
          }
          if (System.IO.File.Exists(Path.Combine(fbd.SelectedPath, keyUtil.ExecutableName)))
          {
            gamePath = fbd.SelectedPath;
          }
          else if (keyUtil is KeyUtilEFLC && IsCompleteEdition(fbd.SelectedPath))
          {
            keyUtil = new KeyUtilGTAIV();
            gamePath = fbd.SelectedPath;
          }
        }

        byte[] key = keyUtil.FindKey(gamePath);

        if (key == null)
        {
          string message = "Your " + keyUtil.ExecutableName + " seems to be modified or is a newer version than this tool supports. " +
                          "SparkIV can not run without a supported " + keyUtil.ExecutableName + " file." + "\n" + "Would you like to check for updates?";
          string caption = "Newer or Modified " + keyUtil.ExecutableName;

          if (MessageBox.Show(message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
          {
            Updater.CheckForUpdate();
          }

          return;
        }


        KeyStore.SetKeyLoader(() => key);

        fs.Open(gamePath);

        if (_fs != null)
        {
          _fs.Close();
        }
        _fs = fs;

        Text = Application.ProductName + " - Browse Game Directory";

        PopulateUI();
      }
    }

    #endregion

    #region Toolbar Handlers

    private void toolStripEFLC_Click(object sender, EventArgs e)
    {
      LoadGameDirectory(new KeyUtilEFLC(), "EFLC");
    }

    private void toolStripGTAIV_Click(object sender, EventArgs e)
    {
      LoadGameDirectory(new KeyUtilGTAIV(), "GTAIV");
    }

    private void tsbOpen_Click(object sender, EventArgs e)
    {
      OpenFileDialog ofd = new OpenFileDialog();
      ofd.Title = "Open IV Archive";
      ofd.Filter = "All Supported IV Archives|*.rpf;*.img|RPF Files (*.rpf)|*.rpf|IMG Files (*.img)|*.img";
      ofd.FileName = _lastOpenPath;

      if (ofd.ShowDialog() == DialogResult.OK)
      {
        _lastOpenPath = ofd.FileName;

        FileSystem fs = null;

        if (ofd.FilterIndex == 2)
        {
          fs = new RPFFileSystem();
        }
        else if (ofd.FilterIndex == 3)
        {
          fs = new IMGFileSystem();
        }
        else
        {
          if (ofd.FileName.EndsWith(".rpf"))
          {
            fs = new RPFFileSystem();
          }
          else if (ofd.FileName.EndsWith(".img"))
          {
            fs = new IMGFileSystem();
          }
          else
          {
            MessageBox.Show("Please select a type for the file you are trying to open.", "Open IV Archive", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
          }
        }

        OpenFile(ofd.FileName, fs);
      }

    }

    private void tsbSave_Click(object sender, EventArgs e)
    {
      if (_fs == null) return;

      try
      {
        using (new WaitCursor(this))
        {
          _fs.Save();
        }

        PopulateListView();

        MessageBox.Show("The archive has been saved.", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
      catch
      {
        MessageBox.Show("Could not save the archive.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void tsbExportSelected_Click(object sender, EventArgs e)
    {
      if (_fs == null) return;

      if (lvFiles.SelectedItems.Count == 1 && lvFiles.SelectedItems[0].Tag is File)
      {
        File file = lvFiles.SelectedItems[0].Tag as File;

        SaveFileDialog sfd = new SaveFileDialog();
        sfd.Title = "Export...";

        if (_lastImportExportPath != null)
        {
          sfd.InitialDirectory = _lastImportExportPath;
          sfd.FileName = Path.Combine(_lastImportExportPath, file.Name);
        }
        else
        {
          sfd.FileName = file.Name;
        }


        sfd.OverwritePrompt = true;

        if (sfd.ShowDialog() == DialogResult.OK)
        {
          _lastImportExportPath = IODirectory.GetParent(sfd.FileName).FullName;

          using (new WaitCursor(this))
          {
            byte[] data = file.GetData();
            IOFile.WriteAllBytes(sfd.FileName, data);
          }
        }
      }
      else if (lvFiles.SelectedItems.Count > 0)
      {
        FolderBrowserDialog fbd = new FolderBrowserDialog();
        fbd.Description = "Export Selected...";
        fbd.ShowNewFolderButton = true;
        fbd.SelectedPath = _lastImportExportPath;

        if (fbd.ShowDialog() == DialogResult.OK)
        {
          _lastImportExportPath = fbd.SelectedPath;

          string path = fbd.SelectedPath;

          using (new WaitCursor(this))
          {
            foreach (ListViewItem item in lvFiles.SelectedItems)
            {
              File file = item.Tag as File;
              if (file != null)
              {
                byte[] data = file.GetData();
                IOFile.WriteAllBytes(Path.Combine(path, file.Name), data);
              }
              else
              {
                // A selected folder is exported with everything in it.
                var dir = (Directory)item.Tag;
                string dirPath = Path.Combine(path, dir.Name);
                IODirectory.CreateDirectory(dirPath);
                ExtractToPath(dir, dirPath + "\\");
              }
            }
          }

          MessageBox.Show("All selected files exported.", "Export Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
      }
    }

    private void tsbExportAll_Click(object sender, EventArgs e)
    {
      if (_fs == null) return;

      FolderBrowserDialog fbd = new FolderBrowserDialog();
      fbd.Description = "Export All...";
      fbd.ShowNewFolderButton = true;
      fbd.SelectedPath = _lastImportExportPath;

      if (fbd.ShowDialog() == DialogResult.OK)
      {
        _lastImportExportPath = fbd.SelectedPath;

        string path = fbd.SelectedPath;
        if (!path.EndsWith("\\")) path += "\\";

        using (new WaitCursor(this))
        {
          ExtractToPath(_fs.RootDirectory, path);
        }

        MessageBox.Show("All files in archive exported.", "Export All", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
    }

    private void tsbImport_Click(object sender, EventArgs e)
    {
      if (_fs == null) return;

      var ofd = new OpenFileDialog();
      ofd.Title = "Import...";

      if (_lastImportExportPath != null)
      {
        ofd.InitialDirectory = _lastImportExportPath;
      }

      ofd.CheckFileExists = true;
      ofd.CheckPathExists = true;
      ofd.Multiselect = true;

      if (ofd.ShowDialog() == DialogResult.OK)
      {
        _lastImportExportPath = IODirectory.GetParent(ofd.FileName).FullName;

        List<string> _invalidFiles = new List<string>();
        using (new WaitCursor(this))
        {
          for (var i = 0; i < ofd.FileNames.Length; i++)
          {
            var safename = Path.GetFileName(ofd.FileNames[i]);
            File file = FindFileByName(safename);
            if (file == null)
            {
              _invalidFiles.Add(safename);
            }
            else
            {
              byte[] data = IOFile.ReadAllBytes(ofd.FileNames[i]);
              file.SetData(data);
            }
          }
        }

        if (_invalidFiles.Count > 0)
        {
          var sb = new StringBuilder();
          foreach (var s in _invalidFiles)
          {
            sb.Append("  " + s + "\n");
          }
          MessageBox.Show("The following files were not found in the archive to be replaced:\n\n" + sb +
                          "\nPlease note that you can not add new files, only replace existing ones. The files must be named exactly " +
                          "as they are in the archive.", "Import", MessageBoxButtons.OK,
                          MessageBoxIcon.Exclamation);
        }

        PopulateListView();
      }
    }

    private void tsbRebuild_Click(object sender, EventArgs e)
    {
      if (_fs == null) return;

      try
      {
        using (new WaitCursor(this))
        {
          _fs.Rebuild();
        }

        PopulateListView();

        MessageBox.Show("The archive has been rebuilt.", "Rebuild", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
      catch
      {
        MessageBox.Show("Could not rebuild the archive.\n\n" +
                        "Note that only IMG files can be rebuilt at the moment, rebuilding RPF files is not currently supported.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }

    }

    private void tstFilterBox_TextChanged(object sender, EventArgs e)
    {
      if (_fs == null) return;

      PopulateListView();
    }

    private void tsbPreview_Click(object sender, EventArgs e)
    {
      if (lvFiles.SelectedItems.Count == 1)
      {
        var file = lvFiles.SelectedItems[0].Tag as File;
        if (file != null)
        {
          PreviewFile(file);
        }
      }
    }


    private void tsbEdit_Click(object sender, EventArgs e)
    {
      if (lvFiles.SelectedItems.Count == 1)
      {
        var file = lvFiles.SelectedItems[0].Tag as File;
        if (file != null)
        {
          EditFile(file);
        }
      }
    }

    /// <summary>Opens a folder or previews/edits a file, like a double-click in Explorer.</summary>
    private void ActivateItem(ListViewItem item)
    {
      var dir = item.Tag as Directory;
      if (dir != null)
      {
        OpenDirectory(dir);
      }
      else
      {
        PreviewOrEditFile(item.Tag as File);
      }
    }

    #endregion

    #region Event Handlers

    private void tvDir_AfterSelect(object sender, TreeViewEventArgs e)
    {
      Directory dir = (e.Node.Tag as Directory);
      _selectedDir = dir;
      PopulateListView();
    }

    private void lvFiles_MouseDoubleClick(object sender, MouseEventArgs e)
    {
      ListViewHitTestInfo hitTest = lvFiles.HitTest(e.X, e.Y);
      if (hitTest.Item != null)
      {
        ActivateItem(hitTest.Item);
      }
    }

    private void lvFiles_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.KeyCode == Keys.Enter)
      {
        if (lvFiles.SelectedItems.Count == 1)
        {
          ActivateItem(lvFiles.SelectedItems[0]);
        }
      }

      if (e.KeyCode == Keys.Back && _selectedDir != null && _selectedDir.ParentDirectory != null)
      {
        OpenDirectory(_selectedDir.ParentDirectory);
        e.Handled = true;
      }

      if (e.KeyCode == Keys.Delete && lvFiles.SelectedItems.Count > 0 && lvFiles.SelectedItems[0].Tag is File)
      {
        DialogResult dialogResult = MessageBox.Show("Are you sure to delete this file : " + lvFiles.SelectedItems[0].Text, "Delete", MessageBoxButtons.YesNo);
        if (dialogResult == DialogResult.Yes)
        {
          //do something
          if (System.IO.File.Exists(Path.Combine(KeyUtil.GetDir.Get(), lvFiles.SelectedItems[0].Text)))
          {
            System.IO.File.Delete(Path.Combine(KeyUtil.GetDir.Get(), lvFiles.SelectedItems[0].Text));
            lvFiles.Items.Remove(lvFiles.SelectedItems[0]);
          }
        }
        else if (dialogResult == DialogResult.No)
        {
          //do something else
        }
      }

      //if (e.KeyCode == Keys.C && e.Control)
      //{
      //  Clipboard.SetData(DataFormats.FileDrop, Path.Combine(KeyUtil.GetDir.Get(), lvFiles.SelectedItems[0].Text));
      //Clipboard.SetFileDropList(Path.Combine(KeyUtil.GetDir.Get(), lvFiles.SelectedItems[0].Text));
      //}
      // if (e.KeyCode == Keys.V && e.Control)
      // {
      //  if (Clipboard.ContainsFileDropList())
      //  {
      //copy to D:\test
      //     foreach (string source in Clipboard.GetFileDropList())
      //    {
      //         System.IO.File.Copy(source, KeyUtil.GetDir.Get());
      //         lvFiles.Items.Add(Path.GetFileName(source));
      //         lvFiles.Sorting = SortOrder.Ascending;
      //    }
      // }
      // }
    }

    private void lvFiles_ColumnClick(object sender, ColumnClickEventArgs e)
    {
      if (e.Column != _sortColumn)
      {
        _sortColumn = e.Column;
        lvFiles.Sorting = SortOrder.Ascending;
      }
      else
      {
        lvFiles.Sorting = lvFiles.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
      }

      if (e.Column != SizeColumn)
      {
        lvFiles.ListViewItemSorter = new ListViewItemComparer(e.Column, lvFiles.Sorting == SortOrder.Descending);
      }
      else
      {
        lvFiles.ListViewItemSorter = new ListViewItemComparer(lvFiles.Sorting == SortOrder.Descending);
      }

      lvFiles.Sort();
    }

    private void lvFiles_SelectedIndexChanged(object sender, EventArgs e)
    {
      var file = lvFiles.SelectedItems.Count == 1 ? lvFiles.SelectedItems[0].Tag as File : null;
      tsbPreview.Enabled = file != null && Viewers.HasViewer(file);
      tsbEdit.Enabled = file != null && Editors.HasEditor(file);

      UpdateStatus();
    }

    private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
    {
      if (_fs != null)
      {
        _fs.Close();
      }
      _icons.Dispose();
    }

    private void tslAbout_Click(object sender, EventArgs e)
    {
      Updater.CheckForUpdate();
    }

    #endregion

    private void MainForm_Load(object sender, EventArgs e)
    {

    }
  }
}