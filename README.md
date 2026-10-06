# Pizza Hero Clicker

A Windows auto clicker and macro tool by Pizza Hero Gaming. It clicks, presses keys, scrolls,
drags, waits for on-screen colours, and can record what you do and play it back. Everything is
driven by global hotkeys, so it works while a game has focus.

Built with C# / .NET 8 and WPF. Ships as a single self-contained `.exe` (no .NET install needed).

> **Heads up:** some games and anti-cheat systems block or penalise automated input. Check a
> game's rules before using this with it.

## Features

**Clicking**

- Interval entered as hours / minutes / seconds / milliseconds, down to 1 ms.
- Plain mode: with an empty action list it clicks wherever the cursor is.
- Left / right / middle button, single or double click, click-and-hold.
- Random interval range, position jitter (± px), smooth cursor glide to each target.
- Burst mode: N actions, pause, repeat.
- Stop automatically after N actions, N passes through the list, a duration, or at a clock time.
- Start countdown (default 3 s).

**Action list**

Every profile is an ordered list of actions, run in sequence (looping) or in random order.
Each action can override the global interval with its own delay or random delay range.

| Action      | What it does                                                              |
| ----------- | ------------------------------------------------------------------------- |
| Click       | Click at a position or at the cursor; button, single/double, hold time    |
| Key         | A key, a combo (`Ctrl+C`), or a sequence of them, with hold time          |
| Wait        | Pause for N ms                                                            |
| Scroll      | Wheel up / down / left / right by N notches                               |
| Drag        | Press at A, glide to B over N ms, release                                 |
| Move        | Move or glide the cursor without clicking                                 |
| Pixel wait  | Wait until a screen pixel matches a colour (tolerance, timeout)           |
| Pixel click | Same, then click (the pixel itself or another position)                   |
| Area watch  | Watch a rectangle; click things that appear in it, button chosen by what each looks like (picture or colour) |

Add, edit, duplicate, delete, enable/disable, and drag rows to reorder. Positions can be typed,
picked by clicking on screen, or captured with a hotkey. Pixel actions have an eyedropper with a
magnifier.

**Record and playback**

Record real mouse and keyboard input, then get it back as ordinary editable actions with the
original timing. Options: record mouse movement or clicks only, trim long pauses, playback speed
multiplier.

**Profiles**

Named profiles, grouped by game: create, save, revert, rename, move between games, duplicate,
delete, import, export. A game can hold as many profiles as you like, and different games can
reuse the same profile names. The last profile used is loaded on start. Each profile keeps its own hotkeys, target window and
overlay options, and can have a quick-switch hotkey.

**Windows, tray, overlays**

- Target a window: run only while it is focused (auto-pause / auto-resume), with optional
  window-relative positions that survive the window being moved.
- System tray: minimize to tray, start minimized, tray menu with start/stop and profile switching.
- Small always-on-top ON/OFF indicator (click-through, or unlock it to drag it).
- Numbered markers drawn at every saved position, on every monitor.
- Live stats: clicks this run and this session, measured clicks per second, time to next action.
- Multi-monitor, negative coordinates and per-monitor DPI scaling are handled throughout.

**Safety**

- Emergency stop hotkey.
- Emergency stop by pushing the mouse into an outer screen corner (can be turned off per profile).
- Inputs are validated before a run starts; the reason is shown in the footer.
- It never clicks its own window: if the cursor is on the app, the run waits until it moves away.

## How to use

The same instructions are on the **How to use** tab inside the app. The **About** tab shows the
version, credits and where this copy keeps its data.

**Click repeatedly in one spot**

1. On the **Clicker** tab, set the interval between clicks and choose the mouse button.
2. Leave the **Actions** list empty.
3. Put the mouse where you want the clicks and press the Start / stop hotkey (`F6`). After the
   countdown it clicks wherever the cursor is.
4. Press the same hotkey to stop.

**Run a sequence of actions**

