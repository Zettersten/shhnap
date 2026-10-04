param(
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64')][string] $RuntimeIdentifier,
    [Parameter(Mandatory = $true)][string] $VpkPath,
    [Parameter(Mandatory = $true)][string] $WorkDirectory,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -cnotmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'ReleaseTag must be a stable v1.0.0-style tag.'
}
if (-not (Test-Path -LiteralPath $VpkPath -PathType Leaf)) {
    throw "Velopack CLI not found: $VpkPath"
}

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$version = $ReleaseTag.Substring(1)
$architecture = $RuntimeIdentifier.Substring(4)
$platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$packId = "ErikZettersten.Shnapp.$architecture"
$publishDirectory = Join-Path $WorkDirectory 'publish'
$velopackDirectory = Join-Path $WorkDirectory 'velopack'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
foreach ($directory in @($publishDirectory, $velopackDirectory, $output)) {
    [void][System.IO.Directory]::CreateDirectory($directory)
}

Push-Location $repository
try {
    dotnet publish src/Shnapp.App/Shnapp.App.csproj -c Release -r $RuntimeIdentifier `
        --no-restore --self-contained true --output $publishDirectory `
        "-p:Platform=$platform" -p:ShnappDistribution=Velopack `
        "-p:Version=$version" "-p:AssemblyVersion=$version.0" `
        "-p:FileVersion=$version.0" "-p:InformationalVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw 'Velopack app publish failed.' }

    $buildDirectory = (dotnet msbuild src/Shnapp.App/Shnapp.App.csproj -getProperty:TargetDir `
        -p:Configuration=Release "-p:Platform=$platform" `
        "-p:RuntimeIdentifier=$RuntimeIdentifier" -p:ShnappDistribution=Velopack).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $buildDirectory -PathType Container)) {
        throw 'Could not locate the Velopack app build output.'
    }
    foreach ($name in @('Shnapp.exe', 'Shnapp.pri', 'LICENSE')) {
        $source = Join-Path $buildDirectory $name
        if ($name -eq 'Shnapp.exe') { $source = Join-Path $publishDirectory $name }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Velopack app output is missing $name."
        }
        if ($name -ne 'Shnapp.exe' -and -not (Test-Path -LiteralPath (Join-Path $publishDirectory $name))) {
            Copy-Item -LiteralPath $source -Destination $publishDirectory
        }
    }
    $depsPath = Join-Path $buildDirectory 'Shnapp.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw 'Velopack app build output is missing Shnapp.deps.json.'
    }
    ./packaging/release/Add-ThirdPartyNotices.ps1 `
        -DepsPath $depsPath -OutputDirectory $publishDirectory

    & $VpkPath pack --packId $packId --packVersion $version `
        --packDir $publishDirectory --mainExe Shnapp.exe `
        --runtime "win11-$architecture" --channel $RuntimeIdentifier `
        --outputDir $velopackDirectory --packTitle Shnapp `
        --packAuthors 'Erik Zettersten' --icon 'src/Shnapp.App/Assets/AppIcon.ico' `
        --instLicense LICENSE --noPortable
    if ($LASTEXITCODE -ne 0) { throw 'Velopack pack failed.' }

    $expected = @(
        "assets.$RuntimeIdentifier.json",
        "$packId-$version-$RuntimeIdentifier-full.nupkg",
        "$packId-$RuntimeIdentifier-Setup.exe",
        "RELEASES-$RuntimeIdentifier",
        "releases.$RuntimeIdentifier.json"
    )
    $actual = @(Get-ChildItem -LiteralPath $velopackDirectory -File | ForEach-Object Name)
    if (@(Compare-Object $expected $actual).Count -ne 0) {
        throw "Velopack produced unexpected files for $RuntimeIdentifier`: $($actual -join ', ')"
    }
    foreach ($name in $expected) {
        $source = Join-Path $velopackDirectory $name
        if ((Get-Item -LiteralPath $source).Length -eq 0) {
            throw "Velopack produced an empty file: $name"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $output $name)
    }
    $setupName = "$packId-$RuntimeIdentifier-Setup.exe"
    $setupPath = Join-Path $output $setupName
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$setupPath.sha256", "$hash  $setupName`n",
        [System.Text.Encoding]::ASCII)
    Write-Host "Created Velopack $RuntimeIdentifier release assets for $ReleaseTag."
}
finally {
    Pop-Location
}
