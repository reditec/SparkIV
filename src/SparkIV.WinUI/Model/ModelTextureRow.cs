using Microsoft.UI.Xaml.Media;

namespace SparkIV.WinUI.Model
{
  /// <summary>One texture stored in a model, in the Textures tab of the model viewer.</summary>
  public sealed class ModelTextureRow
  {
    public ModelTextureRow(string title, string detail, bool used, ImageSource? thumbnail)
    {
      Title = title;
      Detail = used ? detail : detail + "   ·   not used by a material";
      Thumbnail = thumbnail;
    }

    public string Title { get; }
    public string Detail { get; }
    public ImageSource? Thumbnail { get; }
  }
}