1. On the **Actions** tab press **+ ADD** and pick a type.
2. Fill in the action. **PICK ON SCREEN** lets you click the spot instead of typing coordinates.
3. To add click positions quickly, hover over each spot and press the Capture hotkey (`F8`), or
   press **CAPTURE CLICK** and click the spot.
4. Drag rows to reorder. Untick a row to skip it without deleting it. Double-click a row to edit it.
5. Every action is followed by the interval from the Clicker tab, unless the action has its own
   **Delay afterwards**.
6. On the Clicker tab choose Sequential or Random order and, under **When to stop**, how the run ends.

**Record and play back**

1. On the Actions tab press **RECORD** (or `Ctrl+Shift+R`), do the task, then press it again.
2. The recording becomes normal actions you can edit, with the real pauses stored as each
   action's delay.
3. To play it once, set **When to stop** to `Loops` with a count of 1. Leave jitter and glide at 0
   for an exact replay.
4. Untick **Record mouse movement** to keep only clicks, keys and scrolling. **Playback speed ×**
   makes it faster or slower.

**Wait for something on screen**

1. Add a **Pixel wait** (pause until a colour appears) or **Pixel click** (click when it appears).
2. Press **EYEDROPPER** and click the pixel to watch. Its position and colour are filled in.
3. Raise the tolerance if the colour varies slightly. Set a timeout and whether a timeout skips
   the action or stops the run.

**Click things that appear in an area**

For games where targets drift in and each kind needs a different button, for example left-click
meteorites and right-click satellites.

1. On the Actions tab press **+ ADD > Area watch**. Press **SELECT AREA ON SCREEN** and drag a box
   over the part of the game where targets appear. Leave out the planet, score and anything else
   that shares their colours.
2. With the game showing and a target visible, press **+ ADD A PICTURE OF A TARGET**. The
   clicker's windows hide and the screen freezes; drag a snug box around one target. Choose Left
   or Right for that picture. Repeat for each kind of target (a meteorite, a satellite).
   Each thing in the area is separated from the backdrop and compared with your pictures by its
   mix of colours, so it does not matter which way up a target is or how big it is. On a plain
   backdrop anything that stands out is an object. On a busy one (a painted scene, a planet) the
   clicker learns the scene in the first two seconds and then treats whatever **moves** across it
   as an object. It picks the method itself.
   If the game draws a marker on one kind of target, such as a red circle around every satellite,
   use **+ ADD A COLOUR** on the marker instead of a picture. Anything carrying that colour gets
   that button, clicked at its centre. This is the most dependable way to tell two kinds apart.
   Snip the whole target with a little of its surround. The picture may come from a different
   screen than the play field (a menu or preview with another background colour): a plain
   surround is recognised and left out. Avoid snipping a target on a surround the same colour as
   part of it (a satellite with black panels on a black screen), because those parts can't be
   told from the surround.
   Snipping inside a busy game works too, and is the best way to capture a target as the game
   really draws it. Select the area first, then press **+ ADD A PICTURE OF A TARGET** while
   targets are flying: it watches for about three seconds, freezes, and after you drag the box it
   removes the scene behind the target (shown as green).
   Every kind of target needs its own picture. A kind without one is judged by whichever picture
   it happens to resemble most, which can be the wrong button. If a marker colour already
   identifies a kind (the red circle), do not also give that kind a picture.
   Press **TEST ON THE SCREEN NOW** with targets visible: it shows how many objects it sees and
   how closely each picture matched, without clicking anything. Use it to set each picture's %.
3. Set **Click first** to where the planet is, so the most urgent target is dealt with first:
   `Center` if it sits in the middle of the area with targets coming from every side, otherwise
   the edge nearest it.
4. Make this the only action in the list, set a short interval on the Clicker tab (30 to 100 ms),
   and start with the hotkey. It clicks one target per pass and keeps going.

