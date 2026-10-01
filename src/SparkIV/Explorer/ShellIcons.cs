/**********************************************************************\

 Spark IV
 Copyright (C) 2008  Arushan/Aru <oneforaru at gmail.com>

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
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SparkIV.Explorer
{
  /// <summary>
  /// Small shell icons by file extension, so the lists look like Windows Explorer.
  /// The files don't have to exist: the icon comes from the registered file type.
  /// </summary>
  internal class ShellIcons : IDisposable
  {
    public const string FolderKey = "<folder>";

    public ShellIcons()
    {
      ImageList = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = SystemInformation.SmallIconSize };
    }

    public ImageList ImageList { get; private set; }

    public string KeyForFolder()
    {
      return Ensure(FolderKey, "folder", NativeMethods.FILE_ATTRIBUTE_DIRECTORY);
    }

    public string KeyFor(FileType type)
    {
      return type.IconExtension == null ? KeyForFolder() : Ensure(type.IconExtension, "file" + type.IconExtension, NativeMethods.FILE_ATTRIBUTE_NORMAL);
    }

    private string Ensure(string key, string path, uint attributes)
    {
      if (ImageList.Images.ContainsKey(key))
      {
        return key;
      }

      var info = new NativeMethods.SHFILEINFO();
      NativeMethods.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf(info),
                                  NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON | NativeMethods.SHGFI_USEFILEATTRIBUTES);
      if (info.hIcon != IntPtr.Zero)
      {
        using (var icon = Icon.FromHandle(info.hIcon))
        {
          ImageList.Images.Add(key, icon.ToBitmap());
        }
        NativeMethods.DestroyIcon(info.hIcon);
      }
      else
      {
        ImageList.Images.Add(key, new Bitmap(ImageList.ImageSize.Width, ImageList.ImageSize.Height));
      }
      return key;
    }

    public void Dispose()
    {
      ImageList.Dispose();
    }
  }
}
