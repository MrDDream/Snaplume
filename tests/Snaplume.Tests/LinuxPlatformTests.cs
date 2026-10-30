using Avalonia.Input;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;
using Snaplume.Platform.Linux;
using Snaplume.Platform.Linux.DBus;
using Snaplume.Platform.Linux.Shell;
using Snaplume.Platform.Linux.Util;
using Snaplume.Platform.Linux.Wayland;
using Snaplume.Platform.Linux.X11;
using Tmds.DBus.Protocol;

namespace Snaplume.Tests;

/// <summary>Pure logic of Snaplume.Platform.Linux: session, X11 helpers, portals, files of the desktop.</summary>
public class LinuxPlatformTests
{
    static LinuxPlatformTests() => Loc.Initialize("fr");

    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Session_is_detected_from_the_environment()
    {
        Assert.Equal(SessionKind.Wayland, SessionInfo.Detect(Env(("XDG_SESSION_TYPE", "wayland"), ("DISPLAY", ":0"))));
        Assert.Equal(SessionKind.X11, SessionInfo.Detect(Env(("XDG_SESSION_TYPE", "x11"), ("WAYLAND_DISPLAY", "wayland-0"))));
        Assert.Equal(SessionKind.Wayland, SessionInfo.Detect(Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"))));
        Assert.Equal(SessionKind.X11, SessionInfo.Detect(Env(("DISPLAY", ":0"))));
        Assert.Equal(SessionKind.X11, SessionInfo.Detect(Env(("XDG_SESSION_TYPE", "tty"))));
        Assert.Equal(SessionKind.X11, SessionInfo.Detect(Env(("SNAPLUME_SESSION", "X11"), ("XDG_SESSION_TYPE", "wayland"))));
    }

    [Fact]
    public void Wayland_features_follow_the_portals()
    {
        var x11 = SessionInfo.Features(SessionKind.X11, false, false);
        Assert.True(x11.HasFlag(PlatformFeatures.WindowDetection | PlatformFeatures.GlobalHotkeys | PlatformFeatures.RecordAnyArea));
        Assert.False(x11.HasFlag(PlatformFeatures.ExcludeFromCapture));

        var bare = SessionInfo.Features(SessionKind.Wayland, false, false);
        Assert.Equal(PlatformFeatures.StartWithSession, bare);
        var full = SessionInfo.Features(SessionKind.Wayland, true, true);
        Assert.Equal(PlatformFeatures.StartWithSession | PlatformFeatures.GlobalHotkeys | PlatformFeatures.ScrollingCapture, full);
        Assert.False(full.HasFlag(PlatformFeatures.ClickHighlight));
        Assert.False(full.HasFlag(PlatformFeatures.ActiveWindow));
    }

    [Theory]
    [InlineData("Ctrl+Shift+PrintScreen", "CTRL+SHIFT+Print")]
    [InlineData("PrintScreen", "Print")]
    [InlineData("Alt+F13", "ALT+F13")]
    [InlineData("Win+A", "LOGO+a")]
    [InlineData("Ctrl+Alt+D5", "CTRL+ALT+5")]
    public void Portal_triggers_use_xkb_names(string hotkey, string expected)
    {
        Assert.True(Hotkey.TryParse(hotkey, out var parsed));
        Assert.Equal(expected, KeySyms.PortalTrigger(parsed));
    }

    [Fact]
    public void Keysyms_and_modifiers_match_X11()
    {
        Assert.Equal((nuint)0xff61, KeySyms.Of(Key.Snapshot)!.Value.Sym);
        Assert.Equal((nuint)'q', KeySyms.Of(Key.Q)!.Value.Sym);
        Assert.Equal((nuint)0xffc9, KeySyms.Of(Key.F12)!.Value.Sym);
        Assert.Equal((nuint)0xffb3, KeySyms.Of(Key.NumPad3)!.Value.Sym);
        Assert.Null(KeySyms.Of(Key.None));
        Assert.Equal(4u | 1u | 8u | 64u, KeySyms.ModifierMask(KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta));
    }

    [Fact]
    public void Every_lock_combination_is_grabbed()
    {
        var combinations = KeySyms.LockCombinations(2 | 16 | 128); // Caps, Num (Mod2), Scroll (Mod5)
        Assert.Equal(8, combinations.Count);
        Assert.Contains(0u, combinations);
        Assert.Contains(2u | 16u | 128u, combinations);
        Assert.Equal(combinations.Count, combinations.Distinct().Count());
    }

    [Fact]
    public void Frame_extents_add_decorations_and_remove_shadows()
    {
        var client = new PixelRect(100, 100, 800, 600);
        Assert.Equal(new PixelRect(96, 70, 808, 634),
            X11WindowLocator.FrameBounds(client, new X11WindowLocator.FrameExtents(4, 4, 30, 4), null));
        Assert.Equal(new PixelRect(120, 110, 760, 570),
            X11WindowLocator.FrameBounds(client, null, new X11WindowLocator.FrameExtents(20, 20, 10, 20)));
    }

    [Fact]
    public void XImage_pixels_become_opaque_bgra()
    {
        // 2×1, 32 bits little-endian BGRX with garbage in X: copied, alpha forced.
        var direct = new byte[] { 1, 2, 3, 0, 4, 5, 6, 77 };
        var target = new PixelBuffer(2, 1);
        PixelConversion.Copy(direct, new PixelConversion.ImageLayout(2, 1, 8, 32, true, 0xFF0000, 0xFF00, 0xFF), target);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, target.Pixels);

        // 16 bits RGB565, little-endian: pure red then pure blue.
        var rgb565 = new byte[] { 0x00, 0xF8, 0x1F, 0x00 };
        PixelConversion.Copy(rgb565, new PixelConversion.ImageLayout(2, 1, 4, 16, true, 0xF800, 0x07E0, 0x001F), target);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, target.Pixels);

        // 32 bits big-endian server (XRGB in memory order).
        var big = new byte[] { 0, 10, 20, 30, 0, 40, 50, 60 };
        PixelConversion.Copy(big, new PixelConversion.ImageLayout(2, 1, 8, 32, false, 0xFF0000, 0xFF00, 0xFF), target);
        Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, target.Pixels);
    }

    [Fact]
    public void Cursor_is_blended_premultiplied()
    {
        var image = new PixelBuffer(2, 2);
        Array.Fill<byte>(image.Pixels, 200);
        // Opaque red, then half-transparent white (premultiplied 128,128,128), clipped beyond the image.
        PixelConversion.DrawCursor(image, 1, 1, 2, 1, new[] { 0xFFFF0000u, 0x80808080u });
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Pixels[12..16]);
        Assert.Equal(200, image.Pixels[0]);
    }

    [Theory]
    [InlineData("96", 1.0)]
    [InlineData("144", 1.5)]
    [InlineData("192.0", 2.0)]
    [InlineData(null, 1.0)]
    [InlineData("abc", 1.0)]
    [InlineData("5000", 1.0)]
    public void Xft_dpi_gives_the_scale(string? dpi, double expected) =>
        Assert.Equal(expected, X11ScreenCapture.ParseDpiScale(dpi));

    [Fact]
    public void Click_ring_is_a_ring()
    {
        var spans = X11ClickHighlighter.RingSpans(18, 4);
        Assert.NotEmpty(spans);
        Assert.All(spans, s => Assert.True(s.X >= 0 && s.X + s.Width <= 36 && s.Y >= 0 && s.Y < 36));
        // The centre stays empty: clicks and the clicked control remain visible.
        Assert.DoesNotContain(spans, s => s.Y == 18 && s.X <= 18 && s.X + s.Width > 18);
    }

    [Fact]
    public void Portal_paths_follow_the_unique_name()
    {
        Assert.Equal("1_42", Portal.SenderPart(":1.42"));
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/tok", Portal.RequestPath(":1.42", "tok"));
        Assert.Equal("/org/freedesktop/portal/desktop/session/1_42/tok", Portal.SessionPath(":1.42", "tok"));
        var token = Portal.NewToken();
        Assert.Matches("^[A-Za-z0-9_]+$", token);
        Assert.NotEqual(token, Portal.NewToken());
    }

    [Fact]
    public void Portal_options_carry_the_handle_token()
    {
        var options = Portal.Options("abc", ("interactive", VariantValue.Bool(false)));
        Assert.Equal("abc", options["handle_token"].GetString());
        Assert.False(options["interactive"].GetBool());
        Assert.Equal("a(sa{sv})", new DShortcuts(Array.Empty<(string, Dictionary<string, VariantValue>)>()).Signature);
        Assert.Equal("oa{sv}uid", DBusCalls.SignatureOf(new DArg[] { new DPath("/a"), new DOptions(new()), new DUInt(0), new DInt(1), new DDouble(2) }));
    }

    [Fact]
    public void Shortcut_properties_include_the_trigger_when_known()
    {
        var withTrigger = PortalHotkeys.ShortcutProperties("Capturer une zone", "CTRL+Print");
        Assert.Equal("CTRL+Print", withTrigger["preferred_trigger"].GetString());
        Assert.False(PortalHotkeys.ShortcutProperties("x", null).ContainsKey("preferred_trigger"));
    }

    [Fact]
    public void ScreenCast_options_respect_the_portal_version()
    {
        var v1 = ScreenCastSession.SelectOptions(1, 3, 7, true, "token").ToDictionary(o => o.Item1, o => o.Item2);
        Assert.False(v1.ContainsKey("cursor_mode"));
        Assert.False(v1.ContainsKey("persist_mode"));
        Assert.Equal(3u, v1["types"].GetUInt32());

        var v4 = ScreenCastSession.SelectOptions(4, 1, 2, true, "token").ToDictionary(o => o.Item1, o => o.Item2);
        Assert.Equal(2u, v4["cursor_mode"].GetUInt32());
        Assert.Equal(2u, v4["persist_mode"].GetUInt32());
        Assert.Equal("token", v4["restore_token"].GetString());

        var noModes = ScreenCastSession.SelectOptions(4, 0, 1, true, null).ToDictionary(o => o.Item1, o => o.Item2);
        Assert.Equal(1u, noModes["types"].GetUInt32());
        Assert.False(noModes.ContainsKey("cursor_mode")); // embedded asked, only hidden offered
        Assert.False(noModes.ContainsKey("restore_token"));
    }

    [Fact]
    public void Portal_streams_are_parsed()
    {
        var properties = new Dict<string, VariantValue>
        {
            { "position", VariantValue.Struct(VariantValue.Int32(1920), VariantValue.Int32(0)) },
            { "size", VariantValue.Struct(VariantValue.Int32(2560), VariantValue.Int32(1440)) },
            { "source_type", VariantValue.UInt32(1) },
        };
        var streams = VariantValue.ArrayOfVariant(new[] { VariantValue.Struct(VariantValue.UInt32(57), properties.AsVariantValue()) });
        var parsed = PortalStreams.Parse(new Dictionary<string, VariantValue> { ["streams"] = streams });
        var stream = Assert.Single(parsed);
        Assert.Equal(new PortalStream(57, 1920, 0, 2560, 1440, 1), stream);

        // A screenshot at scale 2: physical (4000, 200) is logical (2000, 100), so 80 px into the stream.
        Assert.Equal((80.0, 100.0), PortalStreams.ToStream(stream, 4000, 200, 2));
        Assert.Empty(PortalStreams.Parse(new Dictionary<string, VariantValue>()));
    }

    [Fact]
    public void Wayland_monitors_are_scaled_to_the_screenshot()
    {
        var logical = new[]
        {
            new MonitorInfo(new PixelRect(0, 0, 1280, 800), 1, true, "eDP-1"),
            new MonitorInfo(new PixelRect(1280, 0, 1920, 1080), 1, false, "DP-1"),
        };
        var scaled = WaylandScreenCapture.ScaleMonitors(logical, 6400, 2160);
        Assert.Equal(new PixelRect(0, 0, 2560, 1600), scaled[0].Bounds);
        Assert.Equal(new PixelRect(2560, 0, 3840, 2160), scaled[1].Bounds);
        Assert.Equal(2, scaled[0].Scale);
    }

    [Fact]
    public void Only_temporary_portal_files_are_deleted()
    {
        var old = DateTime.UtcNow;
        Assert.True(WaylandScreenCapture.ShouldDelete("/tmp/screenshot-123.png", old, "/tmp/", null));
        Assert.True(WaylandScreenCapture.ShouldDelete("/run/user/1000/x/shot.png", old, "/tmp", "/run/user/1000"));
        Assert.False(WaylandScreenCapture.ShouldDelete("/home/me/Pictures/holiday.png", old, "/tmp", "/run/user/1000"));
        // A "Screenshot…" file that does not exist (or is older than the request) is never deleted.
        Assert.False(WaylandScreenCapture.ShouldDelete("/home/me/Pictures/Screenshot-none.png", old, "/tmp", null));
    }

    [Theory]
    [InlineData("/home/me/My Captures/a#1.png", "file:///home/me/My%20Captures/a%231.png")]
    [InlineData("/tmp/été.png", "file:///tmp/%C3%A9t%C3%A9.png")]
    [InlineData("/a/b\"c'$(x).png", "file:///a/b%22c%27%24%28x%29.png")]
    public void File_uris_round_trip(string path, string uri)
    {
        Assert.Equal(uri, FileUris.FromPath(path));
        Assert.Equal(path, FileUris.ToPath(uri));
    }

    [Fact]
    public void Non_local_uris_are_refused()
    {
        Assert.Null(FileUris.ToPath("https://example.com/a.png"));
        Assert.Null(FileUris.ToPath("file://otherhost/a.png"));
        Assert.Null(FileUris.ToPath("file:///a%00b"));
        Assert.Equal("/a.png", FileUris.ToPath("file://localhost/a.png"));
        Assert.Equal(new[] { "/a b.png", "/c.jpg" }, FileUris.ParseUriList("# comment\r\nfile:///a%20b.png\r\nhttp://x/y\r\nfile:///c.jpg\r\n"));
        Assert.Equal("file:///a.png\r\nfile:///b.png\r\n", FileUris.UriList(new[] { "/a.png", "/b.png" }));
    }

    [Fact]
    public void Executables_are_found_in_absolute_path_entries_only()
    {
        var folder = Directory.CreateTempSubdirectory("snaplume-path-");
        try
        {
            var tool = Path.Combine(folder.FullName, "mytool");
            File.WriteAllText(tool, "");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var root = folder.FullName.Replace('\\', '/');
            // On Windows the folder is "C:/..." which is not a Unix absolute path: only the Unix case can find it.
            if (!OperatingSystem.IsWindows()) Assert.Equal(tool, ExternalTools.Find("mytool", $"relative:{root}"));
            Assert.Null(ExternalTools.Find("mytool", "relative:bin"));
            Assert.Null(ExternalTools.Find("../mytool", root));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Theory]
    [InlineData("/opt/snaplume/Snaplume", "/opt/snaplume/Snaplume")]
    [InlineData("/home/me/My Apps/snaplume", "\"/home/me/My Apps/snaplume\"")]
    [InlineData("/x/100%/a$b", "\"/x/100%%/a\\\\$b\"")]
    [InlineData("/x/q\"uote", "\"/x/q\\\\\"uote\"")]
    public void Desktop_exec_arguments_are_quoted(string argument, string expected) =>
        Assert.Equal(expected, DesktopFiles.ExecArgument(argument));

    [Fact]
    public void Autostart_entry_starts_in_the_tray()
    {
        var entry = DesktopFiles.AutostartEntry("/opt/snaplume/Snaplume", "Snaplume", "Capture\nd'écran");
        Assert.Contains("Exec=/opt/snaplume/Snaplume --tray\n", entry);
        Assert.Contains("Comment=Capture d'écran\n", entry);
        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.True(DesktopFiles.IsAutostartActive(entry));
        Assert.False(DesktopFiles.IsAutostartActive(entry + "Hidden=true\n"));
        Assert.False(DesktopFiles.IsAutostartActive(entry.Replace("X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false")));
    }

    [Fact]
    public void Trash_info_follows_the_specification()
    {
        var info = DesktopFiles.TrashInfo("/home/me/a b.png", new DateTime(2026, 10, 30, 14, 5, 9));
        Assert.Equal("[Trash Info]\nPath=/home/me/a%20b.png\nDeletionDate=2026-10-30T14:05:09\n", info);
        var taken = new HashSet<string> { "a.png", "a.2.png" };
        Assert.Equal("a.3.png", DesktopFiles.UniqueTrashName("a.png", taken.Contains));
        Assert.Equal("b.png", DesktopFiles.UniqueTrashName("b.png", taken.Contains));
        Assert.Equal(".hidden.2", DesktopFiles.UniqueTrashName(".hidden", n => n == ".hidden"));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/usr", true)]
    [InlineData("/usr/share/x", true)]
    [InlineData("/etc/", true)]
    [InlineData("/proc/1", true)]
    [InlineData("/home", true)]
    [InlineData("/home/me/Pictures/Snaplume", false)]
    [InlineData("/var/home/me/Pictures", false)]
    [InlineData("/media/me/USB/Captures", false)]
    [InlineData("/mnt/nas/captures", false)]
    [InlineData("/usrdata/captures", false)]
    public void System_folders_cannot_hold_the_workspace(string folder, bool refused) =>
        Assert.Equal(refused, DesktopFiles.IsSystemFolder(folder));

    [Fact]
    public void Cups_output_is_parsed()
    {
        const string all = "Office_Laser accepting requests since Mon 01 Jan 2026 10:00:00 AM CET\nPDF accepting requests since Tue\n";
        Assert.Equal(new[] { "Office_Laser", "PDF" }, DesktopFiles.ParsePrinters(all));
        Assert.Empty(DesktopFiles.ParsePrinters("lpstat: No destinations added.\n"));
        Assert.Equal("Office_Laser", DesktopFiles.ParseDefaultPrinter("system default destination: Office_Laser\n"));
        Assert.Null(DesktopFiles.ParseDefaultPrinter("no system default destination\n"));
    }

    [Fact]
    public void Single_instance_messages_are_json_and_bounded()
    {
        var args = new[] { "--capture", "region", "/home/me/a \"b\".png" };
        Assert.Equal(args, UnixSingleInstance.Decode(UnixSingleInstance.Encode(args)));
        Assert.Null(UnixSingleInstance.Decode("not json"u8.ToArray()));
        Assert.Null(UnixSingleInstance.Decode("[1,2]"u8.ToArray()));
        Assert.Null(UnixSingleInstance.Decode("[\"a\",null]"u8.ToArray()));
        Assert.Null(UnixSingleInstance.Decode(Array.Empty<byte>()));
        Assert.Null(UnixSingleInstance.Decode(new byte[UnixSingleInstance.MaxMessageBytes + 1]));
    }

    [Fact]
    public void Notification_bodies_are_escaped() =>
        Assert.Equal("a &lt;b&gt; &amp; c", Notifications.EscapeBody("a <b> & c"));
}
