# PocketDeck installer: builds the small PC app with the C# compiler built into Windows,
# copies it to your user folder, adds shortcuts and starts it. No downloads needed.
# -Update: run by the one-click updater; skips opening the app (its window refreshes itself).
param([switch]$Update)
$ErrorActionPreference = 'Stop'
$src  = Join-Path $PSScriptRoot 'app'
$dest = Join-Path $env:LOCALAPPDATA 'PocketDeck'
if (-not (Test-Path -LiteralPath (Join-Path $src 'PocketDeck.cs'))) {
  throw 'Unzip the download first (right-click > Extract All), then run Install.bat from the unzipped folder.'
}

# Stop a running copy so files can be replaced.
Get-Process PocketDeck -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Get-ChildItem -LiteralPath $src -Recurse | Unblock-File -ErrorAction SilentlyContinue
foreach ($f in 'index.html','icon.ico','icon.png','PocketDeck.cs') { Copy-Item (Join-Path $src $f) $dest -Force }

# Find csc.exe (ships with .NET Framework 4.x on every Windows 10/11 PC).
$csc = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
         "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw '.NET Framework 4 was not found. Turn it on in Windows Features, then run Install.bat again.' }

$exe = Join-Path $dest 'PocketDeck.exe'
Write-Host 'Building PocketDeck...'
& $csc /nologo /optimize+ /target:winexe "/out:$exe" "/win32icon:$(Join-Path $dest 'icon.ico')" `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll (Join-Path $dest 'PocketDeck.cs')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw 'Build failed. Send the text above to Claude.' }

# Let the phone reach the PC on home (Private) networks. Needs one admin "Yes".
Write-Host ''
$null = netsh advfirewall firewall show rule name=PocketDeck; $hasRule = ($LASTEXITCODE -eq 0)
if ($Update -and $hasRule) {
  Write-Host 'Firewall rule already in place.'
} else {
Write-Host 'Windows will ask for permission once so your phone can connect. Click Yes.' -ForegroundColor Yellow
$rule = "netsh advfirewall firewall delete rule name=PocketDeck >nul 2>&1 & netsh advfirewall firewall add rule name=PocketDeck dir=in action=allow program=`"$exe`" enable=yes profile=private,domain"
try { Start-Process cmd.exe -ArgumentList "/c $rule" -Verb RunAs -WindowStyle Hidden -Wait }
catch { Write-Host 'Skipped. If Windows asks about the firewall later, click Allow.' -ForegroundColor Yellow }
}

# Shortcuts + start at sign-in.
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs')) {
  $lnk = $shell.CreateShortcut((Join-Path $dir 'PocketDeck.lnk'))
  $lnk.TargetPath = $exe; $lnk.WorkingDirectory = $dest
  $lnk.IconLocation = (Join-Path $dest 'icon.ico'); $lnk.Description = 'Phone Stream Deck for this PC'
  $lnk.Save()
}
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PocketDeck' -Value "`"$exe`""

Write-Host ''
Write-Host 'PocketDeck is installed.' -ForegroundColor Green
Write-Host '- It runs in the tray (bottom-right, near the clock) and starts when you sign in.'
Write-Host '- A browser page opens now: scan the QR code with your phone.'
Write-Host ''
# One-click updates: the app's "Update now" button opens jrtapp://update/<app>, which runs updater.ps1.
$updDir = Join-Path $env:LOCALAPPDATA 'JrtApps'
New-Item -ItemType Directory -Force -Path $updDir | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'updater.ps1') -Destination $updDir -Force
$cls = 'HKCU:\Software\Classes\jrtapp'
foreach ($k in $cls, "$cls\shell", "$cls\shell\open", "$cls\shell\open\command") { if (-not (Test-Path $k)) { New-Item -Path $k | Out-Null } }
Set-ItemProperty -Path $cls -Name '(default)' -Value 'URL:App updates'
Set-ItemProperty -Path $cls -Name 'URL Protocol' -Value ''
Set-ItemProperty -Path "$cls\shell\open\command" -Name '(default)' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$updDir\updater.ps1`" `"%1`""
if ($Update) { Start-Process $exe } else { Start-Process $exe -ArgumentList '--connect' }
