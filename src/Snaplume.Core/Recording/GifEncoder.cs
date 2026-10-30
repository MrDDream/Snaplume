namespace Snaplume.Recording;

/// <summary>Settings of an animated GIF written by <see cref="GifEncoder"/>.</summary>
public sealed class GifEncoderOptions
{
    /// <summary>Plays the animation again and again (NETSCAPE2.0 extension); otherwise once.</summary>
    public bool LoopForever { get; init; } = true;

    /// <summary>Floyd–Steinberg dithering: smoother gradients and photos, but bigger files.</summary>
    public bool Dither { get; init; }

    /// <summary>
    /// Only the pixels that changed since the previous frame are stored (cropped frame, the
    /// others transparent) and frames identical to the previous one extend its delay.
    /// Much smaller files for screen recordings, where most of the picture stays still.
    /// </summary>
    public bool Optimize { get; init; } = true;

    /// <summary>
    /// Largest difference (per channel, 0–255) still seen as "unchanged" by <see cref="Optimize"/>:
    /// absorbs the noise of video compression.
    /// </summary>
    public int Tolerance { get; init; } = 3;
}

/// <summary>
/// Self-contained animated GIF (GIF89a) writer: one palette of up to 256 colors per frame
/// (octree quantizer), LZW compression, per-frame delays and the loop extension.
/// No dependency on WPF or Windows: frames are BGRA bytes, the output any writable stream.
/// </summary>
/// <remarks>
/// Usage: create, call <see cref="AddFrame"/> for each picture in order, then <see cref="Finish"/>,
/// then dispose. Disposing without <see cref="Finish"/> (e.g. after a cancellation) leaves an
/// incomplete file. Frames are written one by one: memory does not grow with the length of the animation.
/// Not thread-safe; use one encoder per output.
/// </remarks>
public sealed class GifEncoder : IDisposable
{
    /// <summary>Longest delay of one frame in a GIF (unsigned 16-bit, hundredths of a second).</summary>
    public const int MaxDelay = ushort.MaxValue;

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly GifEncoderOptions _options;
    /// <summary>Source color (B, G, R) last stored for each pixel: what the previous frames show.</summary>
    private readonly byte[] _reference;
    private readonly bool[] _changed;
    /// <summary>LZW dictionary, reused from frame to frame (always left cleared).</summary>
    private readonly short[] _lzwTable = new short[LzwEncoder.MaxCodes * 256];
    /// <summary>Nearest palette entries, shared by the frames (each frame's quantizer starts a new generation).</summary>
    private readonly NearestCache _nearest = new();
    private bool _hasReference;
    private bool _headerWritten;
    private bool _finished;

    // Frame waiting for its delay to be known (the next frames may be identical and extend it).
    private byte[]? _pending;
    private int _pendingDelay;
    private bool _pendingTransparent;
    private int _pendingTransparentIndex;

    /// <param name="output">Receives the GIF; written sequentially (no seeking needed).</param>
    /// <param name="width">Size of every frame, in pixels (1–65535).</param>
    /// <param name="leaveOpen">Leaves <paramref name="output"/> open when the encoder is disposed.</param>
    public GifEncoder(Stream output, int width, int height, GifEncoderOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (width is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(height));
        _output = output;
        _leaveOpen = leaveOpen;
        _options = options ?? new GifEncoderOptions();
        Width = width;
        Height = height;
        _reference = new byte[(long)width * height * 3];
        _changed = new bool[(long)width * height];
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Frames written so far (identical frames merged by <see cref="GifEncoderOptions.Optimize"/> count once).</summary>
    public int FrameCount { get; private set; }

    /// <summary>
    /// Delays (hundredths of a second) of <paramref name="count"/> frames shown at
    /// <paramref name="framesPerSecond"/>: rounded on the running total, so the animation
    /// keeps the right length (12 fps gives 8, 9, 8, 8, 9, 8...).
    /// </summary>
    public static int[] FrameDelays(int count, double framesPerSecond)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (!(framesPerSecond > 0)) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        var delays = new int[count];
        long previous = 0;
        for (int i = 0; i < count; i++)
        {
            long next = (long)Math.Round((i + 1) * 100.0 / framesPerSecond, MidpointRounding.AwayFromZero);
            delays[i] = (int)Math.Clamp(next - previous, 1, MaxDelay);
            previous = next;
        }
        return delays;
    }

