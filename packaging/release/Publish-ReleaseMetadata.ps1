param(
    [Parameter(Mandatory = $true)][string] $FilePath,
    [Parameter(Mandatory = $true)][string] $Repository,
    [string] $Branch = 'release-metadata',
    [string] $BaseBranch = 'main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be an owner/repository name.'
}
foreach ($branchName in @($Branch, $BaseBranch)) {
    if ($branchName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw 'Branch names must be simple branch names without slashes.'
    }
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'Set GH_TOKEN to a GitHub Actions token with contents: write.'
}

function Get-StableVersion {
    param([object] $Metadata)

    if ($Metadata.schemaVersion -cnotin @(1, 2)) {
        throw 'Release metadata has an unsupported schema version.'
    }
    $expectedFields = @('schemaVersion', 'version', 'tag', 'publishedAt',
        'releaseUrl', 'notes', 'downloads')
    if ($Metadata.schemaVersion -ceq 2) { $expectedFields += 'installers' }
    $fields = @($Metadata.PSObject.Properties.Name)
    if ($fields.Count -ne $expectedFields.Count -or
        @(Compare-Object $fields $expectedFields).Count -ne 0) {
        throw 'Release metadata does not have the expected feed fields.'
    }
    if ($Metadata.tag -isnot [string] -or
        $Metadata.tag -cnotmatch '^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $Metadata.version -cne $Metadata.tag.Substring(1)) {
        throw 'Release metadata has an invalid schema version or stable tag.'
    }
    if ($Metadata.releaseUrl -cne "https://github.com/$Repository/releases/tag/$($Metadata.tag)" -or
        [string]::IsNullOrWhiteSpace([string]$Metadata.publishedAt) -or
        $Metadata.notes -isnot [string] -or
        [string]::IsNullOrWhiteSpace($Metadata.notes)) {
        throw 'Release metadata has invalid publication details.'
    }
    $architectures = @($Metadata.downloads.PSObject.Properties.Name)
    if ($architectures.Count -ne 2 -or
        @(Compare-Object $architectures @('x64', 'arm64')).Count -ne 0) {
        throw 'Release metadata must have x64 and ARM64 downloads.'
    }
    foreach ($architecture in @('x64', 'arm64')) {
        $download = $Metadata.downloads.$architecture
        $expectedUrl = "https://github.com/$Repository/releases/download/$($Metadata.tag)/Shnapp-win-$architecture.zip"
        if ($download.url -cne $expectedUrl -or $download.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw "Release metadata has an invalid $architecture download."
        }
    }
    if ($Metadata.schemaVersion -ceq 2) {
        $installerArchitectures = @($Metadata.installers.PSObject.Properties.Name)
        if ($installerArchitectures.Count -ne 2 -or
            @(Compare-Object $installerArchitectures @('x64', 'arm64')).Count -ne 0) {
            throw 'Release metadata must have x64 and ARM64 Velopack installers.'
        }
        foreach ($architecture in @('x64', 'arm64')) {
            $installer = $Metadata.installers.$architecture
            $fileName = "ErikZettersten.Shnapp.$architecture-win-$architecture-Setup.exe"
            $expectedUrl = "https://github.com/$Repository/releases/download/$($Metadata.tag)/$fileName"
            if ($installer.url -cne $expectedUrl -or $installer.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "Release metadata has an invalid $architecture installer."
            }
        }
    }
    return $Metadata.version
}

function Compare-StableVersions {
    param([string] $Left, [string] $Right)

    $leftParts = $Left.Split('.')
    $rightParts = $Right.Split('.')
    for ($index = 0; $index -lt 3; $index++) {
        $leftNumber = [System.Numerics.BigInteger]::Parse($leftParts[$index])
        $rightNumber = [System.Numerics.BigInteger]::Parse($rightParts[$index])
        $comparison = $leftNumber.CompareTo($rightNumber)
        if ($comparison -ne 0) { return $comparison }
    }
    return 0
}

$file = (Resolve-Path -LiteralPath $FilePath).Path
$newBytes = [System.IO.File]::ReadAllBytes($file)
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
$newMetadata = $utf8.GetString($newBytes) | ConvertFrom-Json
$newVersion = Get-StableVersion $newMetadata
$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $env:GH_TOKEN"
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'Shnapp-release-metadata'
}
$apiRoot = "https://api.github.com/repos/$Repository"

function Invoke-GitHubApi {
    param(
        [string] $Method,
        [string] $Path,
        [object] $Body = $null,
        [switch] $AllowNotFound
    )

    $request = @{
        Method = $Method
        Uri = "$apiRoot/$Path"
        Headers = $headers
        ErrorAction = 'Stop'
    }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Depth 8 -Compress
    }
    try {
        return Invoke-RestMethod @request
    }
    catch {
        $statusCode = 0
        if ($null -ne $_.Exception.PSObject.Properties['Response'] -and
            $null -ne $_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        if ($AllowNotFound -and $statusCode -eq 404) { return $null }
        throw "GitHub API $Method $Path failed (HTTP $statusCode)."
    }
}

$branchPath = "git/ref/heads/$Branch"
$branchRef = Invoke-GitHubApi -Method GET -Path $branchPath -AllowNotFound
if ($null -eq $branchRef) {
    $baseRef = Invoke-GitHubApi -Method GET -Path "git/ref/heads/$BaseBranch"
    if ($baseRef.object.type -cne 'commit' -or $baseRef.object.sha -notmatch '^[0-9a-f]{40}$') {
        throw 'The base branch does not point to a Git commit.'
    }
    try {
        [void](Invoke-GitHubApi -Method POST -Path 'git/refs' -Body @{
            ref = "refs/heads/$Branch"
            sha = $baseRef.object.sha
        })
    }
    catch {
        # Another release job may have created the branch first.
        $branchRef = Invoke-GitHubApi -Method GET -Path $branchPath -AllowNotFound
        if ($null -eq $branchRef) { throw }
    }
}

$currentFile = Invoke-GitHubApi -Method GET -Path "contents/latest.json?ref=$Branch" -AllowNotFound
$requestBody = @{
    message = "Publish Shnapp $($newMetadata.tag) release metadata"
    branch = $Branch
    content = [System.Convert]::ToBase64String($newBytes)
}
if ($null -ne $currentFile) {
    if ($currentFile.type -cne 'file' -or $currentFile.encoding -cne 'base64' -or
        [string]::IsNullOrWhiteSpace($currentFile.sha) -or
        [string]::IsNullOrWhiteSpace($currentFile.content)) {
        throw 'The existing latest.json is not a readable file.'
    }
    $existingBytes = [System.Convert]::FromBase64String($currentFile.content)
    $existingMetadata = $utf8.GetString($existingBytes) | ConvertFrom-Json
    $existingVersion = Get-StableVersion $existingMetadata
    $comparison = Compare-StableVersions $newVersion $existingVersion
    if ($comparison -lt 0) {
        throw "Refusing to replace newer release metadata v$existingVersion with v$newVersion."
    }
    if ([System.Convert]::ToBase64String($newBytes) -ceq
        [System.Convert]::ToBase64String($existingBytes)) {
        Write-Host "Release metadata for v$newVersion is already published."
        exit 0
    }
    $requestBody.sha = $currentFile.sha
}

[void](Invoke-GitHubApi -Method PUT -Path 'contents/latest.json' -Body $requestBody)
Write-Host "Published Shnapp v$newVersion metadata to $Repository branch $Branch."
