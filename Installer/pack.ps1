param([string]$Game, [string]$Out, [switch]$Public)
# Собирает payload.zip для установщика из папки игры, где уже стоит рабочий BepInEx + мод
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Game = (Resolve-Path $Game).Path.TrimEnd('\')
if (Test-Path $Out) { Remove-Item $Out -Force }
$zip = [System.IO.Compression.ZipFile]::Open($Out, 'Create')
$count = 0
function Add-One($full) {
    $rel = $full.Substring($Game.Length).TrimStart('\').Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $full, $rel, 'Optimal')
    $script:count++
}
function Add-File($rel) {
    $full = Join-Path $Game $rel
    if (Test-Path -LiteralPath $full -PathType Leaf) { Add-One (Get-Item -LiteralPath $full -Force).FullName }
    else { Write-Host "  нет файла: $rel" -ForegroundColor Yellow }
}
function Add-Dir($rel) {
    $root = Join-Path $Game $rel
    if (!(Test-Path -LiteralPath $root)) { Write-Host "  нет папки: $rel" -ForegroundColor Yellow; return }
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { Add-One $_.FullName }
}
Add-File 'winhttp.dll'
Add-File 'doorstop_config.ini'
Add-File '.doorstop_version'
Add-Dir  'dotnet'
Add-Dir  'BepInEx\core'
if (-not $Public) {
    # только для своих: в публичный установщик НЕ кладём — это производные от файлов игры и Unity,
    # BepInEx сам их сделает при первом запуске (дольше, нужен интернет)
    Add-Dir  'BepInEx\unity-libs'   # библиотеки Unity — друзьям не нужен VPN
    Add-Dir  'BepInEx\interop'      # готовые обёртки — первый запуск без долгой генерации
} else { Write-Host '  публичная сборка: без interop и unity-libs' }
Add-File 'BepInEx\plugins\PotionCoop.dll'
Add-File 'BepInEx\plugins\PotionCoop.gamehash'   # отпечаток версии игры
$zip.Dispose()
$mb = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "  упаковано файлов: $count, размер: $mb МБ"
