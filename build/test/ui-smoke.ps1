<#
.SYNOPSIS
  Clicks through NEXORA like a user (UI Automation) and fails if the app crashes or logs errors.
  Read-only: it never applies optimizations or ends processes.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $OutDir = 'smoke'
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

function Start-Nexora {
    $p = Start-Process -FilePath $Exe -PassThru
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
    Step $app 'Optimizer details' 'Details' 2
    Step $app 'Startup apps tab' 'Startup apps' 3
    Step $app 'History tab' 'History' 3
    Step $app 'Optimizations tab' 'Optimizations' 3
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
