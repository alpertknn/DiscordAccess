<p align="center">
  <img src="docs/assets/discordaccess.svg" width="112" height="112" alt="DiscordAccess DA icon">
</p>

<h1 align="center">DiscordAccess</h1>

A connection helper for the official Discord Stable desktop app you already have installed. It lets Discord reach its servers through a local, Discord-only proxy and a hash-pinned DoH engine. On Windows it runs as a system tray app; on macOS it runs as a native menu bar app (pre-release). The interface is in English; the current Windows release, v4.22, still shows it in Turkish.

**v4.22** (Windows) fixes Windows startup setup that failed because of desktop or Downloads folder permissions: it now uses a safe personal copy of the package. It keeps existing file permissions and respects a user's choice to turn startup off.

`DiscordAccess.exe` prepares the connection; you open Discord from your own desktop or Start menu shortcut. The shortcut's real Discord target is kept; only the session's local proxy arguments are added. No separate Discord or profile copy is created; your existing account is used. No open CMD window is needed.

## Download the Windows package

Download **DiscordAccess-v4.22.zip** from the [latest combined release](https://github.com/alpertknn/DiscordAccess/releases/tag/macos-v0.3). This is the unchanged Windows v4.22 package from the [original v4.22 release and source tag](https://github.com/alpertknn/DiscordAccess/releases/tag/v4.22); the new tag adds macOS changes and does not rebuild the Windows executable.

`DiscordAccess-v4.22-engine-source.zip` contains the engine's corresponding source and rebuild materials; you do not need it to run the program. If you redistribute the ready-made package, also provide this source package and the license notices. Old release tags are kept for source history; do not redistribute earlier binary packages that had incomplete notices.

Extract the whole ZIP into a short, permanent local folder. `DiscordAccess.exe` and `engine/sing-box.exe` must stay in the same package. Open `DiscordAccess.exe`; the ready-made package needs no build step. `Baslat.cmd` has been removed.

The package's [usage notes](docs/README.txt) are also in the repository.

**Code > Download ZIP** on GitHub and the **Source code** files under Releases contain only source code; they do not include the program EXE or the engine. If you want to use them for development, first complete the steps in "Building from source (Windows)"; for macOS, see [macos/README.md](macos/README.md). [All releases](https://github.com/alpertknn/DiscordAccess/releases).

You can compute the SHA-256 of the downloaded ZIP with `Get-FileHash .\DiscordAccess-v4.22.zip -Algorithm SHA256` and compare it with `SHA256SUMS.txt` in the same release. The hash file does not provide a code signature.

## macOS pre-release (Apple Silicon)

Download **DiscordAccess-macos-v0.3-installer.zip** from the [latest combined release](https://github.com/alpertknn/DiscordAccess/releases/tag/macos-v0.3). Extract it and open `DiscordAccess-macos-v0.3-Installer.pkg`.

- Runs only on Apple Silicon Macs. The v0.3 build was made on macOS 27; no test suite or live Discord session was run for this release build.
- The installer places the app in `/Applications`. Quit any older running DiscordAccess before installation.
- The installer is unsigned, and the app is signed ad hoc and not notarized. If macOS blocks the installer or app, approve that item with **Open Anyway** in System Settings > Privacy & Security after the first opening attempt.
- Grant the installed app Full Disk Access under System Settings > Privacy & Security > Full Disk Access. If a previous entry remains but access fails, remove it and add `/Applications/DiscordAccess.app` again. The installer cannot grant this permission.
- v0.3 recognizes both numbered and legacy flat Discord startup-module layouts. This may resolve PRF-E03; the cause on the affected Mac was not confirmed by inspecting its Discord profile.
- It runs in the menu bar. You open Discord from its own icon; your existing account is used.

Setup and usage: [macos/README.md](macos/README.md). Trust boundaries: [macos/docs/SECURITY-NOTES.md](macos/docs/SECURITY-NOTES.md). Design: [macos/docs/DESIGN.md](macos/docs/DESIGN.md). The engine and its pin are unchanged from macos-v0.2; `DiscordAccess-macos-v0.2-engine-source.zip` in the combined release contains its corresponding source. If you redistribute the installer, provide that source package and the included license notices as well.

## Upgrading from a previous version (Windows)

1. If **Start with Windows** is on in the old DiscordAccess, turn it off first. This removes the startup entry that points to the old folder.
2. Fully close Discord, then choose **Exit** in the old DiscordAccess.
3. Extract the whole new ZIP into a separate, short, permanent folder. Do not overwrite the running package or move its folder.
4. Prepare the connection with the new `DiscordAccess.exe`. If you want automatic startup, turn **Start with Windows** back on from the new location. A disabled preference is never turned on automatically.

## Requirements (Windows)

- Windows 10/11 x64 and .NET Framework 4.8.
- An official Discord Stable installation that you can write to.
- To build from source only: the Windows .NET Framework x64 C# compiler.
- Node.js: only for the JavaScript tests.

The macOS requirements are in [macos/README.md](macos/README.md).

Do not run as administrator. This project does not change Windows' global proxy, DNS or routing settings, and it does not install a network driver or system service.

## Windows application control

The ready-made package runs directly as an EXE. The EXE and the engine are subject to Windows' security evaluation; tray mode or an open source license does not give the apps a code signature. The project does not change Windows security settings, does not run commands that unblock files or lift security blocks, and does not bypass Windows security controls.

## Building from source (Windows)

Open PowerShell in the repository folder:

```powershell
powershell -NoProfile -File scripts/Fetch-Engine.ps1
.\Build.cmd
```

The engine script downloads the hash-pinned sing-box 1.14.3 source and the Go 1.26.9 toolchain. It builds the `1.14.3-discordaccess.2` engine from source changes pinned by `engine-build/go.mod`, `go.sum` and `engine-patches/http-limits/manifest.json`. The HTTP changes limit connection headers, authentication time and pending unauthenticated connections; they do not limit file size or tunnel duration for authenticated traffic. The output EXE is also verified against a pinned SHA-256. The first build can take a few minutes; Go is not installed system-wide. If the same verified engine is already at `engine/sing-box.exe`, it is not rebuilt. A different engine is never overwritten; build in a fresh source folder instead of reusing an old working folder. Users of the ready-made package do not need to build anything.

## Usage (Windows)

1. Fully close Discord from the system tray. If an earlier DiscordAccess is running, choose **Exit** from its tray menu; in the old CMD version, use `Q` + Enter.
2. Open `DiscordAccess.exe` and wait for the **Connection ready** notification in the system tray.
3. Open Discord from your own shortcut. If no compatible personal shortcut is found, choose **Open Discord** from the DiscordAccess tray menu. While DiscordAccess is running in the tray, you can close Discord and reopen it the same way.
4. When you are done, fully close Discord first, then right-click the DiscordAccess tray icon and choose **Exit**.

Opening the EXE again does not create a second connection or engine; it sends a status request to the existing tray session. The tray menu has **Open Discord**, **Reconnect**, **Check connection**, **Start with Windows** and **Exit**. When you start the helper manually, Discord does not open on its own.

## Windows startup order

After the first successful connection, DiscordAccess automatic startup is set up for the current user only. If there is a recognized personal `Run` entry for the selected official Discord installation, its original command and its enabled/disabled state are saved in the recovery record. When you sign in to Windows, DiscordAccess starts first; only if Discord's own auto-start preference is enabled does it open the official Discord once, after the connection is ready. If Discord's auto-start is off, only the connection is prepared.

Turning off **Start with Windows** in the tray removes the DiscordAccess startup entry and restores the original Discord entry without overwriting user changes. Later manual launches keep this disabled preference. A normal **Exit** does not turn off the automatic startup preference.

Two independent `Run` entries do not guarantee a startup order, so the connection helper coordinates the original Discord entry ([Windows documentation](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys)). If the recognized Discord entry is created again later, the tray session checks for it every five seconds. Unrecognized or modified commands, or multiple entries, are left in place and a warning is shown. If the selected Discord has an active HKLM or Startup folder entry, no order coordination is set up; those entries are not changed.

Do not move the package while the connection is active. If you want to move to another folder while startup is enabled, first turn off **Start with Windows** in the tray, then turn it on again in the new permanent folder. If long startup commands hit Windows' 260-character limit, use a shorter folder for the package.

No automatic startup entry is created if other standard users can write to the package file or any of its parent folders. When v4.22 is opened manually from such a location, it copies the package to `%USERPROFILE%\DiscordAccess\packages\<app-hash>` and restarts from the verified EXE. Only the app, the hash-pinned engine and the package's three usage/license/source texts are copied; no profile, settings, logs or other files are moved. The source package stays where it is, and the permissions of existing files and folders are not changed. Later manual launches use the personal copy with the same content.

The user folder and all parent folders of the installation are also verified. Installation is refused for an unsafe user folder, a junction/symlink, a different existing file, or a missing or wrong engine; the security check is never skipped. Windows startup, rollback, console mode and the Discord launch shortcut do not create a new installation. A previously disabled **Start with Windows** preference is kept; turn it on again from the tray in the new location if you want.

In steady state, one DiscordAccess and one sing-box engine are running. Discord's own Electron child processes are normal; they are not extra Discord copies and the program does not close them.

The installation is found from the verified remembered folder, `%LOCALAPPDATA%\Discord` and personal Discord shortcuts. If it is on another drive and is not found:

```powershell
.\DiscordAccess.exe --discord-path "D:\Programs\Discord"
```

The installation folder, `Discord.exe`, `Update.exe` or an `app-<version>` folder is accepted. Only the installation root is remembered; the version is detected again on the next launch. No full disk scan is performed. PTB/Canary, network shares and junction/symlink installations are not supported.

The earlier console flow is still available with `.\DiscordAccess.exe --console` in the package folder:

| Console command | Function |
| --- | --- |
| `Q` + Enter | Closes the connection; does not kill the normal Discord process. |
| `T` + Enter | Connection diagnostics. |
| `D` + Enter | Download via a Discord attachment link. |
| `A` + Enter | Opens the selected official Discord if no compatible shortcut is found. |

If tray mode is running and you want to switch to a console download, first fully close Discord and choose **Exit** from the DiscordAccess tray menu. Run `.\DiscordAccess.exe --console` in the package folder; when the connection is ready, press `D` + Enter and paste the attachment link. Opening a second EXE does not turn the running tray session into a console.

Attachment downloads inside Discord remain supported. Temporary support is added to the recognized module entry point, and the two update endpoints in the normal `settings.json` file point to the session's local bridge. The Discord EXE, the updater and `app.asar`/`core.asar` are not modified. No account or profile is copied.

## Rollback and updates (Windows)

If DiscordAccess is closed while Discord is still open, a recovery record may remain. Fully close Discord and run the command below, or restart the connection:

```powershell
.\DiscordAccess.exe --restore
```

If Discord's app version or desktop core module is updated, fully close Discord, choose **Exit** from the tray and open `DiscordAccess.exe` again. During the session, the files of the verified version are kept; support for the new version and module is prepared at the next start. `--restore` also undoes the DiscordAccess automatic startup along with the connection and shortcuts, and leaves the preference disabled.

If a file is changed by another program during an operation, the operation may stop. Unrecognized content and the `.discordaccess-fs-*.preserved` / `.rollback-preserved` recovery files are kept; do not delete them with automatic cleanup or upload them to a public bug report.

Opening the Discord EXE directly and `discord://` links are outside the scope of the shortcut flow. The coordinated Windows startup opens the official Discord with fixed proxy arguments. Shared Start menu shortcuts may be read but are not changed. Voice/video UDP traffic stays direct. Success is not guaranteed for every internet provider or for all voice/streaming features.

Logs are in `%LOCALAPPDATA%\DiscordAccess\v3`. Do not move the package folder while the connection is active. The project is unsigned; it does not claim Smart App Control or anti-cheat approval.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, tests and the contribution workflow, [CHANGELOG.md](CHANGELOG.md) for release changes, and the [security notes](docs/SECURITY-NOTES.md) for trust boundaries. Release steps are in [RELEASING.md](docs/RELEASING.md). v4.22 does not change the existing official Discord/profile/network flow. Logs, account/profile files and local test output are not included in the repository.

For normal bugs, use the [Issues](https://github.com/alpertknn/DiscordAccess/issues) template; include the program version, your Windows or macOS version, what you did and the short error code. Read the [SECURITY.md](SECURITY.md) policy before writing about a security issue in a public issue or PR. Do not share your Discord profile, the recovery `journal.json` file, account tokens or signed attachment links. If local logs contain personal paths, remove those parts before sharing.

## License

DiscordAccess's own C#/JavaScript code and its own build tools are licensed under [MIT](LICENSE). The Go files under `engine-patches/` that are derived from upstream sing-box/sing sources remain under GPL-3.0-or-later; this folder is not claimed to be relicensed as MIT. Notices for the separate engine and native components are in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) and [ENGINE-THIRD-PARTY-NOTICES.txt](licenses/ENGINE-THIRD-PARTY-NOTICES.txt); the version/source/hash inventory is in [engine-source-manifest.json](licenses/engine-source-manifest.json). Each component's own license applies. `LICENSES.txt` in the ready-made package contains these texts together; the corresponding engine source package is a separate release file. The Discord app is not distributed in this repository; this project is not an official Discord product.
