using System.Buffers;

namespace HassSharp;

/// <summary>
/// A picture and the rectangles and text to draw on it: a JPEG, a blank picture, or a frame of pixels in a pooled buffer
/// (<see cref="PixelFrame"/>, whose size is the frame's) — a frame of a live feed or of a video (<see cref="SharpVideo"/>),
/// or one made of pixels in code (<see cref="FromRgb"/>, <see cref="FromYuv420"/>, <see cref="Writer"/> and the others),
/// kept in the layout it came in. <see cref="CopyPixels"/> reads any picture's pixels. <see cref="Load"/> keeps the bytes; <see cref="DrawRect"/>
/// and <see cref="DrawText"/> add to a list and return the same image; <see cref="ToJpeg"/> decodes the picture, draws
/// the list on it in order and encodes it once — and gives back the bytes it was loaded from when nothing was drawn, so a
/// picture passed on unchanged needs no codec. Decoding and encoding need the system's libjpeg-turbo library
/// (<c>libturbojpeg</c>). A picture is at most 20 MiB and 40 megapixels and carries at most 500 drawings. Drawing outside
/// the picture is clipped, never an error. A picture is for one flow at a time: it is not thread-safe, so hand another
/// flow or thread a <see cref="Copy"/>. A frame is encoded straight from its pixels when nothing is drawn on it (from its
/// YUV planes, or its RGB, BGR, RGBA or BGRA rows), and converted to RGB to be drawn on otherwise. Each image of a frame
/// holds one reference to its buffer, a copy one of its own, and shares its pixels, which are never copied: the buffer is
/// used again only once each has given its reference back (<see cref="Dispose"/>, which HassSharp calls for the images it
/// makes and is done with). Disposing is never needed: an image never disposed leaves its buffer to the garbage collector,
/// and disposing only lets the buffer go round sooner, which matters at 4K and many frames a second.
/// </summary>
public sealed class SharpImage : IVisualSnapshot, IDisposable
{
    const int MaxBytes = 20 * 1024 * 1024;
    const long MaxPixels = 40_000_000;
    const int MaxSide = 8192;
    const int MaxDrawings = 500;
    const int MaxTextLength = 200;
    const int MinTextSize = 8, MaxTextSize = 200;
    const int MinThickness = 1, MaxThickness = 50;
    const int Quality = 80;
    // The pixel buffers of the pictures decoded, kept for the next: two of each size up to 16 MiB (a picture of up to
    // about 5.6 megapixels), whichever thread asks, so that a pool thread does not keep one of its own for good; and one
    // up to 32 MiB (about 11 megapixels), so that a 4K camera's frames drawn on many times a second do not each make
    // one. A larger one is made for its picture alone.
    const int PooledBytes = 16 * 1024 * 1024, LargeBytes = 32 * 1024 * 1024;
    static readonly ArrayPool<byte> Buffers = ArrayPool<byte>.Create(maxArrayLength: PooledBytes, maxArraysPerBucket: 2);
    static byte[]? large;

    static byte[] Rent(int length)
    {
        if (length <= PooledBytes) return Buffers.Rent(length);
        if (length <= LargeBytes && Interlocked.Exchange(ref large, null) is { } kept) return kept;
        return GC.AllocateUninitializedArray<byte>(length <= LargeBytes ? LargeBytes : length);
    }

    static void Return(byte[] pixels)
    {
        if (pixels.Length <= PooledBytes) Buffers.Return(pixels);
        else if (pixels.Length == LargeBytes) Interlocked.CompareExchange(ref large, pixels, null);
    }

    abstract record Drawing;
    sealed record Rectangle(int X, int Y, int Width, int Height, SharpColor Color, int Thickness) : Drawing;
    sealed record Label(string Text, int X, int Y, SharpColor Color, int Size, bool Background) : Drawing;

    const string Released = "The pixels of this picture were given back (it was disposed), so they can no longer be read";

