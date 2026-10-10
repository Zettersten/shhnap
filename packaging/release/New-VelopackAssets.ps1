param(
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64')][string] $RuntimeIdentifier,
    [Parameter(Mandatory = $true)][string] $VpkPath,
    [Parameter(Mandatory = $true)][string] $WorkDirectory,
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [string] $SignToolPath,
    [string] $CertificateThumbprint,
    [switch] $Unsigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -cnotmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'ReleaseTag must be a stable v1.0.0-style tag.'
}
if (-not (Test-Path -LiteralPath $VpkPath -PathType Leaf)) {
    throw "Velopack CLI not found: $VpkPath"
}
if ($Unsigned -and $ReleaseTag -cnotin @('v1.0.12', 'v1.0.13')) {
    throw 'Unsigned Velopack packaging is allowed only for v1.0.12 and v1.0.13.'
}
if (-not $Unsigned -and (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf) -or
    $CertificateThumbprint -cnotmatch '^[0-9A-Fa-f]{40}$')) {
    throw 'A SignTool executable and code-signing certificate are required for Velopack packaging.'
}

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$version = $ReleaseTag.Substring(1)
$architecture = $RuntimeIdentifier.Substring(4)
$platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$packId = "ErikZettersten.Shnapp.$architecture"
$generatedSetupName = "$packId-$RuntimeIdentifier-Setup.exe"
$setupName = if ([version]$version -lt [version]'1.0.12') {
    $generatedSetupName
}
else { "Shnapp-$ReleaseTag-$architecture.exe" }
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

    $signingArguments = @()
    if (-not $Unsigned) {
        $signParams = "/sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256"
        $signingArguments = @('--signParams', $signParams)
    }
    & $VpkPath pack --packId $packId --packVersion $version `
        --packDir $publishDirectory --mainExe Shnapp.exe `
        --runtime "win11-$architecture" --channel $RuntimeIdentifier `
        --outputDir $velopackDirectory --packTitle Shnapp `
        --packAuthors 'Erik Zettersten' --icon 'src/Shnapp.App/Assets/AppIcon.ico' `
        --instLicense LICENSE --noPortable @signingArguments
    if ($LASTEXITCODE -ne 0) { throw 'Velopack pack failed.' }

    $expected = @(
        "assets.$RuntimeIdentifier.json",
        "$packId-$version-$RuntimeIdentifier-full.nupkg",
        $generatedSetupName,
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
        $destinationName = if ($name -ceq $generatedSetupName) { $setupName } else { $name }
        Copy-Item -LiteralPath $source -Destination (Join-Path $output $destinationName)
    }
    $assetListPath = Join-Path $output "assets.$RuntimeIdentifier.json"
    $assetList = @(Get-Content -LiteralPath $assetListPath -Raw | ConvertFrom-Json)
    $installers = @($assetList | Where-Object { $_.Type -ceq 'Installer' })
    if ($installers.Count -ne 1 -or $installers[0].RelativeFileName -cne $generatedSetupName) {
        throw 'Velopack asset list does not identify the generated Setup executable.'
    }
    $installers[0].RelativeFileName = $setupName
    [System.IO.File]::WriteAllText($assetListPath,
        ((ConvertTo-Json -InputObject $assetList -Depth 16) + [char]10),
        [System.Text.UTF8Encoding]::new($false))
    $setupPath = Join-Path $output $setupName
    if (-not $Unsigned) {
        & (Join-Path $PSScriptRoot 'Sign-ReleaseExecutable.ps1') `
            -Path $setupPath -SignToolPath $SignToolPath `
            -CertificateThumbprint $CertificateThumbprint -VerifyOnly
    }
    $packagePath = Join-Path $output "$packId-$version-$RuntimeIdentifier-full.nupkg"
    $packageArchive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $packageExecutables = @($packageArchive.Entries |
            Where-Object { $_.FullName -match '\.exe$' } |
            ForEach-Object FullName)
    }
    finally {
        $packageArchive.Dispose()
    }
    if ('lib/app/Shnapp.exe' -cnotin $packageExecutables) {
        throw 'The Velopack full package is missing Shnapp.exe.'
    }
    if (-not $Unsigned) {
        & (Join-Path $PSScriptRoot 'Assert-ReleaseArchiveSignatures.ps1') `
            -ArchivePath $packagePath -EntryPaths $packageExecutables `
            -SignToolPath $SignToolPath -CertificateThumbprint $CertificateThumbprint
    }
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$setupPath.sha256", "$hash  $setupName`n",
        [System.Text.Encoding]::ASCII)
    Write-Host "Created Velopack $RuntimeIdentifier release assets for $ReleaseTag."
}
finally {
    Pop-Location
}
