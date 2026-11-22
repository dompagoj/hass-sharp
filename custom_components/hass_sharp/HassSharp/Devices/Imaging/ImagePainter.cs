using static StbTrueTypeSharp.StbTrueType;

namespace HassSharp;

/// <summary>
/// Draws on RGB pixels (three bytes each, rows packed): rectangle outlines, circles filled or outlined, and text in
/// DejaVu Sans with a translucent box behind it. Whatever falls outside the picture is clipped; coordinates are taken as
/// 64-bit so that none overflows.
/// Text is rasterised by StbTrueTypeSharp from the embedded font only: a font file it cannot read can crash the
/// process, so it never sees any other.
/// </summary>
internal static unsafe class ImagePainter
{
    const int CacheLimit = 2000;
    const int BackgroundAlpha = 160;

    static readonly stbtt_fontinfo font = LoadFont();
    static readonly int ascent, descent;
    // The glyph bitmaps drawn so far, by glyph and size; emptied when full. Also guards rasterising.
    static readonly Dictionary<(int Glyph, int Size), Glyph> glyphs = [];

    sealed record Glyph(byte[] Coverage, int Width, int Height, int Left, int Top, float Advance);

    static ImagePainter()
    {
        int a, d, gap;
        stbtt_GetFontVMetrics(font, &a, &d, &gap);
        (ascent, descent) = (a, d);
    }

    static stbtt_fontinfo LoadFont()
    {
        using var stream = typeof(ImagePainter).Assembly.GetManifestResourceStream("HassSharp.DejaVuSans.ttf")
            ?? throw new InvalidOperationException("The embedded font is missing");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        // CreateFont copies the font into native memory it keeps for the process.
        return CreateFont(bytes, 0) ?? throw new InvalidOperationException("The embedded font cannot be read");
    }

    /// <summary>Paints every pixel one colour.</summary>
    internal static void Fill(Span<byte> rgb, int width, int height, SharpColor color) =>
        FillBox(rgb, width, height, 0, 0, width, height, color);

    /// <summary>
    /// Shrinks a picture into a smaller one, each pixel the average of the pixels of the source it covers (a box filter):
    /// <paramref name="to"/> is <paramref name="toWidth"/> × <paramref name="toHeight"/>, at most the source's size on each side.
    /// </summary>
    internal static void Shrink(ReadOnlySpan<byte> from, int width, int height, Span<byte> to, int toWidth, int toHeight)
    {
        for (var y = 0; y < toHeight; y++)
        {
            int top = (int)((long)y * height / toHeight), bottom = Math.Max(top + 1, (int)((long)(y + 1) * height / toHeight));
            for (var x = 0; x < toWidth; x++)
            {
                int left = (int)((long)x * width / toWidth), right = Math.Max(left + 1, (int)((long)(x + 1) * width / toWidth));
                int r = 0, g = 0, b = 0;
                for (var sy = top; sy < bottom; sy++)
                {
                    var row = from.Slice((sy * width + left) * 3, (right - left) * 3);
                    for (var i = 0; i < row.Length; i += 3) { r += row[i]; g += row[i + 1]; b += row[i + 2]; }
                }
                var count = (bottom - top) * (right - left);
                var at = (y * toWidth + x) * 3;
                (to[at], to[at + 1], to[at + 2]) = ((byte)((r + count / 2) / count), (byte)((g + count / 2) / count), (byte)((b + count / 2) / count));
            }
        }
    }

    /// <summary>The outline of a rectangle, its line <paramref name="thickness"/> wide inside its edges.</summary>
    internal static void Rect(Span<byte> rgb, int width, int height, int x, int y, int w, int h, SharpColor color, int thickness)
    {
        if (w <= 0 || h <= 0) return;
        long left = x, top = y, right = left + w, bottom = top + h;
        FillBox(rgb, width, height, left, top, right, Math.Min(bottom, top + thickness), color);
        FillBox(rgb, width, height, left, Math.Max(top, bottom - thickness), right, bottom, color);
        FillBox(rgb, width, height, left, top + thickness, Math.Min(right, left + thickness), bottom - thickness, color);
        FillBox(rgb, width, height, Math.Max(left, right - thickness), top + thickness, right, bottom - thickness, color);
    }

