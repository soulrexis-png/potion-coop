# PotionCoop — online co-op for *A Place for Every Potion*

Play **A Place for Every Potion** together with friends over the internet, through Steam. Up to 6 players in one shared pharmacy.

> **Unofficial mod**, made by a fan, with the developer's permission. Please report problems here (Issues), not to the developer.
> The mod does not include any game files: it works only on top of your own installed copy of the game.

**[Русская версия ниже ↓](#русский)**

## What you get

- **Shared pharmacies.** The host opens a pharmacy (a separate save), friends from the host's Steam friends list join it. No ports, no IP addresses, no LAN needed.
- **Up to 6 players.** You see the other apothecaries, each with their own colour (12 to choose from), name tag, hands and emotes.
- **Everything is synced.** Grabbing, throwing, shelving, pouring, vats, beakers on holders, the cat. You can even steal a potion from someone's hands (there is a steal counter).
- **Shared progress.** XP is shared and counted by the host. World skills (vats, separator) are bought by the host, personal skills (hands, apron, sprint…) are kept for each player.
- **Your own game stays yours.** Co-op lives only in pharmacies. *Continue* and *New game* in the main menu always open your normal solo save.
- **Apron on the hook.** If someone leaves, their potions hang where they appeared in the lab; others can take them, and when the player comes back, the untouched ones jump back into their hands.
- **Fun difficulty modifiers**, on by default: the earthquake, fragile flasks, the leaky pocket, fatigue cured by petting the cat. If something goes wrong, the host presses **F8** in the lab to switch all modifiers off for everyone (F8 again turns them back on). Each one can also be switched off for good in `BepInEx\config\roma.potioncoop.cfg` (section `[Modifiers]`).
- **Emote wheel** (T) and **"I'm here" ping** (middle mouse / F2), with an arrow at the screen edge. The wheel also has a **Letter** with all the keys and rules.
- **Russian and English:** the mod's messages follow the game's language.
- **One-file installer.** Logs are collected with one key (F11) or from the installer.

## Install

1. Download `PotionCoopSetup.exe` from [Releases](../../releases).
2. Run it and press **1** (install / update). It finds the game in Steam by itself. The installer and the mod's texts are in Russian for now.
3. Start the game. The **first start takes a few minutes**: the mod loader (BepInEx) prepares itself and downloads the Unity libraries. Wait until the main menu appears.

All players need the same game version and the same mod version. The mod checks the game version: after a game update it switches itself off and tells you so, until a new mod version is out.

The installer can also **restore a backup** of your solo save (the mod makes one every time you start the game), **collect logs** and **uninstall** the mod.

## What the installer changes (and how to undo it)

**In the game folder** the installer adds the mod loader and the mod. The game's own files are not modified.

| What | Why |
| --- | --- |
| `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | start the mod loader together with the game |
| `dotnet\` | the runtime the loader needs |
| `BepInEx\` | the loader itself (BepInEx 6), its settings and logs; the mod is `BepInEx\plugins\PotionCoop.dll` |

On the first start the loader also creates `BepInEx\interop` and `BepInEx\unity-libs` (that is why the first start takes a few minutes).

**Your saves** live where the game keeps them: `%USERPROFILE%\AppData\LocalLow\CubeQuad\A Place for Every Potion`. The mod adds its own folders next to them and never writes co-op progress into your solo save:

| Folder | What is inside |
| --- | --- |
| `PotionCoop_rooms\<name>\` | each shared pharmacy: its save, players' skills and colours, steal counter |
| `PotionCoop_rooms\_удалённые\` | pharmacies you deleted from the menu (move a folder back to restore it) |
| `PotionCoop_guest\` | a temporary copy of the host's pharmacy while you are a guest |
| `PotionCoop_mine\` | a copy of your personal skills in other people's pharmacies |
| `PotionCoop_backup\` | a copy of your solo save, made every time you start the game (the last 10 are kept) |

Game settings (language, graphics, controls) and overall statistics always stay yours, in every pharmacy.

**Undo anything:**
- **Restore your solo save:** run the installer → **3** → choose a copy by date. Your current save is copied aside first, so you can go back.
- **Remove the mod:** run the installer → **2**. It deletes the loader and the mod; the game is exactly as before. Saves and pharmacies are left alone (delete the `PotionCoop_*` folders by hand if you want).
- **Last resort:** Steam → right-click the game → Properties → Installed Files → *Verify integrity of game files*, then delete the files listed above.

## Play

- **Host:** main menu → **Shared pharmacies** → **+ New pharmacy** (type a name) or **+ New pharmacy from my save**, or open one you already have.
- **Join:** when a friend has opened a pharmacy, the button shows **Join: Name · «pharmacy»**.
- Switching to a pharmacy restarts the game once (Steam starts it again by itself).

| Key | What it does |
| --- | --- |
| T (hold) | emote wheel |
| Middle mouse / F2 | "I'm here" ping |
| F9 | test dummy (to try things alone) |
| F10 | ping to the other players |
| F11 | collect logs into a zip on the Desktop |

## Known limits

- The installer is in Russian for now; in the game the mod speaks Russian or English, following the game's language.
- Opening a different pharmacy restarts the game.
- Tested on game version 1.3.4.

## Build from source

You need Windows, the .NET 6 SDK and the game with BepInEx 6 (be.791, IL2CPP) started at least once.

1. Set the game folder in `PotionCoop/PotionCoop.csproj` (`GameDir`) and in `make_installer_public.bat` (`GAME`).
2. Run `make_installer_public.bat`. It builds the mod and `PotionCoopSetup_public.exe`, which contains the loader and the mod but no files derived from the game.

## Support

The mod is free and will stay free. If you want to support the work (next up: a competitive mode with teams), you can do it here: [DonatePay](https://donatepay.ru/don/1087772).

## License

[MIT](LICENSE). The installer also ships third-party software (BepInEx and others), see [THIRD-PARTY.md](THIRD-PARTY.md). The game *A Place for Every Potion* belongs to CubeQuad Games; this project is not affiliated with them.

---

## Русский

**PotionCoop** — кооп через интернет для *A Place for Every Potion*, через Steam, до 6 игроков в одной общей аптеке.

> **Неофициальный мод**, сделан фанатом с разрешения разработчика. О проблемах пишите сюда (Issues), а не разработчику. Файлов игры в моде нет: он работает только поверх вашей установленной игры.

**Что есть**
- Общие аптеки — отдельные сейвы для коопа. Хост открывает, друзья из Steam заходят. Никаких портов и IP.
- До 6 игроков: у каждого свой цвет (12 на выбор), ник, руки, эмоции.
- Синхронно всё: взять, бросить, поставить, перелить, чаны, мензурки на подставках, кот. Можно отобрать склянку из рук (есть счёт краж).
- Опыт общий, его считает хост. Навыки мира покупает хост, личные навыки у каждого свои.
- Своя игра остаётся своей: «Продолжить» и «Новая игра» всегда открывают ваш обычный сейв.
- «Фартук на крючке»: ушёл — склянки висят там, где ты появился; вернулся — нетронутые прыгают обратно в руки.
- Модификаторы для веселья, включены по умолчанию: землетрясение, хрупкие склянки, дырявый карман, переутомление (лечится котом). Если что-то забаговалось, хост жмёт **F8** в лаборатории — модификаторы выключаются у всех (ещё раз F8 — включить). Насовсем любой выключается в `BepInEx\config\roma.potioncoop.cfg` (раздел `[Modifiers]`).
- Колесо эмоций (T), «я тут» (колёсико мыши / F2). В колесе есть «Письмо» — инструкция со всеми клавишами и правилами.
- Русский и английский: сообщения мода — на языке игры.

**Установка:** скачать `PotionCoopSetup.exe` из [Releases](../../releases), запустить, нажать 1 («Установить / обновить»). Первый запуск игры — несколько минут: загрузчик модов готовится и скачивает библиотеки Unity. Если в вашей сети не скачивается — нужен VPN на первый запуск.

**Что ставится и как всё вернуть**
- В папку игры: `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, папки `dotnet\` и `BepInEx\` (загрузчик модов BepInEx 6, сам мод — `BepInEx\plugins\PotionCoop.dll`). При первом запуске загрузчик сам создаст `BepInEx\interop` и `BepInEx\unity-libs`. Файлы игры не меняются.
- Сейвы — там же, где у игры: `%USERPROFILE%\AppData\LocalLow\CubeQuad\A Place for Every Potion`. Мод кладёт рядом свои папки и в ваш личный сейв кооп не пишет:
  - `PotionCoop_rooms\<название>\` — общие аптеки (сейв, навыки и цвета игроков, кражи);
  - `PotionCoop_rooms\_удалённые\` — удалённые из меню аптеки (перетащите папку обратно — вернётся);
  - `PotionCoop_guest\` — временная копия аптеки хоста, пока вы в гостях;
  - `PotionCoop_mine\` — копия ваших личных навыков в чужих аптеках;
  - `PotionCoop_backup\` — копия вашего личного сейва при каждом запуске игры (последние 10).
- Настройки игры (язык, графика, управление) и общая статистика — всегда ваши, в любой аптеке.
- **Вернуть личный сейв:** установщик → 3 → выбрать копию по дате (текущий сейв перед этим тоже сохраняется).
- **Удалить мод:** установщик → 2 — загрузчик и мод удаляются, игра как была. Сейвы и аптеки не трогаются (папки `PotionCoop_*` можно удалить руками).
- **Крайний случай:** Steam → игра → Свойства → Установленные файлы → «Проверить целостность файлов игры», затем удалить файлы из списка выше.

**Игра:** главное меню → «Общие аптеки» → «+ Новая аптека» / «+ Новая аптека из моего сейва» / открыть свою. Друзья видят кнопку «Зайти: Ник · «аптека»». Переход в аптеку один раз перезапускает игру.

Поддержать разработку (мод бесплатный и останется бесплатным): [DonatePay](https://donatepay.ru/don/1087772).

Логи — F11 в игре или пункт в установщике. Резервная копия личного сейва делается при каждом запуске и восстанавливается установщиком.
