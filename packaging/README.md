# Distribution runbook

Shnapp is a free Windows 11 app. Stable tags use the form **vX.Y.Z**. The tag supplies the portable app version **X.Y.Z** and the Store package version **X.Y.Z.0**. A tag stages a draft GitHub Release; publication is a separate, reviewed action.

## Channel status at the last check (October 2, 2026)

| Channel | State and acceptance gate |
| --- | --- |
| Direct ZIP | [v1.0.6](https://github.com/Zettersten/shhnap/releases/tag/v1.0.6) is public with single-file app payloads for x64 and ARM64. |
| Scoop | The [owner bucket](https://github.com/Zettersten/scoop-bucket) has v1.0.6. Check its committed manifest after each release; a dispatch alone does not establish availability. |
| WinGet | [Initial listing PR #445332](https://github.com/microsoft/winget-pkgs/pull/445332) is open for v1.0.6. Microsoft must accept it before later versions can be submitted. |
| Chocolatey | [v1.0.0](https://community.chocolatey.org/packages/shnapp/1.0.0) is in Community moderation. The workflow will retry the latest release after the first version is approved. Each new version also needs moderation. |
| Microsoft Store | The Shnapp identity is reserved under public Store publisher **Nenvy**. Developer verification and EU Digital Services Act compliance block the first listing. Store submission automation is disabled. |

Check the public catalogs again before changing website availability labels. None of the catalog jobs should be treated as proof that a submitted version is available to users.

## Portable build and verification

The release workflow publishes a self-contained, single-file **app** executable for x64 and ARM64, then places it in a versioned ZIP. This was tested locally before adding it to the release build. The ZIP deliberately contains more than one file:

| ZIP location | Purpose |
| --- | --- |
| Root Shnapp.exe | Stable launcher used by existing v1.0.5 installs and the restart updater. |
| Root current-version.txt | Selects the active version. |
| Root LICENSE and THIRD_PARTY_NOTICES.txt | Required distribution notices. |
| versions/vX.Y.Z/Shnapp.exe | Single-file app payload. |
| versions/vX.Y.Z/Shnapp.pri | Compatibility with the v1.0.5 updater; the app also bundles its WinUI resources. |
| versions/vX.Y.Z/LICENSE and THIRD_PARTY_NOTICES files | Notices for the exact bundled dependencies. |

Keep the ZIP layout intact. The launcher and version pointer maintain the v1.0.5 updater protocol; replacing them with one bare EXE would break in-place updates. The bundled .NET and Windows App SDK native components can extract into the user's Temp directory on first launch. “Single-file” describes the app payload in the download, not every file used while it runs.

CI tests and builds x64 on pull requests and main. A stable tag builds both architectures, verifies the ZIP layout and SHA-256 sidecars, uploads the four release assets, re-downloads them, checks their bytes, and creates catalog candidates. Test x64 capture, annotation, save, share, startup, direct update, and uninstall on Windows 11 before publishing. ARM64 is built and checked structurally in CI; record the lack of a device test until one is available. Review license notices and unsigned-download/SmartScreen behavior for the exact build.

## Cut and publish a release

1. Merge the reviewed change to main. Release notes are generated from merged pull requests; the tag run requires at least one change.
2. Create and push a stable vX.Y.Z tag on a commit reachable from main. **Windows build and release** runs tests, packages both ZIPs, generates version-specific notes, and stages a **draft** GitHub Release. It will not overwrite an existing release.
3. Inspect the Actions run, the draft's **New in vX.Y.Z** notes, the four assets (x64/ARM64 ZIPs and checksum files), and the **Catalog-candidates-vX.Y.Z** artifact. Download and smoke-test x64. Edit the draft notes if needed before publication.
4. Run **Actions → Windows build and release → Run workflow → publish-github-release** on main with the exact tag. The job verifies the draft, assets, hashes, and reviewed notes again, then makes it public.
5. The same operation writes **latest.json** to the separate [**release-metadata** branch](https://github.com/Zettersten/shhnap/tree/release-metadata). The [download page](https://shhnap.com/download/) fetches its version, release notes, and versioned download URLs at runtime from [the raw feed](https://raw.githubusercontent.com/Zettersten/shhnap/release-metadata/latest.json). This branch exists so a release can update the website's version and download links without rebuilding the site. It was seeded with v1.0.5 and updated twice for v1.0.6 (release data, then refreshed notes); those are the recent branch pushes. A normal PR merge does not change the feed. The publisher verifies public stable release assets and refuses to roll the feed back to an older version.
6. Verify the feed and website show the new version and that both download URLs resolve. Inspect the downstream Scoop, WinGet, and Chocolatey job summaries and then verify each public catalog separately. The site can display the GitHub download immediately; show a package-manager version as available only after its catalog accepts it.

The feed was initialized from the verified public v1.0.5 assets. If a later release became public but feed publication failed, run **refresh-release-metadata** with the same public tag. It verifies the release again and republishes latest.json without creating a release or rerunning catalog submissions. Fix the cause if it fails. Never replace public ZIP bytes at an existing tag: package managers pin their hashes.

## Channel jobs and credentials

| Channel | Workflow and operator action |
| --- | --- |
| Scoop | **Scoop catalog** checks that the owner bucket has the exact public version, URLs, and SHA-256 hashes. On release publication it can dispatch the bucket's Excavator if the optional Actions secret **SCOOP_BUCKET_TOKEN** has Actions write access to Zettersten/scoop-bucket. Without it, the scheduled Excavator checks every four hours. A pending summary means verify or rerun the job after the bucket commits the update. |
| WinGet | **WinGet catalog** verifies the public x64 and ARM64 assets and submits an update PR after release publication when the package is listed and no Shnapp PR is open. Its scheduled run checks the latest public release again, so a version held by initial-listing review is submitted after that gate clears. Add **WINGET_CREATE_GITHUB_TOKEN** as an Actions secret: a classic GitHub PAT with `public_repo` scope. GitHub's built-in token cannot open a PR against microsoft/winget-pkgs. Microsoft validation and review remain the publication gate. |
| Chocolatey | **Chocolatey catalog** packs, installs, and uninstalls the package from the verified public assets. It submits after release publication when the Community repository allows it, and its scheduled run retries the latest public release after first-package moderation clears. The existing **CHOCOLATEY_API_KEY** Actions secret is sufficient; no GitHub PAT is needed. The workflow checks for an already submitted version to avoid duplicates. Every version still needs Chocolatey moderation before users can install it. Never place the key in source, logs, issues, or chat. |
| Microsoft Store | **Microsoft Store package candidates** is a manual build and validation workflow for a public tag. It uses repository variables **STORE_IDENTITY_NAME**, **STORE_IDENTITY_PUBLISHER**, and **STORE_PUBLISHER_DISPLAY_NAME** and produces unsigned x64 and ARM64 MSIX/MSIXUPLOAD candidates. It does not submit. Resolve Partner Center developer verification and EU Digital Services Act details, then validate a current build on Windows 11 and complete the first listing in Partner Center. A tested multi-architecture upload bundle and live first listing are gates before enabling automated Store submissions with Partner Center credentials. |

The reserved Store identity is **24664Nenvy.Shnapp**, Publisher **CN=B431E658-A1AD-472F-8DC9-270D1AFEB32C**, display name **Nenvy**, Store ID **9P7LX12F1V5L**. Use the [Store listing draft](store/LISTING-DRAFT.md) to finish listing content. The candidates are not Store-certified and must not be represented as published.

## How users receive updates

Direct ZIP installs check stable GitHub releases, verify the matching architecture and SHA-256 digest, stage a new version, and switch the launcher to it on the next restart. The app leaves the running version intact and retains a previous version for fallback. The v1.0.5 launcher remains in place; a launcher security fix or incompatible protocol needs a fresh ZIP replacement. Older installs without the launcher need one manual replacement.

Scoop, WinGet, and Chocolatey installs use their own upgrade commands after the new manifest or package is accepted. Shnapp offers an in-app update action that saves work, closes, and invokes the relevant manager; a GitHub release by itself does not update those catalogs or installed copies. Store installs use StoreContext and can only receive Store-certified packages. Store and package-manager policies, elevation, and user settings may require a prompt or explicit upgrade.
