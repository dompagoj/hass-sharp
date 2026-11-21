namespace HassSharp;

/// <summary>
/// The pixels of one frame, in a pooled buffer that is counted: a frame of a live feed (<see cref="CameraFeed"/>), or one
/// made of pixels in code (<see cref="SharpImage.FromRgb"/> and the others). Its bytes are packed in its
/// <see cref="Format"/>: three or four bytes a pixel, rows packed; or YUV 4:2:0 as ffmpeg writes it, the Y plane (one byte a
/// pixel, rows packed), then the U and the V plane, each of half the width and half the height rounded up, full range as a
/// JPEG takes it. Whoever holds the frame (the feed for its newest, each <see cref="SharpImage"/> made of it) holds one
/// reference, taken with <see cref="TryAddReference"/> and given back once with <see cref="Release"/>. The buffer goes back
/// to the pool when the last reference is given back, and only then; one never given back is left to the garbage collector
/// with the frame, and the pool makes another. So a buffer that is rented again is one nobody holds any more. Thread-safe.
/// </summary>
internal sealed class PixelFrame
{
    // The pool keeps at most this many buffers of each length, and this many bytes in all (four 4K frames of YUV, two of
    // RGBA): a buffer given back beyond either is left to the garbage collector.
    const int PerLength = 4;
    const long PoolBytes = 64L * 1024 * 1024;

    static readonly Lock pool = new();
    static readonly Dictionary<int, Stack<byte[]>> free = [];
    static long pooledBytes;

    // References held; 0 once given back, after which none is taken.
    int references = 1;

    PixelFrame(byte[] data, int width, int height, SharpPixelFormat format)
    {
        Data = data;
        Width = width;
        Height = height;
        Format = format;
    }

    /// <summary>The pixels: rows packed, or the planes Y then U then V; at least <see cref="Length"/> bytes, of which only those are the frame's.</summary>
    internal byte[] Data { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal SharpPixelFormat Format { get; }
    /// <summary>The width of a chroma plane of YUV 4:2:0: half the width, rounded up.</summary>
    internal int ChromaWidth => (Width + 1) / 2;
    /// <summary>The height of a chroma plane of YUV 4:2:0: half the height, rounded up.</summary>
    internal int ChromaHeight => (Height + 1) / 2;
    /// <summary>The bytes of the Y plane of YUV 4:2:0.</summary>
    internal int LumaLength => Width * Height;
    /// <summary>The bytes of each chroma plane of YUV 4:2:0.</summary>
    internal int ChromaLength => ChromaWidth * ChromaHeight;
    /// <summary>The bytes of the frame.</summary>
    internal int Length => LengthOf(Width, Height, Format);

    /// <summary>The bytes of a pixel in a packed format; 0 for YUV 4:2:0, which is planar.</summary>
    internal static int BytesPerPixel(SharpPixelFormat format) => format switch
    {
        SharpPixelFormat.Rgb or SharpPixelFormat.Bgr => 3,
        SharpPixelFormat.Rgba or SharpPixelFormat.Bgra => 4,
        SharpPixelFormat.Yuv420 => 0,
        _ => throw new ArgumentException($"{format} is not a pixel format", nameof(format)),
    };

    /// <summary>The bytes of a frame of this size and format.</summary>
    internal static int LengthOf(int width, int height, SharpPixelFormat format = SharpPixelFormat.Yuv420) => BytesPerPixel(format) is var size and > 0
        ? checked(width * height * size)
        : checked(width * height + 2 * (((width + 1) / 2) * ((height + 1) / 2)));

    /// <summary>A frame of this size whose bytes are not set yet, in a buffer from the pool when it has one; the caller holds its one reference.</summary>
    internal static PixelFrame Rent(int width, int height, SharpPixelFormat format = SharpPixelFormat.Yuv420)
    {
        var length = LengthOf(width, height, format);
        byte[]? data = null;
        lock (pool)
            if (free.TryGetValue(length, out var stack) && stack.TryPop(out data)) pooledBytes -= length;
        return new(data ?? GC.AllocateUninitializedArray<byte>(length), width, height, format);
    }

    /// <summary>Takes one more reference, for one more holder; false once the frame was given back, when nothing of it may be read.</summary>
    internal bool TryAddReference()
    {
        for (var held = Volatile.Read(ref references); held > 0; held = Volatile.Read(ref references))
            if (Interlocked.CompareExchange(ref references, held + 1, held) == held) return true;
        return false;
    }

    /// <summary>Gives one reference back; the last one puts the buffer back in the pool. Each holder calls it once at most.</summary>
    internal void Release()
    {
        var left = Interlocked.Decrement(ref references);
        if (left > 0) return;
        if (left < 0) { Interlocked.Exchange(ref references, 0); return; }
        lock (pool)
        {
            if (pooledBytes + Data.Length > PoolBytes) return;
            if (!free.TryGetValue(Data.Length, out var stack)) free[Data.Length] = stack = new();
            if (stack.Count >= PerLength) return;
            stack.Push(Data);
            pooledBytes += Data.Length;
        }
    }

    /// <summary>Stretches YUV 4:2:0 of limited range (luma 16–235, chroma 16–240), as video is, to the full range (0–255) a JPEG's YCbCr is.</summary>
    internal void StretchToFullRange()
    {
        var data = Data.AsSpan(0, Length);
        Map(data[..LumaLength], LumaRange);
        Map(data[LumaLength..], ChromaRange);
    }

    static readonly byte[] LumaRange = Range(16, 219, 0), ChromaRange = Range(16, 224, 128);

    static byte[] Range(int low, int span, int middle)
    {
        var table = new byte[256];
        for (var value = 0; value < 256; value++)
        {
            var stretched = middle == 0 ? (value - low) * 255.0 / span : (value - 128) * 255.0 / span + 128;
            table[value] = (byte)Math.Clamp((int)Math.Round(stretched), 0, 255);
        }
        return table;
    }

    static void Map(Span<byte> values, byte[] table)
    {
        for (var i = 0; i < values.Length; i++) values[i] = table[values[i]];
    }

    /// <summary>How many references are held; for tests.</summary>
    internal int References => Volatile.Read(ref references);

    /// <summary>Empties the pool, when the feeds close: the memory of a feed nobody watches is not kept.</summary>
    internal static void Drain()
    {
        lock (pool)
        {
            free.Clear();
            pooledBytes = 0;
        }
    }

    /// <summary>Whether this very buffer is in the pool now; for tests.</summary>
    internal static bool InPool(byte[] data)
    {
        lock (pool) return free.TryGetValue(data.Length, out var stack) && stack.Contains(data);
    }
}
