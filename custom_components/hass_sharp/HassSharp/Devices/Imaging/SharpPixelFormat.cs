namespace HassSharp;

/// <summary>
/// How the pixels of a picture are laid out in bytes, for <see cref="SharpImage.FromPixels"/>, <see cref="SharpImage.Writer"/>
/// and <see cref="SharpImage.CopyPixels"/>. A packed format has its rows one after the other, each pixel's bytes together,
/// starting at the top left; YUV 4:2:0 is three planes one after the other.
/// </summary>
public enum SharpPixelFormat
{
    /// <summary>Three bytes a pixel: red, green, blue.</summary>
    Rgb,
    /// <summary>Three bytes a pixel: blue, green, red, as OpenCV and many capture APIs give them.</summary>
    Bgr,
    /// <summary>Four bytes a pixel: red, green, blue and alpha. A JPEG has no alpha: it is ignored when encoded, and read as 255, unless the picture was made of four-byte pixels, whose alpha is kept.</summary>
    Rgba,
    /// <summary>Four bytes a pixel: blue, green, red and alpha, as Windows and many GPUs give them. Alpha is as <see cref="Rgba"/>'s.</summary>
    Bgra,
    /// <summary>
    /// YUV 4:2:0 (I420), full range as a JPEG's YCbCr is: the Y plane, one byte a pixel; then the U and the V plane, each one
    /// byte for each 2 × 2 pixels, of half the width and half the height rounded up. Video decoders and ffmpeg's
    /// <c>yuv420p</c> give it; a frame of it is encoded as a JPEG without being converted.
    /// </summary>
    Yuv420,
}

/// <summary>Fills the pixels of a new picture, in the layout of its <see cref="SharpPixelFormat"/> (see <see cref="SharpImage.Create"/>).</summary>
/// <param name="pixels">The picture's bytes, packed: exactly <see cref="SharpImage.PixelsLength(int, int, SharpPixelFormat)"/> of them, not set yet.</param>
public delegate void SharpPixelFill(Span<byte> pixels);

/// <summary>
/// A new picture's pixels to write into, in a pooled buffer, so that code can decode or render a frame straight into it, at
/// 4K too, without a copy (<see cref="SharpImage.Writer"/>): write <see cref="Pixels"/>, then <see cref="ToImage"/> makes it
/// the picture, once. Disposing a writer that made no picture gives its buffer back. Do not keep <see cref="Pixels"/> after
/// <see cref="ToImage"/> or disposing: the bytes are the picture's from then on, and go round again once it is given back.
/// Not thread-safe.
/// </summary>
public sealed class SharpFrameWriter : IDisposable
{
    PixelFrame? frame;
    bool made;

    internal SharpFrameWriter(PixelFrame frame) => this.frame = frame;

    /// <summary>The width in pixels.</summary>
    public int Width => Held().Width;
    /// <summary>The height in pixels.</summary>
    public int Height => Held().Height;
    /// <summary>The layout of <see cref="Pixels"/>.</summary>
    public SharpPixelFormat Format => Held().Format;
    /// <summary>The bytes of a row: of the Y plane for <see cref="SharpPixelFormat.Yuv420"/>, whose chroma rows are <c>(Width + 1) / 2</c> bytes.</summary>
    public int Stride => Held() is var held && held.Format == SharpPixelFormat.Yuv420 ? held.Width : held.Width * PixelFrame.BytesPerPixel(held.Format);
    /// <summary>The picture's bytes, packed, not set yet: write each of them.</summary>
    /// <exception cref="ObjectDisposedException">The picture was made, or the writer disposed.</exception>
    public Memory<byte> Pixels => Held() is var held ? held.Data.AsMemory(0, held.Length) : default;

    /// <summary>The picture of the pixels written, which takes over the buffer; once. The writer is done afterwards.</summary>
    /// <exception cref="ObjectDisposedException">The picture was made already, or the writer disposed.</exception>
    public SharpImage ToImage()
    {
        var held = Held();
        (frame, made) = (null, true);
        return SharpImage.Of(held);
    }

    /// <summary>Gives the buffer back unless a picture was made of it.</summary>
    public void Dispose()
    {
        var held = frame;
        frame = null;
        held?.Release();
    }

    PixelFrame Held() => frame ?? throw new ObjectDisposedException(nameof(SharpFrameWriter), made ? "The picture was made of these pixels already" : "The writer was disposed");
}
