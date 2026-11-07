using System;
using Assets.Scripts;

/// <summary>Offline tests of the 256-colour palette for program images.</summary>
public static class PaletteQuantizerTests
{
    public static void Run(Action<string, bool> check)
    {
        // A background, a few block colours and white text, with shaded edges between them, as in the editor.
        var solid = new[] { (40, 40, 40), (58, 150, 220), (70, 200, 80), (230, 170, 40), (255, 255, 255), (170, 90, 210) };
        var random = new Random(7);
        const int pixels = 200000;
        var rgb = new byte[pixels * 3];
        for (var p = 0; p < pixels; p++)
        {
            var (r, g, b) = solid[p % 50 == 0 ? 1 + (p / 50 % 5) : 0];
            if (p % 7 == 0)
            {
                // An edge: a blend of the background and a block colour.
                var (r2, g2, b2) = solid[1 + random.Next(5)];
                var t = random.NextDouble();
                r = (int)(r + ((r2 - r) * t));
                g = (int)(g + ((g2 - g) * t));
                b = (int)(b + ((b2 - b) * t));
            }

            rgb[p * 3] = (byte)r;
            rgb[(p * 3) + 1] = (byte)g;
            rgb[(p * 3) + 2] = (byte)b;
        }

        var quantizer = new PaletteQuantizer();
        quantizer.Sample(rgb, 0, pixels, 1);
        var palette = quantizer.Build(256, out var lookup);
        check("Palette: at most 256 colours (" + (palette.Length / 3) + ")", palette.Length <= 256 * 3 && palette.Length % 3 == 0);

        var indexes = new byte[pixels];
        PaletteQuantizer.Map(lookup, rgb, 0, indexes, 0, pixels);
        var exact = true;
        var worst = 0;
        var far = 0;
        for (var p = 0; p < pixels; p++)
        {
            var i = indexes[p] * 3;
            var dr = Math.Abs(palette[i] - rgb[p * 3]);
            var dg = Math.Abs(palette[i + 1] - rgb[(p * 3) + 1]);
            var db = Math.Abs(palette[i + 2] - rgb[(p * 3) + 2]);
            worst = Math.Max(worst, Math.Max(dr, Math.Max(dg, db)));
            if (Math.Max(dr, Math.Max(dg, db)) > 12)
            {
                far++;
            }
            if (p % 7 != 0 && (dr | dg | db) != 0)
            {
                exact = false;
            }
        }

        check("Palette: the background and block colours stay exact", exact);
        // Rare shades (a blend of two block colours) get fewer entries, so a little more error is allowed there.
        check("Palette: shaded edges stay close (worst " + worst + " of 255, " + far + " pixels over 12)", worst <= 40 && far <= pixels / 7 / 100);

        var few = new PaletteQuantizer();
        few.Sample(new byte[] { 1, 2, 3, 4, 5, 6, 1, 2, 3 }, 0, 3, 1);
        var small = few.Build(256, out var smallLookup);
        check("Palette: two colours give two entries", small.Length == 6);
    }
}
