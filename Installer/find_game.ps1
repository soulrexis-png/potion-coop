# Ищет папку игры во всех библиотеках Steam и печатает путь (пусто — не нашёл)
$name = 'A Place for Every Potion'
$roots = @()
foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
  try { $p = (Get-ItemProperty $k -ErrorAction Stop); if ($p.SteamPath) { $roots += $p.SteamPath }; if ($p.InstallPath) { $roots += $p.InstallPath } } catch {}
}
$libs = @()
foreach ($r in $roots | Select-Object -Unique) {
  $libs += $r
  $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
  if (Test-Path $vdf) {
    foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value -replace '\\\\', '\' }
  }
}
foreach ($l in $libs | Select-Object -Unique) {
  $g = Join-Path $l "steamapps\common\$name"
  if (Test-Path (Join-Path $g 'BepInEx\core')) { Write-Output $g; exit 0 }
}
foreach ($l in $libs | Select-Object -Unique) {
  $g = Join-Path $l "steamapps\common\$name"
  if (Test-Path $g) { Write-Output $g; exit 0 }
}
