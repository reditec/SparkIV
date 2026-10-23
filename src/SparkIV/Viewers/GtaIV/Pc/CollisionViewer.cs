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
