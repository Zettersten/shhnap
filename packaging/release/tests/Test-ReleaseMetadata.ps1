Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$writer = Join-Path $repository 'packaging/release/Write-ReleaseMetadata.ps1'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-release-metadata-test-$([guid]::NewGuid().ToString('N'))"
$assets = Join-Path $scratch 'assets'
$releasePath = Join-Path $scratch 'release.json'
$feedPath = Join-Path $scratch 'latest.json'
$tag = 'v1.2.3'
$draftSlug = 'untagged-5d3ab9754bd2bcc662cd'
$repositoryName = 'Zettersten/shhnap'
[void][System.IO.Directory]::CreateDirectory($assets)

try {
    $names = @(
        foreach ($architecture in @('x64', 'arm64')) {
            $rid = "win-$architecture"
            $packId = "ErikZettersten.Shnapp.$architecture"
            "Shnapp-$rid.zip"
            "Shnapp-$rid.zip.sha256"
            "assets.$rid.json"
            "$packId-1.2.3-$rid-full.nupkg"
            "Shnapp-$tag-$architecture.exe"
            "Shnapp-$tag-$architecture.exe.sha256"
            "RELEASES-$rid"
            "releases.$rid.json"
        }
    )
    foreach ($name in $names) {
        [System.IO.File]::WriteAllText((Join-Path $assets $name), "test bytes for $name")
    }
    foreach ($architecture in @('x64', 'arm64')) {
        $rid = "win-$architecture"
        $packId = "ErikZettersten.Shnapp.$architecture"
        foreach ($name in @("Shnapp-$rid.zip", "Shnapp-$tag-$architecture.exe")) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            [System.IO.File]::WriteAllText((Join-Path $assets "$name.sha256"), "$hash  $name`n")
        }
    }
    $releaseAssets = @(
        foreach ($name in $names) {
            $item = Get-Item -LiteralPath (Join-Path $assets $name)
            @{
                name = $name
                url = "https://github.com/$repositoryName/releases/download/$draftSlug/$name"
                digest = 'sha256:' + (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                size = $item.Length
            }
        }
    )
    $release = [ordered]@{
        tagName = $tag
        url = "https://github.com/$repositoryName/releases/tag/$draftSlug"
        isDraft = $true
        isPrerelease = $false
        publishedAt = $null
        body = "## New in $tag`n`n- Test release."
        assets = $releaseAssets
    }
    $release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releasePath
    & $writer -ReleaseTag $tag -Repository $repositoryName -ReleaseJsonPath $releasePath `
        -AssetsDirectory $assets -DraftPreflight
    if (Test-Path -LiteralPath $feedPath) { throw 'Draft preflight wrote a public feed.' }

    $releaseAssets[0].digest = 'sha256:' + ('0' * 64)
    $release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releasePath
    $rejected = $false
    try {
        & $writer -ReleaseTag $tag -Repository $repositoryName -ReleaseJsonPath $releasePath `
            -AssetsDirectory $assets -DraftPreflight
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Draft preflight accepted a changed asset digest.' }

    $releaseAssets[0].digest = 'sha256:' +
        (Get-FileHash -LiteralPath (Join-Path $assets $names[0]) -Algorithm SHA256).Hash.ToLowerInvariant()
    $release.isDraft = $false
    $release.publishedAt = '2026-10-04T00:00:00Z'
    $release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releasePath
    $rejected = $false
    try {
        & $writer -ReleaseTag $tag -Repository $repositoryName -ReleaseJsonPath $releasePath `
            -AssetsDirectory $assets -OutputPath $feedPath
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Public release metadata accepted a draft URL.' }

    $release.url = "https://github.com/$repositoryName/releases/tag/$tag"
    $release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releasePath
    $rejected = $false
    try {
        & $writer -ReleaseTag $tag -Repository $repositoryName -ReleaseJsonPath $releasePath `
            -AssetsDirectory $assets -OutputPath $feedPath
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Public release metadata accepted draft asset URLs.' }

    foreach ($asset in $releaseAssets) {
        $asset.url = "https://github.com/$repositoryName/releases/download/$tag/$($asset.name)"
    }
    $release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releasePath
    & $writer -ReleaseTag $tag -Repository $repositoryName -ReleaseJsonPath $releasePath `
        -AssetsDirectory $assets -OutputPath $feedPath
    $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
    if ($feed.schemaVersion -ne 2 -or $feed.tag -cne $tag -or
        @($feed.installers.PSObject.Properties.Name).Count -ne 2) {
        throw 'The public release feed is incomplete.'
    }
    Write-Host 'Draft preflight rejects changed bytes, and public metadata writes schema 2.'
}
finally {
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith('shnapp-release-metadata-test-')) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
