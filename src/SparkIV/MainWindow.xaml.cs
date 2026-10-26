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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using RageLib.Common;
using SparkIV.WinUI.Model;
using SparkIV.WinUI.Services;
using Windows.Storage.Pickers;
using Windows.System;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;
using FSObject = RageLib.FileSystem.Common.FSObject;

namespace SparkIV.WinUI
{
  public sealed partial class MainWindow : Window
  {
    private readonly Volumes _volumes = new Volumes();
    private readonly Stack<Location> _back = new Stack<Location>();
    private readonly Stack<Location> _forward = new Stack<Location>();
    private readonly HashSet<Volume> _modified = new HashSet<Volume>();
    private List<EntryGroup> _groups = new List<EntryGroup>();
    private Location? _current;
    private string _sortKey = "Name";
    private bool _sortDescending;

    public MainWindow()
    {
      InitializeComponent();

      ExtendsContentIntoTitleBar = true;
      SetTitleBar(AppTitleBar);

      // With the content extended into the title bar, the caption buttons keep fixed colors;
      // give them the colors of the current theme so they stay visible on the Mica background.
      Root.Loaded += (sender, args) => ApplyCaptionButtonColors();
      Root.ActualThemeChanged += (sender, args) => ApplyCaptionButtonColors();

      string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "SparkIV.ico");
      if (System.IO.File.Exists(icon))
      {
        AppWindow.SetIcon(icon);
        TitleIcon.Source = new BitmapImage(new Uri(icon));
      }
      AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

      Closed += (sender, args) =>
      {
        _volumes.Dispose();
        _externalEdits?.Dispose();
        EditMode.Changed -= UpdateEditModeButton;
      };

      // Edit mode is also turned on from a viewer (Replace).
      EditMode.Changed += UpdateEditModeButton;

