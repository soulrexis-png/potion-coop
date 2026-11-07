@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem === Папка игры. Если у тебя она в другом месте — поменяй только эту строку ===
set "GAME=E:\SteamLibrary\steamapps\common\A Place for Every Potion"

tasklist /fi "imagename eq A Place for Every Potion.exe" | find /i "A Place for Every Potion.exe" >nul && (
  echo Закрой игру и запусти ещё раз.
  pause
  exit /b 1
)

echo [1/3] Собираю мод...
dotnet build PotionCoop -c Release || goto :err

echo.
echo [2/3] Упаковываю загрузчик и мод из папки игры...
powershell -NoProfile -ExecutionPolicy Bypass -File "Installer\pack.ps1" -Game "%GAME%" -Out "Installer\payload.zip" -Public || goto :err

echo.
echo [3/3] Собираю установщик (первый раз дольше — качается .NET для упаковки)...
dotnet publish Installer -c Release -o "%~dp0out" || goto :err
copy /y "out\PotionCoopSetup.exe" "%~dp0PotionCoopSetup_public.exe" >nul

echo.
echo ГОТОВО: PotionCoopSetup_public.exe — для публикации на GitHub (без файлов игры).
pause
exit /b 0

:err
echo.
echo ОШИБКА — скинь скрин этого окна.
pause
exit /b 1