To do some set-up clicks first and then play one timed game: put the click actions above the Area
watch, set the watch's **Keep watching for** to the length of the game (`31000` for 31 seconds),
and on the Clicker tab set **When to stop** to `Loops` with a count of 1. The clicks run once, the
watch plays for that long, and the run stops.

- Targets missed: lower that picture's % to just under the likeness the test shows. Wrong things
  clicked: raise it. About 55% is a good start.
- On a busy backdrop, start the clicker a couple of seconds before targets matter, and keep score
  displays and timers out of the area if they get clicked. Something that stops moving for a few
  seconds becomes part of the scene and is no longer seen; when the whole screen changes (a menu
  opens), expect a few stray clicks until the new screen has been learned.
- Stuck? Turn on **Save what an Area watch sees** (Settings > Troubleshooting), run once, and
  open the snapshot folder. `report.txt` lists every object it saw and how it judged each one,
  next to a picture of each moment.
- Two targets with the same colours but different outlines: switch **Recognise pictures by** to
  `Exact` (about 85%). Exact compares pixel patterns, so it needs targets that never rotate or
  change size, and a picture that shows the whole target.
- Targets that touch or overlap count as one object until they separate.
- Parts of a target that are the same colour as the play field can't be seen there, so they are
  left out of the comparison automatically. Things much smaller than your pictures (stars,
  sparks) and much larger (a planet) are ignored.
- To have the watch start and stop with the game instead of running for a set time, press **SNIP A
  START / STOP MARKER** and drag a box around something that is always showing during the game
  and gone afterwards (a label such as "Time Left"). The watch waits for it to appear and only
  clicks while it is showing. The marker is checked again right before every click, so clicking
  stops the instant it disappears and nothing on the next screen gets clicked. Set **Keep watching
  for** to 0, or leave a number there as an upper limit. Choose something that vanishes the moment
  the game ends and that targets don't fly across; the status shows `WAITING FOR MARKER` until it
  appears.
- As a second safeguard, once a watch has clicked something it never clicks on a different screen.
  If a quarter or more of the area suddenly changes (the game closed, a shop opened) it holds off,
  the status shows `DIFFERENT SCREEN`, and it only carries on if the game screen comes back. A
  screen change before the first click is treated as the game opening and is learned straight away.
- After a click it leaves that spot alone for 400 ms (**After a click** in the action), so a target
  that is already hit and fading out isn't clicked again; wasted clicks often break a combo. Raise
  it if hit targets linger longer than that. A moving target is also left until it is fully inside
  the area.
- The game must be in windowed or borderless mode; exclusive fullscreen usually can't be read.

**Profiles and saving**

- A profile holds everything: actions, intervals, hotkeys, target window and overlays.
- Profiles are grouped by game. Pick the game on the left of the header, then one of its
  profiles. **MANAGE** has **New game**, **New profile in this game**, and **Move profile to
  another game**.
- Profiles that aren't filed under a game sit under `(No game)`. Select it and use **MANAGE >
  Rename this game** to file all of them under one game at once, or move them one at a time.
- Changes are **not** saved until you press **SAVE** (or `Ctrl+S`). The button turns red while
  there are unsaved changes.
- You can run a profile without saving it. You are asked about unsaved changes before switching
  profile or closing.
- **MANAGE > Revert to saved** throws away unsaved changes. **Export** writes the profile, as it
  currently is, to a file you can share.
- App-wide options on the Settings tab (tray, timing precision, recorder options) are not part of
  a profile and save by themselves.

**Stopping and staying safe**

- Start / stop hotkey (`F6`), Emergency stop hotkey (`F9`), or shove the mouse into an outer
  corner of the screen.
- Pause / resume (`F7`) holds a run without ending it.
- To keep a run inside one game, pick it on the **Window** tab. The run then pauses whenever that
  window loses focus.

**Very fast clicking (1 to 5 ms)**

