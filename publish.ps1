# AdbManager 一键发布脚本：产出单文件独立 exe（含 WinUI 运行时与内置谷歌官方 adb）
# 用法: powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root 'src\AdbManager\AdbManager.csproj'
$outDir  = Join-Path $root 'dist'

Write-Host "==> 发布 $project"
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $outDir 'AdbManager.exe'
Write-Host ""
Write-Host "==> 完成: $exe ($([math]::Round((Get-Item $exe).Length / 1MB)) MB)"
Write-Host "    双击即可运行：内置官方 adb 会在首次运行时释放到 %LOCALAPPDATA%\AdbManager\adb\"
