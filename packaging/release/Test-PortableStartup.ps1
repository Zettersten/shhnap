param(
    [Parameter(Mandatory = $true)][string] $PayloadDirectory,
    [int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 120) {
    throw 'Startup timeout must be between 1 and 120 seconds.'
}

$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$executable = Join-Path $payload 'Shnapp.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Portable app is missing: $executable"
}

# A separate data root avoids activating an existing Shnapp instance. The
# launcher health event is set only after the WinUI app has initialized.
$token = [guid]::NewGuid().ToString('N')
$dataRoot = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-startup-test-$token"
[void][System.IO.Directory]::CreateDirectory($dataRoot)
$eventName = "Local\Shnapp.LauncherHealth.$token"
$health = [System.Threading.EventWaitHandle]::new(
    $false, [System.Threading.EventResetMode]::ManualReset, $eventName)
$process = $null
try {
    $process = Start-Process -FilePath $executable `
        -ArgumentList @('--background', '--data-root', "`"$dataRoot`"",
            '--launcher-health', $token) `
        -WorkingDirectory $payload -WindowStyle Hidden -PassThru

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($health.WaitOne(100)) {
            if ($process.HasExited) {
                throw "Portable app exited after signaling startup health (exit $($process.ExitCode))."
            }
            Write-Host "Portable WinUI app started and signaled launcher health in $([math]::Round($timer.Elapsed.TotalSeconds, 1)) seconds."
            return
        }
        if ($process.HasExited) {
            throw "Portable app exited before signaling startup health (exit $($process.ExitCode))."
        }
    }
    throw "Portable app did not signal launcher health within $TimeoutSeconds seconds."
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
    $health.Dispose()
}