- Start and stop with the hotkey, not the START button, so the cursor is already on the target.
  While the cursor is on the clicker's own window the run waits and the status shows
  `CURSOR IS ON THIS APP`.
- Many programs and games can't process 1,000 clicks a second. They may lag, drop clicks, or keep
  reacting for a moment after you stop while they work through the backlog. If that happens, use a
  longer interval.
- **Actual clicks / sec** in Live stats shows what is really being sent.

## Default hotkeys

All hotkeys except `Ctrl+S` are global and rebindable on the **Hotkeys** tab: click a box, then
press the new key. Esc cancels, Backspace clears. Hotkeys are saved per profile.

| Action                    | Default        |
| ------------------------- | -------------- |
| Start / stop              | `F6`           |
| Pause / resume            | `F7`           |
| Capture cursor position   | `F8`           |
| Emergency stop            | `F9`           |
| Show position markers     | `Ctrl+Shift+O` |
| Save profile (app window) | `Ctrl+S`       |
| Record / stop recording   | `Ctrl+Shift+R` |
| Switch to this profile    | not set        |

Start / stop can work as a **toggle** (press to start, press to stop) or **hold** (runs only while
the key is held, with no countdown). The app warns you if a key is already taken by another
program, and refuses to start if two of its own hotkeys collide.

## Install and run

Download or build `PizzaHeroClicker.exe` and run it. Nothing is installed.

- Data lives in `%APPDATA%\PizzaHeroClicker\` (`profiles\` with a folder per game, `logs\`,
  `settings.json`).
- **Portable mode:** put an empty file named `portable.flag` next to the exe (or use the button on
  the Settings tab) and everything is stored next to the exe instead.
- If the game you are automating runs **as administrator**, run the clicker as administrator too.
  Windows does not let a normal program send input to an elevated one.
- Windows 10 version 1803 or newer, 64-bit.

## Build

Requires the .NET SDK 8 or newer.

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet publish src/PizzaHeroClicker -c Release -o publish
```

The publish step produces `publish\PizzaHeroClicker.exe`: single file, self-contained, win-x64
(settings are in the `.csproj`).

### Installer

To build a setup program for sharing, install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(6.3 or newer) and run:

```bash
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

This publishes the app and writes `installer\output\PizzaHeroClicker-Setup-<version>.exe`. The
version comes from `<Version>` in the `.csproj`. The installer:

- installs for the current user without an administrator prompt (it offers "all users" as an option),
- adds a Start Menu entry, an optional desktop shortcut, and an entry in Windows' installed-apps list,
- installs over an older version when run again (close the app first; Setup will ask),
- leaves profiles and settings in `%APPDATA%\PizzaHeroClicker` alone when uninstalled.

## Updates

The app looks at this project's [GitHub releases](https://github.com/PizzaHeroGaming/PizzaHeroClicker/releases)
for a newer version: once when it starts (this can be turned off on the **About** tab) and whenever
**CHECK FOR UPDATES** is pressed there. The check sends nothing about you or your profiles.

Nothing is downloaded or installed until you press the update button. It then downloads the new
installer, checks its size and SHA-256 against what GitHub lists for the release, closes the app,
installs over the old version and starts the new one. Profiles and settings are kept. A portable
copy is sent to the download page instead.

### Publishing a release

1. Bump `<Version>` in `src/PizzaHeroClicker/PizzaHeroClicker.csproj`.
2. Commit and push.
3. Run:

```bash
powershell -ExecutionPolicy Bypass -File installer/release.ps1
```

This builds the installer and publishes it as release `v<version>` (needs the GitHub CLI, signed in
with `gh auth login`). Add `-Draft` to look it over before it goes live, or `-NotesFile notes.md`
to write your own release notes. The app only offers an installer named
`PizzaHeroClicker-Setup-<version>.exe` attached to a release in this repository, so keep that name.

## Profile format

A profile is one JSON file, `profiles\<game>\<profile name>.json` (or `profiles\<profile name>.json`
when it isn't filed under a game). The folder is its game and the file name is its name; neither
is taken from inside the file, so you can also organise them in Explorer while the app is closed.
Times are milliseconds; `createdAtMs` / `modifiedAtMs` are Unix epoch milliseconds. Positions are
physical screen pixels (negative values are monitors left of or above the primary one), or offsets
from the target window's client area when `window.relativeCoordinates` is on.

```json
{
  "schemaVersion": 1,
  "name": "Example",
  "createdAtMs": 1791177860808,
  "modifiedAtMs": 1791177861450,
  "actions": [
    { "type": "click", "x": 812, "y": 440, "button": "Left", "kind": "Single", "holdMs": 0,
      "useCursor": false, "jitterPx": null, "label": "Collect", "enabled": true,
      "intervalMs": null, "intervalMaxMs": null },
    { "type": "key", "keys": ["Ctrl+C", "Enter"], "holdMs": 20, "gapMs": 50 },
    { "type": "wait", "ms": 1500 },
    { "type": "scroll", "direction": "Down", "amount": 3, "useCursor": true },
    { "type": "drag", "x1": 100, "y1": 200, "x2": 640, "y2": 480, "button": "Left", "durationMs": 350 },
    { "type": "move", "x": 300, "y": 300, "durationMs": 120 },
    { "type": "pixelWait", "x": 955, "y": 610, "color": "#E8432E", "tolerance": 12,
      "timeoutMs": 8000, "onTimeout": "Skip", "pollMs": 50 },
    { "type": "pixelClick", "x": 955, "y": 610, "color": "#E8432E", "tolerance": 12,
      "timeoutMs": 8000, "onTimeout": "Stop", "pollMs": 50,
      "button": "Left", "kind": "Single", "holdMs": 0, "clickAtPixel": true }
  ],
  "order": "Sequential",
  "intervalMs": 100,
  "randomInterval": false, "intervalMinMs": 80, "intervalMaxMs": 120,
  "jitterPx": 0, "smoothMoveMs": 0, "speedMultiplier": 1, "startDelayMs": 3000,
  "defaultButton": "Left", "defaultKind": "Single",
  "cornerStop": true,
  "burst": { "enabled": false, "count": 10, "pauseMs": 1000 },
  "repeat": { "mode": "Infinite", "count": 100, "durationMs": 60000, "untilTime": "23:59:00" },
  "hotkeys": { "mode": "Toggle", "toggle": "F6", "pause": "F7", "capture": "F8", "stop": "F9",
               "overlay": "Ctrl+Shift+O", "record": "Ctrl+Shift+R" },
  "quickSwitchHotkey": "",
  "window": { "enabled": false, "title": "", "processName": "", "onlyWhenFocused": true,
              "relativeCoordinates": false },
  "overlay": { "showStatus": false, "statusClickThrough": true, "statusX": null, "statusY": null,
               "showPositions": false }
}
```

An area watch action, not shown above, looks like this:

```json
{ "type": "areaWatch", "x": 600, "y": 200, "width": 900, "height": 700,
  "rules": [
    { "image": "<base64 PNG of the target>", "matchPercent": 55, "button": "Left" },
    { "color": "#3FA9F5", "tolerance": 25, "button": "Right" }
  ],
  "pictureMatch": "Appearance", "watchForMs": 31000, "retargetDelayMs": 400, "priority": "Center", "minPixels": 12, "holdMs": 0, "pollMs": 25,
  "timeoutMs": 0, "onTimeout": "Skip" }
