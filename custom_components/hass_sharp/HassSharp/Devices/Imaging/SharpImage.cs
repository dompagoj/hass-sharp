using System.Buffers;

namespace HassSharp;

/// <summary>
/// A JPEG picture and the rectangles and text to draw on it. <see cref="Load"/> keeps the bytes; <see cref="DrawRect"/>
/// and <see cref="DrawText"/> add to a list and return the same image; <see cref="ToJpeg"/> decodes the picture, draws
/// the list on it in order and encodes it once — and gives back the bytes it was loaded from when nothing was drawn, so a
/// picture passed on unchanged needs no codec. Decoding and encoding need the system's libjpeg-turbo library
/// (<c>libturbojpeg</c>). A picture is at most 20 MiB and 40 megapixels and carries at most 500 drawings. Drawing outside
/// the picture is clipped, never an error.
/// </summary>
public sealed class SharpImage : IVisualSnapshot
{
    const int MaxBytes = 20 * 1024 * 1024;
    const long MaxPixels = 40_000_000;
    const int MaxSide = 8192;
    const int MaxDrawings = 500;
    const int MaxTextLength = 200;
    const int MinTextSize = 8, MaxTextSize = 200;
    const int MinThickness = 1, MaxThickness = 50;
    const int Quality = 80;

    abstract record Drawing;
    sealed record Rectangle(int X, int Y, int Width, int Height, SharpColor Color, int Thickness) : Drawing;
    sealed record Label(string Text, int X, int Y, SharpColor Color, int Size, bool Background) : Drawing;

    // What Load was given; null for a blank picture, which is all of one colour.
    readonly byte[]? jpeg;
    readonly SharpColor fill;
    readonly List<Drawing> drawings;
    // The size, 0 until read from the header.
    int width, height;
    // The last ToJpeg with drawings, until the next drawing.
    byte[]? encoded;

    SharpImage(byte[]? jpeg, SharpColor fill, List<Drawing> drawings, int width, int height, byte[]? encoded)
    {
        this.jpeg = jpeg;
        this.fill = fill;
        this.drawings = drawings;
        this.width = width;
        this.height = height;
        this.encoded = encoded;
    }

    /// <summary>
    /// A picture from the bytes of a JPEG, which are kept as they are (not copied): do not change the array afterwards.
    /// Only the first bytes are looked at; a damaged picture is found when it is decoded.
    /// </summary>
    /// <param name="jpeg">The JPEG, at most 20 MiB.</param>
    /// <exception cref="ArgumentException">The bytes are not a JPEG, or are more than 20 MiB.</exception>
    public static SharpImage Load(byte[] jpeg)
    {
        if (jpeg is not [0xFF, 0xD8, ..]) throw new ArgumentException("This is not a JPEG picture");
        if (jpeg.Length > MaxBytes) throw new ArgumentException("The picture is larger than 20 MiB");
        return new(jpeg, default, [], 0, 0, null);
    }