    // What Load was given; null for a blank picture, which is all of one colour, and for a frame.
    readonly byte[]? jpeg;
    // A frame's pixels while this image holds its reference; null once given back (Release), and for any other picture.
    PixelFrame? frame;
    // The size in bytes of a frame's pixels, given back or not; 0 for any other picture.
    readonly int frameBytes;
    readonly SharpColor fill;
    readonly List<Drawing> drawings;
    // The size, 0 until read from the header.
    int width, height;
    // The last ToJpeg with drawings, until the next drawing.
    byte[]? encoded;

    // An image of the frame, holding the reference the caller took for it.
    SharpImage(PixelFrame frame, List<Drawing> drawings, byte[]? encoded) : this(null, default, drawings, frame.Width, frame.Height, encoded)
    {
        this.frame = frame;
        frameBytes = frame.Length;
    }

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

    /// <summary>An image of a frame, holding the reference the caller took for it: <see cref="Release"/> gives it back.</summary>
    internal static SharpImage Of(PixelFrame frame) => new(frame, [], null);

    /// <summary>A picture of RGB pixels, copied: three bytes a pixel, red, green, blue, rows from the top.</summary>
    /// <param name="width">Its width in pixels, 1–8192.</param>
    /// <param name="height">Its height in pixels, 1–8192.</param>
    /// <param name="pixels">The rows, each <paramref name="stride"/> bytes apart; the last may end at its last pixel.</param>
    /// <param name="stride">The bytes from one row to the next, at least a row's; 0 for rows packed (<c>width * 3</c>).</param>
    /// <exception cref="ArgumentException">A side is outside 1–8192, the picture is over 40 megapixels, the stride is less than a row, or the pixels are too few.</exception>
    public static SharpImage FromRgb(int width, int height, ReadOnlySpan<byte> pixels, int stride = 0) => FromPixels(width, height, SharpPixelFormat.Rgb, pixels, stride);

    /// <summary>A picture of BGR pixels, copied: three bytes a pixel, blue, green, red, as OpenCV gives them.</summary>
    /// <inheritdoc cref="FromRgb" select="param|exception"/>
    public static SharpImage FromBgr(int width, int height, ReadOnlySpan<byte> pixels, int stride = 0) => FromPixels(width, height, SharpPixelFormat.Bgr, pixels, stride);

    /// <summary>A picture of RGBA pixels, copied: four bytes a pixel, red, green, blue and alpha, which a JPEG has none of.</summary>
    /// <inheritdoc cref="FromRgb" select="param|exception"/>
    public static SharpImage FromRgba(int width, int height, ReadOnlySpan<byte> pixels, int stride = 0) => FromPixels(width, height, SharpPixelFormat.Rgba, pixels, stride);

    /// <summary>A picture of BGRA pixels, copied: four bytes a pixel, blue, green, red and alpha, which a JPEG has none of.</summary>
    /// <inheritdoc cref="FromRgb" select="param|exception"/>
    public static SharpImage FromBgra(int width, int height, ReadOnlySpan<byte> pixels, int stride = 0) => FromPixels(width, height, SharpPixelFormat.Bgra, pixels, stride);

