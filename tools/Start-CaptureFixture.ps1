param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$FullScreen
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShnappFixtureNative
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
}
'@
[ShnappFixtureNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Shnapp capture fixture'
$form.StartPosition = 'Manual'
$form.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#F7F9FC')
$form.Font = New-Object System.Drawing.Font('Segoe UI', 12)
if ($FullScreen) {
    $form.FormBorderStyle = 'None'
    $form.Bounds = $screen.Bounds
} else {
    $form.Size = New-Object System.Drawing.Size(1000, 680)
    $form.Location = New-Object System.Drawing.Point(
        ($screen.WorkingArea.X + [Math]::Max(0, ($screen.WorkingArea.Width - $form.Width) / 2)),
        ($screen.WorkingArea.Y + [Math]::Max(0, ($screen.WorkingArea.Height - $form.Height) / 2)))
}

$form.Add_Paint({
    param($sender, $eventArgs)
    $graphics = $eventArgs.Graphics
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $width = $sender.ClientSize.Width
    $height = $sender.ClientSize.Height
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#1B1F24'))
    $muted = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#5C6672'))
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#0A84FF'))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $heading = New-Object System.Drawing.Font('Segoe UI', 30, [System.Drawing.FontStyle]::Bold)
    $body = New-Object System.Drawing.Font('Segoe UI', 16)
    $small = New-Object System.Drawing.Font('Segoe UI', 12)
    $graphics.DrawString('A clearer way to explain.', $heading, $ink, 56, 56)
    $graphics.DrawString('Capture a detail. Add a little context. Share it.', $body, $muted, 60, 118)
    $graphics.FillRectangle($white, 60, 184, ($width - 120), ($height - 264))
    $graphics.FillRectangle($blue, 92, 220, 180, 44)
    $graphics.DrawString('Ready to shnapp', $small, $white, 108, 229)
    $graphics.DrawString('One shortcut, one useful image.', $body, $ink, 92, 302)
    $graphics.DrawString('Window   /   Full screen   /   A precise region', $small, $muted, 92, 352)
    $graphics.DrawString('Demo detail: this is not a secret.', $small, $ink, 92, 402)
    $graphics.DrawString('Non-sensitive verification fixture', $small, $muted, 60, ($height - 48))
    foreach ($resource in @($ink, $muted, $blue, $white, $heading, $body, $small)) {
        $resource.Dispose()
    }
})
$form.Add_Shown({
    $form.Activate()
    $information = [ordered]@{
        processId = $PID
        hwnd = $form.Handle.ToInt64()
        dpi = [ShnappFixtureNative]::GetDpiForWindow($form.Handle)
        x = $form.Bounds.X
        y = $form.Bounds.Y
        width = $form.Bounds.Width
        height = $form.Bounds.Height
        screenX = $screen.Bounds.X
        screenY = $screen.Bounds.Y
        screenWidth = $screen.Bounds.Width
        screenHeight = $screen.Bounds.Height
    }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
    [System.IO.File]::WriteAllText($OutputPath, ($information | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
})
[System.Windows.Forms.Application]::Run($form)
$form.Dispose()
