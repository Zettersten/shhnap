param(
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][string] $Repository,
    [Parameter(Mandatory = $true)][string] $ReleaseJsonPath,
    [Parameter(Mandatory = $true)][string] $AssetsDirectory,
    [Parameter(Mandatory = $true)][string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -cnotmatch '^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'ReleaseTag must be a stable v1.0.0-style tag.'
}
if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be an owner/repository name.'
}

$releaseJson = (Resolve-Path -LiteralPath $ReleaseJsonPath).Path
$assetsPath = (Resolve-Path -LiteralPath $AssetsDirectory).Path
$releaseText = [System.IO.File]::ReadAllText($releaseJson)
$release = $releaseText | ConvertFrom-Json
$releaseUrl = "https://github.com/$Repository/releases/tag/$ReleaseTag"
if ($release.tagName -cne $ReleaseTag -or $release.url -cne $releaseUrl -or
    $release.isDraft -cne $false -or $release.isPrerelease -cne $false) {
    throw 'Release metadata must describe the requested public stable release.'
}
if ($release.body -isnot [string] -or [string]::IsNullOrWhiteSpace($release.body)) {
    throw 'The public release must have notes.'
}
if ($release.body.Length -gt 30000) {
    throw 'Release notes exceed the website feed limit of 30,000 characters.'
}
$document = [System.Text.Json.JsonDocument]::Parse($releaseText)
try { $publishedAt = $document.RootElement.GetProperty('publishedAt').GetString() }
finally { $document.Dispose() }
if ($publishedAt -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$') {
    throw 'The public release must have a UTC publication timestamp.'
}
try {
    [void][System.DateTimeOffset]::Parse($publishedAt, [System.Globalization.CultureInfo]::InvariantCulture)
}
catch {
    throw 'The public release has an invalid publication timestamp.'
}

$expectedNames = @(
    'Shnapp-win-x64.zip', 'Shnapp-win-x64.zip.sha256',
    'Shnapp-win-arm64.zip', 'Shnapp-win-arm64.zip.sha256'
)
$assets = @($release.assets)
$localItems = @(Get-ChildItem -LiteralPath $assetsPath -Force)
if ($assets.Count -ne $expectedNames.Count -or $localItems.Count -ne $expectedNames.Count) {
    throw 'The release and assets directory must contain exactly two ZIPs and their checksum files.'
}

$hashes = @{}
foreach ($asset in $assets) {
    $name = [string]$asset.name
    if ($name -cnotin $expectedNames -or $hashes.ContainsKey($name)) {
        throw "Unexpected or duplicate release asset: $name"
    }
    $expectedUrl = "https://github.com/$Repository/releases/download/$ReleaseTag/$name"
    if ($asset.url -cne $expectedUrl) {
        throw "Release asset has an unexpected URL: $name"
    }
    if ($asset.digest -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw "Release asset has no usable SHA-256 digest: $name"
    }
    $path = Join-Path $assetsPath $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing downloaded release asset: $name"
    }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne [long]$asset.size -or $item.Length -eq 0) {
        throw "Downloaded release asset has the wrong size: $name"
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $asset.digest.Substring(7)) {
        throw "Downloaded release asset does not match GitHub's SHA-256 digest: $name"
    }
    $hashes[$name] = $actualHash
}

$downloads = [ordered]@{}
foreach ($architecture in @('x64', 'arm64')) {
    $archiveName = "Shnapp-win-$architecture.zip"
    $checksumName = "$archiveName.sha256"
    $checksumText = [System.IO.File]::ReadAllText((Join-Path $assetsPath $checksumName))
    $pattern = '^([0-9a-f]{64})  ' + [regex]::Escape($archiveName) + '(?:\r?\n)?\z'
    $match = [regex]::Match($checksumText, $pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups[1].Value -cne $hashes[$archiveName]) {
        throw "Checksum file does not match the release ZIP: $checksumName"
    }
    $downloads[$architecture] = [ordered]@{
        url = "https://github.com/$Repository/releases/download/$ReleaseTag/$archiveName"
        sha256 = $hashes[$archiveName]
    }
}

$feed = [ordered]@{
    schemaVersion = 1
    version = $ReleaseTag.Substring(1)
    tag = $ReleaseTag
    publishedAt = $publishedAt
    releaseUrl = $releaseUrl
    notes = $release.body
    downloads = $downloads
}
$output = [System.IO.Path]::GetFullPath($OutputPath)
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output))
$json = $feed | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($output, $json + [char]10, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote release metadata for $ReleaseTag to $output"