    /// <summary>
    /// A picture of pixels in the layout of <paramref name="format"/>, copied into a pooled frame; kept in that layout, so
    /// that nothing is converted until it is drawn on, read in another layout or encoded. A YUV 4:2:0 picture's three
    /// planes are one after the other, packed (<see cref="FromYuv420"/> takes them apart and with strides).
    /// </summary>
    /// <param name="width">Its width in pixels, 1–8192.</param>
    /// <param name="height">Its height in pixels, 1–8192.</param>
    /// <param name="format">The layout of <paramref name="pixels"/>.</param>
    /// <param name="pixels">The rows, each <paramref name="stride"/> bytes apart; the last may end at its last pixel.</param>
    /// <param name="stride">The bytes from one row to the next of a packed format, at least a row's; 0 for rows packed. Always 0 for YUV 4:2:0.</param>
    /// <exception cref="ArgumentException">A side is outside 1–8192, the picture is over 40 megapixels, the stride is less than a row (or not 0 for YUV 4:2:0), or the pixels are too few.</exception>
    public static SharpImage FromPixels(int width, int height, SharpPixelFormat format, ReadOnlySpan<byte> pixels, int stride = 0)
    {
        CheckSize(width, height);
        var bytes = PixelFrame.BytesPerPixel(format);
        if (bytes == 0)
        {
            if (stride != 0) throw new ArgumentException("YUV 4:2:0 pixels are packed planes, of no stride; give the planes with theirs to FromYuv420", nameof(stride));
            int luma = width * height, chroma = (width + 1) / 2 * ((height + 1) / 2);
            if (pixels.Length < luma + 2 * chroma) throw new ArgumentException($"A {width} × {height} picture of YUV 4:2:0 has {luma + 2 * chroma} bytes, not {pixels.Length}", nameof(pixels));
            return FromYuv420(width, height, pixels[..luma], pixels.Slice(luma, chroma), pixels.Slice(luma + chroma, chroma));
        }
        var frame = PixelFrame.Rent(width, height, format);
        try { Rows(pixels, stride, width * bytes, height, frame.Data.AsSpan(0, frame.Length), "The pixels"); }
        catch { frame.Release(); throw; }
        return Of(frame);
    }

    /// <summary>
    /// A picture of YUV 4:2:0 planes (I420, as a video decoder gives them), copied into a pooled frame: the Y plane of
    /// <paramref name="width"/> × <paramref name="height"/>, and the U and V planes of half of each rounded up. Encoded as a
    /// JPEG without being converted.
    /// </summary>
    /// <param name="width">Its width in pixels, 1–8192.</param>
    /// <param name="height">Its height in pixels, 1–8192.</param>
    /// <param name="y">The Y plane's rows, each <paramref name="yStride"/> bytes apart.</param>
    /// <param name="u">The U (Cb) plane's rows, each <paramref name="uStride"/> bytes apart.</param>
    /// <param name="v">The V (Cr) plane's rows, each <paramref name="vStride"/> bytes apart.</param>
    /// <param name="yStride">The bytes from one row of Y to the next; 0 for packed (<c>width</c>).</param>
    /// <param name="uStride">The bytes from one row of U to the next; 0 for packed (<c>(width + 1) / 2</c>).</param>
    /// <param name="vStride">The bytes from one row of V to the next; 0 for packed.</param>
    /// <param name="limitedRange">Whether the planes are of limited range (Y 16–235, U and V 16–240), as most video is; they are stretched to the full range a JPEG takes. False: full range already.</param>
    /// <exception cref="ArgumentException">A side is outside 1–8192, the picture is over 40 megapixels, a stride is less than a row, or a plane is too small.</exception>
    public static SharpImage FromYuv420(int width, int height, ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int yStride = 0, int uStride = 0, int vStride = 0, bool limitedRange = false)
    {
        CheckSize(width, height);
        var frame = PixelFrame.Rent(width, height);
        try
        {
            var data = frame.Data.AsSpan(0, frame.Length);
            Rows(y, yStride, width, height, data[..frame.LumaLength], "The Y plane");
            Rows(u, uStride, frame.ChromaWidth, frame.ChromaHeight, data.Slice(frame.LumaLength, frame.ChromaLength), "The U plane");
            Rows(v, vStride, frame.ChromaWidth, frame.ChromaHeight, data.Slice(frame.LumaLength + frame.ChromaLength, frame.ChromaLength), "The V plane");
        }
        catch { frame.Release(); throw; }
        if (limitedRange) frame.StretchToFullRange();
        return Of(frame);
    }

