using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A model (.wdr), model dictionary (.wdd) or fragment (.wft) in 3D, with the tree of its parts to show
  /// and hide. The scene is drawn by ModelView.html with WebGL; its textures are served from memory.
  /// </summary>
  public sealed partial class ModelView : UserControl, IDisposable
  {
    private const string Host = "https://model.sparkiv/";

    // Remembered while SparkIV runs.
    private static int s_background;
    private static bool s_textures = true;
    private static bool s_wireframe;

    private readonly ViewerContext _context;
    private readonly ModelScene _scene;
    private bool _started;
    private bool _pageReady;

    public ModelView(ViewerContext context, ModelScene scene)
    {
      InitializeComponent();
      _context = context;
      _scene = scene;

      PartTree.ItemsSource = new[] { scene.Root };
      scene.Root.CheckedChanged = () =>
      {
        // A box changed by hand ends the stepping.
        _step = -1;
        SendVisible(fit: false);
        UpdateStatus();
        UpdateStepText();
      };

      // The levels of detail there are in this model; high is shown first.
      string[] lodNames = { "High", "Medium", "Low", "Very low" };
      int levels = Math.Min(lodNames.Length, scene.Root.MaxLevelsOfDetail());
      for (int i = 0; i < levels; i++)
      {
        LodBox.Items.Add(lodNames[i] + " (LOD " + i + ")");
      }
      LodBox.SelectedIndex = levels > 0 ? 0 : -1;
      LodContainer.Visibility = levels > 1 ? Visibility.Visible : Visibility.Collapsed;
      _lodReady = true;

      ShowEmbeddedTextures();
      // A texture replaced in the model or in a dictionary it takes textures from: shown at once.
      Services.FileChanges.Changed += FileChanges_Changed;

      BackgroundBox.SelectedIndex = s_background;
      TexturesButton.IsChecked = s_textures;
      WireframeButton.IsChecked = s_wireframe;
      UpdateStatus();
      UpdateStepText();
      Loaded += ModelView_Loaded;
    }

    #region Textures

    /// <summary>
    /// The Textures tab, only for a model with textures of its own (vehicles and peds have theirs in .wtd
    /// files; map objects and others often in the model): those textures, also the unused ones.
    /// </summary>
    private void ShowEmbeddedTextures()
    {
      var embedded = _scene.TextureList.Where(t => t.IsEmbedded).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
      PaneTabs.Visibility = embedded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
      if (embedded.Count == 0)
      {
        PaneTabs.SelectedItem = PartsTab;
      }
      EmbeddedList.ItemsSource = embedded.Select(t => new Model.ModelTextureRow(t.Name, t.Detail, t.IsUsed, t.Thumbnail?.ToBitmap())).ToList();
      int used = embedded.Count(t => t.IsUsed);
      EmbeddedCountText.Text = embedded.Count + (embedded.Count == 1 ? " texture" : " textures") + " stored in the model" +
                               (used < embedded.Count ? ", " + used + " used" : "");
    }

    private void PaneTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
      bool textures = sender.SelectedItem == TexturesTab;
      TexturesPanel.Visibility = textures ? Visibility.Visible : Visibility.Collapsed;
      PartsPanel.Visibility = textures ? Visibility.Collapsed : Visibility.Visible;
    }

    // Raised up when the textures are made again, so the page doesn't take the old pictures from its cache.
    private int _textureVersion;

    private string? TextureUrl(SceneMaterial m) => m.TextureKey == null ? null : Host + m.TextureKey + "?v=" + _textureVersion;

    private bool _reloading;
    private bool _reloadAgain;

    private async void FileChanges_Changed(File file)
    {
      if (!_scene.SourceFiles.Contains(file))
      {
        return;
      }
      // One reload at a time; a change during it reloads once more after.
      if (_reloading)
      {
        _reloadAgain = true;
        return;
      }
      _reloading = true;
      try
      {
        do
        {
          _reloadAgain = false;
          var again = await System.Threading.Tasks.Task.Run(() => ModelScene.Load(_context.File, _context.File.GetData()));
          if (!_scene.TakeTexturesFrom(again))
          {
            _context.ShowMessage("Textures not updated", "The model changed too much; open it again to see it.", true);
            return;
          }
          _textureVersion++;
          Post(new { type = "materials", materials = _scene.Materials.Select(m => new { id = m.Id, glass = m.Glass, texture = TextureUrl(m) }) });
          ShowEmbeddedTextures();
          UpdateStatus();
        }
        while (_reloadAgain);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Textures not updated", ex.Message, true);
      }
      finally
      {
        _reloading = false;
      }
    }

    #endregion

    /// <summary>The textures in the model, in the texture viewer; replaced ones go into the model (where it can be changed).</summary>
    private void EmbeddedTextures_Click(object sender, RoutedEventArgs e)
    {
      var window = new ViewerWindow(_context.File, new ModelTextureViewer(), _context.File.Name, _context.ReplaceOf);
      window.Activate();
      window.BringToFront();
    }

    private async void ModelView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;

      if (!await ViewerPages.StartAsync(Web, null, _context, Core_WebMessageReceived, allowCopyMenu: false))
      {
        Working.IsActive = false;
        return;
      }

      // The page comes from the same made-up host as the textures, so WebGL may use them.
      var core = Web.CoreWebView2;
      core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
      core.WebResourceRequested += Core_WebResourceRequested;
      core.NavigationStarting += (s, args) =>
      {
        if (!args.Uri.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
        {
          args.Cancel = true;
        }
      };
      core.Navigate(Host + "index.html");
    }

    private void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
      string name = new Uri(args.Request.Uri).AbsolutePath.TrimStart('/');
      byte[]? body = null;
      string type = "image/png";
      if (name == "index.html")
      {
        using var page = typeof(ModelView).Assembly.GetManifestResourceStream("SparkIV.ModelView.html")!;
        using var copy = new MemoryStream();
        page.CopyTo(copy);
        body = copy.ToArray();
        type = "text/html; charset=utf-8";
      }
      else
      {
        _scene.Textures.TryGetValue(name, out body);
      }

      args.Response = sender.Environment.CreateWebResourceResponse(new MemoryStream(body ?? Array.Empty<byte>()).AsRandomAccessStream(),
                                                                   body != null ? 200 : 404, body != null ? "OK" : "Not Found",
                                                                   "Content-Type: " + type);
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
      using var message = JsonDocument.Parse(args.WebMessageAsJson);
      var root = message.RootElement;
      if (root.GetProperty("type").GetString() != "ready")
      {
        return;
      }

      _pageReady = true;
      Working.IsActive = false;
      if (!root.GetProperty("webgl").GetBoolean())
      {
        return;
      }

      Post(new
      {
        type = "scene",
        materials = _scene.Materials.Select(m => new { id = m.Id, glass = m.Glass, texture = TextureUrl(m) }),
        meshes = _scene.Meshes.Select(m => m.ToMessage()),
      });
      SendOptions();
      SendBackground();
      SendVisible(fit: true);
    }

    private void Post(object message)
    {
      if (_pageReady)
      {
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
      }
    }

    private void SendVisible(bool fit) => Post(new { type = "visible", ids = _scene.Root.CheckedMeshes().ToArray(), fit });

    private void SendOptions() => Post(new { type = "options", textures = TexturesButton.IsChecked == true, wireframe = WireframeButton.IsChecked == true });

    private static readonly string[] BackgroundNames = { "", "grey", "black", "white" };

    private void SendBackground() => Post(new { type = "background", name = BackgroundNames[Math.Clamp(BackgroundBox.SelectedIndex, 0, BackgroundNames.Length - 1)] });

    private void UpdateStatus()
    {
      var shown = _scene.Root.CheckedMeshes().Select(id => _scene.Meshes[id]).ToList();
      int triangles = shown.Sum(m => m.TriangleCount);
      int vertices = shown.Sum(m => m.Positions.Length / 3);
      string text = shown.Count + (shown.Count == 1 ? " mesh" : " meshes") + " shown   \u00B7   " + triangles.ToString("N0") + " triangles   \u00B7   " +
                    vertices.ToString("N0") + " vertices   \u00B7   " + _scene.Textures.Count + (_scene.Textures.Count == 1 ? " texture" : " textures");
      // Where the textures the materials use come from: the model itself, dictionaries outside it.
      var used = _scene.TextureList.Where(t => t.IsUsed && t.Source != ModelScene.MissingSource).ToList();
      var external = used.Where(t => !t.IsEmbedded).Select(t => t.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
      if (used.Any(t => t.IsEmbedded))
      {
        external.Insert(0, "the model");
      }
      if (external.Count > 0)
      {
        text += " from " + string.Join(", ", external);
      }
      if (_scene.MissingTextures.Count > 0)
      {
        text += "   \u00B7   not found: " + string.Join(", ", _scene.MissingTextures);
      }
      Status.Text = text;
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
      s_textures = TexturesButton.IsChecked == true;
      s_wireframe = WireframeButton.IsChecked == true;
      SendOptions();
    }

    #region Show all, none, one after the other

    // The models stepped through: the top nodes of the tree (a dictionary's entries; a fragment's body,
    // wheels and parts). -1 while not stepping.
    private int _step = -1;

    private IReadOnlyList<Model.ModelPart> Items => _scene.Root.Children;

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
      _step = -1;
      _scene.Root.Initialize(true);
      ApplyChoice(fit: true);
    }

    private void ShowNone_Click(object sender, RoutedEventArgs e)
    {
      _step = -1;
      _scene.Root.Initialize(false);
      ApplyChoice(fit: false);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(+1);

    /// <summary>Shows only the next (or previous) model, and looks at it.</summary>
    private void Step(int direction)
    {
      if (Items.Count == 0)
      {
        return;
      }
      // Not stepping yet: from the first model that is shown (or before the first if none is).
      int from = _step >= 0 ? _step : Items.ToList().FindIndex(i => i.IsChecked != false);
      if (from < 0)
      {
        from = direction > 0 ? -1 : 0;
      }
      _step = (from + direction + Items.Count) % Items.Count;
      _scene.Root.Initialize(false);
      Items[_step].Initialize(true);
      ApplyChoice(fit: true);
    }

    /// <summary>After showing or hiding: one level of detail, the boxes above, the view and the status.</summary>
    private void ApplyChoice(bool fit)
    {
      _scene.Root.SettleParents();
      _scene.Root.ShowLevelOfDetail(Math.Max(0, LodBox.SelectedIndex));
      SendVisible(fit);
      UpdateStatus();
      UpdateStepText();
    }

    private void UpdateStepText()
    {
      StepText.Text = _step >= 0 ? (_step + 1) + " / " + Items.Count : Items.Count + (Items.Count == 1 ? " model" : " models");
      PreviousButton.IsEnabled = NextButton.IsEnabled = Items.Count > 1;
    }

    #endregion

    private bool _lodReady;

    private void LodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_lodReady || LodBox.SelectedIndex < 0)
      {
        return;
      }
      _scene.Root.ShowLevelOfDetail(LodBox.SelectedIndex);
      SendVisible(fit: false);
      UpdateStatus();
    }

    private void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      s_background = Math.Max(0, BackgroundBox.SelectedIndex);
      SendBackground();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Post(new { type = "fit" });

    private async void ExportModel_Click(object sender, RoutedEventArgs e)
    {
      var shown = _scene.Root.CheckedMeshes().ToList();
      if (shown.Count == 0)
      {
        _context.ShowMessage("Nothing to export", "Tick the parts to export; what is shown is exported.", true);
        return;
      }

      var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(_context.File.Name) };
      picker.FileTypeChoices.Add("Wavefront OBJ (Blender, 3ds Max ...)", new List<string> { ".obj" });
      picker.FileTypeChoices.Add("Valve Studiomdl SMD (with skeleton)", new List<string> { ".smd" });
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_context.Window));
      var file = await picker.PickSaveFileAsync();
      if (file == null)
      {
        return;
      }

      Working.IsActive = true;
      try
      {
        string path = file.Path;
        int textures = await System.Threading.Tasks.Task.Run(() => ModelExport.Write(_scene, shown, path));
        _context.ShowMessage("Exported", path + (textures > 0 ? " and " + textures + (textures == 1 ? " texture" : " textures") + " as PNG next to it" : "") + ".", false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the model", ex.Message, true);
      }
      finally
      {
        Working.IsActive = false;
      }
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _context.File.Name;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    public void Dispose()
    {
      Services.FileChanges.Changed -= FileChanges_Changed;
      if (Web.CoreWebView2 != null)
      {
        Web.CoreWebView2.WebResourceRequested -= Core_WebResourceRequested;
      }
      Web.Close();
    }
  }

  /// <summary>Models (.wdr), model dictionaries (.wdd) and fragments (.wft).</summary>
  internal sealed class ModelViewer : IViewer
  {
    public object Load(File file, byte[] data) => ModelScene.Load(file, data);

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new ModelView(context, (ModelScene)loaded);
  }

  /// <summary>
  /// The textures stored in a model (.wdr), model dictionary (.wdd, each entry may have some) or fragment
  /// (.wft), in the texture viewer. Replaced textures are written into the model's resource, as for web pages.
  /// </summary>
  internal sealed class ModelTextureViewer : IViewer
  {
    public object Load(File file, byte[] data)
    {
      var textures = new List<RageLib.Textures.TextureFile>();
      string extension = Path.GetExtension(file.Name).ToLowerInvariant();
      if (extension == ".wft")
      {
        var fragment = new RageLib.Models.ModelFragTypeFile();
        fragment.Open(new MemoryStream(data));
        textures.Add(fragment.EmbeddedTextureFile);
      }
      else if (extension == ".wdd")
      {
        var dictionary = new RageLib.Models.ModelDictionaryFile();
        dictionary.Open(new MemoryStream(data));
        textures.AddRange(dictionary.EmbeddedTextureFiles);
      }
      else
      {
        var model = new RageLib.Models.ModelFile();
        model.Open(new MemoryStream(data));
        textures.Add(model.EmbeddedTextureFile);
      }
      textures.RemoveAll(t => t == null || t.Count == 0);
      if (textures.Count == 0)
      {
        throw new InvalidDataException("This model has no textures of its own.");
      }
      return new TextureDictionary(textures) { Embedded = true };
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new TextureView(context, (TextureDictionary)loaded);
  }
}
