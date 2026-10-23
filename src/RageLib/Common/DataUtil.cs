/**********************************************************************\

 RageLib
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
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace RageLib.Common
{
  public static class DataUtil
  {
    public static byte[] Decrypt(byte[] dataIn)
    {
      byte[] data = new byte[dataIn.Length];
      dataIn.CopyTo(data, 0);

      // AES-256 is Rijndael with a 128-bit block, which is what the archives use.
      using Aes aes = Aes.Create();
      aes.KeySize = 256;
      aes.Mode = CipherMode.ECB;
      aes.Key = KeyStore.AESKey;
      aes.Padding = PaddingMode.None;

      using ICryptoTransform transform = aes.CreateDecryptor();

      int dataLen = data.Length & ~0x0F;

      // Decrypt!

      // R* was nice enough to do it 16 times...
      // AES is just as effective doing it 1 time because it has multiple internal rounds

      if (dataLen > 0)
      {
        for (int i = 0; i < 16; i++)
        {
          transform.TransformBlock(data, 0, dataLen, data, 0);
        }
      }

      return data;
    }

    public static byte[] DecompressDeflate(byte[] data, int decompSize)
    {
      var decompData = new byte[decompSize];

      var inflater = new Inflater(true);
      inflater.SetInput(data);
      inflater.Inflate(decompData);

      return decompData;
    }

    /// <summary>
    /// Inflates a zlib stream (with its header and Adler-32, as the old native ragezip.dll took it) into parts of
    /// the given sizes, one after the other: a compressed script's code, its locals and its globals.
    /// </summary>
    public static byte[][] DecompressZlib(byte[] data, params int[] partSizes)
    {
      var parts = new byte[partSizes.Length][];
      using var inflater = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
      for (int i = 0; i < partSizes.Length; i++)
      {
        parts[i] = new byte[partSizes[i]];
        inflater.ReadExactly(parts[i]);
      }
      return parts;
    }

    public static uint SwapEndian(uint v)
    {
      return ((v >> 24) & 0xFF) |
             ((v >> 8) & 0xFF00) |
             ((v & 0xFF00) << 8) |
             ((v & 0xFF) << 24);
    }
  }
}
