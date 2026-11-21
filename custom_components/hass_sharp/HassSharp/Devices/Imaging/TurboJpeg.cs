using System.Runtime.InteropServices;

namespace HassSharp;

/// <summary>
/// The system's libjpeg-turbo, through its TurboJPEG API: nothing is shipped. Home Assistant's image has
/// <c>/usr/lib/libturbojpeg.so.0</c> (3.1); Ubuntu has 2.1. Only functions present since 2.0 are called, through function
/// pointers looked up once per process, so a missing library or function only makes <see cref="Available"/> false.
/// A TurboJPEG handle is not thread-safe, and after a failed call it stays failed: each thread has one decompressor and
/// one compressor, destroyed and made again after any failed call (and the compressor after any call with flags), and
/// destroyed once the thread has ended. Pixels are RGB, three bytes each, rows packed, unless a call takes another
/// <see cref="SharpPixelFormat"/> (BGR, or four bytes a pixel, whose fourth is alpha, ignored when encoding and 255 when
/// decoding); a frame's YUV 4:2:0 planes (<see cref="PixelFrame"/>) are encoded straight from them or converted to
/// pixels to be drawn on.
/// </summary>
internal static unsafe class TurboJpeg
{
    internal const string Missing = "Drawing on a picture needs the libjpeg-turbo library (libturbojpeg), which this system does not have";

    const int PixelFormatRgb = 0;   // TJPF_RGB
    const int PixelFormatBgr = 1;   // TJPF_BGR
    const int PixelFormatRgba = 7;  // TJPF_RGBA
    const int PixelFormatBgra = 8;  // TJPF_BGRA
    const int Subsampling420 = 2;   // TJSAMP_420
    const int LimitScans = 32768;   // TJFLAG_LIMITSCANS (2.1+): a progressive picture of over 500 scans is refused
    const int ErrorWarning = 0;     // TJERR_WARNING: a truncated or damaged picture, still decoded

    // The soname first (Linux has no unversioned .so without the development package), then by directory: Python.NET
    // processes no deps.json probing, and macOS does not search Homebrew's directories.
    static readonly string[] Names = ["libturbojpeg.so.0", "libturbojpeg.0.dylib", "turbojpeg"];
    static readonly string[] Directories = ["/usr/lib", "/usr/lib64", "/usr/local/lib", "/opt/homebrew/lib", "/opt/homebrew/opt/jpeg-turbo/lib"];

