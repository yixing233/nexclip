# NexClip Installer Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the native installer reliable for upgrades, uninstall, dependency setup, cancellation, payload extraction, and release packaging.

**Architecture:** Keep the existing native Win32 UI and service boundaries. Add small policy/helpers for path resolution, payload validation, and deferred self-delete; use a staging directory for upgrades and a detached cleanup process for uninstall. Keep dependency downloads sequential and cancellable.

**Tech Stack:** .NET 9, Native AOT, Win32/GDI+, PowerShell packaging, xUnit.

---

### Task 1: Preserve the existing installation path

**Files:**
- Modify: `NexClip.Installer.Native/UI/FluentInstallerWindow.cs`
- Test: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] Add a helper that reads `InstallLocation` from the HKCU uninstall key, validates it as an absolute directory, and falls back to the current default only when missing or invalid.
- [x] Use the resolved path before version detection and disk-space checks.
- [x] Add tests for valid and invalid install-location values.

### Task 2: Harden payload extraction and staging upgrades

**Files:**
- Modify: `NexClip.Installer.Native/Services/PayloadService.cs`
- Modify: `NexClip.Installer.Native/UI/FluentInstallerWindow.cs`
- Test: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] Reject ZIP entries whose normalized full path escapes the destination directory.
- [x] Add payload validation for required files before installation starts.
- [x] Extract into a uniquely named staging directory on the target volume, validate it, then copy/swap into the installation directory while preserving a rollback directory until success.
- [x] Remove rollback data after successful registration; restore it on failure where possible.
- [x] Add traversal and required-file tests.

### Task 3: Make uninstall complete and cancellable

**Files:**
- Modify: `NexClip.Installer.Native/Program.cs`
- Modify: `NexClip.Installer.Native/Services/ProcessHelper.cs`
- Modify: `NexClip.Installer.Native/Services/ShortcutHelper.cs`
- Modify: `NexClip.Installer.Native/UI/FluentInstallerWindow.cs`
- Test: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] Share a cancellation token across install and dependency operations; disable destructive close while work is active or request cancellation and wait for child processes.
- [x] Make silent uninstall resolve the registered install directory, remove shortcuts/registry/data, and schedule detached deletion of the install directory after the uninstaller exits.
- [x] Make interactive uninstall use the same cleanup path and only show success after cleanup is scheduled.
- [x] Add cleanup-path tests without deleting real user directories.

### Task 4: Correct user-data cleanup and privilege handoff

**Files:**
- Modify: `NexClip.Installer.Native/Services/SettingsStore.cs` or installer-side path helper
- Modify: `NexClip.Installer.Native/Services/ProcessHelper.cs`
- Modify: `NexClip.Installer.Native/UI/FluentInstallerWindow.cs`
- Test: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] Enumerate default roaming, local, legacy, and configured storage directories when the user opts out of preserving data.
- [x] Launch the installed app through the shell using the unelevated user token when available, with a direct launch fallback.
- [x] Add path-list and launch-policy tests.

### Task 5: Validate and publish complete payloads

**Files:**
- Modify: `build-installer.ps1`
- Modify: `NexClip.Installer.Native/Services/SetupPolicy.cs`
- Test: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] Fail packaging if required runtime files (`NexClip.exe`, `NexClip.Tray.dll`, `Svg.dll`, PRI/XBF resources) are absent from staging.
- [x] Use per-dependency download limits instead of a blanket 512 MB allowance while retaining a global safety margin.
- [x] Add tests for payload manifest validation and dependency limits.

### Verification

- [x] Run `dotnet test .\\NexClip.Installer.Native.Tests\\NexClip.Installer.Native.Tests.csproj --configuration Release`.
- [x] Run `dotnet build .\\NexClip.Installer.Native\\NexClip.Installer.Native.csproj --configuration Release`.
- [x] Run the packaging script only after the payload manifest check passes; do not launch NexClip automatically.
