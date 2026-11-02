# One-click updater shared by all of Rubirosa's apps.
# The app's "Update now" button opens  jrtapp://update/<app>  and Windows runs this script.
# It downloads the latest copy of that app from GitHub and runs its installer in update mode.
# Your data is never touched: it lives in %LOCALAPPDATA%\<App>, which installers only add files to.
param([string]$Uri = '')

$ErrorActionPreference = 'Stop'
$Owner = 'Jrtorres13'
# Only these apps can be updated, so a web page can't make this script fetch anything else.
$Apps = @{
  'morning-brief' = 'Morning Brief'
  'groundwork'    = 'Groundwork'
  'jobsite-crm'   = 'JobSite CRM'
  'hot-seat'      = 'Hot Seat'
  'pocketdeck'    = 'PocketDeck'
}

$Host.UI.RawUI.WindowTitle = 'Updating...'
function Fail($msg) {
  Write-Host ''
  Write-Host "Update did not finish: $msg" -ForegroundColor Red
  Write-Host 'Nothing was removed; your app and data are as they were. Send this text to Claude.'
  Read-Host 'Press Enter to close'
  exit 1
}

if ($Uri -notmatch '^jrtapp:/*update/([a-z0-9-]+)/?$') { Fail "unexpected link '$Uri'" }
$app = $Matches[1]
if (-not $Apps.ContainsKey($app)) { Fail "unknown app '$app'" }
$name = $Apps[$app]
$Host.UI.RawUI.WindowTitle = "Updating $name..."
Write-Host "Updating $name from github.com/$Owner/$app ..." -ForegroundColor Cyan

$work = Join-Path $env:TEMP ("jrtapp-" + $app + "-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
  New-Item -ItemType Directory -Force -Path $work | Out-Null
  $zip = Join-Path $work 'latest.zip'
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  try {
    Invoke-WebRequest -UseBasicParsing -Uri "https://codeload.github.com/$Owner/$app/zip/refs/heads/main" -OutFile $zip
  } catch { Fail "could not download the new version (no internet, or the repo is private). $($_.Exception.Message)" }

  Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
  $dir = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
  $installer = if ($dir) { Join-Path $dir.FullName 'install.ps1' } else { '' }
  if (-not $installer -or -not (Test-Path -LiteralPath $installer)) { Fail 'the download has no install.ps1' }
  Get-ChildItem -LiteralPath $dir.FullName -Recurse | Unblock-File -ErrorAction SilentlyContinue

  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Update
  if ($LASTEXITCODE -ne 0) { Fail "the installer stopped with code $LASTEXITCODE" }
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "$name is up to date. The app window refreshes by itself." -ForegroundColor Green
Start-Sleep -Seconds 3
