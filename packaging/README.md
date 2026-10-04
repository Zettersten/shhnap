# Distribution runbook

Shnapp is a free Windows 11 app. Stable tags use the form **vX.Y.Z**. The tag supplies the portable and Velopack app version **X.Y.Z** and the Store package version **X.Y.Z.0**. A tag stages a draft GitHub Release; publication is a separate, reviewed action.

## Channel status

Use the [download page](https://shhnap.com/download/), [GitHub releases](https://github.com/Zettersten/shhnap/releases), [Scoop bucket](https://github.com/Zettersten/scoop-bucket), [WinGet submissions](https://github.com/microsoft/winget-pkgs/pulls?q=is%3Apr+Zettersten.Shnapp), and [Chocolatey package page](https://community.chocolatey.org/packages/shnapp) for current availability. A catalog submission is available to users only after that catalog accepts it. Microsoft Store submission remains disabled until Partner Center account requirements and the first listing are complete.

The website recommends the x64 or ARM64 Velopack installer when the release metadata includes verified installers. It keeps the portable ZIP available beside each installer. Published v1 metadata and an unavailable feed leave the current ZIP links and extraction instructions in place. Scoop, WinGet, and Chocolatey continue to consume the ZIP assets, not the Velopack installer.

## Direct installer and portable build

For each architecture, the release workflow publishes two separate distributions:

| Distribution | Release assets and use |
| --- | --- |
| Velopack direct install | `ErikZettersten.Shnapp.{arch}-win-{arch}-Setup.exe` and its `.sha256` file are the website's preferred downloads when present. The same release also carries `ErikZettersten.Shnapp.{arch}-{version}-win-{arch}-full.nupkg`, `releases.win-{arch}.json`, `assets.win-{arch}.json`, and `RELEASES-win-{arch}` for Velopack's update and deployment protocol. The release build currently publishes full packages, without delta packages. |
| Portable ZIP | `Shnapp-win-{arch}.zip` and its `.sha256` file remain available for existing direct ZIP users and the Scoop, WinGet, and Chocolatey channels. |

`{arch}` is `x64` or `arm64`. The [Velopack Windows installer](https://docs.velopack.io/packaging/operating-systems/windows) is per-user and installs under `%LOCALAPPDATA%\ErikZettersten.Shnapp.{arch}` by default. The app keeps its library and preferences separately under `%LOCALAPPDATA%\Shnapp`. The installer launches Shnapp after setup; it does not convert or remove an existing portable ZIP copy.

### Portable ZIP layout

The ZIP contains a self-contained, single-file **app** executable for x64 or ARM64 in a versioned folder. The archive deliberately contains more than one file:

| ZIP location | Purpose |
| --- | --- |
| Root Shnapp.exe | Stable launcher used by existing v1.0.5 installs and the restart updater. |
| Root current-version.txt | Selects the active version. |
| Root LICENSE and THIRD_PARTY_NOTICES.txt | Required distribution notices. |
| versions/vX.Y.Z/Shnapp.exe | Single-file app payload. |
| versions/vX.Y.Z/Shnapp.pri | Compatibility with the v1.0.5 updater; the app also bundles its WinUI resources. |
| versions/vX.Y.Z/LICENSE and THIRD_PARTY_NOTICES files | Notices for the exact bundled dependencies. |

Keep the ZIP layout intact. The launcher and version pointer maintain the v1.0.5 updater protocol; replacing them with one bare EXE would break in-place updates. The bundled .NET and Windows App SDK native components can extract into the user's Temp directory on first launch. “Single-file” describes the app payload in the ZIP, not every file used while it runs.

### Verification

CI tests and builds x64 on pull requests and main. A stable tag builds both architectures, verifies the ZIP layouts, Velopack packages and feeds, and SHA-256 sidecars, uploads 16 release assets, re-downloads them, checks their bytes, and creates catalog candidates. Smoke-test the x64 installer, its launch, update through Settings, and uninstall on Windows 11. Also test x64 ZIP capture, annotation, save, share, startup, and direct update. ARM64 is built and checked structurally in CI; record the lack of a device test until one is available. Review license notices and unsigned-download/SmartScreen behavior for the exact build.

## Cut and publish a release

1. Merge the reviewed change to main. Release notes are generated from merged pull requests; the tag run requires at least one change.
2. Create and push a stable vX.Y.Z tag on a commit reachable from main. **Windows build and release** runs tests, packages both ZIPs and both Velopack installers and update feeds, generates version-specific notes, and stages a **draft** GitHub Release. It will not overwrite an existing release.
3. Inspect the Actions run, the draft's **New in vX.Y.Z** notes, all 16 assets (eight per architecture), and the **Catalog-candidates-vX.Y.Z** artifact. Check the installer and ZIP checksum sidecars and confirm each `releases.win-{arch}.json` points to its matching full `.nupkg`. Download and smoke-test the x64 installer and ZIP. Edit the draft notes if needed before publication.
4. Run **Actions → Windows build and release → Run workflow → publish-github-release** on main with the exact tag. The job verifies the draft, assets, hashes, and reviewed notes again, then makes it public.
5. The same operation writes schema-version 2 **latest.json** to the separate [**release-metadata** branch](https://github.com/Zettersten/shhnap/tree/release-metadata). Its `installers` entries carry the Setup URLs and hashes; `downloads` retains the ZIP URLs and hashes. The [download page](https://shhnap.com/download/) fetches the version, release notes, and verified URLs at runtime from [the raw feed](https://raw.githubusercontent.com/Zettersten/shhnap/release-metadata/latest.json). It prefers installers for a valid v2 feed and retains ZIP links for v1 or unavailable metadata. This branch lets a release update the website without rebuilding it. A normal PR merge does not change the feed. The publisher verifies public stable release assets and refuses to roll the feed back to an older version.
6. Verify the feed and website show the new version, that both Setup downloads and their checksums resolve, and that both portable ZIP links and checksums still resolve. Inspect the downstream Scoop, WinGet, and Chocolatey job summaries and then verify each public catalog separately. The site can display the GitHub downloads immediately; show a package-manager version as available only after its catalog accepts it.

If a release became public but feed publication failed, run **refresh-release-metadata** with that public tag. It verifies the release again and republishes latest.json without creating a release or rerunning catalog submissions. Fix the cause if it fails. Never replace public asset bytes at an existing tag: the portable catalogs and Velopack feeds pin their hashes.

## Channel jobs and credentials

| Channel | Workflow and operator action |
| --- | --- |
| Scoop | **Scoop catalog** checks that the owner bucket has the exact public version, URLs, and SHA-256 hashes. On release publication it can dispatch the bucket's Excavator if the optional Actions secret **SCOOP_BUCKET_TOKEN** has Actions write access to Zettersten/scoop-bucket. Without it, the scheduled Excavator checks every four hours. A pending summary means verify or rerun the job after the bucket commits the update. |
| WinGet | **WinGet catalog** verifies the public x64 and ARM64 assets and submits an update PR after release publication when the package is listed and no Shnapp PR is open. **WINGET_CREATE_GITHUB_TOKEN** is an Actions secret containing a classic GitHub PAT with `public_repo` scope; GitHub's built-in token cannot open a PR against microsoft/winget-pkgs. Microsoft validation and review remain the publication gate. If the first listing or another PR blocked submission, rerun **WinGet catalog** manually with the exact release tag after that gate clears. |
| Chocolatey | **Chocolatey catalog** packs, installs, and uninstalls the package from the verified public assets, then attempts submission after release publication. The existing **CHOCOLATEY_API_KEY** Actions secret is sufficient; no GitHub PAT is needed. A blocked push fails visibly. After resolving moderation, rerun **Chocolatey catalog** manually with the exact release tag. Every version still needs Chocolatey moderation before users can install it. Never place the key in source, logs, issues, or chat. |
| Microsoft Store | **Microsoft Store package candidates** is a manual build and validation workflow for a public tag. It uses repository variables **STORE_IDENTITY_NAME**, **STORE_IDENTITY_PUBLISHER**, and **STORE_PUBLISHER_DISPLAY_NAME** and produces unsigned x64 and ARM64 MSIX/MSIXUPLOAD candidates. It does not submit. Resolve Partner Center developer verification and EU Digital Services Act details, then validate a current build on Windows 11 and complete the first listing in Partner Center. A tested multi-architecture upload bundle and live first listing are gates before enabling automated Store submissions with Partner Center credentials. |

WinGet and Chocolatey run once when a release is published. They do not poll catalogs. If an external review gate blocks submission, run that channel's workflow manually with the public release tag after the gate clears. If Chocolatey already has a page for a rejected version, resolve that version's moderation with Chocolatey before retrying.

The reserved Store identity is **24664Nenvy.Shnapp**, Publisher **CN=B431E658-A1AD-472F-8DC9-270D1AFEB32C**, display name **Nenvy**, Store ID **9P7LX12F1V5L**. Use the [Store listing draft](store/LISTING-DRAFT.md) to finish listing content. The candidates are not Store-certified and must not be represented as published.

## How users receive updates

Velopack-installed copies use the architecture-specific GitHub release feed through `GithubSource`. Shnapp checks for an update, downloads the matching Velopack package, and shows when a restart is needed. **Settings → About & Updates** can save work and restart through Velopack; a prepared update can also apply on a later launch through Velopack's startup behavior. The release must retain each `releases.win-{arch}.json` file and its matching full `.nupkg` for this path to work. An existing ZIP installation does not migrate to Velopack automatically; install the Setup EXE to switch channels and retire the old ZIP copy separately.

Direct ZIP installs check stable GitHub releases, verify the matching architecture and SHA-256 digest, stage a new version, and switch the launcher to it on the next restart. The app leaves the running version intact and retains a previous version for fallback. The v1.0.5 launcher remains in place; a launcher security fix or incompatible protocol needs a fresh ZIP replacement. Older installs without the launcher need one manual replacement.

Scoop, WinGet, and Chocolatey installs use their own upgrade commands after the new manifest or package is accepted. Shnapp offers an in-app update action that saves work, closes, and invokes the relevant manager; a GitHub release by itself does not update those catalogs or installed copies. Store installs use StoreContext and can only receive Store-certified packages. Store and package-manager policies, elevation, and user settings may require a prompt or explicit upgrade.