    /// <summary>
    /// A new picture whose pixels <paramref name="fill"/> writes straight into its pooled buffer, with no copy: for code
    /// that decodes or renders a frame itself, such as at 4K. The layout is <paramref name="format"/>'s, packed. For code
    /// that awaits while it writes, use <see cref="Writer"/>.
    /// </summary>
    /// <param name="width">Its width in pixels, 1–8192.</param>
    /// <param name="height">Its height in pixels, 1–8192.</param>
    /// <param name="format">The layout the pixels are written in.</param>
    /// <param name="fill">Writes every byte of the pixels it is given, which hold an earlier picture's until it does; not kept after it returns.</param>
    /// <exception cref="ArgumentException">A side is outside 1–8192, or the picture is over 40 megapixels.</exception>
    public static SharpImage Create(int width, int height, SharpPixelFormat format, SharpPixelFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        using var writer = Writer(width, height, format);
        fill(writer.Pixels.Span);
        return writer.ToImage();
    }

    /// <summary>
    /// A writer of a new picture's pixels, in a pooled buffer: write its <see cref="SharpFrameWriter.Pixels"/>, awaiting
    /// what it needs to, then <see cref="SharpFrameWriter.ToImage"/> makes it the picture without a copy. Dispose it, as with
    /// <c>using</c>, so that a picture never made gives its buffer back.
    /// </summary>
    /// <param name="width">Its width in pixels, 1–8192.</param>
    /// <param name="height">Its height in pixels, 1–8192.</param>
    /// <param name="format">The layout the pixels are written in.</param>
    /// <exception cref="ArgumentException">A side is outside 1–8192, or the picture is over 40 megapixels.</exception>
    public static SharpFrameWriter Writer(int width, int height, SharpPixelFormat format)
    {
        CheckSize(width, height);
        PixelFrame.BytesPerPixel(format);
        return new(PixelFrame.Rent(width, height, format));
    }

    /// <summary>The bytes the pixels of a picture of that size take in <paramref name="format"/>, packed.</summary>
    /// <exception cref="ArgumentException">A side is outside 1–8192, or the picture is over 40 megapixels.</exception>
    public static int PixelsLength(int width, int height, SharpPixelFormat format)
    {
        CheckSize(width, height);
        return PixelFrame.LengthOf(width, height, format);
    }

    /// <summary>The bytes this picture's pixels take in <paramref name="format"/>, packed: what <see cref="CopyPixels"/> writes.</summary>
    public int PixelsLength(SharpPixelFormat format = SharpPixelFormat.Rgb) => PixelFrame.LengthOf(Width, Height, format);

