Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Release signing requires a Windows runner.'
}
if ([string]::IsNullOrWhiteSpace($env:RELEASE_SIGNING_PFX_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:RELEASE_SIGNING_PFX_PASSWORD)) {
    throw 'The release-signing environment needs RELEASE_SIGNING_PFX_BASE64 and RELEASE_SIGNING_PFX_PASSWORD.'
}
if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV) -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Release signing must run in GitHub Actions with GITHUB_ENV and RUNNER_TEMP.'
}

$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signTool = Get-ChildItem -LiteralPath $sdkBin -Filter signtool.exe -Recurse -File |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signTool) {
    throw 'The Windows SDK x64 SignTool is required for release signing.'
}

$pfxPath = Join-Path $env:RUNNER_TEMP 'shnapp-release-signing.pfx'
try {
    $pfxBytes = [Convert]::FromBase64String($env:RELEASE_SIGNING_PFX_BASE64)
    [System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
    $password = ConvertTo-SecureString $env:RELEASE_SIGNING_PFX_PASSWORD -AsPlainText -Force
    $certificates = @(Import-PfxCertificate -FilePath $pfxPath `
        -CertStoreLocation Cert:\CurrentUser\My -Password $password -Exportable:$false)
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $signers = @($certificates | Where-Object {
        $certificate = $_
        $eku = @($certificate.Extensions |
            Where-Object { $_.Oid.Value -eq '2.5.29.37' })
        $certificate.HasPrivateKey -and
            $certificate.NotBefore.ToUniversalTime() -le [DateTime]::UtcNow -and
            $certificate.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow -and
            @($eku | ForEach-Object {
                ([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages |
                    Where-Object { $_.Value -eq $codeSigningOid }
            }).Count -gt 0
    })
    if ($signers.Count -ne 1) {
        throw 'The signing PFX must contain exactly one current code-signing certificate with a private key.'
    }
    $thumbprint = $signers[0].Thumbprint.ToUpperInvariant()
    if ($thumbprint -cnotmatch '^[0-9A-F]{40}$') {
        throw 'The code-signing certificate has an invalid thumbprint.'
    }
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "RELEASE_SIGNING_THUMBPRINT=$thumbprint"
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "RELEASE_SIGNTOOL_PATH=$($signTool.FullName)"
    Write-Host "Imported release code-signing certificate $thumbprint."
}
finally {
    if (Test-Path -LiteralPath $pfxPath -PathType Leaf) {
        Remove-Item -LiteralPath $pfxPath -Force
    }
}
