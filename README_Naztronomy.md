# Target Scheduler (Naztronomy Addon): build and manual deployment

This fork of [Target Scheduler](https://github.com/tcpalmer/nina.plugin.targetscheduler) adds features that aren't in the official plugin yet. Because it isn't distributed through NINA's plugin manager, you build it yourself and copy it into NINA by hand. This guide covers both.

For general plugin documentation, see the [official docs](https://tcpalmer.github.io/nina-scheduler/) and the main [README](README.md).

## What this fork adds

- **Archived project state**: a new project State, "Archived". Archived projects are never imaged, and a toolbar button in Target Management hides them from the Projects tree.
- **Target Prioritization Overrides**: a new panel in the plugin's Options page.
  - It shows when each active target can be imaged over the coming nights (times, total hours, moon phase, closest moon approach).
  - You can prioritize targets through a date. While a target is prioritized, the scheduler picks it over all other targets whenever it can be imaged, and falls back to your other targets otherwise.
  - While a target is prioritized, you can skip individual filters (for example, shoot only OIII for a week).
  - Moon avoidance, twilight and visibility still take precedence: a prioritized target is never imaged when those checks reject it.
- **Prioritize Through** field on each target in Target Management.

## Prerequisites

On the machine you build on:

- Windows 10 or 11
- [.NET SDK](https://dotnet.microsoft.com/download) **8.0 or later**. The plugin targets .NET 8; it has been built with SDK 9.0.101. Check yours with `dotnet --list-sdks`.
- Git, to clone and update the repository
- Optional: Visual Studio 2022 with the ".NET desktop development" workload, if you prefer building in an IDE

On the machine that runs NINA:

- NINA **3.2 or later**
- Target Scheduler installed or previously used. The fork uses the same database, so your existing projects and targets carry over.

The build machine and the NINA machine can be the same computer.

## 1. Get the source

```powershell
git clone https://github.com/naztronaut/nina.plugin.targetscheduler.git
cd nina.plugin.targetscheduler
```

## 2. Build

From the repository root:

```powershell
dotnet build NINA.Plugin.TargetScheduler.sln -c Release
```

Or, in Visual Studio: open `NINA.Plugin.TargetScheduler.sln`, set the configuration to **Release**, and use **Build → Build Solution**.

A successful build ends with `0 Error(s)`. You'll also see a few dozen warnings (`NU1701`, `MSB3245`, `MSB3243`). They're expected and come from older framework references that NINA itself uses; they don't affect the plugin.

**Where the output goes.** A post-build step copies the plugin into NINA's plugin folder on the build machine:

```
%LOCALAPPDATA%\NINA\Plugins\3.0.0\Target Scheduler\
```

Use this folder as the source for deployment. Don't copy from `bin\` or `obj\`: `bin\` also contains NINA's own DLLs, which must not go into the plugin folder.

**Optional: run the tests.**

```powershell
dotnet test NINA.Plugin.TargetScheduler.Test -c Release
```

Run the whole test project. Don't narrow it with `--filter` to the database tests: they only work when the full suite loads. Astronomy-dependent tests (visibility, twilight, previous-target continuation) need NINA's astronomy database and fail on machines where it isn't available; that's an environment issue, not a regression.

## 3. Package

The plugin folder contains exactly these 10 files:

```
NINA.Plugin.TargetScheduler.dll              <- the fork's changes are in this one
NINA.Plugin.TargetScheduler.Shared.dll
NINA.Plugin.TargetScheduler.SyncService.dll
EmbedIO.dll
LinqKit.dll
LinqKit.Core.dll
Microsoft.WindowsAPICodePack.dll
Microsoft.WindowsAPICodePack.Shell.dll
Namotion.Reflection.dll
Swan.Lite.dll
```

To zip them for another machine:

```powershell
Compress-Archive -Path "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\Target Scheduler\*" -DestinationPath "$env:USERPROFILE\Desktop\TargetScheduler-fork.zip" -Force
```

To confirm you're packaging the latest build, check the timestamp:

```powershell
Get-Item "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\Target Scheduler\NINA.Plugin.TargetScheduler.dll" | Select-Object LastWriteTime
```

## 4. Deploy to the NINA machine

If the build machine _is_ the NINA machine, the build already deployed the plugin: skip to step 5.

1. **Close NINA.** It locks the plugin DLLs while running.
2. **Back up** these two items (copy them somewhere outside `%LOCALAPPDATA%\NINA\Plugins`, e.g. the Desktop):
   - the plugin folder: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\NINA.Plugin.TargetScheduler\` (if present)
   - the database: `%LOCALAPPDATA%\NINA\SchedulerPlugin\schedulerdb.sqlite`
3. **Make sure only one copy of Target Scheduler is installed.** Run:

   ```powershell
   Get-ChildItem "$env:LOCALAPPDATA\NINA\Plugins" -Recurse -Filter NINA.Plugin.TargetScheduler.dll | Select-Object FullName, LastWriteTime
   ```

   NINA's plugin manager may have installed the official plugin in a differently named folder (e.g. `Target Scheduler`). If more than one folder is listed, move the extra one **out of** `%LOCALAPPDATA%\NINA\Plugins` entirely; renaming it isn't enough, because NINA scans every folder there. With two copies loaded, NINA mixes them and the UI misbehaves: blank dropdowns, red validation borders, and an empty Target Prioritization Overrides panel.

4. **Copy the 10 files** into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\NINA.Plugin.TargetScheduler\`, creating the folder if needed and overwriting existing files.

## 5. First start and verification

Start NINA. On first start the fork prepares the database automatically:

- It adds two columns, `target.prioritizeThrough` and `exposureplan.skipWhilePrioritized`, if they're missing. Existing data is untouched.
- If the database was used by an earlier development build of this fork (database version 24 or 25), it resets the version to 23. That keeps future official migrations from being skipped.

To verify:

- **Target Scheduler log:** the newest file in `%LOCALAPPDATA%\NINA\SchedulerPlugin\Logs\` should show `adding database column ...` (on first use) or no errors at startup.
- **Options → Target Scheduler:** a **Target Prioritization Overrides** section appears between Scheduler Preview and Reporting.
- **Target Management:** a project's State dropdown includes **Archived**, and a target shows a **Prioritize Through** field.

## Updating to a newer build of the fork

1. Pull the latest changes: `git pull`
2. Build (step 2) and package (step 3).
3. On the NINA machine: close NINA, copy the files over (step 4.4), and start NINA.

## Keeping up with the official plugin

To pull in upstream changes:

```powershell
git remote add upstream https://github.com/tcpalmer/nina.plugin.targetscheduler.git   # once
git fetch upstream
git merge upstream/main
```

Then build and deploy as above. Conflicts, if any, are most likely in `Planner.cs`, `SchedulerDatabaseContext.cs` and `Options.xaml`.

**Don't let NINA's plugin manager update Target Scheduler** while you run the fork. It would replace these DLLs with the official build. Leave any plugin auto-update setting off, and don't click Update on Target Scheduler in the Plugins page.

## Switching back to the official plugin

1. Close NINA.
2. Delete `%LOCALAPPDATA%\NINA\Plugins\3.0.0\NINA.Plugin.TargetScheduler\` and reinstall Target Scheduler from NINA's plugin manager, or restore the plugin folder you backed up.
3. Start NINA.

Your database can stay as it is: the official plugin ignores the fork's two extra columns, and the fork doesn't change the database version. Prioritization and filter skips have no effect under the official plugin. They take effect again if you return to the fork.

The one exception: if your database was used by an **early development build** of the fork (version 24 or 25), start the current fork build once before switching, so it can reset the version. Otherwise a future official update could skip its own migrations. Restoring the database backup you made before first installing the fork also works.

## Troubleshooting

| Symptom                                                      | Likely cause / fix                                                                                                                                            |
| ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Build fails copying files ("access denied" / file in use)    | NINA is running on the build machine. Close it and rebuild.                                                                                                   |
| State/Priority dropdowns blank, values turn red, can't save  | Two copies of Target Scheduler are installed. See step 4.3.                                                                                                   |
| Target Prioritization Overrides panel is empty (no profiles) | Same as above.                                                                                                                                                |
| New features missing after starting NINA                     | NINA's plugin manager replaced the DLLs, or the copy went to the wrong folder. Check the timestamp of `NINA.Plugin.TargetScheduler.dll` in the plugin folder. |
| Times in the overrides grid look shifted by hours            | The grid uses the PC's clock time zone. Check the PC's time zone and the profile's latitude/longitude in NINA's Options.                                      |
