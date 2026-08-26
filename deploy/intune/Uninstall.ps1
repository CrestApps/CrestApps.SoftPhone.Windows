<#
  Uninstaller for the Soft Phone portable Intune app. Stops the app, removes the
  Program Files install and the Start Menu / Startup shortcuts. Leaves per-user settings
  and the WebView2 profile (user data) intact; delete %LocalAppData%\CrestApps\SoftPhone
  to remove those too.
#>
$ErrorActionPreference = 'SilentlyContinue'
$installDir = Join-Path $env:ProgramFiles 'CrestApps\Soft Phone'

Get-Process -Name 'CrestApps.SoftPhone' | Stop-Process -Force
Start-Sleep -Seconds 1

Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Soft Phone.lnk') -Force
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\StartUp\Soft Phone.lnk') -Force
Remove-Item $installDir -Recurse -Force

# Best-effort: remove any per-user autostart Run key the app registered.
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'CrestAppsSoftPhone' -ErrorAction SilentlyContinue

Write-Host 'Uninstall complete.'
