Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) { throw 'Release signing checks run on Windows.' }
$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signTool = Get-ChildItem -LiteralPath $sdkBin -Filter signtool.exe -Recurse -File |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signTool) { throw 'The Windows SDK x64 SignTool is missing.' }
$signature = Get-AuthenticodeSignature -LiteralPath $signTool.FullName
if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate) {
    throw 'The Windows SDK SignTool test fixture is not trusted and time-stamped.'
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-signature-test-$([guid]::NewGuid().ToString('N'))"
$input = Join-Path $scratch 'input'
$entryDirectory = Join-Path $input 'lib/app'
[void][System.IO.Directory]::CreateDirectory($entryDirectory)
$executable = Join-Path $entryDirectory 'Shnapp.exe'
$archive = Join-Path $scratch 'signed.zip'
$unsignedArchive = Join-Path $scratch 'unsigned.zip'
$verifier = Join-Path $PSScriptRoot '../Assert-ReleaseArchiveSignatures.ps1'
try {
    Copy-Item -LiteralPath $signTool.FullName -Destination $executable
    [System.IO.Compression.ZipFile]::CreateFromDirectory($input, $archive)
    & $verifier -ArchivePath $archive -EntryPaths @('lib/app/Shnapp.exe') `
        -SignToolPath $signTool.FullName -CertificateThumbprint $signature.SignerCertificate.Thumbprint

    $wrongSignerRejected = $false
    try {
        & $verifier -ArchivePath $archive -EntryPaths @('lib/app/Shnapp.exe') `
            -SignToolPath $signTool.FullName -CertificateThumbprint ('0' * 40)
    }
    catch { $wrongSignerRejected = $true }
    if (-not $wrongSignerRejected) { throw 'A different signer was accepted.' }

    [System.IO.File]::WriteAllBytes($executable, [byte[]]@(0x4D, 0x5A, 0x00, 0x00))
    [System.IO.Compression.ZipFile]::CreateFromDirectory($input, $unsignedArchive)
    $unsignedRejected = $false
    try {
        & $verifier -ArchivePath $unsignedArchive -EntryPaths @('lib/app/Shnapp.exe') `
            -SignToolPath $signTool.FullName -CertificateThumbprint $signature.SignerCertificate.Thumbprint
    }
    catch { $unsignedRejected = $true }
    if (-not $unsignedRejected) { throw 'An unsigned archive executable was accepted.' }
    # GitHub's pwsh command wrapper returns the last native exit code even when
    # this expected SignTool failure was caught by the negative test.
    $global:LASTEXITCODE = 0
    Write-Host 'Release archive signature checks reject an unsigned or differently signed executable.'
}
finally {
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith('shnapp-signature-test-')) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
