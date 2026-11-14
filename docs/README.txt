DiscordAccess v4.23.1 — Windows x64 package

Extract the entire ZIP into a short, permanent local folder. DiscordAccess.exe
and engine/sing-box.exe must stay in the same package. Baslat.cmd was removed.
GitHub Code > Download ZIP contains source code only; get the ready-made ZIP
from Releases.
https://github.com/alpertknn/DiscordAccess/releases

Upgrading from an earlier version / changing folders:
1. If "Start with Windows" is on in the old DiscordAccess, turn it off first.
2. Close Discord completely; choose "Exit" in the old DiscordAccess.
3. Extract the entire new package into a separate, short, permanent folder. Do
   not overwrite the running package or move its folder.
4. Prepare the connection with the new EXE. If you want automatic startup,
   turn "Start with Windows" back on from the new location; an off preference
   is kept.

First launch:
1. Close Discord completely from the system tray. If an old DiscordAccess is
   open, close the CMD version with Q + Enter and the tray version with "Exit".
2. Open DiscordAccess.exe as a normal user. No CMD window stays open.
3. Wait for the "Connection ready" tray notification; then open Discord from
   your own icon. If there is no compatible personal shortcut, choose
   "Open Discord" from the tray.
   Your original official Stable installation and account are used; no copy is
   created.
4. Before exiting, close Discord completely; then choose "Exit" from the
   DiscordAccess tray. If you exit while Discord is open, Discord is not killed;
   the recovery record is kept.

Tray menu:
- "Open Discord": opens the selected official Discord with the ready
  connection.
- "Reconnect": clears a failed connection and prepares it again.
- "Check connection": runs an anonymous diagnosis of the current connection.
- "Start with Windows": turns automatic startup on/off.
- "Exit": closes the connection and the app's own engine.
The icon may be in the hidden icons (^) area. If the EXE is opened again, no
second connection or second engine is created; a status request is sent to the
existing session.

Windows startup:
After the first successful connection, automatic startup is prepared for the
current user. If the selected official Discord has a recognized personal Run
entry, its original command and enabled/disabled preference are kept in a
private record. At Windows sign-in, DiscordAccess prepares the connection first;
if Discord's automatic startup preference is enabled, it then opens Discord
once. If Discord does not start automatically, only DiscordAccess runs in the
tray. Opening the EXE manually in the normal way does not start Discord on its
own.

Turning off "Start with Windows" restores the original Discord startup entry
without overwriting changed user data. The off preference is kept on later
manual launches. A normal "Exit" does not turn this preference off. For an
enabled HKLM or Startup-folder entry, or for unrecognized or multiple Discord
entries, no ordering coordination is set up and a warning is shown; those
entries are not changed. StartupApproved preferences are not written.
If the recognized Discord entry is recreated, the tray checks it every five
seconds. If an independent Discord entry is seen at sign-in, the order cannot
be verified and a warning is shown; an open Discord is not force-closed or
treated as prepared.

Do not move or delete the package while the connection is open. If you are
moving it to another folder, first turn off "Start with Windows", then enable it
again in the new location. If long startup commands hit the Windows 260
character limit, choose a shorter permanent folder. The startup preference
belongs only to that Windows user.

Personal installation (since v4.22):
If other standard users can write to the desktop/download package, a normal
manual launch copies the package under
%USERPROFILE%\DiscordAccess\packages\<app-hash> and
restarts with the verified EXE. The source package stays in place; existing
folder/file permissions are not changed. Only the EXE, the hash-pinned engine
and the three usage/license/source texts are copied; no profile, settings or
log file is moved. If the user folder and its parent folders are not secure
either, the installation is refused. A different existing file is never
overwritten. Windows startup, restore, console mode and the Discord-opening
shortcut do not create a new installation. An off startup preference is kept;
if wanted, turn "Start with Windows" on again from the new tray session.

One DiscordAccess and one sing-box engine are enough. Discord's Electron child
processes are normal; they do not count as separate Discord copies and are not
killed. Global Windows proxy/DNS/routing settings do not change; no driver or
service is installed. Voice/video UDP traffic stays direct, as in the previous
version. Shortcuts keep their real Discord target; temporary proxy arguments
are added. Shared shortcuts are not changed. Direct Discord EXE / discord://
shortcuts are out of scope.

