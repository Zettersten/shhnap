param(
    [Parameter(Mandatory = $true)][int]$AppPid,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$FixturePath,
    [switch]$VerifyQuit
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShnappSmokeNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct IconIdentifier
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id;
        public Guid Guid;
    }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect bounds);
}
'@

function Invoke-Ui {
    param([string[]]$Arguments)
    $output = & winapp ui @Arguments --json
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    if ($output) { return (($output -join "`n") | ConvertFrom-Json) }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Wait-Until {
    param([scriptblock]$Condition, [string]$Message, [int]$Timeout = 8000)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $Timeout) {
        try {
            if (& $Condition) { return }
        } catch {
            # UI Automation can have no visible target during a capture transition.
        }
        Start-Sleep -Milliseconds 100
    }
    throw $Message
}

function Test-Flow {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    } catch {
        $results.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message })
    }
}

function Documents {
    $path = Join-Path $DataRoot 'shnapps'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    return @(Get-ChildItem -LiteralPath $path -Filter document.json -Recurse -File |
        ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json })
}

function Latest-Document {
    return (Documents | Sort-Object createdAt -Descending | Select-Object -First 1)
}

function Wait-Editor {
    Invoke-Ui -Arguments @('wait-for', 'ShnappCanvas', '-a', "$AppPid", '-t', '8000') | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'CopyShnapp', '-a', "$AppPid", '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ShnappStatus', '-a', "$AppPid", '--value', 'Saved locally', '--contains', '-t', '8000') | Out-Null
}
function Native-SavePicker {
    # WinRT file pickers are owned windows in a separate PickerHost process.
    return (Invoke-Ui -Arguments @('list-windows') | Where-Object {
        $_.ownerHwnd -eq $script:MainHwnd -and $_.processName -eq 'PickerHost' -and $_.className -eq '#32770'
    } | Select-Object -First 1)
}

function Open-Settings {
    Invoke-Ui -Arguments @('invoke', 'MoreButton', '-w', "$script:MainHwnd") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'OpenSettings', '-a', "$AppPid", '-t', '3000') | Out-Null
    Invoke-Ui -Arguments @('invoke', 'OpenSettings', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ShnappTheme', '-a', "$AppPid", '-t', '3000') | Out-Null
}

function Select-Theme {
    param([string]$Name)
    Invoke-Ui -Arguments @('invoke', 'ShnappTheme', '-a', "$AppPid") | Out-Null
    $option = (Invoke-Ui -Arguments @('search', $Name, '-a', "$AppPid")).matches |
        Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Name } | Select-Object -First 1
    Assert-True ($null -ne $option) "Theme option $Name was not found."
    Invoke-Ui -Arguments @('invoke', $option.selector, '-a', "$AppPid") | Out-Null
}

function Activate-Fixture {
    [ShnappSmokeNative]::SetForegroundWindow([IntPtr]$fixture.hwnd) | Out-Null
    Start-Sleep -Milliseconds 150
    if ([ShnappSmokeNative]::GetForegroundWindow() -ne [IntPtr]$fixture.hwnd) {
        Invoke-Ui -Arguments @('click', "$($fixture.x + 160),$($fixture.y + 220)", '-w', "$($fixture.hwnd)") | Out-Null
    }
    Wait-Until { [ShnappSmokeNative]::GetForegroundWindow() -eq [IntPtr]$fixture.hwnd } 'The safe capture fixture could not become the foreground window.' 3000
}

function Capture-Evidence {
    param([string]$Name)
    $path = Join-Path $media $Name
    Invoke-Ui -Arguments @('screenshot', '-w', "$script:MainHwnd", '-o', $path) | Out-Null
    $image = [System.Drawing.Image]::FromFile($path)
    try {
        Assert-True ($image.Width -ge 1366 -and $image.Height -ge 768) "$Name is smaller than the Store desktop screenshot minimum (1366 x 768)."
    } finally {
        $image.Dispose()
    }
}

