<!-- Release notes template used by .github/workflows/release.yml: {{VERSION}} is replaced by the version.
     Update the "Highlights" section before tagging a new version. -->

**Free, open-source screenshots, annotations and screen recording for Windows. 100% offline.**

## Highlights of Snaplume 1.0

- **Capture**: smart region selection (windows and controls detected under the mouse), magnifier with pixel coordinates and color picker, full screen / all screens / active window / repeat last region, timer, **scrolling capture**, **text capture (offline Windows OCR)** and **QR code / barcode reading**. Multi-monitor and Per-Monitor v2 high-DPI aware.
- **Editor**: arrows, lines, shapes, freehand pen, text with halo, **step numbers**, 54 vector stamps, highlighter, pixelate, spotlight and magnifier, crop, resize and effects (shadow, border, rounded corners, torn edge). Tool presets, unlimited undo/redo, and annotations that **stay editable** after saving.
- **Video**: MP4 recording of a region, a window or the full screen with system audio and/or microphone, click highlighting and webcam overlay; built-in player with **trim**, **GIF** and frame export.
- **Organize and share**: "My screenshots" quick panel, workspaces (plain sub-folders) with drag and drop, and a **document generator** (PDF, DOCX, HTML).
- **6 languages** (English, French, German, Spanish, Italian, Arabic with a right-to-left interface), light / dark / automatic theme, global hotkeys, tray icon, start with Windows and a scriptable command line.
- **Private by design**: no account, no cloud, no telemetry, no ads. MIT licensed.

## Download

| Package | File | Best for |
|---|---|---|
| **Installer** | [`Snaplume-Setup-{{VERSION}}.exe`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/Snaplume-Setup-{{VERSION}}.exe) | Most users: per-user setup (no administrator rights needed, all-users install available), Start menu shortcut, optional desktop shortcut and start with Windows, uninstaller. |
| **Portable** | [`Snaplume-{{VERSION}}-portable.zip`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/Snaplume-{{VERSION}}-portable.zip) | USB sticks and locked-down PCs: unzip the `Snaplume` folder anywhere and run `Snaplume.exe`; settings stay in the folder. |
| **MSI** | [`Snaplume-{{VERSION}}-x64.msi`](https://github.com/MrDDream/Snaplume/releases/download/v{{VERSION}}/Snaplume-{{VERSION}}-x64.msi) | IT departments: all-users installation in `Program Files` for Intune, Group Policy, SCCM or `msiexec /i Snaplume-{{VERSION}}-x64.msi /qn` (add `DESKTOPSHORTCUT=1` for a desktop shortcut). Administrator rights required. |

All packages are self-contained: **no .NET installation needed**.
**Requirements**: Windows 10 version 2004 (May 2020 Update) or later, or Windows 11, 64-bit (x64).

> [!NOTE]
> The executables are **not code-signed** yet, so Windows SmartScreen may show *"Windows protected your PC"* the first time. Click **More info › Run anyway**.

**Checksums**: `SHA256SUMS.txt` lists the SHA-256 of each file. Check a download in PowerShell with
`Get-FileHash .\Snaplume-Setup-{{VERSION}}.exe -Algorithm SHA256` and compare it with the file, or run `sha256sum -c SHA256SUMS.txt` in Git Bash / WSL.

## En français

Snaplume {{VERSION}} : capture d'écran, annotation et enregistrement vidéo libres (MIT) pour Windows, 100 % hors ligne, sans compte ni télémétrie.

- **Programme d'installation** (`Snaplume-Setup-{{VERSION}}.exe`) : installation pour l'utilisateur courant sans droits administrateur (ou pour tous les utilisateurs), raccourcis, démarrage avec Windows en option, désinstalleur.
- **Version portable** (`Snaplume-{{VERSION}}-portable.zip`) : décompressez le dossier `Snaplume` où vous voulez et lancez `Snaplume.exe` ; les paramètres restent dans le dossier.
- **Paquet MSI** (`Snaplume-{{VERSION}}-x64.msi`) : pour les services informatiques, installation pour tous les utilisateurs dans `Program Files` via Intune, une stratégie de groupe, SCCM ou `msiexec /i Snaplume-{{VERSION}}-x64.msi /qn` (ajoutez `DESKTOPSHORTCUT=1` pour un raccourci sur le bureau). Droits administrateur requis.
- Aucune installation de .NET n'est nécessaire. Windows 10 version 2004 ou ultérieure, ou Windows 11, 64 bits.
- Les exécutables ne sont pas encore signés : si SmartScreen affiche « Windows a protégé votre ordinateur », cliquez sur **Informations complémentaires › Exécuter quand même**. Vérifiez les fichiers avec `SHA256SUMS.txt`.
