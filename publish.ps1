# LunchBox 配布用ビルド（自己完結・単一exe）＋インストーラ作成
# 実行: PowerShell で  .\publish.ps1
# インターネット接続が必要（初回に .NET ランタイムを取得するため）
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'LunchBox.csproj'
$dist = Join-Path $root 'dist'

$dotnet = 'dotnet'
if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $dist `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'publish に失敗しました。' }

$exe = Join-Path $dist 'LunchBox.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "単一exe: $exe ($size MB)" -ForegroundColor Green

# インストーラ（Inno Setup 6 が入っている場合だけ）
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  & $iscc (Join-Path $root 'installer\LunchBox.iss')
  if ($LASTEXITCODE -ne 0) { throw 'インストーラの作成に失敗しました。' }
  Write-Host 'インストーラ: installer\Output\' -ForegroundColor Green
} else {
  Write-Host 'Inno Setup 6 が見つからないので、インストーラは作りませんでした。' -ForegroundColor Yellow
}
