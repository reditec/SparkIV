using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SparkIV.WinUI.Model;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A window that shows one file with the viewer for its type.
  /// </summary>
  public sealed partial class ViewerWindow : Window
  {
    private readonly File _file;
    private readonly IViewer _viewer;

    public ViewerWindow(File file, IViewer viewer, string location)
    {
      InitializeComponent();
      _file = file;
      _viewer = viewer;

      Title = file.Name + " - SparkIV";
      TitleText.Text = file.Name;
      TitleDetail.Text = location;
      TitleGlyph.Glyph = FileTypes.ForFile(file.Name).Glyph;

      ExtendsContentIntoTitleBar = true;
      SetTitleBar(AppTitleBar);
      Root.Loaded += (sender, args) => TitleBarColors.Apply(this, Root);
      Root.ActualThemeChanged += (sender, args) => TitleBarColors.Apply(this, Root);

      string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "GTAIV.ico");
      if (System.IO.File.Exists(icon))
      {
        AppWindow.SetIcon(icon);
      }
      AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

      Root.Loaded += async (sender, args) => await LoadAsync();

      // A view that holds something open (the audio view: its player and sound bank) is disposed with the window.
      Closed += (sender, args) =>
      {
        _closed = true;
        (ViewHost.Content as IDisposable)?.Dispose();
      };
    }

    private bool _closed;

    /// <summary>Puts the window in front of the main window and gives it the focus.</summary>
    public void BringToFront()
    {
      AppWindow.MoveInZOrderAtTop();
      SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    private async Task LoadAsync()
    {
      try
      {
        byte[] data = await Task.Run(() => _file.GetData());
        object loaded = await Task.Run(() => _viewer.Load(_file, data));
        if (_closed)
        {
          (loaded as IDisposable)?.Dispose();
          return;
        }
        var context = new ViewerContext(this, _file, data, ShowMessage, SetTitle);
        ViewHost.Content = _viewer.CreateView(context, loaded);
      }
      catch (Exception ex)
      {
        if (!_closed)
        {
          ShowMessage("Could not open " + _file.Name, ex.Message, true);
        }
      }
      finally
      {
        if (!_closed)
        {
          Loading.IsActive = false;
        }
      }
    }

    /// <summary>For views that start something slow (a browser): whether the window was closed meanwhile.</summary>
    public bool IsClosed => _closed;

    private void SetTitle(string title, string detail)
    {
      Title = title;
      TitleText.Text = title;
      TitleDetail.Text = detail;
    }

    private void ShowMessage(string title, string message, bool isError)
    {
      Info.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
      Info.Title = title;
      Info.Message = message;
      Info.IsOpen = true;
    }
  }
}
