param(
    [Parameter(Mandatory = $true)][string] $PayloadDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'ARM64')][string] $Platform,
    [string] $RedistPath,
    [string] $WixPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ScreenRecorderLib's native DLL imports the dynamic MSVC runtime. Store/MSIX
# obtains it from the declared VCLibs framework; unpackaged releases need it
# next to the application. Copy only Microsoft-signed release DLLs.
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$nativeRecorder = Join-Path $payload 'ScreenRecorderLib.dll'
if (-not (Test-Path -LiteralPath $nativeRecorder -PathType Leaf)) {
    throw "Published app is missing ScreenRecorderLib.dll: $payload"
}
$architecture = $Platform.ToLowerInvariant()
$machine = if ($architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
$cabSuffix = if ($architecture -eq 'arm64') { 'arm64' } else { 'amd64' }
$runtimeNames = @('concrt140.dll', 'msvcp140.dll', 'vcruntime140.dll')
if ($architecture -eq 'x64') { $runtimeNames += 'vcruntime140_1.dll' }

function Assert-Machine {
    param([string] $Path, [int] $Expected)
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 0x40 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Not a PE binary: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt $stream.Length - 6) {
            throw "Invalid PE header: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne $Expected) {
            throw "Binary has the wrong architecture: $Path"
        }
    }
    finally { $reader.Dispose() }
}

function Assert-MicrosoftSignature {
    param([string] $Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation' -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Microsoft-signed, time-stamped VC++ runtime is required: $Path"
    }
}

Assert-Machine $nativeRecorder $machine
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-vcrt-$([guid]::NewGuid().ToString('N'))"
[void][System.IO.Directory]::CreateDirectory($scratch)
try {
    if ([string]::IsNullOrWhiteSpace($RedistPath)) {
        $redist = Join-Path $scratch 'vc_redist.exe'
        Invoke-WebRequest -Uri "https://aka.ms/vc14/vc_redist.$architecture.exe" -OutFile $redist
    }
    else { $redist = (Resolve-Path -LiteralPath $RedistPath).Path }
    Assert-MicrosoftSignature $redist

    if ([string]::IsNullOrWhiteSpace($WixPath)) {
        $tools = Join-Path $scratch 'tools'
        Push-Location $scratch
        try {
            dotnet tool install wix --version 4.0.6 --tool-path $tools
            if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned WiX extractor.' }
        }
        finally { Pop-Location }
        $wix = Join-Path $tools 'wix.exe'
    }
    else { $wix = (Resolve-Path -LiteralPath $WixPath).Path }
    if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) {
        throw "WiX extractor not found: $wix"
    }

    $extracted = Join-Path $scratch 'extracted'
    & $wix burn extract $redist -o $extracted
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the Microsoft VC++ Redistributable.' }

    $cab = $null
    foreach ($candidate in Get-ChildItem -LiteralPath $extracted -File) {
        $stream = [System.IO.File]::OpenRead($candidate.FullName)
        try {
            $header = [byte[]]::new(4)
            $headerLength = $stream.Read($header, 0, 4)
        }
        finally { $stream.Dispose() }
        if ($headerLength -ne 4 -or [Text.Encoding]::ASCII.GetString($header) -ne 'MSCF') {
            continue
        }
        $listing = (& expand.exe -D $candidate.FullName | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "Could not list VC++ CAB: $($candidate.FullName)" }
        $matchesAll = $true
        foreach ($name in $runtimeNames) {
            if ($listing -notmatch [regex]::Escape("${name}_${cabSuffix}")) {
                $matchesAll = $false
                break
            }
        }
        if ($matchesAll) { $cab = $candidate.FullName; break }
    }
    if ($null -eq $cab) { throw "VC++ Redistributable has no $architecture release runtime CAB." }

    $dllDirectory = Join-Path $scratch 'dlls'
    [void][System.IO.Directory]::CreateDirectory($dllDirectory)
    foreach ($name in $runtimeNames) {
        $entry = "${name}_${cabSuffix}"
        & expand.exe "-F:$entry" $cab $dllDirectory | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not extract $entry from VC++ Redistributable." }
        $source = Join-Path $dllDirectory $entry
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing VC++ runtime file: $entry" }
        Assert-MicrosoftSignature $source
        Assert-Machine $source $machine
        Copy-Item -LiteralPath $source -Destination (Join-Path $payload $name) -Force
    }
    Write-Host "Bundled Microsoft VC++ runtime $((Get-Item -LiteralPath $redist).VersionInfo.ProductVersion) for $architecture`: $($runtimeNames -join ', ')"
}
finally {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith('shnapp-vcrt-')) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