    /// <summary>A plain picture of one colour.</summary>
    /// <param name="width">Its width in pixels, 1–8192.</param>
    /// <param name="height">Its height in pixels, 1–8192.</param>
    /// <param name="color">Its colour; black when not given.</param>
    /// <exception cref="ArgumentException">A side is outside 1–8192, or the picture is over 40 megapixels.</exception>
    public static SharpImage Blank(int width, int height, SharpColor? color = null)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide) throw new ArgumentException("A blank picture is 1 to 8192 pixels on each side");
        if ((long)width * height > MaxPixels) throw new ArgumentException("The picture is larger than 40 megapixels");
        return new(null, color ?? SharpColor.Black, [], width, height, null);
    }

    /// <summary>The width in pixels, read from the picture's header the first time.</summary>
    /// <exception cref="InvalidOperationException">libjpeg-turbo is missing.</exception>
    /// <exception cref="System.IO.InvalidDataException">The header cannot be read.</exception>
    public int Width { get { ReadSize(); return width; } }

    /// <summary>The height in pixels, read from the picture's header the first time.</summary>
    /// <exception cref="InvalidOperationException">libjpeg-turbo is missing.</exception>
    /// <exception cref="System.IO.InvalidDataException">The header cannot be read.</exception>
    public int Height { get { ReadSize(); return height; } }

    /// <summary>How many drawings the image carries.</summary>
    public int Drawings => drawings.Count;

    /// <summary>Adds the outline of a rectangle; returns this image.</summary>
    /// <param name="x">Its left edge, in pixels from the left of the picture.</param>
    /// <param name="y">Its top edge, in pixels from the top of the picture.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="color">The colour of its line; red when not given.</param>
    /// <param name="thickness">How wide its line is, 1–50 pixels, drawn inside its edges.</param>
    /// <exception cref="ArgumentException">The thickness is outside 1–50, or the image already carries 500 drawings.</exception>
    public SharpImage DrawRect(int x, int y, int width, int height, SharpColor? color = null, int thickness = 2)
    {
        if (thickness is < MinThickness or > MaxThickness) throw new ArgumentException("A rectangle's line is 1 to 50 pixels thick");
        return Add(new Rectangle(x, y, width, height, color ?? SharpColor.Red, thickness));
    }

    /// <summary>Adds a line of text in DejaVu Sans; returns this image.</summary>
    /// <param name="text">The text, at most 200 characters.</param>
    /// <param name="x">The left of the text, in pixels from the left of the picture.</param>
    /// <param name="y">The top of the text, in pixels from the top of the picture.</param>
    /// <param name="color">Its colour; red when not given.</param>
    /// <param name="size">Its size in pixels, 8–200.</param>
    /// <param name="background">Whether a translucent black box goes behind it, so it reads on any picture.</param>
    /// <exception cref="ArgumentException">There is no text, it is over 200 characters, its size is outside 8–200, or the image already carries 500 drawings.</exception>
    public SharpImage DrawText(string text, int x, int y, SharpColor? color = null, int size = 16, bool background = true)
    {
        if (text == null) throw new ArgumentException("There is no text to draw");
        if (text.Length > MaxTextLength) throw new ArgumentException("A text is at most 200 characters");
        if (size is < MinTextSize or > MaxTextSize) throw new ArgumentException("A text's size is 8 to 200 pixels");
        return Add(new Label(text, x, y, color ?? SharpColor.Red, size, background));
    }

    SharpImage Add(Drawing drawing)
    {
        if (drawings.Count >= MaxDrawings) throw new ArgumentException("An image carries at most 500 drawings");
        drawings.Add(drawing);
        encoded = null;
        return this;
    }

    /// <summary>
    /// The picture as a JPEG. With nothing drawn, the bytes it was loaded from (the same array: do not change it).
    /// Otherwise the picture is decoded, the drawings are drawn in order and it is encoded at quality 80, which is kept
    /// until the next drawing (the same array again: do not change it).
    /// </summary>
    /// <exception cref="InvalidOperationException">libjpeg-turbo is missing, and something is drawn or the picture is blank.</exception>
    /// <exception cref="ArgumentException">The picture is over 40 megapixels; refused before its pixels are decoded.</exception>
    /// <exception cref="System.IO.InvalidDataException">The picture cannot be read. A truncated or damaged one is drawn on as far as it decodes.</exception>
    public byte[] ToJpeg()
    {
        if (drawings.Count == 0 && jpeg != null) return jpeg;
        if (encoded != null) return encoded;
        if (!TurboJpeg.Available) throw new InvalidOperationException(TurboJpeg.Missing);
        ReadSize();
        if ((long)width * height > MaxPixels) throw new ArgumentException("The picture is larger than 40 megapixels");
        var length = width * height * 3;
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var rgb = pixels.AsSpan(0, length);
            if (jpeg != null) TurboJpeg.Decode(jpeg, rgb, width, height);
            else ImagePainter.Fill(rgb, width, height, fill);
            foreach (var drawing in drawings)
                switch (drawing)
                {
                    case Rectangle r: ImagePainter.Rect(rgb, width, height, r.X, r.Y, r.Width, r.Height, r.Color, r.Thickness); break;
                    case Label l: ImagePainter.Text(rgb, width, height, l.Text, l.X, l.Y, l.Color, l.Size, l.Background); break;
                }
            return encoded = TurboJpeg.Encode(rgb, width, height, Quality);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    /// <summary>A copy with the same picture (its bytes shared) and its own list of drawings, the same ones so far.</summary>
    public SharpImage Copy() => new(jpeg, fill, [.. drawings], width, height, encoded);

    object IVisualSnapshot.CreateVisualSnapshot() => Copy();

    void ReadSize()
    {
        if (width != 0) return;
        TurboJpeg.Header(jpeg!, out var w, out var h);
        (width, height) = (w, h);
    }
}