    /// <summary>
    /// A circle about <paramref name="x"/>, <paramref name="y"/>: the pixels no farther than <paramref name="radius"/> from
    /// its centre, and for an outline only those farther than <c>radius - thickness</c> too, so that the line is drawn
    /// inwards and a line as wide as the radius is a disc. Painted a row at a time, over the rows the picture has of it.
    /// </summary>
    internal static void Circle(Span<byte> rgb, int width, int height, int x, int y, int radius, SharpColor color, bool filled, int thickness)
    {
        if (radius <= 0) return;
        long cx = x, cy = y, outer = (long)radius * radius;
        long inner = filled || thickness >= radius ? -1 : (long)(radius - thickness) * (radius - thickness);
        long top = Math.Max(cy - radius, 0), bottom = Math.Min(cy + radius, height - 1L);
        for (var row = top; row <= bottom; row++)
        {
            var dy = (row - cy) * (row - cy);
            // How far from the centre the row is painted; for an outline, how far it is left as it was inside the line.
            var reach = FloorSqrt(outer - dy);
            if (inner < dy) { FillBox(rgb, width, height, cx - reach, row, cx + reach + 1, row + 1, color); continue; }
            var hole = FloorSqrt(inner - dy);
            FillBox(rgb, width, height, cx - reach, row, cx - hole, row + 1, color);
            FillBox(rgb, width, height, cx + hole + 1, row, cx + reach + 1, row + 1, color);
        }
    }

    // The largest whole number whose square is at most n, of 0 ≤ n.
    static long FloorSqrt(long n)
    {
        var root = (long)Math.Sqrt(n);
        while (root * root > n) root--;
        while ((root + 1) * (root + 1) <= n) root++;
        return root;
    }

    /// <summary>
    /// The pixels a text drawn at <paramref name="x"/>, <paramref name="y"/> (the top left of its line) can change: its
    /// line and every glyph's bitmap, and with <paramref name="background"/> a margin around them.
    /// </summary>
    internal static (long X, long Y, long Width, long Height) TextBox(string text, int x, int y, int size, bool background)
    {
        var (left, top, right, bottom) = Extent(text, size);
        var pad = background ? Padding(size) : 0;
        return (x + left - pad, y + top - pad, right - left + 2 * pad, bottom - top + 2 * pad);
    }

    // The character a rune is drawn as: a control character (a newline, a tab, any below U+0020, and U+007F) as a space,
    // which the font draws as nothing, rather than as its empty box.
    static int Drawn(System.Text.Rune rune) => rune.Value < 0x20 || rune.Value == 0x7F ? ' ' : rune.Value;

