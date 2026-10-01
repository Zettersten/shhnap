param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$SafeBackdrop
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShnappMarketingNative
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
'@
[ShnappMarketingNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$backdrops = @()
if ($SafeBackdrop) {
    # Cover every monitor while Shnapp hides for its virtual-desktop capture picker.
    foreach ($display in [System.Windows.Forms.Screen]::AllScreens) {
        $backdrop = New-Object System.Windows.Forms.Form
        $backdrop.Text = 'Fictional demo backdrop'
        $backdrop.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
        $backdrop.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
        $backdrop.Bounds = $display.Bounds
        $backdrop.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#DCE6F1')
        $backdrop.ShowInTaskbar = $false
        $backdrop.Show()
        $backdrops += $backdrop
    }
}
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Launch checklist - sample workspace'
$form.StartPosition = 'Manual'
$form.Size = New-Object System.Drawing.Size(1240, 760)
$form.Location = New-Object System.Drawing.Point(
    ($screen.WorkingArea.X + [int](($screen.WorkingArea.Width - $form.Width) / 2)),
    ($screen.WorkingArea.Y + [int](($screen.WorkingArea.Height - $form.Height) / 2)))
$form.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#F5F7FB')

$form.Add_Paint({
    param($sender, $eventArgs)
    $g = $eventArgs.Graphics
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $ink = [System.Drawing.ColorTranslator]::FromHtml('#1B2430')
    $muted = [System.Drawing.ColorTranslator]::FromHtml('#64748B')
    $blue = [System.Drawing.ColorTranslator]::FromHtml('#0A84FF')
    $border = [System.Drawing.ColorTranslator]::FromHtml('#E2E8F0')
    $white = [System.Drawing.Color]::White
    $green = [System.Drawing.ColorTranslator]::FromHtml('#16803C')
    $sidebar = [System.Drawing.ColorTranslator]::FromHtml('#17202B')
    $font = New-Object System.Drawing.Font('Segoe UI', 12)
    $small = New-Object System.Drawing.Font('Segoe UI', 10)
    $medium = New-Object System.Drawing.Font('Segoe UI Semibold', 12)
    $heading = New-Object System.Drawing.Font('Segoe UI Semibold', 25)
    $subheading = New-Object System.Drawing.Font('Segoe UI Semibold', 16)
    $brushes = @(
        (New-Object System.Drawing.SolidBrush($ink)),
        (New-Object System.Drawing.SolidBrush($muted)),
        (New-Object System.Drawing.SolidBrush($blue)),
        (New-Object System.Drawing.SolidBrush($white)),
        (New-Object System.Drawing.SolidBrush($green)),
        (New-Object System.Drawing.SolidBrush($sidebar)),
        (New-Object System.Drawing.SolidBrush($border))
    )
    $inkBrush, $mutedBrush, $blueBrush, $whiteBrush, $greenBrush, $sidebarBrush, $borderBrush = $brushes
    $borderPen = New-Object System.Drawing.Pen($border, 1)
    $checkPen = New-Object System.Drawing.Pen($white, 2.4)
    try {
        $g.FillRectangle($sidebarBrush, 0, 0, 216, $sender.ClientSize.Height)
        $g.FillRectangle($blueBrush, 28, 33, 30, 30)
        $g.DrawString('S', $medium, $whiteBrush, 36, 35)
        $g.DrawString('SAMPLE SPACE', $medium, $whiteBrush, 68, 36)
        $g.DrawString('Overview', $font, $mutedBrush, 28, 111)
        $g.FillRectangle($blueBrush, 0, 155, 4, 44)
        $g.DrawString('Projects', $font, $whiteBrush, 28, 165)
        $g.DrawString('Activity', $font, $mutedBrush, 28, 220)
        $g.DrawString('Files', $font, $mutedBrush, 28, 274)
        $g.DrawString('Fictional demo content', $small, $mutedBrush, 28, $sender.ClientSize.Height - 50)

        $g.DrawString('Project / Release', $small, $mutedBrush, 258, 33)
        $g.DrawString('Launch checklist', $heading, $inkBrush, 256, 67)
        $g.DrawString('Track the final steps, then share a clear update.', $font, $mutedBrush, 258, 118)

        $g.FillRectangle($whiteBrush, 255, 177, 582, 455)
        $g.DrawRectangle($borderPen, 255, 177, 582, 455)
        $g.DrawString('Release readiness', $subheading, $inkBrush, 280, 202)
        $g.DrawString('3 of 4 steps complete', $small, $mutedBrush, 280, 239)
        $g.FillRectangle($borderBrush, 280, 274, 532, 8)
        $g.FillRectangle($blueBrush, 280, 274, 399, 8)

        $rows = @(
            @{ y = 316; name = 'Confirm release scope'; note = 'Ready for review'; done = $true },
            @{ y = 388; name = 'Capture the final screens'; note = 'Images for the launch page'; done = $true },
            @{ y = 460; name = 'Review privacy copy'; note = 'Check wording before publishing'; done = $true },
            @{ y = 532; name = 'Publish the Windows build'; note = 'Prepare x64 and ARM64 packages'; done = $false }
        )
        foreach ($row in $rows) {
            $g.DrawLine($borderPen, 280, ($row.y - 15), 812, ($row.y - 15))
            $circleBrush = if ($row.done) { $greenBrush } else { $whiteBrush }
            $g.FillEllipse($circleBrush, 282, $row.y, 24, 24)
            $g.DrawEllipse($borderPen, 282, $row.y, 24, 24)
            if ($row.done) {
                $g.DrawLine($checkPen, 288, ($row.y + 12), 292, ($row.y + 16))
                $g.DrawLine($checkPen, 292, ($row.y + 16), 300, ($row.y + 8))
            }
            $g.DrawString($row.name, $medium, $inkBrush, 322, ($row.y - 2))
            $g.DrawString($row.note, $small, $mutedBrush, 322, ($row.y + 26))
        }

        $g.FillRectangle($whiteBrush, 858, 177, 324, 292)
        $g.DrawRectangle($borderPen, 858, 177, 324, 292)
        $g.DrawString('Share details', $subheading, $inkBrush, 882, 202)
        $g.DrawString('Status', $small, $mutedBrush, 882, 253)
        $g.DrawString('In review', $font, $inkBrush, 882, 276)
        $g.DrawString('Demo access code', $small, $mutedBrush, 882, 330)
        $g.DrawString('DEMO-4242', $medium, $inkBrush, 882, 355)
        $g.DrawString('Use Shnapp Cover before sharing.', $small, $mutedBrush, 882, 413)

        $g.DrawString('This workspace is fictional and contains no private data.', $small, $mutedBrush, 258, 659)
    }
    finally {
        $borderPen.Dispose()
        $checkPen.Dispose()
        foreach ($brush in $brushes) { $brush.Dispose() }
        foreach ($face in @($font, $small, $medium, $heading, $subheading)) { $face.Dispose() }
    }
})

$form.Add_Shown({
    $form.Activate()
    $information = [ordered]@{
        processId = $PID
        hwnd = $form.Handle.ToInt64()
        x = $form.Bounds.X
        y = $form.Bounds.Y
        width = $form.Bounds.Width
        height = $form.Bounds.Height
        screenWidth = $screen.Bounds.Width
        screenHeight = $screen.Bounds.Height
        safeBackdrop = [bool]$SafeBackdrop
    }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
    [System.IO.File]::WriteAllText($OutputPath, ($information | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
})

[System.Windows.Forms.Application]::Run($form)
$form.Dispose()
foreach ($backdrop in $backdrops) { $backdrop.Dispose() }
