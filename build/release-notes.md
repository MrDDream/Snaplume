<!-- Release notes template used by .github/workflows/release.yml: {{VERSION}} is replaced by the version.
     Update the "Highlights" section before tagging a new version. -->

**Free, open-source screenshots, annotations and screen recording for Windows and Linux. 100% offline.**

## What's new in 1.2

- **Linux version**: `.deb` and `.rpm` packages for x64 and ARM64, under X11 and Wayland (through the desktop portals). The whole interface was rebuilt with Avalonia: the same Snaplume on Windows and Linux.
- **Video options before recording**, like in Screenpresso: record button, microphone and webcam choice, system sound, **zoom on clicks** (the video zooms in where you click), size of the area (1280 × 720, 1920 × 1080…) with a frame you can move.
- **Export and compress videos**: MP4 H.264, and with the optional ffmpeg module (downloaded and checked from the settings) H.265, AV1, WebM and animated WebP. A **maximum file size** (Discord 10 MB, e-mail 20 MB, your own size) lowers the resolution and frame rate rather than blurring the picture.
- **Borders**: one-click styles in the editor (black outline, white frame, shadow, rounded corners, torn edges), with a preview of your capture. Rounded corners, torn edges and shadows are now **transparent** in PNG.
- **WebP** images: save captures as WebP, open and edit WebP images.
- **Download and install updates** from Snaplume itself, the file being checked (SHA-256) before installing.
- **Win+Shift+S for Snaplume** (option in Settings › Shortcuts) instead of the Snipping Tool.
- Quick panel docked against the taskbar, workspaces in a side panel, clearer pixelate icon, and a lighter Windows build.

## Highlights of Snaplume 1.x

- **Capture**: smart region selection (windows and controls detected under the mouse), magnifier with pixel coordinates and color picker, full screen / all screens / active window / repeat last region, timer, **scrolling capture**, **text capture (offline OCR)** and **QR code / barcode reading**. Multi-monitor and high-DPI aware.
- **Editor**: arrows, lines, shapes, freehand pen, text with halo, **step numbers**, 54 vector stamps, highlighter, pixelate, spotlight and magnifier, crop, resize, borders and effects, insert images. Tool presets, unlimited undo/redo, and annotations that **stay editable** after saving.
- **Video**: MP4 recording of a region, a window or the full screen with system audio and/or microphone, click highlighting, zoom on clicks and webcam overlay; built-in player with **trim**, **GIF** and frame export, web formats and compression.
- **Organize and share**: "My screenshots" quick panel, workspaces (plain sub-folders) with drag and drop and a **document generator** (PDF, DOCX, HTML).
- **6 languages** (English, French, German, Spanish, Italian, Arabic with a right-to-left interface), light / dark / automatic theme, global hotkeys, tray icon, start with the session and a scriptable command line.
- **Private by design**: no account, no cloud, no telemetry, no ads. MIT licensed.

## Download

### Windows

| Package | File | Best for |
|---|---|---|
| **Installer** | [`Snaplume-Setup-{{VERSION}}.exe`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/Snaplume-Setup-{{VERSION}}.exe) | Most users: per-user setup (no administrator rights needed, all-users install available), Start menu shortcut, optional desktop shortcut and start with Windows, uninstaller. |
| **Portable** | [`Snaplume-{{VERSION}}-portable.zip`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/Snaplume-{{VERSION}}-portable.zip) | USB sticks and locked-down PCs: unzip the `Snaplume` folder anywhere and run `Snaplume.exe`; settings stay in the folder. |
| **MSI** | [`Snaplume-{{VERSION}}-x64.msi`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/Snaplume-{{VERSION}}-x64.msi) | IT departments: all-users installation in `Program Files` for Intune, Group Policy, SCCM or `msiexec /i Snaplume-{{VERSION}}-x64.msi /qn` (add `DESKTOPSHORTCUT=1` for a desktop shortcut). Administrator rights required. |

