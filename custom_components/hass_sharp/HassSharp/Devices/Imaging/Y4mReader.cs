using System.Text;

namespace HassSharp;

/// <summary>
/// The frames of a YUV4MPEG2 stream, as ffmpeg's <c>yuv4mpegpipe</c> writes them, read as they come however the stream
/// splits them. A stream header (<c>YUV4MPEG2 W… H… C420…</c>) gives the size; then each frame is a <c>FRAME</c> line and
/// its three planes, which <see cref="NextAsync"/> reads into a pooled <see cref="PixelFrame"/>. A new stream header between
/// frames gives a new size for the frames after it. Only 4:2:0 is taken (<c>C420</c>, <c>C420jpeg</c>, <c>C420mpeg2</c>,
/// <c>C420paldv</c>, or none, which is 4:2:0). A frame of limited range (<c>XCOLORRANGE=LIMITED</c>) is stretched to the
/// full range a JPEG takes; one with no range given is taken as full, which the feed asks ffmpeg for. A size is 1 to 8192
/// pixels a side and at most 40 megapixels, as a <see cref="SharpImage"/> is. Not thread-safe.
/// </summary>
internal sealed class Y4mReader(Stream stream)
{
    const int MaxSide = 8192;
    const long MaxPixels = 40_000_000;
    // A header or FRAME line is far shorter: more without a newline is not a YUV4MPEG2 stream.
    const int MaxLine = 4096;

    // What was read and not taken yet: buffer[start..end].
    readonly byte[] buffer = new byte[64 * 1024];
    int start, end;
    bool limited;

    /// <summary>The size the last stream header gave; 0 before the first.</summary>
    internal int Width { get; private set; }
    internal int Height { get; private set; }

    /// <summary>
    /// The next frame, which the caller holds the one reference of; null at the end of the stream, also one that ends
    /// inside a frame.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not YUV4MPEG2 of 4:2:0, or a size is outside the limits.</exception>
    internal async ValueTask<PixelFrame?> NextAsync(CancellationToken cancellation = default)
    {
        while (true)
        {
            var line = await LineAsync(cancellation);
            if (line == null) return null;
            if (line.StartsWith("YUV4MPEG2", StringComparison.Ordinal)) { Header(line); continue; }
            if (!line.StartsWith("FRAME", StringComparison.Ordinal)) throw new InvalidDataException("This is not a YUV4MPEG2 stream");
            if (Width == 0) throw new InvalidDataException("A frame came before the stream header");
            var frame = PixelFrame.Rent(Width, Height);
            try
            {
                if (!await FillAsync(frame.Data.AsMemory(0, frame.Length), cancellation)) { frame.Release(); return null; }
            }
            catch
            {
                frame.Release();
                throw;
            }
            if (limited) frame.StretchToFullRange();
            return frame;
        }
    }

    void Header(string line)
    {
        int width = 0, height = 0;
        var range = false;
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            switch (word[0])
            {
                case 'W': width = Number(word); break;
                case 'H': height = Number(word); break;
                case 'C' when word is not ("C420" or "C420jpeg" or "C420mpeg2" or "C420paldv"): throw new InvalidDataException($"The stream's frames are {word[1..]}, not 4:2:0");
                case 'X' when word.StartsWith("XCOLORRANGE=", StringComparison.Ordinal): range = word == "XCOLORRANGE=LIMITED"; break;
            }
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide) throw new InvalidDataException($"The stream's frames are {width}x{height}: a side is 1 to 8192 pixels");
        if ((long)width * height > MaxPixels) throw new InvalidDataException($"The stream's frames are {width}x{height}: larger than 40 megapixels");
        (Width, Height, limited) = (width, height, range);
    }

    static int Number(string word) => int.TryParse(word.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
        ? number : throw new InvalidDataException($"The stream header's {word} is no number");

    // The next line, without its newline; null at the end of the stream (a line cut off by it too).
    async ValueTask<string?> LineAsync(CancellationToken cancellation)
    {
        var scanned = 0;
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start + scanned, end - start - scanned);
            if (newline >= 0)
            {
                var line = Encoding.ASCII.GetString(buffer, start, newline - start);
                start = newline + 1;
                return line;
            }
            scanned = end - start;
            if (scanned >= MaxLine) throw new InvalidDataException("This is not a YUV4MPEG2 stream");
            if (!await MoreAsync(cancellation)) return null;
        }
    }

    // Reads more after what is kept, moving it to the front first; false at the end of the stream.
    async ValueTask<bool> MoreAsync(CancellationToken cancellation)
    {
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            (end, start) = (end - start, 0);
        }
        var read = await stream.ReadAsync(buffer.AsMemory(end), cancellation);
        end += read;
        return read > 0;
    }

    // Fills the planes: from what is kept first, then straight from the stream. False when it ends first.
    async ValueTask<bool> FillAsync(Memory<byte> target, CancellationToken cancellation)
    {
        var kept = Math.Min(end - start, target.Length);
        buffer.AsMemory(start, kept).CopyTo(target);
        start += kept;
        if (start == end) start = end = 0;
        var rest = target[kept..];
        return rest.IsEmpty || await stream.ReadAtLeastAsync(rest, rest.Length, throwOnEndOfStream: false, cancellation) == rest.Length;
    }
}
