#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the MinecraftBlocker Windows service and locks it down so a
    standard (non-admin) user cannot stop, modify, or delete it.

.PARAMETER PublishDir
    Path to the folder that contains the published MinecraftBlocker.exe and
    its companion files.  Defaults to .\publish  (relative to this script).

.PARAMETER InstallDir
    Where the service files are deployed on the target machine.
    Defaults to C:\ProgramData\MinecraftBlocker.

.EXAMPLE
    .\Install-MinecraftBlocker.ps1
    .\Install-MinecraftBlocker.ps1 -PublishDir "C:\Build\publish" -InstallDir "D:\Services\MinecraftBlocker"
#>
param(
    [string]$PublishDir  = (Join-Path $PSScriptRoot "publish"),
    [string]$InstallDir  = "C:\ProgramData\MinecraftBlocker"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName        = "MinecraftBlocker"
$ServiceDisplayName = "Minecraft Blocker (Parental Control)"
$ServiceDescription = "Monitors and terminates Minecraft Java Edition processes on weekdays."
$BinaryPath         = Join-Path $InstallDir "MinecraftBlocker.exe"

# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) {
    Write-Host "`n[*] $msg" -ForegroundColor Cyan
}

function Confirm-Success([int]$exitCode, [string]$context) {
    if ($exitCode -ne 0) {
        Write-Host "    ERROR: $context failed with exit code $exitCode." -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------------------
# 1. Verify publish output exists
# ---------------------------------------------------------------------------
Write-Step "Verifying published binary..."
if (-not (Test-Path (Join-Path $PublishDir "MinecraftBlocker.exe"))) {
    Write-Host "ERROR: MinecraftBlocker.exe not found in '$PublishDir'." -ForegroundColor Red
    Write-Host "       Build the project first:  dotnet publish src\MinecraftBlocker -c Release -o publish" -ForegroundColor Yellow
    exit 1
}

# ---------------------------------------------------------------------------
# 2. Stop + remove old installation if present
# ---------------------------------------------------------------------------
Write-Step "Checking for existing installation..."
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "    Found existing service - stopping and removing it."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

# ---------------------------------------------------------------------------
# 3. Create install directory and copy files
# ---------------------------------------------------------------------------
Write-Step "Deploying files to '$InstallDir'..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $InstallDir -Recurse -Force
Write-Host "    Files copied."

# ---------------------------------------------------------------------------
# 4. Pre-create the Windows Event Log source (requires admin; do it now so
#    the service doesn't need to do it at runtime).
# ---------------------------------------------------------------------------
Write-Step "Registering Windows Event Log source..."
$eventSource = "MinecraftBlocker"
if (-not [System.Diagnostics.EventLog]::SourceExists($eventSource)) {
    [System.Diagnostics.EventLog]::CreateEventSource($eventSource, "Application")
    Write-Host "    Event source '$eventSource' created."
} else {
    Write-Host "    Event source '$eventSource' already exists."
}

# ---------------------------------------------------------------------------
# 5. Create the service
# ---------------------------------------------------------------------------
Write-Step "Creating Windows service '$ServiceName'..."
& sc.exe create $ServiceName `
    binPath= "`"$BinaryPath`"" `
    start=   auto `
    obj=     LocalSystem `
    DisplayName= "$ServiceDisplayName"
Confirm-Success $LASTEXITCODE "sc.exe create"

& sc.exe description $ServiceName "$ServiceDescription"
Confirm-Success $LASTEXITCODE "sc.exe description"

# Auto-restart on failure: restart after 5 s, 30 s, then 1 min.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000
Confirm-Success $LASTEXITCODE "sc.exe failure"

Write-Host "    Service created."

# ---------------------------------------------------------------------------
# 6. Lock down the install directory
#    Remove all inherited ACEs, then grant FullControl ONLY to
#    Administrators and SYSTEM.  Standard users get nothing.
# ---------------------------------------------------------------------------
Write-Step "Locking down directory permissions..."
$acl = Get-Acl -Path $InstallDir

# Disable inheritance and strip all inherited ACEs.
$acl.SetAccessRuleProtection($true, $false)

# Remove every existing rule (inherited or explicit).
foreach ($rule in @($acl.Access)) {
    $acl.RemoveAccessRule($rule) | Out-Null
}

$inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
$prop    = [System.Security.AccessControl.PropagationFlags]::None
$allow   = [System.Security.AccessControl.AccessControlType]::Allow

$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
    "BUILTIN\Administrators", "FullControl", $inherit, $prop, $allow))
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
    "NT AUTHORITY\SYSTEM",    "FullControl", $inherit, $prop, $allow))

Set-Acl -Path $InstallDir -AclObject $acl
Write-Host "    Directory ACL set: Administrators + SYSTEM only."

# ---------------------------------------------------------------------------
# 7. Lock down service control permissions via SDDL
#    Only SYSTEM (SY) and Administrators (BA) can start/stop/configure.
#    Standard users cannot even query the service control manager entry.
#
#    SDDL breakdown:
#      (A;;CCLCSWRPWPDTLOCRRC;;;SY)        - SYSTEM: full service rights
#      (A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA) - Admins: owner-level rights
# ---------------------------------------------------------------------------
Write-Step "Hardening service control permissions (SDDL)..."
$sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)"
& sc.exe sdset $ServiceName $sddl
Confirm-Success $LASTEXITCODE "sc.exe sdset"
Write-Host "    Service DACL applied: only SYSTEM and Administrators can control the service."

# ---------------------------------------------------------------------------
# 8. Start the service
# ---------------------------------------------------------------------------
Write-Step "Starting service..."
Start-Service -Name $ServiceName
$svc = Get-Service -Name $ServiceName
Write-Host "    Service status: $($svc.Status)"

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  MinecraftBlocker installed and running successfully."    -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Install directory : $InstallDir"
Write-Host "  Config file       : $InstallDir\appsettings.json"
Write-Host "  Event Log         : Application > Source = MinecraftBlocker"
Write-Host ""
Write-Host "  To edit the schedule, open appsettings.json as Administrator."
Write-Host "  Changes are picked up live - no service restart needed."
Write-Host ""
