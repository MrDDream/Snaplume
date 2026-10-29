namespace Snaplume.Capturing;

/// <summary>What <see cref="ScrollStitcher.Add"/> did with a frame.</summary>
public enum StitchOutcome
{
    /// <summary>First frame: it starts the image.</summary>
    First,
    /// <summary>New rows were found below the previous frame and appended.</summary>
    Appended,
    /// <summary>Same content as the previous frame: nothing scrolled (end of the page).</summary>
    NoChange,
    /// <summary>The frame could not be matched with the previous one (scrolled too far, animated content...).</summary>
    NoMatch,
    /// <summary>The maximum height is reached: nothing more is appended.</summary>
    Full,
}

/// <param name="NewRows">Rows appended to the image by this frame.</param>
/// <param name="Shift">Rows the content moved up since the previous frame (0 if unknown).</param>
public readonly record struct StitchStep(StitchOutcome Outcome, int NewRows, int Shift);

/// <summary>
/// Stitches the successive frames of a scrolling area into one tall image. Pure: it only works on
/// 32-bit BGRA/BGRX pixel buffers (stride = width * 4), so it can be tested without a screen.
/// </summary>
/// <remarks>
/// Each new frame is compared with the previous one. Columns that did not change at all (fixed sidebar,
/// scrollbar, margins) are left out of the comparison, and so are the rows identical at the top (sticky
/// header) and at the bottom (footer). The remaining columns are split into strips; for every row and strip
/// a hash and the total brightness are computed. The shift is the offset at which the top of the new frame
/// matches the bottom of the previous one, counting the strips that differ (a few may: hover effects, caret).
/// Only the rows below the overlap are appended; the fixed footer of the last frame closes the image.
/// </remarks>
public sealed class ScrollStitcher
{
    public const int DefaultMaxHeight = 30000;

    /// <summary>Column strips compared separately (fewer on narrow areas).</summary>
    private const int MaxBins = 32;
    private const int MinBinWidth = 8;
    /// <summary>Share of the informative strips that may differ in the overlap (hover effects, caret, animations).</summary>
    private const double MaxMismatchRatio = 0.12;
    /// <summary>Candidates whose mismatch ratio is this close to the best one are considered equally good.</summary>
    private const double TieRatio = 0.02;
    /// <summary>Below this many non-uniform matching strips an overlap proves nothing (blank areas match anywhere).</summary>
    private const int MinInformativeCells = 8;
    private const int MinOverlapRows = 12;
    /// <summary>Brightness tolerance of a strip, per pixel, on the 0..1020 scale of B + 2G + R.</summary>
    private const int LumaTolerancePerPixel = 8;

    private readonly int _stride;

    private byte[] _body;
    private int _bodyRows;
    private byte[]? _previous;
    /// <summary>Bottom rows of the previous frame kept aside as a fixed footer (added at the end).</summary>
    private int _footer;
    private bool _completed;

    public ScrollStitcher(int width, int frameHeight, int maxHeight = DefaultMaxHeight)
    {
        if (width <= 0 || frameHeight <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Empty frame");
        if ((long)width * 4 * frameHeight > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(frameHeight), "Frame too large");
        Width = width;
        FrameHeight = frameHeight;
        MaxHeight = Math.Max(frameHeight, maxHeight);
        _stride = width * 4;
        _body = new byte[(long)_stride * Math.Min(MaxHeight, frameHeight * 2)];
    }

    public int Width { get; }
    public int FrameHeight { get; }
    public int MaxHeight { get; }

    /// <summary>Height of the stitched image so far.</summary>
    public int Height => _bodyRows + _footer;

    public bool IsFull => Height >= MaxHeight;

    /// <summary>Number of frames whose rows were used (the first one included).</summary>
    public int FramesUsed { get; private set; }

    /// <summary>Adds the next frame, captured after scrolling down.</summary>
    /// <param name="frame">BGRA pixels, <see cref="Width"/> x <see cref="FrameHeight"/>, stride = width * 4.</param>
    /// <param name="expectedShift">Likely shift (from the previous steps), used to choose between equally good matches; 0 if unknown.</param>
    public StitchStep Add(byte[] frame, int expectedShift = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_completed) throw new InvalidOperationException("The image is already complete");
        if (frame.Length < (long)_stride * FrameHeight) throw new ArgumentException("Frame buffer too small", nameof(frame));

        if (_previous == null)
        {
            AppendRows(frame, 0, FrameHeight);
            _footer = 0;
            Keep(frame);
            return new StitchStep(StitchOutcome.First, FrameHeight, 0);
        }
        if (IsFull) return new StitchStep(StitchOutcome.Full, 0, 0);

        // Columns identical over the whole height in both frames do not scroll: ignore them.
        var columns = ChangedColumns(_previous, frame);
        if (columns.Length == 0) return new StitchStep(StitchOutcome.NoChange, 0, 0);

        var strips = new Strips(columns);
        var match = new RowMatcher(strips.Analyze(_previous, this), strips.Analyze(frame, this), strips);
        int h = FrameHeight;

        // Nearly identical frames (a blinking caret...): nothing scrolled.
        if (match.Score(0, 0, h) is { } still && still.Ratio <= 0.02 && still.Informative >= MinInformativeCells)
            return new StitchStep(StitchOutcome.NoChange, 0, 0);

        // Fixed header and footer: rows that did not change at all.
        int header = 0;
        while (header < h && match.Equal(header, header)) header++;
        int footer = 0;
        while (footer < h - header && match.Equal(h - 1 - footer, h - 1 - footer)) footer++;
        header = Math.Min(header, h / 2);
        footer = Math.Min(footer, h / 3);

        int? shift = match.FindShift(header, h - footer, expectedShift);
        if (shift == null && (header > 0 || footer > 0))
        {
            // The "fixed" rows may only be uniform areas that happened to match: try the whole frame.
            footer = 0;
            shift = match.FindShift(0, h, expectedShift);
        }
        if (shift is not int d) return new StitchStep(StitchOutcome.NoMatch, 0, 0);

        SetFooter(footer);
        int newRows = Math.Min(d, MaxHeight - Height);
        if (newRows <= 0) return new StitchStep(StitchOutcome.Full, 0, d);
        // The body ends at row (h - footer) of the previous frame, i.e. at row (h - footer - d) of this one.
        AppendRows(frame, h - footer - d, newRows);
        Keep(frame);
        return new StitchStep(newRows < d || IsFull ? StitchOutcome.Full : StitchOutcome.Appended, newRows, d);
    }

