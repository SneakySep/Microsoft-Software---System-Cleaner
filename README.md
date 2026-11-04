# Windows Cleaner

A lightweight Windows disk-cleanup utility. It scans the standard Windows temporary folders
(plus any folders you add), shows you exactly what it found, lets you tick the files you want
gone, and removes only those — reporting honestly whether the space was actually reclaimed or
merely moved to the Recycle Bin.

Ships as two front-ends over one shared engine:

- **`WindowsCleaner.App`** — a WPF desktop dashboard (C# / .NET 10, MVVM), with a panel for
  scheduled cleaning and a guarded action for emptying the Recycle Bin
- **`WindowsCleaner.Cli`** (`wclean`) — a headless command-line tool over the same engine

---

## Why WPF and not WinUI 3

WinUI 3 was the first choice and was evaluated here rather than assumed. On this machine it
compiles (35 s) but the unpackaged app fails to launch with a Windows App SDK bootstrapper
error (`0x80670016`), even with Windows App Runtime 1.7/1.8/2.0 installed. The equivalent WPF
app builds in ~2 s and launches cleanly.

For an explicitly *lightweight* tool, WPF also wins on merit: no Windows App SDK dependency, no
MSIX packaging or signing certificate, and a much faster inner loop. The engine is a separate
assembly, so moving the UI to WinUI 3 later means replacing one project.

---

## Requirements

- Windows 10 1607 or later (developed and verified on Windows with .NET 10)
- Windows 11 22H2 or later to see the liquid-glass backdrop: it is drawn by DWM on request, and on
  Windows 10 or older builds the request simply fails, leaving the solid surface - nothing breaks
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- No third-party NuGet packages at runtime

---

## Project structure

```
WindowsCleaner/
├── WindowsCleaner.sln
├── src/
│   ├── WindowsCleaner.Core/            # Engine. No UI references at all.
│   │   ├── Models/
│   │   │   ├── CleanTarget.cs          # An allowlisted directory whose contents may be cleaned
│   │   │   ├── JunkFile.cs             # One disposable file + its scanned size/timestamp
│   │   │   ├── SkipReason.cs           # Why an entry was deliberately left alone
│   │   │   ├── DeletionMode.cs         # Permanent vs Recycle Bin
│   │   │   ├── CleanOptions.cs         # Mode + recycle batch size
│   │   │   ├── RecycleBinStats.cs      # Current bin size and item count
│   │   │   ├── ScanResult.cs           # Scan aggregate + per-target summary
│   │   │   ├── ScanProgress.cs
│   │   │   ├── CleanResult.cs          # Freed vs recycled bytes + per-file failures
│   │   │   ├── CleanProgress.cs
│   │   │   └── CleanReport.cs          # What the last clean did, before vs after
│   │   ├── Configuration/
│   │   │   ├── CleanerSettings.cs      # Folders, emptying threshold, remembered CLI path
│   │   │   ├── SettingsStore.cs        # Defensive JSON load/save
│   │   │   └── CleanReportStore.cs     # Persists the last clean's outcome
│   │   ├── Safety/
│   │   │   ├── PathGuard.cs            # The single gate for every destructive operation
│   │   │   ├── RecycleBin.cs           # shell32 recycling, batched
│   │   │   ├── RecycleBinInfo.cs       # Queries bin size/item count
│   │   │   └── RecycleBinEmpty.cs      # shell32 emptying (whole bin, irreversible)
│   │   ├── Targets/
│   │   │   ├── ICleanTargetProvider.cs
│   │   │   ├── DefaultCleanTargetProvider.cs
│   │   │   ├── CompositeTargetProvider.cs  # Defaults + user folders
│   │   │   ├── SafeRootValidator.cs    # Guard rail on user-supplied folders
│   │   │   └── StaticTargetProvider.cs # Explicit directories (CLI --target, tests)
│   │   ├── Scheduling/
│   │   │   ├── ScheduledCleanOptions.cs
│   │   │   ├── ScheduledCleanTask.cs   # Creates, queries and removes the job
│   │   │   ├── ScheduledTaskSnapshot.cs # Reads the registered job back from the scheduler
│   │   │   ├── ScheduleText.cs
│   │   │   └── WcleanLocator.cs        # Finds the CLI the job has to run
│   │   ├── Services/
│   │   │   ├── IScannerService.cs / TempScanner.cs
│   │   │   ├── ICleanerService.cs / CleanerService.cs
│   │   │   ├── IRecycleBinService.cs / RecycleBinService.cs
│   │   │   ├── ISchedulerService.cs / SchedulerService.cs
│   │   │   └── ScannerOptions.cs
│   │   └── Utilities/                  # ByteSizeFormatter, SkipReasonFormatter
│   │
│   ├── WindowsCleaner.App/             # WPF UI. Contains no file system code.
│   │   ├── App.xaml(.cs)               # Composition root
│   │   ├── MainWindow.xaml(.cs)        # Dashboard (code-behind: backdrop wiring only)
│   │   ├── SchedulerWindow.xaml(.cs)   # Scheduled cleaning dialog
│   │   ├── Theme/Theme.xaml            # Glass palette, typography, control styles
│   │   ├── Theme/Backdrop.cs           # DWM acrylic backdrop, degrades to solid
│   │   ├── ViewModels/                 # MainViewModel, SchedulerViewModel, commands
│   │   ├── Converters/
│   │   ├── Humanize.cs                 # Relative ages and durations
│   │   ├── SettingsEditor.cs           # Load-modify-save, so one change never resets others
│   │   └── app.manifest                # PerMonitorV2 DPI, long paths, asInvoker
│   │
│   └── WindowsCleaner.Cli/             # Headless front-end (`wclean`)
│       ├── Program.cs                  # Commands, confirmation, progress line
│       ├── CommandLine.cs              # Argument parsing + usage
│       └── Output.cs                   # Text and JSON reporting
│
├── tests/WindowsCleaner.Tests/         # 121 tests, including real on-disk round trips
└── scripts/ui-smoke-test.ps1           # Drives the real window and its dialogs via UI Automation
```

---

## Architecture

Three layers, with dependencies pointing strictly one way:

```
   WindowsCleaner.App (WPF, MVVM)     WindowsCleaner.Cli (console)
                 \                    /
                  ▼                  ▼
   IScannerService / ICleanerService / IRecycleBinService / ISchedulerService
                            │
                  WindowsCleaner.Core (engine)
                            │
                   Windows file system
```

**The UI layers hold no file-system code.** `MainViewModel` and the CLI both talk to the same
Core interfaces and nothing else. That is why adding a CLI required **zero changes to the
engine**, and why scheduling could be built by invoking the CLI's own `clean` command rather
than duplicating any cleaning logic: the app's scheduler panel and the CLI both drive one
cleaning path.

**The engine** (`WindowsCleaner.Core`) targets plain `net10.0` with no reference to WPF.

### Async and responsiveness

Directory enumeration is synchronous, I/O-bound work. Rather than fake asynchrony over it, each
operation runs inside a single `Task.Run` so it never occupies the dispatcher thread, and
progress is marshalled back through `IProgress<T>`. Progress reporting is throttled by both file
count and elapsed time, because reporting on all ~16,000 files individually would flood the
dispatcher.

The results list is bound to a plain `IReadOnlyList<SelectableFile>` that is **swapped in one
assignment** rather than an `ObservableCollection` that gets appended to. A `CollectionChanged`
per item would stall the UI for seconds at this scale; one property change is enough because the
list only ever changes wholesale. Individual rows *do* raise change notifications, for the
checkboxes.

---

## Selecting what to clean

Every row carries a checkbox, ticked by default. The stat cards deliberately show the
**selection**, not the raw scan totals — the number that matters is what pressing Clean Now
would actually remove.

- **Select all / Select none** are in the toolbar above the list.
- Bulk changes suppress per-row recalculation and run once at the end. Ticking 16,000 boxes one
  at a time would otherwise trigger 16,000 full-list recalculations, which is the difference
  between an instant action and a multi-second freeze.
- **Clean Now** is disabled whenever nothing is selected, so the destructive action is never
  available when it would be a no-op.

---

## Recycle Bin mode

On by default. Files go to the Recycle Bin so the operation can be undone, and a toggle in the
toolbar switches to permanent deletion.

The reporting is careful about the difference, because conflating the two would mean lying about
disk space:

| Mode | What happens | Reported as |
|---|---|---|
| `RecycleBin` (default) | File moves to the bin | **"Moved 3.4 GB to the Recycle Bin"** — `FreedBytes` stays 0 |
| `Permanent` | File is deleted | **"Freed 3.4 GB"** — `FreedBytes` is real |

A recycled file still occupies the volume. Claiming "freed 3.4 GB" while 3.4 GB sits in the bin
would be wrong, so `CleanResult` keeps `FreedBytes` and `RecycledBytes` separate and the UI and
CLI word their messages accordingly.

**The UI also shows what the Recycle Bin is currently holding**, refreshed after every clean and
turning amber once there is enough in there to be worth acting on. An **Open** button reveals it
in Explorer; emptying it is a separate, gated action rather than something a readout can do by
accident.

Because there is no managed API for recycling, `RecycleBin` wraps the shell's `SHFileOperation`.
Recycling is far slower per call than `File.Delete`, so files are submitted in batches of 64; a
batch that fails is retried one file at a time so the failure lands on the specific file
responsible rather than tarring the whole batch with it.

### Emptying it

The bin readout carries an **Empty** action, guarded three ways:

- **A size threshold**, 1 GB by default. Below it the action is disabled rather than hidden, with
the reason in the tooltip. Emptying destroys the whole bin irreversibly and cannot be scoped to
particular files, so it should not sit one click away from a bin somebody is keeping on purpose.
Lower `recycleBinEmptyThresholdBytes` in the settings file — or set it to `0` — to allow smaller
bins.
- **A confirmation** that names exactly what would go: item count, total size, and the fact that
it cannot be recovered.
- **A fresh reading either side.** The outcome reports what actually left the bin, measured by
asking the shell again afterwards, instead of assuming the whole of the previous total went. A
file held open by another process survives an empty, and counting its bytes as reclaimed would
overstate what happened.

`SHEmptyRecycleBin` is used with the shell's own confirmation, progress UI and sound suppressed.
Two prompts for one irreversible action would be one too many, so the consent has to come from
the app.

---

## Extra folders

You can add your own folders to the scan, from the chips row under the action buttons. The list
is **additive** — it never replaces the standard Windows locations, so adding a folder cannot
silently stop `%TEMP%` from being cleaned — and it persists to
`%APPDATA%\WindowsCleaner\settings.json`.

### The guard rail

Letting a user point a deleting tool at arbitrary folders makes it strictly more useful and
strictly more capable of destroying something important. `SafeRootValidator` is the answer: it
refuses any folder that is, or *contains*, a system or personal location — every drive root, the
Windows directory, System32, Program Files, ProgramData, AppData, the profile directory, and
Desktop/Documents/Downloads/Pictures/Videos/Music/OneDrive.

The rule is about whole folders rather than a blanket ban on a directory tree, which matters:
adding `C:\Windows\Temp` is allowed even though `C:\Windows` is not, because it is neither equal
to a protected root nor an ancestor of one. Adding `C:\Windows` or `C:\Users` is refused, because
everything inside would become deletable.

Two further details:

- The guard is **re-applied at scan time**, not just when a folder is added. The settings file is
  plain JSON that anyone can edit, so a folder that would now be refused is reported as ignored
  rather than trusted just because it was once accepted.
- `--target` on the CLI **replaces** the saved list entirely, so a one-off scripted run is never
  silently widened by state left behind in a settings file.

---

## Scheduled cleaning

An unattended cleaning job is registered with **Windows Task Scheduler** rather than a timer
inside the app, so it still runs when nothing is open. The job simply invokes the existing
`wclean clean` command — meaning a scheduled run goes through exactly the same scanning,
allowlist and safety code as a manual one.

```bash
wclean schedule                                          # show the current job
wclean schedule --install --at 03:00 --yes               # daily at 03:00
wclean schedule --install --weekly --day SUN --at 04:30 --yes
wclean schedule --remove --yes
```

The scheduler defaults differ from the interactive ones on purpose:

| | Interactive | Scheduled |
|---|---|---|
| Disposal | **Recycle Bin** (undoable) | **Permanent** (a repeating job that only fills the bin never reclaims anything) |
| Freshness guard | 15 minutes | **24 hours** (unattended, so only provably stale files) |

Safety for an unattended job comes from that long freshness guard rather than from the disposal
mode. Override with `--mode recycle` and `--min-age` if you prefer.

The task is registered with `/RL LIMITED`, so it runs with your normal token rather than
elevated, and `/F`, so re-running the install command updates the job instead of failing.

### The panel

**Scheduled cleaning…** in the app's header opens the same job's controls: what is registered
right now, when it runs, how it disposes of files, and buttons to turn it on, update it or turn
it off. Two things about it are deliberate.

**It reports what the scheduler holds, not what it last asked for.** The job is read back and
its definition parsed, so a job changed by hand is described as it really is, a job that has
disappeared is not still listed as scheduled, and a scheduler that cannot be queried at all is
reported as unknown rather than as "nothing is scheduled". The XML form of the task is parsed
rather than `schtasks`'s text listing, whose column labels are localised and whose element names
are not.

**It schedules `wclean.exe`, never the app.** A job pointing at the desktop app would launch the
GUI at 3am, the GUI would ignore `clean --yes`, and nothing would ever be cleaned — a failure
that is invisible precisely because it happens unattended. `WcleanLocator` finds the CLI (in
order: an explicitly remembered choice, then beside the app, then a source checkout's build
outputs), any executable named `WindowsCleaner.exe` is refused outright, and the panel will not
register anything until it has a target. If the search comes up empty it says where it looked,
offers a file picker, and remembers the answer.

The freshness guard is shown but not editable here: 24 hours is what this UI writes, because it
is the main thing keeping an unattended job safe. A job registered with a different guard through
`wclean schedule --min-age` still works, and the panel points out the difference rather than
silently rewriting it on the next update.

---

## What the last clean did

Every clean records its outcome to `%APPDATA%\WindowsCleaner\last-clean.json`, and the dashboard
shows it as a line under the results:

```
LAST CLEAN   Moved 3.4 GB to the Recycle Bin · 3h ago · command line   [Details]
```

**Details** expands the before/after breakdown, which is the part that makes a partial run
legible:

```
Before: 12,481 files selected, 3.4 GB.
After: 12,464 files moved, 17 skipped. Of that, 0 B was reclaimed; the rest is still occupying
       the disk inside the Recycle Bin.
Recycle Bin: 1.2 GB before → 4.6 GB after.
Took 4.1s.
```

Storing only the totals would make a clean that skipped 17 locked files indistinguishable from
one that removed everything. Both front-ends write the record, so a scheduled run is visible in
the morning — which is the run most worth being able to check, because nobody was watching it.
It is a single record rather than a growing history: no unbounded growth, and no long-term log of
what was on your disk.

---

## Robustness

`Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)` aborts the entire walk the
moment it meets one inaccessible subdirectory — which in `%TEMP%` is guaranteed to happen. This
uses an explicit stack instead, wrapping every individual file-system interaction, so one bad
entry costs one entry and the scan continues.

Skipped entries are counted by reason (locked, access denied, modified too recently, junction,
vanished) and shown in the UI footer and the CLI report, so "nothing found" is never ambiguous.

---

## Safety model

A tool that deletes files is only as safe as its path validation. Every destructive call routes
through `PathGuard`, which enforces:

1. **Allowlist.** A path must be *strictly inside* one of the configured roots. Being equal to a
   root is rejected — that is what prevents the cleaner from deleting `%TEMP%` itself.
2. **Canonicalization.** Paths are fully resolved first, so `..` segments cannot escape the root
   after validation.
3. **Reparse points.** Symlinks and junctions are neither followed nor deleted through. A
   junction planted in `%TEMP%` pointing at `C:\Users` would otherwise turn a temp clean into
   data loss, and lexical checks alone would not catch it.

On top of that:

- **Two-phase by design.** Scanning only ever *reads*; nothing is removed until you press Clean
  Now (or pass `--yes` on the CLI). The file list is the confirmation step.
- **Revalidation at delete time.** The cleaner does not trust the list it is handed. It re-runs
  the `PathGuard` check on every path immediately before touching it, closing the
  time-of-check/time-of-use window, and this holds in **both** disposal modes.
- **Re-stat at delete time.** Sizes and timestamps come from scan time, but the live file is
  measured again so reported totals are truthful and a file rewritten in the meantime is not
  silently removed.
- **Freshness guard.** Files modified in the last 15 minutes are skipped by default
  (`ScannerOptions.MinimumAge`). Applications and installers constantly create short-lived files
  in `%TEMP%`; deleting one mid-write is how a cleaner corrupts an in-flight install.
- **Runs unelevated.** The app requests `asInvoker` and reports `C:\Windows\Temp` as inaccessible
  rather than demanding admin up front — no training users to click through UAC for a tool that
  deletes files. A banner explains the limitation when you are not elevated.

### Deliberately not cleaned

Common "cleaner" categories are excluded because they need more care than a file sweep: the
Windows Update download cache (needs services stopped first), Prefetch (deleting it degrades
startup times), and browser caches (belong to a profile, unsafe while the browser runs).

---

## Build and run

```bash
dotnet build WindowsCleaner.sln -c Release

# desktop app
dotnet run --project src/WindowsCleaner.App

# command line
dotnet run --project src/WindowsCleaner.Cli -- scan
```

Run the tests:

```bash
dotnet test
```

The tests create isolated sandbox directories under `%TEMP%` and point the entire pipeline at
them, so they exercise the real production code path without ever touching your actual temp
folder. They cover real scan-to-delete round trips, locked-file tolerance, read-only files,
allowlist escapes, the freshness guard, real Recycle Bin recycling, the guard rail on
user-supplied folders, settings persistence, scheduled-job command construction, reading a
registered job back from real captured Task Scheduler XML, locating the CLI, the emptying
threshold, and last-clean report persistence.

To smoke-test the built window through UI Automation (clicks **Scan** and the selection controls,
then reads back what renders — it never clicks Clean Now):

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ui-smoke-test.ps1 `
  -ExePath src/WindowsCleaner.App/bin/Release/net10.0-windows/WindowsCleaner.exe `
  -CliPath src/WindowsCleaner.Cli/bin/Release/net10.0/wclean.exe
```

It also drives the scheduled-cleaning dialog — registering a real job and then removing it, so
the run ends where it started — and, when the bin is above the threshold, opens the emptying
confirmation and declines it, asserting the bin's contents are unchanged.

---

## Command line

```
wclean scan     [options]   Report what could be cleaned
wclean clean    [options]   Remove what the scan finds
wclean schedule [options]   Manage the unattended cleaning job

  --target <dir>     Clean this directory instead of the saved locations.
                     Repeatable. Replaces the default set entirely.
  --min-age <mins>   Skip files modified more recently than this (default 15; 0 disables).
  --mode <mode>      recycle (default) | permanent
  --top <n>          How many of the largest files to list (default 20, 0 for none).
  --json             Machine-readable output.
  --yes, -y          Skip the confirmation prompt. Required when not interactive.

  --status           Report the current job (the default with no action given).
  --install          Create or update the job.    --remove  Delete the job.
  --at <HH:mm>       Time of day (default 03:00).  --daily / --weekly --day SUN
```

Examples:

```bash
wclean scan --top 10
wclean clean --yes                      # recycles, so it can be undone
wclean clean --yes --mode permanent     # reclaims the space immediately
wclean scan --json | jq '.totalBytes'
wclean schedule --install --at 03:00 --yes
```

Exit codes: `0` success, `1` scan/clean failed, `2` bad usage, `3` declined at the prompt.

**`clean` will not proceed without `--yes` when input is not interactive.** Defaulting to "yes"
when there is nobody to ask would be the worst possible failure mode for a destructive command.
It also always prints the full scan report *before* asking, even with `--yes`, so the list is
never hidden. The same applies to `schedule --install` / `--remove`.

---

## Known limitations

- **Elevation.** Without an elevated token, `C:\Windows\Temp` and the Internet cache are reported
  as unreachable. This is by design; run as administrator to include them.
- **Emptying the Recycle Bin destroys all of it.** The shell offers no way to empty selected
  items, so this app has none either. Below the threshold the action is disabled with the reason
  in its tooltip rather than being hidden.
- **The emptying call itself is not covered by an automated test.** Running it would destroy
  whatever the developer had recycled, so the suite tests the decision logic only; the
  confirmation, and the fact that declining leaves the bin untouched, are exercised against a real
  bin by `scripts/ui-smoke-test.ps1`.
- **Scheduled cleaning's freshness guard is fixed at 24 hours in the UI.** `wclean schedule
  --min-age` can still set another value, and the panel reports the difference rather than
  silently rewriting it.
- **The last-clean record is a single entry, not a history.** It is overwritten by each clean, so
  it answers "what did the last one do?" and nothing older than that.
- **Custom folders are not pruned by age.** A file in a user-added folder is judged solely by the
  same freshness guard as everything else; the cleaner does not know what is safe to delete in an
  arbitrary folder the way it does in `%TEMP%`.
- **Empty directories are left behind.** Only files are removed. Removing the emptied folder tree
  is a straightforward extension but carries more risk than it is worth here.
- **Recycling is slower than deleting** (one shell call per batch instead of one syscall per
  file), and `SHFileOperation` predates long-path support, so paths over `MAX_PATH` are refused
  with a clear reason rather than silently mishandled.
- **If a volume has no Recycle Bin available, the shell falls back to permanently deleting** even
  when undo was requested. That is shell behaviour the wrapper cannot override.
- **"Freed" is the logical file length**, not the cluster-rounded allocation size the volume
  reports. They can differ slightly.
- **The test suite places a handful of small files in the real Recycle Bin** (a few
  `windows-cleaner-*.tmp` files). The shell's item count is the only way to actually prove
  recycling happened rather than assuming it, and the tests cannot remove them again without
  risking your other recycled files.
- **The junction/symlink traversal test skips itself** when run without
  `SeCreateSymbolicLinkPrivilege` (admin or Developer Mode), because the test cannot create a
  link to traverse. The related `PathGuard` logic is covered by unit tests regardless.

---

## License

[MIT](LICENSE) © 2026 SneakySep.