function Image-Point {
    param([double]$X, [double]$Y)
    $element = (Invoke-Ui -Arguments @('wait-for', 'ShnappCanvas', '-a', "$AppPid", '-t', '2000')).element
    $document = Latest-Document
    $dpi = [ShnappSmokeNative]::GetDpiForWindow([IntPtr]$script:MainHwnd) / 96.0
    $padding = 0
    $extraHeight = 0
    if ($document.hasWindowShadow) { $padding = 32; $extraHeight = 8 }
    $flatWidth = $document.pixelWidth + 2 * $padding
    $flatHeight = $document.pixelHeight + 2 * $padding + $extraHeight
    $scale = [Math]::Min($dpi, [Math]::Min(
        ($element.width - 64 * $dpi) / $flatWidth,
        ($element.height - 104 * $dpi) / $flatHeight))
    $left = $element.x + ($element.width - $flatWidth * $scale) / 2
    $top = $element.y + 48 * $dpi + ($element.height - 48 * $dpi - $flatHeight * $scale) / 2
    return "$([int]($left + ($X + $padding) * $scale)),$([int]($top + ($Y + $padding) * $scale))"
}

$fixture = [System.IO.File]::ReadAllText($FixturePath) | ConvertFrom-Json
$script:MainHwnd = (Get-Process -Id $AppPid).MainWindowHandle.ToInt64()
[ShnappSmokeNative]::ShowWindow([IntPtr]$script:MainHwnd, 3) | Out-Null # SW_MAXIMIZE
$media = Join-Path $DataRoot 'evidence'
[System.IO.Directory]::CreateDirectory($media) | Out-Null
Assert-True (@(Documents).Count -eq 0) 'Use a fresh isolated data root; existing shnapps must not be used as verification evidence.'
Assert-True ((Get-Process -Id $fixture.processId).Responding) 'The non-sensitive capture fixture is not running.'
$initialTree = & winapp ui inspect -a "$AppPid" --json
Assert-True ($LASTEXITCODE -eq 0 -and ($initialTree -join "`n") -notmatch 'Shortcut already in use') 'Capture shortcuts are in use; close the conflicting app before running the full UI smoke.'

Test-Flow 'Responsive shell and native tray registration' {
    Assert-True ((Get-Process -Id $AppPid).Responding) 'Shnapp is not responding.'
    $icon = New-Object ShnappSmokeNative+IconIdentifier
    $icon.Size = [System.Runtime.InteropServices.Marshal]::SizeOf($icon)
    $icon.Hwnd = [IntPtr]$script:MainHwnd
    $icon.Id = 1
    $rectangle = New-Object ShnappSmokeNative+Rect
    Assert-True ([ShnappSmokeNative]::Shell_NotifyIconGetRect([ref]$icon, [ref]$rectangle) -eq 0) 'The Windows notification area has no Shnapp icon.'
}

Test-Flow 'Window shortcut selects the fixture and opens the editor' {
    Activate-Fixture
    [ShnappSmokeNative]::SetCursorPos(($fixture.x + 160), ($fixture.y + 220)) | Out-Null
    Invoke-Ui -Arguments @('send-keys', 'ctrl+shift+4', '-w', "$($fixture.hwnd)", '--via', 'send-input') | Out-Null
    Wait-Until { @(Invoke-Ui -Arguments @('list-windows', '-a', "$AppPid") | Where-Object { $_.title -match 'Shnapp.*Window' }).Count -gt 0 } 'Window picker did not appear.'
    $picker = Invoke-Ui -Arguments @('list-windows', '-a', "$AppPid") | Where-Object { $_.title -match 'Shnapp.*Window' } | Select-Object -First 1
    $point = "$($fixture.x + 160),$($fixture.y + 220)"
    Invoke-Ui -Arguments @('drag', $point, $point, '-w', "$($picker.hwnd)") | Out-Null
    Wait-Editor
    $document = Latest-Document
    Assert-True ($document.captureKind -eq 'Window') 'The result was not a window capture.'
    Assert-True ($document.title -like 'Shnapp capture fixture*') 'The selected window was not the safe fixture.'
    Assert-True ($document.hasWindowShadow) 'Window shadow default was not applied.'
    $script:WindowDocument = $document
}

