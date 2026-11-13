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

3. The **Release** workflow (`.github/workflows/release.yml`) runs the tests, builds the installer with that version, signs `latest.json` and creates the GitHub release with both files and generated notes. You can also run it by hand from the Actions tab with a version; untick **publish** for a trial run, which builds and signs everything and keeps the installer and `latest.json` on the run for a week instead of creating a release.

Each release also carries `OpenBook-<version>.cdx.json`, the package list (a CycloneDX 1.5 software bill of materials) that `tools/make-sbom.ps1` writes from the published files: every NuGet package in the installer with its version and licence, the .NET runtime it was published with, and the fonts for printed forms. Organisations that review software before installing it often ask for one. To make one by hand after packaging: `powershell -File tools/make-sbom.ps1 -Version 1.2.0`.

Versions are three numbers, `major.minor.patch`. Installed copies offer a release only when its version is higher than theirs.

To check a manifest by hand:

```bash
dotnet run --project src/OpenBook.ReleaseTool -- verify --manifest latest.json --installer OpenBook-Setup-1.2.0.exe
```

## What installed copies do

- **OpenBook Desktop**, installed from the setup, checks a minute after starting and then once a day. It downloads a newer release into `%LOCALAPPDATA%\OpenBook\updates` and checks it. It then offers **Install** in the tray menu and on **Settings → System → Updates**. Installing backs up the books first (as Quit does), runs the installer silently, and starts OpenBook again with the same folders.
- **OpenBook Server** doesn't check unless `OpenBook:Updates:Check` is `true`, and then only shows what's available; update it by getting the new source, then `docker compose build --pull` and `docker compose up -d`.
- A copy run from source never checks unless told to.

Settings:

- `OpenBook:Updates:Check` (`true` / `false`) turns checking on or off. On the desktop, **Settings → System → Updates** remembers each person's choice in `%LOCALAPPDATA%\OpenBook\updates.json`.
- `OpenBook:Updates:Feed` points at another manifest, for trying a release before publishing it. It must use HTTPS, except a plain `http://` address on the same computer.

A manifest can set a minimum version (`--minimum` in the release tool): older copies then say to download the new installer by hand instead of updating directly.

## Yearly tax upkeep

A few figures in OpenBook follow US tax rules that change by year. Go through this list each December or January, once the IRS has published the new year's figures and forms, and ship the result in the first release of the year. Each item says where the figure lives and which test holds it. When a figure for a year isn't published yet, leave it: OpenBook uses the latest one it knows, and people can enter the published figure themselves where the item says so.

1. **Standard mileage rate.** `MileageRate.Standard` in `src/OpenBook.Domain/Projects/Projects.cs` lists the IRS business rate per year (72.5 cents for 2026). Add the new year when the IRS announces it (usually in December). Years after the last one use the last rate, and an accountant can set a year's rate on the **Mileage** page (**Set rate**), which wins over the built-in one. Test: `SmallFixTests` (`MileageRate.For`), which assumes the year after the last listed one falls back to it.
2. **1099 reporting threshold.** `PayablesService.DefaultThreshold` in `src/OpenBook.Application/Purchases/PayablesService.cs`: $600 through 2025, $2,000 for 2026, indexed for inflation from 2027. Add each year's published figure as it comes out. Until then, the filing data on **Accountant tools** takes a threshold typed in. Test: `PayablesFlowTests`.
3. **1099 forms, boxes and the IRIS upload.** `Form1099Box` (`src/OpenBook.Domain/Purchases/Vendor.cs`) names the 1099-NEC and 1099-MISC boxes vendors can be reported in. `AccountantTools.Filing1099CsvAsync` writes the filing data for the IRS's IRIS upload template. Compare the boxes with the new forms and the columns with the current IRIS CSV template. Vendors store the box by its enum name, so a renumbered box changes its description, never its name. The vendors' copies (`Filing1099Service`) print the same boxes.
4. **Tax form lines.** `TaxLines` in `src/OpenBook.Domain/Accounts/TaxLines.cs` maps profit and loss accounts to the lines of Schedule C, Form 1065, Form 1120-S, Form 1120 and Form 990. Its comment names the forms' year (2025). When the new forms are final, compare each line's number and name. Change the label and the year in the comment, **never a key**: accounts and the starter charts store the keys. A line the new form drops stays in the list, since accounts may be mapped to it; say in its name what replaced it. Check the 50% meals line (`HalfDeductible`) still applies. Test: `ReportsAndBudgetsTests` (keys unique). Chapter 3 and chapter 7 of the user guide describe the tax summary.
5. **MACRS tables.** `Depreciation` in `src/OpenBook.Domain/Assets/FixedAssets.cs` holds the percentages of IRS Publication 946, Tables A-1 (half-year), A-6 (27.5-year residential) and A-7a (39-year nonresidential). They rarely change. Compare them with the new Publication 946 anyway. Bonus depreciation and section 179 limits aren't built in: people enter a first-year amount themselves (chapter 19), so a new limit needs no code.
6. **Donor letter wording.** `GiftService` (`src/OpenBook.Application/Nonprofit/GiftService.cs`) writes the IRS statements in gift acknowledgments and giving statements (goods or services received in return, or none). Check them against the current IRS Publication 1771.
7. **The user guide.** Search `docs/user-guide` for the old year and for each figure changed above, and update what it says in the same commit.

Not in the list, because people enter them for their own company: sales tax rates and agencies, nexus thresholds and bank holidays. Payroll isn't part of OpenBook.

Note the date the list was last done here: **2026-11-12** (2026 figures in place; 2027 mileage rate and threshold not yet published).
