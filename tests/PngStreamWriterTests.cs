using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Assets.Scripts;

/// <summary>Offline tests of the streaming PNG encoder: it's decoded back here and compared.</summary>
public static class PngStreamWriterTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (var size in new[] { (1, 1), (3, 2), (257, 130), (700, 400) })
        {
            var (width, height) = size;
            var random = new Random(width * 31 + height);
            var pixels = new byte[width * height * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                // Plain areas and noise, so the filter and the chunking both get used.
                pixels[i] = (i / 300) % 3 == 0 ? (byte)random.Next(256) : (byte)(i / 900);
            }

            var file = new MemoryStream();
            using (var png = new PngStreamWriter(file, width, height))
            {
                for (var row = 0; row < height; row++)
                {
                    png.WriteRow(pixels, row * width * 3);
                }

                png.Finish();
            }

            var decoded = Decode(file.ToArray(), out var w, out var h, out var chunksOk, out var adlerOk);
            check("PNG " + width + "x" + height + ": size", w == width && h == height);
            check("PNG " + width + "x" + height + ": chunk CRCs", chunksOk);
            check("PNG " + width + "x" + height + ": zlib checksum", adlerOk);
            check("PNG " + width + "x" + height + ": pixels", decoded != null && decoded.SequenceEqual(pixels));
        }

        // An indexed image: the PLTE chunk and one index per pixel come back unchanged.
        {
            const int width = 300, height = 70;
            var palette = new byte[17 * 3];
            for (var i = 0; i < palette.Length; i++)
            {
                palette[i] = (byte)(i * 5);
            }

            var indexes = new byte[width * height];
            for (var i = 0; i < indexes.Length; i++)
            {
                indexes[i] = (byte)((i / 37) % 17);
            }

            var file = new MemoryStream();
            var png = new PngStreamWriter(file, width, height, palette);
            for (var row = 0; row < height; row++)
            {
                png.WriteRow(indexes, row * width);
            }

            png.Finish();
            var decoded = DecodeIndexed(file.ToArray(), out var foundPalette, out var chunksOk);
            check("PNG indexed: chunk CRCs", chunksOk);
            check("PNG indexed: palette", foundPalette != null && foundPalette.SequenceEqual(palette));
            check("PNG indexed: indexes", decoded != null && decoded.SequenceEqual(indexes));
        }

        var tooFew = new PngStreamWriter(new MemoryStream(), 2, 2);
        tooFew.WriteRow(new byte[6], 0);
        var threw = false;
        try
        {
            tooFew.Finish();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        check("PNG: finishing with rows missing fails", threw);
    }

    private static byte[] Decode(byte[] file, out int width, out int height, out bool chunksOk, out bool adlerOk)
    {
        width = height = 0;
        chunksOk = true;
        adlerOk = false;
        var idat = new MemoryStream();
        var position = 8;
        while (position < file.Length)
        {
            var length = (int)ReadBigEndian(file, position);
            var type = System.Text.Encoding.ASCII.GetString(file, position + 4, 4);
            var crc = Crc(file, position + 4, length + 4);
            chunksOk &= crc == ReadBigEndian(file, position + 8 + length);
            if (type == "IHDR")
            {
                width = (int)ReadBigEndian(file, position + 8);
                height = (int)ReadBigEndian(file, position + 12);
            }
            else if (type == "IDAT")
            {
                idat.Write(file, position + 8, length);
            }

            position += 12 + length;
        }

        var zlib = idat.ToArray();
        var raw = new MemoryStream();
        using (var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 6), CompressionMode.Decompress))
        {
            inflate.CopyTo(raw);
        }

        var data = raw.ToArray();
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        adlerOk = ((b << 16) | a) == ReadBigEndian(zlib, zlib.Length - 4);
        var stride = width * 3;
        if (data.Length != height * (stride + 1))
        {
            return null;
        }

        var pixels = new byte[height * stride];
        for (var row = 0; row < height; row++)
        {
            var filter = data[row * (stride + 1)];
            for (var i = 0; i < stride; i++)
            {
                var value = data[(row * (stride + 1)) + 1 + i];
                var left = i >= 3 ? pixels[(row * stride) + i - 3] : (byte)0;
                pixels[(row * stride) + i] = filter == 1 ? (byte)(value + left) : value;
            }
        }

        return pixels;
    }

    private static byte[] DecodeIndexed(byte[] file, out byte[] palette, out bool chunksOk)
    {
        palette = null;
        chunksOk = true;
        var idat = new MemoryStream();
        int width = 0, height = 0, colorType = -1;
        var position = 8;
        while (position < file.Length)
        {
            var length = (int)ReadBigEndian(file, position);
            var type = System.Text.Encoding.ASCII.GetString(file, position + 4, 4);
            chunksOk &= Crc(file, position + 4, length + 4) == ReadBigEndian(file, position + 8 + length);
            if (type == "IHDR")
            {
                width = (int)ReadBigEndian(file, position + 8);
                height = (int)ReadBigEndian(file, position + 12);
                colorType = file[position + 17];
            }
            else if (type == "PLTE")
            {
                palette = new byte[length];
                Array.Copy(file, position + 8, palette, 0, length);
            }
            else if (type == "IDAT")
            {
                idat.Write(file, position + 8, length);
            }

            position += 12 + length;
        }

        if (colorType != 3)
        {
            return null;
        }

        var zlib = idat.ToArray();
        var raw = new MemoryStream();
        using (var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 6), CompressionMode.Decompress))
        {
            inflate.CopyTo(raw);
        }

        var data = raw.ToArray();
        if (data.Length != height * (width + 1))
        {
            return null;
        }

        var indexes = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            if (data[row * (width + 1)] != 0)
            {
                return null;
            }

            Array.Copy(data, (row * (width + 1)) + 1, indexes, row * width, width);
        }

        return indexes;
    }

    private static uint ReadBigEndian(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    private static uint Crc(byte[] data, int offset, int length)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
