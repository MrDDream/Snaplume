using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Player;
using Snaplume.Tests.Fakes;

namespace Snaplume.Tests;

/// <summary>The GIF export of the player: the dialog's rules (<see cref="GifExportForm"/>) and the export itself on a fake video.</summary>
public class CaptureGifExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "SnaplumeGifTests_" + Guid.NewGuid().ToString("N"));

    public CaptureGifExportTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FormProposesTheSelectionAndAWidth()
    {
        var form = new GifExportForm(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), null, 1920, 1080);
        Assert.True(form.UseSelection);
        Assert.Equal("800", form.WidthText);
        Assert.True(form.TryRead(out var settings, out _));
        Assert.Equal(TimeSpan.FromSeconds(2), settings.Start);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.End);
        Assert.Equal(800, settings.Width);
        Assert.Equal(450, settings.Height); // same ratio as the video
        Assert.Equal(12 * 18, settings.FrameCount);

        form.UseSelection = false;
        form.Speed = 2;
        Assert.True(form.TryRead(out var whole, out _));
        Assert.Equal(TimeSpan.Zero, whole.Start);
        Assert.Equal(12 * 10, whole.FrameCount); // twice as fast: half the pictures

        // A small video: its own width is proposed.
        Assert.Equal("640", new GifExportForm(TimeSpan.FromSeconds(5), null, null, 640, 480).WidthText);
    }

    [Theory]
    [InlineData("4", "800")]   // too few pictures per second
    [InlineData("31", "800")]  // too many
    [InlineData("12", "8")]    // too narrow
    [InlineData("12", "4000")] // wider than the video
    [InlineData("abc", "800")]
    public void FormRefusesBadValues(string fps, string width)
    {
        var form = new GifExportForm(TimeSpan.FromSeconds(20), null, null, 1920, 1080)
        {
            FramesPerSecondText = fps,
            WidthText = width,
        };
        Assert.False(form.TryRead(out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void FormKeepsTheGifWithinItsLargestSizeAndASaneSpeed()
    {
        // A very tall video (a scrolling capture made into a video): the height would pass 65535.
        var tall = new GifExportForm(TimeSpan.FromSeconds(2), null, null, 2000, 200_000) { WidthText = "800" };
        Assert.False(tall.TryRead(out _, out var error));
        Assert.Equal(Loc.T("Player.Gif.WidthError", GifExportForm.SmallestWidth, tall.LargestWidth), error);
        Assert.Equal(655, tall.LargestWidth);
        tall.WidthText = "655";
        Assert.True(tall.TryRead(out var settings, out _));
        Assert.True(settings.Height <= 65535);

        foreach (var speed in new[] { double.NaN, double.PositiveInfinity, 0, -2 })
        {
            var form = new GifExportForm(TimeSpan.FromSeconds(5), null, null, 640, 480) { Speed = speed };
            Assert.True(form.TryRead(out var read, out _));
            Assert.Equal(1, read.Speed);
        }
    }

    [Fact]
    public void FormRefusesTooManyPictures()
    {
        // 30 pictures per second for 200 s: 6000 > 3000.
        var form = new GifExportForm(TimeSpan.FromSeconds(200), null, null, 640, 360) { FramesPerSecondText = "30" };
        Assert.False(form.TryRead(out _, out _));
    }

    [Fact]
    public void FrameTimesStayInsideTheVideo()
    {
        var settings = new GifExportSettings(TimeSpan.FromSeconds(1.8), TimeSpan.FromSeconds(2.5), 10, 100, 50, 1, true, false);
        var times = VideoGifExport.FrameTimes(settings, TimeSpan.FromSeconds(2));
        Assert.Equal(settings.FrameCount, times.Count);
        Assert.All(times, t => Assert.True(t <= TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(1.8), times[0]);
    }

    [Fact]
    public async Task ExportWritesAnAnimatedGif()
    {
        var video = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(video, new byte[] { 1 });
        var gif = Path.Combine(_folder, "clip.gif");
        var tools = new FakeVideoTools { NativeSize = (64, 32) }; // frames of another size are scaled
        var settings = new GifExportSettings(TimeSpan.Zero, TimeSpan.FromSeconds(1), 10, 40, 20, 1, true, false);
        var reports = new List<double>();
        await VideoGifExport.ExportAsync(tools, video, gif, settings, TimeSpan.FromSeconds(5), new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(10, tools.RequestedTimes.Count);
        var bytes = File.ReadAllBytes(gif);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        var first = ImageIO.Load(gif);
        Assert.Equal((40, 20), (first.Width, first.Height));
        Assert.Equal(1.0, reports[^1], 3);
    }

    [Fact]
    public async Task FailedOrCancelledExportLeavesNoFile()
    {
        var video = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(video, new byte[] { 1 });
        var settings = new GifExportSettings(TimeSpan.Zero, TimeSpan.FromSeconds(1), 10, 40, 20, 1, true, false);

        var broken = Path.Combine(_folder, "broken.gif");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VideoGifExport.ExportAsync(new FakeVideoTools { FramesAvailable = 3 }, video, broken, settings, TimeSpan.FromSeconds(5), null, CancellationToken.None));
        Assert.False(File.Exists(broken));

        var cancelled = Path.Combine(_folder, "cancelled.gif");
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cancel.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VideoGifExport.ExportAsync(new FakeVideoTools(), video, cancelled, settings, TimeSpan.FromSeconds(5), progress, cancel.Token));
        Assert.False(File.Exists(cancelled));

        // Never over the video or another file.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            VideoGifExport.ExportAsync(new FakeVideoTools(), video, video, settings, TimeSpan.FromSeconds(5), null, CancellationToken.None));
        Assert.Single(File.ReadAllBytes(video));
    }

    /// <summary>Reports at once on the calling thread (Progress&lt;T&gt; would post them later).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
