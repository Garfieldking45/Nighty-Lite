# Nighty Lite

Just the **Auto Clicker** and **Auto Crossbow** from Nighty, as a small **overlay** (WPF, .NET 8). Nothing else is included.

- Hold **Tab** while Roblox is in front: two bars appear (Auto Clicker, Auto Crossbow). Click a bar to drop it down, change settings with the - / + buttons, release Tab and it vanishes. It never takes focus, so Roblox keeps running underneath. It also shows for 6 s at start-up. Use the Quit button in the overlay to close it.
- Roblox must be windowed or borderless (F11 fullscreen), otherwise Windows can't draw over it.

- Build: `powershell -ExecutionPolicy Bypass -File build.ps1` -> `publish\NightyLite.exe` (no .NET install needed).
- Settings and logs: `%AppData%\NightyLite` (set `NIGHTY_LITE_DATA` to use another folder).
- Emergency stop: F9 (changeable) stops both.
- Auto Crossbow: default hotkey Mouse Side 1 (Hold), crossbow slot 3, sword slot 1, blocks slot 2. It only starts while you are holding slot 1 (press 1 first) and never while this window is in front.
- Auto Clicker: default hotkey F6 (Toggle), 12 CPS, left button.
- Auto Tool: while you hold one of your tool slots (set Pickaxe / Axe / Shears slots in Combat > Auto Tool) and mine a block, it presses the slot of the tool that breaks it. It reads the screen pixels under the crosshair, so Roblox must be windowed/borderless. Common blocks (obsidian, stone, ore, terracotta, wood, beds, wool) are built in from screenshots; if it misses one, teach it: hold the right tool, aim at the block, press the Teach key (default F8). Wool: teach a few colours (Shears ignore hue, but not brightness). Slots start Off, and it only reads number-key slot changes (not the scroll wheel).
- The engine code (clicker timing, shot timing) is copied unchanged from Nighty 2.5.1.
- Updates: on start-up it checks the latest GitHub release. If a newer one exists, an "Update to vX" button appears in Settings; pressing it downloads the new exe (size and SHA-256 checked), restarts and replaces itself. Nothing installs without your click. To publish an update: bump `<Version>` in `NightyLite.csproj`, run `build.ps1`, and attach `publish\NightyLite.exe` to a release tagged `vX.Y.Z`.
