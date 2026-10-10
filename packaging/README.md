# Distribution runbook

Shnapp is a free Windows 11 app. Stable tags use the form **vX.Y.Z**. The tag supplies the portable and Velopack app version **X.Y.Z** and the Store package version **X.Y.Z.0**. A tag stages a draft GitHub Release; publication is a separate, reviewed action.

## Channel status

Use the [download page](https://shhnap.com/download/), [GitHub releases](https://github.com/Zettersten/shhnap/releases), [Scoop bucket](https://github.com/Zettersten/scoop-bucket), and [Chocolatey package page](https://community.chocolatey.org/packages/shnapp) for available downloads. Chocolatey approved Shnapp v1.0.0 on October 10, 2026; its catalog version can lag the latest GitHub release while newer packages await moderation. The [WinGet submissions](https://github.com/microsoft/winget-pkgs/pulls?q=is%3Apr+Zettersten.Shnapp) still await a first listing. Microsoft Store submission remains disabled until Partner Center account requirements and the first listing are complete. A catalog submission becomes available only after that catalog accepts it.

The website recommends the x64 or ARM64 Velopack installer when the release metadata includes verified installers. It keeps the portable ZIP available beside each installer. Published v1 metadata and an unavailable feed leave the current ZIP links and extraction instructions in place. Scoop and Chocolatey consume the ZIP assets; WinGet package candidates also target the ZIP assets rather than the Velopack installer.

## Direct installer and portable build

For each architecture, the release workflow publishes two separate distributions:

| Distribution | Release assets and use |
| --- | --- |
| Velopack direct install | `ErikZettersten.Shnapp.{arch}-win-{arch}-Setup.exe` and its `.sha256` file are the website's preferred downloads when present. The same release also carries `ErikZettersten.Shnapp.{arch}-{version}-win-{arch}-full.nupkg`, `releases.win-{arch}.json`, `assets.win-{arch}.json`, and `RELEASES-win-{arch}` for Velopack's update and deployment protocol. The release build currently publishes full packages, without delta packages. |
| Portable ZIP | `Shnapp-win-{arch}.zip` and its `.sha256` file remain available for direct ZIP users and the Scoop and Chocolatey channels, and as inputs to pending WinGet packages. |

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

### Signing and verification

CI tests and builds x64 on pull requests and main without signing credentials. A stable tag needs the **release-signing** Actions environment, restricted to `v*` tags and requiring review by Zettersten (self review is allowed). Provide these two environment secrets:

| Secret | Value |
| --- | --- |
| `RELEASE_SIGNING_PFX_BASE64` | Single-line base64 encoding of an exportable, trusted Authenticode code-signing PFX with its private key. Keep the PFX out of the repository. |
| `RELEASE_SIGNING_PFX_PASSWORD` | Password for that PFX. |

The tag job imports the certificate into its ephemeral Windows user store, requires a current code-signing EKU and private key, and removes the temporary PFX file. It signs and RFC 3161 time-stamps both the portable ZIP's launcher and versioned app before archiving them. Velopack signs the installer and its packaged executables through `--signParams`. The job then verifies Authenticode trust, the expected signer, and a timestamp on the **emitted** ZIP executables, Setup EXE, and every EXE in the full package. An absent credential, failed timestamp, invalid signature, or unexpected signer stops packaging before any draft release is staged. The current Velopack build emits a Setup EXE and full package; it does not emit an MSI. If the certificate authority does not offer an exportable PFX, change this workflow to use a provisioned signing service such as Azure Artifact Signing and verify the same outputs before tagging.

A stable tag builds both architectures, verifies the ZIP layouts, Velopack packages and feeds, and SHA-256 sidecars, uploads 16 release assets, re-downloads them, checks their bytes, and creates catalog candidates. The successful tag run also records its exact commit and asset hashes as a separate Actions artifact. Smoke-test the x64 installer, its launch, update through Settings, and uninstall on Windows 11. Also test x64 ZIP capture, annotation, save, share, startup, and direct update. ARM64 is built and checked structurally in CI; record the lack of a device test until one is available. Review license notices and SmartScreen behavior for the exact signed build.

## Cut and publish a release

1. Merge the reviewed change to main through a pull request. The `main` ruleset requires the x64 build/test, release-script tests, and strict website build checks. Release notes are generated from merged pull requests; the tag run requires at least one change.
2. Confirm [GitHub release immutability](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes) is enabled and the signing environment and secrets above are ready. Create and push a stable vX.Y.Z tag on a commit reachable from main. The `v*` ruleset blocks tag updates and deletions. **Windows build and release** runs tests, signs and packages both ZIPs and both Velopack installers and update feeds, generates version-specific notes, and stages a **draft** GitHub Release. A retry uploads only missing assets whose existing draft siblings match the current build; changed draft assets stop the job for review.
3. Inspect the successful Actions run, the draft's **New in vX.Y.Z** notes, all 16 assets (eight per architecture), the **Release-evidence-vX.Y.Z** artifact, and the **Catalog-candidates-vX.Y.Z** artifact. Check the installer and ZIP checksum sidecars and confirm each `releases.win-{arch}.json` points to its matching full `.nupkg`. Download and smoke-test the x64 installer and ZIP. Edit the draft notes if needed before publication.
4. The **release-publication** Actions environment is restricted to `main` and requires review by Zettersten; self review is currently allowed. Add its environment secret `RELEASE_SETTINGS_TOKEN`: a fine-grained GitHub PAT scoped to this repository with **Administration: read**. The manual publisher uses it only to read the [immutable-releases setting](https://docs.github.com/en/rest/repos/repos#get-the-immutable-releases-setting-for-a-repository); the normal `GITHUB_TOKEN` cannot request that permission. Run **Actions → Windows build and release → Run workflow → publish-github-release** on main with the exact tag. The job requires immutability enabled, verifies that the tag still points to the successful staged build commit, compares every draft asset with that build's recorded SHA-256, validates the feed inputs, and then makes the release public. A missing secret or failed provenance check stops publication; do not replace draft bytes to bypass it.
5. The same operation writes schema-version 2 **latest.json** to the separate [**release-metadata** branch](https://github.com/Zettersten/shhnap/tree/release-metadata) and reports whether the public release and feed match. Its `installers` entries carry the Setup URLs and hashes; `downloads` retains the ZIP URLs and hashes. The [download page](https://shhnap.com/download/) fetches the version, release notes, and verified URLs at runtime from [the raw feed](https://raw.githubusercontent.com/Zettersten/shhnap/release-metadata/latest.json). It prefers installers for a valid v2 feed and retains ZIP links for v1 or unavailable metadata. This branch lets a release update the website without rebuilding it. Its ruleset blocks force pushes and deletions. A normal PR merge does not change the feed. The publisher verifies public stable release assets and refuses to roll the feed back to an older version.
6. Verify the feed and website show the new version, that both Setup downloads and their checksums resolve, and that both portable ZIP links and checksums still resolve. Inspect the downstream Scoop, WinGet, and Chocolatey job summaries and verify each public catalog separately. The site can display the GitHub downloads immediately; show a package-manager version as available only after its catalog accepts it.

If a release became public but feed publication failed, the workflow summary marks the partial state. Run **refresh-release-metadata** with that public tag. It verifies the release again and republishes latest.json without creating a release or rerunning catalog submissions. Fix the cause if it fails. Never replace public asset bytes at an existing tag: release immutability, the portable catalogs, and Velopack feeds pin them.

## Channel jobs and credentials

| Channel | Workflow and operator action |
| --- | --- |
| Scoop | **Scoop catalog** checks that the owner bucket has the exact public version, URLs, and SHA-256 hashes. On release publication it can dispatch the bucket's Excavator if the optional Actions secret **SCOOP_BUCKET_TOKEN** has Actions write access to Zettersten/scoop-bucket. Without it, the scheduled Excavator checks every four hours. A pending summary means verify or rerun the job after the bucket commits the update. |
| WinGet | **WinGet catalog** verifies the public x64 and ARM64 assets and submits an update PR after release publication when the package is listed and no Shnapp PR is open. **WINGET_CREATE_GITHUB_TOKEN** is an Actions secret containing a classic GitHub PAT with `public_repo` scope; GitHub's built-in token cannot open a PR against microsoft/winget-pkgs. Microsoft validation and review remain the publication gate. If the first listing or another PR blocked submission, rerun **WinGet catalog** manually with the exact release tag after that gate clears. |
| Chocolatey | [v1.0.0](https://community.chocolatey.org/packages/shnapp) is the latest approved package. The [v1.0.11 catalog run](https://github.com/Zettersten/shhnap/actions/runs/38057365744) successfully installed, uninstalled, and submitted that version for moderation, confirming **CHOCOLATEY_API_KEY** can publish. **Chocolatey catalog** runs once after a public GitHub release and remains available manually for reruns or `verify_only` checks. Each new version becomes available to users only after Chocolatey approves it, so the catalog may lag GitHub releases. Never place the key in source, logs, issues, or chat. |
| Microsoft Store | **Microsoft Store package candidates** is a manual build and validation workflow for a public tag. It uses repository variables **STORE_IDENTITY_NAME**, **STORE_IDENTITY_PUBLISHER**, and **STORE_PUBLISHER_DISPLAY_NAME** and produces unsigned x64 and ARM64 MSIX/MSIXUPLOAD candidates. It does not submit. Resolve Partner Center developer verification and EU Digital Services Act details, then validate a current build on Windows 11 and complete the first listing in Partner Center. A tested multi-architecture upload bundle and live first listing are gates before enabling automated Store submissions with Partner Center credentials. |

WinGet and Chocolatey run once when a release is published. They regenerate candidates from the release tag's reviewed templates on rerun, so a later main branch change cannot silently change an older version's install script. If moderation requires a catalog-only correction, first merge and review that correction, then rerun the channel workflow with its exact `catalog_source_sha` commit input. For Chocolatey, use `verify_only` to inspect the corrected package before a submission run. They do not poll catalogs. If an external review gate blocks submission, run that channel's workflow manually with the public release tag after the gate clears. If Chocolatey already has a page for a rejected version, resolve that version's moderation with Chocolatey before retrying.

The reserved Store identity is **24664Nenvy.Shnapp**, Publisher **CN=B431E658-A1AD-472F-8DC9-270D1AFEB32C**, display name **Nenvy**, Store ID **9P7LX12F1V5L**. Use the [Store listing draft](store/LISTING-DRAFT.md) to finish listing content. The candidates are not Store-certified and must not be represented as published.

## How users receive updates

Velopack-installed copies use the architecture-specific GitHub release feed through `GithubSource`. Shnapp checks for an update, downloads the matching Velopack package, and shows when a restart is needed. **Settings → About & Updates** can save work and restart through Velopack; a prepared update can also apply on a later launch through Velopack's startup behavior. The release must retain each `releases.win-{arch}.json` file and its matching full `.nupkg` for this path to work. An existing ZIP installation does not migrate to Velopack automatically; install the Setup EXE to switch channels and retire the old ZIP copy separately.

Direct ZIP installs check stable GitHub releases, verify the matching architecture and SHA-256 digest, stage a new version, and switch the launcher to it on the next restart. The app leaves the running version intact and retains a previous version for fallback. The v1.0.5 launcher remains in place; a launcher security fix or incompatible protocol needs a fresh ZIP replacement. Older installs without the launcher need one manual replacement.

Scoop and Chocolatey installs use their package managers' upgrade commands after the new manifest or package is accepted. WinGet installs will use WinGet after its first listing is accepted. Shnapp offers an in-app update action that saves work, closes, and invokes the relevant manager; a GitHub release by itself does not update those catalogs or installed copies. A future Store install would use StoreContext and could receive only Store-certified packages. Store and package-manager policies, elevation, and user settings may require a prompt or explicit upgrade.
