using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SparkIV.WinUI.Services;
using Windows.Storage.Pickers;

namespace SparkIV.WinUI
{
  /// <summary>
  /// Asks which game a file is from, when SparkIV can't tell from where the file is: like the start of
  /// OpenIV, one line per game with its platforms. The game's folder is asked for once and remembered.
  /// </summary>
  public sealed partial class GameChooserWindow : Window
  {
    private readonly TaskCompletionSource<GameInstall?> _result = new TaskCompletionSource<GameInstall?>();

    private GameChooserWindow(string path, bool optional)
    {
      InitializeComponent();

      Title = "Select game - SparkIV";
      TitleText.Text = "Select game";
      FileText.Text = Path.GetFileName(path) + " is not in a game folder. " +
                      (optional ? "With its game, the page shows the game's texts and links to the other sites."
                                : "SparkIV needs the game for the key of the archive.");
      CancelButton.Content = optional ? "Without game" : "Cancel";
      HintText.Text = "The folder of a game is asked for once and remembered; it can be changed in the settings.";

      ExtendsContentIntoTitleBar = true;
      SetTitleBar(AppTitleBar);
      Root.Loaded += (sender, args) => TitleBarColors.Apply(this, Root);
      Root.ActualThemeChanged += (sender, args) => TitleBarColors.Apply(this, Root);

      string icon = Path.Combine(AppContext.BaseDirectory, "GTAIV.ico");
      if (System.IO.File.Exists(icon))
      {
        AppWindow.SetIcon(icon);
      }
      if (AppWindow.Presenter is OverlappedPresenter presenter)
      {
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
      }
      AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 520));
      CenterOnScreen();

      foreach (var game in Games.All)
      {
        GameList.Children.Add(CreateRow(game));
      }

      Closed += (sender, args) => _result.TrySetResult(null);
    }

    /// <summary>
    /// Asks for the game of a file. Null if the user cancelled. <paramref name="optional"/>: the file
    /// opens without a game too (a web page), so cancelling is offered as "Without game".
    /// </summary>
    internal static async Task<GameInstall?> AskAsync(string path, bool optional)
    {
      var window = new GameChooserWindow(path, optional);
      window.Activate();
      GameInstall? install = await window._result.Task;
      window.Close();
      return install;
    }

    private void CenterOnScreen()
    {
      var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
      var size = AppWindow.Size;
      AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    #region Rows

    private FrameworkElement CreateRow(Game game)
    {
      var grid = new Grid { Padding = new Thickness(8), ColumnSpacing = 16 };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

      var tile = new Border
      {
        Width = 64,
        Height = 64,
        CornerRadius = new CornerRadius(6),
        VerticalAlignment = VerticalAlignment.Top,
        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
      };
      grid.Children.Add(tile);

      var text = new StackPanel { Spacing = 2 };
      Grid.SetColumn(text, 1);
      grid.Children.Add(text);

      text.Children.Add(new TextBlock { Text = game.Name, Style = (Style)Application.Current.Resources["BodyLargeTextBlockStyle"] });

      // One link per platform, like OpenIV. Each platform has its own folder: asked for on the first click,
      // changed in the settings.
      var platforms = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-12, 0, 0, 0) };
      foreach (var platform in game.Platforms)
      {
        var button = new HyperlinkButton { Content = platform };
        button.Click += async (sender, args) => await ChooseAsync(game, platform);
        platforms.Children.Add(button);
      }
      text.Children.Add(platforms);

      tile.Child = CreateTileContent(game);
      return grid;
    }

    /// <summary>The icon of the game's exe when the folder of a platform is known, else the short name of the game.</summary>
    private static UIElement CreateTileContent(Game game)
    {
      string? folder = game.Platforms.Select(p => Games.InstallFolder(game, p)).FirstOrDefault(f => f != null);
      string exe = game == Games.Episodes ? "EFLC.exe" : "GTAIV.exe";
      if (folder != null && System.IO.File.Exists(Path.Combine(folder, exe)))
      {
        try
        {
          using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(folder, exe));
          if (icon != null)
          {
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            png.Position = 0;
            var image = new BitmapImage();
            image.SetSource(png.AsRandomAccessStream());
            return new Image { Source = image, Width = 40, Height = 40 };
          }
        }
        catch (Exception)
        {
          // Then the name.
        }
      }
      return new TextBlock
      {
        Text = game.ShortName,
        FontWeight = FontWeights.SemiBold,
        FontSize = game.ShortName.Length > 2 ? 16 : 24,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
      };
    }

    #endregion

    private async Task ChooseAsync(Game game, string platform)
    {
      Info.IsOpen = false;
      string? folder = Games.InstallFolder(game, platform);
      if (folder == null)
      {
        var (picked, error) = await GameFolders.PickAsync(this, game, platform);
        if (error != null)
        {
          Info.Title = "Not a folder of " + game.Name;
          Info.Message = error;
          Info.IsOpen = true;
        }
        if (picked == null)
        {
          return;
        }
        folder = picked;
      }
      _result.TrySetResult(new GameInstall(game, folder, platform));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _result.TrySetResult(null);
  }
}