    /// <summary>
    /// Ends the stitching and returns the image without copying it: the start of the internal buffer,
    /// BGRA, opaque, <see cref="Width"/> x <see cref="Height"/>, stride = width * 4 (the image can be
    /// tens of megapixels: no second copy is made). No frame can be added afterwards.
    /// </summary>
    public ArraySegment<byte> Complete()
    {
        if (_footer > 0 && _previous != null)
        {
            // The fixed footer closes the image (the buffer grows to the exact size if needed).
            EnsureCapacity((long)(_bodyRows + _footer) * _stride, exact: true);
            AppendRows(_previous, FrameHeight - _footer, _footer);
            _footer = 0;
        }
        _previous = null;
        _completed = true;
        int length = _stride * _bodyRows;
        // Screen copies leave the 4th byte undefined (BGRX): make every pixel opaque.
        for (int i = 3; i < length; i += 4) _body[i] = 255;
        return new ArraySegment<byte>(_body, 0, length);
    }

    private void Keep(byte[] frame)
    {
        _previous = frame;
        FramesUsed++;
    }

    /// <summary>
    /// Changes the number of footer rows. Invariant: the body ends at row (FrameHeight - footer) of the previous frame.
    /// </summary>
    private void SetFooter(int footer)
    {
        if (footer == _footer || _previous == null) return;
        if (footer > _footer)
        {
            // Those rows of the previous frame are the last ones of the body: they become footer.
            _bodyRows -= footer - _footer;
        }
        else
        {
            AppendRows(_previous, FrameHeight - _footer, _footer - footer);
        }
        _footer = footer;
    }

    private void AppendRows(byte[] source, int firstRow, int count)
    {
        if (count <= 0) return;
        EnsureCapacity((long)(_bodyRows + count) * _stride, exact: false);
        Buffer.BlockCopy(source, firstRow * _stride, _body, _bodyRows * _stride, count * _stride);
        _bodyRows += count;
    }

    /// <summary>
    /// Grows the body buffer to hold <paramref name="needed"/> bytes: doubling, but never beyond the
    /// maximum height (a 30000-row image must not reserve 60000 rows); exactly the size if <paramref name="exact"/>.
    /// </summary>
    private void EnsureCapacity(long needed, bool exact)
    {
        if (needed > int.MaxValue) throw new InvalidOperationException("Stitched image too large");
        if (needed <= _body.Length) return;
        long capacity = exact ? needed : Math.Max(needed, Math.Min((long)MaxHeight * _stride, (long)_body.Length * 2));
        Array.Resize(ref _body, (int)Math.Min(int.MaxValue, capacity));
    }