    static void CheckSize(int width, int height)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide) throw new ArgumentException("A picture is 1 to 8192 pixels on each side");
        if ((long)width * height > MaxPixels) throw new ArgumentException("The picture is larger than 40 megapixels");
    }

    // Copies rows of rowBytes, stride apart in from (0: packed), into to, packed.
    static void Rows(ReadOnlySpan<byte> from, int stride, int rowBytes, int rows, Span<byte> to, string what)
    {
        if (stride == 0) stride = rowBytes;
        if (stride < rowBytes) throw new ArgumentException($"{what} are {stride} bytes a row apart, fewer than a row's {rowBytes}", nameof(stride));
        var needed = (long)stride * (rows - 1) + rowBytes;
        if (from.Length < needed) throw new ArgumentException($"{what} are {from.Length} bytes, fewer than the {needed} of {rows} rows of {rowBytes}");
        if (stride == rowBytes) { from[..(rowBytes * rows)].CopyTo(to); return; }
        for (var row = 0; row < rows; row++) from.Slice(row * stride, rowBytes).CopyTo(to.Slice(row * rowBytes, rowBytes));
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

    /// <summary>The size in bytes of the JPEG it was loaded from, or of a frame's pixels, without decoding anything; 0 for a blank picture.</summary>
    internal int SourceBytes => jpeg?.Length ?? frameBytes;

    /// <summary>Whether this is the image of a frame of pixels (a live feed's, a video's, or one made of pixels), given back or not.</summary>
    internal bool IsFrame => frameBytes != 0;

    /// <summary>
    /// Gives back this image's reference to its frame's pixels, once, from any thread (nothing for a JPEG or a blank
    /// picture): call it when nothing will read the image again. What was already encoded is still given by
    /// <see cref="ToJpeg"/>; anything that needs the pixels afterwards, and <see cref="Copy"/>, throws. Copies hold
    /// references of their own and go on. Never needed: an image never disposed leaves its buffer to the garbage collector.
    /// </summary>
    public void Dispose() => Release();

    /// <summary>Gives back this image's reference to its frame, once (<see cref="Dispose"/>).</summary>
    internal void Release() => Interlocked.Exchange(ref frame, null)?.Release();

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
    /// <param name="text">The text, at most 200 characters; a control character, such as a newline or a tab, is drawn as a space.</param>
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
    /// <exception cref="InvalidOperationException">libjpeg-turbo is missing, and something is drawn or the picture is blank or a frame; or the frame was given back.</exception>
    /// <exception cref="ArgumentException">The picture is over 40 megapixels; refused before its pixels are decoded.</exception>
    /// <exception cref="System.IO.InvalidDataException">The picture cannot be read. A truncated or damaged one is drawn on as far as it decodes.</exception>
    public byte[] ToJpeg()
    {
        if (drawings.Count == 0 && jpeg != null) return jpeg;
        if (encoded != null) return encoded;
        if (!TurboJpeg.Available) throw new InvalidOperationException(TurboJpeg.Missing);
        // A frame with nothing drawn is encoded from its own pixels, converted by nothing. Kept, as a frame is no JPEG to give back.
        if (IsFrame && drawings.Count == 0)
        {
            var held = Held();
            return encoded = held.Format == SharpPixelFormat.Yuv420 ? TurboJpeg.EncodeYuv(held, Quality)
                : TurboJpeg.Encode(held.Data.AsSpan(0, held.Length), held.Width, held.Height, Quality, format: held.Format);
        }
        ReadSize();
        if ((long)width * height > MaxPixels) throw new ArgumentException("The picture is larger than 40 megapixels");
        var length = width * height * 3;
        var pixels = Rent(length);
        try
        {
            var rgb = pixels.AsSpan(0, length);
            Render(rgb);
            return encoded = TurboJpeg.Encode(rgb, width, height, Quality);
        }
        finally
        {
            Return(pixels);
        }
    }

    /// <summary>
    /// Writes the picture's pixels into <paramref name="destination"/> in the layout of <paramref name="format"/>, packed:
    /// <see cref="PixelsLength(SharpPixelFormat)"/> bytes from its start. The drawings are drawn on them, as
    /// <see cref="ToJpeg"/> draws them, so read a frame before drawing on it to see what the camera saw (or read a
    /// <see cref="Copy"/> made before). A frame read in its own layout with nothing drawn is copied as it is; anything else
    /// is converted (to YUV 4:2:0 and from it by libjpeg-turbo) or decoded. Nothing is kept: each call works it out again.
    /// </summary>
    /// <param name="destination">At least <see cref="PixelsLength(SharpPixelFormat)"/> bytes.</param>
    /// <param name="format">The layout to write; RGB when not given.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small, or the picture is over 40 megapixels.</exception>
    /// <exception cref="InvalidOperationException">libjpeg-turbo is missing and a JPEG is to be decoded or pixels converted to or from YUV 4:2:0; or the frame was given back.</exception>
    /// <exception cref="System.IO.InvalidDataException">The picture cannot be read.</exception>
    public void CopyPixels(Span<byte> destination, SharpPixelFormat format = SharpPixelFormat.Rgb)
    {
        ReadSize();
        if ((long)width * height > MaxPixels) throw new ArgumentException("The picture is larger than 40 megapixels");
        var length = PixelFrame.LengthOf(width, height, format);
        if (destination.Length < length) throw new ArgumentException($"The picture's pixels are {length} bytes, more than the {destination.Length} given", nameof(destination));
        var target = destination[..length];
        if (drawings.Count == 0 && IsFrame) { Convert(Held(), target, format); return; }
        // A JPEG with nothing drawn is decoded straight into a packed layout.
        if (drawings.Count == 0 && jpeg != null && format != SharpPixelFormat.Yuv420)
        {
            if (!TurboJpeg.Available) throw new InvalidOperationException(TurboJpeg.Missing);
            target.Clear();
            TurboJpeg.Decode(jpeg, target, width, height, format);
            return;
        }
        if (format == SharpPixelFormat.Rgb) { Render(target); return; }
        var rgbLength = width * height * 3;
        var pixels = Rent(rgbLength);
        try
        {
            var rgb = pixels.AsSpan(0, rgbLength);
            Render(rgb);
            if (format == SharpPixelFormat.Yuv420) TurboJpeg.EncodeToYuv(rgb, width, height, SharpPixelFormat.Rgb, target);
            else Swizzle(rgb, SharpPixelFormat.Rgb, target, format);
        }
        finally
        {
            Return(pixels);
        }
    }

    /// <summary>The picture's pixels as a new array, in the layout of <paramref name="format"/>, as <see cref="CopyPixels"/> writes them.</summary>
    /// <inheritdoc cref="CopyPixels" select="exception"/>
    public byte[] ToPixels(SharpPixelFormat format = SharpPixelFormat.Rgb)
    {
        var pixels = GC.AllocateUninitializedArray<byte>(PixelsLength(format));
        CopyPixels(pixels, format);
        return pixels;
    }

    // The picture in RGB with its drawings drawn on it, into rgb, of its size.
    void Render(Span<byte> rgb)
    {
        // Cleared first: a pooled array holds an earlier picture, which a decode that warns and then fails leaves as it
        // was — what did not decode is black, never someone else's frame (nor what an unset array held).
        if (IsFrame) Convert(Held(), rgb, SharpPixelFormat.Rgb);
        else if (jpeg != null) { rgb.Clear(); TurboJpeg.Decode(jpeg, rgb, width, height); }
        else ImagePainter.Fill(rgb, width, height, fill);
        foreach (var drawing in drawings)
            switch (drawing)
            {
                case Rectangle r: ImagePainter.Rect(rgb, width, height, r.X, r.Y, r.Width, r.Height, r.Color, r.Thickness); break;
                case Label l: ImagePainter.Text(rgb, width, height, l.Text, l.X, l.Y, l.Color, l.Size, l.Background); break;
            }
    }

    // A frame's pixels in another layout (or its own), of its size, packed.
    static void Convert(PixelFrame frame, Span<byte> to, SharpPixelFormat format)
    {
        if (frame.Format == format) frame.Data.AsSpan(0, frame.Length).CopyTo(to);
        else if (frame.Format == SharpPixelFormat.Yuv420) TurboJpeg.DecodeYuv(frame, to, format);
        else if (format == SharpPixelFormat.Yuv420) TurboJpeg.EncodeToYuv(frame.Data.AsSpan(0, frame.Length), frame.Width, frame.Height, frame.Format, to);
        else Swizzle(frame.Data.AsSpan(0, frame.Length), frame.Format, to, format);
    }

    // Packed pixels of one layout in another: the colours reordered, alpha dropped or set to 255.
    static void Swizzle(ReadOnlySpan<byte> from, SharpPixelFormat fromFormat, Span<byte> to, SharpPixelFormat toFormat)
    {
        int fromBytes = PixelFrame.BytesPerPixel(fromFormat), toBytes = PixelFrame.BytesPerPixel(toFormat);
        bool fromBgr = fromFormat is SharpPixelFormat.Bgr or SharpPixelFormat.Bgra, toBgr = toFormat is SharpPixelFormat.Bgr or SharpPixelFormat.Bgra;
        var count = from.Length / fromBytes;
        for (int i = 0, s = 0, d = 0; i < count; i++, s += fromBytes, d += toBytes)
        {
            byte first = from[s], second = from[s + 1], third = from[s + 2];
            if (fromBgr != toBgr) (first, third) = (third, first);
            to[d] = first;
            to[d + 1] = second;
            to[d + 2] = third;
            if (toBytes == 4) to[d + 3] = fromBytes == 4 ? from[s + 3] : (byte)255;
        }
    }

    /// <summary>
    /// The size a picture of <paramref name="width"/> × <paramref name="height"/> has once it fits in
    /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (a side of 0 has no limit): its aspect ratio kept, never
    /// larger than it is. ffmpeg's scale filter with <c>force_original_aspect_ratio=decrease</c> fits a live frame so too.
    /// </summary>
    internal static (int Width, int Height) FitSize(int width, int height, int maxWidth, int maxHeight)
    {
        var across = maxWidth > 0 ? (double)maxWidth / width : 1;
        var down = maxHeight > 0 ? (double)maxHeight / height : 1;
        if (across >= 1 && down >= 1) return (width, height);
        return across <= down
            ? (maxWidth, Math.Clamp((int)Math.Round(height * across), 1, maxHeight > 0 ? maxHeight : height))
            : (Math.Clamp((int)Math.Round(width * down), 1, maxWidth > 0 ? maxWidth : width), maxHeight);
    }

    /// <summary>
    /// This JPEG with nothing drawn on it, made to fit in <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (a
    /// side of 0 has no limit) as <see cref="FitSize"/> says: itself when it fits already, otherwise a new JPEG, decoded
    /// scaled by eighths to at least that size, then averaged down to it and encoded at quality 80. Itself too when
    /// libjpeg-turbo is missing, which no picture can be scaled without, and for any other picture.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The picture cannot be read.</exception>
    /// <exception cref="ArgumentException">The picture is scaled to more than 40 megapixels.</exception>
    internal SharpImage Fitted(int maxWidth, int maxHeight)
    {
        if (jpeg == null || drawings.Count > 0 || !TurboJpeg.Available) return this;
        ReadSize();
        var (toWidth, toHeight) = FitSize(width, height, maxWidth, maxHeight);
        if ((toWidth, toHeight) == (width, height)) return this;
        var (decodedWidth, decodedHeight) = TurboJpeg.ScaledAtLeast(width, height, toWidth, toHeight);
        if ((long)decodedWidth * decodedHeight > MaxPixels) throw new ArgumentException("The picture is larger than 40 megapixels");
        int decodedLength = decodedWidth * decodedHeight * 3, length = toWidth * toHeight * 3;
        var decoded = Rent(decodedLength);
        var shrunk = (decodedWidth, decodedHeight) == (toWidth, toHeight) ? null : Rent(length);
        try
        {
            var rgb = decoded.AsSpan(0, decodedLength);
            // Cleared first, as ToJpeg clears: what does not decode is black, never an earlier picture.
            rgb.Clear();
            TurboJpeg.Decode(jpeg, rgb, decodedWidth, decodedHeight);
            if (shrunk == null) return Load(TurboJpeg.Encode(rgb, toWidth, toHeight, Quality));
            ImagePainter.Shrink(rgb, decodedWidth, decodedHeight, shrunk.AsSpan(0, length), toWidth, toHeight);
            return Load(TurboJpeg.Encode(shrunk.AsSpan(0, length), toWidth, toHeight, Quality));
        }
        finally
        {
            Return(decoded);
            if (shrunk != null) Return(shrunk);
        }
    }

    /// <summary>A copy with the same picture (its bytes shared) and its own list of drawings, the same ones so far.</summary>
    /// <exception cref="InvalidOperationException">This is a frame that was given back.</exception>
    public SharpImage Copy()
    {
        if (!IsFrame) return new(jpeg, fill, [.. drawings], width, height, encoded);
        var held = frame;
        // The copy's own reference, taken while this image's keeps the buffer from being given back.
        if (held == null || !held.TryAddReference()) throw new InvalidOperationException(Released);
        return new(held, [.. drawings], encoded);
    }

    // The frame's pixels, while this image holds them.
    PixelFrame Held() => frame ?? throw new InvalidOperationException(Released);

    object IVisualSnapshot.CreateVisualSnapshot() => Copy();

    void ReadSize()
    {
        if (width != 0) return;
        TurboJpeg.Header(jpeg!, out var w, out var h);
        (width, height) = (w, h);
    }
}
