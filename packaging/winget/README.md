# Snaplume in winget

Package identifier: `MrDDream.Snaplume` (install with `winget install MrDDream.Snaplume`).

The manifests live in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), not here. This folder keeps a copy
of the ones submitted, in the same layout (`manifests/m/MrDDream/Snaplume/<version>/`), so that they can be checked and
reused. They point to the Inno Setup installer of the GitHub release (current user by default, every user with
`--scope machine`).

## First submission

1. Check the manifests (Windows 10/11, winget 1.6 or later):

   ```powershell
   winget validate --manifest packaging\winget\manifests\m\MrDDream\Snaplume\1.2.0
   winget settings --enable LocalManifestFiles   # once, as administrator
   winget install --manifest packaging\winget\manifests\m\MrDDream\Snaplume\1.2.0
   ```

2. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) on GitHub, copy the version folder to
   `manifests/m/MrDDream/Snaplume/1.2.0/` in the fork, commit, and open a pull request to microsoft/winget-pkgs
   (title: `New package: MrDDream.Snaplume version 1.2.0`). Fill in the checklist of the pull request template.

   Or let [wingetcreate](https://github.com/microsoft/winget-create) (Microsoft) do the fork and the pull request:
   `wingetcreate submit --token <GitHub token> packaging\winget\manifests\m\MrDDream\Snaplume\1.2.0`.

3. Automated checks run on the pull request (manifest, download, hash, installation in a sandbox, malware scan).
   A Microsoft moderator then merges it, usually within a few days. Answer the bot's comments if a check fails.

## New versions

Once the package exists, each release only needs a new version folder. The simplest is wingetcreate, which downloads
the installer, computes the hash and opens the pull request:

```powershell
wingetcreate update MrDDream.Snaplume --version 1.2.1 `
  --urls https://github.com/MrDDream/Snaplume/releases/download/v1.2.1/Snaplume-Setup-1.2.1.exe `
  --submit --token <GitHub token>
```

The token is a GitHub personal access token with the `public_repo` scope, from the account that owns the fork.
This step can later be added to `.github/workflows/release.yml`, with the token in a repository secret.

## Notes

- The installer URL and its SHA-256 must never change for a published version: do not replace the assets of a release
  that winget points to (publish a new version instead).
- The installers are not code-signed: winget accepts them, but Windows SmartScreen may warn on first run.
