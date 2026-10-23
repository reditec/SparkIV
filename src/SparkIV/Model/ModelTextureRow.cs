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
