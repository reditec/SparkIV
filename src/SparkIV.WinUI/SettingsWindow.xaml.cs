using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SparkIV.WinUI.Services;

namespace SparkIV.WinUI
{
  /// <summary>
  /// SparkIV's settings: for now where the games are installed, one folder per game and platform.
  /// </summary>
  public sealed partial class SettingsWindow : Window
  {
    public SettingsWindow()
    {
      InitializeComponent();

      Title = "Settings - SparkIV";
      ExtendsContentIntoTitleBar = true;
      SetTitleBar(AppTitleBar);
      Root.Loaded += (sender, args) => TitleBarColors.Apply(this, Root);
      Root.ActualThemeChanged += (sender, args) => TitleBarColors.Apply(this, Root);

      string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "GTAIV.ico");
      if (System.IO.File.Exists(icon))
      {
        AppWindow.SetIcon(icon);
      }
      AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 560));

      foreach (var game in Games.All)
      {
        foreach (var platform in game.Platforms)
        {
          GameFolderList.Children.Add(CreateFolderRow(game, platform));
        }
      }
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
      string? page = sender.SelectedItem?.Tag as string;
      foreach (var child in Pages.Children)
      {
        if (child is FrameworkElement element)
        {
          element.Visibility = element.Name == page ? Visibility.Visible : Visibility.Collapsed;
        }
      }
      Info.IsOpen = false;
    }

    private FrameworkElement CreateFolderRow(Game game, string platform)
    {
      var card = new Border
      {
        Padding = new Thickness(16, 12, 16, 12),
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
      };
      var grid = new Grid { ColumnSpacing = 12 };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      card.Child = grid;

      var text = new StackPanel { Spacing = 2 };
      text.Children.Add(new TextBlock { Text = game.Name + "  ·  " + platform, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
      var folderText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
      var stateText = new TextBlock
      {
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
      };
      text.Children.Add(folderText);
      text.Children.Add(stateText);
      grid.Children.Add(text);

      var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
      var browse = new Button { Content = "Browse…" };
      var reset = new Button { Content = "Reset" };
      ToolTipService.SetToolTip(reset, "Forget the folder set: use the one found automatically, or ask when it is needed");
      buttons.Children.Add(browse);
      buttons.Children.Add(reset);
      Grid.SetColumn(buttons, 1);
      grid.Children.Add(buttons);

      void Show()
      {
        string? stored = AppSettings.GetGamePath(game.Id, platform);
        string? folder = Games.InstallFolder(game, platform);
        bool storedValid = stored != null && stored == folder;

        folderText.Text = folder ?? stored ?? "Not set";
        ToolTipService.SetToolTip(folderText, folder ?? stored);
        string edition = folder != null && GameLocator.IsCompleteEdition(folder) ? "Complete Edition  ·  " : "";
        stateText.Text = storedValid ? edition + "Set"
          : stored != null ? "The folder set doesn't contain the game any more" + (folder != null ? "; found automatically: " + folder : "")
          : folder != null ? edition + "Found automatically"
          : "Asked for when it is needed";
        reset.IsEnabled = stored != null;
      }
      Show();

      browse.Click += async (sender, args) =>
      {
        Info.IsOpen = false;
        var (picked, error) = await GameFolders.PickAsync(this, game, platform);
        if (error != null)
        {
          Info.Title = "Not a folder of " + game.Name;
          Info.Message = error;
          Info.IsOpen = true;
        }
        if (picked != null)
        {
          Show();
        }
      };
      reset.Click += (sender, args) =>
      {
        AppSettings.RemoveGamePath(game.Id, platform);
        Show();
      };

      return card;
    }
  }
}
