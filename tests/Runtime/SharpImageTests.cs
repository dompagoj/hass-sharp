using HassSharp;

/// <summary>
/// <see cref="SharpImage"/>: a JPEG and a list of drawings, decoded and encoded by the system's libjpeg-turbo. A picture
/// with nothing drawn comes back as the bytes it was loaded from; rectangles and text land where they were asked for and
/// are clipped at the edges; a copy draws without touching the original; every limit is refused by a message naming
/// it; a damaged or truncated picture gives the usable part or a plain error, and the next picture works; a picture
/// whose header is too large is refused before its pixels are; a frame allocates little; threads draw at once; without
/// the library a picture still passes through and drawing fails with one plain message; and user code cannot name the
/// font library while it can use <see cref="SharpImage"/>. The pictures are made in code with <see cref="SharpImage.Blank"/>.
/// </summary>
static class SharpImageTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static readonly SharpColor Grey = SharpColor.FromRgb(128, 128, 128);

    [RuntimeSuite("--sharp-image", Order = 40)]
    public static void Run()
    {
        Check(TurboJpeg.Available, TurboJpeg.Missing + " (brew install jpeg-turbo on macOS, the distribution's libturbojpeg package on Linux)");
        Colours();
        PassThrough();
        Blank();
        Rectangle();
        Clipping();
        Text();
        CopyLeavesTheOriginal();
        Limits();
        Truncated();
        Corrupted();
        HugeHeader();
        Allocation();
        Threads();
        WithoutTheLibrary();
        UserCode();
    }

    // A test-only decode: the picture's size and its pixels as RGB.
    sealed record Pixels(int Width, int Height, byte[] Rgb)
    {
        public (int R, int G, int B) At(int x, int y) { var o = (y * Width + x) * 3; return (Rgb[o], Rgb[o + 1], Rgb[o + 2]); }
        public bool Near(int x, int y, SharpColor color, int tolerance = 48)
        {
            var (r, g, b) = At(x, y);
            return Math.Abs(r - color.R) <= tolerance && Math.Abs(g - color.G) <= tolerance && Math.Abs(b - color.B) <= tolerance;
        }
    }

    static Pixels Decode(byte[] jpeg)
    {
        TurboJpeg.Header(jpeg, out var width, out var height);
        var rgb = new byte[width * height * 3];
        TurboJpeg.Decode(jpeg, rgb, width, height);
        return new(width, height, rgb);
    }

    // A picture with something in it, so that its JPEG has real entropy data to damage.
    static byte[] Busy(int width, int height) => SharpImage.Blank(width, height, Grey)
        .DrawRect(10, 10, width / 2, height / 2, SharpColor.Blue, 6)
        .DrawRect(width / 3, height / 4, width / 2, height / 2, SharpColor.Yellow, 3)
        .DrawText("Kamera ulaz 1", 12, height - 40, SharpColor.White, 20)
        .DrawText("Čćžšđ 12", width / 2, 20, SharpColor.Orange, 28, background: false)
        .ToJpeg();

    static void Colours()
    {
        Check(SharpColor.Red == new SharpColor(SharpColor.Red.R, SharpColor.Red.G, SharpColor.Red.B) && SharpColor.FromRgb(1, 2, 3) == new SharpColor(1, 2, 3), "FromRgb makes the colour");
        var named = new[] { ("Red", SharpColor.Red), ("Green", SharpColor.Green), ("Blue", SharpColor.Blue), ("Yellow", SharpColor.Yellow), ("Orange", SharpColor.Orange), ("White", SharpColor.White), ("Black", SharpColor.Black) };
        Check(named.Select(n => n.Item2).Distinct().Count() == 7, "the seven colours differ");
        foreach (var (name, color) in named)
            foreach (var spelling in new[] { name, name.ToLowerInvariant(), name.ToUpperInvariant() })
                Check(SharpColor.TryParse(spelling, out var parsed) && parsed == color, "TryParse reads " + spelling);
        Check(!SharpColor.TryParse("purple", out _) && !SharpColor.TryParse("", out _) && !SharpColor.TryParse(null!, out _) && !SharpColor.TryParse(" red", out _), "TryParse refuses other names");
        Console.WriteLine("PASS sharp image: SharpColor has the seven named colours and FromRgb, and TryParse reads the seven names in any case and nothing else");
    }

    static void PassThrough()
    {
        var jpeg = Busy(160, 120);
        var image = SharpImage.Load(jpeg);
        Check(image.Drawings == 0, "a loaded picture has no drawings");
        Check(image.ToJpeg().AsSpan().SequenceEqual(jpeg), "with nothing drawn ToJpeg gives the bytes it was loaded from");
        Console.WriteLine("PASS sharp image: a loaded picture with nothing drawn gives back the bytes it was loaded from");
    }

    static void Blank()
    {
        var jpeg = SharpImage.Blank(64, 48).ToJpeg();
        Check(jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "a blank picture is a JPEG");
        var image = SharpImage.Load(jpeg);
        Check(image.Width == 64 && image.Height == 48, $"Load of a blank picture reads 64 × 48, not {image.Width} × {image.Height}");
        var pixels = Decode(jpeg);
        Check(pixels.Near(0, 0, SharpColor.Black, 8) && pixels.Near(63, 47, SharpColor.Black, 8), "a blank picture is black by default");
        var blue = Decode(SharpImage.Blank(32, 32, SharpColor.Blue).ToJpeg());
        Check(blue.Near(16, 16, SharpColor.Blue, 24), "a blank picture takes its colour");
        var blank = SharpImage.Blank(40, 30, SharpColor.Green);
        Check(blank.Width == 40 && blank.Height == 30, "a blank picture knows its size");
        Console.WriteLine("PASS sharp image: Blank(64, 48) is a JPEG that Load reads as 64 × 48, black by default and of the colour it is given");
    }

    static void Rectangle()
    {
        var black = SharpImage.Blank(128, 96).ToJpeg();
        var image = SharpImage.Load(black);
        Check(ReferenceEquals(image.DrawRect(20, 20, 80, 50, SharpColor.Red, 4), image) && image.Drawings == 1, "DrawRect adds a drawing and returns the image");
        var first = image.ToJpeg();
        Check(ReferenceEquals(image.ToJpeg(), first), "ToJpeg is kept until the next drawing");
        var pixels = Decode(first);
        Check(pixels.Near(21, 44, SharpColor.Red) && pixels.Near(60, 21, SharpColor.Red) && pixels.Near(98, 44, SharpColor.Red) && pixels.Near(60, 68, SharpColor.Red), "a pixel on each side of the line is red");
        Check(pixels.Near(60, 44, SharpColor.Black, 24) && pixels.Near(110, 44, SharpColor.Black, 24) && pixels.Near(60, 85, SharpColor.Black, 24) && pixels.Near(4, 4, SharpColor.Black, 24), "a pixel inside and one outside are black");
        var red = SharpImage.Load(black).DrawRect(20, 20, 80, 50);
        Check(Decode(red.ToJpeg()).Near(20, 44, SharpColor.Red, 64), "red is the default colour");

        image.DrawRect(40, 30, 40, 30, SharpColor.Green, 2);
        Check(!ReferenceEquals(image.ToJpeg(), first) && image.Drawings == 2, "a new drawing makes a new JPEG");

        var thick = Decode(SharpImage.Load(black).DrawRect(20, 20, 80, 50, SharpColor.Red, 12).ToJpeg());
        var thin = Decode(SharpImage.Load(black).DrawRect(20, 20, 80, 50, SharpColor.Red, 2).ToJpeg());
        Check(thick.Near(29, 44, SharpColor.Red) && thin.Near(29, 44, SharpColor.Black, 24), "the thickness is the line's width, drawn inwards");
        Console.WriteLine("PASS sharp image: a rectangle draws its line in its colour (red by default) and thickness, inside and outside stay as they were, and the JPEG is kept until the next drawing");
    }

    static void Clipping()
    {
        var image = SharpImage.Load(SharpImage.Blank(64, 48).ToJpeg())
            .DrawRect(-10, -10, 40, 30, SharpColor.Red, 4)
            .DrawRect(50, 30, 100, 100, SharpColor.Red, 4)
            .DrawRect(100, 100, 10, 10, SharpColor.Red, 4)
            .DrawRect(-50, -50, 10, 10, SharpColor.Red, 4)
            .DrawRect(10, 10, -5, -5, SharpColor.Red, 4)
            .DrawRect(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue, SharpColor.Red, 4)
            .DrawRect(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, SharpColor.Red, 4)
            .DrawText("half outside", -20, 20, SharpColor.White, 16)
            .DrawText("far away", 1000, 1000, SharpColor.White, 16)
            .DrawText("far before", int.MinValue, int.MinValue, SharpColor.White, 16)
            .DrawText("far after", int.MaxValue, int.MaxValue, SharpColor.White, 200);
        var pixels = Decode(image.ToJpeg());
        Check(pixels.Width == 64 && pixels.Height == 48, "clipping keeps the size");
        Check(pixels.Near(28, 4, SharpColor.Red) && pixels.Near(52, 46, SharpColor.Red), "the part of a rectangle inside the picture is drawn");
        Check(Enumerable.Range(0, 64).Any(x => pixels.At(x, 28).R > 100 && pixels.At(x, 28).G > 100), "the part of a text inside the picture is drawn");
        Console.WriteLine("PASS sharp image: rectangles and text half or wholly outside the picture, at negative or extreme coordinates, are clipped without an error");
    }

    static void Text()
    {
        foreach (var background in new[] { false, true })
        {
            const int width = 260, height = 90;
            var rgb = new byte[width * height * 3];
            ImagePainter.Fill(rgb, width, height, Grey);
            var box = ImagePainter.TextBox("Čćžšđ 12", 30, 25, 24, background);
            Check(box.Width > 24 * 3 && box.Width < 24 * 8 && box.Height >= 24 && box.Height < 24 * 2, $"the box of the text is the size of the text: {box}");
            ImagePainter.Text(rgb, width, height, "Čćžšđ 12", 30, 25, SharpColor.White, 24, background);
            int inside = 0, outside = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 3;
                    var changed = rgb[o] != 128 || rgb[o + 1] != 128 || rgb[o + 2] != 128;
                    var within = x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;
                    if (changed && within) inside++;
                    if (changed && !within) outside++;
                }
            Check(inside > 100 && outside == 0, $"text {(background ? "with" : "without")} a background changes pixels inside its box ({inside}) and none outside ({outside})");
            if (background)
            {
                var corner = (box.Y * width + box.X) * 3;
                Check(rgb[corner] < 128, "the background darkens the box behind the text");
                Check(box.X < 30 && box.Y < 25, "the background reaches around the text");
            }

            // Through the JPEG: pixels well away from the box (more than a block of 16) are as they were.
            var pixels = Decode(SharpImage.Blank(width, height, Grey).DrawText("Čćžšđ 12", 30, 25, SharpColor.White, 24, background).ToJpeg());
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (x < box.X - 16 || x >= box.X + box.Width + 16 || y < box.Y - 16 || y >= box.Y + box.Height + 16)
                        Check(pixels.Near(x, y, Grey, 6), $"a pixel far from the text is unchanged at {x}, {y}");
        }

        // A character the font does not have draws the font's empty box.
        var missing = new byte[64 * 40 * 3];
        ImagePainter.Text(missing, 64, 40, "中", 4, 4, SharpColor.White, 24, background: false);
        Check(missing.Any(b => b != 0), "a character the font lacks draws its empty box");
        Console.WriteLine("PASS sharp image: text changes pixels inside its box and none outside, with and without the translucent background, Croatian letters included, and a character the font lacks draws the font's box");
    }

    static void CopyLeavesTheOriginal()
    {
        var original = SharpImage.Load(Busy(160, 120)).DrawRect(5, 5, 50, 50, SharpColor.Green);
        var before = original.ToJpeg();
        var copy = original.Copy();
        Check(copy != original && copy.Drawings == 1, "a copy has the original's drawings");
        copy.DrawText("copy", 10, 10).DrawRect(0, 0, 30, 30);
        Check(original.Drawings == 1 && copy.Drawings == 3, "drawing on a copy adds to the copy only");
        Check(original.ToJpeg().AsSpan().SequenceEqual(before), "the original's JPEG is unchanged");
        Check(!copy.ToJpeg().AsSpan().SequenceEqual(before), "the copy's JPEG has its drawings");
        var snapshot = new VisualSnapshot().Copy(original);
        Check(snapshot is SharpImage { Drawings: 1 } shot && !ReferenceEquals(shot, original), "a wire's snapshot of an image is its copy");
        Console.WriteLine("PASS sharp image: Copy() and a wire's snapshot copy the drawings, and drawing on the copy leaves the original's JPEG unchanged");
    }

    static void Limits()
    {
        void Refused(Action action, string names, string what)
        {
            var error = Throws<ArgumentException>(action, what);
            Check(error.Message.Contains(names, StringComparison.Ordinal), $"{what}: the message names {names}: {error.Message}");
        }
        Refused(() => SharpImage.Load(null!), "This is not a JPEG picture", "null");
        Refused(() => SharpImage.Load([]), "This is not a JPEG picture", "empty");
        Refused(() => SharpImage.Load([0xFF]), "This is not a JPEG picture", "one byte");
        Refused(() => SharpImage.Load([0x89, 0x50, 0x4E, 0x47]), "This is not a JPEG picture", "a PNG");
        var large = new byte[20 * 1024 * 1024 + 1];
        large[0] = 0xFF; large[1] = 0xD8;
        Refused(() => SharpImage.Load(large), "The picture is larger than 20 MiB", "over 20 MiB");
        Check(SharpImage.Load(large[..(20 * 1024 * 1024)]).ToJpeg().Length == 20 * 1024 * 1024, "20 MiB exactly is taken");

        foreach (var (w, h) in new[] { (0, 10), (10, 0), (-1, 10), (8193, 10), (10, 8193) })
            Refused(() => SharpImage.Blank(w, h), "8192", $"a blank picture of {w} × {h}");
        Refused(() => SharpImage.Blank(8192, 8192), "40 megapixels", "a blank picture over 40 megapixels");
        SharpImage.Blank(8192, 1);

        var image = SharpImage.Load(Busy(64, 48));
        Refused(() => image.DrawRect(0, 0, 10, 10, thickness: 0), "1 to 50", "a line 0 thick");
        Refused(() => image.DrawRect(0, 0, 10, 10, thickness: 51), "1 to 50", "a line 51 thick");
        image.DrawRect(0, 0, 10, 10, thickness: 1).DrawRect(0, 0, 10, 10, thickness: 50);
        Refused(() => image.DrawText("x", 0, 0, size: 7), "8 to 200", "text of size 7");
        Refused(() => image.DrawText("x", 0, 0, size: 201), "8 to 200", "text of size 201");
        image.DrawText("x", 0, 0, size: 8).DrawText("x", 0, 0, size: 200);
        Refused(() => image.DrawText(new string('x', 201), 0, 0), "200 characters", "a text of 201 characters");
        Refused(() => image.DrawText(null!, 0, 0), "text", "no text");
        image.DrawText(new string('x', 200), 0, 0).DrawText("", 0, 0);
        Check(image.Drawings == 6, "a refused drawing is not added");
        while (image.Drawings < 500) image.DrawRect(1, 1, 2, 2);
        Refused(() => image.DrawRect(1, 1, 2, 2), "500 drawings", "the 501st drawing");
        Refused(() => image.DrawText("x", 1, 1), "500 drawings", "the 501st drawing, a text");
        Check(image.ToJpeg()[0] == 0xFF, "500 drawings are drawn");
        Console.WriteLine("PASS sharp image: not a JPEG, over 20 MiB, a blank side outside 1–8192 or over 40 megapixels, a line outside 1–50, a text size outside 8–200, a text over 200 characters and a 501st drawing are each refused by a message naming the limit");
    }

    static void Truncated()
    {
        var jpeg = Busy(320, 240);
        var cut = jpeg[..(jpeg.Length * 2 / 3)];
        var pixels = Decode(SharpImage.Load(cut).DrawRect(4, 4, 40, 40, SharpColor.Red, 4).ToJpeg());
        Check(pixels.Width == 320 && pixels.Height == 240 && pixels.Near(5, 20, SharpColor.Red), "a truncated picture is decoded and drawn on");
        Console.WriteLine("PASS sharp image: a JPEG with its last third cut off still decodes, and is drawn on");
    }

    static void Corrupted()
    {
        var good = Busy(320, 240);
        var random = new Random(20261118);
        int pictures = 0, refused = 0;
        for (var i = 0; i < 200; i++)
        {
            var variant = (byte[])good.Clone();
            if (i % 4 == 3) variant = variant[..random.Next(2, Math.Min(700, variant.Length))];
            else for (var k = random.Next(1, 12); k > 0; k--) variant[random.Next(2, variant.Length)] = (byte)random.Next(256);
            try
            {
                var image = SharpImage.Load(variant).DrawRect(1, 1, 10, 10);
                _ = image.Width;
                Check(image.ToJpeg() is [0xFF, 0xD8, ..], "a damaged picture that decodes gives a JPEG");
                pictures++;
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { refused++; }
            var after = Decode(SharpImage.Load(good).DrawRect(100, 100, 40, 40, SharpColor.Red, 4).ToJpeg());
            Check(after.Near(101, 120, SharpColor.Red), $"a good picture decodes right after damaged variant {i}");
        }
        Check(pictures > 0 && refused > 0, $"some damaged pictures decode ({pictures}) and some are refused ({refused})");
        Console.WriteLine($"PASS sharp image: 200 damaged pictures (bytes changed at random, headers cut) each give a picture ({pictures}) or a plain error ({refused}), and a good picture decodes right after each");
    }

    // A JPEG whose start-of-frame says 9000 × 9000, with the data of a small picture.
    static byte[] HugeHeaderJpeg()
    {
        var jpeg = SharpImage.Blank(64, 48).ToJpeg();
        for (var i = 2; i + 8 < jpeg.Length; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                jpeg[i + 5] = 9000 >> 8; jpeg[i + 6] = 9000 & 0xFF;
                jpeg[i + 7] = 9000 >> 8; jpeg[i + 8] = 9000 & 0xFF;
                return jpeg;
            }
        throw new Exception("no start of frame");
    }

    static void HugeHeader()
    {
        var image = SharpImage.Load(HugeHeaderJpeg());
        Check(image.Width == 9000 && image.Height == 9000, "the header is read");
        image.DrawRect(0, 0, 10, 10);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Throws<ArgumentException>(() => image.ToJpeg(), "a 9000 × 9000 picture");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(error.Message.Contains("40 megapixels", StringComparison.Ordinal), "the message names the limit: " + error.Message);
        Check(allocated < 1024 * 1024, $"no pixel buffer was rented ({allocated} bytes allocated)");
        Console.WriteLine("PASS sharp image: a picture whose header says 9000 × 9000 is refused by a message naming 40 megapixels, before any pixel buffer is rented");
    }

    static byte[] Frame(byte[] source)
    {
        var image = SharpImage.Load(source);
        for (var i = 0; i < 5; i++) image.DrawRect(40 + i * 200, 60 + i * 80, 160, 120, SharpColor.Red, 3);
        for (var i = 0; i < 3; i++) image.DrawText($"person {i + 1}: 0.9{i}", 44 + i * 200, 64 + i * 80, SharpColor.White, 24);
        return image.ToJpeg();
    }

    static void Allocation()
    {
        var source = Busy(1280, 720);
        Frame(source);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++) Frame(source);
        var average = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        Check(average < 1_500_000, $"a 1280 × 720 frame with 5 rectangles and 3 labels allocates under 1.5 MB on average, not {average} bytes");
        Console.WriteLine("PASS sharp image: a 1280 × 720 frame with 5 rectangles and 3 labels allocates under 1.5 MB of managed memory on average");
    }

    static void Threads()
    {
        var source = Busy(320, 240);
        var colours = new[] { SharpColor.Red, SharpColor.Green, SharpColor.Blue, SharpColor.Yellow, SharpColor.Orange, SharpColor.White, SharpColor.Black, SharpColor.FromRgb(0, 255, 255) };
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var start = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (var round = 0; round < 10; round++)
                {
                    var pixels = Decode(SharpImage.Load(source).DrawRect(200, 120, 60, 60, colours[t], 20).DrawText($"thread {t}", 8, 8).ToJpeg());
                    if (!pixels.Near(210, 150, colours[t])) failures.Enqueue($"thread {t} round {round}: {pixels.At(210, 150)}");
                }
            }
            catch (Exception error) { failures.Enqueue($"thread {t}: {error}"); }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        Check(failures.IsEmpty, "threads drawing at once: " + string.Join("; ", failures));
        Console.WriteLine("PASS sharp image: 8 threads drawing at once all give correct pictures");
    }

    static void WithoutTheLibrary()
    {
        var jpeg = Busy(64, 48);
        TurboJpeg.Disabled = true;
        try
        {
            Check(!TurboJpeg.Available, "the library can be made unavailable");
            var image = SharpImage.Load(jpeg);
            Check(image.ToJpeg().AsSpan().SequenceEqual(jpeg), "a picture with nothing drawn passes through without the library");
            image.DrawRect(1, 1, 10, 10);
            Check(Throws<InvalidOperationException>(() => image.ToJpeg(), "drawing without the library").Message == TurboJpeg.Missing, "drawing says the library is missing");
            Check(Throws<InvalidOperationException>(() => _ = image.Width, "the size without the library").Message == TurboJpeg.Missing, "the size says the library is missing");
            Check(Throws<InvalidOperationException>(() => SharpImage.Blank(8, 8).ToJpeg(), "a blank picture without the library").Message == TurboJpeg.Missing, "a blank picture says the library is missing");
            Check(TurboJpeg.Missing == "Drawing on a picture needs the libjpeg-turbo library (libturbojpeg), which this system does not have", "the message");
        }
        finally { TurboJpeg.Disabled = false; }
        Check(TurboJpeg.Available && SharpImage.Load(jpeg).DrawRect(1, 1, 5, 5).ToJpeg()[0] == 0xFF, "the library is back");
        Console.WriteLine("PASS sharp image: without libjpeg-turbo a picture loads and passes through, and drawing on it fails with one plain message naming the library");
    }

    static void UserCode()
    {
        var compiler = new NodeLibraryCompiler(new DiagnosticsProvider());
        var font = compiler.Preview([new NodeLibrarySource("fonts", """
            public static class Fonts
            {
                [Node("Scale")]
                public static float Scale() => StbTrueTypeSharp.StbTrueType.stbtt_ScaleForPixelHeight(null!, 16);
            }
            """, 1)]);
        Check(!font.Valid && font.Diagnostics.Any(d => d.Message.Contains("StbTrueTypeSharp", StringComparison.Ordinal)), "user code cannot name StbTrueTypeSharp: " + string.Join("; ", font.Diagnostics.Select(d => d.Message)));
        var pictures = compiler.Preview([new NodeLibrarySource("pictures", """
            public static class Pictures
            {
                [Node("Mark")]
                public static SharpImage Mark(SharpImage image) => image.DrawRect(1, 1, 20, 20, SharpColor.Red).DrawText("here", 2, 2, SharpColor.FromRgb(1, 2, 3));
            }
            """, 1)]);
        Check(pictures.Valid, "user code can use SharpImage: " + string.Join("; ", pictures.Diagnostics.Select(d => d.Message)));
        Check(!NodeLibraryCompiler.FrameworkReferences().Any(r => Path.GetFileNameWithoutExtension(r.Display) == "StbTrueTypeSharp")
            && !ProjectReferences.GetDefaultReferences().Any(r => Path.GetFileNameWithoutExtension(r.Display) == "StbTrueTypeSharp"), "StbTrueTypeSharp is not among the references");
        Console.WriteLine("PASS sharp image: user code compiled by the node library compiler cannot name StbTrueTypeSharp and can use SharpImage");
    }
}
