using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using RageLib.FileSystem;
using RageLib.HyperText;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// An in-game web page (.whm), shown like in a browser. Its links lead to the other pages in the game
  /// files, so one can browse the game's internet; pages and images are served from memory.
  /// </summary>
  public sealed partial class WebPageView : UserControl, IDisposable
  {
    private const string Host = "https://whm.sparkiv/";

    // The home page of the game's browser.
    private const string HomePage = "www.eyefind.info/index.html";

    private readonly ViewerContext _context;
    private readonly WebSite _site;
    private readonly Dictionary<string, WebPage> _pages = new Dictionary<string, WebPage>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _sharedImages = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
    private WebPage? _current;
    private bool _started;

    public WebPageView(ViewerContext context, WebSite site, WebPage first)
    {
      InitializeComponent();
      _context = context;
      _site = site;
      _pages[first.Path] = first;
      Loaded += WebPageView_Loaded;

      // Only the languages this game has; with one there is nothing to choose.
      _languages = site.Languages;
      foreach (var language in _languages)
      {
        LanguageBox.Items.Add(language.Name);
      }
      LanguageBox.SelectedIndex = _languages.ToList().FindIndex(l => l.File == site.Language);
      LanguageContainer.Visibility = _languages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
      _languagesReady = true;
    }

    private readonly IReadOnlyList<(string File, string Name)> _languages;
    private bool _languagesReady;

    /// <summary>Puts the game texts of all loaded pages into the chosen language and shows the page again.</summary>
    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!_languagesReady || LanguageBox.SelectedIndex < 0)
      {
        return;
      }
      string file = _languages[LanguageBox.SelectedIndex].File;
      if (file == _site.Language)
      {
        return;
      }

      WebPageViewer.s_language = file;
      Working.IsActive = true;
      LanguageBox.IsEnabled = false;
      try
      {
        var pages = _pages.Values.ToList();
        await Task.Run(() =>
        {
          _site.SetLanguage(file);
          foreach (var page in pages)
          {
            page.Retranslate(_site);
          }
        });
        Web.CoreWebView2?.Reload();
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not change the language", ex.Message, true);
      }
      finally
      {
        LanguageBox.IsEnabled = true;
        Working.IsActive = false;
      }
    }

    private async void WebPageView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;
      _context.SetTitle("GTA IV Browser", "");

      if (!await ViewerPages.StartAsync(Web, null, _context, (s, a) => { }, allowCopyMenu: true))
      {
        Working.IsActive = false;
        return;
      }

      var core = Web.CoreWebView2;
      // Back and forward with Alt+arrows or the mouse buttons, Ctrl+F to find, Ctrl+wheel to zoom.
      core.Settings.AreBrowserAcceleratorKeysEnabled = true;
      core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
      core.WebResourceRequested += Core_WebResourceRequested;
      core.NavigationStarting += Core_NavigationStarting;
      core.NavigationCompleted += Core_NavigationCompleted;
      core.Navigate(Host + _site.StartPath);
    }

    #region Serving the pages

    // Only the game's pages are shown; there is no way out to the real internet.
    private static void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
      if (!args.Uri.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
      {
        args.Cancel = true;
      }
    }

    private async void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
      string path = PathOf(args.Request.Uri);
      using var deferral = args.GetDeferral();
      try
      {
        // An image of a page that is already loaded.
        foreach (var page in _pages.Values)
        {
          if (page.Images.TryGetValue(path, out byte[]? png))
          {
            args.Response = Respond(sender, png, 200, "image/png");
            return;
          }
        }

        // An image from one of the game's texture dictionaries.
        if (path.StartsWith(WebPage.SharedImages + "/", StringComparison.OrdinalIgnoreCase))
        {
          if (!_sharedImages.TryGetValue(path, out byte[]? shared))
          {
            string[] parts = path.Split('/');
            shared = parts.Length == 3 ? await Task.Run(() => _site.LoadSharedImage(parts[1], Path.GetFileNameWithoutExtension(parts[2]))) : null;
            _sharedImages[path] = shared;
          }
          args.Response = shared != null ? Respond(sender, shared, 200, "image/png") : Respond(sender, Array.Empty<byte>(), 404, "text/plain");
          return;
        }

        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
          if (!_pages.TryGetValue(path, out var page))
          {
            File? file = _site.Find(path);
            if (file == null)
            {
              args.Response = Respond(sender, NotFound(path), 404, "text/html; charset=utf-8");
              return;
            }
            page = await Task.Run(() => WebPage.Load(_site, file, path));
            _pages[path] = page;
          }
          args.Response = Respond(sender, Encoding.UTF8.GetBytes(page.Html), 200, "text/html; charset=utf-8");
          return;
        }

        args.Response = Respond(sender, Array.Empty<byte>(), 404, "text/plain");
      }
      catch (Exception ex)
      {
        args.Response = Respond(sender, Encoding.UTF8.GetBytes("<p>Could not show " + Escape(path) + ": " + Escape(ex.Message) + "</p>"),
                                500, "text/html; charset=utf-8");
      }
      finally
      {
        deferral.Complete();
      }
    }

    private static CoreWebView2WebResourceResponse Respond(CoreWebView2 core, byte[] body, int status, string type) =>
      core.Environment.CreateWebResourceResponse(new MemoryStream(body).AsRandomAccessStream(), status,
                                                 status == 200 ? "OK" : status == 404 ? "Not Found" : "Error",
                                                 "Content-Type: " + type);

    private static byte[] NotFound(string path) => Encoding.UTF8.GetBytes(
      "<html><body style=\"font-family: Segoe UI, sans-serif; color: #444; margin: 32px\"><h3>Not in the game files</h3><p>" +
      Escape(path) + " doesn't exist; the page links to a page the game doesn't have.</p></body></html>");

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    /// <summary>"https://whm.sparkiv/www.burgershot.net/menu.html" → "www.burgershot.net/menu.html"</summary>
    private static string PathOf(string uri)
    {
      string path = new Uri(uri).AbsolutePath.TrimStart('/');
      return Uri.UnescapeDataString(path);
    }

    private void Core_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
      Working.IsActive = false;
      string path = PathOf(sender.Source);
      _pages.TryGetValue(path, out _current);
      AddressBox.Text = path;
      // Next to "GTA IV Browser": the title of the page, like a browser tab.
      _context.SetTitle("GTA IV Browser", sender.DocumentTitle);
      BackButton.IsEnabled = sender.CanGoBack;
      ForwardButton.IsEnabled = sender.CanGoForward;
      TexturesButton.IsEnabled = _current != null && _current.TextureCount > 0;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoForward();

    // Eyefind, like in the game; where it isn't in the files (a single page out of its folders), the page that was opened.
    private void Home_Click(object sender, RoutedEventArgs e) =>
      Web.CoreWebView2?.Navigate(Host + (_site.Find(HomePage) != null ? HomePage : _site.StartPath));

    #endregion

    #region Textures and export

    private void Textures_Click(object sender, RoutedEventArgs e)
    {
      if (_current == null)
      {
        return;
      }
      var window = new ViewerWindow(_current.File, new WebPageTextureViewer(), _current.Path);
      window.Activate();
      window.BringToFront();
    }

    private async void ExportHtml_Click(object sender, RoutedEventArgs e)
    {
      if (_current == null)
      {
        return;
      }

      var picker = new Windows.Storage.Pickers.FolderPicker();
      picker.FileTypeFilter.Add("*");
      WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_context.Window));
      var folder = await picker.PickSingleFolderAsync();
      if (folder == null)
      {
        return;
      }

      var page = _current;
      try
      {
        string htmlPath = await Task.Run(() => page.Export(folder.Path));
        _context.ShowMessage("Exported", htmlPath, false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the page", ex.Message, true);
      }
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      if (_current == null)
      {
        return;
      }
      var file = _current.File;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, file.Name, Path.GetExtension(file.Name));
      if (path != null)
      {
        byte[] data = await Task.Run(() => file.GetData());
        await System.IO.File.WriteAllBytesAsync(path, data);
        _context.ShowMessage("Exported", path, false);
      }
    }

    #endregion

    public void Dispose()
    {
      if (Web.CoreWebView2 != null)
      {
        Web.CoreWebView2.WebResourceRequested -= Core_WebResourceRequested;
      }
      Web.Close();
    }
  }

  /// <summary>
  /// Where the pages of the game's internet are: a folder with one folder per site (pc\html\www.burgershot.net\...).
  /// Paths are like the links in the pages, "www.burgershot.net/menu.html" for menu.whm.
  /// </summary>
  public sealed class WebSite
  {
    private WebSite(Directory root, string startPath)
    {
      Root = root;
      StartPath = startPath;
      _roots.Add(root);

      // The episodes only have the sites that are new or changed for them (TBoGT\pc\html); the others are
      // those of the game in the folders above (pc\html of GTA IV in the Complete Edition). Pages are looked
      // up in the episode first, then upwards.
      if (root.Name.Equals("html", StringComparison.OrdinalIgnoreCase) &&
          root.ParentDirectory?.Name.Equals("pc", StringComparison.OrdinalIgnoreCase) == true)
      {
        for (var dir = root.ParentDirectory.ParentDirectory?.ParentDirectory; dir != null; dir = dir.ParentDirectory)
        {
          if (dir.FindByName("pc") is Directory pc && pc.FindByName("html") is Directory html && html != root)
          {
            _roots.Add(html);
          }
        }
      }

      // A page copied out of its game: the game it was said to be from gives the other sites and the texts.
      if (FindTextFolderAbove(root) == null && Services.Games.Current is Services.GameInstall game)
      {
        AddGame(game);
      }
    }

    // The html folders pages are looked up in, nearest first.
    private readonly List<Directory> _roots = new List<Directory>();

    // The html folders of the game added for a page outside it, with the folder (episode) each is in.
    private readonly List<(Directory Html, Directory Content)> _gameFolders = new List<(Directory, Directory)>();

    private void AddGame(Services.GameInstall game)
    {
      try
      {
        var fs = new RealFileSystem();
        fs.Open(game.Folder);
        foreach (var name in game.Game.ContentFolders)
        {
          Directory? content = name.Length == 0 ? fs.RootDirectory : fs.RootDirectory.FindByName(name) as Directory;
          if (content?.FindByName("pc") is Directory pc && pc.FindByName("html") is Directory html)
          {
            _roots.Add(html);
            _gameFolders.Add((html, content));
          }
        }
      }
      catch (Exception)
      {
        // Then the page shows the keys and only links to the sites next to it.
      }
    }

    private static Directory? FindTextFolderAbove(Directory start)
    {
      for (var dir = start; dir != null; dir = dir.ParentDirectory)
      {
        if (dir.FindByName("common") is Directory common && common.FindByName("text") is Directory text)
        {
          return text;
        }
      }
      return null;
    }

    public Directory Root { get; }
    public string StartPath { get; }

    #region Languages

    /// <summary>
    /// The text tables a game can have, in the order offered. Japanese is left out: its table holds the
    /// numbers of the signs in the font texture (fonts_j.wtd), and nothing in the game says which sign
    /// each one is.
    /// </summary>
    private static readonly (string File, string Name)[] KnownLanguages =
    {
      ("american.gxt", "English"),
      ("german.gxt", "Deutsch"),
      ("french.gxt", "Fran\u00E7ais"),
      ("italian.gxt", "Italiano"),
      ("spanish.gxt", "Espa\u00F1ol"),
      ("russian.gxt", "\u0420\u0443\u0441\u0441\u043A\u0438\u0439"),
    };

    private Directory? _textFolder;
    private bool _textFolderSearched;
    private RageLib.Text.GxtFile? _texts;
    private string? _language;

    /// <summary>The languages this game has text tables for, by their file name, and the name to show.</summary>
    public IReadOnlyList<(string File, string Name)> Languages
    {
      get
      {
        var folder = TextFolder();
        return folder == null
          ? Array.Empty<(string, string)>()
          : KnownLanguages.Where(l => folder.FindByName(l.File) is File).ToArray();
      }
    }

    /// <summary>The text table in use ("german.gxt"), or null when the pages show the keys.</summary>
    public string? Language => _language;

    /// <summary>Loads the text table of a language; false if it can't be read (then the keys are shown).</summary>
    public bool SetLanguage(string file)
    {
      RageLib.Text.GxtFile? texts = null;
      if (TextFolder()?.FindByName(file) is File gxt)
      {
        try
        {
          texts = new RageLib.Text.GxtFile();
          texts.Open(new MemoryStream(gxt.GetData()));
          texts.Charset = file.Equals("russian.gxt", StringComparison.OrdinalIgnoreCase)
            ? RageLib.Text.GxtCharset.Russian
            : RageLib.Text.GxtCharset.Western;
        }
        catch (Exception)
        {
          texts = null;
        }
      }
      lock (this)
      {
        _texts = texts;
        _language = texts != null ? file : null;
      }
      return texts != null;
    }

    /// <summary>A text of the game the pages show by key ("EYE_HOME_3") in the chosen language, or null.</summary>
    public string? FindText(string key)
    {
      RageLib.Text.GxtFile? texts;
      lock (this)
      {
        texts = _texts;
      }
      return texts?.Find(key);
    }

    /// <summary>common\text in the folder of the game, found from the html folder upwards.</summary>
    private Directory? TextFolder()
    {
      lock (this)
      {
        if (!_textFolderSearched)
        {
          _textFolderSearched = true;
          _textFolder = FindTextFolderAbove(Root);
          if (_textFolder == null && _gameFolders.Count > 0)
          {
            // Of the game added for a page outside it: the texts of the episode that has the page's site.
            string site = StartPath.Split('/')[0];
            var (_, content) = _gameFolders.FirstOrDefault(g => g.Html.FindByName(site) is Directory);
            content ??= _gameFolders[0].Content;
            _textFolder = (content.FindByName("common") as Directory)?.FindByName("text") as Directory;
          }
        }
        return _textFolder;
      }
    }

    #endregion

    /// <summary>The folder above the site the page is in (a folder named like a domain), or the page's own folder.</summary>
    public static WebSite For(File page)
    {
      Directory? root = null;
      for (var dir = page.ParentDirectory; dir != null; dir = dir.ParentDirectory)
      {
        if (dir.Name.Contains('.') && dir.ParentDirectory != null)
        {
          root = dir.ParentDirectory;
          break;
        }
      }
      root ??= page.ParentDirectory;

      var names = new List<string> { HtmlName(page.Name) };
      for (var dir = page.ParentDirectory; dir != null && dir != root; dir = dir.ParentDirectory)
      {
        names.Insert(0, dir.Name);
      }
      return new WebSite(root!, string.Join("/", names));
    }

    /// <summary>The .whm file of a page path, from the nearest html folder that has it, or null.</summary>
    public File? Find(string path)
    {
      foreach (var root in _roots)
      {
        if (FindIn(root, path) is File file)
        {
          return file;
        }
      }
      return null;
    }

    private static File? FindIn(Directory root, string path)
    {
      string[] parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
      Directory dir = root;
      for (int i = 0; i < parts.Length; i++)
      {
        bool last = i == parts.Length - 1;
        string name = last ? Path.ChangeExtension(parts[i], ".whm") : parts[i];
        var entry = dir.FindByName(name);
        if (last)
        {
          return entry as File;
        }
        if (entry is not Directory sub)
        {
          return null;
        }
        dir = sub;
      }
      return null;
    }

    public static string HtmlName(string whmName) => Path.ChangeExtension(whmName, ".html");

    /// <summary>
    /// A texture of one of the game's texture dictionaries as PNG, or null. They are in the textures folder
    /// next to the html folder (pc\textures\webthemesvip.wtd).
    /// </summary>
    public byte[]? LoadSharedImage(string dictionary, string texture)
    {
      File? file = _roots.Select(root => (root.ParentDirectory?.FindByName("textures") as Directory)?.FindByName(dictionary + ".wtd") as File)
                         .FirstOrDefault(f => f != null);
      if (file == null)
      {
        return null;
      }

      var textureFile = new RageLib.Textures.TextureFile();
      textureFile.Open(new MemoryStream(file.GetData()));
      try
      {
        var found = textureFile.FindTextureByName(texture);
        if (found == null)
        {
          return null;
        }
        using var image = found.Decode();
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        return png.ToArray();
      }
      finally
      {
        textureFile.Dispose();
      }
    }
  }

  /// <summary>A page converted to HTML, with its images as PNG files under their paths.</summary>
  public sealed class WebPage
  {
    // The links as RageLib writes them: an alert with the target.
    private static readonly Regex LinkPattern = new Regex("href=\"#\" onclick=\"alert\\('Link to: ([^']*)'\\);\"", RegexOptions.Compiled);

    // src="dictionary:texture.png" or url(dictionary:texture.png)
    private static readonly Regex SharedImagePattern = new Regex("(src=\"|url\\()([A-Za-z0-9_]+):([^\"):/]+)(\\.png[\")])", RegexOptions.Compiled);

    /// <summary>The path under which images of the game's texture dictionaries are served: "_txd/webThemesVIP/webTheme_1.png".</summary>
    public const string SharedImages = "_txd";

    private WebPage(File file, string path, string html, Dictionary<string, byte[]> images, int textureCount)
    {
      File = file;
      Path = path;
      Html = html;
      Images = images;
      TextureCount = textureCount;
    }

    public File File { get; }
    public string Path { get; }
    /// <summary>The page as HTML, with the game texts in the language of the site (see <see cref="Retranslate"/>).</summary>
    public string Html { get; private set; }
    public int TextureCount { get; }

    /// <summary>The images, as "www.burgershot.net/images/menu-home0.png" (lower case).</summary>
    public IReadOnlyDictionary<string, byte[]> Images { get; }

    private string Folder => Path.Contains('/') ? Path.Substring(0, Path.LastIndexOf('/') + 1) : "";

    public static WebPage Load(WebSite site, File file, string path)
    {
      var hyperText = new HyperTextFile();
      hyperText.Open(new MemoryStream(file.GetData()));
      string folder = path.Contains('/') ? path.Substring(0, path.LastIndexOf('/') + 1) : "";
      string html = BuildHtml(hyperText, site, folder);

      var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      int count = 0;
      var textures = hyperText.EmbeddedTextureFile;
      if (textures != null)
      {
        try
        {
          foreach (var texture in textures)
          {
            count++;
            using var image = texture.Decode();
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            images[(folder + texture.Name + ".png").ToLowerInvariant()] = png.ToArray();
          }
        }
        finally
        {
          textures.Dispose();
        }
      }
      return new WebPage(file, path, html, images, count);
    }

    /// <summary>Makes the HTML again with the texts of the site's current language; the images stay.</summary>
    public void Retranslate(WebSite site)
    {
      var hyperText = new HyperTextFile();
      hyperText.Open(new MemoryStream(File.GetData()));
      try
      {
        Html = BuildHtml(hyperText, site, Folder);
      }
      finally
      {
        hyperText.EmbeddedTextureFile?.Dispose();
      }
    }

    private static string BuildHtml(HyperTextFile hyperText, WebSite site, string folder)
    {
      var writer = new StringWriter();
      hyperText.WriteHTML(writer, site.FindText);

      // Links lead to the other pages, from the root of the game's internet.
      string html = LinkPattern.Replace(writer.ToString(), m => Link(m.Groups[1].Value, folder));
      // Images from a texture dictionary of the game ("webThemesVIP:webTheme_1.png") would read as a URL scheme.
      return SharedImagePattern.Replace(html, m => m.Groups[1].Value + "/" + SharedImages + "/" +
                                                   Uri.EscapeDataString(m.Groups[2].Value) + "/" + Uri.EscapeDataString(m.Groups[3].Value) + m.Groups[4].Value);
    }

    /// <summary>
    /// The attributes for a link of the game. Targets are a site and page ("www.burgershot.net\index.html",
    /// also with "http://"), a site alone ("www.burgershot.net/" means its index), a page next to this one
    /// ("aboutus.html"), "BACK", or a number or "#" for something the game's scripts do.
    /// </summary>
    private static string Link(string link, string folder)
    {
      string path = link.Replace('\\', '/').Trim();
      int scheme = path.IndexOf("://", StringComparison.Ordinal);
      if (scheme >= 0)
      {
        path = path.Substring(scheme + 3);
      }
      path = path.TrimStart('/');

      if (path.Equals("BACK", StringComparison.OrdinalIgnoreCase))
      {
        return "href=\"javascript:history.back()\"";
      }
      if (path.Length == 0 || path.All(c => char.IsDigit(c) || c == '#'))
      {
        string title = System.Net.WebUtility.HtmlEncode("In the game this link starts an action of the scripts (" + link + ")");
        return "href=\"#\" onclick=\"return false;\" title=\"" + title + "\"";
      }

      // "www.craplist.net/#": the part after # only marks a place on the page.
      int hash = path.IndexOf('#');
      if (hash > 0)
      {
        path = path.Substring(0, hash);
      }

      string first = path.Split('/')[0];
      bool isSite = first.Contains('.') && !IsPage(first);
      if (!isSite)
      {
        path = folder + path;
      }
      else if (!path.Contains('/'))
      {
        path += "/index.html";
      }
      else if (path.EndsWith("/", StringComparison.Ordinal))
      {
        path += "index.html";
      }
      return "href=\"/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)) + "\"";
    }

    private static bool IsPage(string name) =>
      name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
      name.EndsWith(".whm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves the page and its images into <paramref name="folder"/>; links point to the other pages' places.</summary>
    public string Export(string folder)
    {
      string pageFolder = Folder;
      int depth = pageFolder.Count(c => c == '/');
      string up = string.Concat(Enumerable.Repeat("../", depth));

      // On disk the links are relative, so a folder of exported pages links up.
      string html = Html.Replace("href=\"/", "href=\"" + up);
      string htmlPath = System.IO.Path.Combine(folder, Path.Substring(pageFolder.Length));
      System.IO.File.WriteAllText(htmlPath, html);

      foreach (var image in Images)
      {
        string relative = image.Key.Substring(pageFolder.Length);
        string target = System.IO.Path.Combine(folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        System.IO.File.WriteAllBytes(target, image.Value);
      }
      return htmlPath;
    }
  }

  /// <summary>In-game web pages (.whm).</summary>
  internal sealed class WebPageViewer : IViewer
  {
    // The language chosen last, while SparkIV runs.
    internal static string? s_language;

    public object Load(File file, byte[] data)
    {
      var site = WebSite.For(file);
      var languages = site.Languages;
      if (languages.Count > 0)
      {
        // The one chosen before if this game has it, else English, else the first there is.
        string language = languages.Any(l => l.File == s_language) ? s_language!
          : languages.Any(l => l.File == "american.gxt") ? "american.gxt"
          : languages[0].File;
        site.SetLanguage(language);
      }
      return (site, WebPage.Load(site, file, site.StartPath));
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded)
    {
      var (site, page) = ((WebSite, WebPage))loaded;
      return new WebPageView(context, site, page);
    }
  }

  /// <summary>The images of an in-game web page, in the texture viewer.</summary>
  internal sealed class WebPageTextureViewer : IViewer
  {
    public object Load(File file, byte[] data)
    {
      var hyperText = new HyperTextFile();
      hyperText.Open(new MemoryStream(data));
      if (hyperText.EmbeddedTextureFile == null)
      {
        throw new InvalidDataException("This page has no images.");
      }
      return new TextureDictionary(hyperText.EmbeddedTextureFile) { Embedded = true };
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new TextureView(context, (TextureDictionary)loaded);
  }
}