Custom installation path: in PowerShell or CMD, from this package folder, run
  .\DiscordAccess.exe --discord-path "D:\Programs\Discord"
The folder, Discord.exe, Update.exe or an app-version folder is accepted. The
path is remembered automatically; the version is found again on the next
launch. PTB/Canary, junctions/symlinks and network shares are not supported;
the whole disk is not scanned.

Advanced commands:
  .\DiscordAccess.exe --console         Q/T/D command-line mode
  .\DiscordAccess.exe --restore         Restore connection and startup while Discord is closed
  .\DiscordAccess.exe --help            Usage/DNS options
  .\DiscordAccess.exe --verify-package  Verify the package without running the engine

To switch to console downloads while the tray is open, first close Discord
completely and choose "Exit" from the DiscordAccess tray. In the package
folder, run
  .\DiscordAccess.exe --console
and when the connection is ready, paste the attachment link with D + Enter.
Opening a second EXE does not turn the open tray session into a console.

If the Discord app/core module updates, close Discord completely; exit
DiscordAccess and open it again. Existing download support is kept.
The EXE, updater and app.asar/core.asar are not changed. Temporary download
support is prepared in the recognized module entry, along with only two update
endpoints in settings.json. The user's other preferences are kept on restore.

Shortcut recovery after a Windows restart:
v4.23.1 fixes the launch error caused by an empty or partially written shortcut
restore file. Fully close Discord and open this version normally; it recovers
the existing shortcut record using a new temporary file for each attempt.
There is no need to delete the recovery record. Unknown temporary file contents
and user changes to the actual shortcut are preserved.

Logs: %LOCALAPPDATA%\DiscordAccess\v3
Startup recovery: startup-session\journal.json (personal data; do not share).
Requires Windows 10/11 x64, .NET Framework 4.8 and a writable official Discord
Stable. Do not run as administrator. EXEs are subject to Windows security
evaluation.

This version keeps the v4.20 protections; it checks the permissions of the
temporary executable support files and recovery files themselves.
Open/status/startup signals live in a per-user namespace; a lock handover does
not allow a second connection. The engine's limits before HTTP authentication:
128 pending connections, an absolute 5 seconds, and 64 KiB total for the request
line and headers. Authenticated large file bodies and long CONNECT/WSS tunnels
are not subject to these size/time limits. The 1.14.3-discordaccess.2 engine is
built with pinned source changes and Go/dependencies.

For automatic startup, the package must be in a permanent personal folder that
only your own account can write to. Targets or parent folders that other
standard users can write to are refused; Windows permissions are not changed.
After a file conflict, .discordaccess-fs-*.preserved / .rollback-preserved
backups may be kept. Do not delete them or upload them to a public bug report.

GitHub Actions was removed. The Windows build, regression tests and the check
that the engine accepts the six DNS settings are run manually in an approved
development environment. A blocked EXE is not opened by another method;
security settings are not changed. The completed checks and commit details are
stated in the related PR. Messages/files/streaming with a real account, Windows
restarts and tray interaction are separate from these automated tests.
A user confirmed that Windows restart and normal launch succeeded with a local
build containing this same shortcut recovery fix. This is separate from the
automated checks; it does not establish messaging, file transfer or streaming
coverage for the final release build.

v4.23.1 fixes interrupted shortcut recovery and retains v4.23's connection
startup improvements. The engine, official Discord target, existing profile
and connection flow do not change. Old source tags are kept; do not
redistribute old binary packages with incomplete notices.

The app's own code is MIT-licensed. The GPL-3.0-or-later terms of the engine
code derived from upstream and the native components' own licenses are kept.
The engine and embedded dependency/Go runtime licenses are in LICENSES.txt.
Pinned source versions and the notice manifest are in the GitHub licenses/
folder. The engine's source and rebuild package is provided with the
distribution. The package contains no test, log or profile files. Success is
not guaranteed for every provider/voice/streaming condition. Hash verification
is not a Windows code signature.

For normal bugs, use the GitHub Issues template; the version, Windows build,
steps and a short error code are enough. For sensitive security findings,
first read the policy at
https://github.com/alpertknn/DiscordAccess/blob/main/SECURITY.md
Do not share tokens, profiles, journals, signed URLs or personal paths.