Test-Flow 'Annotations, numbering, and undo/redo persist' {
    Invoke-Ui -Arguments @('invoke', 'ToolStep', '-a', "$AppPid") | Out-Null
    $first = Image-Point 80 180
    $second = Image-Point 80 300
    Invoke-Ui -Arguments @('drag', $first, $first, '-w', "$script:MainHwnd") | Out-Null
    Invoke-Ui -Arguments @('drag', $second, $second, '-w', "$script:MainHwnd") | Out-Null
    Wait-Until { @((Latest-Document).annotations | Where-Object { $_.kind -eq 'Step' }).Count -eq 2 } 'Two numbered steps were not saved.'
    Invoke-Ui -Arguments @('invoke', 'UndoEdit', '-a', "$AppPid") | Out-Null
    Wait-Until { @((Latest-Document).annotations | Where-Object { $_.kind -eq 'Step' }).Count -eq 1 } 'Undo was not saved.'
    Invoke-Ui -Arguments @('invoke', 'RedoEdit', '-a', "$AppPid") | Out-Null
    Wait-Until { @((Latest-Document).annotations | Where-Object { $_.kind -eq 'Step' }).Count -eq 2 } 'Redo was not saved.'
    $steps = @((Latest-Document).annotations | Where-Object { $_.kind -eq 'Step' })
    Assert-True ($steps[0].stepNumber -eq 1 -and $steps[1].stepNumber -eq 2) 'Step numbering is not consecutive.'
    [ShnappSmokeNative]::SetForegroundWindow([IntPtr]$script:MainHwnd) | Out-Null
    Invoke-Ui -Arguments @('send-keys', 'a', '-w', "$script:MainHwnd", '--via', 'send-input') | Out-Null
    Invoke-Ui -Arguments @('drag', (Image-Point 500 230), (Image-Point 285 245), '-w', "$script:MainHwnd") | Out-Null
    Wait-Until { @((Latest-Document).annotations | Where-Object { $_.kind -eq 'Line' -and $_.endCap -eq 'Triangle' }).Count -eq 1 } 'Arrow preset was not saved as a capped line.'
}

