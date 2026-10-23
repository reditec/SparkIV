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

using System;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Tells open views that a file got new content (a texture replaced in it), so those that show it or
  /// use it (a model and the texture dictionary it takes its textures from) show the new one. Raised on
  /// the UI thread.
  /// </summary>
  internal static class FileChanges
  {
    public static event Action<File>? Changed;

    public static void Raise(File file) => Changed?.Invoke(file);
  }
}
