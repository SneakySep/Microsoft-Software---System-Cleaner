# Verifies "Run now" end to end: it presses the button, answers the confirmation, and checks what
# the run actually removed.
#
# It works against a job registered by hand whose command is pointed at a sandbox directory under
# %TEMP%. That is the only way to exercise the feature safely: the button runs whatever the
# scheduler holds, so this script controls what the scheduler holds. It refuses to press anything
# unless the dialog is showing the sandbox path, and it removes the job and the sandbox afterwards.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-now-smoke-test.ps1 `
#                     -ExePath <path> -CliPath <path>

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$CliPath
)

. "$PSScriptRoot/UiAutomation.ps1"

# The app's own task name. It only ever looks at that one name - which is what stops it claiming
# a job somebody else registered - so a probe under any other name would be invisible to it.
$sandbox = Join-Path $env:TEMP ("wc-runnow-" + [Guid]::NewGuid().ToString("N"))
$taskName = 'ScheduledClean'
$taskPath = '\WindowsCleaner\'
$proc = $null

try {
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    Set-Content -Path (Join-Path $sandbox 'stale-a.tmp') -Value ('x' * 2000)
    Set-Content -Path (Join-Path $sandbox 'stale-b.tmp') -Value ('y' * 3000)

    # Registered with the cmdlets rather than schtasks: nothing to get wrong with quoting, and the
    # definition it writes is the same schema the app reads back.
    $action = New-ScheduledTaskAction -Execute $CliPath `
        -Argument "clean --yes --target `"$sandbox`" --min-age 0 --origin scheduled"
    $trigger = New-ScheduledTaskTrigger -Daily -At 03:00
    Register-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Action $action -Trigger $trigger -Force | Out-Null

    Assert-That "the probe job is registered" `
        ($null -ne (Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath -ErrorAction SilentlyContinue)) "not found"

    $app = Start-App $ExePath
    $proc = $app.Process
    $window = $app.Window
    $root = $app.Root
    $pidCond = $app.Condition

    Invoke-Button $scheduleLinkName
    $dialog = Wait-ForWindow 'Scheduled cleaning'
    Assert-That "scheduler dialog opened" ($null -ne $dialog) `
        "windows of this process: $((Get-WindowNames) -join ' | ')"

    if ($null -eq $dialog) { throw "The scheduler dialog did not open." }

    $dialogTexts = Get-Texts $dialog
    $commandLine = $dialogTexts | Where-Object { $_ -like "*$sandbox*" } | Select-Object -First 1
    Write-Output "STAT  registered command     = $commandLine"

    Assert-That "the dialog reads the hand-registered command back" ([bool]$commandLine) `
        "not found among: $($dialogTexts -join ' | ')"
    # The history is emptied before this script runs, so a fresh job genuinely has no run behind it.
    Assert-That "a job that has never run says so" `
        ([bool](($dialogTexts | Where-Object { $_ -like 'It has not run yet*' }) | Select-Object -First 1)) `
        "no last-run line found"
    Assert-That "the next run is shown for a job this app did not write" `
        ([bool](($dialogTexts | Where-Object { $_ -like 'Next run *' }) | Select-Object -First 1)) `
        "no next-run line found"

    # The guard that makes running this at all safe.
    if (-not $commandLine) {
        throw "Refusing to press Run now: the registered command does not mention the sandbox."
    }

    $runNow = Find-In $dialog 'Run now'
    Assert-That "run now is available with a job registered" `
        ($null -ne $runNow -and $runNow.Current.IsEnabled) "it was not available"

    Invoke-Element $runNow 'Run now'

    $confirm = Wait-ForWindow 'Run the scheduled job'
    Assert-That "running it now asks before doing anything" ($null -ne $confirm) "no confirmation appeared"

    if ($null -ne $confirm) {
        $confirmNames = Get-Names $confirm
        Write-Output "STAT  confirm prompt         = $(($confirmNames -join ' | ') -replace '\r?\n', ' ')"

        # The probe's command has no --mode, so the CLI's own default applies and the job recycles.
        # The prompt has to say so: it said "delete files permanently" until this was checked
        # against what the run actually did.
        Assert-That "the prompt describes the disposal mode the command really uses" `
            ([bool](($confirmNames | Where-Object { $_ -match 'move files to the Recycle Bin' }) | Select-Object -First 1)) `
            "no accurate disposal mode in the prompt"
        Assert-That "the prompt states the freshness guard" `
            ([bool](($confirmNames | Where-Object { $_ -match 'modified in the last' }) | Select-Object -First 1)) `
            "no guard in the prompt"

        [void][Win32]::SetForegroundWindow($confirm.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 500
        Click-Element (Find-In $confirm 'Yes') 'Yes'
    }

    # Polled rather than slept: what matters is what the run left behind.
    $deadline = (Get-Date).AddSeconds(60)
    $remaining = -1

    while ((Get-Date) -lt $deadline) {
        $remaining = @(Get-ChildItem $sandbox -File -ErrorAction SilentlyContinue).Count
        if ($remaining -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }

    Write-Output "STAT  files left in sandbox  = $remaining"
    Assert-That "the job actually removed the sandbox's files" ($remaining -eq 0) "$remaining still there"

    $dialogTexts = Get-Texts $dialog
    $finished = $dialogTexts | Where-Object { $_ -like 'Finished*' } | Select-Object -First 1
    Write-Output "STAT  run summary            = $finished"

    Assert-That "the run reports what it did" ([bool]$finished) "no summary found"
    Assert-That "the summary is taken from the record the run wrote" `
        ([bool]($finished -like 'Finished: *')) "was '$finished'"
    # Before the run the same line said it had never run; now it has to say otherwise.
    $lastRun = $dialogTexts | Where-Object { $_ -like 'It last ran*' } | Select-Object -First 1
    Write-Output "STAT  last-run line           = $lastRun"
    Assert-That "the dialog now knows when the job last ran" ([bool]$lastRun) "no last-run line found"

    Invoke-Element (Find-In $dialog 'Close') 'Close'
    Start-Sleep -Seconds 2

    # The dashboard re-reads the history when the dialog closes. That is the only way a clean
    # performed by another process becomes visible here.
    Invoke-Button 'History'
    Start-Sleep -Milliseconds 800

    $texts = Get-Texts $window
    $origins = ($texts | Where-Object { $_ -in @('app', 'command line', 'scheduled') }) -join ', '

    Assert-That "the dashboard history shows the scheduled run" ($texts -contains 'scheduled') `
        "no scheduled row; origins shown: $origins"
    Assert-That "the dashboard totals the recorded runs" `
        ([bool](($texts | Where-Object { $_ -like '*runs recorded*' }) | Select-Object -First 1)) "no totals line"

    Complete-Run
}
catch {
    Write-Output "SMOKE_TEST_ERROR: $($_.Exception.Message)"
    Write-Output "SMOKE_TEST_RESULT=FAIL (exception)"
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) { $proc.Kill() }
    Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -Path $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
