using System.Runtime.InteropServices;

namespace HassSharp;

/// <summary>
/// The system's libjpeg-turbo, through its TurboJPEG API: nothing is shipped. Home Assistant's image has
/// <c>/usr/lib/libturbojpeg.so.0</c> (3.1); Ubuntu has 2.1. Only functions present since 2.0 are called, through function
/// pointers looked up once per process, so a missing library or function only makes <see cref="Available"/> false.
/// A TurboJPEG handle is not thread-safe, and after a failed call it stays failed: each thread has one decompressor and
/// one compressor, destroyed and made again after any failed call (and the compressor after any call with flags), and
/// destroyed once the thread has ended. Pixels are RGB, three bytes each, rows packed.
/// </summary>
internal static unsafe class TurboJpeg
{
    internal const string Missing = "Drawing on a picture needs the libjpeg-turbo library (libturbojpeg), which this system does not have";

    const int PixelFormatRgb = 0;   // TJPF_RGB
    const int Subsampling420 = 2;   // TJSAMP_420
    const int LimitScans = 32768;   // TJFLAG_LIMITSCANS (2.1+): a progressive picture of over 500 scans is refused
    const int ErrorWarning = 0;     // TJERR_WARNING: a truncated or damaged picture, still decoded

    // The soname first (Linux has no unversioned .so without the development package), then by directory: Python.NET
    // processes no deps.json probing, and macOS does not search Homebrew's directories.
    static readonly string[] Names = ["libturbojpeg.so.0", "libturbojpeg.0.dylib", "turbojpeg"];
    static readonly string[] Directories = ["/usr/lib", "/usr/lib64", "/usr/local/lib", "/opt/homebrew/lib", "/opt/homebrew/opt/jpeg-turbo/lib"];

    sealed class Api(nint library)
    {
        internal static readonly string[] Exports = ["tjInitDecompress", "tjDecompressHeader3", "tjDecompress2", "tjInitCompress", "tjCompress2", "tjFree", "tjDestroy", "tjGetErrorStr2", "tjGetErrorCode"];
        internal readonly delegate* unmanaged<nint> InitDecompress = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "tjInitDecompress");
        internal readonly delegate* unmanaged<nint, byte*, CULong, int*, int*, int*, int*, int> DecompressHeader3 = (delegate* unmanaged<nint, byte*, CULong, int*, int*, int*, int*, int>)NativeLibrary.GetExport(library, "tjDecompressHeader3");
        internal readonly delegate* unmanaged<nint, byte*, CULong, byte*, int, int, int, int, int, int> Decompress2 = (delegate* unmanaged<nint, byte*, CULong, byte*, int, int, int, int, int, int>)NativeLibrary.GetExport(library, "tjDecompress2");
        internal readonly delegate* unmanaged<nint> InitCompress = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "tjInitCompress");
        internal readonly delegate* unmanaged<nint, byte*, int, int, int, int, byte**, CULong*, int, int, int, int> Compress2 = (delegate* unmanaged<nint, byte*, int, int, int, int, byte**, CULong*, int, int, int, int>)NativeLibrary.GetExport(library, "tjCompress2");
        internal readonly delegate* unmanaged<byte*, void> Free = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(library, "tjFree");
        internal readonly delegate* unmanaged<nint, int> Destroy = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "tjDestroy");
        internal readonly delegate* unmanaged<nint, byte*> GetErrorStr2 = (delegate* unmanaged<nint, byte*>)NativeLibrary.GetExport(library, "tjGetErrorStr2");
        internal readonly delegate* unmanaged<nint, int> GetErrorCode = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "tjGetErrorCode");
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

    /// <summary>Decodes a JPEG of the size <see cref="Header"/> read into <paramref name="rgb"/>.</summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The picture cannot be read, or the size given is not one; a truncated or damaged one is decoded as far as it goes.</exception>
    internal static void Decode(ReadOnlySpan<byte> jpeg, Span<byte> rgb, int width, int height)
    {
        var api = Require();
        // The library reads a size of 0 as the picture's own, which the buffer may not hold.
        if (width <= 0 || height <= 0) throw new InvalidDataException("The picture could not be read: it has no size");
        if (rgb.Length < (long)width * height * 3) throw new ArgumentException("The pixel buffer is smaller than the picture");
        int result;
        var handle = Decompressor(api);
        fixed (byte* source = jpeg)
        fixed (byte* pixels = rgb)
            result = api.Decompress2(handle, source, new CULong((nuint)jpeg.Length), pixels, width, width * 3, height, PixelFormatRgb, LimitScans);
        if (result != 0) Failed(api, ref decompressor, "The picture could not be read: ");
    }

    /// <summary>Encodes RGB pixels as a JPEG with 4:2:0 subsampling.</summary>
    /// <exception cref="InvalidOperationException">The library is missing: <see cref="Missing"/>.</exception>
    /// <exception cref="InvalidDataException">The library could not encode it, or the size given is not one.</exception>
    internal static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, int quality, int flags = 0)
    {
        var api = Require();
        if (width <= 0 || height <= 0) throw new InvalidDataException("The picture could not be written: it has no size");
        if (rgb.Length < (long)width * height * 3) throw new ArgumentException("The pixel buffer is smaller than the picture");
        byte* output = null;
        var size = new CULong(0);
        int result;
        var handle = Compressor(api);
        // The library allocates the output; it is copied into a managed array of its size and freed.
        fixed (byte* pixels = rgb)
            result = api.Compress2(handle, pixels, width, width * 3, height, PixelFormatRgb, &output, &size, Subsampling420, quality, flags);
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
}
