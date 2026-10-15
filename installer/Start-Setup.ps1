[CmdletBinding()]
param([switch]$PreviewOnly, [switch]$SmokeTest, [switch]$SmokeFlow,
    [ValidateSet('Express','Minimal','Custom')][string]$SmokeMode = 'Custom', [ValidateRange(0,5)][int]$SmokePage = 0,
    [string]$SmokeScreenshotPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$root = $PSScriptRoot
$installer = Join-Path $root 'Install-ClipDat.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    $installer = Join-Path $root '..\Clippr\packaging\Install-ClipDat.ps1'
}
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Install-ClipDat.ps1 must be beside Start-Setup.ps1.' }
$background = [Drawing.Color]::FromArgb(15, 23, 31)
$surface = [Drawing.Color]::FromArgb(29, 39, 50)
$foreground = [Drawing.Color]::FromArgb(242, 246, 250)
$muted = [Drawing.Color]::FromArgb(166, 183, 199)
$blue = [Drawing.Color]::FromArgb(20, 112, 226)

$form = New-Object Windows.Forms.Form
$form.Text = 'ClipDat Setup'
$form.Size = New-Object Drawing.Size(760, 610)
$form.MinimumSize = $form.Size
$form.MaximumSize = $form.Size
$form.StartPosition = 'CenterScreen'
$form.BackColor = $background
$form.ForeColor = $foreground
$form.Font = New-Object Drawing.Font('Segoe UI', 10)
$iconPath = Join-Path $root 'ClipprTray.ico'
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { $iconPath = Join-Path $root '..\Clippr\Assets\ClipprTray.ico' }
if (Test-Path -LiteralPath $iconPath) { $form.Icon = New-Object Drawing.Icon($iconPath) }

function New-Page {
    $panel = New-Object Windows.Forms.Panel
    $panel.Location = New-Object Drawing.Point(24, 58)
    $panel.Size = New-Object Drawing.Size(696, 460)
    $panel.BackColor = $background
    $form.Controls.Add($panel)
    return $panel
}
function New-Text($parent, [string]$value, [int]$x, [int]$y, [int]$width, [int]$height, [int]$size = 10, [bool]$secondary = $false) {
    $label = New-Object Windows.Forms.Label
    $label.Text = $value
    $label.Location = New-Object Drawing.Point($x, $y)
    $label.Size = New-Object Drawing.Size($width, $height)
    $label.ForeColor = if ($secondary) { $muted } else { $foreground }
    $label.Font = New-Object Drawing.Font('Segoe UI', $size, $(if ($size -ge 16) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }))
    $parent.Controls.Add($label)
    return $label
}
function New-Check($parent, [string]$caption, [int]$x, [int]$y, [bool]$checked, [int]$width = 310) {
    $check = New-Object Windows.Forms.CheckBox
    $check.Text = $caption
    $check.Checked = $checked
    $check.Location = New-Object Drawing.Point($x, $y)
    $check.Size = New-Object Drawing.Size($width, 28)
    $check.FlatStyle = 'Flat'
    $check.BackColor = $surface
    $check.ForeColor = $foreground
    $parent.Controls.Add($check)
    return $check
}
function New-Choice($parent, [string]$caption, [string[]]$options, [int]$x, [int]$y, [int]$selected = 0) {
    New-Text $parent $caption $x $y 300 23 | Out-Null
    $combo = New-Object Windows.Forms.ComboBox
    $combo.Location = New-Object Drawing.Point($x, ($y + 24))
    $combo.Size = New-Object Drawing.Size(285, 30)
    $combo.DropDownStyle = 'DropDownList'
    $combo.FlatStyle = 'Flat'
    $combo.DrawMode = 'OwnerDrawFixed'
    $combo.ItemHeight = 24
    $combo.BackColor = $surface
    $combo.ForeColor = $foreground
    $combo.Add_DrawItem({
        param($sender, $e)
        if ($e.Index -lt 0) { return }
        $fill = New-Object Drawing.SolidBrush($(if (($e.State -band [Windows.Forms.DrawItemState]::Selected) -ne 0) { $blue } else { $surface }))
        $ink = New-Object Drawing.SolidBrush($foreground)
        try {
            $e.Graphics.FillRectangle($fill, $e.Bounds)
            $e.Graphics.DrawString([string]$sender.Items[$e.Index], $sender.Font, $ink,
                [float]($e.Bounds.X + 6), [float]($e.Bounds.Y + 3))
        } finally { $fill.Dispose(); $ink.Dispose() }
    })
    [void]$combo.Items.AddRange($options)
    $combo.SelectedIndex = $selected
    $parent.Controls.Add($combo)
    return $combo
}