    sealed class Api(nint library)
    {
        internal static readonly string[] Exports = ["tjInitDecompress", "tjDecompressHeader3", "tjDecompress2", "tjInitCompress", "tjCompress2", "tjFree", "tjDestroy", "tjGetErrorStr2", "tjGetErrorCode", "tjCompressFromYUVPlanes", "tjDecodeYUVPlanes", "tjEncodeYUVPlanes"];
        internal readonly delegate* unmanaged<nint> InitDecompress = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "tjInitDecompress");
        internal readonly delegate* unmanaged<nint, byte*, CULong, int*, int*, int*, int*, int> DecompressHeader3 = (delegate* unmanaged<nint, byte*, CULong, int*, int*, int*, int*, int>)NativeLibrary.GetExport(library, "tjDecompressHeader3");
        internal readonly delegate* unmanaged<nint, byte*, CULong, byte*, int, int, int, int, int, int> Decompress2 = (delegate* unmanaged<nint, byte*, CULong, byte*, int, int, int, int, int, int>)NativeLibrary.GetExport(library, "tjDecompress2");
        internal readonly delegate* unmanaged<nint> InitCompress = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "tjInitCompress");
        internal readonly delegate* unmanaged<nint, byte*, int, int, int, int, byte**, CULong*, int, int, int, int> Compress2 = (delegate* unmanaged<nint, byte*, int, int, int, int, byte**, CULong*, int, int, int, int>)NativeLibrary.GetExport(library, "tjCompress2");
        internal readonly delegate* unmanaged<byte*, void> Free = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(library, "tjFree");
        internal readonly delegate* unmanaged<nint, int> Destroy = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "tjDestroy");
        internal readonly delegate* unmanaged<nint, byte*> GetErrorStr2 = (delegate* unmanaged<nint, byte*>)NativeLibrary.GetExport(library, "tjGetErrorStr2");
        internal readonly delegate* unmanaged<nint, int> GetErrorCode = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "tjGetErrorCode");
        // Both since 1.4: (handle, planes, width, strides, height, subsampling, &jpeg, &size, quality, flags), with a
        // compressor; (handle, planes, strides, subsampling, pixels, width, pitch, height, pixel format, flags), with a
        // decompressor.
        internal readonly delegate* unmanaged<nint, byte**, int, int*, int, int, byte**, CULong*, int, int, int> CompressFromYuvPlanes = (delegate* unmanaged<nint, byte**, int, int*, int, int, byte**, CULong*, int, int, int>)NativeLibrary.GetExport(library, "tjCompressFromYUVPlanes");
        internal readonly delegate* unmanaged<nint, byte**, int*, int, byte*, int, int, int, int, int, int> DecodeYuvPlanes = (delegate* unmanaged<nint, byte**, int*, int, byte*, int, int, int, int, int, int>)NativeLibrary.GetExport(library, "tjDecodeYUVPlanes");
        // Since 1.4, with a compressor: (handle, pixels, width, pitch, height, pixel format, planes, strides, subsampling, flags).
        internal readonly delegate* unmanaged<nint, byte*, int, int, int, int, byte**, int*, int, int, int> EncodeYuvPlanes = (delegate* unmanaged<nint, byte*, int, int, int, int, byte**, int*, int, int, int>)NativeLibrary.GetExport(library, "tjEncodeYUVPlanes");
    }

    static readonly Api? api = Find();
    [ThreadStatic] static Handle? decompressor;
    [ThreadStatic] static Handle? compressor;

    /// <summary>
    /// A TurboJPEG handle, destroyed when disposed after a failed call, or by its finalizer once the thread that holds
    /// it has ended and nothing reaches it.
    /// </summary>
    sealed class Handle : SafeHandle
    {
        internal Handle(nint value) : base(0, ownsHandle: true) => SetHandle(value);

        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            api!.Destroy(handle);
            return true;
        }
    }

    /// <summary>For tests: the current thread's decompressor and compressor; each null until made.</summary>
    internal static (object? Decompressor, object? Compressor) ThreadHandles => (decompressor, compressor);
    /// <summary>For tests: TJFLAG_PROGRESSIVE, to make a progressive JPEG with <see cref="Encode"/>.</summary>
    internal const int Progressive = 16384;

    /// <summary>For tests: makes the library unavailable, as on a system without it.</summary>
    internal static bool Disabled { get; set; }

    /// <summary>Whether the library was found, with every function called here.</summary>
    internal static bool Available => api != null && !Disabled;

    static Api? Find()
    {
        foreach (var path in Names.Concat(Directories.SelectMany(directory => Names.Select(name => Path.Combine(directory, name)))))
        {
            var loaded = Path.IsPathRooted(path) ? NativeLibrary.TryLoad(path, out var library)
                : NativeLibrary.TryLoad(path, typeof(TurboJpeg).Assembly, null, out library);
            if (!loaded) continue;
            if (Api.Exports.All(name => NativeLibrary.TryGetExport(library, name, out _))) return new Api(library);
            NativeLibrary.Free(library);
        }
        return null;
    }

    static Api Require() => Available ? api! : throw new InvalidOperationException(Missing);

    // TurboJPEG's pixel format of a packed format.
    static int Packed(SharpPixelFormat format) => format switch
    {
        SharpPixelFormat.Rgb => PixelFormatRgb,
        SharpPixelFormat.Bgr => PixelFormatBgr,
        SharpPixelFormat.Rgba => PixelFormatRgba,
        SharpPixelFormat.Bgra => PixelFormatBgra,
        _ => throw new ArgumentException($"{format} is not a packed pixel format", nameof(format)),
    };

    // The thread-static field keeps the handle alive while a call uses its value.
    static nint Decompressor(Api api) => (decompressor ??= Made(api, api.InitDecompress())).DangerousGetHandle();
    static nint Compressor(Api api) => (compressor ??= Made(api, api.InitCompress())).DangerousGetHandle();
    static Handle Made(Api api, nint handle) => handle != 0 ? new Handle(handle)
        : throw new InvalidDataException("The picture could not be read: " + Message(api, 0));

    static string Message(Api api, nint handle) => Marshal.PtrToStringUTF8((nint)api.GetErrorStr2(handle)) ?? "no message";

    /// <summary>
    /// After a call that returned −1: the handle is destroyed (the library leaves it failed) and made again on the next
    /// call. A warning — a truncated or damaged picture — is accepted; anything else is thrown.
    /// </summary>
    static void Failed(Api api, ref Handle? handle, string what)
    {
        var value = handle!.DangerousGetHandle();
        var warning = api.GetErrorCode(value) == ErrorWarning;
        var message = Message(api, value);
        handle.Dispose();
        handle = null;
        if (!warning) throw new InvalidDataException(what + message);
    }

    /// <summary>
    /// Reads a JPEG's size from its header, without decoding it, with a new decompressor: one that read a picture before
    /// answers a header that ends before its size with that picture's size (libjpeg-turbo 3), as if it were this one's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The header cannot be read.</exception>
    internal static void Header(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        var api = Require();
        int w = 0, h = 0, subsampling, colorspace, result;
        decompressor?.Dispose();
        decompressor = null;
        var handle = Decompressor(api);
        fixed (byte* source = jpeg) result = api.DecompressHeader3(handle, source, new CULong((nuint)jpeg.Length), &w, &h, &subsampling, &colorspace);
        if (result != 0) Failed(api, ref decompressor, "The picture could not be read: ");
        if (w <= 0 || h <= 0) throw new InvalidDataException("The picture could not be read: it has no size");
        (width, height) = (w, h);
    }

    /// <summary>
    /// Decodes a JPEG of the size <see cref="Header"/> read into <paramref name="rgb"/>; of a size
    /// <see cref="ScaledAtLeast"/> gave, the picture scaled down to it while it decodes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The picture cannot be read, or the size given is not one; a truncated or damaged one is decoded as far as it goes.</exception>
    internal static void Decode(ReadOnlySpan<byte> jpeg, Span<byte> rgb, int width, int height, SharpPixelFormat format = SharpPixelFormat.Rgb)
    {
        var api = Require();
        // The library reads a size of 0 as the picture's own, which the buffer may not hold.
        if (width <= 0 || height <= 0) throw new InvalidDataException("The picture could not be read: it has no size");
        var size = PixelFrame.BytesPerPixel(format);
        if (rgb.Length < (long)width * height * size) throw new ArgumentException("The pixel buffer is smaller than the picture");
        int result;
        var handle = Decompressor(api);
        fixed (byte* source = jpeg)
        fixed (byte* pixels = rgb)
            result = api.Decompress2(handle, source, new CULong((nuint)jpeg.Length), pixels, width, width * size, height, Packed(format), LimitScans);
        if (result != 0) Failed(api, ref decompressor, "The picture could not be read: ");
    }

    /// <summary>
    /// The smallest size the decoder can scale a picture of <paramref name="width"/> × <paramref name="height"/> down to
    /// (by eighths, 7/8 to 1/8, as libjpeg-turbo scales while it decodes) that is still at least
    /// <paramref name="atLeastWidth"/> × <paramref name="atLeastHeight"/>; the picture's own size when even 7/8 is smaller.
    /// <see cref="Decode"/> of that size decodes the picture scaled so.
    /// </summary>
    internal static (int Width, int Height) ScaledAtLeast(int width, int height, int atLeastWidth, int atLeastHeight)
    {
        for (var eighths = 1; eighths < 8; eighths++)
        {
            // TJSCALED: a side times the factor, rounded up.
            var (w, h) = ((int)(((long)width * eighths + 7) / 8), (int)(((long)height * eighths + 7) / 8));
            if (w >= atLeastWidth && h >= atLeastHeight) return (w, h);
        }
        return (width, height);
    }

    /// <summary>Encodes pixels, RGB unless another packed format is given, as a JPEG with 4:2:0 subsampling.</summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The library could not encode it, or the size given is not one.</exception>
    internal static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, int quality, int flags = 0, SharpPixelFormat format = SharpPixelFormat.Rgb)
    {
        var api = Require();
        if (width <= 0 || height <= 0) throw new InvalidDataException("The picture could not be written: it has no size");
        var bytes = PixelFrame.BytesPerPixel(format);
        if (rgb.Length < (long)width * height * bytes) throw new ArgumentException("The pixel buffer is smaller than the picture");
        byte* output = null;
        var size = new CULong(0);
        int result;
        var handle = Compressor(api);
        // The library allocates the output; it is copied into a managed array of its size and freed.
        fixed (byte* pixels = rgb)
            result = api.Compress2(handle, pixels, width, width * bytes, height, Packed(format), &output, &size, Subsampling420, quality, flags);
        try
        {
            if (result != 0) Failed(api, ref compressor, "The picture could not be written: ");
            if (output == null || size.Value == 0) throw new InvalidDataException("The picture could not be written: the library gave nothing");
            return new ReadOnlySpan<byte>(output, checked((int)size.Value)).ToArray();
        }
        finally
        {
            if (output != null) api.Free(output);
            // libjpeg-turbo 2.1 keeps what a call with flags set in the compressor: after a progressive picture, every
            // baseline picture it writes is grey. Such a compressor is not used again; the next call makes a new one.
            if (flags != 0)
            {
                compressor?.Dispose();
                compressor = null;
            }
        }
    }

    /// <summary>Encodes a frame's YUV 4:2:0 planes as a JPEG with 4:2:0 subsampling, without making RGB of them.</summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The library could not encode it.</exception>
    internal static byte[] EncodeYuv(PixelFrame frame, int quality)
    {
        var api = Require();
        byte* output = null;
        var size = new CULong(0);
        int result;
        var handle = Compressor(api);
        fixed (byte* data = frame.Data)
        {
            var planes = stackalloc byte*[3] { data, data + frame.LumaLength, data + frame.LumaLength + frame.ChromaLength };
            var strides = stackalloc int[3] { frame.Width, frame.ChromaWidth, frame.ChromaWidth };
            result = api.CompressFromYuvPlanes(handle, planes, frame.Width, strides, frame.Height, Subsampling420, &output, &size, quality, 0);
        }
        try
        {
            if (result != 0) Failed(api, ref compressor, "The picture could not be written: ");
            if (output == null || size.Value == 0) throw new InvalidDataException("The picture could not be written: the library gave nothing");
            return new ReadOnlySpan<byte>(output, checked((int)size.Value)).ToArray();
        }
        finally
        {
            if (output != null) api.Free(output);
        }
    }

    /// <summary>Converts a frame's YUV 4:2:0 planes into pixels of its size, RGB unless another packed format is given, to be drawn on or read.</summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The library could not convert it.</exception>
    internal static void DecodeYuv(PixelFrame frame, Span<byte> rgb, SharpPixelFormat format = SharpPixelFormat.Rgb)
    {
        var api = Require();
        var size = PixelFrame.BytesPerPixel(format);
        if (rgb.Length < (long)frame.Width * frame.Height * size) throw new ArgumentException("The pixel buffer is smaller than the picture");
        int result;
        var handle = Decompressor(api);
        fixed (byte* data = frame.Data)
        fixed (byte* pixels = rgb)
        {
            var planes = stackalloc byte*[3] { data, data + frame.LumaLength, data + frame.LumaLength + frame.ChromaLength };
            var strides = stackalloc int[3] { frame.Width, frame.ChromaWidth, frame.ChromaWidth };
            result = api.DecodeYuvPlanes(handle, planes, strides, Subsampling420, pixels, frame.Width, frame.Width * size, frame.Height, Packed(format), 0);
        }
        if (result != 0) Failed(api, ref decompressor, "The picture could not be read: ");
    }

    /// <summary>
    /// Converts packed pixels of <paramref name="format"/> into YUV 4:2:0 planes, packed one after the other as a frame's
    /// are (Y, then U, then V), full range as a JPEG's YCbCr is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The library could not convert them.</exception>
    internal static void EncodeToYuv(ReadOnlySpan<byte> pixels, int width, int height, SharpPixelFormat format, Span<byte> yuv)
    {
        var api = Require();
        var size = PixelFrame.BytesPerPixel(format);
        if (pixels.Length < (long)width * height * size) throw new ArgumentException("The pixel buffer is smaller than the picture");
        if (yuv.Length < PixelFrame.LengthOf(width, height)) throw new ArgumentException("The plane buffer is smaller than the picture");
        int chromaWidth = (width + 1) / 2, luma = width * height, chroma = chromaWidth * ((height + 1) / 2);
        int result;
        var handle = Compressor(api);
        fixed (byte* source = pixels)
        fixed (byte* data = yuv)
        {
            var planes = stackalloc byte*[3] { data, data + luma, data + luma + chroma };
            var strides = stackalloc int[3] { width, chromaWidth, chromaWidth };
            result = api.EncodeYuvPlanes(handle, source, width, width * size, height, Packed(format), planes, strides, Subsampling420, 0);
        }
        if (result != 0) Failed(api, ref compressor, "The picture could not be converted: ");
    }
}