    /// <summary>Draws a line of text with the top left of its line at <paramref name="x"/>, <paramref name="y"/>; a control character as a space.</summary>
    internal static void Text(Span<byte> rgb, int width, int height, string text, int x, int y, SharpColor color, int size, bool background)
    {
        if (text.Length == 0) return;
        if (background)
        {
            var box = TextBox(text, x, y, size, background);
            BlendBox(rgb, width, height, box.X, box.Y, box.X + box.Width, box.Y + box.Height, SharpColor.Black, BackgroundAlpha);
        }
        var scale = stbtt_ScaleForMappingEmToPixels(font, size);
        var baseline = (long)y + (int)MathF.Round(ascent * scale);
        var pen = 0f;
        var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var index = stbtt_FindGlyphIndex(font, Drawn(rune));
            if (previous >= 0) pen += stbtt_GetGlyphKernAdvance(font, previous, index) * scale;
            var glyph = GlyphOf(index, size, scale);
            Blend(rgb, width, height, glyph, x + (long)MathF.Round(pen) + glyph.Left, baseline + glyph.Top, color);
            pen += glyph.Advance;
            previous = index;
        }
    }

    static int Padding(int size) => Math.Max(2, size / 6);

    // The text's extent relative to the top left of its line, laid out as Text lays it out.
    static (long Left, long Top, long Right, long Bottom) Extent(string text, int size)
    {
        var scale = stbtt_ScaleForMappingEmToPixels(font, size);
        var baseline = (int)MathF.Round(ascent * scale);
        long left = 0, top = 0, right = 0, bottom = (int)MathF.Ceiling((ascent - descent) * scale);
        var pen = 0f;
        var previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            var index = stbtt_FindGlyphIndex(font, Drawn(rune));
            if (previous >= 0) pen += stbtt_GetGlyphKernAdvance(font, previous, index) * scale;
            var glyph = GlyphOf(index, size, scale);
            if (glyph.Width > 0 && glyph.Height > 0)
            {
                var gx = (long)MathF.Round(pen) + glyph.Left;
                var gy = baseline + glyph.Top;
                (left, top) = (Math.Min(left, gx), Math.Min(top, gy));
                (right, bottom) = (Math.Max(right, gx + glyph.Width), Math.Max(bottom, gy + glyph.Height));
            }
            pen += glyph.Advance;
            previous = index;
        }
        return (left, top, Math.Max(right, (long)MathF.Ceiling(pen)), bottom);
    }

    static Glyph GlyphOf(int index, int size, float scale)
    {
        lock (glyphs)
        {
            if (glyphs.TryGetValue((index, size), out var cached)) return cached;
            int x0, y0, x1, y1, advance, bearing;
            stbtt_GetGlyphBitmapBox(font, index, scale, scale, &x0, &y0, &x1, &y1);
            stbtt_GetGlyphHMetrics(font, index, &advance, &bearing);
            int w = Math.Max(0, x1 - x0), h = Math.Max(0, y1 - y0);
            var coverage = new byte[w * h];
            if (w > 0 && h > 0)
                fixed (byte* output = coverage) stbtt_MakeGlyphBitmap(font, output, w, h, w, scale, scale, index);
            var glyph = new Glyph(coverage, w, h, x0, y0, advance * scale);
            if (glyphs.Count >= CacheLimit) glyphs.Clear();
            glyphs[(index, size)] = glyph;
            return glyph;
        }
    }

    // The part of [left, right) × [top, bottom) inside the picture, or false when none is.
    static bool Clip(int width, int height, ref long left, ref long top, ref long right, ref long bottom)
    {
        (left, top, right, bottom) = (Math.Max(left, 0), Math.Max(top, 0), Math.Min(right, width), Math.Min(bottom, height));
        return left < right && top < bottom;
    }

    static void FillBox(Span<byte> rgb, int width, int height, long left, long top, long right, long bottom, SharpColor color)
    {
        if (!Clip(width, height, ref left, ref top, ref right, ref bottom)) return;
        for (var row = (int)top; row < bottom; row++)
        {
            var line = rgb.Slice((row * width + (int)left) * 3, ((int)right - (int)left) * 3);
            for (var i = 0; i < line.Length; i += 3) (line[i], line[i + 1], line[i + 2]) = (color.R, color.G, color.B);
        }
    }

    static void BlendBox(Span<byte> rgb, int width, int height, long left, long top, long right, long bottom, SharpColor color, int alpha)
    {
        if (!Clip(width, height, ref left, ref top, ref right, ref bottom)) return;
        for (var row = (int)top; row < bottom; row++)
        {
            var line = rgb.Slice((row * width + (int)left) * 3, ((int)right - (int)left) * 3);
            for (var i = 0; i < line.Length; i += 3) Mix(line.Slice(i, 3), color, alpha);
        }
    }

    // A glyph's coverage blended in the colour, its bitmap's top left at gx, gy.
    static void Blend(Span<byte> rgb, int width, int height, Glyph glyph, long gx, long gy, SharpColor color)
    {
        long left = gx, top = gy, right = gx + glyph.Width, bottom = gy + glyph.Height;
        if (!Clip(width, height, ref left, ref top, ref right, ref bottom)) return;
        for (var row = top; row < bottom; row++)
        {
            var coverage = glyph.Coverage.AsSpan((int)(row - gy) * glyph.Width, glyph.Width);
            for (var column = left; column < right; column++)
            {
                int alpha = coverage[(int)(column - gx)];
                if (alpha != 0) Mix(rgb.Slice(((int)row * width + (int)column) * 3, 3), color, alpha);
            }
        }
    }

    static void Mix(Span<byte> pixel, SharpColor color, int alpha)
    {
        if (alpha == 255) { (pixel[0], pixel[1], pixel[2]) = (color.R, color.G, color.B); return; }
        pixel[0] = (byte)(pixel[0] + ((color.R - pixel[0]) * alpha + 127) / 255);
        pixel[1] = (byte)(pixel[1] + ((color.G - pixel[1]) * alpha + 127) / 255);
        pixel[2] = (byte)(pixel[2] + ((color.B - pixel[2]) * alpha + 127) / 255);
    }
}