$heading = New-Text $form 'ClipDat Setup' 26 15 560 36 21
$status = New-Text $form '' 27 529 540 28 9 $true
$back = New-Object Windows.Forms.Button
$back.Text = 'Back'; $back.Location = New-Object Drawing.Point(554, 525); $back.Size = New-Object Drawing.Size(76, 32)
$next = New-Object Windows.Forms.Button
$next.Text = 'Next'; $next.Location = New-Object Drawing.Point(638, 525); $next.Size = New-Object Drawing.Size(76, 32)
$next.BackColor = $blue; $next.ForeColor = $foreground; $next.FlatStyle = 'Flat'
$back.BackColor = $surface; $back.ForeColor = $foreground; $back.FlatStyle = 'Flat'
$form.Controls.AddRange(@($back,$next))

$welcome = New-Page
New-Text $welcome 'Welcome to ClipDat' 8 10 650 36 22 | Out-Null
New-Text $welcome 'Choose how you would like to set up the screen recorder.' 8 52 650 30 10 $true | Out-Null
$modePanel = New-Object Windows.Forms.Panel
$modePanel.Location = New-Object Drawing.Point(8, 103); $modePanel.Size = New-Object Drawing.Size(670, 260); $modePanel.BackColor = $background
$welcome.Controls.Add($modePanel)
function New-ModeCard($parent, [string]$caption, [int]$y, [bool]$checked) {
    $radio = New-Object Windows.Forms.RadioButton
    $radio.Appearance = 'Button'
    $radio.Text = $caption
    $radio.TextAlign = 'MiddleLeft'
    $radio.Padding = New-Object Windows.Forms.Padding(17, 0, 0, 0)
    $radio.Location = New-Object Drawing.Point(0, $y)
    $radio.Size = New-Object Drawing.Size(670, 66)
    $radio.FlatStyle = 'Flat'
    $radio.FlatAppearance.BorderSize = 1
    $radio.FlatAppearance.BorderColor = [Drawing.Color]::FromArgb(58, 73, 88)
    $radio.FlatAppearance.CheckedBackColor = [Drawing.Color]::FromArgb(23, 55, 88)
    $radio.FlatAppearance.MouseOverBackColor = [Drawing.Color]::FromArgb(35, 60, 83)
    $radio.BackColor = $surface
    $radio.ForeColor = $foreground
    $radio.Font = New-Object Drawing.Font('Segoe UI', 10, [Drawing.FontStyle]::Bold)
    $radio.Checked = $checked
    $parent.Controls.Add($radio)
    return $radio
}
$express = New-ModeCard $modePanel "Express Install  |  Recommended`r`nMost useful features, ready to go." 0 $true
$minimal = New-ModeCard $modePanel "Minimal Install`r`nJust clipping and recording." 75 $false
$custom = New-ModeCard $modePanel "Custom Install`r`nChoose features and supported settings." 150 $false
New-Text $modePanel 'Optional Clip Library is offered before installation.' 1 230 610 24 9 $true | Out-Null
$modeBoxes = @($express,$minimal,$custom)
foreach ($box in $modeBoxes) {
    $box.Add_CheckedChanged({
        $this.BackColor = if ($this.Checked) { [Drawing.Color]::FromArgb(23, 55, 88) } else { $surface }
        $this.FlatAppearance.BorderColor = if ($this.Checked) { $blue } else { [Drawing.Color]::FromArgb(58, 73, 88) }
    })
}
$express.BackColor = [Drawing.Color]::FromArgb(23, 55, 88)
$express.FlatAppearance.BorderColor = $blue

$features = New-Page
New-Text $features 'Features' 8 5 650 35 19 | Out-Null
New-Text $features 'Core clipping and recording always stay installed.' 8 41 650 25 9 $true | Out-Null
$library = New-Check $features 'Install optional Clip Library companion' 15 84 $false 640
$voice = New-Check $features 'Enable voice commands (requires offline Windows speech)' 15 119 $false 640
$screenshots = New-Check $features 'Enable screenshots' 15 154 $true 640
$quickShare = New-Check $features 'Show Quick Share controls (AnonMP4 uploads require action)' 15 189 $true 640
$desktop = New-Check $features 'Create desktop shortcut' 15 224 $false 640
$startup = New-Check $features 'Start ClipDat when I sign in' 15 259 $false 640
New-Text $features 'The in-app Clip Library is part of ClipDat; the separate companion is optional.' 15 318 650 42 9 $true | Out-Null
$modeFeatures = New-Text $features '' 15 172 640 90 10 $true

