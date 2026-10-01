using Microsoft.UI.Xaml;

namespace SparkIV.WinUI
{
  internal static class TitleBarColors
  {
    /// <summary>
    /// With the content extended into the title bar, the caption buttons keep fixed colors;
    /// this gives them the colors of the current theme so they stay visible on the Mica background.
    /// </summary>
    public static void Apply(Window window, FrameworkElement root)
    {
      bool dark = root.ActualTheme == ElementTheme.Dark;
      byte c = dark ? (byte)0xFF : (byte)0x00;
      var titleBar = window.AppWindow.TitleBar;

      titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
      titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
      titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, c, c, c);
      titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x80, c, c, c);
      titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x18, c, c, c);
      titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xFF, c, c, c);
      titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x30, c, c, c);
      titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xFF, c, c, c);
    }
  }
}
