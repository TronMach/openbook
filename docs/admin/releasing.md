# Releasing OpenBook and its updates

Installed copies of OpenBook Desktop update themselves from the latest release on GitHub. This page is for whoever publishes releases.

## How updates are kept safe

Each release has two files: the installer, `OpenBook-Setup-<version>.exe`, and `latest.json`. The manifest names the version, the installer's address, its size and its SHA-256, and carries a signature made with the **release key**. Copies of OpenBook have the public half of that key built in (`src/OpenBook.Application/Updates/UpdateKeys.cs`). They ignore a manifest the key didn't sign, and throw away a download whose size or hash doesn't match. So a fake release, or a changed file on the download host, is never installed.

The installer itself isn't code-signed yet: Windows SmartScreen warns on a first install downloaded from the web. Updates OpenBook downloads itself don't show that warning.

## The release key

- The private key is a PEM file kept **outside the repository**, at `%USERPROFILE%\.openbook-release\update-signing-key.pem` on the computer where it was made. Back it up somewhere safe. If it's lost, copies already installed can't be told to trust a new key, so they'd have to be updated by hand once.
- The release workflow reads it from the repository secret `OPENBOOK_UPDATE_KEY`. Set it once with the GitHub CLI:

```bash
gh secret set OPENBOOK_UPDATE_KEY --repo TronMach/openbook < "$USERPROFILE/.openbook-release/update-signing-key.pem"
```

- `src/OpenBook.ReleaseTool` made the key (`keygen`); it refuses to overwrite an existing key file. Changing `UpdateKeys.PublicKey` means older copies stop trusting new releases, so don't, unless the private key has leaked.

## Publishing a release

1. Make sure the tests pass on SQLite and PostgreSQL (see CLAUDE.md), and that `master` is pushed.
2. Tag the version and push the tag:

```bash
git tag v1.2.0
```

```bash
git push origin v1.2.0
```

3. The **Release** workflow (`.github/workflows/release.yml`) runs the tests, builds the installer with that version, signs `latest.json` and creates the GitHub release with both files and generated notes. You can also run it by hand from the Actions tab with a version.

Versions are three numbers, `major.minor.patch`. Installed copies offer a release only when its version is higher than theirs.

To check a manifest by hand:

```bash
dotnet run --project src/OpenBook.ReleaseTool -- verify --manifest latest.json --installer OpenBook-Setup-1.2.0.exe
```

## What installed copies do

- **OpenBook Desktop**, installed from the setup, checks a minute after starting and then once a day. It downloads a newer release into `%LOCALAPPDATA%\OpenBook\updates` and checks it. It then offers **Install** in the tray menu and on **Settings → System → Updates**. Installing backs up the books first (as Quit does), runs the installer silently, and starts OpenBook again with the same folders.
- **OpenBook Server** doesn't check unless `OpenBook:Updates:Check` is `true`, and then only shows what's available; update it with `docker compose pull` and `docker compose up -d`.
- A copy run from source never checks unless told to.

Settings:

- `OpenBook:Updates:Check` (`true` / `false`) turns checking on or off. On the desktop, **Settings → System → Updates** remembers each person's choice in `%LOCALAPPDATA%\OpenBook\updates.json`.
- `OpenBook:Updates:Feed` points at another manifest, for trying a release before publishing it. It must use HTTPS, except a plain `http://` address on the same computer.

A manifest can set a minimum version (`--minimum` in the release tool): older copies then say to download the new installer by hand instead of updating directly.
