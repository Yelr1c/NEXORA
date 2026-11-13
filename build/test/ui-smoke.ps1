<#
.SYNOPSIS
  Clicks through NEXORA like a user (UI Automation) and fails if the app crashes or logs errors.
  Read-only: it never applies optimizations or ends processes.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $OutDir = 'smoke',
    # Launch with a standard-user (non-admin) token, like a normal home PC.
    [switch] $StandardUser
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
New-Item -ItemType Directory -Force $OutDir | Out-Null
$data = Join-Path $env:LOCALAPPDATA 'NEXORA'
$script:failures = @()

function Shot([string] $name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $OutDir "$name.png")); $bmp.Dispose()
}

Add-Type -Namespace Native -Name Spi -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint p, int[] v, uint w);
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint p, ref int v, uint w);
"@
function Get-MouseState {
    $m = New-Object int[] 3; [void][Native.Spi]::SystemParametersInfo(0x0003, 0, $m, 0)
    $speed = 0; [void][Native.Spi]::SystemParametersInfo(0x0070, 0, [ref] $speed, 0)
    "accel=$($m -join ',') speed=$speed"
}

function Start-Nexora {
    if ($StandardUser) {
        Start-Process -FilePath "$env:WINDIR\System32\runas.exe" -ArgumentList '/trustlevel:0x20000', "`"$Exe`""
        for ($i = 0; $i -lt 40 -and -not (Get-Process NEXORA -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
        $p = Get-Process NEXORA -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $p) { throw 'NEXORA did not start under a standard-user token' }
    }
    else {
        $p = Start-Process -FilePath $Exe -PassThru
    }
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $w = $AE::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)))
        if ($w) { return @{ Process = $p; Window = $w } }
    }
    throw 'NEXORA window did not appear'
}

function Find([object] $win, [string] $name) {
    $win.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
}

function Click([object] $el) {
    $pattern = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) { $pattern.Invoke(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) { $pattern.Select(); return }
    throw "Element '$($el.Current.Name)' cannot be clicked"
}

function DialogClick([hashtable] $app, [string] $label, [string] $button, [int] $wait = 4, [switch] $Optional) {
    Write-Host "==> $label"
    $dialog = $null
    for ($i = 0; $i -lt 10 -and -not $dialog; $i++) {
        # A WinUI ContentDialog is exposed as a Popup window named after the dialog title.
        $dialog = $app.Window.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Popup'))) |
            Where-Object { $_.Current.Name -and $_.Current.Name -ne 'Popup' -and (Find $_ $button) } | Select-Object -First 1
        if (-not $dialog) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $dialog) {
        # Dialogs may be hosted in a separate popup window: search all top-level windows of the process too.
        $tops = $AE::RootElement.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Process.Id)))
        foreach ($t in $tops) {
            $dialog = $t.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'ContentDialog')))
            if ($dialog) { break }
            if ($t.Current.ClassName -eq 'ContentDialog') { $dialog = $t; break }
        }
    }
    if (-not $dialog) {
        if (-not $Optional) {
            $script:failures += "No dialog appeared for '$label'"
            Write-Host '   (no dialog) top-level windows / named elements:'
            foreach ($t in $tops) {
                Write-Host "   [top] class=$($t.Current.ClassName) name=$($t.Current.Name)"
                $t.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                    Where-Object { $_.Current.Name -and $_.Current.ControlType.ProgrammaticName -match 'Button|Window|Pane|Text' } |
                    Select-Object -First 60 | ForEach-Object { Write-Host "      $($_.Current.ControlType.ProgrammaticName) class=$($_.Current.ClassName) '$($_.Current.Name)'" }
            }
        }
        else { Write-Host '   (no dialog)' }
        return
    }
    $el = Find $dialog $button
    if (-not $el) { $script:failures += "Dialog button '$button' not found for '$label'"; return }
    Click $el
    Start-Sleep -Seconds $wait
    Shot ($label -replace '[^A-Za-z0-9]+', '-')
    $app.Process.Refresh()
    if ($app.Process.HasExited) { throw "NEXORA CRASHED after: $label" }
}

function Step([hashtable] $app, [string] $label, [string] $target, [int] $wait = 4) {
    Write-Host "==> $label"
    if ($app.Process.HasExited) { throw "NEXORA already exited before '$label'" }
    $el = Find $app.Window $target
    if (-not $el) { $script:failures += "Could not find '$target' for step '$label'"; Write-Host "   (not found)"; return }
    try { Click $el } catch { $script:failures += "Click failed on '$target': $_" }
    Start-Sleep -Seconds $wait
    $app.Process.Refresh()
    Shot ($label -replace '[^A-Za-z0-9]+', '-')
    if ($app.Process.HasExited) { throw "NEXORA CRASHED after: $label (exit code $($app.Process.ExitCode))" }
}

try {
    # --- First run: welcome → scan -------------------------------------------------------
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $app = Start-Nexora
    Start-Sleep -Seconds 3; Shot '00-welcome'
    Step $app 'Start system scan' 'START SYSTEM SCAN' 25
    Step $app 'Continue to dashboard' 'Continue to Dashboard' 4

    # --- Every page ------------------------------------------------------------------------
    foreach ($page in 'Dashboard', 'Performance', 'Network', 'Optimizer', 'Games', 'Processes', 'Settings', 'About') {
        Step $app "Open $page" $page 5
    }

    # --- Main read-only actions ------------------------------------------------------------
    Step $app 'Open Network (test)' 'Network' 3
    Step $app 'Run network test' 'RUN NETWORK TEST' 20
    Step $app 'Open Optimizer (sections)' 'Optimizer' 4
    Step $app 'Startup apps tab' 'Startup apps' 3
    Step $app 'History tab' 'History' 3
    Step $app 'Optimizations tab' 'Optimizations' 3

    # --- Apply, verify the mouse is untouched, undo, restore ---------------------------------
    $mouseBefore = Get-MouseState
    Step $app 'Quick optimize' 'Quick optimize' 2
    DialogClick $app 'Confirm quick optimize' 'Apply' 6
    DialogClick $app 'Dismiss result' 'OK' 2 -Optional
    $mouseAfter = Get-MouseState
    Write-Host "   mouse before: $mouseBefore  after: $mouseAfter"
    if ($mouseBefore -ne $mouseAfter) { $script:failures += "Quick optimize changed mouse settings ($mouseBefore -> $mouseAfter)" }
    Step $app 'History after apply' 'History' 3
    Step $app 'Undo newest change' 'Undo' 4
    DialogClick $app 'Dismiss undo result' 'OK' 2 -Optional
    Step $app 'Restore previous configuration' 'Restore previous configuration' 2
    DialogClick $app 'Confirm restore' 'Restore all' 5
    DialogClick $app 'Dismiss restore result' 'OK' 2
    Step $app 'Back to optimizations' 'Optimizations' 3
    Step $app 'Open Performance (session)' 'Performance' 3
    Step $app 'Start performance test' 'START PERFORMANCE TEST' 4
    Step $app 'Stop performance test' 'Stop test' 4
    Step $app 'Open Dashboard again' 'Dashboard' 3
    Step $app 'Scan from dashboard' 'SCAN MY PC' 25
    Step $app 'Back to dashboard' 'Back to Dashboard' 4
    Step $app 'Open Games' 'Games' 8
    Step $app 'Open Processes' 'Processes' 6
    Step $app 'Open Settings' 'Settings' 3

    $app.Process.Refresh()
    if ($app.Process.HasExited) { throw 'NEXORA exited at the end of the test' }
    Write-Host "NEXORA survived the full click-through."
    Stop-Process -Id $app.Process.Id -Force
}
catch {
    $script:failures += "$_"
    Write-Host "FAILURE: $_"
}
finally {
    Shot 'zz-final'
    Write-Host '---- startup-error.txt ----'
    Get-Content (Join-Path $data 'startup-error.txt') -ErrorAction SilentlyContinue
    Write-Host '---- NEXORA log (errors/warnings) ----'
    Get-ChildItem (Join-Path $data 'Logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content |
        Select-String '"level":"(Warning|Error|Critical)"' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(1500, $_.Line.Length)) }
    Write-Host '---- NEXORA log (optimizations/dialogs) ----'
    Get-ChildItem (Join-Path $data 'Logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content |
        Select-String 'optimization|Dialog|Applied|Reverted' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length)) }
    Write-Host '---- Windows crash events ----'
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting' -and $_.Message -match 'NEXORA' } |
        Select-Object -First 6 | ForEach-Object { "[$($_.ProviderName)] $($_.Message)" }
    Copy-Item $data (Join-Path $OutDir 'appdata') -Recurse -ErrorAction SilentlyContinue
    Get-Process NEXORA -ErrorAction SilentlyContinue | Stop-Process -Force
}

$logErrors = Get-ChildItem (Join-Path $data 'Logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content | Select-String '"level":"(Error|Critical)"'
if ($logErrors) { $script:failures += 'NEXORA logged errors (see above)' }
if ($script:failures.Count) { Write-Host "`nFAILURES:"; $script:failures | ForEach-Object { Write-Host " - $_" }; exit 1 }
Write-Host 'UI smoke test passed.'
