param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$pageUrl = "https://community.chocolatey.org/packages/shnapp/$Version"
$response = Invoke-WebRequest -Uri $pageUrl -UseBasicParsing -TimeoutSec 30
if ([int] $response.StatusCode -ne 200) {
    throw "Unexpected Chocolatey response for $pageUrl`: $($response.StatusCode)"
}

# Fail closed if Chocolatey no longer explicitly identifies this version as in moderation.
$visibleText = [System.Net.WebUtility]::HtmlDecode([regex]::Replace($response.Content, '<[^>]+>', ' '))
$visibleText = [regex]::Replace($visibleText, '\s+', ' ')
if ($visibleText -notmatch 'This version is in moderation and has not yet been approved\.') {
    throw "Chocolatey $Version is not confirmed as awaiting moderation; refusing to overwrite it."
}

$feedUrl = "https://community.chocolatey.org/api/v2/Packages(Id='shnapp',Version='$Version')"
[xml] $feed = (Invoke-WebRequest -Uri $feedUrl -UseBasicParsing -TimeoutSec 30).Content
$feedVersion = $feed.SelectSingleNode("//*[local-name()='properties']/*[local-name()='Version']")
$published = $feed.SelectSingleNode("//*[local-name()='properties']/*[local-name()='Published']")
if ($null -eq $feedVersion -or $feedVersion.InnerText -cne $Version -or
    $null -eq $published -or $published.InnerText -cne '1900-01-01T00:00:00') {
    throw "Chocolatey $Version is not unpublished in the package feed; refusing to overwrite it."
}
