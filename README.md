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
- **Fun difficulty modifiers**, on by default: the earthquake, fragile flasks, the leaky pocket, the colour-blind spirit (a minute of mixed-up colours), fatigue cured by petting the cat. The host presses **F8** in the lab to open the modifiers menu and switch each one (or all) on or off. Each one can also be switched off for good in `BepInEx\config\roma.potioncoop.cfg` (section `[Modifiers]`).
- **Emote wheel** (T) and **"I'm here" ping** (G / F2), with an arrow at the screen edge. The wheel also has a **Letter** with all the keys and rules.
- **11 languages:** the mod's messages follow the game's language (English, Français, Deutsch, Español, Português, Italiano, Українська, Русский, 简体中文, 日本語, 한국어).
- **Competition mode.** When creating a pharmacy, choose *Together* or *Competition*: 2–6 teams, the host picks the team count, team size and colours; extra players become spectators, and the host can play or be an arbiter. Every team colour owns two potion families (table below). Your own zone is worth 1000 points by the share of correctly filled cells. The common pool is random and revealed at the start, so it cannot be prepared in advance; it is worth 500 points in total and opens for a team only when its own zone is complete. The game ends when the commons are taken or when nobody can catch the leader. **H** — shove a nearby player (charges with correct shelvings).
- **No restarts.** Opening or joining a pharmacy happens right in the menu (falls back to a restart if something goes wrong).
- **One-file installer.** Logs are collected with one key (F11) or from the installer.

## Install