      // Also when the list item has already handled the release itself.
      FileList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FileList_PointerReleased), true);

      UpdateSortHeaders();
      UpdateEditModeButton();

      CheckForUpdate();
    }

    private async void CheckForUpdate()
    {
      // Just updated: what's new, once (a newer update found below takes its place).
      if (Updates.JustUpdated)
      {
        ShowMessage(InfoBarSeverity.Success, "Updated", "SparkIV " + Updates.CurrentVersion + " is installed.",
                    "What's new", async () => await ChangelogDialog.ShowAsync(Root.XamlRoot, Updates.CurrentVersion));
      }
      string? version = await Updates.CheckAsync();
      if (version != null)
      {
        ShowMessage(InfoBarSeverity.Informational, "Update available", "SparkIV " + version + " is ready to install.",
                    "Install and restart", async () => await InstallUpdateAsync());
      }
    }

    private async Task InstallUpdateAsync()
    {
      ShowMessage(InfoBarSeverity.Informational, "Updating", "Downloading the update; SparkIV restarts when it is done.");
      try
      {
        await Updates.InstallAndRestartAsync();
      }
      catch (Exception ex)
      {
        ShowMessage(InfoBarSeverity.Error, "Update failed", ex.Message);
      }
    }

    #region Opening

    private async void OpenGame_Click(object sender, RoutedEventArgs e) => await OpenGameAsync(episodes: false);

    private async void OpenEpisodes_Click(object sender, RoutedEventArgs e) => await OpenGameAsync(episodes: true);

    private async Task OpenGameAsync(bool episodes)
    {
      Game game = episodes ? Games.Episodes : Games.GtaIV;
      string gameName = episodes ? "Episodes from Liberty City" : "GTA IV";

      // The folder set in the settings, else the one found; asked for once and then remembered.
      string? path = Games.InstallFolder(game, "Windows");
      if (path == null)
      {
        var (folder, error) = await GameFolders.PickAsync(this, game, "Windows");
        if (error != null)
        {
          ShowMessage(InfoBarSeverity.Error, "Not a game folder", error);
        }
        if (folder == null)
        {
          return;
        }
        path = folder;
      }

      var install = new GameInstall(game, path, "Windows");
      await RunBusyAsync(async () =>
      {
        if (!await LoadKeyAsync(install))
        {
          return;
        }

        ResetVolumes();
        Volume volume = await Task.Run(() => _volumes.OpenFolder(install.Folder));
        // Models find their textures through the game's .ide files and shared dictionaries.
        Games.SetCurrent(install);
        ShowVolume(volume, gameName + "  \u00B7  " + install.Folder);
        _ = IndexCutsceneScriptsAsync(install);
      });
    }

    /// <summary>
    /// When a game is loaded for the first time (or the game or SparkIV changed since): which mission script
    /// plays which cutscene and how it colours its vehicles, from the game's scripts (GTA IV, and TLAD and TBoGT
    /// each for the episodes), in the background, shown in the info bar. See CutsceneScriptIndex.
    /// </summary>
    private async Task IndexCutsceneScriptsAsync(GameInstall install)
    {
      // The game's own folders, and the episodes' inside it too (the Complete Edition keeps TLAD and TBoGT in
      // GTA IV's folder: browsed from GTA IV, their cutscenes open from there).
      var folders = install.Game.ContentFolders.Concat(Games.Episodes.ContentFolders)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .Select(c => (Name: c.Length == 0 ? install.Game.ShortName : c, Folder: c.Length == 0 ? install.Folder : Path.Combine(install.Folder, c)))
                           .Where(g => Viewers.GtaIV.Pc.CutsceneScriptIndex.NeedsBuild(g.Folder))
                           .ToList();
      if (folders.Count == 0)
      {
        return;
      }
      var done = new List<string>();
      try
      {
        foreach (var (name, folder) in folders)
        {
          void Report(int at, int of) => DispatcherQueue.TryEnqueue(() =>
            ShowMessage(InfoBarSeverity.Informational, "Indexing the cutscenes' mission scripts",
                        name + ": script " + Math.Min(at + 1, of) + " of " + of + " (once per game, and again after the game or SparkIV changes)"));
          var index = await Viewers.GtaIV.Pc.CutsceneScriptIndex.EnsureAsync(folder, Report);
          done.Add(name + " " + index.Cutscenes.Count);
        }
        ShowMessage(InfoBarSeverity.Success, "Cutscene scripts indexed", "Cutscenes found in mission scripts: " + string.Join(", ", done) + ".");
      }
      catch (Exception ex)
      {
        ShowMessage(InfoBarSeverity.Warning, "Could not index the cutscenes' mission scripts", ex.Message);
      }
    }

    #region Edit mode

    private async void EditMode_Click(object sender, RoutedEventArgs e)
    {
      if (EditMode.IsOn)
      {
        EditMode.TurnOff();
      }
      else
      {
        await EditMode.TurnOnAsync(Root.XamlRoot);
      }
      UpdateEditModeButton();
    }

    private void UpdateEditModeButton()
    {
      EditModeButton.IsChecked = EditMode.IsOn;
      ToolTipService.SetToolTip(EditModeButton, EditMode.IsOn
        ? "Edit mode is on: game files can be changed. Click to turn it off."
        : "Turn on changing game files (import, replace, save)");
      UpdateCommands();
    }

    // Whether the shield is on Save: checked when the folder changes, not on every selection.
    private Volume? _shieldVolume;
    private bool _shield;

    /// <summary>Like Windows: the shield on Save when saving there needs administrator rights.</summary>
    private void UpdateSaveShield()
    {
      Volume? volume = _current?.Volume;
      if (!ReferenceEquals(volume, _shieldVolume))
      {
        _shieldVolume = volume;
        // The folder the file on disk is in: of the game, or of the outermost archive.
        Volume? outer = volume == null ? null : Saving.Chain(volume).Last();
        string? folder = outer?.FileSystem is RageLib.FileSystem.RealFileSystem real ? real.RealDirectory
          : outer?.ArchivePath != null ? System.IO.Path.GetDirectoryName(outer.ArchivePath) : null;
        _shield = EditMode.NeedsAdministrator(folder);
      }
      SaveGlyph.Glyph = _shield ? "\uEA18" : "\uE74E";
      ToolTipService.SetToolTip(SaveButton, _shield
        ? "Write the changes (Windows asks for administrator rights: the game is in a protected folder)"
        : "Write the changes");
    }

    #endregion

    /// <summary>Reads the key for the archives from the game's exe; false (with a message) if it isn't there.</summary>
    private async Task<bool> LoadKeyAsync(GameInstall install)
    {
      KeyUtil keyUtil = install.KeyUtil;
      byte[]? key = await Task.Run(() => keyUtil.FindKey(install.Folder));
      if (key == null)
      {
        ShowMessage(InfoBarSeverity.Error, "Unsupported " + keyUtil.ExecutableName,
                    "Your " + keyUtil.ExecutableName + " seems to be modified or is a newer version than SparkIV supports.");
        return false;
      }
      KeyStore.SetKeyLoader(() => key);
      return true;
    }

    private SettingsWindow? _settings;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
      if (_settings == null)
      {
        _settings = new SettingsWindow();
        _settings.Closed += (s, args) => _settings = null;
      }
      _settings.Activate();
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
      var picker = new FileOpenPicker();
      picker.FileTypeFilter.Add(".rpf");
      picker.FileTypeFilter.Add(".img");
      InitializeWithWindow(picker);
      var file = await picker.PickSingleFileAsync();
      if (file == null)
      {
        return;
      }

      await OpenArchiveAsync(file.Path);
    }

    /// <summary>
    /// Opens an .rpf or .img file (also when SparkIV is started with one). Its key comes from its game:
    /// the one it lies in, else the one asked for (<paramref name="install"/> when that was asked already).
    /// </summary>
    internal async Task OpenArchiveAsync(string path, GameInstall? install = null)
    {
      install ??= Games.ForFile(path) ?? await GameChooserWindow.AskAsync(path, optional: false);
      if (install == null)
      {
        return;
      }

      await RunBusyAsync(async () =>
      {
        if (!await LoadKeyAsync(install))
        {
          return;
        }

        ResetVolumes();
        Volume volume = await Task.Run(() => _volumes.OpenArchive(path));
        Games.SetCurrent(install);
        ShowVolume(volume, install.Game.Name + "  ·  " + path);
      });
    }

    private void ResetVolumes()
    {
      // Set again for what is opened next.
      Games.SetCurrent(null);
      // Copies open in other programs belong to what is closed now.
      _externalEdits?.Forget(edit => true);
      _volumes.CloseAll();
      _modified.Clear();
      _back.Clear();
      _forward.Clear();
      _current = null;
      FolderTree.RootNodes.Clear();
    }

    private void ShowVolume(Volume volume, string detail)
    {
      TitleDetail.Text = detail;

      var root = CreateNode(TreeEntry.ForFolder(volume.Root));
      FolderTree.RootNodes.Add(root);

      Navigate(volume.Root);
    }

    private Task<Volume> OpenNestedAsync(Location host, File file) => Task.Run(() => _volumes.OpenNested(host, file));

    #endregion

    #region Navigation

    private void Navigate(Location target, bool record = true)
    {
      if (record && _current != null && !target.Equals(_current))
      {
        _back.Push(_current);
        _forward.Clear();
      }
      _current = target;

      // Like Explorer: a new folder starts unfiltered.
      FilterBox.Text = "";
      RefreshList();

      Breadcrumb.ItemsSource = target.Path();
      _ = SyncTreeAsync(target);
      UpdateCommands();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
      if (_back.Count > 0 && _current != null)
      {
        _forward.Push(_current);
        Navigate(_back.Pop(), record: false);
      }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
      if (_forward.Count > 0 && _current != null)
      {
        _back.Push(_current);
        Navigate(_forward.Pop(), record: false);
      }
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void GoUp()
    {
      Location? parent = _current?.Parent;
      if (parent != null)
      {
        Navigate(parent);
      }
    }

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
      if (args.Item is Location location)
      {
        Navigate(location);
      }
    }

    /// <summary>Opens a folder or an archive, like a double-click in Explorer.</summary>
    private async Task ActivateAsync(EntryItem item)
    {
      if (_current == null)
      {
        return;
      }

      if (item.Directory != null)
      {
        Navigate(new Location(_current.Volume, item.Directory));
      }
      else if (item.IsArchive && item.File != null)
      {
        Location host = _current;
        File file = item.File;
        await RunBusyAsync(async () => Navigate((await OpenNestedAsync(host, file)).Root));
      }
      else if (item.File != null && Viewers.ViewerRegistry.Find(item.Name) is Viewers.IViewer viewer)
      {
        OpenViewer(item.File, viewer);
      }
      else if (item.File != null)
      {
        File file = item.File;
        string extension = System.IO.Path.GetExtension(item.Name);
        string kind = extension.Length > 1 ? extension.ToLowerInvariant() + " files" : "files without an extension";
        ShowMessage(InfoBarSeverity.Informational, "No viewer for " + kind + " yet",
                    "SparkIV can't show " + item.Name + " (" + item.TypeName + ") yet. You can look at its bytes or export it.",
                    "Open as binary", () => OpenViewer(file, Viewers.ViewerRegistry.Binary));
      }
    }

    /// <summary>
    /// Changed and not saved yet: a file with new content (imported, a texture replaced), or an archive
    /// with such files inside that isn't saved yet.
    /// </summary>
    private bool IsUnsaved(FSObject entry)
    {
      if (entry is not File file)
      {
        return false;
      }
      return file.IsCustomData || _modified.Any(v => ReferenceEquals(v.HostFile, file));
    }

    /// <summary>The top folder of the file system a file is in.</summary>
    internal static Directory RootOf(File file)
    {
      Directory dir = file.ParentDirectory;
      while (dir.ParentDirectory != null)
      {
        dir = dir.ParentDirectory;
      }
      return dir;
    }

    private void OpenViewer(File file, Viewers.IViewer viewer)
    {
      if (_current == null)
      {
        return;
      }
      string where = string.Join(" \u203A ", _current.Path().Select(l => l.Name));
      Volume volume = _current.Volume;

      // A viewer that changes a file (a texture replaced) puts it back here, like Import: in the archive,
      // written with Save. Also the files next to it a viewer opens (the pages a web page links to), as
      // long as they are in the same volume.
      Func<byte[], Task<string>>? ReplaceOf(File changed)
      {
        if (!ReferenceEquals(RootOf(changed), volume.FileSystem.RootDirectory))
        {
          return null;
        }
        return data =>
        {
          changed.SetData(data);
          FileChanges.Raise(changed);
          _modified.Add(volume);
          RefreshList();
          UpdateCommands();
          ShowMessage(InfoBarSeverity.Success, changed.Name + " changed", "Use Save to write it into " + volume.Name + ".");
          return Task.FromResult("Use Save in the main window to write it into " + volume.Name + ".");
        };
      }

      OpenViewerWhenInputDone(() =>
      {
        var window = new Viewers.ViewerWindow(file, viewer, where, ReplaceOf);
        window.Activate();
        window.BringToFront();
      });
    }

    #endregion

    #region Folder tree

    /// <summary>
    /// Folders get their whole subtree right away (it is in memory anyway), so expanding them needs no
    /// lazy loading. Only archives load lazily, because they have to be opened first.
    /// </summary>
    private static TreeViewNode CreateNode(TreeEntry entry)
    {
      var node = new TreeViewNode { Content = entry };
      if (entry.IsArchive && entry.Folder == null)
      {
        node.HasUnrealizedChildren = true;
      }
      else if (entry.Folder != null)
      {
        Populate(node, entry.Folder);
      }
      return node;
    }

    private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
      if (args.Node.HasUnrealizedChildren)
      {
        await RealizeAsync(args.Node);
      }
    }

    /// <summary>Fills in the children of a node; an archive is opened first.</summary>
    private async Task RealizeAsync(TreeViewNode node)
    {
      var entry = (TreeEntry)node.Content;
      if (entry.IsRealizing)
      {
        return;
      }

      if (entry.Folder == null && entry.ArchiveHost != null && entry.ArchiveFile != null)
      {
        entry.IsRealizing = true;
        try
        {
          entry.Folder = (await OpenNestedAsync(entry.ArchiveHost, entry.ArchiveFile)).Root;
        }
        catch (Exception ex)
        {
          node.HasUnrealizedChildren = false;
          ShowMessage(InfoBarSeverity.Error, "Could not open " + entry.Name, ex.Message);
          return;
        }
        finally
        {
          entry.IsRealizing = false;
        }
      }

      // The children have to be there before HasUnrealizedChildren goes false,
      // otherwise the tree sees an empty node and collapses it again.
      if (entry.Folder != null)
      {
        Populate(node, entry.Folder);
      }
      node.HasUnrealizedChildren = false;

      // An archive was opened in the background; the expand that started it has been undone by now.
      if (entry.IsArchive && node.Children.Count > 0)
      {
        node.IsExpanded = true;
      }
    }

    private static void Populate(TreeViewNode node, Location folder)
    {
      node.Children.Clear();
      var entries = folder.Directory.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();

      // Folders first, then the archives that can be opened like folders.
      foreach (var o in entries)
      {
        if (o is Directory d)
        {
          node.Children.Add(CreateNode(TreeEntry.ForFolder(new Location(folder.Volume, d))));
        }
      }
      foreach (var o in entries)
      {
        if (o is File f && Volumes.IsArchiveName(f.Name))
        {
          node.Children.Add(CreateNode(TreeEntry.ForArchive(folder, f)));
        }
      }
    }

    private async void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
      if (args.InvokedItem is not TreeViewNode node)
      {
        return;
      }

      var entry = (TreeEntry)node.Content;
      if (entry.Folder == null)
      {
        await RealizeAsync(node);
      }
      if (entry.Folder != null)
      {
        Navigate(entry.Folder);
      }
    }

    /// <summary>Expands the tree down to the current folder and selects it.</summary>
    private async Task SyncTreeAsync(Location target)
    {
      if (FolderTree.RootNodes.Count == 0)
      {
        return;
      }

      List<Location> path = target.Path();
      TreeViewNode node = FolderTree.RootNodes[0];
      if (((TreeEntry)node.Content).Folder?.Equals(path[0]) != true)
      {
        return;
      }

      for (int i = 1; i < path.Count; i++)
      {
        if (node.HasUnrealizedChildren)
        {
          await RealizeAsync(node);
        }
        node.IsExpanded = true;

        TreeViewNode? next = node.Children.FirstOrDefault(c => Matches((TreeEntry)c.Content, path[i]));
        if (next == null)
        {
          break;
        }
        node = next;
      }

      if (target.Equals(_current))
      {
        FolderTree.SelectedNode = node;
      }
    }

    private static bool Matches(TreeEntry entry, Location step)
    {
      if (entry.IsArchive)
      {
        if (step.IsVolumeRoot && ReferenceEquals(step.Volume.HostFile, entry.ArchiveFile))
        {
          // Opened from the list: the tree node now knows the archive's root as well.
          entry.Folder ??= step;
          return true;
        }
        return false;
      }
      return entry.Folder != null && entry.Folder.Equals(step);
    }

    #endregion

    #region File list

    private void RefreshList()
    {
      Welcome.Visibility = _current == null ? Visibility.Visible : Visibility.Collapsed;

      if (_current == null)
      {
        _groups = new List<EntryGroup>();
      }
      else
      {
        string filter = FilterBox.Text ?? "";
        var groups = _current.Directory
          .Where(o => filter.Length == 0 || o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
          .Select(o => new EntryItem(o, IsUnsaved(o)))
          .GroupBy(i => i.Type.Group)
          .Select(g => new EntryGroup(g.Key, g.First().Type.GroupOrder, Sort(g)));

        // Most groups of an archive hold a single type, so sorting by type also orders the groups: by the
        // name of their first type, folders always first. Otherwise the groups keep their usual order.
        if (_sortKey == "Type")
        {
          var byType = groups.OrderBy(g => g.Order == 0 ? 0 : 1);
          groups = _sortDescending
            ? byType.ThenByDescending(g => g[0].TypeName, StringComparer.OrdinalIgnoreCase)
            : byType.ThenBy(g => g[0].TypeName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
          groups = groups.OrderBy(g => g.Order);
        }
        _groups = groups.ToList();
      }

      GroupedEntries.Source = _groups;
      FileList.ItemsSource = GroupedEntries.View;
      UpdateStatus();
    }

    private IEnumerable<EntryItem> Sort(IEnumerable<EntryItem> items)
    {
      IOrderedEnumerable<EntryItem> sorted = _sortKey switch
      {
        "Type" => OrderBy(items, i => i.TypeName),
        "Resource" => OrderBy(items, i => i.Resource),
        "Size" => _sortDescending ? items.OrderByDescending(i => i.Size) : items.OrderBy(i => i.Size),
        _ => OrderBy(items, i => i.Name),
      };
      return sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
    }

    private IOrderedEnumerable<EntryItem> OrderBy(IEnumerable<EntryItem> items, Func<EntryItem, string> key) =>
      _sortDescending ? items.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : items.OrderBy(key, StringComparer.OrdinalIgnoreCase);

    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
      string key = (string)((Button)sender).Tag;
      _sortDescending = key == _sortKey && !_sortDescending;
      _sortKey = key;
      UpdateSortHeaders();
      RefreshList();
    }

    private void UpdateSortHeaders()
    {
      foreach (var button in new[] { SortName, SortType, SortSize, SortResource })
      {
        string key = (string)button.Tag;
        button.Content = key == _sortKey ? key + (_sortDescending ? "  \u2193" : "  \u2191") : key;
      }
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
      if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
      {
        RefreshList();
      }
    }

    private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
      if ((e.OriginalSource as FrameworkElement)?.DataContext is EntryItem item)
      {
        e.Handled = true;
        _pointerStillDown = true;
        await ActivateAsync(item);
      }
    }

    // A double-click is reported on the second press. When the button goes up, the list focuses the
    // item, which brings the main window back to the front; so a viewer window opens only after that.
    private bool _pointerStillDown;
    private Action? _pendingViewer;

    private void OpenViewerWhenInputDone(Action open)
    {
      if (!_pointerStillDown)
      {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => open());
        return;
      }

      _pendingViewer = open;

      // In case the button goes up somewhere the list doesn't see it.
      var timer = DispatcherQueue.CreateTimer();
      timer.Interval = TimeSpan.FromMilliseconds(500);
      timer.IsRepeating = false;
      timer.Tick += (sender, args) => OpenPendingViewer();
      timer.Start();
    }

    private void FileList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
      _pointerStillDown = false;
      if (_pendingViewer != null)
      {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, OpenPendingViewer);
      }
    }

    private void OpenPendingViewer()
    {
      _pointerStillDown = false;
      Action? open = _pendingViewer;
      _pendingViewer = null;
      open?.Invoke();
    }

    private async void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
      if (e.Key == VirtualKey.Enter && FileList.SelectedItems.Count == 1 && FileList.SelectedItem is EntryItem item)
      {
        e.Handled = true;
        await ActivateAsync(item);
      }
      else if (e.Key == VirtualKey.Back)
      {
        e.Handled = true;
        GoUp();
      }
    }

    private async void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
      if (FileList.SelectedItem is EntryItem item)
      {
        await ActivateAsync(item);
      }
    }

    private void ContextOpenBinary_Click(object sender, RoutedEventArgs e)
    {
      if (FileList.SelectedItem is EntryItem item && item.File != null)
      {
        OpenViewer(item.File, Viewers.ViewerRegistry.Binary);
      }
    }

    #region Edit externally

    private ExternalEdits? _externalEdits;

    private ExternalEdits ExternalEdits
    {
      get
      {
        if (_externalEdits == null)
        {
          _externalEdits = new ExternalEdits(DispatcherQueue);
          _externalEdits.Changed += ExternalEdit_Changed;
        }
        return _externalEdits;
      }
    }

    /// <summary>Opens the file in the program Windows has for it; when it is saved there, SparkIV offers to take it.</summary>
    private void ContextEditExternally_Click(object sender, RoutedEventArgs e)
    {
      if (_current == null || FileList.SelectedItem is not EntryItem item || item.File == null)
      {
        return;
      }
      try
      {
        ExternalEdits.Open(item.File, _current.Volume);
        ShowMessage(InfoBarSeverity.Informational, item.Name + " opened in another program",
                    "Save it there; SparkIV then offers to take the change (edit mode needed). The game files are only written with Save.");
      }
      catch (Exception ex)
      {
        ShowMessage(InfoBarSeverity.Error, "Could not open " + item.Name, ex.Message);
      }
    }

    private void ExternalEdit_Changed(ExternalEdits.Edit edit)
    {
      var pending = ExternalEdits.Pending;
      string names = string.Join(", ", pending.Select(p => p.File.Name));
      ShowMessage(InfoBarSeverity.Informational,
                  (pending.Count == 1 ? names + " was" : pending.Count + " files were") + " changed in another program",
                  (pending.Count == 1 ? "" : names + ". ") + "Take the change into SparkIV? It is written into the game files with Save.",
                  "Apply", async () => await ApplyExternalEditsAsync());
    }

    private async Task ApplyExternalEditsAsync()
    {
      if (!await EditMode.TurnOnAsync(Root.XamlRoot))
      {
        // Still pending: offered again when the file is saved again, or apply after turning edit mode on.
        ShowMessage(InfoBarSeverity.Warning, "Not taken", "Changes are only taken in edit mode.", "Apply", async () => await ApplyExternalEditsAsync());
        return;
      }
      UpdateEditModeButton();

      var applied = new List<string>();
      var locked = new List<string>();
      foreach (var edit in ExternalEdits.Pending)
      {
        if (ExternalEdits.Apply(edit))
        {
          _modified.Add(edit.Volume);
          FileChanges.Raise(edit.File);
          applied.Add(edit.File.Name);
        }
        else
        {
          locked.Add(edit.File.Name);
        }
      }
      RefreshList();
      UpdateCommands();
      if (locked.Count > 0)
      {
        ShowMessage(InfoBarSeverity.Warning, "Still in use: " + string.Join(", ", locked),
                    "The other program still has it open for writing. Try again in a moment.", "Apply", async () => await ApplyExternalEditsAsync());
      }
      else
      {
        ShowMessage(InfoBarSeverity.Success, string.Join(", ", applied) + " changed", "Use Save to write the change into the game files.");
      }
    }

    #endregion

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      UpdateStatus();
      UpdateCommands();
    }

    private void UpdateStatus()
    {
      if (_current == null)
      {
        StatusItems.Text = "";
        StatusSelection.Text = "";
        return;
      }

      int count = _groups.Sum(g => g.Count);
      StatusItems.Text = count == 1 ? "1 item" : count + " items";

      var selected = FileList.SelectedItems.OfType<EntryItem>().ToList();
      if (selected.Count == 0)
      {
        StatusSelection.Text = "";
      }
      else
      {
        string text = selected.Count == 1 ? "1 item selected" : selected.Count + " items selected";
        if (selected.All(i => i.File != null))
        {
          text += "   " + EntryItem.FriendlySize(selected.Sum(i => (long)i.File!.Size));
        }
        StatusSelection.Text = text;
      }
    }

    #endregion

    #region Export, import, save

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
      var selected = FileList.SelectedItems.OfType<EntryItem>().Select(i => i.Entry).ToList();
      if (selected.Count == 0)
      {
        return;
      }
      await ExportAsync(selected);
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
      if (_current != null)
      {
        await ExportAsync(_current.Directory.ToList());
      }
    }

    private async Task ExportAsync(List<FSObject> entries)
    {
      string? folder = await PickFolderAsync();
      if (folder == null)
      {
        return;
      }

      await RunBusyAsync(async () =>
      {
        int count = await Task.Run(() => entries.Sum(entry => ExportEntry(entry, folder)));
        ShowMessage(InfoBarSeverity.Success, "Exported", (count == 1 ? "1 file" : count + " files") + " exported to " + folder + ".");
      });
    }

    /// <summary>Writes a file, or a folder with everything in it. Returns the number of files.</summary>
    private static int ExportEntry(FSObject entry, string folder)
    {
      if (entry is File file)
      {
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, file.Name), file.GetData());
        return 1;
      }

      var dir = (Directory)entry;
      string path = System.IO.Path.Combine(folder, dir.Name);
      System.IO.Directory.CreateDirectory(path);
      int count = 0;
      foreach (var child in dir)
      {
        count += ExportEntry(child, path);
      }
      return count;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
      if (_current == null)
      {
        return;
      }

      var picker = new FileOpenPicker();
      picker.FileTypeFilter.Add("*");
      InitializeWithWindow(picker);
      var files = await picker.PickMultipleFilesAsync();
      if (files == null || files.Count == 0)
      {
        return;
      }
      await ImportAsync(files.Select(f => f.Path).ToList());
    }

    #region Drag and drop

    // Files dragged from Explorer onto the list are imported into the folder shown, like Import.
    private void FileList_DragOver(object sender, DragEventArgs e)
    {
      if (_current != null && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
      {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = EditMode.IsOn ? "Import into " + _current.Name : "Import (turns on edit mode)";
      }
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
      if (_current == null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
      {
        return;
      }
      var items = await e.DataView.GetStorageItemsAsync();
      var paths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
      if (paths.Count == 0)
      {
        ShowMessage(InfoBarSeverity.Warning, "Nothing imported", "Only files can be imported, not folders.");
        return;
      }
      if (!await EditMode.TurnOnAsync(Root.XamlRoot))
      {
        return;
      }
      UpdateEditModeButton();
      await ImportAsync(paths);
    }

    #endregion

    /// <summary>Replaces files of the folder shown with files of the same name (written with Save).</summary>
    private async Task ImportAsync(IReadOnlyList<string> paths)
    {
      if (_current == null)
      {
        return;
      }
      Location location = _current;
      var missing = new List<string>();
      await RunBusyAsync(async () =>
      {
        foreach (var path in paths)
        {
          string name = Path.GetFileName(path);
          // Only replacing is supported: the file must already be there with the same name.
          if (location.Directory.FindByName(name) is File target)
          {
            target.SetData(await System.IO.File.ReadAllBytesAsync(path));
            FileChanges.Raise(target);
            _modified.Add(location.Volume);
          }
          else
          {
            missing.Add(name);
          }
        }
      });
      int count = paths.Count;

      RefreshList();
      UpdateCommands();

      int replaced = count - missing.Count;
      if (missing.Count > 0)
      {
        ShowMessage(InfoBarSeverity.Warning, replaced + " of " + count + " files replaced",
                    "Not in this folder (only files with the same name can be replaced): " + string.Join(", ", missing));
      }
      else
      {
        ShowMessage(InfoBarSeverity.Success, "Imported", replaced + " files replaced. Use Save to write them.");
      }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
      if (_current == null || !_modified.Contains(_current.Volume) || !EditMode.IsOn)
      {
        return;
      }

      Volume volume = _current.Volume;
      var chain = Saving.Chain(volume);
      // An archive in an archive is saved into the one around it, and so on up to the file on disk.
      string around = chain.Count > 1 ? " It goes into " + string.Join(", then ", chain.Skip(1).Select(v => v.Name)) + ", which is saved too." : "";
      // As the old SparkIV asked: a write-protected file can only be saved without its protection.
      var readOnly = FileInstaller.ReadOnly(Saving.Targets(volume));
      string protection = readOnly.Count > 0
        ? "\n\n" + string.Join(", ", readOnly.Select(Path.GetFileName)) + (readOnly.Count == 1 ? " is" : " are") +
          " write-protected. Saving takes the write protection off."
        : "";
      var dialog = new ContentDialog
      {
        XamlRoot = Root.XamlRoot,
        Title = "Save " + volume.Name + "?",
        Content = "This writes the changes into the game files." + around + " Before a file is replaced the first time, " +
                  "its original is kept in " + Backups.Folder + "." +
                  (_shield ? "\n\nThe game is in a protected folder: Windows asks for administrator rights to write it." : "") +
                  protection,
        PrimaryButtonText = "Save",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Close,
      };
      if (await dialog.ShowAsync() != ContentDialogResult.Primary)
      {
        return;
      }

      var saved = new List<Volume>();
      await RunBusyAsync(async () =>
      {
        try
        {
          await Task.Run(() => Saving.Save(volume, _volumes, saved));
          ShowMessage(InfoBarSeverity.Success, "Saved", volume.Name + " has been saved. The originals are in " + Backups.Folder + ".");
        }
        catch (OperationCanceledException ex)
        {
          ShowMessage(InfoBarSeverity.Warning, "Not saved", ex.Message);
        }
        finally
        {
          foreach (var v in saved)
          {
            _modified.Remove(v);
          }
          // Saved into the archive around it, which couldn't be saved itself: its changes are still to save.
          if (saved.Count > 0 && saved.Count < chain.Count)
          {
            _modified.Add(chain[saved.Count]);
          }
        }
      });
      RefreshList();
      UpdateCommands();
    }

    #endregion

    #region Helpers

    private void ApplyCaptionButtonColors() => TitleBarColors.Apply(this, Root);

    private void UpdateCommands()
    {
      bool open = _current != null;
      BackButton.IsEnabled = _back.Count > 0;
      ForwardButton.IsEnabled = _forward.Count > 0;
      UpButton.IsEnabled = _current?.Parent != null;
      FilterBox.IsEnabled = open;
      ExportButton.IsEnabled = open && FileList.SelectedItems.Count > 0;
      ExportAllButton.IsEnabled = open;
      // Changing files only in edit mode.
      ImportButton.IsEnabled = open && EditMode.IsOn;
      SaveButton.IsEnabled = _current != null && _modified.Contains(_current.Volume) && EditMode.IsOn;
      UpdateSaveShield();
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
      Busy.IsActive = true;
      try
      {
        await work();
      }
      catch (Exception ex)
      {
        ShowMessage(InfoBarSeverity.Error, "Something went wrong", ex.Message);
      }
      finally
      {
        Busy.IsActive = false;
      }
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message, string? actionLabel = null, Action? action = null)
    {
      Info.Severity = severity;
      Info.Title = title;
      Info.Message = message;
      Info.ActionButton = null;
      if (actionLabel != null && action != null)
      {
        var button = new Button { Content = actionLabel };
        button.Click += (sender, args) =>
        {
          Info.IsOpen = false;
          action();
        };
        Info.ActionButton = button;
      }
      Info.IsOpen = true;
    }

    private async Task<string?> PickFolderAsync()
    {
      var picker = new FolderPicker();
      picker.FileTypeFilter.Add("*");
      InitializeWithWindow(picker);
      var folder = await picker.PickSingleFolderAsync();
      return folder?.Path;
    }

    /// <summary>Pickers in a desktop app need to know their owner window.</summary>
    private void InitializeWithWindow(object picker)
    {
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    #endregion
  }
}
