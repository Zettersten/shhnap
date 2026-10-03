param(
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][string] $PayloadDirectory,
    [Parameter(Mandatory = $true)][string] $LauncherDirectory,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -cnotmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'A stable v1.0.0-style release tag is required.'
}

$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$launcher = (Resolve-Path -LiteralPath $LauncherDirectory).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
        throw "Portable output directory is not empty: $output"
    }
}

foreach ($required in @('Shnapp.exe', 'Shnapp.pri', 'LICENSE', 'THIRD_PARTY_NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
        throw "Published app is missing $required."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $launcher 'Shnapp.exe') -PathType Leaf)) {
    throw 'Launcher publish is missing Shnapp.exe.'
}

$versionDirectory = Join-Path $output "versions/$ReleaseTag"
New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $payload -Force |
    Copy-Item -Destination $versionDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $launcher 'Shnapp.exe') -Destination (Join-Path $output 'Shnapp.exe')
foreach ($notice in @('LICENSE', 'THIRD_PARTY_NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $payload $notice) -Destination (Join-Path $output $notice)
}
[System.IO.File]::WriteAllText(
    (Join-Path $output 'current-version.txt'), "$ReleaseTag`n`n", [System.Text.Encoding]::ASCII)

Write-Host "Created portable layout for $ReleaseTag in $output"
