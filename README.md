# Key Shift for Spotify (Windows)

Key Shift changes Spotify's pitch while keeping the music tempo. One installer adds a **Key Shift** button and an integrated settings page inside Spotify. It starts the audio processor quietly in the background. Spicetify and other setup programs are not required.

## Download

Download `KeyShiftSetup.exe` from the latest [GitHub Release](../../releases/latest), run it, and click **Установить**. Close Spotify before installing. The installer also appears in Windows' installed-apps list so it can be removed later.

## Requirements

- 64-bit Windows 10 build 19041 or later
- Classic Spotify Desktop installed from spotify.com

Keep Spotify closed during installation and removal. The installer backs up the Spotify interface file it changes and restores it when Key Shift is removed.

## Create a shareable installer

Run `Build-Distribution.ps1` on a Windows machine with the .NET 10 SDK installed. It builds the app and creates:

- `release/Distribution/KeyShiftSetup.exe` — the installer to send to people
- `release/KeyShift-Setup.zip` — the installer with a short README and third-party notices

The setup file contains the app and Spotify interface script. Recipients do not need to unpack project files or run PowerShell commands.

To publish a GitHub Release, push a version tag such as `v1.0.0`. GitHub Actions builds the installer and attaches the `.exe` and `.zip` to that release.

## Install

Run `KeyShiftSetup.exe`, click **Установить**, and restart Spotify. The button appears in the play bar; click it to open Key Shift's settings in Spotify, then use **Назад в Spotify** to return. The audio processor starts automatically and does not show a separate settings window. Windows adds Key Shift to its installed-apps list, where it can be removed later. If Spotify updates its interface files, run the installer again to restore the button.

## Use

Click **Key Shift** in Spotify. Choose a shift from −12 to +12 semitones and enable Spotify audio processing. The processor runs in the background and starts automatically with Windows.

The companion captures only Spotify's process audio, changes its pitch, and sends it to Spotify's current output device. Keep Spotify Exclusive Mode off so Windows can capture the audio.

## Remove

Open **Settings → Apps → Installed apps** in Windows, find **Key Shift for Spotify**, and select **Uninstall**. The uninstaller removes the embedded Key Shift script and restores Spotify's original interface file when it has not been changed since installation.

This is an unofficial Spotify client modification. Spotify updates may replace the changed interface file. The installer supports the classic Windows desktop client and does not modify Microsoft Store installations.

See `THIRD_PARTY_NOTICES.md` for dependency licenses.
