<#
  Intune Win32 (or manual) installer for the Soft Phone portable build.

  Deploys the app to Program Files (all users), adds Start Menu + all-users Startup
  shortcuts, and launches it in the tray for the current user. The app's single-instance
  guard makes any duplicate autostart harmless.

  Packaging: place the portable build (CrestApps.SoftPhone.exe + Assets\) next to this
  script, then wrap the folder with the Microsoft Win32 Content Prep Tool:
    IntuneWinAppUtil.exe -c <thisFolder> -s CrestApps.SoftPhone.exe -o <out>
  Intune install command:   powershell.exe -ExecutionPolicy Bypass -File Install.ps1
  Intune uninstall command: powershell.exe -ExecutionPolicy Bypass -File Uninstall.ps1
#>
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$installDir = Join-Path $env:ProgramFiles 'CrestApps\SoftPhone'
$exeName = 'CrestApps.SoftPhone.exe'

Write-Host "Installing Soft Phone to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Copy payload (exe + Assets), skip the deployment scripts themselves.
Copy-Item (Join-Path $source $exeName) $installDir -Force
if (Test-Path (Join-Path $source 'Assets')) {
    Copy-Item (Join-Path $source 'Assets') $installDir -Recurse -Force
}

$exePath = Join-Path $installDir $exeName

# WScript.Shell shortcut helper.
function New-Shortcut([string]$linkPath, [string]$target, [string]$args) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($linkPath)
    $sc.TargetPath = $target
    $sc.Arguments = $args
    $sc.WorkingDirectory = (Split-Path $target)
    $sc.IconLocation = $target
    $sc.Save()
}

# Start Menu (all users).
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
New-Shortcut (Join-Path $startMenu 'Soft Phone.lnk') $exePath ''

# All-users Startup → launches minimized to the tray at each logon.
$startup = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\StartUp'
New-Shortcut (Join-Path $startup 'Soft Phone.lnk') $exePath '--tray'

Write-Host 'Install complete.'