Test-Flow 'Opaque redaction and exportable transparent shadow' {
    Invoke-Ui -Arguments @('invoke', 'ToolRedact', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('drag', (Image-Point 100 440), (Image-Point 380 482), '-w', "$script:MainHwnd") | Out-Null
    Wait-Until { @((Latest-Document).annotations | Where-Object { $_.kind -eq 'Redaction' }).Count -eq 1 } 'Redaction was not saved.'
    $document = Latest-Document
    $folder = Join-Path (Join-Path $DataRoot 'shnapps') ([Guid]$document.id).ToString('N')
    $image = [System.Drawing.Bitmap]::FromFile((Join-Path $folder 'shnapp.png'))
    try {
        Assert-True ($image.Width -eq ($document.pixelWidth + 64)) 'Window export has incorrect horizontal padding.'
        Assert-True ($image.Height -eq ($document.pixelHeight + 72)) 'Window export has incorrect vertical padding.'
        $redaction = $document.annotations | Where-Object { $_.kind -eq 'Redaction' } | Select-Object -First 1
        $x = [int](($redaction.start.x + $redaction.end.x) / 2) + 32
        $y = [int](($redaction.start.y + $redaction.end.y) / 2) + 32
        $pixel = $image.GetPixel($x, $y)
        Assert-True ($pixel.A -eq 255 -and $pixel.R -eq 17 -and $pixel.G -eq 20 -and $pixel.B -eq 24) 'Redaction does not fully replace covered output pixels.'
        Assert-True ($image.GetPixel(0, 0).A -lt 255) 'Export padding is not transparent.'
    } finally {
        $image.Dispose()
    }
    Capture-Evidence 'editor.png'
}

Test-Flow 'Copy produces an image and PNG Save uses the native picker' {
    Invoke-Ui -Arguments @('invoke', 'CopyShnapp', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ShnappStatus', '-a', "$AppPid", '--value', 'Copied', '--contains', '-t', '5000') | Out-Null
    Assert-True ([System.Windows.Forms.Clipboard]::ContainsImage()) 'Clipboard does not contain the copied fixture image.'
    Invoke-Ui -Arguments @('invoke', 'SaveShnapp', '-a', "$AppPid") | Out-Null
    try {
        Wait-Until { $null -ne (Native-SavePicker) } 'The native Save picker did not appear.'
        $picker = Native-SavePicker
        $path = Join-Path $DataRoot 'manual-export.png'
        Invoke-Ui -Arguments @('set-value', 'FileNameControlHost', $path, '-w', "$($picker.hwnd)") | Out-Null
        $saveButton = (Invoke-Ui -Arguments @('search', 'Save', '-w', "$($picker.hwnd)")).matches |
            Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save' } | Select-Object -First 1
        Assert-True ($null -ne $saveButton) 'The native Save picker has no Save button.'
        Invoke-Ui -Arguments @('invoke', $saveButton.selector, '-w', "$($picker.hwnd)") | Out-Null
        Wait-Until { Test-Path -LiteralPath $path } 'The chosen PNG export was not written.'
        Invoke-Ui -Arguments @('wait-for', 'ShnappStatus', '-a', "$AppPid", '--value', 'PNG exported', '--contains', '-t', '5000') | Out-Null
        $image = [System.Drawing.Bitmap]::FromFile($path)
        try {
            $document = Latest-Document
            Assert-True ($image.Width -eq ($document.pixelWidth + 64) -and $image.Height -eq ($document.pixelHeight + 72)) 'The native PNG export has incorrect dimensions.'
        } finally {
            $image.Dispose()
        }
    } finally {
        $remaining = Native-SavePicker
        if ($remaining) {
            Invoke-Ui -Arguments @('invoke', 'Cancel', '-w', "$($remaining.hwnd)") | Out-Null
            Wait-Until { $null -eq (Native-SavePicker) } 'The failed Save test left a modal picker open.'
        }
    }
}

Test-Flow 'Region shortcut cancels without creating a shnapp' {
    $count = @(Documents).Count
    Invoke-Ui -Arguments @('send-keys', 'ctrl+shift+2', '-w', "$script:MainHwnd", '--via', 'send-input') | Out-Null
    Wait-Until { @(Invoke-Ui -Arguments @('list-windows', '-a', "$AppPid") | Where-Object { $_.title -match 'Shnapp.*Free form' }).Count -gt 0 } 'Region picker did not appear.'
    $picker = Invoke-Ui -Arguments @('list-windows', '-a', "$AppPid") | Where-Object { $_.title -match 'Shnapp.*Free form' } | Select-Object -First 1
    Invoke-Ui -Arguments @('send-keys', 'esc', '-w', "$($picker.hwnd)", '--via', 'send-input') | Out-Null
    Wait-Until { [ShnappSmokeNative]::IsWindowVisible([IntPtr]$script:MainHwnd) } 'Cancellation did not restore the previous editor.'
    Assert-True (@(Documents).Count -eq $count) 'Cancellation created a saved document.'
}

Test-Flow 'Region shortcut preserves the dragged physical pixel dimensions' {
    $count = @(Documents).Count
    Activate-Fixture
    Invoke-Ui -Arguments @('send-keys', 'ctrl+shift+2', '-w', "$($fixture.hwnd)", '--via', 'send-input') | Out-Null
    Wait-Until { @(Invoke-Ui -Arguments @('list-windows', '-a', "$AppPid") | Where-Object { $_.title -match 'Shnapp.*Free form' }).Count -gt 0 } 'Region picker did not appear.'
    $picker = Invoke-Ui -Arguments @('list-windows', '-a', "$AppPid") | Where-Object { $_.title -match 'Shnapp.*Free form' } | Select-Object -First 1
    Invoke-Ui -Arguments @('drag', "$($fixture.x + 120),$($fixture.y + 200)",
        "$($fixture.x + 620),$($fixture.y + 500)", '-w', "$($picker.hwnd)") | Out-Null
    Wait-Editor
    Wait-Until { @(Documents).Count -eq ($count + 1) } 'Region metadata was not saved.'
    $document = Latest-Document
    Assert-True ($document.captureKind -eq 'Region' -and $document.pixelWidth -eq 500 -and $document.pixelHeight -eq 300) 'Region dimensions differ from the 500 x 300 physical-pixel drag.'
}

Test-Flow 'Full-screen shortcut captures the monitor under the pointer' {
    $fullPath = Join-Path $DataRoot 'fullscreen-fixture.json'
    $fixtureScript = Join-Path $PSScriptRoot 'Start-CaptureFixture.ps1'
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$fixtureScript`"", '-OutputPath', "`"$fullPath`"", '-FullScreen') -PassThru
    try {
        Wait-Until { Test-Path -LiteralPath $fullPath } 'Full-screen fixture did not become ready.'
        $full = [System.IO.File]::ReadAllText($fullPath) | ConvertFrom-Json
        [ShnappSmokeNative]::SetForegroundWindow([IntPtr]$full.hwnd) | Out-Null
        [ShnappSmokeNative]::SetCursorPos(($full.x + 100), ($full.y + 100)) | Out-Null
        Invoke-Ui -Arguments @('send-keys', 'ctrl+shift+3', '-w', "$($full.hwnd)", '--via', 'send-input') | Out-Null
        Wait-Editor
        $document = Latest-Document
        Assert-True ($document.captureKind -eq 'FullScreen') 'The result was not a full-screen capture.'
        Assert-True ($document.pixelWidth -eq $full.screenWidth -and $document.pixelHeight -eq $full.screenHeight) 'Full-screen output does not match the pointer monitor.'
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    }
}

Test-Flow 'Close-to-tray and second launch reopen one existing instance' {
    Invoke-Ui -Arguments @('send-keys', 'alt+f4', '-w', "$script:MainHwnd", '--via', 'send-input', '--allow-system-keys') | Out-Null
    Wait-Until { -not [ShnappSmokeNative]::IsWindowVisible([IntPtr]$script:MainHwnd) } 'Closing did not hide the editor.'
    Assert-True (-not (Get-Process -Id $AppPid).HasExited) 'Closing terminated the tray process.'
    $app = (Get-Process -Id $AppPid).Path
    $second = Start-Process -FilePath $app -ArgumentList @('--data-root', "`"$DataRoot`"") -PassThru
    Wait-Until { $second.Refresh(); $second.HasExited } 'A second tray process stayed running.'
    Wait-Until { [ShnappSmokeNative]::IsWindowVisible([IntPtr]$script:MainHwnd) } 'The existing tray instance did not reopen.'
    Invoke-Ui -Arguments @('wait-for', 'ShnappLibrary', '-a', "$AppPid", '-t', '5000') | Out-Null
    Invoke-Ui -Arguments @('set-value', 'TextBox', 'Shnapp capture fixture', '-a', "$AppPid") | Out-Null
    Capture-Evidence 'library.png'
}
Test-Flow 'Library search and accessible cards reopen editable annotations' {
    $selector = 'Shnapp_' + ([Guid]$script:WindowDocument.id).ToString('N')
    $entry = (Invoke-Ui -Arguments @('wait-for', $selector, '-a', "$AppPid", '-t', '3000')).element
    Assert-True ($entry.name -eq $script:WindowDocument.title) 'The library card does not expose its title to accessibility.'
    Invoke-Ui -Arguments @('invoke', $selector, '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ShnappCanvas', '-a', "$AppPid", '-t', '5000') | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'CopyShnapp', '-a', "$AppPid", '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
    $document = Documents | Where-Object { $_.id -eq $script:WindowDocument.id } | Select-Object -First 1
    Assert-True (@($document.annotations).Count -eq 4) 'Reopened annotations were not retained.'
}

Test-Flow 'Settings navigation and light/dark themes persist without startup changes' {
    Open-Settings
    Invoke-Ui -Arguments @('wait-for', 'StartOnLogin', '-a', "$AppPid", '-p', 'IsEnabled', '--value', 'False', '-t', '3000') | Out-Null
    Invoke-Ui -Arguments @('invoke', 'SettingsUpdates', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'CheckForUpdates', '-a', "$AppPid", '-t', '3000') | Out-Null
    Capture-Evidence 'settings-updates.png'
    Invoke-Ui -Arguments @('invoke', 'SettingsFeedback', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ReportBug', '-a', "$AppPid", '-t', '3000') | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'RequestFeature', '-a', "$AppPid", '-t', '3000') | Out-Null
    Capture-Evidence 'settings-feedback.png'
    Invoke-Ui -Arguments @('invoke', 'SettingsGeneral', '-a', "$AppPid") | Out-Null
    Select-Theme 'Dark'
    Invoke-Ui -Arguments @('invoke', 'SavePreferences', '-a', "$AppPid") | Out-Null
    $settingsPath = Join-Path $DataRoot 'settings.json'
    Wait-Until { (Test-Path -LiteralPath $settingsPath) -and
        (([System.IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json).settings.theme -eq 'Dark') } 'Dark theme was not saved.'
    $settings = [System.IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
    Assert-True ($settings.settings.theme -eq 'Dark') 'Dark theme was not persisted.'
    Invoke-Ui -Arguments @('invoke', 'BackToEditor', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ShnappCanvas', '-a', "$AppPid", '-t', '5000') | Out-Null
    Capture-Evidence 'editor-dark.png'
    Open-Settings
    Select-Theme 'Light'
    Invoke-Ui -Arguments @('invoke', 'SavePreferences', '-a', "$AppPid") | Out-Null
    Wait-Until { ([System.IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json).settings.theme -eq 'Light' } 'Light theme was not saved.'
    $settings = [System.IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
    Assert-True ($settings.settings.theme -eq 'Light' -and -not $settings.settings.startOnLogin) 'Light theme or disabled startup was not persisted.'
    Invoke-Ui -Arguments @('invoke', 'BackToEditor', '-a', "$AppPid") | Out-Null
    Invoke-Ui -Arguments @('wait-for', 'ShnappCanvas', '-a', "$AppPid", '-t', '5000') | Out-Null
    Capture-Evidence 'editor-light.png'
}

Test-Flow 'Startup remains disabled in the isolated verification instance' {
    $path = Join-Path $DataRoot 'settings.json'
    if (Test-Path -LiteralPath $path) {
        $settings = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json
        Assert-True (-not $settings.settings.startOnLogin) 'Verification enabled sign-in startup.'
    }
}

if ($VerifyQuit) {
    Test-Flow 'Explicit Quit removes the tray icon and releases all global shortcuts' {
        Invoke-Ui -Arguments @('invoke', 'MoreButton', '-w', "$script:MainHwnd") | Out-Null
        Invoke-Ui -Arguments @('wait-for', 'QuitShnapp', '-a', "$AppPid", '-t', '3000') | Out-Null
        Invoke-Ui -Arguments @('invoke', 'QuitShnapp', '-a', "$AppPid") | Out-Null
        Wait-Until { $null -eq (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) } 'Quit did not terminate the tray instance.'
        $icon = New-Object ShnappSmokeNative+IconIdentifier
        $icon.Size = [System.Runtime.InteropServices.Marshal]::SizeOf($icon)
        $icon.Hwnd = [IntPtr]$script:MainHwnd
        $icon.Id = 1
        $rectangle = New-Object ShnappSmokeNative+Rect
        Assert-True ([ShnappSmokeNative]::Shell_NotifyIconGetRect([ref]$icon, [ref]$rectangle) -ne 0) 'Quit left a registered tray icon.'
        foreach ($key in @(0x34, 0x33, 0x32)) {
            $registered = [ShnappSmokeNative]::RegisterHotKey([IntPtr]::Zero, $key, 0x4006, $key)
            try {
                Assert-True $registered "Quit did not release Ctrl+Shift+$([char]$key)."
            } finally {
                if ($registered) { [ShnappSmokeNative]::UnregisterHotKey([IntPtr]::Zero, $key) | Out-Null }
            }
        }
    }
}

[System.IO.File]::WriteAllText((Join-Path $DataRoot 'ui-results.json'), ($results | ConvertTo-Json -Depth 5),
    (New-Object System.Text.UTF8Encoding($false)))
$results | Format-Table name, status -AutoSize
$failures = @($results | Where-Object { $_.status -eq 'FAIL' })
foreach ($failure in $failures) { Write-Host "$($failure.name): $($failure.detail)" }
if ($failures.Count -gt 0) { exit 1 }
exit 0