1. Download `PotionCoopSetup.exe` from [Releases](../../releases).
2. Run it and press **1** (install / update). It finds the game in Steam by itself. The installer speaks English or Russian, following your Windows language.
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
| `PotionCoop_backup_rooms\<name>\` | a copy of a shared pharmacy, made every time its host opens it (the last 5 are kept) |

Game settings (language, graphics, controls) and overall statistics always stay yours, in every pharmacy.

**Undo anything:**
- **Restore your solo save:** run the installer → **3** → choose a copy by date. Your current save is copied aside first, so you can go back.
- **Remove the mod:** run the installer → **2**. It deletes the loader and the mod; the game is exactly as before. Saves and pharmacies are left alone (delete the `PotionCoop_*` folders by hand if you want).
- **Last resort:** Steam → right-click the game → Properties → Installed Files → *Verify integrity of game files*, then delete the files listed above.

## Play

- **Host:** main menu → **Shared pharmacies** → **+ New pharmacy** (type a name) or **+ New pharmacy from my save**, or open one you already have.
- **Join:** when a friend has opened a pharmacy, the button shows **Join: Name · «pharmacy»**.
- Opening or joining a pharmacy happens right in the menu; if that ever fails, the game asks to restart once.

| Key | What it does |
| --- | --- |
| T (hold) | emote wheel |
| G / F2 | "I'm here" ping |
| F9 | test dummy (to try things alone) |
| F10 | ping to the other players |
| F11 | collect logs into a zip on the Desktop |

## Competition: team colours

| Team colour | Potion families |
| --- | --- |
| Red | Vitality + Might |
| Yellow | Swiftness + Fortune |
| Green | Venoms + Vigor |
| Teal | Veils + Wards |
| Blue | Arcana + Curses |
| Purple | Sorcery + Charms |

Common pool: Remedy plus the families of colours nobody picked — 4 cells per team (8 in the whole-lab mode). The common pads blink white and pink.

## Privacy and safety

- The mod talks only to your **Steam friends** who run the game with the mod, over Steam's own networking. No servers, ports or IP addresses.
- Game messages (moves, positions, shoves, experience) are sent and accepted only **between members of the same pharmacy**: the host and the friends who joined it.
- **Logs (F11)** go into a zip on your Desktop. When someone in your pharmacy presses F11, your log is sent to them too — you see a notice, it happens at most once per 30 s, and nobody outside your pharmacy can ask for it. In the logs your Windows user name, Steam IDs and the beginning of file paths are masked; public Steam nicknames stay.
- A host's save is checked before it touches your disk (size, file count, plain file names) and goes into a separate folder; your solo save is never overwritten and is backed up at every start.
- In a pharmacy the host's fairness settings apply to everyone (extra hand and apron slots, shove charge), whatever a guest has in their own config.
- The installer is not code-signed, so Windows SmartScreen may warn. Compare the file with the SHA-256 published in the release. It needs no administrator rights and only writes into the game folder.
- Join pharmacies of people you trust: a host decides what save you load, and a guest's moves are written into the host's pharmacy (it is backed up on every opening).


## Known limits

- The mod speaks English or Russian: the installer follows your Windows language, the game texts follow the game's language.
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
- Модификаторы для веселья, включены по умолчанию: землетрясение, хрупкие склянки, дырявый карман, дальтоник-дух (минута перепутанных цветов), переутомление (лечится котом). Хост жмёт **F8** в лаборатории — меню модификаторов: каждый или все сразу можно включить и выключить. Насовсем любой выключается в `BepInEx\config\roma.potioncoop.cfg` (раздел `[Modifiers]`).
- Соревнование: при создании аптеки — «Общая игра» или «Соревнование». 2–6 команд; хост выбирает число команд, размер и цвета, лишние игроки — зрители, хост играет или судит (арбитр). У каждого цвета команды — два своих семейства зелий (таблица ниже). Своя зона — 1000 очков по доле верно заполненных ячеек. Общие — случайные, открываются на старте (заранее не набрать), вместе стоят 500 очков и доступны команде только после своей зоны. Конец — когда общие разобраны или лидера уже не догнать. **H** — толкнуть игрока рядом (заряд копится за верно поставленные склянки).
- Без перезапусков: открыть аптеку и зайти к другу — прямо в меню.
- Колесо эмоций (T), «я тут» (G / F2). В колесе есть «Письмо» — инструкция со всеми клавишами и правилами.
- 11 языков: сообщения мода — на языке игры (English, Français, Deutsch, Español, Português, Italiano, Українська, Русский, 简体中文, 日本語, 한국어).

**Установка:** скачать `PotionCoopSetup.exe` из [Releases](../../releases), запустить, нажать 1 («Установить / обновить»). Первый запуск игры — несколько минут: загрузчик модов готовится и скачивает библиотеки Unity. Если в вашей сети не скачивается — нужен VPN на первый запуск.

**Что ставится и как всё вернуть**
- В папку игры: `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, папки `dotnet\` и `BepInEx\` (загрузчик модов BepInEx 6, сам мод — `BepInEx\plugins\PotionCoop.dll`). При первом запуске загрузчик сам создаст `BepInEx\interop` и `BepInEx\unity-libs`. Файлы игры не меняются.
- Сейвы — там же, где у игры: `%USERPROFILE%\AppData\LocalLow\CubeQuad\A Place for Every Potion`. Мод кладёт рядом свои папки и в ваш личный сейв кооп не пишет:
  - `PotionCoop_rooms\<название>\` — общие аптеки (сейв, навыки и цвета игроков, кражи);
  - `PotionCoop_rooms\_удалённые\` — удалённые из меню аптеки (перетащите папку обратно — вернётся);
  - `PotionCoop_guest\` — временная копия аптеки хоста, пока вы в гостях;
  - `PotionCoop_mine\` — копия ваших личных навыков в чужих аптеках;
  - `PotionCoop_backup\` — копия вашего личного сейва при каждом запуске игры (последние 10);
  - `PotionCoop_backup_rooms\<название>\` — копия общей аптеки при каждом её открытии хостом (последние 5).
- Настройки игры (язык, графика, управление) и общая статистика — всегда ваши, в любой аптеке.
- **Вернуть личный сейв:** установщик → 3 → выбрать копию по дате (текущий сейв перед этим тоже сохраняется).
- **Удалить мод:** установщик → 2 — загрузчик и мод удаляются, игра как была. Сейвы и аптеки не трогаются (папки `PotionCoop_*` можно удалить руками).
- **Крайний случай:** Steam → игра → Свойства → Установленные файлы → «Проверить целостность файлов игры», затем удалить файлы из списка выше.

**Игра:** главное меню → «Общие аптеки» → «+ Новая аптека» / «+ Новая аптека из моего сейва» / открыть свою. Друзья видят кнопку «Зайти: Ник · «аптека»». Переход в аптеку — прямо в меню, без перезапуска.

**Соревнование: цвета команд**

| Цвет команды | Семейства |
| --- | --- |
| Красные | Жизненная сила + Мощь |
| Жёлтые | Стремительность + Удача |
| Зелёные | Яды + Бодрость |
| Бирюзовые | Завесы + Обереги |
| Синие | Аркана + Проклятия |
| Фиолетовые | Колдовство + Чары |

Общие: Исцеление и семейства цветов, которые никто не выбрал, — 4 ячейки на команду (в режиме «Вся лаборатория» — 8). Общие подложки мигают белым и розовым.

**Приватность и безопасность**
- Мод общается только с вашими **друзьями в Steam**, у которых запущена игра с модом, через сеть самого Steam. Никаких серверов, портов и IP.
- Игровые сообщения (ходы, положение, толчки, опыт) шлются и принимаются только **между участниками одной аптеки**: хост и те, кто к нему зашёл.
- **Логи (F11)** собираются в zip на рабочем столе. Когда кто-то из вашей аптеки жмёт F11, ваш лог тоже уходит ему: вы видите плашку, это бывает не чаще раза в 30 с, и никто вне вашей аптеки запросить логи не может. В логах скрыты имя пользователя Windows, Steam ID и начало путей; публичные ники Steam остаются.
- Сейв хоста проверяется до записи на диск (размер, число файлов, простые имена) и кладётся в отдельную папку; ваш личный сейв не затирается и копируется при каждом запуске.
- В аптеке у всех действуют настройки честности хоста (доп. места в руке и фартуке, заряд толчка), что бы ни стояло в конфиге гостя.
- Установщик не подписан, поэтому Windows SmartScreen может предупредить. Сверьте файл с SHA-256 из релиза. Права администратора не нужны, пишет он только в папку игры.
- Заходите в аптеки к тем, кому доверяете: хост решает, какой сейв вы загрузите, а ходы гостя пишутся в аптеку хоста (её копия делается при каждом открытии).

Поддержать разработку (мод бесплатный и останется бесплатным): [DonatePay](https://donatepay.ru/don/1087772).

Логи — F11 в игре или пункт в установщике. Резервная копия личного сейва делается при каждом запуске и восстанавливается установщиком.
