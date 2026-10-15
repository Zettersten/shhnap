Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) { throw 'Release signing checks run on Windows.' }

$publisher = 'Nenvy LLC'
$profileEku = '1.3.6.1.4.1.311.97.648645738.819706600.759829221.862934614'
$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$publicTrustEku = '1.3.6.1.4.1.311.97.1.0'
$certificateCheck = Join-Path $PSScriptRoot '../Assert-ReleaseSignerCertificate.ps1'

function New-TestCertificate {
    param([string] $Subject, [string[]] $Ekus)

    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $oids = [System.Security.Cryptography.OidCollection]::new()
        foreach ($oid in $Ekus) {
            [void]$oids.Add([System.Security.Cryptography.Oid]::new($oid))
        }
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $oids, $false))
        return $request.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddHours(-1), [DateTimeOffset]::UtcNow.AddHours(1))
    }
    finally {
        $rsa.Dispose()
    }
}

function Assert-Rejected {
    param([scriptblock] $Check, [string] $Reason)
    $rejected = $false
    try { & $Check }
    catch { $rejected = $true }
    if (-not $rejected) { throw $Reason }
}

$validCert = New-TestCertificate 'CN=Nenvy LLC, O=Nenvy LLC' @(
    $codeSigningEku, $publicTrustEku, $profileEku)
$wrongOrgCert = New-TestCertificate 'CN=Nenvy LLC, O=Different LLC' @(
    $codeSigningEku, $publicTrustEku, $profileEku)
$wrongProfileCert = New-TestCertificate 'CN=Nenvy LLC, O=Nenvy LLC' @(
    $codeSigningEku, $publicTrustEku, '1.3.6.1.4.1.311.97.123.456')
$noPublicTrustCert = New-TestCertificate 'CN=Nenvy LLC, O=Nenvy LLC' @(
    $codeSigningEku, $profileEku)
try {
    & $certificateCheck -Certificate $validCert -ExpectedPublisher $publisher -ExpectedProfileEku $profileEku
    Assert-Rejected {
        & $certificateCheck -Certificate $validCert -ExpectedPublisher 'Other LLC' -ExpectedProfileEku $profileEku
    } 'A different publisher was accepted.'
    Assert-Rejected {
        & $certificateCheck -Certificate $wrongOrgCert -ExpectedPublisher $publisher -ExpectedProfileEku $profileEku
    } 'A different certificate organization was accepted.'
    Assert-Rejected {
        & $certificateCheck -Certificate $wrongProfileCert -ExpectedPublisher $publisher -ExpectedProfileEku $profileEku
    } 'A different Artifact Signing profile EKU was accepted.'
    Assert-Rejected {
        & $certificateCheck -Certificate $noPublicTrustCert -ExpectedPublisher $publisher -ExpectedProfileEku $profileEku
    } 'A certificate without Artifact Signing Public Trust was accepted.'
}
finally {
    foreach ($cert in @($validCert, $wrongOrgCert, $wrongProfileCert, $noPublicTrustCert)) {
        $cert.Dispose()
    }
}

# Archive verification must still reject executables that do not match the release profile.
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
    Assert-Rejected {
        & $verifier -ArchivePath $archive -EntryPaths @('lib/app/Shnapp.exe') `
            -SignToolPath $signTool.FullName -ExpectedPublisher $publisher -ExpectedProfileEku $profileEku
    } 'An executable signed by another publisher was accepted.'

    [System.IO.File]::WriteAllBytes($executable, [byte[]]@(0x4D, 0x5A, 0x00, 0x00))
    [System.IO.Compression.ZipFile]::CreateFromDirectory($input, $unsignedArchive)
    Assert-Rejected {
        & $verifier -ArchivePath $unsignedArchive -EntryPaths @('lib/app/Shnapp.exe') `
            -SignToolPath $signTool.FullName -ExpectedPublisher $publisher -ExpectedProfileEku $profileEku
    } 'An unsigned archive executable was accepted.'
    # Native command failures are expected in the negative tests.
    $global:LASTEXITCODE = 0
    Write-Host 'Release signing checks accepted the expected profile and rejected other and unsigned signers.'
}
finally {
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith('shnapp-signature-test-')) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
