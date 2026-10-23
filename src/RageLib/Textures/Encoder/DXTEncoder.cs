/**********************************************************************\

 RageLib - Textures

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

namespace RageLib.Textures.Encoder
{
  /// <summary>
  /// Compresses R G B A pixels to DXT1, DXT3 or DXT5, in C# (it used the native libsquish.dll, which
  /// SparkIV doesn't ship and which isn't there for 64 bit).
  /// </summary>
  /// <remarks>
  /// Each 4x4 block: the two colours at the ends of the colours' main axis, rounded to 5-6-5 bits, and for
  /// each pixel the nearest of the four colours between them. DXT1 with see-through pixels (alpha below
  /// 128) uses the three-colour mode, whose fourth colour is see-through. DXT3 keeps alpha with 4 bits per
  /// pixel, DXT5 as one of eight steps between the block's lowest and highest alpha. A block at the edge
  /// of a picture smaller than 4 pixels repeats its last pixels.
  /// </remarks>
  internal static class DXTEncoder
  {
    public static byte[] EncodeDXT1(byte[] data, int width, int height)
    {
      return Encode(data, width, height, 8, (pixels, output, at) => ColourBlock(pixels, output, at, true));
    }

    public static byte[] EncodeDXT3(byte[] data, int width, int height)
    {
      return Encode(data, width, height, 16, (pixels, output, at) =>
      {
        // 16 alphas of 4 bits, two per byte, the first in the low half.
        for (int i = 0; i < 8; i++)
        {
          int a0 = (pixels[(i * 2) * 4 + 3] * 15 + 127) / 255;
          int a1 = (pixels[(i * 2 + 1) * 4 + 3] * 15 + 127) / 255;
          output[at + i] = (byte)(a0 | (a1 << 4));
        }
        ColourBlock(pixels, output, at + 8, false);
      });
    }

    public static byte[] EncodeDXT5(byte[] data, int width, int height)
    {
      return Encode(data, width, height, 16, (pixels, output, at) =>
      {
        AlphaBlock(pixels, output, at);
        ColourBlock(pixels, output, at + 8, false);
      });
    }

    private delegate void BlockEncoder(byte[] pixels, byte[] output, int at);

    private static byte[] Encode(byte[] data, int width, int height, int blockSize, BlockEncoder encode)
    {
      int blocksX = Math.Max(1, (width + 3) / 4);
      int blocksY = Math.Max(1, (height + 3) / 4);
      var output = new byte[blocksX * blocksY * blockSize];
      var pixels = new byte[16 * 4];

      for (int by = 0; by < blocksY; by++)
      {
        for (int bx = 0; bx < blocksX; bx++)
        {
          for (int y = 0; y < 4; y++)
          {
            for (int x = 0; x < 4; x++)
            {
              int sx = Math.Min(bx * 4 + x, width - 1);
              int sy = Math.Min(by * 4 + y, height - 1);
              Buffer.BlockCopy(data, (sy * width + sx) * 4, pixels, (y * 4 + x) * 4, 4);
            }
          }
          encode(pixels, output, (by * blocksX + bx) * blockSize);
        }
      }
      return output;
    }

    /// <summary>The colour part of a block: two 5-6-5 colours and 2 bits per pixel.</summary>
    private static void ColourBlock(byte[] pixels, byte[] output, int at, bool dxt1)
    {
      bool transparent = false;
      if (dxt1)
      {
        for (int i = 0; i < 16; i++)
        {
          transparent |= pixels[i * 4 + 3] < 128;
        }
      }

      // The ends of the colours along their main axis (only the pixels that are shown count).
      FindEndpoints(pixels, dxt1, out var min, out var max);
      ushort c0 = To565(max), c1 = To565(min);

      // Four colours: c0 > c1. Three colours and see-through: c0 <= c1.
      if (transparent ? c0 > c1 : c0 < c1)
      {
        (c0, c1) = (c1, c0);
      }
      if (!transparent && c0 == c1)
      {
        // All one colour: the four-colour mode needs c0 > c1, which index 0 then shows.
        if (c1 > 0)
        {
          c1--;
        }
        else
        {
          c0++;
        }
      }

      var palette = new int[4][];
      palette[0] = From565(c0);
      palette[1] = From565(c1);
      if (!transparent)
      {
        palette[2] = Mix(palette[0], palette[1], 2, 1, 3);
        palette[3] = Mix(palette[0], palette[1], 1, 2, 3);
      }
      else
      {
        palette[2] = Mix(palette[0], palette[1], 1, 1, 2);
        palette[3] = null;  // see-through
      }

      uint indices = 0;
      for (int i = 0; i < 16; i++)
      {
        int index;
        if (transparent && pixels[i * 4 + 3] < 128)
        {
          index = 3;
        }
        else
        {
          index = 0;
          int best = int.MaxValue;
          for (int p = 0; p < 4; p++)
          {
            if (palette[p] == null)
            {
              continue;
            }
            int dr = pixels[i * 4] - palette[p][0], dg = pixels[i * 4 + 1] - palette[p][1], db = pixels[i * 4 + 2] - palette[p][2];
            int distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (distance < best)
            {
              best = distance;
              index = p;
            }
          }
        }
        indices |= (uint)index << (i * 2);
      }

      output[at] = (byte)c0;
      output[at + 1] = (byte)(c0 >> 8);
      output[at + 2] = (byte)c1;
      output[at + 3] = (byte)(c1 >> 8);
      output[at + 4] = (byte)indices;
      output[at + 5] = (byte)(indices >> 8);
      output[at + 6] = (byte)(indices >> 16);
      output[at + 7] = (byte)(indices >> 24);
    }

    private static void FindEndpoints(byte[] pixels, bool skipTransparent, out int[] min, out int[] max)
    {
      // Mean and covariance of the colours.
      double mr = 0, mg = 0, mb = 0;
      int count = 0;
      for (int i = 0; i < 16; i++)
      {
        if (skipTransparent && pixels[i * 4 + 3] < 128)
        {
          continue;
        }
        mr += pixels[i * 4];
        mg += pixels[i * 4 + 1];
        mb += pixels[i * 4 + 2];
        count++;
      }
      if (count == 0)
      {
        min = new[] { 0, 0, 0 };
        max = new[] { 0, 0, 0 };
        return;
      }
      mr /= count;
      mg /= count;
      mb /= count;

      double rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
      for (int i = 0; i < 16; i++)
      {
        if (skipTransparent && pixels[i * 4 + 3] < 128)
        {
          continue;
        }
        double r = pixels[i * 4] - mr, g = pixels[i * 4 + 1] - mg, b = pixels[i * 4 + 2] - mb;
        rr += r * r;
        rg += r * g;
        rb += r * b;
        gg += g * g;
        gb += g * b;
        bb += b * b;
      }

      // The main axis by a few steps of power iteration, from the direction of the largest spread.
      double ar = 1, ag = 1, ab = 1;
      for (int step = 0; step < 8; step++)
      {
        double nr = rr * ar + rg * ag + rb * ab;
        double ng = rg * ar + gg * ag + gb * ab;
        double nb = rb * ar + gb * ag + bb * ab;
        double length = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
        if (length < 1e-9)
        {
          break;
        }
        ar = nr / length;
        ag = ng / length;
        ab = nb / length;
      }

      double lowest = double.MaxValue, highest = double.MinValue;
      int low = 0, high = 0;
      for (int i = 0; i < 16; i++)
      {
        if (skipTransparent && pixels[i * 4 + 3] < 128)
        {
          continue;
        }
        double t = (pixels[i * 4] - mr) * ar + (pixels[i * 4 + 1] - mg) * ag + (pixels[i * 4 + 2] - mb) * ab;
        if (t < lowest)
        {
          lowest = t;
          low = i;
        }
        if (t > highest)
        {
          highest = t;
          high = i;
        }
      }
      min = new int[] { pixels[low * 4], pixels[low * 4 + 1], pixels[low * 4 + 2] };
      max = new int[] { pixels[high * 4], pixels[high * 4 + 1], pixels[high * 4 + 2] };
    }

    /// <summary>The DXT5 alpha part: two alphas and 3 bits per pixel for one of the eight steps between them.</summary>
    private static void AlphaBlock(byte[] pixels, byte[] output, int at)
    {
      int a0 = 0, a1 = 255;
      for (int i = 0; i < 16; i++)
      {
        int a = pixels[i * 4 + 3];
        a0 = Math.Max(a0, a);
        a1 = Math.Min(a1, a);
      }

      // a0 > a1: eight steps from a0 to a1. All the same: one step that fits all.
      var steps = new int[8];
      steps[0] = a0;
      steps[1] = a1;
      for (int s = 1; s < 7; s++)
      {
        steps[s + 1] = ((7 - s) * a0 + s * a1) / 7;
      }

      ulong indices = 0;
      for (int i = 0; i < 16; i++)
      {
        int a = pixels[i * 4 + 3];
        int index = 0, best = int.MaxValue;
        for (int s = 0; s < 8; s++)
        {
          int distance = Math.Abs(a - steps[s]);
          if (distance < best)
          {
            best = distance;
            index = s;
          }
        }
        indices |= (ulong)index << (i * 3);
      }

      output[at] = (byte)a0;
      output[at + 1] = (byte)a1;
      for (int b = 0; b < 6; b++)
      {
        output[at + 2 + b] = (byte)(indices >> (b * 8));
      }
    }

    private static ushort To565(int[] rgb) =>
      (ushort)(((rgb[0] * 31 + 127) / 255 << 11) | ((rgb[1] * 63 + 127) / 255 << 5) | ((rgb[2] * 31 + 127) / 255));

    private static int[] From565(ushort c)
    {
      int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
      return new[] { (r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2) };
    }

    private static int[] Mix(int[] a, int[] b, int wa, int wb, int total) =>
      new[] { (a[0] * wa + b[0] * wb) / total, (a[1] * wa + b[1] * wb) / total, (a[2] * wa + b[2] * wb) / total };
  }
}
