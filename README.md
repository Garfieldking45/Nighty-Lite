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
- Privacy: once per start Nighty Lite sends an anonymous ping so the author can see how many people use which version: a random install ID, the app version, Windows 10 or 11, which features were used since last time (names only, e.g. "clicker"), and counts of logged errors and failed updates. No name, IP address, settings values, hotkeys, games or Roblox account are sent, and the server stores no IPs. Turn it off in Settings > "Share anonymous usage stats". The server side is in `stats-worker/`.
- Suggestions: Settings > "Send a suggestion" opens a small window. Nothing is sent until you press Send; it goes with your app version and the random install ID (used only to limit spam, not stored with the text). Max 500 characters, 3 per day. Please don't include personal info.
- Versions: use `X.Y.Z` tags. Small updates bump the last number (1.3.0 -> 1.3.1) and the updater picks them up the same way as bigger ones.
- Wins tracker: press Insert after each win to add one (Ctrl+F7 takes one back); the count shows over Roblox as "Wins: N" and flashes green. Open Render > Wins Counter (the three dots) to rebind the keys, set the number (including "Type in a number..."), move or resize it, or reset it to 0. The count is saved between runs.
