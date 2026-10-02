using Microsoft.UI.Xaml.Media;
using RageLib.Textures;

namespace SparkIV.WinUI.Model
{
  /// <summary>One texture in the list of a texture dictionary.</summary>
  public sealed class TextureRow
  {
    public TextureRow(Texture texture, ImageSource? thumbnail)
    {
      Texture = texture;
      Thumbnail = thumbnail;
    }

    public Texture Texture { get; }
    public ImageSource? Thumbnail { get; }

    public string Title => Texture.TitleName;
    public string Detail => Texture.Width + " \u00D7 " + Texture.Height + "   \u00B7   " + Texture.TextureType;
  }
}