$recording = New-Page
New-Text $recording 'Recording' 8 5 650 35 19 | Out-Null
$length = New-Choice $recording 'Replay clip length' @('10 seconds','20 seconds','30 seconds','60 seconds','120 seconds','900 seconds') 15 55 2
$quality = New-Choice $recording 'Quality preset' @('Standard','High') 352 55 0
$format = New-Choice $recording 'Replay video format' @('MP4','MKV') 15 139 0
$desktopAudio = New-Check $recording 'Capture desktop audio' 352 139 $true
$micAudio = New-Check $recording 'Capture microphone audio' 352 175 $true
New-Text $recording 'Process audio sources and track routing can be selected in ClipDat Audio settings after installation.' 15 250 650 50 9 $true | Out-Null

$storage = New-Page
New-Text $storage 'Storage' 8 5 650 35 19 | Out-Null
New-Text $storage 'Clips folder' 15 53 300 23 | Out-Null
$folder = New-Object Windows.Forms.TextBox
$folder.Location = New-Object Drawing.Point(15, 82); $folder.Size = New-Object Drawing.Size(565, 28)
$folder.Text = Join-Path ([Environment]::GetFolderPath('MyVideos')) 'ClipDat'
$folder.BackColor = $surface; $folder.ForeColor = $foreground
$storage.Controls.Add($folder)
$browse = New-Object Windows.Forms.Button
$browse.Text = 'Browse'; $browse.Location = New-Object Drawing.Point(587, 80); $browse.Size = New-Object Drawing.Size(75, 30)
$storage.Controls.Add($browse)
$browse.Add_Click({
    $picker = New-Object Windows.Forms.FolderBrowserDialog
    if ($picker.ShowDialog() -eq [Windows.Forms.DialogResult]::OK) { $folder.Text = $picker.SelectedPath }
    $picker.Dispose()
})
$organise = New-Check $storage 'Organise replay clips by game' 15 136 $true
$imageFormat = New-Choice $storage 'Screenshot format' @('PNG','JPEG') 15 200 0
$jpegQuality = New-Choice $storage 'JPEG quality' @('70','80','90','100') 352 200 2
$jpegQuality.Enabled = $false
$imageFormat.Add_SelectedIndexChanged({ $jpegQuality.Enabled = $screenshots.Checked -and $imageFormat.SelectedIndex -eq 1 })
New-Text $storage 'Screenshots save in a Screenshots subfolder. PNG has no quality setting.' 15 295 650 42 9 $true | Out-Null

$experience = New-Page
New-Text $experience 'Experience' 8 5 650 35 19 | Out-Null
$notifications = New-Choice $experience 'Notifications' @('Windows','ClipDat','None') 15 55 0
$hotkey = New-Choice $experience 'Global clipping hotkey' @('Ctrl+Shift+F10','Ctrl+Alt+F10','Ctrl+Shift+F9') 352 55 0
$sound = New-Check $experience 'Play a sound after saving a clip' 15 150 $true
$tray = New-Check $experience 'Minimise to tray when closing' 15 187 $true
New-Text $experience 'Shortcut choices can also be changed in ClipDat Settings.' 15 257 650 28 9 $true | Out-Null

$review = New-Page
New-Text $review 'Review your setup' 8 5 650 35 19 | Out-Null
$summary = New-Text $review '' 18 60 640 320 10

$complete = New-Page
New-Text $complete 'ClipDat installation complete' 8 90 650 55 22 | Out-Null
New-Text $complete 'You can now launch ClipDat from the Start Menu. Your clips folder and settings can be changed later.' 8 155 650 70 11 $true | Out-Null
$launchAfterInstall = New-Check $complete 'Launch ClipDat now' 8 236 $false 640

