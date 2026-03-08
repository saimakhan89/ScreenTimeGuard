#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Completely removes the ScreenTimeGuard Windows service and optionally
    deletes the install directory.

.PARAMETER InstallDir
    Path where the service was installed.  Defaults to C:ProgramDataScreenTimeGuard.

.PARAMETER KeepFiles
    Pass this switch to leave the install directory intact (useful for re-install / upgrade).
#>
param(
    [string]$InstallDir = "C:ProgramDataScreenTimeGuard",
    [switch]$KeepFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName = "ScreenTimeGuard"
$EventSource = "ScreenTimeGuard"

function Write-Step([string]$msg) {
    Write-Host "`n[*] $msg" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 1. Stop the service
# ---------------------------------------------------------------------------
Write-Step "Stopping service '$ServiceName'..."
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    Write-Host "    Service not found - nothing to stop."
} else {
    if ($svc.Status -ne 'Stopped') {
        # The service SDDL restricts sc.exe from standard users, but this script
        # runs as Administrator so Stop-Service works fine.
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $timeout = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 500
            $svc.Refresh()
        } while ($svc.Status -ne 'Stopped' -and [DateTime]::UtcNow -lt $timeout)
        Write-Host "    Service stopped."
    } else {
        Write-Host "    Service was already stopped."
    }
}

# ---------------------------------------------------------------------------
# 2. Delete the service registration
# ---------------------------------------------------------------------------
Write-Step "Deleting service registration..."
$svcCheck = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svcCheck) {
    # sc.exe sdset may have restricted query rights; reset SDDL to allow delete.
    $openSddl = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)"
    & sc.exe sdset $ServiceName $openSddl 2>$null | Out-Null

    $result = & sc.exe delete $ServiceName
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    Service deleted."
    } else {
        Write-Host "    sc.exe delete output: $result"
        Write-Host "    WARNING: Service deletion returned exit code $LASTEXITCODE." -ForegroundColor Yellow
        Write-Host "             You may need to reboot for it to be fully removed." -ForegroundColor Yellow
    }
} else {
    Write-Host "    Service not found - skipping delete."
}

# ---------------------------------------------------------------------------
# 3. Remove the Windows Event Log source
# ---------------------------------------------------------------------------
Write-Step "Removing Event Log source '$EventSource'..."
try {
    if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) {
        [System.Diagnostics.EventLog]::DeleteEventSource($EventSource)
        Write-Host "    Event source removed."
    } else {
        Write-Host "    Event source not found - skipping."
    }
} catch {
    Write-Host "    WARNING: Could not remove Event Log source: $_" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 4. Delete install directory (unless -KeepFiles)
# ---------------------------------------------------------------------------
if ($KeepFiles) {
    Write-Host "`n    -KeepFiles specified - leaving '$InstallDir' intact."
} else {
    Write-Step "Removing install directory '$InstallDir'..."
    if (Test-Path $InstallDir) {
        # Restore normal ACLs so we can delete the directory.
        try {
            $acl = Get-Acl $InstallDir
            $acl.SetAccessRuleProtection($false, $true)  # Re-enable inheritance.
            Set-Acl -Path $InstallDir -AclObject $acl
        } catch {
            # Best-effort; proceed anyway.
        }
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-Host "    Directory removed."
    } else {
        Write-Host "    Directory not found - skipping."
    }
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  ScreenTimeGuard has been uninstalled."                   -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
