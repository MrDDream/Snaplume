# Contributing to Snaplume

Thanks for helping! Bug reports, ideas, translations and pull requests are all welcome.
(Les contributions en français sont les bienvenues aussi.)

## Reporting a bug

Open an [issue](https://github.com/MrDDream/Snaplume/issues) with:

- your Windows version (Settings › System › About) and Snaplume version (Settings › About);
- what you did, what you expected, and what happened;
- the error log if there is one: `%APPDATA%\Snaplume\errors.log` (or next to `Snaplume.exe` in portable mode).

Please remove anything private from screenshots and logs before posting them.

## Building

You need Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Snaplume.slnx
dotnet run --project src/Snaplume
```

`pwsh build/build-release.ps1` builds the portable zip and, when [Inno Setup 6](https://jrsoftware.org/isinfo.php) and [WiX Toolset 5](https://wixtoolset.org/) are installed, the installer and the MSI package.

## Pull requests

- Keep each pull request focused on one change, and describe how you tested it.
- Follow the existing code style (C# with nullable reference types, WPF with `DynamicResource` theme brushes).
- Every user-visible string goes through the translation files: add the key to **all six** files in `src/Snaplume/Languages/` (an English value is fine if you cannot translate it).
- Check the change in both the light and the dark theme, and in Arabic (right-to-left) when it touches layout.
- Snaplume is 100% offline: features that need an online service, an account or telemetry will not be accepted.

## Translations

Each language is one JSON file in `src/Snaplume/Languages/`. To add a language:

1. Copy `en.json`, name it with the two-letter language code (for example `pt.json`), translate the values (not the keys) and keep the `{0}` placeholders.
2. Register it in `src/Snaplume/Core/Loc.cs`, in the `Languages` list: code, name of the language in that language, and the culture used for dates and numbers (for example `("pt", "Português", "pt-PT")`; pick a culture with the Gregorian calendar). A right-to-left language must also be added to `IsRightToLeft` in the same file.
3. Add the code to `SatelliteResourceLanguages` in `build/build-release.ps1`, so that the release keeps the .NET resources of that language.
4. Open a pull request.

## Website

The website lives in `docs/` (plain HTML and CSS, no build step) and is deployed to GitHub Pages by `.github/workflows/pages.yml`. The mockups in `docs/assets/` are SVG files.

## License

By contributing, you agree that your contributions are licensed under the [MIT license](LICENSE).
