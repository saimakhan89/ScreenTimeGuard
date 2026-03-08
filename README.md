# ScreenTimeGuard

A tamper-resistant Windows Service (.NET 8) that blocks entertainment apps (Minecraft, Fortnite, Roblox) on weekdays and enforces a configurable daily time limit on weekends.

---

## How it works

| Day | Default behaviour |
|-----|-------------------|
| Monday - Friday | Any blocked app process is killed within 3 seconds. |
| Saturday - Sunday | Apps are allowed up to the daily time limit (default: 60 min total across all apps), then killed for the rest of the day. |

The service runs as **LocalSystem** (highest privilege), auto-starts at boot, and its file/service ACLs are set so a standard user account cannot stop, reconfigure, or delete it.

---

## Blocked apps (default)

| App | Detection method |
|-----|-----------------|
| Minecraft (vanilla + Lunar Client) | `javaw.exe` command line + launcher path |
| Fortnite | `FortniteClient-Win64-Shipping.exe` + `EpicGamesLauncher.exe` path |
| Roblox | `RobloxPlayerBeta.exe` / `RobloxPlayer.exe` path |

All apps share a single daily time counter on weekends.

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| Windows 10 / 11 | 64-bit |
| .NET 8 Runtime | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) - "ASP.NET Core & .NET Runtime" |
| Visual Studio 2022 or `dotnet` CLI | To build |
| PowerShell 5.1+ running as Administrator | For install/uninstall scripts |

---

## Quick-start

### 1. Build & publish

```powershell
# From the repo root:
dotnet publish src/MinecraftBlocker -c Release -r win-x64 --no-self-contained -o publish
```

This produces a `publish\` folder next to the install scripts.

### 2. Install (run as Administrator)

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-MinecraftBlocker.ps1
```

The script:
- Copies binaries to `C:\ProgramData\MinecraftBlocker`
- Registers the service (`sc.exe create ... start=auto obj=LocalSystem`)
- Pre-creates the Windows Event Log source
- Removes all inherited ACEs from the install directory and grants **Administrators + SYSTEM only**
- Applies a restrictive service DACL (SDDL) so standard users cannot `sc stop` the service
- Configures three automatic-restart failure actions
- Starts the service

### 3. Verify

```powershell
Get-Service MinecraftBlocker   # Status should be Running
Get-EventLog -LogName Application -Source ScreenTimeGuard -Newest 5
```

---

## Configuring the schedule

Edit `C:\ProgramData\MinecraftBlocker\appsettings.json` **as Administrator**.
Changes are detected automatically - **no restart required**.

### Block all day Mon-Fri (default)

```json
"BlockedDays": ["Monday","Tuesday","Wednesday","Thursday","Friday"],
"AllowedTimeWindows": []
```

### Allow after 5 pm on weekdays

```json
"BlockedDays": ["Monday","Tuesday","Wednesday","Thursday","Friday"],
"AllowedTimeWindows": [
  { "Start": "17:00:00", "End": "21:30:00" }
]
```

### Add a new app to block

Add a fragment of the executable path to `ProcessPathKeywords`:

```json
"ProcessPathKeywords": [
  "Lunar Client",
  "FortniteClient-Win64-Shipping",
  "EpicGamesLauncher",
  "RobloxPlayerBeta",
  "RobloxPlayer",
  "Steam\\steamapps\\common\\YourGame"
]
```

### Full config reference

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BlockedDays` | string[] | Mon-Fri | Days when blocking is active |
| `AllowedTimeWindows` | object[] | `[]` | Time ranges inside a blocked day when apps are permitted |
| `JavaProcessKeywords` | string[] | `[".minecraft",".lunarclient","minecraft"]` | Substrings matched against `javaw.exe` command line (for Java-based games) |
| `ProcessPathKeywords` | string[] | see above | Substrings matched against each process's executable path (for native apps) |
| `FreeDayDailyLimitMinutes` | int | `60` | Shared daily limit in minutes across all blocked apps on free days (0 = unlimited) |
| `TimeZoneId` | string | `"Eastern Standard Time"` | Schedule timezone - pinned to prevent clock-change bypass. Run `tzutil /l` for IDs. |
| `PollIntervalSeconds` | int | `3` | How often the service scans for processes |
| `LogBlockedAttempts` | bool | `true` | Write kill events to the Windows Event Log |
| `EventLogSource` | string | `"ScreenTimeGuard"` | Custom Event Log source name |

---

## Uninstall

```powershell
.\Uninstall-MinecraftBlocker.ps1
```

Pass `-KeepFiles` to preserve the install directory (useful for re-deployment):

```powershell
.\Uninstall-MinecraftBlocker.ps1 -KeepFiles
```

---

## Custom install path

Both scripts accept parameters:

```powershell
.\Install-MinecraftBlocker.ps1 -PublishDir "C:\Build\publish" -InstallDir "D:\Services\ScreenTimeGuard"
.\Uninstall-MinecraftBlocker.ps1 -InstallDir "D:\Services\ScreenTimeGuard"
```

---

## Tamper resistance summary

| Vector | Protection |
|--------|-----------|
| `sc stop MinecraftBlocker` (standard user) | Service DACL denies all service-control rights to non-admins |
| Deleting / editing files in the install dir | Directory ACL: Administrators + SYSTEM only |
| Killing the service process in Task Manager | SYSTEM processes require SeDebugPrivilege; standard users lack it |
| Disabling auto-start via `sc config` | Covered by the same service DACL |
| Changing the system clock / timezone | Schedule pinned to configured `TimeZoneId` via UTC conversion |
| Rebooting | Service is `start=auto`; restarts with Windows |
| Crash / unexpected exit | Three automatic-restart failure actions configured |

> **Note:** No software protection is absolute. A determined administrator-level user can always bypass these controls. The goal is to prevent a standard (limited) child account from bypassing the blocker.

---

## Event Log

Blocked attempts are logged under:

- **Log:** Application
- **Source:** ScreenTimeGuard
- **Level:** Warning

Service start/stop events are logged as Information.

View quickly:

```powershell
Get-EventLog -LogName Application -Source ScreenTimeGuard -Newest 20
```

---

## Project structure

```
ScreenTimeGuard/
|- MinecraftBlocker.sln
|- src/
|  `- MinecraftBlocker/
|     |- MinecraftBlocker.csproj   # Worker Service, net8.0-windows
|     |- Program.cs                # Host setup
|     |- Worker.cs                 # BackgroundService - polling loop + kill logic
|     |- BlockerConfig.cs          # Config POCO
|     |- PlayState.cs              # Per-day time tracking, persisted to play-state.json
|     `- appsettings.json          # Default schedule config
|- Install-MinecraftBlocker.ps1
|- Uninstall-MinecraftBlocker.ps1
`- README.md
```