**Requirements**: Windows 10 version 2004 (May 2020 Update) or later, or Windows 11, 64-bit (x64). Also available with `winget install MrDDream.Snaplume` once the package is published there.

> [!NOTE]
> The executables are **not code-signed** yet, so Windows SmartScreen may show *"Windows protected your PC"* the first time. Click **More info › Run anyway**.

### Linux

| Package | x64 | ARM64 |
|---|---|---|
| **Debian, Ubuntu, Mint…** | [`snaplume_{{VERSION}}-1_amd64.deb`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/snaplume_{{VERSION}}-1_amd64.deb) | [`snaplume_{{VERSION}}-1_arm64.deb`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/snaplume_{{VERSION}}-1_arm64.deb) |
| **Fedora, openSUSE…** | [`snaplume-{{VERSION}}-1.x86_64.rpm`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/snaplume-{{VERSION}}-1.x86_64.rpm) | [`snaplume-{{VERSION}}-1.aarch64.rpm`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/snaplume-{{VERSION}}-1.aarch64.rpm) |

Install with `sudo apt install ./snaplume_{{VERSION}}-1_amd64.deb` or `sudo dnf install ./snaplume-{{VERSION}}-1.x86_64.rpm`. Under Wayland, windows are not detected under the mouse (select by dragging), the desktop asks for permission the first time, and global shortcuts need KDE or GNOME 48+. On GNOME, the tray icon needs the AppIndicator extension.

All packages are self-contained: **no .NET installation needed**.

**Checksums**: `SHA256SUMS.txt` (Windows) and `SHA256SUMS-linux.txt` list the SHA-256 of each file. Check a download with `Get-FileHash .\Snaplume-Setup-{{VERSION}}.exe -Algorithm SHA256` in PowerShell, or `sha256sum -c SHA256SUMS-linux.txt --ignore-missing` on Linux.

## En français

Snaplume {{VERSION}} : capture d'écran, annotation et enregistrement vidéo libres (MIT) pour Windows et Linux, 100 % hors ligne, sans compte ni télémétrie.

**Nouveautés de la 1.2** :

- **Version Linux** (`.deb` et `.rpm`, x64 et ARM64, X11 et Wayland), avec une interface refaite en Avalonia, identique sous Windows et Linux.
- **Options vidéo avant l'enregistrement** : micro, webcam, son du système, **zoom sur les clics**, taille de la zone (1280 × 720…) avec un cadre déplaçable.
- **Export et compression des vidéos** : MP4 H.264, et avec le module ffmpeg optionnel H.265, AV1, WebM et WebP animé. Une **taille maximale** (Discord 10 Mo, e-mail 20 Mo, ou votre taille) baisse la définition plutôt que de flouter l'image.
- **Bordures** en un clic dans l'éditeur, et coins arrondis, bords déchirés et ombres **transparents** en PNG.
- Images **WebP**, **mises à jour téléchargées et installées** depuis Snaplume (fichier vérifié), option **Win+Maj+S pour Snaplume**, panneau collé à la barre des tâches, espaces de travail dans un panneau latéral.

- **Windows** : programme d'installation (`Snaplume-Setup-{{VERSION}}.exe`, sans droits administrateur), version portable (`Snaplume-{{VERSION}}-portable.zip`) ou paquet MSI pour les services informatiques. Windows 10 version 2004 ou ultérieure, ou Windows 11, 64 bits. Les exécutables ne sont pas encore signés : si SmartScreen affiche « Windows a protégé votre ordinateur », cliquez sur **Informations complémentaires › Exécuter quand même**.
- **Linux** : `sudo apt install ./snaplume_{{VERSION}}-1_amd64.deb` (Debian, Ubuntu) ou `sudo dnf install ./snaplume-{{VERSION}}-1.x86_64.rpm` (Fedora). Sous Wayland, pas de détection de fenêtre sous la souris et une autorisation est demandée la première fois.
- Aucune installation de .NET n'est nécessaire. Vérifiez les fichiers avec `SHA256SUMS.txt` et `SHA256SUMS-linux.txt`.
