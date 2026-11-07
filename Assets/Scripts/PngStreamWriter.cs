namespace Assets.Scripts
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    /// <summary>
    /// Writes an 8-bit PNG row by row, straight to a stream, so an image larger than memory or than Unity's
    /// texture limit can be saved: RGB, or with a palette of up to 256 colours, one index byte per pixel.
    /// Rows go in top to bottom; <see cref="Finish"/> closes the image.
    /// </summary>
    public sealed class PngStreamWriter : IDisposable
    {
        private const int ChunkSize = 1 << 16;
        private static readonly uint[] CrcTable = MakeCrcTable();

        private readonly Stream _output;
        private readonly int _width;
        private readonly int _height;
        private readonly IdatStream _idat;
        private readonly DeflateStream _deflate;
        private readonly byte[] _row;
        private readonly int _bytesPerPixel;
        private int _rows;
        private uint _adlerA = 1;
        private uint _adlerB;
        private bool _finished;

        /// <param name="palette">RGB triples of up to 256 colours for an indexed image, or null for RGB.</param>
        public PngStreamWriter(Stream output, int width, int height, byte[] palette = null)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("An image needs at least one pixel.");
            }

            if (palette != null && (palette.Length == 0 || palette.Length % 3 != 0 || palette.Length > 256 * 3))
            {
                throw new ArgumentException("A palette has 1 to 256 RGB colours.");
            }

            _output = output;
            _width = width;
            _height = height;
            _bytesPerPixel = palette == null ? 3 : 1;
            _row = new byte[1 + (width * _bytesPerPixel)];

            _output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8; // bit depth
            header[9] = (byte)(palette == null ? 2 : 3); // RGB, or indexed
            WriteChunk(_output, "IHDR", header, header.Length);
            if (palette != null)
            {
                WriteChunk(_output, "PLTE", palette, palette.Length);
            }

            // zlib around raw deflate: a header here, the Adler-32 of the raw data at the end.
            _idat = new IdatStream(_output);
            _idat.Write(new byte[] { 0x78, 0x01 }, 0, 2);
            _deflate = new DeflateStream(_idat, CompressionLevel.Optimal, true);
        }

        public int RowsWritten => _rows;

        /// <summary>
        /// One row from <paramref name="data"/> at <paramref name="offset"/>: width × 3 bytes (R, G, B), or with a
        /// palette width bytes of colour indexes.
        /// </summary>
        public void WriteRow(byte[] data, int offset)
        {
            if (_finished || _rows >= _height)
            {
                throw new InvalidOperationException("The image already has all its rows.");
            }

            var length = _width * _bytesPerPixel;
            if (_bytesPerPixel == 1)
            {
                // Indexes don't filter well; the PNG specification advises none for them.
                _row[0] = 0;
                Buffer.BlockCopy(data, offset, _row, 1, length);
            }
            else
            {
                // Filter "Sub": each byte minus the one a pixel to its left, which turns plain areas into zeros.
                _row[0] = 1;
                for (var i = 0; i < 3; i++)
                {
                    _row[1 + i] = data[offset + i];
                }

                for (var i = 3; i < length; i++)
                {
                    _row[1 + i] = (byte)(data[offset + i] - data[offset + i - 3]);
                }
            }

            this.Adler(_row);
            _deflate.Write(_row, 0, _row.Length);
            _rows++;
        }

        /// <summary>Ends the image; every row must have been written.</summary>
        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            if (_rows != _height)
            {
                throw new InvalidOperationException("The image has " + _rows + " of its " + _height + " rows.");
            }

            _finished = true;
            _deflate.Dispose();
            var adler = new byte[4];
            WriteBigEndian(adler, 0, (_adlerB << 16) | _adlerA);
            _idat.Write(adler, 0, 4);
            _idat.Flush();
            WriteChunk(_output, "IEND", new byte[0], 0);
            _output.Flush();
        }

        public void Dispose()
        {
            if (!_finished)
            {
                _finished = true;
                _deflate.Dispose();
            }
        }

        private void Adler(byte[] data)
        {
            const uint Mod = 65521;
            var a = _adlerA;
            var b = _adlerB;
            var i = 0;
            while (i < data.Length)
            {
                // 5552 bytes is the most that can be summed before the 32-bit values could overflow.
                var end = Math.Min(data.Length, i + 5552);
                for (; i < end; i++)
                {
                    a += data[i];
                    b += a;
                }

                a %= Mod;
                b %= Mod;
            }

            _adlerA = a;
            _adlerB = b;
        }

        private static void WriteChunk(Stream output, string type, byte[] data, int length)
        {
            var head = new byte[8];
            WriteBigEndian(head, 0, (uint)length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            output.Write(head, 0, 8);
            output.Write(data, 0, length);
            var crc = Crc(Crc(0xFFFFFFFFu, head, 4, 4), data, 0, length) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            WriteBigEndian(tail, 0, crc);
            output.Write(tail, 0, 4);
        }

        private static uint Crc(uint crc, byte[] data, int offset, int length)
        {
            for (var i = offset; i < offset + length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        /// <summary>The compressed data, cut into IDAT chunks as it comes.</summary>
        private sealed class IdatStream : Stream
        {
            private readonly Stream _output;
            private readonly byte[] _buffer = new byte[ChunkSize];
            private int _count;

            public IdatStream(Stream output)
            {
                _output = output;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    var take = Math.Min(count, ChunkSize - _count);
                    Buffer.BlockCopy(buffer, offset, _buffer, _count, take);
                    _count += take;
                    offset += take;
                    count -= take;
                    if (_count == ChunkSize)
                    {
                        this.Flush();
                    }
                }
            }

            public override void Flush()
            {
                if (_count > 0)
                {
                    WriteChunk(_output, "IDAT", _buffer, _count);
                    _count = 0;
                }
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
