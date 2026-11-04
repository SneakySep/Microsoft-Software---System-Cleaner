# Drives the built app through UI Automation to confirm the bindings, commands, progress and
# selection wiring actually work in the real window (not just in unit tests).
#
# Deliberately never invokes "Clean Now": that would delete files from the real temp folder. The
# deletion path is covered by the unit tests against sandbox directories instead.
#
# It does drive the scheduled-cleaning dialog, which registers and then removes a real Task
# Scheduler job - that round trip is the only way to prove the wiring, and it ends where it began.
# It does not press "Run now" (see run-now-smoke-test.ps1 for that) and it never clicks the
# elevation button, because the consent prompt belongs to Windows' secure desktop.
#
# Seed some history first with a command-line clean if you want the history panel assertions to
# run; without any recorded run they report SKIP.
#
# Build the Release configuration first: the scripts point at bin/Release, and a Release build left
# over from before a source change makes this report the old behaviour rather than the new one.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ui-smoke-test.ps1 `
#                     -ExePath <path> [-CliPath <path>]

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$CliPath
)

. "$PSScriptRoot/UiAutomation.ps1"

$ScheduledTaskPath = '\WindowsCleaner\'
$ScheduledTaskName = 'ScheduledClean'

$proc = $null

try {
    $app = Start-App $ExePath

    $proc = $app.Process
    $window = $app.Window
    $root = $app.Root
    $pidCond = $app.Condition

    Assert-AppAlive $proc $app.ErrorLog 'after startup'

    Write-Output "WINDOW_TITLE=$($window.Current.Name)"
    Write-Output ""

    # ---------------------------- initial state ----------------------------
    Assert-That "header renders" ((Get-Texts $window) -contains 'Windows Cleaner') "title text missing"
    Assert-That "Scan button present" ($null -ne (Find-In $window 'Scan')) "not found"
    Assert-That "Clean Now button present" ($null -ne (Find-In $window 'Clean Now')) "not found"
    Assert-That "Select all button present" ($null -ne (Find-In $window 'Select all')) "not found"
    Assert-That "Select none button present" ($null -ne (Find-In $window 'Select none')) "not found"
    Assert-That "recycle bin toggle present" ($null -ne (Find-In $window 'Move to Recycle Bin (undoable)')) "not found"
    Assert-That "empty state shown before scanning" `
        ((Get-Texts $window) -contains 'Press Scan to look for disposable files in your temporary folders.') "empty state text missing"

    # Extra-folder management. Deliberately never clicks "+ Add folder": that opens a modal dialog
    # which would block the run.
    Assert-That "extra folders row present" ((Get-Texts $window) -contains 'Extra folders') "not found"
    Assert-That "add folder button present" ($null -ne (Find-In $window '+ Add folder')) "not found"
    Assert-That "empty-folder hint shown" `
        ((Get-Texts $window) -contains 'Only the standard Windows locations are scanned.') "hint missing"

    # ------------------------------- elevation ------------------------------
    # Exactly one of the two states must be showing, and the unelevated one has to offer the way
    # out rather than only describing the limitation.
    $elevationTexts = Get-Texts $window
    $showsStandardUser = [bool]($elevationTexts | Where-Object { $_ -like 'Standard user*' } | Select-Object -First 1)
    $showsElevated = [bool]($elevationTexts | Where-Object { $_ -like 'Running as administrator*' } | Select-Object -First 1)
    $restartButton = Find-In $window 'Restart as administrator'

    Write-Output "STAT  standard user notice = $showsStandardUser"
    Write-Output "STAT  elevated notice      = $showsElevated"

    Assert-That "the elevation state is stated, and says which one" ($showsStandardUser -xor $showsElevated) `
        "standard=$showsStandardUser elevated=$showsElevated"

    if ($showsStandardUser) {
        # Never clicked here: it raises Windows' own consent prompt, which cannot be answered by a
        # script and should not be answered on someone's behalf.
        Assert-That "restarting elevated is offered" ($null -ne $restartButton) "no restart button"
        Assert-That "the restart explains what it is for" `
            ($null -ne $restartButton -and $restartButton.Current.HelpText -match 'Windows Temp') `
            "tooltip was '$($restartButton.Current.HelpText)'"
    }
    else {
        Assert-That "no restart button once elevated" ($null -eq $restartButton) "it was still offered"
    }

    # --------------------------- recycle bin state --------------------------
    $binText = Get-BinReadout
    Write-Output "STAT  recycle bin readout  = $binText"
    Assert-That "recycle bin status shown" ([bool]$binText) "no recycle bin text found"
    Assert-That "open recycle bin button present" ($null -ne (Find-In $window 'Open')) "not found"

    $emptyButton = Find-In $window 'Empty'
    Assert-That "empty recycle bin button present" ($null -ne $emptyButton) "not found"

    if ($null -ne $emptyButton) {
        $help = $emptyButton.Current.HelpText
        $enabled = $emptyButton.Current.IsEnabled
        Write-Output "STAT  empty tooltip         = $help"
        Write-Output "STAT  empty enabled         = $enabled"

        if ($help -match 'threshold') {
            # Below the configured threshold: emptying is not offered, and the tooltip says why.
            Assert-That "empty is disabled below the threshold" (-not $enabled) "it was enabled"
            Assert-That "the tooltip names the threshold" ($help -match 'below the') "tooltip did not explain: $help"
        }
        else {
            Assert-That "empty is enabled above the threshold" $enabled "it was disabled"
            Assert-That "destruction is described before it happens" `
                ($help -match 'cannot be undone') "tooltip did not warn: $help"

            # Only exercised when the bin genuinely qualifies, so nothing irreversible is ever one
            # click away: the confirmation is answered No, and the bin must be untouched.
            Invoke-Element $emptyButton 'Empty'
            $confirm = Wait-ForWindow 'Empty the Recycle Bin'
            Assert-That "emptying asks before destroying anything" ($null -ne $confirm) "no confirmation appeared"

            if ($null -ne $confirm) {
                $confirmNames = Get-Names $confirm
                Write-Output "STAT  confirm controls       = $((Get-DescendantSummary $confirm) -join ' | ')"
                Assert-That "the prompt states what would be destroyed" `
                    ([bool](($confirmNames | Where-Object { $_ -match '\d+ items,' }) | Select-Object -First 1)) `
                    "no figures in the prompt"
                Assert-That "the prompt warns it cannot be undone" `
                    ([bool](($confirmNames | Where-Object { $_ -match 'cannot be recovered' }) | Select-Object -First 1)) `
                    "no warning in the prompt"
                Assert-That "the prompt offers a way to decline" ($null -ne (Find-In $confirm 'No')) `
                    "no No button among: $((Get-DescendantSummary $confirm) -join ' | ')"

                [void][Win32]::SetForegroundWindow($confirm.Current.NativeWindowHandle)
                Start-Sleep -Milliseconds 500
                Click-Element (Find-In $confirm 'No') 'No'
                Start-Sleep -Seconds 3

                $afterDecline = Get-BinReadout
                Write-Output "STAT  bin after declining   = $afterDecline"
                Assert-That "declining leaves the Recycle Bin untouched" ($afterDecline -eq $binText) `
                    "was '$binText', now '$afterDecline'"
                Assert-That "declining is acknowledged" `
                    ((Get-Texts $window) -contains 'Left the Recycle Bin untouched.') "no status message"
            }
        }
    }

    # --------------------------- last clean summary -------------------------
    $texts = Get-Texts $window

    if ($texts -contains 'LAST CLEAN') {
        $headline = $texts | Where-Object { $_ -match '^(Freed|Moved) ' } | Select-Object -First 1
        Write-Output "STAT  last clean headline   = $headline"
        Assert-That "last clean headline shown" ([bool]$headline) "no headline text found"

        # The before/after breakdown is collapsed until asked for.
        $detailBefore = ($texts | Where-Object { $_ -like 'Before:*' } | Select-Object -First 1)
        Assert-That "before/after hidden until Details is clicked" (-not $detailBefore) "was already visible"

        Invoke-Button 'Details'
        Start-Sleep -Milliseconds 800

        $afterTexts = Get-Texts $window
        $detail = $afterTexts | Where-Object { $_ -like 'Before:*' } | Select-Object -First 1
        Write-Output "STAT  last clean details    = $(($detail -split "`n") -join ' | ')"
        Assert-That "Details reveals the before/after breakdown" ([bool]$detail) "no detail text found"
        Assert-That "the breakdown states the bin reading either side" `
            ([bool](($afterTexts | Where-Object { $_ -like '*Recycle Bin:*' }) | Select-Object -First 1)) "no bin line found"

        # ------------------------------- history -------------------------------
        $historyButton = Find-In $window 'History'
        Assert-That "history is offered once there are runs" `
            ($null -ne $historyButton -and $historyButton.Current.IsEnabled) "it was not available"

        Invoke-Button 'History'
        Start-Sleep -Milliseconds 800

        $historyTexts = Get-Texts $window
        Write-Output "STAT  history origins       = $((($historyTexts | Where-Object { $_ -in @('app', 'command line', 'scheduled') })) -join ', ')"

        # A window with nothing in it usually means the process is gone, which is worth saying
        # plainly rather than reporting as a missing control.
        if (-not ($historyTexts -contains 'WHEN')) {
            Assert-AppAlive $proc $app.ErrorLog 'opening the history'
            Write-Output "STAT  window text dump      = $(($historyTexts -join ' | '))"
        }

        Assert-That "history rows are labelled" `
            (($historyTexts -contains 'WHEN') -and ($historyTexts -contains 'RUN BY') -and ($historyTexts -contains 'RESULT')) `
            "column headers missing"
        Assert-That "history shows at least one run" `
            ([bool]($historyTexts | Where-Object { $_ -in @('app', 'command line', 'scheduled') } | Select-Object -First 1)) `
            "no origin labels found"
        Assert-That "history totals the recorded runs" `
            ([bool]($historyTexts | Where-Object { $_ -like '*runs recorded*' } | Select-Object -First 1)) `
            "no totals line found"
    }
    else {
        Write-Output "SKIP  no recorded clean to display"
    }

    Assert-AppAlive $proc $app.ErrorLog 'before scanning'

    $cleanButton = Find-In $window 'Clean Now'
    Assert-That "Clean Now disabled before a scan" (-not $cleanButton.Current.IsEnabled) "it was enabled"

    # -------------------------------- scan --------------------------------
    Invoke-Button 'Scan'
    Start-Sleep -Seconds 20

    $found = Get-StatValue 'Files selected'
    $size = Get-StatValue 'Selected to clean'
    $locations = Get-StatValue 'Locations reachable'

    Write-Output "STAT  files selected     = $found"
    Write-Output "STAT  selected to clean  = $size"
    Write-Output "STAT  locations          = $locations"
    Write-Output ""

    $foundCount = 0
    [void][int]::TryParse(($found -replace ',', ''), [ref]$foundCount)

    Assert-That "scan found files" ($foundCount -gt 0) "files selected was '$found'"
    Assert-That "selected size is populated" ($size -ne '-' -and $size -ne $null) "was '$size'"
    Assert-That "locations reported" ($locations -match '/') "was '$locations'"
    Assert-That "Clean Now enabled after a scan" ((Find-In $window 'Clean Now').Current.IsEnabled) "still disabled"

    # --------------------------- deselect one row ---------------------------
    $cbCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::CheckBox)
    $boxes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cbCond)

    $rowCheckbox = $null
    foreach ($box in $boxes) {
        if ([string]::IsNullOrEmpty($box.Current.Name)) { $rowCheckbox = $box; break }
    }

    Assert-That "row checkboxes rendered" ($null -ne $rowCheckbox) "no unnamed checkbox found"

    if ($null -ne $rowCheckbox) {
        $rowCheckbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 800

        $afterToggle = Get-StatValue 'Files selected'
        $afterToggleCount = -1
        [void][int]::TryParse(($afterToggle -replace ',', ''), [ref]$afterToggleCount)

        Write-Output "STAT  after unchecking one = $afterToggle"
        Assert-That "unticking a row lowers the selected count" `
            ($afterToggleCount -eq $foundCount - 1) "was $foundCount, now $afterToggle"
    }

    # ----------------------------- select none -----------------------------
    Invoke-Button 'Select none'
    Start-Sleep -Milliseconds 800

    Assert-That "Select none clears the selection" ((Get-StatValue 'Files selected') -eq '0') `
        "was '$((Get-StatValue 'Files selected'))'"
    Assert-That "Clean Now disabled with nothing selected" `
        (-not (Find-In $window 'Clean Now').Current.IsEnabled) "it was still enabled"
    Assert-That "size reads zero with nothing selected" ((Get-StatValue 'Selected to clean') -eq '0 B') `
        "was '$((Get-StatValue 'Selected to clean'))'"

    # ------------------------------ select all -----------------------------
    Invoke-Button 'Select all'
    Start-Sleep -Milliseconds 800

    $reselected = Get-StatValue 'Files selected'
    $reselectedCount = -1
    [void][int]::TryParse(($reselected -replace ',', ''), [ref]$reselectedCount)

    Assert-That "Select all restores the full selection" ($reselectedCount -eq $foundCount) `
        "expected $foundCount, got $reselected"
    Assert-That "Clean Now re-enabled after Select all" ((Find-In $window 'Clean Now').Current.IsEnabled) "still disabled"

    Assert-AppAlive $proc $app.ErrorLog 'after the history panel'

    # ========================= scheduled cleaning dialog =========================
    Assert-That "scheduled cleaning link present" ($null -ne (Find-In $window $scheduleLinkName)) "not found"

    Invoke-Button $scheduleLinkName
    $dialog = Wait-ForWindow 'Scheduled cleaning'
    Assert-That "scheduler dialog opened" ($null -ne $dialog) `
        "windows of this process: $((Get-WindowNames) -join ' | ')"

    # Whether a job already exists decides what "Run now" should look like, so it is read from the
    # scheduler rather than assumed: on a machine that already had one the state below would
    # otherwise be reported as a failure when it is in fact correct. The cmdlets rather than
    # schtasks.exe because this script runs with $ErrorActionPreference = 'Stop' (UiAutomation.ps1),
    # and under that, schtasks' "not found" stderr merged back through 2>&1 becomes a terminating
    # error - aborting the run on exactly the machine state this probe exists to detect.
    $hadJob = $null -ne (Get-ScheduledTask -TaskName $ScheduledTaskName -TaskPath $ScheduledTaskPath `
        -ErrorAction SilentlyContinue)
    Write-Output "STAT  job registered before    = $hadJob"

    if ($null -ne $dialog) {
        $dialogTexts = Get-Texts $dialog
        Write-Output "STAT  dialog first texts     = $(($dialogTexts | Select-Object -First 3) -join ' | ')"

        Assert-That "the dialog reports the current state" `
            ([bool](($dialogTexts | Where-Object { $_ -like 'Scheduled cleaning is *' }) | Select-Object -First 1)) `
            "no state headline found"
        Assert-That "the dialog names the executable the job will run" `
            ([bool](($dialogTexts | Where-Object { $_ -like 'The job runs*' }) | Select-Object -First 1)) `
            "the CLI was not resolved; found: $($dialogTexts -join ' | ')"
        Assert-That "the freshness guard is stated in the dialog" `
            ([bool](($dialogTexts | Where-Object { $_ -like '*24 hours*' }) | Select-Object -First 1)) "no guard text found"
        Assert-That "the dialog reports when the job last ran" `
            ([bool](($dialogTexts | Where-Object { $_ -like 'It has not run yet*' -or $_ -like 'It last ran*' }) | Select-Object -First 1)) `
            "no last-run line found"
        Assert-That "daily is selected by default" `
            ((Find-In $dialog 'Every day').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') `
            "Every day was not on"
        Assert-That "the weekday picker is hidden for a daily schedule" `
            ($null -eq (Find-In $dialog 'MON')) "the weekday row was visible"

        # Running the job needs something registered, and the button says so.
        $runNow = Find-In $dialog 'Run now'
        Assert-That "run now is offered" ($null -ne $runNow) "not found"

        if ($null -ne $runNow -and -not $hadJob) {
            Assert-That "run now is unavailable with nothing registered" (-not $runNow.Current.IsEnabled) "it was enabled"
        }

        Invoke-Element (Find-In $dialog 'Turn on / update') 'Turn on / update'
        Start-Sleep -Seconds 4

        $dialogTexts = Get-Texts $dialog
        Write-Output "STAT  dialog after turn on   = $(($dialogTexts | Select-Object -First 4) -join ' | ')"

        Assert-That "turning it on reports it as on" `
            ([bool](($dialogTexts | Where-Object { $_ -eq 'Scheduled cleaning is on' }) | Select-Object -First 1)) `
            "headline did not change"
        Assert-That "the registered command is shown back" `
            ([bool](($dialogTexts | Where-Object { $_ -like '*wclean.exe*clean --yes*' }) | Select-Object -First 1)) `
            "no registered command line found"
        Assert-That "the next run is worked out from the definition" `
            ([bool](($dialogTexts | Where-Object { $_ -like 'Next run *' }) | Select-Object -First 1)) `
            "no next-run line; found: $($dialogTexts -join ' | ')"
        Assert-That "run now becomes available once there is a job" `
            ((Find-In $dialog 'Run now').Current.IsEnabled) "it was still disabled"

        if ($CliPath) {
            $status = (& $CliPath schedule --status 2>&1 | Out-String).Trim() -replace '\s+', ' '
            Write-Output "STAT  cli schedule status    = $status"
            Assert-That "Task Scheduler agrees the job exists" `
                ($status -notmatch 'No scheduled job') "the CLI could not find the registered job"
        }

        Invoke-Element (Find-In $dialog 'Turn off') 'Turn off'
        Start-Sleep -Seconds 4

        $dialogTexts = Get-Texts $dialog
        Assert-That "turning it off reports it as off" `
            ([bool](($dialogTexts | Where-Object { $_ -eq 'Scheduled cleaning is off' }) | Select-Object -First 1)) `
            "headline did not change"
        Assert-That "the next-run line goes with it" `
            (-not [bool](($dialogTexts | Where-Object { $_ -like 'Next run *' }) | Select-Object -First 1)) `
            "it was still shown"
        Assert-That "run now is unavailable again" (-not (Find-In $dialog 'Run now').Current.IsEnabled) "it was enabled"

        if ($CliPath) {
            $status = (& $CliPath schedule --status 2>&1 | Out-String).Trim() -replace '\s+', ' '
            Assert-That "Task Scheduler agrees the job is gone" `
                ($status -match 'No scheduled job') "the job was still registered"
        }

        Invoke-Element (Find-In $dialog 'Close') 'Close'
        Start-Sleep -Seconds 1
    }

    Complete-Run
}
catch {
    Write-Output "SMOKE_TEST_ERROR: $($_.Exception.Message)"
    Write-Output "SMOKE_TEST_RESULT=FAIL (exception)"
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) { $proc.Kill() }
}
