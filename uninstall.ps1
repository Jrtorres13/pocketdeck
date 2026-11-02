# Removes PocketDeck, its shortcuts, auto-start and firewall rule. Optionally deletes your keys.
Get-Process PocketDeck -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($dir in [Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs')) {
  Remove-Item (Join-Path $dir 'PocketDeck.lnk') -ErrorAction SilentlyContinue
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PocketDeck' -ErrorAction SilentlyContinue
try { Start-Process cmd.exe -ArgumentList '/c netsh advfirewall firewall delete rule name=PocketDeck' -Verb RunAs -WindowStyle Hidden -Wait } catch {}
$dest = Join-Path $env:LOCALAPPDATA 'PocketDeck'
Start-Sleep -Milliseconds 400
Get-ChildItem -LiteralPath $dest -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$ans = Read-Host 'Also delete your saved keys and paired phones? (y/N)'
if ($ans -match '^[yY]') { Remove-Item -LiteralPath $dest -Recurse -Force -ErrorAction SilentlyContinue; Write-Host 'Data deleted.' }
Write-Host 'PocketDeck removed.' -ForegroundColor Green
