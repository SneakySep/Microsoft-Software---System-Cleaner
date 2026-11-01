# Shared UI Automation helpers for the verification scripts.
#
# Dot-source this from a script that has called Start-App, which defines the $window, $root and
# $pidCond the helpers below rely on. PowerShell's dynamic scoping means a function called from
# the script body resolves those from the script's own scope, so they do not have to be passed
# around; that coupling is the price of not threading four parameters through every helper.

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    // A real click at a real position: used to answer a dialog whose buttons UI Automation here
    // exposes as plain panes with no pattern that would activate them.
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

$script:failures = @()
$script:ellipsis = [char]0x2026
$script:scheduleLinkName = "Scheduled cleaning$([char]0x2026)"

# The condition is typed loosely on purpose: when a control is missing the expression that looked
# it up yields $null or an empty string, and that has to be reported as a failed assertion rather
# than aborting the whole run with a type conversion error.
function Assert-That([string]$description, $condition, [string]$detail) {
    if ([bool]$condition) {
        Write-Output "PASS  $description"
    }
    else {
        Write-Output "FAIL  $description  ($detail)"
        $script:failures += $description
    }
}

# Starts the app and waits for its window, rather than sleeping a fixed amount: a cold start can
# take several seconds and a fixed sleep makes a run flaky rather than wrong.
function Start-App([string]$exePath, [int]$timeoutSeconds = 40) {
    # stderr is captured so that if the app dies during a run there is something to go on besides
    # a window that has stopped answering.
    $errorLog = Join-Path $env:TEMP ("wc-app-" + [Guid]::NewGuid().ToString("N") + ".log")
    $process = Start-Process -FilePath $exePath -PassThru -RedirectStandardError $errorLog
    $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)

    $deadline = (Get-Date).AddSeconds($timeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $found = $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)

        if ($null -ne $found) {
            [void][Win32]::SetForegroundWindow($found.Current.NativeWindowHandle)

            # A moment for the first binding pass to settle.
            Start-Sleep -Seconds 2

            return [pscustomobject]@{
                Process   = $process
                Window    = $found
                Root      = $rootElement
                Condition = $condition
                ErrorLog  = $errorLog
            }
        }

        Start-Sleep -Milliseconds 500
    }

    throw "The app window did not appear within $timeoutSeconds seconds."
}

function Get-Texts($scope) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)

    $out = @()
    foreach ($element in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $out += $element.Current.Name
    }

    return $out
}

# Every descendant's name, whatever its control type. A Win32 dialog does not expose its message
# as a Text control the way WPF does, so assertions about dialog copy look at names.
function Get-Names($scope) {
    $out = @()
    foreach ($element in $scope.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($element.Current.Name) { $out += $element.Current.Name }
    }

    return $out
}

# Control types and names, for when a dialog does not look the way a script expects.
function Get-DescendantSummary($scope) {
    $out = @()
    foreach ($element in $scope.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
        $type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $out += "$type=$($element.Current.Name)"
    }

    return $out
}

function Find-In($scope, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)

    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

# Searches descendants rather than the desktop's direct children: a modal dialog is owned by its
# parent window, and UI Automation nests owned windows under the owner rather than beside it, so
# looking only at the root's children misses every dialog the app shows.
function Find-Window([string]$name) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $both = New-Object System.Windows.Automation.AndCondition($pidCond, $nameCondition)

    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $both)
}

function Wait-ForWindow([string]$name, [int]$timeoutSeconds = 25) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $found = Find-Window $name
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Get-WindowNames {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $combined = New-Object System.Windows.Automation.AndCondition($pidCond, $condition)

    $names = @()
    foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $combined)) {
        $names += $element.Current.Name
    }

    return $names
}

# Stat cards render the value immediately before its caption in the visual tree.
function Get-StatValue([string]$caption) {
    $texts = Get-Texts $window

    for ($index = 1; $index -lt $texts.Count; $index++) {
        if ($texts[$index] -eq $caption) { return $texts[$index - 1] }
    }

    return $null
}

function Invoke-Element($element, [string]$name) {
    if ($null -eq $element) { throw "'$name' not found." }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Invoke-Button([string]$name) { Invoke-Element (Find-In $window $name) $name }

function Get-BinReadout {
    return ((Get-Texts $window) | Where-Object { $_ -like 'Recycle Bin*' } | Select-Object -First 1)
}

# Clicks an element at its real screen position. Used for the buttons of a Win32 dialog, which
# only support patterns that do not activate them.
function Click-Element($element, [string]$name) {
    if ($null -eq $element) { throw "'$name' not found." }

    $rect = $element.Current.BoundingRectangle
    Write-Output "STAT  $name rect = $([int]$rect.Left),$([int]$rect.Top) $([int]$rect.Width)x$([int]$rect.Height)"

    if ($rect.Width -lt 4 -or $rect.Height -lt 4 -or $rect.Width -gt 500) {
        throw "The '$name' button is not where a click would land: $rect"
    }

    [Win32]::ClickAt([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
}

# Reports anything the app wrote to stderr, and whether it is still alive. Called where a window
# that stops responding would otherwise be reported as a missing control.
function Assert-AppAlive($process, $errorLog, [string]$stage) {
    Assert-That "the app is still running ($stage)" (-not $process.HasExited) "it exited"

    if ($process.HasExited) {
        Write-Output "STAT  exit code at $stage = $($process.ExitCode)"
    }

    if (Test-Path $errorLog) {
        $errors = (Get-Content $errorLog -Raw -ErrorAction SilentlyContinue)
        if (-not [string]::IsNullOrWhiteSpace($errors)) {
            Write-Output "STAT  app stderr at $stage = $(($errors -replace '\r?\n', ' ') )"
        }
    }
}

function Complete-Run {
    Write-Output ""

    if ($script:failures.Count -eq 0) {
        Write-Output "SMOKE_TEST_RESULT=PASS"
    }
    else {
        Write-Output "SMOKE_TEST_RESULT=FAIL ($($script:failures.Count) failed)"
    }
}
