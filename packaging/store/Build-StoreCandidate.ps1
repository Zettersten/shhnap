param(
    [Parameter(Mandatory = $true)][string] $RepositoryRoot,
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64')][string] $RuntimeIdentifier,
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'ARM64')][string] $Platform,
    [Parameter(Mandatory = $true)][string] $IdentityName,
    [Parameter(Mandatory = $true)][string] $IdentityPublisher,
    [Parameter(Mandatory = $true)][string] $PublisherDisplayName,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -notmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Store candidates require a stable vX.Y.Z tag.'
}
if (($RuntimeIdentifier -eq 'win-x64' -and $Platform -ne 'x64') -or
    ($RuntimeIdentifier -eq 'win-arm64' -and $Platform -ne 'ARM64')) {
    throw 'Runtime identifier and MSBuild platform do not match.'
}
foreach ($value in @($IdentityName, $IdentityPublisher, $PublisherDisplayName)) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw 'Set all three Partner Center identity repository variables.'
    }
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-store-$RuntimeIdentifier"
$probeDir = Join-Path $scratch 'notice-probe'
$packageDir = (Join-Path $scratch 'packages') + [System.IO.Path]::DirectorySeparatorChar
$version = $ReleaseTag.Substring(1)
New-Item -ItemType Directory -Path $output -Force | Out-Null

Push-Location $root
try {
    ./packaging/store/Set-StoreManifest.ps1 `
        -ManifestPath src/Shnapp.App/Package.appxmanifest `
        -ReleaseTag $ReleaseTag `
        -IdentityName $IdentityName `
        -IdentityPublisher $IdentityPublisher `
        -PublisherDisplayName $PublisherDisplayName

    dotnet restore src/Shnapp.App/Shnapp.App.csproj --locked-mode "-p:Platform=$Platform"
    if ($LASTEXITCODE -ne 0) { throw "Store restore failed with exit code $LASTEXITCODE." }
    dotnet publish src/Shnapp.App/Shnapp.App.csproj -c Release -r $RuntimeIdentifier `
        --no-restore --self-contained true --output $probeDir `
        "-p:Platform=$Platform" "-p:Version=$version" "-p:AssemblyVersion=$version.0" `
        "-p:FileVersion=$version.0" "-p:InformationalVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Store notice publish failed with exit code $LASTEXITCODE." }
    ./packaging/release/Add-ThirdPartyNotices.ps1 `
        -DepsPath (Join-Path $probeDir 'Shnapp.deps.json') `
        -OutputDirectory "src/Shnapp.App/obj/$Platform/DistributionNotices"

    # MSBuild appends a package folder name, so this property must end with a separator.
    msbuild src/Shnapp.App/Shnapp.App.csproj /restore /p:RestoreLockedMode=true `
        /p:Configuration=Release "/p:Platform=$Platform" "/p:RuntimeIdentifier=$RuntimeIdentifier" `
        /p:SelfContained=true /p:ShnappDistribution=Store /p:GenerateAppxPackageOnBuild=true `
        /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never `
        /p:UapAppxPackageBuildMode=StoreUpload "/p:AppxPackageDir=$packageDir" `
        "/p:Version=$version" "/p:AssemblyVersion=$version.0" "/p:FileVersion=$version.0" `
        "/p:InformationalVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Store MSIX build failed with exit code $LASTEXITCODE." }

    $packages = @(Get-ChildItem -Path $packageDir -Recurse -File -Filter '*.msix')
    $uploads = @(Get-ChildItem -Path $packageDir -Recurse -File -Filter '*.msixupload')
    if ($packages.Count -ne 1 -or $uploads.Count -ne 1) {
        throw "Expected one MSIX and one MSIX upload for $RuntimeIdentifier; found $($packages.Count) and $($uploads.Count)."
    }
    Copy-Item -LiteralPath $packages[0].FullName -Destination (Join-Path $output "Shnapp-$RuntimeIdentifier.msix")
    Copy-Item -LiteralPath $uploads[0].FullName -Destination (Join-Path $output "Shnapp-$RuntimeIdentifier.msixupload")
}
finally {
    Pop-Location
}
