# PC–TV Switcher

Switch your Windows displays and sound between your desk and your TV with one hotkey. For example, **PC mode** can use your laptop screen, monitor, and speakers, while **TV mode** uses the TV and its HDMI audio, then opens Steam Big Picture or a media player.

[Download TVMode.exe from GitHub Releases](https://github.com/baam28/pc-tv-switcher/releases/latest)

Run `TVMode.exe` on the Windows PC connected to your displays. Keep it in a permanent folder if you plan to use desktop shortcuts or start it with Windows.

> The setup wizard, desktop shortcut creation, and `tv-no-apps` command are available in the current source build. If you use the v1.0.1 release, follow [manual setup](#manual-setup). Its tray menu already includes **TV mode without apps**.

## Set up your PC and TV profiles

Connect your displays and turn on the TV. On a fresh setup, the wizard opens when you launch the app. You can also open it from **Settings → Setup wizard**.

1. Arrange your desk displays in Windows and select your usual sound output. Choose **Capture PC setup**.
2. Select the displays you want in TV mode. **Identify** shows matching numbers on screens that are already on. Use **Refresh** after connecting a display.
3. Choose **Test TV setup**. If you can see the wizard after the switch, select **I can see this** within 15 seconds. If the screen stays blank, wait for the previous display setup to return.
4. Choose your TV sound output. Use **Refresh** if the HDMI output takes a moment to appear. Select **Test sound output** and play something to check it.
5. Optionally enable **Create three desktop shortcuts** for **PC mode**, **TV mode**, and **TV mode without apps**. Choose **Finish and save** to save both profiles and return to your previous setup.

### Manual setup

1. Under **PC profile** and **TV profile**, tick the screens each mode should turn on. Each mode turns off displays outside its profile.
2. Choose a sound output for each profile, or select **Don't change** to keep the current Windows output.
3. To save a specific layout, switch to that mode and arrange the displays in **Windows Settings → System → Display**. In PC–TV Switcher, select **Save current setup** in that profile. Saved layouts include resolution, refresh rate, rotation, arrangement, and the main display. Use **Forget** to go back to the layout Windows remembers for that set of screens. Windows manages scaling and HDR separately.

## Switch modes

| What you want to do | How |
| --- | --- |
| Toggle between PC and TV | Press **Ctrl+Alt+T**. Change the shortcut under **Settings → Switch hotkey**. |
| Pick a mode directly | Click **PC mode** or **TV mode** in the main window or tray menu. You can also use the matching desktop shortcut if you created it. |
| Switch to the TV without launching apps | Choose **TV mode without apps** in the tray menu or use the **TV mode without apps** desktop shortcut. |

**TV mode without apps** still applies the TV display and sound profile. It leaves running apps alone. When you return to PC mode, the usual per-app close settings apply.

After you switch to TV mode, the TV shows **Keep this setup?** with a 15-second countdown. Select **Keep** or press **Enter** to stay in TV mode; your TV apps open at that point. Select **Go back**, press **Esc**, or let the countdown run out, and your previous screens and sound output come back. That way a TV that's off or on the wrong input can't leave you with a black screen. Pressing the hotkey again while the question is showing also counts as keeping it. Turn the question off under **Settings → Ask to keep TV mode**. Desktop shortcuts ask too; commands typed in a terminal don't.

Desktop shortcuts switch the same way the hotkey does: the running copy in the tray does the switch and shows the on-screen message. If PC–TV Switcher isn't running, the shortcut starts it in the tray and then switches.

Closing the window or pressing **Esc** keeps the app available in the tray. Right-click the tray icon and select **Exit** to quit. Enable **Start with Windows** in Settings to keep the hotkey available after signing in.

## Launch apps in TV mode

1. Under **Apps for TV mode**, select **Add app**. Choose an installed preset such as **Steam Big Picture**, or select **Choose another app…** to search the Start menu or browse for an executable.
2. Each app has two switches. The line under its name sums them up, for example *Opens in TV · closes in PC*.
   - **Open in TV mode**: turn it off to keep the app in the list without opening it.
   - **Close in PC mode**: decides what happens when you switch back. For supported desktop apps, PC–TV Switcher asks only the instances it started to close. This gives apps a chance to prompt you to save. Store apps and web links can't be closed automatically, so that switch is greyed out for them.
3. Use the pencil button to open **Start options**. Supported desktop apps can take arguments such as `--fullscreen`. Any listed app can wait for a TV-profile screen to become the main display, for the configured TV sound output to become the Windows default, or for both. If a selected check is still pending after 12 seconds, the app is skipped and the switch status explains why. The audio check is inactive when TV sound is set to **Don't change**.

With **Close in PC mode** enabled for Steam Big Picture, returning to PC mode exits Big Picture. If PC–TV Switcher started Steam, it asks Steam to shut down as well.

## Troubleshooting

| What you see | What to check |
| --- | --- |
| The TV is missing or marked **NOT CONNECTED** | Turn it on, check the input and cable, then select **Refresh** or press **F5** in the main window. If some profile displays are disconnected, Windows chooses a layout for the connected ones. If none are connected, the switch stops and keeps the current setup. |
| The TV has picture but no sound | Switch to TV mode, refresh the output list, and select the TV's HDMI output in **TV profile**. Some outputs appear only after the TV display is active. |
| An app opens on the wrong screen or uses the wrong audio output | Check the TV profile's main display and sound output, then enable the appropriate wait checks under the app's **Start options**. |
| The laptop refresh rate differs from the saved layout | **Laptop refresh rate follows charger / battery** is enabled by default. Turn it off in Settings to keep the laptop refresh rate from the saved layout. |
| The hotkey is already in use | Choose another combination under **Settings → Switch hotkey**. |
| The TV stays black after switching | Wait for the 15-second countdown, or press **Esc**: your previous screens come back. Then check the TV's input. |
| **Couldn't switch** appears | The message explains the reason, for example a locked screen, a UAC prompt, or a TV that's still waking up. Each attempt that Windows refused is recorded in `%APPDATA%\TVMode\switch.log`. |

## Command line

Run these commands from the folder containing `TVMode.exe`. In PowerShell, include the `.\` prefix.

```powershell
.\TVMode.exe pc
.\TVMode.exe tv
.\TVMode.exe tv-no-apps
.\TVMode.exe toggle
.\TVMode.exe list
```

`list` shows displays, sound outputs, profiles, and configured TV apps. The switch commands use the same profiles and app settings as the window and tray menu. Run from a terminal, they switch directly, print the result and exit, without the on-screen message or **Keep this setup?**. Run without a terminal, for example from a shortcut or a launcher app, they're handed to the app in the tray like a desktop shortcut.

## Build from source

From the repository root on Windows, run:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script uses the Windows .NET Framework C# compiler and writes `TVMode.exe` to the repository root. If a running copy is using that executable, exit it from the tray before rebuilding.

Settings are stored at `%APPDATA%\TVMode\settings.ini`. To upgrade, exit the running copy, then replace `TVMode.exe` in the same folder so your desktop shortcuts continue to point to it.