    /// <summary>Columns where at least one pixel differs between the two frames (colors only, the 4th byte is ignored).</summary>
    private int[] ChangedColumns(byte[] a, byte[] b)
    {
        var changed = new bool[Width];
        int remaining = Width;
        for (int y = 0; y < FrameHeight && remaining > 0; y++)
        {
            int row = y * _stride;
            for (int x = 0; x < Width; x++)
            {
                if (changed[x]) continue;
                int i = row + x * 4;
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])
                {
                    changed[x] = true;
                    remaining--;
                }
            }
        }
        var result = new List<int>();
        for (int x = 0; x < Width; x++)
        {
            if (changed[x]) result.Add(x);
        }
        return result.ToArray();
    }

    // ---- Frame analysis ----

    /// <summary>Per row and strip: a hash of the colors, the total brightness, and its color if it is uniform.</summary>
    private sealed class FrameInfo
    {
        public required ulong[] Hash { get; init; }
        public required int[] Luma { get; init; }
        /// <summary>Color of the strip if it is one plain color, else -1.</summary>
        public required long[] Plain { get; init; }
    }

    /// <summary>The compared columns, split into strips of neighbouring columns.</summary>
    private sealed class Strips
    {
        public readonly int[] Columns;
        public readonly int Count;
        /// <summary>Strip k covers Columns[Start[k]] .. Columns[Start[k + 1] - 1].</summary>
        public readonly int[] Start;

        public Strips(int[] columns)
        {
            Columns = columns;
            Count = Math.Clamp(columns.Length / MinBinWidth, 1, MaxBins);
            Start = new int[Count + 1];
            for (int k = 0; k <= Count; k++) Start[k] = (int)((long)k * columns.Length / Count);
        }

        public int Pixels(int k) => Start[k + 1] - Start[k];

        public FrameInfo Analyze(byte[] frame, ScrollStitcher owner)
        {
            int h = owner.FrameHeight, stride = owner._stride;
            var hash = new ulong[h * Count];
            var luma = new int[h * Count];
            var plain = new long[h * Count];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int k = 0; k < Count; k++)
                {
                    ulong hv = 14695981039346656037UL;
                    int sum = 0;
                    int f = row + Columns[Start[k]] * 4;
                    uint first = (uint)(frame[f] | frame[f + 1] << 8 | frame[f + 2] << 16);
                    bool uniform = true;
                    for (int c = Start[k]; c < Start[k + 1]; c++)
                    {
                        int i = row + Columns[c] * 4;
                        byte b = frame[i], g = frame[i + 1], r = frame[i + 2];
                        uint color = (uint)(b | g << 8 | r << 16);
                        hv = (hv ^ color) * 1099511628211UL;
                        sum += b + 2 * g + r;
                        if (color != first) uniform = false;
                    }
                    int index = y * Count + k;
                    hash[index] = hv;
                    luma[index] = sum;
                    plain[index] = uniform ? first : -1;
                }
            }
            return new FrameInfo { Hash = hash, Luma = luma, Plain = plain };
        }
    }

    private readonly record struct OverlapScore(double Ratio, int Informative);

    /// <summary>Compares rows of the previous frame and of the new one, strip by strip.</summary>
    private sealed class RowMatcher(FrameInfo prev, FrameInfo cur, Strips strips)
    {
        private readonly int _bins = strips.Count;

        /// <summary>Exactly the same pixels (on the compared columns).</summary>
        public bool Equal(int curRow, int prevRow)
        {
            int c = curRow * _bins, p = prevRow * _bins;
            for (int k = 0; k < _bins; k++)
            {
                if (cur.Hash[c + k] != prev.Hash[p + k]) return false;
            }
            return true;
        }

        /// <summary>
        /// Strip mismatch ratio when the content moved up by <paramref name="shift"/> rows: row y of the new frame
        /// against row y + shift of the previous one, for y in [top, bottom - shift). Null if clearly wrong.
        /// Strips of the same plain color in both frames match anywhere: they are not counted.
        /// </summary>
        public OverlapScore? Score(int shift, int top, int bottom)
        {
            int overlap = bottom - top - shift;
            if (overlap <= 0) return null;
            long allowed = (long)(overlap * (long)_bins * MaxMismatchRatio);
            int mismatches = 0, informative = 0;
            for (int y = top; y < bottom - shift; y++)
            {
                int c = y * _bins, p = (y + shift) * _bins;
                for (int k = 0; k < _bins; k++)
                {
                    if (cur.Hash[c + k] == prev.Hash[p + k])
                    {
                        if (cur.Plain[c + k] < 0) informative++;
                    }
                    else if (Math.Abs(cur.Luma[c + k] - prev.Luma[p + k]) <= strips.Pixels(k) * LumaTolerancePerPixel)
                    {
                        // Nearly the same brightness: anti-aliasing or dithering differences.
                        informative++;
                    }
                    else if (++mismatches > allowed)
                    {
                        return null;
                    }
                }
            }
            int counted = informative + mismatches;
            return counted == 0 ? new OverlapScore(1, 0) : new OverlapScore((double)mismatches / counted, informative);
        }

        /// <summary>Best shift in the content rows [top, bottom), or null if none is convincing.</summary>
        public int? FindShift(int top, int bottom, int expected)
        {
            int content = bottom - top;
            int minOverlap = Math.Max(MinOverlapRows, content / 8);
            var candidates = new List<(int Shift, double Ratio)>();
            for (int d = 1; d <= content - minOverlap; d++)
            {
                if (Score(d, top, bottom) is { } s && s.Informative >= MinInformativeCells && s.Ratio <= MaxMismatchRatio)
                    candidates.Add((d, s.Ratio));
            }
            if (candidates.Count == 0) return null;
            double best = candidates.Min(c => c.Ratio);
            // Repetitive content (identical list rows) matches at several shifts: prefer the expected one,
            // else the smallest shift (largest overlap, the most evidence).
            return expected > 0
                ? candidates.Where(c => c.Ratio <= best + TieRatio).OrderBy(c => Math.Abs(c.Shift - expected)).ThenBy(c => c.Ratio).First().Shift
                : candidates.Where(c => c.Ratio <= best + 0.005).OrderBy(c => c.Shift).First().Shift;
        }
    }
}
