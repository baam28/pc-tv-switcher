# TV Mode

Switch your laptop between two display profiles with one hotkey.

- **PC profile**: for example, laptop screen + monitor, with sound on your speakers
- **TV profile**: for example, TV only, with sound on the TV, and Steam Big Picture opened

Each profile has its own **screens to turn on**, an optional **saved layout**, and its own **sound output**. Screens that aren't in the profile are turned off.

## First-time setup
1. Plug in the monitor and the TV, and turn the TV on.
2. Run `TVMode.exe`.
3. **PC profile**: under **Screens to turn on**, tick the laptop screen and the monitor.
4. **TV profile**: tick only the TV.
5. **Save the exact layout** (recommended): switch to a mode, arrange it the way you like in **Windows Settings > System > Display** (resolution, refresh rate, rotation, position, main screen), then click **Save current setup** in that profile. Do this for both profiles.
   The profile then restores exactly that layout every time. **Forget** goes back to letting Windows choose the layout.
   Text size (scaling %) and HDR aren't part of the saved layout, but Windows remembers those for each screen anyway.
6. **Sound**: pick an output for each profile, or leave it on **Don't change**.
   The TV's sound only shows up while the TV picture is on, so switch to TV mode once and then pick it.
7. **Apps to open in TV mode** (optional): click **Add app** for each app you want to open. TV mode starts all listed apps.
   - **Add app** offers TV-friendly apps found on this PC, such as Steam Big Picture, Kodi, VLC (full screen), Plex HTPC, Jellyfin Media Player, Playnite (full screen) and Moonlight.
   - **Choose another app...** opens a searchable list of everything in your Start menu, including Store apps like Netflix or Disney+.
     If an app isn't listed, use **Browse for a file...** at the bottom.
   - Set **Close in PC mode** separately on each row. Turn it off to keep that app open. With it on, TV Mode asks the app to close when you switch back, as if you'd clicked its X.
     - Only copies TV Mode opened itself are closed. If the app was already running, it is left alone.
     - Apps aren't force-closed, in case they're asking you to save something. If an app doesn't close, the on-screen message tells you.
     - Store apps and web links open fine but can't be closed automatically.
   - **Steam Big Picture:** TV mode opens Big Picture on the TV, starting Steam if needed. With its close switch on, going back to PC mode leaves Big Picture.
     If Steam wasn't running before TV mode, Steam is shut down too. If it was already running, Steam keeps running.
   - The pencil button opens **Start options** for a desktop app, for example to add `--fullscreen`. The trash button removes an app from the list.
   - Upgrading from the single-app setting carries over the selected app, its start options, and its close switch.

## Settings (gear icon, top right)
- **Switch hotkey**: click the box and press the keys you want (Ctrl and/or Alt plus a key, or an F-key on its own). If another app already uses that combination, you're told and the old one is kept.
- **Start with Windows**: runs quietly in the tray, so the hotkey always works.
- **Laptop refresh rate follows charger / battery** (on by default): after every switch, the laptop screen is set the way G-Helper's auto mode does it: the highest rate on the charger (e.g. 165Hz), 60Hz on battery.
  This overrides the laptop rate stored in a saved layout, so it doesn't matter whether you saved on battery or on the charger. G-Helper still handles plugging in or unplugging later.

Settings are saved in `%APPDATA%\TVMode\settings.ini`. A screen that's unplugged stays in its profile and shows as **NOT CONNECTED**.

## Switching
- Press **Ctrl+Alt+T** (or your own hotkey) from anywhere. It goes to PC mode if you're in TV mode, and to TV mode otherwise.
- Or click **PC mode** / **TV mode** in the window,
- or right-click the tray icon (the current mode is marked),
- or use the command line (for shortcuts or scripts):
  ```
  TVMode.exe pc
  TVMode.exe tv
  TVMode.exe toggle
  TVMode.exe list
  ```
  The command-line switches use the same TV app list and close switches. `list` shows the configured apps and their close settings.

After each switch, a large message appears for about two seconds in the middle of the new main screen, for example "TV MODE" with "Sound: LG TV" below it. It never takes focus from a game or video, and clicks go straight through it. If a switch fails, the message shows why, in red.

If some screens in a profile are unplugged, the app switches to the ones that are connected and lets Windows choose the layout. If none of them are connected, nothing changes, so you can't end up with every screen turned off.

Closing the window (or pressing Esc) keeps the app in the tray. To quit it, right-click the tray icon and choose **Exit**. Starting `TVMode.exe` again brings the running window back instead of opening a second copy.

Keyboard: **F5** refreshes; **Tab** moves between controls; **Space** toggles the focused switch or screen.

## Sharp at any scaling
The app is per-monitor DPI aware: everything is drawn at the real scaling of the screen it's on (100% to 300%+), and the window redraws at the right size when it moves between screens, for example from the laptop at 150% to a TV at 300%. Nothing gets stretched or blurry.

## How it works
It uses the Windows display API (`SetDisplayConfig`). Without a saved layout, Windows remembers the resolution, scaling, arrangement and main display for each combination of screens. So the first time you enter a mode, set things up in **Windows Settings > System > Display**, and that layout comes back every time you switch to that mode.

## Building
```
powershell -ExecutionPolicy Bypass -File build.ps1
```
This uses the C# compiler that comes with Windows (.NET Framework 4.x), so you don't need to install anything. The build also draws the app icon (`src\app.ico`) the first time.

Source files:
- `src\TVMode.cs`: displays, sound, profiles, settings, the app launcher, and the command line
- `src\Controls.cs`: theme, DPI scaling, custom controls, app icons
- `src\Windows.cs`: main window, Settings, app picker, and the on-screen message
- `src\app.manifest`: turns on per-monitor DPI awareness