```

A rule with an `image` is a picture rule (matched by shape); a rule without one is a colour rule.
`priority` is one of `Top`, `Bottom`, `Left`, `Right`, `Center`; `pictureMatch` is `Appearance` or `Exact`.

Notes for hand-editing:

- `"type"` must be the **first** property of each action.
- Every action also accepts `label`, `enabled`, `intervalMs` and `intervalMaxMs`. `intervalMs`
  replaces the global interval for that action; adding `intervalMaxMs` makes it a random range.
  Omitted properties take their defaults.
- `repeat.mode` is one of `Infinite`, `Count` (actions), `Loops` (passes through the list),
  `Duration`, `UntilTime`.
- Key names are the ones shown in the app (`F6`, `A`, `5`, `Enter`, `Space`, `LeftShift`, ...),
  joined to modifiers with `+`. An empty string means "not bound".
- Out-of-range values are clamped on load. A file that isn't a valid profile is skipped and logged.

## How the timing works

A dedicated engine thread runs the actions; the UI only polls it for status. All delays follow one
absolute timeline, so the time spent sending input never accumulates as drift.

Each wait is a hybrid: a high-resolution kernel timer sleeps through most of it (no CPU), waking
slightly early, and a short spin lands on the exact deadline. How early it wakes adapts to how late
Windows has been waking the thread recently. Measured on the development machine:

| Interval | Mode     | Average | 95% of clicks within | CPU (one core) |
| -------- | -------- | ------- | -------------------- | -------------- |
| 1 ms     | Balanced | exact   | about 0.17 ms late   | about 35%      |
| 1 ms     | Precise  | exact   | about 0.001 ms late  | about 70%      |
| 5 ms     | Balanced | exact   | about 0.07 ms late   | about 10%      |
| 16 ms    | Balanced | exact   | about 0.01 ms late   | about 2%       |

"Precise" is the **Maximum timing precision** switch on the Settings tab. Details are in the
comments of `Services/TimingEngine.cs`.

## Good to know

- Changes made while a run is active apply to the **next** run. A run works on a snapshot of the
  profile taken when it starts (saved or not).
- Signing out of Windows or shutting down does not prompt for unsaved profile changes; save first.
- A list in which every action is disabled won't start. An empty list is valid and means plain
  clicking at the cursor.
- A key action can't press one of the app's own hotkeys; the app tells you which one clashes.
- Recorded actions play back one after another, so inputs that overlapped while recording (holding
  `W` while clicking) become sequential.
- For faithful playback of a recording, leave jitter and cursor glide at 0, and set **When to
  stop** to `Loops` = 1 to play it once.
- Overlays and markers can't be drawn over games in exclusive fullscreen. Use borderless or
  windowed mode. Hotkeys and clicking still work either way.
- The exe is unsigned, so SmartScreen or antivirus tools may warn about it, as they do for most
  auto clickers.

## Project layout

```
src/PizzaHeroClicker/
  Native/        every P/Invoke declaration, commented
  Models/        Profile, actions, settings, key combos (plain data + JSON)
  Services/      InputService, HotkeyService, TimingEngine, ProfileService, WindowService,
                 ScreenService, RecorderService, OverlayService, TrayService, Log, AppPaths
  Engine/        ClickEngine: the background thread that runs a profile
  ViewModels/    MainViewModel (split by feature), ActionEditorViewModel
  Views/         windows, tabs, overlays, dialogs
  Controls/      HotkeyBox (press-to-rebind), numeric input behaviour
  Themes/        the dark Pizza Hero theme
tests/PizzaHeroClicker.Tests/   xUnit tests (timing, engine, profiles, recording, coordinates)
```

Developer switches on the exe:

- `--data <folder>`: use that folder for profiles, settings and logs.
- `--snapshot <folder>`: render every tab to PNG, check overlay placement, then exit.
- `--selftest`: exercise real input end to end (moves the mouse for a few seconds and clicks only
  inside its own test window), write `SELFTEST` lines to the log, then exit.

Logs roll at about 1 MB (`logs\app.log`, `app.1.log` ... `app.3.log`).

## Credits and license

Made by YourPizzaHero / Pizza Hero Gaming. Uses
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT).

**License: not chosen yet.** Until a license file is added, all rights are reserved.
