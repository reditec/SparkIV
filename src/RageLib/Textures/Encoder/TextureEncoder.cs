/**********************************************************************\

 RageLib - Textures
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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RageLib.Textures.Encoder
{
  internal class TextureEncoder
  {
    internal static void Encode(Texture texture, Image image, int level)
    {
      var width = (int)texture.GetWidth(level);
      var height = (int)texture.GetHeight(level);

      // The picture scaled to the level's size, as B G R A (how GDI+ keeps 32 bit ARGB in memory).
      var bgra = new byte[width * height * 4];
      using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
      {
        using (Graphics g = Graphics.FromImage(bitmap))
        {
          g.InterpolationMode = InterpolationMode.HighQualityBicubic;
          g.PixelOffsetMode = PixelOffsetMode.HighQuality;
          g.DrawImage(image, 0, 0, width, height);
        }

        // Row by row: the rows of a locked bitmap may be longer than its pixels (stride). The pixels are
        // only read from the copy, as reading them one by one while locked fails.
        var rect = new Rectangle(0, 0, width, height);
        BitmapData bmpdata = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
          for (var y = 0; y < height; y++)
          {
            Marshal.Copy(bmpdata.Scan0 + y * bmpdata.Stride, bgra, y * width * 4, width * 4);
          }
        }
        finally
        {
          bitmap.UnlockBits(bmpdata);
        }
      }

      byte[] data;
      if (texture.TextureType == TextureType.A8R8G8B8)
      {
        data = bgra;  // A8R8G8B8 is B G R A in memory too.
      }
      else if (texture.TextureType == TextureType.L8)
      {
        data = new byte[width * height];
        for (var i = 0; i < data.Length; i++)
        {
          data[i] = (byte)((bgra[i * 4] + bgra[i * 4 + 1] + bgra[i * 4 + 2]) / 3);
        }
      }
      else
      {
        // The DXT encoder takes R G B A.
        data = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
          data[i * 4 + 0] = bgra[i * 4 + 2];
          data[i * 4 + 1] = bgra[i * 4 + 1];
          data[i * 4 + 2] = bgra[i * 4 + 0];
          data[i * 4 + 3] = bgra[i * 4 + 3];
        }
      }

      switch (texture.TextureType)
      {
        case TextureType.DXT1:
          data = DXTEncoder.EncodeDXT1(data, (int)width, (int)height);
          break;
        case TextureType.DXT3:
          data = DXTEncoder.EncodeDXT3(data, (int)width, (int)height);
          break;
        case TextureType.DXT5:
          data = DXTEncoder.EncodeDXT5(data, (int)width, (int)height);
          break;
        case TextureType.A8R8G8B8:
        case TextureType.L8:
          // Nothing to do
          break;
        default:
          throw new ArgumentOutOfRangeException();
      }

      texture.SetTextureData(level, data);
    }
  }
}
