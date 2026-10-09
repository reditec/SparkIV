using Microsoft.UI.Xaml;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>GTA IV (PC) collision: static collision (.wbn) and collision dictionaries (.wbd), in the model view.</summary>
  internal sealed class CollisionViewer : IViewer
  {
    public object Load(File file, byte[] data) =>
      CollisionScene.Load(file, data, Services.Games.Current?.Folder ?? Services.Games.InstallFolder(Services.Games.GtaIV, "Windows"));

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new ModelView(context, (ModelScene)loaded);
  }
}
