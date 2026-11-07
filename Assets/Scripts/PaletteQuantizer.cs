namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Picks up to 256 colours for an image from samples of it, and maps its pixels onto them, for a much
    /// smaller PNG. Colours are counted in a 64-level cube (6 bits a channel). The most common cells, such as the
    /// background and the blocks' own colours, each get their own entry with their exact average colour; the
    /// rest (the shades at the edges of text and blocks) are shared out by median cut. No dithering, so text
    /// stays crisp.
    /// </summary>
    public sealed class PaletteQuantizer
    {
        private const int Bits = 6;
        private const int Levels = 1 << Bits;
        private const int Cells = Levels * Levels * Levels;
        private const int Shift = 8 - Bits;

        private readonly long[] _count = new long[Cells];
        private readonly long[] _sumR = new long[Cells];
        private readonly long[] _sumG = new long[Cells];
        private readonly long[] _sumB = new long[Cells];

        /// <summary>How many of the colours go to the most common cells, each on its own.</summary>
        public int Reserved { get; set; } = 64;

        public static int Cell(byte r, byte g, byte b)
        {
            return ((r >> Shift) << (2 * Bits)) | ((g >> Shift) << Bits) | (b >> Shift);
        }

        /// <summary>Counts every <paramref name="step"/>-th of <paramref name="pixels"/> RGB pixels.</summary>
        public void Sample(byte[] rgb, int offset, int pixels, int step)
        {
            for (var p = 0; p < pixels; p += step)
            {
                var i = offset + (p * 3);
                var r = rgb[i];
                var g = rgb[i + 1];
                var b = rgb[i + 2];
                var cell = Cell(r, g, b);
                _count[cell]++;
                _sumR[cell] += r;
                _sumG[cell] += g;
                _sumB[cell] += b;
            }
        }

        /// <summary>
        /// The palette (RGB triples, at most <paramref name="colours"/>) and a lookup from each cube cell to its
        /// palette index, for <see cref="Map"/>.
        /// </summary>
        public byte[] Build(int colours, out byte[] lookup)
        {
            lookup = new byte[Cells];
            var occupied = Enumerable.Range(0, Cells).Where(cell => _count[cell] > 0).ToList();
            if (occupied.Count == 0)
            {
                lookup = new byte[Cells];
                return new byte[3];
            }

            var entries = new List<byte[]>();
            var groups = new List<List<int>>();

            // The most common cells first, one entry each.
            occupied.Sort((a, b) => _count[b].CompareTo(_count[a]));
            var reserved = Math.Min(occupied.Count, Math.Min(this.Reserved, colours - 1));
            if (occupied.Count <= colours)
            {
                reserved = occupied.Count;
            }

            for (var i = 0; i < reserved; i++)
            {
                groups.Add(new List<int> { occupied[i] });
            }

            // The rest by median cut: the group with the most pixels times colour spread splits along its widest
            // channel at the pixel-weighted median, until the colours are used up.
            var rest = occupied.Skip(reserved).ToList();
            if (rest.Count > 0)
            {
                var boxes = new List<List<int>> { rest };
                while (groups.Count + boxes.Count < colours)
                {
                    List<int> widest = null;
                    long most = -1;
                    foreach (var box in boxes)
                    {
                        if (box.Count < 2)
                        {
                            continue;
                        }

                        // Pixels times the widest range: a large spread of colours splits before a crowded narrow one.
                        var score = box.Sum(cell => _count[cell]) * (Range(box, WidestAxis(box)) + 1);
                        if (score > most)
                        {
                            most = score;
                            widest = box;
                        }
                    }

                    if (widest == null)
                    {
                        break;
                    }

                    var axis = WidestAxis(widest);
                    widest.Sort((a, b) => Channel(a, axis).CompareTo(Channel(b, axis)));
                    var half = widest.Sum(cell => _count[cell]) / 2;
                    long running = 0;
                    var split = 1;
                    for (; split < widest.Count - 1; split++)
                    {
                        running += _count[widest[split - 1]];
                        if (running >= half)
                        {
                            break;
                        }
                    }

                    boxes.Remove(widest);
                    boxes.Add(widest.GetRange(0, split));
                    boxes.Add(widest.GetRange(split, widest.Count - split));
                }

                groups.AddRange(boxes);
            }

            // Each entry is its cells' pixel-weighted average colour.
            var palette = new byte[groups.Count * 3];
            var filled = new bool[Cells];
            for (var index = 0; index < groups.Count; index++)
            {
                long n = 0, r = 0, g = 0, b = 0;
                foreach (var cell in groups[index])
                {
                    n += _count[cell];
                    r += _sumR[cell];
                    g += _sumG[cell];
                    b += _sumB[cell];
                    if (index < reserved)
                    {
                        lookup[cell] = (byte)index;
                        filled[cell] = true;
                    }
                }

                palette[index * 3] = (byte)((r + (n / 2)) / n);
                palette[(index * 3) + 1] = (byte)((g + (n / 2)) / n);
                palette[(index * 3) + 2] = (byte)((b + (n / 2)) / n);
            }

            // A few rounds of k-means: each shared entry moves to the average of the cells nearest to it, which
            // spreads the entries more evenly than median cut alone. Reserved entries stay where they are.
            var occupiedRest = occupied.Skip(reserved).ToArray();
            for (var round = 0; round < 4 && occupiedRest.Length > 0; round++)
            {
                var n = new long[groups.Count];
                var r = new long[groups.Count];
                var g = new long[groups.Count];
                var b = new long[groups.Count];
                foreach (var cell in occupiedRest)
                {
                    var index = this.Nearest(palette, groups.Count, cell);
                    n[index] += _count[cell];
                    r[index] += _sumR[cell];
                    g[index] += _sumG[cell];
                    b[index] += _sumB[cell];
                }

                for (var index = reserved; index < groups.Count; index++)
                {
                    if (n[index] > 0)
                    {
                        palette[index * 3] = (byte)((r[index] + (n[index] / 2)) / n[index]);
                        palette[(index * 3) + 1] = (byte)((g[index] + (n[index] / 2)) / n[index]);
                        palette[(index * 3) + 2] = (byte)((b[index] + (n[index] / 2)) / n[index]);
                    }
                }
            }

            // Every other cell gets the entry nearest to its average colour (or its middle, if never sampled),
            // which is closer than its median-cut group's average at the group's edges.
            for (var cell = 0; cell < Cells; cell++)
            {
                if (filled[cell])
                {
                    continue;
                }

                var best = this.Nearest(palette, groups.Count, cell);
                lookup[cell] = (byte)best;
            }

            return palette;
        }

        /// <summary>The entry nearest to a cell's average colour, or to its middle if it was never sampled.</summary>
        private int Nearest(byte[] palette, int entries, int cell)
        {
            long r, g, b;
            if (_count[cell] > 0)
            {
                r = _sumR[cell] / _count[cell];
                g = _sumG[cell] / _count[cell];
                b = _sumB[cell] / _count[cell];
            }
            else
            {
                r = (((cell >> (2 * Bits)) & (Levels - 1)) << Shift) + (1 << (Shift - 1));
                g = (((cell >> Bits) & (Levels - 1)) << Shift) + (1 << (Shift - 1));
                b = ((cell & (Levels - 1)) << Shift) + (1 << (Shift - 1));
            }

            var best = 0;
            var bestDistance = long.MaxValue;
            for (var index = 0; index < entries; index++)
            {
                var dr = palette[index * 3] - r;
                var dg = palette[(index * 3) + 1] - g;
                var db = palette[(index * 3) + 2] - b;
                var distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = index;
                }
            }

            return best;
        }

        /// <summary>Palette indexes for <paramref name="pixels"/> RGB pixels.</summary>
        public static void Map(byte[] lookup, byte[] rgb, int rgbOffset, byte[] indexes, int indexOffset, int pixels)
        {
            for (var p = 0; p < pixels; p++)
            {
                var i = rgbOffset + (p * 3);
                indexes[indexOffset + p] = lookup[Cell(rgb[i], rgb[i + 1], rgb[i + 2])];
            }
        }

        private static int WidestAxis(List<int> box)
        {
            var axis = 0;
            var widest = -1;
            for (var a = 0; a < 3; a++)
            {
                var range = Range(box, a);
                if (range > widest)
                {
                    widest = range;
                    axis = a;
                }
            }

            return axis;
        }

        private static int Range(List<int> box, int axis)
        {
            var min = int.MaxValue;
            var max = int.MinValue;
            foreach (var cell in box)
            {
                var value = Channel(cell, axis);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            return max - min;
        }

        private static int Channel(int cell, int axis)
        {
            return (cell >> ((2 - axis) * Bits)) & (Levels - 1);
        }
    }
}
