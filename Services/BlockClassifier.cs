namespace Nighty.Services;

public enum ToolKind { None, Pickaxe, Axe, Shears }

/// <summary>
/// Looks at a small square of screen pixels (the block under the crosshair) and says which tool breaks it.
/// A block is reduced to a colour "signature": a histogram over 65 coarse colour bins plus one texture number.
/// Signatures the user taught are compared by histogram overlap. Wool comes in every colour, so Shears are matched
/// ignoring hue and colour brightness (only dark / grey / vivid / muted and texture), so one taught wool block covers many colours.
/// Pure maths on a byte array, so it is easy to test.
/// </summary>
public static class BlockClassifier
{
    // Bin layout: 0-11 dark by hue, 12 dark grey, 13-16 neutral by brightness, 17-64 colour (hue 12 x saturation 2 x brightness 2).
    public const int Bins = 65;
    /// <summary>Signature length: the histogram, then the texture value.</summary>
    public const int Length = Bins + 1;
    private const int AgnosticBins = 7;

    /// <summary>BGRA pixels (as GetDIBits returns them), size x size. Pixels within skipRadius of the centre are ignored (the crosshair).</summary>
    public static float[]? Signature(byte[] bgra, int w, int h, int skipRadius)
    {
        if (bgra.Length < w * h * 4 || w < 4 || h < 4) return null;
        var hist = new float[Length];
        int count = 0;
        double texSum = 0; int texN = 0;
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        for (int y = 0; y < h; y++)
        {
            float prevV = -1;
            for (int x = 0; x < w; x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= skipRadius * skipRadius) { prevV = -1; continue; }
                int i = (y * w + x) * 4;
                float v = Bin(bgra[i + 2], bgra[i + 1], bgra[i], out int bin);
                hist[bin]++; count++;
                if (prevV >= 0) { texSum += Math.Abs(v - prevV); texN++; }
                prevV = v;
            }
        }
        if (count == 0) return null;
        for (int b = 0; b < Bins; b++) hist[b] /= count;
        hist[Bins] = texN > 0 ? (float)(texSum / texN) : 0;
        return hist;
    }

    private static float Bin(byte r, byte g, byte b, out int bin)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float v = max / 255f, s = max == 0 ? 0 : (max - min) / (float)max;
        int hueBin = 0;
        if (max != min)
        {
            float d = max - min, hue;
            if (max == r) hue = ((g - b) / d) % 6;
            else if (max == g) hue = (b - r) / d + 2;
            else hue = (r - g) / d + 4;
            hue *= 60; if (hue < 0) hue += 360;
            hueBin = (int)(hue / 30) % 12;
        }
        if (v < 0.22f) bin = s > 0.25f ? hueBin : 12;
        else if (s < 0.2f) bin = 13 + Math.Min(3, (int)(v * 4));
        else bin = 17 + hueBin * 4 + (s >= 0.55f ? 2 : 0) + (v >= 0.6f ? 1 : 0);
        return v;
    }

    /// <summary>Histogram overlap (0-1), lowered when the textures differ.</summary>
    public static double Similarity(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < Bins; i++) sum += Math.Min(a[i], b[i]);
        return sum * TexturePenalty(a, b);
    }

    /// <summary>Like <see cref="Similarity"/> but hue is ignored: every colour of wool looks the same to it.</summary>
    public static double SimilarityAnyHue(float[] a, float[] b)
    {
        Span<float> x = stackalloc float[AgnosticBins], y = stackalloc float[AgnosticBins];
        Collapse(a, x); Collapse(b, y);
        double sum = 0;
        for (int i = 0; i < AgnosticBins; i++) sum += Math.Min(x[i], y[i]);
        return sum * TexturePenalty(a, b);
    }

    /// <summary>For flat blocks (beds): any colour at all counts as the same, only dark / grey / coloured remain.</summary>
    public static double SimilarityFlat(float[] a, float[] b)
    {
        Span<float> x = stackalloc float[AgnosticBins], y = stackalloc float[AgnosticBins];
        Collapse(a, x); Collapse(b, y);
        x[5] += x[6]; x[6] = 0; y[5] += y[6]; y[6] = 0;
        double sum = 0;
        for (int i = 0; i < AgnosticBins; i++) sum += Math.Min(x[i], y[i]);
        return sum * TexturePenalty(a, b);
    }

    private static void Collapse(float[] h, Span<float> o)
    {
        for (int i = 0; i < 13; i++) o[0] += h[i];
        for (int i = 0; i < 4; i++) o[1 + i] = h[13 + i];
        for (int i = 17; i < Bins; i++) o[5 + ((i - 17) % 4 >= 2 ? 1 : 0)] += h[i];   // colour: only vivid vs muted survives
    }

    private static double TexturePenalty(float[] a, float[] b) => 1 - Math.Min(0.8, Math.Abs(a[Bins] - b[Bins]) * 25);

    /// <summary>Built-in rule that needs no teaching: obsidian is nearly black with a purple tint. Returns the share of matching pixels.</summary>
    public static double ObsidianShare(float[] sig) => sig[8] + sig[9];

    public const double ObsidianNeeded = 0.55;
    /// <summary>Texture below this counts as a flat, smooth surface (a bed); wool, stone and wood are all above it.</summary>
    public const float FlatTexture = 0.003f;

    /// <summary>
    /// Picks the tool for a signature, or None when nothing is close enough. <paramref name="strictness"/> is the overlap needed for
    /// pickaxe/axe matches (0.5-0.9); wool needs a little more because it ignores hue.
    /// </summary>
    public static ToolKind Classify(float[] sig, IReadOnlyList<float[]> pick, IReadOnlyList<float[]> axe, IReadOnlyList<float[]> shears, double strictness)
    {
        double needExact = Math.Clamp(strictness, 0.4, 0.95), needWool = Math.Min(0.97, needExact + 0.18);
        double bestMargin = -1; var best = ToolKind.None;
        // flatAnyHue: a taught block with no texture (a bed) also matches in any colour, since beds come in every team colour.
        void Try(ToolKind kind, IReadOnlyList<float[]> list, bool anyHue, bool flatAnyHue = false)
        {
            foreach (var s in list)
            {
                if (s.Length != Length) continue;
                double margin = anyHue ? SimilarityAnyHue(sig, s) - needWool : Similarity(sig, s) - needExact;
                if (flatAnyHue && s[Bins] < FlatTexture && sig[Bins] < FlatTexture)
                    margin = Math.Max(margin, SimilarityFlat(sig, s) - needWool);
                if (margin >= 0 && margin > bestMargin) { bestMargin = margin; best = kind; }
            }
        }
        Try(ToolKind.Pickaxe, pick, false);
        Try(ToolKind.Axe, axe, false, flatAnyHue: true);
        Try(ToolKind.Shears, shears, true);
        if (best != ToolKind.None) return best;
        return ObsidianShare(sig) >= ObsidianNeeded ? ToolKind.Pickaxe : ToolKind.None;
    }
}