$pages = @($welcome,$features,$recording,$storage,$experience,$review,$complete)
$script:step = 0
function Get-Mode { if ($minimal.Checked) { 'Minimal' } elseif ($custom.Checked) { 'Custom' } else { 'Express' } }
function Get-YesNo([bool]$value) { if ($value) { 'Yes' } else { 'No' } }
function Show-Step {
    foreach ($page in $pages) { $page.Visible = $false }
    $pages[$script:step].Visible = $true
    $back.Enabled = $script:step -gt 0 -and $script:step -lt 6
    $next.Text = switch ($script:step) { 5 { 'Install' } 6 { 'Finish' } default { 'Next' } }
    if ($script:step -eq 1) {
        $isCustom = (Get-Mode) -eq 'Custom'
        foreach ($control in @($voice,$screenshots,$quickShare,$startup)) { $control.Visible = $isCustom }
        $desktop.Location = New-Object Drawing.Point(15, $(if ($isCustom) { 224 } else { 125 }))
        $modeFeatures.Visible = -not $isCustom
        $modeFeatures.Text = if ((Get-Mode) -eq 'Minimal') {
            'Minimal keeps core recording and desktop audio. Screenshots, game folders, voice, Quick Share controls, and notifications start off.'
        } else {
            'Express enables screenshots, game folders, desktop and microphone audio, and Windows notifications. Voice stays off.'
        }
    }
    if ($script:step -eq 3) {
        $imageFormat.Enabled = $screenshots.Checked
        $jpegQuality.Enabled = $screenshots.Checked -and $imageFormat.SelectedIndex -eq 1
    }
    if ($script:step -eq 5) {
        $mode = Get-Mode
        $package = Get-ChildItem -LiteralPath $root -Filter 'ClipDat_*_x64.msix' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $sizeNote = if ($package) { "Main package: about $([math]::Round($package.Length / 1MB, 1)) MB (runtime dependency separate)" } else { 'Package size: shown after building the release' }
        $summary.Text = "Mode: $mode`r`nOptional Clip Library: $(Get-YesNo $library.Checked)`r`nReplay: $($length.SelectedItem), $($quality.SelectedItem), $($format.SelectedItem)`r`nDesktop audio: $(Get-YesNo $desktopAudio.Checked)  |  Microphone: $(Get-YesNo $micAudio.Checked)`r`nClips: $($folder.Text)`r`nGame folders: $(Get-YesNo $organise.Checked)`r`nScreenshots: $(Get-YesNo $screenshots.Checked) ($($imageFormat.SelectedItem))`r`nNotifications: $($notifications.SelectedItem)  |  Save sound: $(Get-YesNo $sound.Checked)`r`nVoice commands: $(Get-YesNo $voice.Checked)`r`nWindows startup: $(Get-YesNo $startup.Checked)  |  Desktop shortcut: $(Get-YesNo $desktop.Checked)`r`nInstall location: managed by Windows`r`n$sizeNote"
        if ($mode -ne 'Custom') {
            $summary.Text = "Mode: $mode`r`nOptional Clip Library: $(Get-YesNo $library.Checked)`r`nMain app: clipping, recording and desktop audio`r`nClips: default Videos/ClipDat folder`r`nExisting settings: preserved on upgrades`r`nPublic sharing: not started automatically`r`nInstall location: managed by Windows`r`n$sizeNote"
        }
    }
}
$back.Add_Click({
    if ($script:step -eq 5 -and (Get-Mode) -ne 'Custom') { $script:step = 1 }
    elseif ($script:step -gt 0) { $script:step-- }
    Show-Step
})
$next.Add_Click({
    try {
        if ($script:step -eq 6) {
            if ($launchAfterInstall.Checked -and -not $PreviewOnly) {
                $package = Get-AppxPackage -Name 'ce842948-d72b-4d57-ae7b-474f481f442e' | Select-Object -First 1
                if ($package) { Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($package.PackageFamilyName)!App" }
            }
            $form.Close(); return
        }
        if ($script:step -eq 0) { $script:step = 1; Show-Step; return }
        if ($script:step -eq 1 -and (Get-Mode) -ne 'Custom') { $script:step = 5; Show-Step; return }
        if ($script:step -lt 5) { $script:step++; Show-Step; return }
        if ($PreviewOnly) { $status.Text = 'Preview only — no installation was performed.'; $script:step = 6; Show-Step; return }
        $next.Enabled = $false; $back.Enabled = $false
        $status.Text = 'Installing ClipDat…'; $form.Refresh()
        $mode = Get-Mode
        if ($library.Checked -and -not (Get-ChildItem -LiteralPath $root -Filter 'ClipDat-Library-v*.zip' -File | Select-Object -First 1)) {
            throw 'The optional Library archive is missing beside setup. Place it here or deselect Clip Library.'
        }
        $arguments = @('-Mode', $mode)
        $tempConfig = $null
        if ($mode -eq 'Custom') {
            if (-not [IO.Path]::IsPathRooted($folder.Text)) { throw 'Choose an absolute clips folder.' }
            $values = @{
                clipLengthSeconds = [int]($length.SelectedItem -replace '[^0-9]','')
                qualityPreset = [string]$quality.SelectedItem
                videoFormat = ([string]$format.SelectedItem).ToLowerInvariant()
                desktopAudioEnabled = $desktopAudio.Checked; microphoneAudioEnabled = $micAudio.Checked
                organizeClipsByGame = $organise.Checked; screenshotsEnabled = $screenshots.Checked
                quickShareEnabled = $quickShare.Checked; voiceCommandsEnabled = $voice.Checked
                startWithWindows = $startup.Checked; minimizeToTrayOnClose = $tray.Checked
                notificationStyle = [string]$notifications.SelectedItem; saveNotificationSound = $sound.Checked
                hotkey = [string]$hotkey.SelectedItem; clipFolder = $folder.Text
                screenshotFormat = if ($imageFormat.SelectedIndex -eq 0) { 'png' } else { 'jpg' }
                screenshotJpegQuality = [int]$jpegQuality.SelectedItem
            }
            $tempConfig = Join-Path $env:TEMP "ClipDat-Setup-$PID.json"
            [IO.File]::WriteAllText($tempConfig, ($values | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
            $arguments += @('-ConfigurationPath', $tempConfig)
        }
        if ($desktop.Checked) { $arguments += '-DesktopShortcut' }
        try { & $installer @arguments } finally { if ($tempConfig) { Remove-Item -LiteralPath $tempConfig -Force -ErrorAction SilentlyContinue } }
        if ($library.Checked) {
            $archive = Get-ChildItem -LiteralPath $root -Filter 'ClipDat-Library-v*.zip' -File | Select-Object -First 1
            if (-not $archive) { throw 'ClipDat installed, but the optional Library archive is missing beside setup.' }
            $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
            $tempLibrary = [IO.Path]::GetFullPath((Join-Path $tempRoot "ClipDat-Library-Setup-$PID"))
            if (-not $tempLibrary.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($tempLibrary) -ne "ClipDat-Library-Setup-$PID") { throw 'Unsafe temporary Library setup path.' }
            if (Test-Path -LiteralPath $tempLibrary) { throw 'Temporary Library folder already exists.' }
            try {
                Expand-Archive -LiteralPath $archive.FullName -DestinationPath $tempLibrary
                & (Join-Path $tempLibrary 'Install-Library.ps1')
            } finally { Remove-Item -LiteralPath $tempLibrary -Recurse -Force -ErrorAction SilentlyContinue }
        }
        $status.Text = 'Installation complete.'; $script:step = 6; Show-Step
    } catch {
        [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'ClipDat Setup', 'OK', 'Error') | Out-Null
        $status.Text = 'Installation was not completed.'
    } finally { $next.Enabled = $true; $back.Enabled = $script:step -gt 0 -and $script:step -lt 6 }
})
Show-Step
if ($SmokeTest) {
    $smokeTimer = New-Object Windows.Forms.Timer
    $smokeTimer.Interval = 700
    $smokeTimer.Add_Tick({
        $smokeTimer.Stop()
        if ($SmokeFlow) {
            switch ($SmokeMode) { Express { $express.Checked = $true } Minimal { $minimal.Checked = $true } Custom { $custom.Checked = $true } }
            $clicks = if ($SmokeMode -eq 'Custom') { 6 } else { 3 }
            for ($i = 0; $i -lt $clicks; $i++) { $next.PerformClick() }
            if ($script:step -ne 6) { throw 'Setup wizard preview flow did not reach completion.' }
            Write-Host 'Setup wizard preview flow passed.'
            $form.Close()
            return
        }
        if ($SmokePage -gt 0) { $custom.Checked = $true; $script:step = $SmokePage; Show-Step; $form.Refresh() }
        if ($SmokeScreenshotPath) {
            $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
            try {
                $form.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
                $bitmap.Save($SmokeScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $bitmap.Dispose() }
        }
        Write-Host 'Setup wizard opened and rendered.'
        $form.Close()
    })
    $smokeTimer.Start()
    $PreviewOnly = $true
}
[void]$form.ShowDialog()
if ($SmokeTest) { $smokeTimer.Dispose() }
$form.Dispose()
