# Snaplume Linux packages

`.deb` (Debian, Ubuntu, Mint…) and `.rpm` (Fedora, openSUSE…) packages, for x64 and arm64, built by
`build/build-linux.ps1` with [nfpm](https://nfpm.goreleaser.com) from `nfpm.yaml`.

## Building

Requirements: the .NET 10 SDK and PowerShell (`pwsh` 7 on any system, or Windows PowerShell 5.1), plus the
nfpm executable for the packages themselves. The script does not download nfpm: take the archive for your
system from <https://github.com/goreleaser/nfpm/releases>, check it against the release's `checksums.txt`,
and put `nfpm` (`nfpm.exe` on Windows) in `artifacts/tools/` or in PATH (or pass `-NfpmPath`).

```sh
pwsh build/build-linux.ps1                      # x64 and arm64, .deb and .rpm
pwsh build/build-linux.ps1 -Architectures x64   # one architecture
pwsh build/build-linux.ps1 -SkipPackages        # publish and stage only (no nfpm needed)
```

The version comes from `Directory.Build.props` (`-Version` overrides it); the script warns when the first
`<release>` of the AppStream file does not match it. Output in `artifacts/linux/`:

```
snaplume_1.2.0-1_amd64.deb    snaplume-1.2.0-1.x86_64.rpm
snaplume_1.2.0-1_arm64.deb    snaplume-1.2.0-1.aarch64.rpm
SHA256SUMS
```

To inspect a package without installing it: `dpkg-deb -c`/`dpkg-deb -I` for the .deb,
`rpm -qlp`/`rpm -qip --requires` for the .rpm.

## What is installed

| Path | Content |
|---|---|
| `/opt/snaplume/Snaplume` | the application: self-contained .NET single-file executable (no .NET install needed) |
| `/opt/snaplume/libSkiaSharp.so`, `libHarfBuzzSharp.so` | native drawing libraries, which a single file cannot carry |
| `/usr/bin/snaplume` | link to the executable |
| `/usr/share/applications/io.github.mrddream.Snaplume.desktop` | menu entry, file associations (PNG, JPEG, BMP, GIF, MP4), actions: capture a region, the screen, a scrolling page, settings |
| `/usr/share/metainfo/io.github.mrddream.Snaplume.metainfo.xml` | AppStream description for the software centres |
| `/usr/share/icons/hicolor/*/apps/io.github.mrddream.Snaplume.{png,svg}` | icons, 16 to 512 px and scalable |

The post-install and post-removal scripts refresh the icon cache and the desktop database. Removing the
package leaves the user's settings (`~/.config/Snaplume`) and captures alone.

## Dependencies

Everything Snaplume loads in process or runs is declared, so that every feature works after installation.

| Purpose | Debian / Ubuntu | Fedora |
|---|---|---|
| X11 capture, windows, pointer, shortcuts, click highlight (P/Invoke) | `libx11-6`, `libxtst6`, `libxrandr2`, `libxi6`, `libxext6`, `libxfixes3` | `libX11`, `libXtst`, `libXrandr`, `libXi`, `libXext`, `libXfixes` |
| .NET runtime needs (globalization, HTTPS for the update check) | `libicu78 \| libicu77 \| libicu76 \| libicu74 \| libicu72 \| libicu70`, `libssl3t64 \| libssl3` | `libicu`, `openssl-libs` |
| Fonts for Skia and the documents | `libfontconfig1`, `fonts-noto-core \| fonts-dejavu-core` | `fontconfig`, `google-noto-sans-fonts` |
| Video recording and playback (GStreamer, in process) | `libgstreamer1.0-0`, `libgstreamer-plugins-base1.0-0`, `gstreamer1.0-plugins-base`, `gstreamer1.0-plugins-good`, `gstreamer1.0-pulseaudio`, `gstreamer1.0-plugins-ugly \| gstreamer1.0-plugins-bad` (H.264: x264enc or openh264enc) | `gstreamer1`, `gstreamer1-plugins-base`, `gstreamer1-plugins-good`, `(gstreamer1-plugin-openh264 or gstreamer1-plugins-ugly)` |
| Screen recording under Wayland (pipewiresrc) | `gstreamer1.0-pipewire` | `pipewire-gstreamer` |
| Clips, GIF frames, thumbnails | `ffmpeg` | `(ffmpeg-free or ffmpeg)` |
| Text recognition (OCR) | `tesseract-ocr` (English included) | `tesseract` |
| Trash (`gio trash`), D-Bus | `libglib2.0-bin`, `libglib2.0-0t64 \| libglib2.0-0` | `glib2` |
| Open files, e-mail (`xdg-open`, `xdg-email`) | `xdg-utils` | `xdg-utils` |
| Wayland: screenshots, wheel, shortcuts, screencast | `xdg-desktop-portal` | `xdg-desktop-portal` |

Recommended: `gstreamer1.0-libav` / `gstreamer1-plugin-libav` (AAC sound; Opus is used otherwise),
`wl-clipboard` (clipboard under Wayland), `xclip`, `cups-client` (printing; without it the PDF opens in the
viewer), `pulseaudio-utils` (`pactl`: the list of microphones), `pkexec` (Debian 12+, Ubuntu 22.04+; `policykit-1`
before) or `polkit` (installing updates), `xdg-user-dirs` (`xdg-user-dir`: the downloads folder; `~/Downloads`
otherwise), a portal backend (`xdg-desktop-portal-gnome`, `-kde` or `-gtk`). Suggested: the GNOME AppIndicator
extension (the notification-area icon needs it on GNOME) and more Tesseract languages
(`tesseract-ocr-fra`, `tesseract-langpack-fra`…).

## Icons

The PNG icons in `icons/hicolor/` and the tray icon `src/Snaplume.Platform.Linux/Assets/tray.png` are
rendered from `docs/branding/snaplume.svg` by a small SkiaSharp program, and committed:

```sh
dotnet run --project packaging/linux/icongen
```

It understands only the SVG subset the logo uses (rect, path, group styles, translate/scale) and stops on
anything else rather than drawing it wrong. The SVG itself is installed as the scalable icon.

## Behaviour per session

Snaplume detects the session from `XDG_SESSION_TYPE` (then `WAYLAND_DISPLAY`); `SNAPLUME_SESSION=x11` or
`=wayland` forces one, for testing.

* **X11**: everything, as on Windows, except keeping Snaplume's own windows out of captures (they are hidden
  instead).
* **Wayland** (through xdg-desktop-portal; the desktop asks once, and Snaplume keeps the restore tokens in
  its settings folder): screenshots through the Screenshot portal, the scrolling capture through
  RemoteDesktop (wheel) and a screenshot per step (slow), global shortcuts through GlobalShortcuts (KDE
  Plasma, GNOME 48+; elsewhere bind a system shortcut to `snaplume --capture region`), recording through
  ScreenCast (the desktop chooses the screen or window). No window detection, no click highlight, no
  capture of the active window.