    /// <summary>Adds a picture shown for <paramref name="delayCentiseconds"/> hundredths of a second.</summary>
    /// <param name="bgra">Pixels, 4 bytes each (blue, green, red, alpha ignored), <paramref name="stride"/> bytes per row.</param>
    public void AddFrame(ReadOnlySpan<byte> bgra, int stride, int delayCentiseconds)
    {
        if (_finished) throw new InvalidOperationException("The GIF is already finished.");
        if (stride < Width * 4) throw new ArgumentOutOfRangeException(nameof(stride));
        if (bgra.Length < (long)stride * (Height - 1) + Width * 4) throw new ArgumentException("Not enough pixels for the frame size.", nameof(bgra));
        int delay = Math.Clamp(delayCentiseconds, 0, MaxDelay);

        if (!_headerWritten)
        {
            WriteHeader();
            _headerWritten = true;
        }

        // Pixels to store in this frame, and the rectangle around them.
        bool optimize = _options.Optimize && _hasReference;
        int tolerance = Math.Clamp(_options.Tolerance, 0, 255);
        int left = Width, top = Height, right = -1, bottom = -1;
        int unchangedCount = 0;
        for (int y = 0; y < Height; y++)
        {
            var row = bgra.Slice(y * stride, Width * 4);
            int refRow = y * Width * 3;
            for (int x = 0; x < Width; x++)
            {
                int s = x * 4, r = refRow + x * 3;
                bool changed = !optimize ||
                               Math.Abs(row[s] - _reference[r]) > tolerance ||
                               Math.Abs(row[s + 1] - _reference[r + 1]) > tolerance ||
                               Math.Abs(row[s + 2] - _reference[r + 2]) > tolerance;
                _changed[y * Width + x] = changed;
                if (!changed)
                {
                    unchangedCount++;
                    continue;
                }
                _reference[r] = row[s];
                _reference[r + 1] = row[s + 1];
                _reference[r + 2] = row[s + 2];
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        _hasReference = true;

        if (right < 0)
        {
            // Nothing changed: the previous frame simply stays longer.
            if (_pending != null && _pendingDelay + delay <= MaxDelay)
            {
                _pendingDelay += delay;
                return;
            }
            // (Or a transparent pixel, when its delay is full.)
            left = top = right = bottom = 0;
        }

        int w = right - left + 1, h = bottom - top + 1;
        bool transparent = unchangedCount > 0 && HasUnchanged(left, top, w, h);
        var image = EncodeImage(bgra, stride, left, top, w, h, transparent, out int transparentIndex);

        FlushPending();
        _pending = image;
        _pendingDelay = delay;
        _pendingTransparent = transparent;
        _pendingTransparentIndex = transparentIndex;
    }

    /// <summary>Writes the last frame and the end of the file. Further frames are refused.</summary>
    public void Finish()
    {
        if (_finished) return;
        if (!_headerWritten)
        {
            // An empty animation is still a valid (blank) GIF.
            WriteHeader();
            _headerWritten = true;
        }
        FlushPending();
        _output.WriteByte(0x3B); // trailer
        _output.Flush();
        _finished = true;
    }

    /// <summary>Closes the output (unless left open); does not <see cref="Finish"/> the GIF.</summary>
    public void Dispose()
    {
        if (!_leaveOpen) _output.Dispose();
    }

    private bool HasUnchanged(int left, int top, int w, int h)
    {
        for (int y = top; y < top + h; y++)
        {
            int start = y * Width + left;
            for (int i = start; i < start + w; i++)
            {
                if (!_changed[i]) return true;
            }
        }
        return false;
    }

    private void WriteHeader()
    {
        var s = _output;
        s.Write("GIF89a"u8);
        // Logical screen: no global color table (each frame has its own), 8 bits per primary color.
        WriteUInt16(Width);
        WriteUInt16(Height);
        s.WriteByte(0x70);
        s.WriteByte(0); // background color index
        s.WriteByte(0); // square pixels
        if (_options.LoopForever)
        {
            s.WriteByte(0x21); // extension
            s.WriteByte(0xFF); // application
            s.WriteByte(11);
            s.Write("NETSCAPE2.0"u8);
            s.WriteByte(3);
            s.WriteByte(1);
            WriteUInt16(0); // repetitions: 0 = forever
            s.WriteByte(0);
        }
    }

    private void FlushPending()
    {
        if (_pending == null) return;
        var s = _output;
        // Graphic control extension: delay, transparency, "leave in place" disposal.
        s.WriteByte(0x21);
        s.WriteByte(0xF9);
        s.WriteByte(4);
        s.WriteByte((byte)((1 << 2) | (_pendingTransparent ? 1 : 0)));
        WriteUInt16(_pendingDelay);
        s.WriteByte((byte)(_pendingTransparent ? _pendingTransparentIndex : 0));
        s.WriteByte(0);
        s.Write(_pending);
        _pending = null;
        FrameCount++;
    }

    private void WriteUInt16(int value)
    {
        _output.WriteByte((byte)value);
        _output.WriteByte((byte)(value >> 8));
    }

    /// <summary>Image descriptor, local color table and LZW data of the changed pixels of a rectangle.</summary>
    private byte[] EncodeImage(ReadOnlySpan<byte> bgra, int stride, int left, int top, int w, int h, bool transparent, out int transparentIndex)
    {
        // Palette of the stored pixels (one entry is kept for transparency).
        var quantizer = new OctreeQuantizer(_nearest);
        for (int y = top; y < top + h; y++)
        {
            int rowStart = y * stride;
            for (int x = left; x < left + w; x++)
            {
                if (!_changed[y * Width + x]) continue;
                int s = rowStart + x * 4;
                quantizer.Add(bgra[s + 2], bgra[s + 1], bgra[s]);
            }
        }
        var palette = quantizer.BuildPalette(transparent ? 255 : 256);
        int colors = palette.Length / 3 + (transparent ? 1 : 0);
        int bits = 1;
        while ((1 << bits) < colors) bits++;
        transparentIndex = transparent ? palette.Length / 3 : 0;

        var indices = new byte[w * h];
        if (_options.Dither) MapDithered(bgra, stride, left, top, w, h, quantizer, palette, indices, (byte)transparentIndex);
        else MapNearest(bgra, stride, left, top, w, h, quantizer, indices, (byte)transparentIndex);

        using var image = new MemoryStream(w * h / 2 + 1024);
        image.WriteByte(0x2C);
        WriteUInt16(image, left);
        WriteUInt16(image, top);
        WriteUInt16(image, w);
        WriteUInt16(image, h);
        image.WriteByte((byte)(0x80 | (bits - 1))); // local color table, not interlaced
        image.Write(palette);
        for (int i = palette.Length; i < 3 << bits; i++) image.WriteByte(0); // table padded to a power of two
        LzwEncoder.Encode(image, indices, Math.Max(2, bits), _lzwTable);
        return image.ToArray();
    }

    private void MapNearest(ReadOnlySpan<byte> bgra, int stride, int left, int top, int w, int h,
        OctreeQuantizer quantizer, byte[] indices, byte transparentIndex)
    {
        for (int y = 0; y < h; y++)
        {
            int rowStart = (top + y) * stride;
            for (int x = 0; x < w; x++)
            {
                if (!_changed[(top + y) * Width + left + x])
                {
                    indices[y * w + x] = transparentIndex;
                    continue;
                }
                int s = rowStart + (left + x) * 4;
                indices[y * w + x] = (byte)quantizer.IndexOf(bgra[s + 2], bgra[s + 1], bgra[s]);
            }
        }
    }

    /// <summary>Floyd–Steinberg error diffusion, limited to the stored pixels.</summary>
    private void MapDithered(ReadOnlySpan<byte> bgra, int stride, int left, int top, int w, int h,
        OctreeQuantizer quantizer, byte[] palette, byte[] indices, byte transparentIndex)
    {
        // Errors in 1/16 units, for this row and the next one (one pixel of margin on each side).
        var current = new int[(w + 2) * 3];
        var next = new int[(w + 2) * 3];
        for (int y = 0; y < h; y++)
        {
            Array.Clear(next);
            int rowStart = (top + y) * stride;
            for (int x = 0; x < w; x++)
            {
                if (!_changed[(top + y) * Width + left + x])
                {
                    indices[y * w + x] = transparentIndex;
                    continue;
                }
                int s = rowStart + (left + x) * 4;
                int e = (x + 1) * 3;
                int r = Math.Clamp(bgra[s + 2] + (current[e] + 8) / 16, 0, 255);
                int g = Math.Clamp(bgra[s + 1] + (current[e + 1] + 8) / 16, 0, 255);
                int b = Math.Clamp(bgra[s] + (current[e + 2] + 8) / 16, 0, 255);
                int index = quantizer.IndexOf(r, g, b);
                indices[y * w + x] = (byte)index;
                int er = r - palette[index * 3], eg = g - palette[index * 3 + 1], eb = b - palette[index * 3 + 2];
                Spread(current, e + 3, er, eg, eb, 7);
                Spread(next, e - 3, er, eg, eb, 3);
                Spread(next, e, er, eg, eb, 5);
                Spread(next, e + 3, er, eg, eb, 1);
            }
            (current, next) = (next, current);
        }

        static void Spread(int[] errors, int i, int r, int g, int b, int weight)
        {
            errors[i] += r * weight;
            errors[i + 1] += g * weight;
            errors[i + 2] += b * weight;
        }
    }

    private static void WriteUInt16(Stream s, int value)
    {
        s.WriteByte((byte)value);
        s.WriteByte((byte)(value >> 8));
    }

    /// <summary>Variable-length LZW of GIF: codes of 3 to 12 bits, packed LSB first in blocks of 255 bytes.</summary>
    private static class LzwEncoder
    {
        public const int MaxCodes = 4096;

        /// <param name="table">Child code of (code, next index), 0 when absent (real codes are always
        /// above "end"); MaxCodes × 256 entries, all zero on entry and on return.</param>
        public static void Encode(Stream s, byte[] indices, int minCodeSize, short[] table)
        {
            s.WriteByte((byte)minCodeSize);
            var writer = new BlockWriter(s);
            int clear = 1 << minCodeSize, end = clear + 1;
            int alphabet = clear;
            int codeSize = minCodeSize + 1;
            int nextCode = end + 1;

            writer.Write(clear, codeSize);
            if (indices.Length > 0)
            {
                int prefix = indices[0];
                for (int i = 1; i < indices.Length; i++)
                {
                    int symbol = indices[i];
                    int slot = prefix * alphabet + symbol;
                    int child = table[slot];
                    if (child != 0)
                    {
                        prefix = child;
                        continue;
                    }
                    writer.Write(prefix, codeSize);
                    if (nextCode < MaxCodes)
                    {
                        table[slot] = (short)nextCode;
                        // The decoder widens its codes once it has filled this size (it lags one code behind).
                        if (nextCode == 1 << codeSize && codeSize < 12) codeSize++;
                        nextCode++;
                    }
                    else
                    {
                        // Table full: start a new one.
                        writer.Write(clear, codeSize);
                        Array.Clear(table, 0, MaxCodes * alphabet);
                        codeSize = minCodeSize + 1;
                        nextCode = end + 1;
                    }
                    prefix = symbol;
                }
                writer.Write(prefix, codeSize);
                // The decoder adds one more entry on reading that last code: if this fills the current
                // size, it reads the end code one bit wider (as giflib writes it).
                if (nextCode == 1 << codeSize && codeSize < 12) codeSize++;
            }
            // Only codes below nextCode have children.
            Array.Clear(table, 0, nextCode * alphabet);
            writer.Write(end, codeSize);
            writer.Flush();
            s.WriteByte(0); // block terminator
        }

        private sealed class BlockWriter
        {
            private readonly Stream _stream;
            private readonly byte[] _block = new byte[256];
            private int _count;
            private int _bits;
            private int _bitCount;

            public BlockWriter(Stream stream) => _stream = stream;

            public void Write(int code, int size)
            {
                _bits |= code << _bitCount;
                _bitCount += size;
                while (_bitCount >= 8)
                {
                    Put((byte)_bits);
                    _bits >>= 8;
                    _bitCount -= 8;
                }
            }

            public void Flush()
            {
                if (_bitCount > 0) Put((byte)_bits);
                _bits = 0;
                _bitCount = 0;
                if (_count > 0) WriteBlock();
            }

            private void Put(byte value)
            {
                _block[1 + _count++] = value;
                if (_count == 255) WriteBlock();
            }

            private void WriteBlock()
            {
                _block[0] = (byte)_count;
                _stream.Write(_block, 0, _count + 1);
                _count = 0;
            }
        }
    }

    /// <summary>
    /// Closest palette entry for each 6-bit-per-channel cell (2^18 cells). Allocated on first use
    /// (frames without dithering rarely need it) and never cleared between frames: each entry is
    /// tagged with the generation of the palette it was computed for.
    /// </summary>
    private sealed class NearestCache
    {
        private const int Cells = 1 << 18;
        /// <summary>Generations fit in the 23 upper bits of an entry, the index in the 8 lower ones.</summary>
        private const int MaxGeneration = (1 << 23) - 1;

        private int[]? _entries; // (generation << 8) | index; 0 = never set
        private int _generation;

        /// <summary>A new palette: the entries of the previous ones no longer count.</summary>
        public void NextPalette()
        {
            if (++_generation <= MaxGeneration) return;
            if (_entries != null) Array.Clear(_entries);
            _generation = 1;
        }

        public bool TryGet(int key, out int index)
        {
            int entry = _entries?[key] ?? 0;
            index = entry & 0xFF;
            return entry != 0 && entry >> 8 == _generation;
        }

        public void Set(int key, int index)
        {
            _entries ??= new int[Cells];
            _entries[key] = (_generation << 8) | index;
        }
    }

    /// <summary>
    /// Octree color quantizer: colors are sorted into a tree by their bits, then the least used
    /// branches are merged until the palette fits. Exact colors (UI, text) stay exact when few.
    /// </summary>
    private sealed class OctreeQuantizer
    {
        /// <summary>Leaves kept while adding colors (merged down to the palette size at the end).</summary>
        private const int WorkingLeaves = 4096;

        private sealed class Node
        {
            public readonly int Level;
            public Node?[]? Children;
            public long Count; // pixels that went through this node
            public long R, G, B; // sum of the colors of a leaf
            public int PaletteIndex;

            public Node(int level) => Level = level;

            public bool IsLeaf => Children == null;
        }

        private readonly Node _root;
        /// <summary>Nodes with children, by level (merging always starts with the deepest).</summary>
        private readonly List<Node>[] _reducible = new List<Node>[8];
        private int _leafCount;
        private readonly NearestCache _nearest;
        private byte[] _palette = Array.Empty<byte>();

        /// <param name="nearest">Cache of the closest palette entries, reset for this palette by <see cref="BuildPalette"/>.</param>
        public OctreeQuantizer(NearestCache nearest)
        {
            _nearest = nearest;
            for (int i = 0; i < 8; i++) _reducible[i] = new List<Node>();
            _root = new Node(0) { Children = new Node?[8] };
            _reducible[0].Add(_root);
        }

        public void Add(int r, int g, int b)
        {
            var node = _root;
            while (true)
            {
                node.Count++;
                if (node.IsLeaf)
                {
                    node.R += r;
                    node.G += g;
                    node.B += b;
                    return;
                }
                int i = ChildIndex(r, g, b, node.Level);
                var child = node.Children![i];
                if (child == null)
                {
                    int level = node.Level + 1;
                    child = new Node(level);
                    if (level < 8)
                    {
                        child.Children = new Node?[8];
                        _reducible[level].Add(child);
                    }
                    else
                    {
                        _leafCount++;
                    }
                    node.Children[i] = child;
                }
                node = child;
                if (node.IsLeaf && _leafCount > WorkingLeaves)
                {
                    // Account for this pixel before the tree changes under it.
                    node.Count++;
                    node.R += r;
                    node.G += g;
                    node.B += b;
                    while (_leafCount > WorkingLeaves / 2) Reduce();
                    return;
                }
            }
        }

        /// <summary>Palette as R, G, B triplets, at most <paramref name="maxColors"/> (at least one).</summary>
        public byte[] BuildPalette(int maxColors)
        {
            while (_leafCount > maxColors) Reduce();
            var colors = new List<byte>(maxColors * 3);
            int index = 0;
            Collect(_root, colors, ref index);
            if (index == 0) colors.AddRange(new byte[] { 0, 0, 0 });
            _palette = colors.ToArray();
            _nearest.NextPalette();
            return _palette;
        }

        /// <summary>Palette index of a color: its own leaf, else the closest entry (colors moved by dithering).</summary>
        public int IndexOf(int r, int g, int b)
        {
            var node = _root;
            while (!node.IsLeaf)
            {
                var child = node.Children![ChildIndex(r, g, b, node.Level)];
                if (child == null) return Nearest(r, g, b);
                node = child;
            }
            return node.Count > 0 ? node.PaletteIndex : Nearest(r, g, b);
        }

        private int Nearest(int r, int g, int b)
        {
            // Cached for each 6-bit-per-channel cell.
            int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
            if (_nearest.TryGet(key, out int cached)) return cached;
            int best = 0;
            long bestDistance = long.MaxValue;
            for (int i = 0; i < _palette.Length / 3; i++)
            {
                // Weighted like the eye's sensitivity (green most, blue least).
                int dr = r - _palette[i * 3], dg = g - _palette[i * 3 + 1], db = b - _palette[i * 3 + 2];
                long distance = 3L * dr * dr + 4L * dg * dg + 2L * db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            _nearest.Set(key, best);
            return best;
        }

        private static int ChildIndex(int r, int g, int b, int level)
        {
            int shift = 7 - level;
            return (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);
        }

        /// <summary>Merges the children (all leaves) of the least used node of the deepest level.</summary>
        private void Reduce()
        {
            int level = 7;
            while (level > 0 && _reducible[level].Count == 0) level--;
            var list = _reducible[level];
            if (list.Count == 0) return;
            int pick = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].Count < list[pick].Count) pick = i;
            }
            var node = list[pick];
            list[pick] = list[^1];
            list.RemoveAt(list.Count - 1);

            int merged = 0;
            foreach (var child in node.Children!)
            {
                if (child == null) continue;
                node.R += child.R;
                node.G += child.G;
                node.B += child.B;
                merged++;
            }
            node.Children = null;
            _leafCount -= merged - 1;
        }

        private static void Collect(Node node, List<byte> colors, ref int index)
        {
            if (node.IsLeaf)
            {
                if (node.Count == 0) return;
                node.PaletteIndex = index++;
                colors.Add((byte)(node.R / node.Count));
                colors.Add((byte)(node.G / node.Count));
                colors.Add((byte)(node.B / node.Count));
                return;
            }
            foreach (var child in node.Children!)
            {
                if (child != null) Collect(child, colors, ref index);
            }
        }
    }
}
