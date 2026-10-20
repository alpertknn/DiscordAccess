# DiscordAccess for macOS — design

This document explains how the macOS version works and why. It describes the code released as **macos-v0.2**. The code is the source of truth; code comments cite sections of this document, for example "design 8". Usage is in the [macOS README](../README.md), and a short summary of the trust boundaries is in [SECURITY-NOTES.md](SECURITY-NOTES.md).

## 1. Purpose

DiscordAccess for macOS reproduces the Windows v4.22 flow. For the user the flow is the same; only the operating system mechanisms change.

The first target was a build from source on an Apple Silicon Mac with macOS 27, verified live on a network that actually blocks Discord. The design is meant to carry over to public releases; signing, notarization and packaging belong to stage 3.

The app is written in native Swift. The sing-box engine, the patches in `engine-patches/` and the attachment download hook (`bridge/attachment-download.cjs`) are shared by both platforms.

## 2. Constraint: the Windows side stays as it is

- Everything the macOS version needs lives under `macos/`. Outside it, only shared documents (the root README, CHANGELOG, CONTRIBUTING, SECURITY and the issue templates) mention macOS.
- Shared inputs are read only: `bridge/attachment-download.cjs`, `engine-patches/http-limits/` (manifest and `source/`), `engine-build/go.mod` and `engine-build/go.sum`.
- A Mac-specific change to the engine source is out of scope; it would need its own decision and review.

Reasons, from the repository:

| Evidence | Consequence |
| --- | --- |
| `Test.cmd` compiles `src\*.cs`; `Build.cmd` compiles the `build-production.rsp` list. | No files are added under `src/`. |
| `scripts/ReleaseChecks.py` rejects undeclared files under `engine-patches/http-limits/source/`, and the Windows engine hash depends on these inputs. | The patches and their manifest do not change. |
| The hash of `scripts/Build-SecurityEngine.ps1` is pinned in `licenses/engine-source-manifest.json`. | The Mac engine is built by its own script. |
| `docs/README.txt` goes into the Windows package (`scripts/Package-Release.py`). | The Mac documents live under `macos/`. |
| The release script (`exact_head`) stops when an untracked file is not ignored. | Mac build outputs are kept out of Git by `macos/.gitignore`. |

## 3. Findings the design rests on

Read in October 2026 from the installation on the development Mac and from Discord's own code (`app.asar`).

- **Installation:** `/Applications/Discord.app` 0.0.397, bundle id `com.hnc.Discord`, Team ID `53Q6R32WPB` (Discord, Inc.), hardened runtime, universal binary, Electron 37.6.0. `Info.plist` sets `NSAllowsArbitraryLoads = true`.
- **Updater:** the code of 0.0.397 read the `USE_NEW_UPDATER` setting as off by default on macOS, but on this Mac it was `true`, and Discord used the same new native updater (`updater.node`) as on Windows. In 0.0.415 the setting is in `OBSOLETED_SETTINGS`, and the new updater is the only one on macOS too. The update and module layout on the Mac is the same as on Windows.
- **The new updater's network behavior:** it fetches its manifest from `NEW_UPDATE_ENDPOINT` (by default `https://updates.discord.com/`) and downloads with its own HTTPS client, which ignores Chromium's proxy. On the blocked network it failed with "The certificate was not trusted"; Discord on this Mac had not been able to update for months.
- **Host app update:** the updater downloads the new version into `app-<version>/Discord.app`, and ShipIt installs it over `/Applications/Discord.app`.
- **Legacy updater (no longer used):** Squirrel.Mac went to the system network. When its check failed, the splash screen retried forever at 10–60 second intervals and never opened the main window (0.0.397 code).
- **`chromiumSwitches`:** this `settings.json` setting applies switch names only, never values, so `--proxy-server` cannot be passed this way.
- **Core module:** loaded with `requireNative("discord_desktop_core")`, immediately followed by `core.startup(...)`. The path has the same shape as on Windows: `~/Library/Application Support/discord/app-<version>/modules/discord_desktop_core-<N>/discord_desktop_core/index.js`.
- **Other apps' data (TCC):** Discord's data folder is labeled with `com.apple.macl`. macOS blocks other apps from reading it (`kTCCServiceSystemPolicyAppDataDetailed`) and shows no permission prompt. This applied to Apple's own Terminal as well; Terminal could reach the folder once it had Full Disk Access. A folder that Terminal created itself was not labeled.
- **Network:** on the development network, `discord.com` resolves to the ISP's block address and HTTPS connections fail, so live checks ran against a real block.
- **Tools:** Xcode 27, Swift 6.4 and macOS 27.0.1 on arm64. Go and .NET are not needed on the machine; the engine build script downloads its pinned Go toolchain itself.

### Stage 0 results (2026-10-14, Discord 0.0.397 → 0.0.415)

Checks that needed a real account were confirmed on screen by the user.

| Check | Result |
| --- | --- |
| Engine build (darwin/arm64) | SHA-256 `013ba5d5055988953ebec1a1ac0eceb8aead52415e3ef186a5c7481083472cb0`, 33,315,362 bytes, cdhash `344f8bd088ce068a1745cf16e7bb0c9a4bbbe99e`, ad-hoc signature. The Go linker records the identifier as `a.out`. |
| Reproducibility | Two independent builds, with clean module and build caches, are byte-identical. |
| Go tests of the patches | `httpguard`, `protocol/http`, `common/listener`: all `ok`. |
| DNS configurations | All six pass the engine's `check` command. |
| API through the gate | `/api/v10/gateway` → `{"url":"wss://gateway.discord.gg"}`; `example.com` → 403. |
| Update bridge | The legacy Squirrel feed and the new updater's manifest returned 200. All 35 download addresses in the manifest were rewritten to the bridge; the only target domain was `stable.dl2.discordapp.net`. |
| Host app update | A real update went through the bridge: 16 downloads, about 240 MB, no errors. ShipIt installed it, and Discord opened as 0.0.415 with its official signature (`com.hnc.Discord`, `53Q6R32WPB`). |
| Experiment A (launch with arguments) | The account loaded; messages and an image were sent. An attachment download went to the browser, so the Windows hook is needed. |
| Peer identity | All 10 sockets connected to the gate belonged to `Discord Helper.app`, signed as `com.hnc.Discord.helper`, and met the Team ID requirement. 493 processes were scanned in 1 ms. |
| Experiment B (Dock, wrapper) | Opened with `open -a` and with a real Dock click, the wrapper applied the proxy both times (`PROXY 127.0.0.1:<gate>;DIRECT`). `gateway.discord.gg` went through the gate and the user saw messages. |
| Refused by the gate | `api.spotify.com`, `dealer.spotify.com` and `*.dc-telemetry.net`, as with the Windows allow list. Spotify inside Discord does not work. |
| Rollback | The owned keys returned to their original values, and the core entry file returned to the official one, verified by hash. Discord 0.0.415 removed the `USE_NEW_UPDATER` key itself. |
| Profile access | Discord's data folder was reachable only from Terminal with Full Disk Access (see the TCC finding). |

## 4. User flow

1. Quit Discord completely.
2. Open `DiscordAccess.app` and wait for the "Connection ready" notification.
3. Open Discord from its own icon (Dock, Launchpad, Spotlight). The existing installation and account are used; no copy is made.
4. When done, quit Discord first, then choose "Quit" in the menu bar.

Differences the user will notice:

- The icon is in the menu bar, not in a tray.
- Discord's own login item cannot be read or taken over by a program. The "Also open Discord at login" preference is in the DiscordAccess menu, and Discord's own login item should be turned off.
- Instead of copying a package from an unsafe folder into the user's folder, as on Windows, the app is moved to the Applications folder.
- Opening Discord from its own icon works as on Windows, by a different method (see 7.2).
- On first launch DiscordAccess needs a one-time **Full Disk Access** permission. macOS protects Discord's data folder from other apps and shows no prompt for it (see 3, the TCC finding). The app never turns the permission on itself; it explains why it is needed and opens the right page of System Settings.

## 5. Windows → macOS mapping

| Windows v4.22 | macOS |
| --- | --- |
| `DiscordAccess.exe` in the tray | `DiscordAccess.app` in the menu bar (`LSUIElement`, no Dock icon). Menu: "Open Discord", "Reconnect", "Check connection", "Start at login", "Also open Discord at login", "Quit". |
| sing-box `1.14.3-discordaccess.2` with a pinned hash | The same source commit, Go version and patches, built for `darwin/arm64`, with its own pinned hash and cdhash. |
| Engine configuration generated in memory | The same policy: DoH, IPv4, authenticated HTTP inbound, TLS record fragmentation, rules and a final `reject`. |
| `ProcessGate` | The same policy; the peer process is found with libproc and identified by Discord's Team ID signature. |
| Proxy arguments on the `.lnk` shortcuts | The core wrapper applies the session proxy at run time; "Open Discord" also passes it as arguments. |
| `UpdateBridge` (native updater) | The same bridge. The Mac uses the same native updater, so the manifest and download rewriting rules are the same; confirmed with a real update in stage 0. |
| Direct access to `%LOCALAPPDATA%\Discord` | `~/Library/Application Support/discord` needs Full Disk Access (TCC). Without it, no profile change is made and the user is guided. |
| Attachment download hook | The same file, unchanged. |
| Run entry and coordination with Discord's Run entry | An `SMAppService` login item; the open-Discord-at-login preference lives in DiscordAccess. |
| Named objects, mutex, file lock | A `flock` lock in the user's private state folder; Launch Services hands a reopen of the app to the running instance. |
| ACL and parent-folder trust, personal package copy | POSIX ownership, permission and macOS ACL checks. No login item from an untrusted or translocated location; the user is asked to move the app to Applications. |
| `WinVerifyTrust` and "Discord Inc." | Security.framework: Discord's designated requirement for each executable (its signing identifier, `anchor apple generic`, Developer ID, Team ID `53Q6R32WPB`). |
| `GetExtendedTcpTable` | libproc (`proc_pidfdinfo`, `PROC_PIDFDSOCKETINFO`). |
| Job object | None. The engine is watched as a child process; an engine left behind by a crash is stopped at the next launch. |
| Refusal to run as administrator | Refusal to run as root (`geteuid() == 0`). |
| `--console`, `--restore`, `--discord-path`, `--dns=…`, `--dns-record-fragment`, `--verify-package`, `--help` | The same options, from Terminal through `DiscordAccess.app/Contents/MacOS/DiscordAccess`. |

Unchanged principles: no system-wide proxy, DNS or routing setting, TUN, network extension, driver or administrator requirement is added; the `Discord.app` bundle is never modified; a normal Discord process is not killed at shutdown; a manual launch does not start Discord by itself.

## 6. Architecture

```
macos/
  .gitignore                    .build/, build/, work/
  README.md                     usage notes, also shipped in the release package
  Package.swift                 SwiftPM; macOS 13 target; Swift 5 language mode
  Sources/DiscordAccessKit/     testable core without a user interface
  Sources/DiscordAccess/        menu bar app and command-line entry
  Tests/DiscordAccessKitTests/  Swift tests
  Tests/build-engine.test.py    offline tests of the engine build script
  Tests/wrapper.test.cjs        Node test of the generated wrapper (stand-in Electron)
  Resources/                    Info.plist and the login item's launch agent
  scripts/build-engine.py       macOS engine from pinned Go, source and patches, with hash verification
  scripts/build-app.sh          assembles the .app under build/apps.noindex/<time>/ (Spotlight skips it) and embeds the engine and the hook
  scripts/release.py            license inventory, source archives and release assets (stage 3)
  licenses/                     notices and the engine's source manifest
  docs/                         this design and the trust boundaries
```

All tests live under `Tests/`; there is no separate `tests/` folder. The default macOS volume is case-insensitive, so `tests/` and SwiftPM's `Tests/` would be the same folder.

### DiscordAccessKit components

| Component | Responsibility | Windows counterpart |
| --- | --- | --- |
| `EngineConfig` | Builds the engine policy in memory. | `Package.Config` |
| `EngineImage`, `EngineProcess` | Verify the engine against its pinned SHA-256; start it suspended, check its cdhash, resume it with a random credential; check that the engine owns its listener. | `DnsConnection`, `TcpOwner.OwnsListener` |
| `DnsOptions`, `DnsSelection`, `DnsConnection` | The DNS choice; automatic DoH selection (cloudflare-ip → cloudflare → google); one candidate's engine and gate. | `DnsOptions`, `DnsSelection`, `DnsConnection` |
| `ProxyGate`, `RequestPolicy`, `Relay` | The local proxy: peer admission, request policy, engine credential, engine identity and the two-way relay. | `ProcessGate` |
| `PeerAdmission`, `ProcessInspector` | Socket → process ID, start time, user, executable and code signature; which processes may use the gate. | `TcpOwner.Find`, `ExternalDiscordScope`, `TrustedImage` |
| `UpdateBridge`, `BridgeRequest`, `BridgeResponse`, `UpdateRewriter` | The loopback update bridge; rewrites manifest and redirect addresses to the bridge. | `UpdateBridge` |
| `DiscordInstallation`, `DiscordApplication` | Finds and verifies `Discord.app` (including `--discord-path`); the approved executables and their signing requirements. | `DiscordInstallation`, `DiscordPathOptions` |
| `DiscordProfile`, `StateStore`, `DiscordProfileChanges`, `SafeFile`, `SessionWrapper` | Discord's data folder and DiscordAccess's state folder; the two `settings.json` keys and the core wrapper; journaled changes and their undo; careful file operations. | `InstalledDiscord`, `FileTransaction`, `PrivateFileTrust` |
| `Launcher` | Opens Discord through Launch Services (`/usr/bin/open`) with arguments. | `ResidentController` (open path) |
| `SessionController` | The session lifecycle (7.1–7.3). | `Controller` |
| `ResidentLifecycle` | The menu bar app's session: shared connect requests, bounded shutdown, preferences. | `BackgroundLifecycle` |
| `SessionLog` (with `SessionLock`) | The session log and the single-session lock. | `SessionLog`, `ResidentInstance` |
| `StartupRegistration` | The login item and location trust. | `StartupCoordinator`, `StartupPathTrust`, `StartupPackage` |
| `ConnectivityProbe`, `GatewayHello` | HTTPS checks and the anonymous Gateway HELLO. | `ConnectivityChecks`, `GatewayChecks` |
| `CommandOptions`, `ConsoleProgram`, `ConsoleSession`, `ConsoleOutput` | Command-line modes, options and Terminal mode. | `Program.Main`, `ConsoleHost`, `ResidentOptions`, `ConsoleOutput` |
| `AttachmentDownloader` | Attachment download from Terminal. | `AttachmentDownloader`, `ConsoleDownload` |
| `MenuState` | What the menu shows: the status line and the available actions. | `TrayHost` |

The app target (`Sources/DiscordAccess`) holds the AppKit menu bar app (`MenuBarApp`, Windows `TrayHost`) and the entry point, which finds the embedded engine and hook.

## 7. Data flow

### 7.1 Opening a session

1. Preliminary checks:
   - Running as root stops here.
   - The single-session lock is taken. If another instance holds it, the new one says it is already running and exits. Reopening the app bundle goes to the running app through Launch Services, and the app shows its status.
   - `Discord.app` is found and its signature verified.
   - If Discord is open, connecting stops (DSC-E05) and no profile change is made. If the process list cannot be read completely, Discord counts as open (DSC-E04). Copies of Discord in other locations count too, and so does a renamed app running Discord's main or helper executable. A live process that cannot be read counts if the kernel names it Discord (Windows also treats an `Unknown` observation as an obstacle).
   - A recovery journal left by an earlier session is undone.
   - Access to Discord's data folder (Full Disk Access) is checked. Without it, no profile change is made; the user is told why and the relevant page of System Settings opens.
2. DNS selection: candidates are tried in order. For each one, an engine and a gate start, and an HTTPS request to the Discord Gateway API (`https://discord.com/api/v10/gateway`) goes through the gate; the answer must be the Gateway address (Windows `CheckDnsCandidate`). The first successful candidate joins the session; a failed one is closed completely before the next one starts. If the user named a provider (`--dns=…`), no check is made. The anonymous Gateway WebSocket HELLO check runs from "Check connection".
3. The update bridge starts on a random port with a secret path token.
4. Profile changes, journaled:
   - `settings.json`: `UPDATE_ENDPOINT = <bridge>/legacy` and `NEW_UPDATE_ENDPOINT = <bridge>/updates/`.
   - `discord_desktop_core/index.js`: the session wrapper (7.2).
   - Every core entry of the installed version (`build_info.json`) is wrapped: `app-<version>/modules/discord_desktop_core-<N>/discord_desktop_core/index.js` for the new updater, or `<version>/modules/discord_desktop_core/index.js` for the legacy Mac updater. Both trees can coexist; all recognized entries are validated before any is written, with at most 16 entries across both layouts. No other host version is searched. Each entry file must be Discord's own entry or an old wrapper whose journal was lost; other content gives PRF-E02, and a missing module PRF-E03. TCC permission errors (`EPERM`) retain PRF-E04 instead of being treated as missing files.
   - New journals use schema 2 and record a fixed layout identifier and a module number (0 only for the flat legacy core). Recovery constructs paths from these templates and the validated host version; it never accepts an arbitrary relative path. Schema-1 records from macos-v0.1/v0.2 remain recoverable as new-updater entries. Recovery uses the recorded layout, without rediscovering whichever layout happens to exist at shutdown.
   - Every change is written to the journal in the state folder first (`installed-session/journal.json`). Undoing, and recovery at the next launch, put back only what still belongs to DiscordAccess. A value the user or an update changed stays as it is and is reported with PRF-E01, and the journal is kept (Windows `InstalledDiscord`).
5. The ready notification appears. On a login launch with "Also open Discord at login" on, Discord opens once.

### 7.2 When Discord opens

**Update check**

- The new native updater reaches the bridge through `NEW_UPDATE_ENDPOINT`. `UPDATE_ENDPOINT` is redirected too, for the legacy updater, as on Windows. The bridge is on loopback, so no proxy is needed to reach it.
- The bridge verifies that the client is an admitted Discord process and forwards the request through the gate and the engine.
- The path mapping is the same as on Windows:
  - `/legacy…` → `https://discord.com/api…`
  - `/updates/…` → `https://updates.discord.com/…`
  - `/download/<domain>/…` → `https://<domain>/…` (the domain must be covered by `ProcessGate.AllowedDomains`)
- Download addresses in manifests and redirects are rewritten to the bridge. ShipIt installs host app updates and verifies Discord's signature itself.
- The bridge's limits are the same as on Windows:
  - at most 16 connections; a request header of at most 16 KiB, within 10 seconds;
  - only GET and HEAD without a body; the client's headers are not forwarded to Discord;
  - 180 seconds in total per request, download included. URLSession allows at most 30 seconds between two packets (Windows waits 180 seconds for the first response; here the first byte must also arrive within 30 seconds);
  - a manifest of at most 4 MiB and 64 levels.
- Requests go through the gate with URLSession; the gate admits them with this process's diagnostic permission. Downloads stream through a URLSession delegate: while the delegate writes to the client, URLSession waits before reading more. In a 600 MB transfer to a slow consumer, memory stayed around 12 MB.

**Applying the proxy**

- "Open Discord" opens Discord through Launch Services with `--proxy-server=http://127.0.0.1:<gate> --proxy-bypass-list=127.0.0.1;localhost --disable-quic` (see 8, "Session and Terminal mode").
- A launch from the Dock, Launchpad or Spotlight has no arguments. The session wrapper then applies the proxy at run time. In outline:

```js
// DiscordAccess macOS session wrapper v1
if (process.type === 'browser') {
  // electron = require('electron'); each step below fails on its own, silently
  if (electron.app.commandLine.getSwitchValue('proxy-server') !== proxy) {
    // session.defaultSession.setProxy({ proxyRules: proxy + ',direct://', proxyBypassRules: '127.0.0.1;localhost' })
    // every webContents.loadURL waits until the proxy is set (10 seconds at most)
  }
  // the embedded attachment download hook:
  // hook.install(electron, { env: { DISCORDACCESS_ATTACHMENTS: '1', DISCORDACCESS_DOWNLOAD_LOG: '<download log path>' } })
}
module.exports = require('./core.asar');
```

- How it behaves:
  - The load barrier, installed on `app.on('web-contents-created')`, holds every `loadURL` call until `setProxy` completes.
  - With the `,direct://` fallback, Discord behaves as it did before DiscordAccess when the gate is down. Success is therefore judged by the connections in the gate's log, not by Discord opening.
  - The hook is installed with the same call as in the Windows wrapper. Discord must be able to read the hook (see 12), so it is embedded in the wrapper. The embedded hook is `bridge/attachment-download.cjs`; its SHA-256 is pinned in the Kit, and a different file gives PKG-E05. The hook writes its records into Discord's own folder (`DiscordAccess-downloads.log`).
  - The generated wrapper is tested with Node and a stand-in Electron (`Tests/wrapper.test.cjs`): `setProxy` and the load barrier when no proxy was given; pages still load when `setProxy` fails; no extra work when Discord was opened from the menu with the proxy argument; the hook is installed; processes other than the main process, and an Electron failure, leave Discord untouched.

**Traffic**

- Window traffic (HTTPS and WSS) takes this path: Discord Helper (the network process) → gate → engine → out directly, with DoH and TLS record fragmentation.
- Voice and video UDP traffic stays direct, as on Windows.

### 7.3 Shutdown and recovery

- On "Quit", the profile changes are undone, then the bridge, the gate and the engine close.
- If Discord is open, the recovery journal is kept. The undo happens at the next launch or with `--restore`.
- An undo writes back only owned values. Values the user changed later, and other preferences, are never overwritten.

### 7.4 Start at login

- The app registers only from a trusted location (`StartupRegistration.verifyLocation`):
  - every folder from the root down to the bundle, and every file inside the bundle, belongs to the user or root;
  - other users cannot write to them; the group may write only if it is admin or wheel (as in `/Applications`, whose admin members can become root anyway); no ACL entry grants changes;
  - there is no symbolic link in the path or in the bundle;
  - the app is not under App Translocation;
  - the volume is local and does not ignore ownership. macOS ignores ownership on external drives by default; there every file appears to belong to whoever asks.
- A login launch (`--startup`) checks the location again. If it is no longer trusted, the registration is removed, Discord is not opened and the user is warned (Windows refuses a startup entry for another path).
- Registration uses `SMAppService.agent`. The launch agent inside the bundle (`Contents/Library/LaunchAgents/`) starts the app with `--startup`, which tells a login launch apart from a manual one.
- Turning off "Start at login" removes the registration. A normal quit does not change this preference.
- After the first working connection, if the user has not chosen yet, start at login turns itself on, as on Windows (`EnsureEnabled`). Once the user turns it off, it stays off. The choice is written to `preferences.json` in the state folder before the registration changes. If that file exists but cannot be read safely, the app does not register itself.
- "Also open Discord at login" is off by default. When it is on, Discord opens once after the first working connection at login. If connecting fails, the request is kept until a later "Reconnect" succeeds; a failed open is not repeated. An open Discord is not opened again.
- If Discord opened before the connection was ready, a warning is shown; Discord is not forced to quit.

## 8. Security boundaries

**Gate policy**, the same as Windows `ProcessGate`:

- the `AllowedDomains`, `UploadHosts` and `ChallengeDomains` lists;
- CONNECT only to port 443, except for the `discord.media` domain, which may use any port;
- a plain HTTP `Upgrade` is refused;
- a request header of at most 32 KiB, ASCII only;
- at most 64 concurrent connections;
- logs hold no URL, header or body.

**Engine:**

- It listens only on loopback, with a random credential.
- Before forwarding a request, the gate verifies that the listener belongs to the engine process.
- The engine starts suspended. If its cdhash does not match the pinned value, it is killed before any of its code runs. The file is measured again on every start.
- The Go linker's ad-hoc signature records the identifier as `a.out`. The engine is embedded in the bundle with that signature, unchanged (`Contents/Helpers/sing-box`). Re-signing would change the pinned SHA-256 and cdhash, and signing with a personal certificate would make the SHA-256 differ on every Mac. The pinned values and the app's signature protect it together.

**Request policy** (Windows `RewriteHeader`): all Windows test vectors pass, with their refusal categories. Absolute `http://` targets are checked more strictly than Windows' `Uri` parsing:

- The host name must already be in DNS form and contain only letters, digits, `-` and `.`. So `%`, `[` (IPv6) and `@` (user info) are refused in the host part; they are allowed in the path and query.
- An explicit port must be exactly `80`.
- `#`, `\`, spaces and control characters are refused anywhere in the target.

**Peer admission** (Windows `ExternalDiscordScope` and `PermittedOwner`):

- The client end of a connection is looked for only among candidates: DiscordAccess itself and processes running one of the approved Discord executables. The candidate that holds the end must pass the admission criteria below; Windows, too, checks the single owner that `TcpOwner.Find` finds. No other process's descriptors are inspected. If an unapproved process holds the connection, the refusal reason is `NoHolder`.
- Why only candidates: inspecting a descriptor with `proc_pidfdinfo` takes a temporary reference to it. If the owner closes the descriptor at that moment, the kernel drains the socket, and reads fail with `EBADF` for good in every process that shares it. A child being spawned holds copies of all its parent's descriptors until `exec`, and closes them at `exec`. An earlier version that scanned all of the user's processes could therefore break other apps' sockets: with scanning and spawning together, 2–8 of 16 blocking reads ended with `EBADF`, and the socket stayed broken.
- A process that has not completed `exec` (no `PROC_FLAG_EXEC`) or is exiting is never inspected; with this filter, more than ten thousand scans in the same experiment produced no error. In XNU, `exec_mach_imgact` runs `fdt_exec`, which closes the pre-`exec` copies, before it sets `P_EXEC`; when the flag is visible, the copies are closed. After reading, the process is read again; if it began exiting, or its process ID moved to another process meanwhile, the result is discarded. The copies held by a Discord process being spawned are therefore never seen. The engine's identity is verified only with the engine process's own descriptors. A test catches the failure on every run when the filter is removed.
- Remaining risk, accepted: if a candidate (a Discord process or the engine) closes a socket it shares with another process at the very moment it is inspected, or runs `exec` again, that socket drains. This needs a coincidence within microseconds. Every socket of a candidate is inspected (TCP, UDP or local), because a socket's type is known only by inspecting it. The damage is a broken Discord connection, or a broken local socket shared between Discord processes. The user's other apps are no longer looked at, so they are not affected. The kernel's TCP list (`net.inet.tcp.pcblist_n`) shows an unprivileged process only its own sockets, so there is no way to find the owner without touching descriptors.
- If a candidate's process ID moves to another process between candidate selection and inspection, that process is inspected too. Process IDs are assigned in sequence, so this does not happen in practice; if it did, the result would still go through that process's admission check.
- Ends that are not found: a socket a candidate holds as a Mach fileport, or one in transit inside an `SCM_RIGHTS` message. These need the same user's cooperation and are out of scope; the outcome is on the safe side (refusal).
- The process list and the candidates' descriptor lists are read completely: when a buffer fills up, it is enlarged and read again; if a list is still incomplete, the connection is refused. On the development Mac, the same user's 437 processes were listed in 0.3 ms.
- Refusals that may pass on a second look (an incomplete list, a process or owner that changed during the check, a signature check) are retried three times, 10 ms apart, like Windows `FindPeer`. A connection no candidate holds is refused at once: looking again would not change the result, and otherwise every connection from another local user would hold a gate slot for 30 ms. The candidate search compares file names case-insensitively and verifies the path with `realpath`. The file name is taken without asking the file system, so a process running from an unresponsive network volume does not hold up connections.
- Admission criteria:
  - the process belongs to this user;
  - it started after the session became ready (an equal time is accepted);
  - the real path of its executable (links resolved, letter case as on disk) is one of five files in the selected `Discord.app`: `Discord` and the four `Discord Helper` executables;
  - its running code satisfies that file's designated requirement in Discord: its own signing identifier, Developer ID and Team ID `53Q6R32WPB`.
- Windows also compares the session ID. The macOS counterpart is the user ID: a user has one graphical session at a time, and the same user's SSH sessions are out of scope.
- An admission is remembered per process instance (process ID, start time, path). At most 256 entries are kept; when the table is full, a new process is refused. Refusals are not remembered. If an admitted process runs the file at the same path again (`exec`), the admission stays and the signature is not checked again; this requires the same user to replace the approved file and is out of scope. The entry is compared before and after the checks, and after the checks the connection's end must still be in the same process.
- The parent is not part of a process instance's identity: a process whose parent dies moves to launchd and stays admitted. A process that exists but cannot be read stays in the list: `anyAdmittedRunning` counts it as running, while admission refuses it (Windows `Unknown`); this keeps the settings from being restored while Discord runs, as on Windows. An exited but unreaped (zombie) process cannot be read either and counts as running until its parent reaps it. DiscordAccess never starts Discord as its own child: Launch Services starts it.
- The signature check runs outside the lock, so a slow check does not hold up the connections of processes already admitted.
- DiscordAccess's own process is admitted only while its diagnostic permission is on.

**Peer signature checks use no network** (`kSecCSNoNetworkAccess`). Online revocation and notarization checks could hold a connection for minutes; Gatekeeper already performs them at launch. The `Discord.app` check at connect and before opening Discord is a full static validation (strict, every architecture, nested code); it stays off the request path.

**No static validation of bundles on the request path.** `SecStaticCodeCheckValidity` registers the bundle's notarization ticket with `syspolicyd` through a synchronous XPC call, which waited between 49 and 256 seconds in tests. The dynamic check of a running process does not use this path: under load, the longest of 1651 checks took 10 ms.

**Electron fuses** (Discord 0.0.415): `RunAsNode` and `NodeCliInspectArguments` are off, `NodeOptionsEnvironmentVariable` is on, and asar integrity validation is off. A process running as the same user can start Discord with `NODE_OPTIONS` and run code inside a Discord-signed process. That attacker is out of scope, as on Windows. All five executables have the `allow-unsigned-executable-memory` entitlement; `Discord` and `Discord Helper (Renderer)` also have `allow-jit`, and `Discord Helper`, which carries the network service, has `disable-library-validation`. A signature proves which file runs, not which code runs inside it.

**Narrowing the allow list (possible):** Chromium's network service runs in `Discord Helper`; the GPU and Renderer processes are sandboxed and open no sockets. Once the real connection owners have been observed end to end, the list could be limited to `Discord` and `Discord Helper`. On Windows every Discord process runs the same `Discord.exe`, so the list effectively covers them all. The test that compares the list with the installed app scans only `Contents/MacOS` and `Contents/Frameworks/*.app`; `chrome_crashpad_handler` under `*.framework/Helpers` and Squirrel's `ShipIt` do not use the gate.

**Gate network layer** (Windows `ProcessGate`):

- Each connection goes through these steps in order: peer admission (refused with `403 Forbidden`), the request header (at most 32 KiB, within 15 seconds, ASCII only), the request policy, the connection to the engine (5 seconds), the engine's identity, writing the header, and the two-way relay. When one direction ends, the sending side of the other socket is shut down; the remaining direction may drain for at most 30 seconds. The steps, time limits, the 64-connection limit and the diagnostic lines are the same as on Windows.
- The engine's identity is verified for every connection: the engine is running, it holds the other end of this connection, and it is still running. The engine is an unreaped child, so its process ID cannot pass to another process between the two checks. The engine accepts connections on its own schedule, so the gate waits at most 1 second for that end. Not a single byte, the credential included, goes to an unverified backend.
- Every socket is close-on-exec. macOS has no `SOCK_CLOEXEC`: a child that another thread starts between opening a socket and setting the flag could inherit the descriptor. Every child process of the app is therefore started with `POSIX_SPAWN_CLOEXEC_DEFAULT`: the engine, and `/usr/bin/open` (for Discord and System Settings).
- Each connection runs on its own thread, with non-blocking sockets and `poll`; peer checks do not hold up the accept loop. Shutdown is signaled by closing the write end of a pipe that every wait includes; no thread closes another thread's descriptor. The accept thread closes the listener; `close()` waits for it, and when it returns the port is free (like Windows `Dispose`). The accept thread may be inside a diagnostic callback at that moment, so `close()` is never called while holding a lock or queue that the callback waits for; diagnostics are written asynchronously (`ConsoleOutput`). Diagnostic lines of finished connections can still arrive shortly after `close()` returns. The gate also checks its own closing state, and a peer admission result that races with `close()` is not used.
- Time limits use a monotonic clock (`ContinuousClock`); changing the system clock neither shortens nor extends them.
- The listener is opened with `SO_REUSEADDR`: a new gate can take a closed gate's port even while old connections are in TIME_WAIT. While the listener is open, no other socket can take the same port on 127.0.0.1.
- An unexpected error while accepting (for example `EMFILE`) closes the gate: first the listener closes, then `faulted` is reported, as on Windows. A gate fault ends the session, as on Windows; the gate is not rebuilt, and errors such as `EMFILE` are not retried. Instead, the app raises its soft descriptor limit (`RLIMIT_NOFILE`) to 4096 at launch, within the hard limit: 64 connections use 128 descriptors, and macOS starts a program with a soft limit of 256.
- Refusals are visible. An ownership refusal is logged as `Failure stage=Ownership target=unknown category=UnownedClient reason=<reason>`; like the line's other variable parts, the reason is filtered before writing and cannot start a new line. The reasons are `NoHolder`, `DiagnosticNotAllowed`, `OtherUser`, `StartedBeforeReady`, `RequirementNotMet`, `UnknownProcess`, `HoldersChanged`, `Unreadable`, `Capacity`, `Closed`, and `UnapprovedExecutable` with the executable's name. An unapproved Discord helper is not a candidate, so it shows up as `NoHolder`; the test that compares the list with the installed app catches new helpers during development. If one of the approved files is missing, `PeerAdmission` reports `missingExecutable`.
- The gate accepts connections only from exactly 127.0.0.1. Another loopback address (for example 127.0.0.2, added to lo0 by root) is refused with `RemoteAddress`.
- The policy sees exactly the bytes that go to the engine: the header is split at the first CRLFCRLF of the raw bytes, and the engine receives the output of `rewrite` followed by the remaining bytes unchanged.
- Another local user can flood the gate with connections. No candidate holds them, so they are refused within a few milliseconds and hold a slot only that long. They can still use up the per-minute quota of diagnostic lines; the same holds on Windows.

**Connectivity checks** (Windows `ConnectivityChecks` and `GatewayChecks`):

- Requests go only through the session's gate: URLSession gets a fixed loopback proxy, with no system proxy, proxy script or direct connection. If the proxy refuses, the request fails at once (CFNetwork error 306 for plain HTTP, 310 or 311 for HTTPS and WebSocket); it never falls back to a direct connection.
- Redirects are not followed, and no cookies, cache or credentials are kept. A body is read only for the Gateway's 200 response, at most 16 KiB; every other response is judged by its header, as on Windows. A 429 that comes with a long, truncated or slow page is therefore still a 429.
- Credentials are never offered: on an authentication challenge, CFNetwork asks once more without credentials, and the 401 is then returned as it is. Header values are fixed; the language list and browser name that URLSession would take from the Mac are not sent (Windows does not send them either).
- Each request has a 10-second deadline. When it passes, the request is canceled, and its closing is awaited at most 5 more seconds. The HTTPS check makes two requests (the Gateway API and the update server); a DNS candidate's check is the Gateway API request alone.
- The Gateway WebSocket check only reads the public HELLO and closes normally; no IDENTIFY, token or account data is sent.
- The gate admits this process with its diagnostic permission, which stays on for the session, as on Windows. URLSession's proxy connection appears in this process as a BSD socket, so peer admission recognizes it.
- Error messages hold only a fixed category, the HTTP status and the response type; never a URL, header, body or error text.
- `discord.com` and `discord.gg` are on the HSTS preload list: URLSession turns plain `http` and `ws` requests into `https` and `wss`. Tests therefore use plain HTTP with `.invalid` names, which resolve nowhere.
- The selected candidate becomes the session's engine and gate. The session learns that the gate closed itself through `DnsConnection.onGateFault` (Windows `Gate.Faulted`) and watches the engine's exit separately (Windows `Engine.Exited`). A gate that failed before the handler was set would have been reported to no one (Windows has the same gap), so the session checks with `ProxyGate.isAccepting`, after setting the handler and at the end of connecting, that the gate still accepts connections.
- The handlers hold the session weakly. A fault that races with shutdown may still be reported once; the session ignores it, because its connection is no longer that candidate.
- A second `DnsConnection.close()` does nothing, even if the first one gave DNS-E03. The session controller is therefore single-use, and reconnecting takes a new controller. "Reconnect" starts no new candidate while an engine could not be reaped.

**Update bridge** (Windows `UpdateBridge`):

- It listens only on 127.0.0.1, on a random port. Every path starts with a random 24-byte token.
- Clients are admitted with `PeerAdmission`, as at the gate: admitted Discord processes, and this process with its diagnostic permission. A refused client gets 403 before its request is read.
- It goes only to allowed Discord domains, over HTTPS on port 443, through the gate. Redirects are not followed; cookies, cache and credentials are neither kept nor offered.
- Addresses in redirects and manifests are rewritten to the bridge only if they are allowed Discord addresses. A redirect that leaves Discord is refused with 400, so the updater cannot be sent elsewhere.
- Diagnostic lines hold only a fixed stage, a category, the HTTP status and a numeric error code; never a URL, token, header, body or error text.

**Session and Terminal mode** (Windows `Controller` and `ConsoleHost`):

- Connecting takes these steps: `Discord.app` is verified and must be closed; the previous session's journal is undone; the data folder must be readable; the hook and the engine are verified; orphaned engines of this bundle's engine path that moved to launchd are stopped; a DNS candidate is selected; the bridge starts; Discord must still be closed; the profile changes are made. If a step fails, everything done so far is undone.
- Backend failure: the engine's exit (watched with a `DispatchSource` process source), or the gate or the bridge closing itself, ends the session. The gate and the engine stop at once; the rest happens after the operation lock is taken, as at shutdown (Windows `BackendFailed`).
- Shutdown: profile changes are undone only while Discord is closed. The changes are kept with their journal (SES-E01) if a Discord process the gate admitted is still running, if a Discord process does not exit within 1 second, if the process list cannot be read, if undoing fails, or if a journal remains. They are undone at the next launch or with `--restore`; `--restore` changes nothing while Discord is open (DSC-E07).
- One session at a time: `flock` on `session.lock` in the state folder. The lock is held until the process ends and is then released by the system, after a crash too, so another instance cannot start while a thread of a timed-out shutdown is still changing the profile. A second instance says that DiscordAccess is already running and exits; `--restore` takes the lock as well (SES-E04). A second instance can therefore neither undo nor overwrite the first session's changes.
- Opening Discord (A): Discord is verified again; if it no longer verifies, it is not opened (SES-E03), and if it is already open, it is not opened twice. If it updated itself to another version, the profile changes move to that version first; if that fails, the session ends. The signature check can take seconds, so it runs without the operation lock; the session must still be the same one when Discord opens. `open` is awaited at most 10 seconds. Discord is opened with `/usr/bin/open -a <Discord.app> --args --proxy-server=… --proxy-bypass-list=127.0.0.1;localhost --disable-quic`. `open` starts with only its standard descriptors, all connected to /dev/null, and with `POSIX_SPAWN_CLOEXEC_DEFAULT`. Launch Services starts Discord, so Discord does not inherit this process's environment (verified by a test).
- Terminal: the commands A, Q, T and D; at most 64 pending commands, each at most 8192 characters. Each operation runs on its own thread, and Q closes the session at any time. Ctrl-C, SIGTERM and SIGHUP (a closed Terminal window) close it like Q, with exit code 130. Shutdown takes at most 15 seconds; when the time runs out, this process's engine children are killed, because on macOS the engine outlives its parent. Exit codes: 0; 1 (connecting or cleanup failed); 3 (could not start); 64 (wrong command line); 130 (interrupted, or shutdown took longer than 15 seconds).
- Output: the screen and the log have separate, bounded queues (32 and 512 lines). A paused Terminal or a slow disk does not hold up the gate, the bridge or shutdown; lines that do not fit are dropped and counted. Lines are cut at 1000 characters, and control and text-direction characters become spaces, so a file name cannot hide its extension. The log is written to `session.log` (0600, started anew at 256 KiB) and holds no link, token or header.
- Full Disk Access: when DiscordAccess runs from Terminal, macOS asks for the permission on behalf of the terminal app. On PRF-E04, the Full Disk Access page of System Settings opens and a message specific to Terminal is shown.
- At launch, root is refused (SYS-E02) and SIGPIPE is ignored, so a closed Terminal does not cut cleanup short. Child processes (the engine, `open`) start with the default handling of these signals. Terminal lines are read up to 32 KiB; longer ones are skipped. Error lines written outside the session's output queues are cleaned too.
- Attachment download (D) follows the Windows rules. The link is sent as typed. At most 3 redirects are followed, each validated again. A download may take 15 minutes in total and at most 30 seconds without data. The file is written to a hidden `.part` file under `~/Downloads/DiscordAccess/<time>-<random>/`, given the quarantine attribute, then moved to its final name with `RENAME_EXCL`. The Windows rule for device names (CON and so on) does not apply on macOS. A name is cut to 160 characters; if it is still over macOS's limit of 255 bytes, it is shortened further, keeps its extension (up to 20 bytes) and starts with `_`, like the names Windows shortens. Writes to the file hold a lock, so its descriptor cannot be closed in the middle of a write and its number cannot pass to another file. The signed link never reaches the log, the screen or the file name.

**Known gate limits:**

- On a plain HTTP keep-alive connection, only the first request goes through the policy; later requests are relayed as they are. The same holds on Windows, and the engine's own routing still limits the destinations.
- A client that sends its header and immediately half-closes is refused, because ownership looks only at established (`ESTABLISHED`) connections. The same holds on Windows; Chromium does not do this.

**Discord installation:** bundle id `com.hnc.Discord`, a Team ID signature, no symbolic link in the path, a local volume. PTB and Canary are not supported. Approved paths must have the form that `proc_pidpath` reports (`/Applications/...`); the `/System/Volumes/Data/...` form matches no process, and such a Discord is refused. Bundle selection converts that form to the `/Applications` form or refuses it.

**File operations:**

- `O_NOFOLLOW` is used, every parent component is checked with `lstat`, and the final path is verified with `F_GETPATH`.
- Ownership, permissions and ACLs are checked.
- A change is an atomic swap with `renamex_np(RENAME_SWAP)`, and the content swapped out is compared with what was expected.
- Unrecognized content is preserved as `.discordaccess-fs-*.preserved`.
- A deletion is a comparison too: the file is first moved to a unique name; if its bytes are as expected it is deleted, otherwise it is put back or preserved next to it.

**State folder:** the folder is 0700 and its files 0600. The recovery journal and the logs are personal data. The folder must belong to this user, grant nothing to the group or others, and carry no access control list (ACL); otherwise no profile change is made.

- The journal is in a place macOS protects less than Discord's folder. Recovery therefore trusts the journal only so far: it touches only the standard Discord folder, and writes back to the update settings only HTTPS addresses in Discord's own domains. Any other previous value is refused at session start, before anything changes (PRF-E01).
- The journal and the changed files are flushed to disk with `F_FULLFSYNC`, and the folder is synced too. The journal is written before the change, and after a power loss it still comes first: the disk never holds a change without its journal.

**Control socket:** not built. Launch Services opens an app bundle as a single instance and hands a reopen to the running instance, which then shows its status. A second process (from Terminal or launchd) only says that DiscordAccess is already running and exits. If a socket is ever needed, its file will be 0600, and the peer's user (`LOCAL_PEERCRED`) and code signature (audit token) will be verified.

**Other apps' data (Full Disk Access):**

- The permission is broad. The app uses it only for the owned files in Discord's data folder.
- Without it, no profile change is made, and the app never tries to change the permission itself. The permission is checked by reading the folder's contents (`DiscordProfile.access`); a file operation that macOS blocks gives PRF-E04.
- A TCC grant is bound to the app's code identity. With an ad-hoc signature that identity is the binary's cdhash, so a rebuild that changes the binary loses the grant. A lasting grant needs a stable signing identity: a local personal certificate or a Developer ID.

**Out of scope, as on Windows:** malware that already runs with the same user's privileges, and a root attacker. An unsigned or locally signed app claims no Gatekeeper approval.

## 9. Error handling

- **Messages:** error codes and messages follow the Windows family (`PKG-E01` and so on). The menu bar app shows alerts and notifications; Terminal mode writes to stderr.
- **DNS:** after a failed candidate, the next one is tried. If all fail, only a fixed message is shown (DNS-E02) and "Reconnect" is suggested. These stop the selection without trying another provider: HTTP 429 (Discord's rate limit is not worked around with another provider), a canceled check, a candidate that could not start (package integrity, engine start) and a candidate that could not be closed safely (DNS-E03). An engine that stops during the check is reported as ENG-E02. An invalid or repeated `--dns` option gives DNS-E01.
- **Lost engine:** the menu shows that the connection was lost, and "Reconnect" sets everything up again from scratch. In Terminal mode the session ends and the reason is shown.
- **Reconnecting** deliberately differs from Windows. If the previous session ended keeping only its profile changes (SES-E01), a new session starts and undoes them first; Windows refuses until the app restarts. If the previous session did not close in time, ended with another error or could not reap its engine (DNS-E03), no new engine starts.
- **Orphaned engine:** an engine left behind by a crash is found at the next launch by this bundle's engine path, the user ID and its parent process (launchd, process ID 1), and stopped.
- **Profile conflict:** a value the user changed is never overwritten. The operation stops, the recovery journal is kept and the user is told.
- **Discord update:** the session keeps the version it verified. A new version is handled at the next launch. No change is made in a profile structure that is not recognized.
- **Discord opened early:** if Discord opened before the connection was ready, a warning is shown; Discord is not forced to quit.

## 10. Testing and verification

**Pending local layout fix (2026-10-15, unreleased):** the working changes after macos-v0.2 add legacy-layout
discovery and an Applications installation hint. Eight new Swift fixture tests cover legacy prepare/recovery, coexistence
with the new updater, complete validation before writes, the shared entry limit, preserved user edits, rejecting another
host version, linked/incomplete cores, and recovery of schema-1 records. Journal tampering cases were also extended.
These tests have **not run**: the current development host is Windows without Swift/Xcode or a connected Mac. A syntax
parser accepted the four changed Swift files and `git diff --check` passed; neither is a Swift build or a runtime test.
An attempted run of the 35 Python build-engine tests on Windows ended with 2 failures and 10 errors due to POSIX
permissions/symlinks, `O_NOFOLLOW`, path semantics and the macOS CA-file location. It is not a passing suite. Run the
macOS checks in CONTRIBUTING.md on an Apple Silicon Mac before creating a test app; verify both Finder installation
and the reported PRF-E03 scenario there. The published-release evidence below does not verify these working changes.

- **Swift tests** (`swift test --package-path macos`; 280 tests at macos-v0.2):
  - the engine configuration;
  - the gate policy, with vectors derived from the Windows fixtures;
  - the bridge's path mapping and rewriting;
  - the journal and the undo flow, in temporary folders;
  - path trust and signing requirements;
  - finding the process behind a socket, with a local test process;
  - the session lifecycle with the real engine, a sample `Discord.app` and a sample profile, without network; the Terminal command flow; attachment download against a fake server inside URLSession; opening through Launch Services with a test app in the background.

  Live checks against Discord's servers are compiled only with `-Xswiftc -DDISCORDACCESS_LIVE` and are not part of a normal test run.
- **Node tests:** `macos/Tests/wrapper.test.cjs`, which `SessionWrapperTests` runs with the generated wrapper when Node.js is installed, and the shared `node tests/attachment-download.test.cjs` (run only, never changed).
- **Build script:** `python3 macos/Tests/build-engine.test.py` (offline, synthetic fixtures).
- **Engine:** the patches' Go tests run on macOS; all six DNS configurations pass the engine's `check` command; two builds produce the same hash.

**Live verification** on the development Mac (macOS 27, blocked network). Live results are reported separately from the automated tests, and a check that was not done is never presented as done.

| Date | Setup | Result |
| --- | --- | --- |
| 2026-10-14 | Stage 0 experiments | See the table in section 3. |
| 2026-10-15 | Connectivity checks and DNS selection | Automatic selection chose the first candidate (Cloudflare with the IP certificate). The Gateway API, the update server and the WebSocket HELLO worked through the gate; all three providers reached the Gateway API. |
| 2026-10-15 | Update bridge | The legacy feed, the new updater's manifest and a HEAD request for a download went through the bridge, the gate and the engine; all 35 manifest addresses were rewritten. The new updater's manifest request without the `arch` parameter gets 400. The very first manifest request got no answer within 30 seconds (cause unknown; the bridge then answers 502), and every later attempt passed within 2 seconds. |
| 2026-10-15 | Terminal mode, Discord 0.0.415, Terminal with Full Disk Access | Discord opened from the Dock connected through the wrapper and was fully usable; the gate allowed only Discord domains and refused no owner. The real profile had the expected layout, and the attachment download hook was installed on both launches. A 28.6 MB attachment was downloaded from within Discord through the save dialog. T (the HTTPS checks and the Gateway HELLO) passed. Q undid the profile changes and deleted the journal, without SES-E01. A opened Discord with its arguments, it connected through the gate, and Q undid the changes again. As on Windows, the gate refused Spotify (`api.spotify.com`, `dealer.spotify.com`) and `dc-telemetry.net`. |
| 2026-10-15 | Menu bar app, ad-hoc signed bundle in `/Applications` | The Full Disk Access alert appeared on first launch and led to System Settings; Terminal's grant did not carry over to the app. After the grant, the app connected by itself, and start at login turned itself on after the first connection (`SMAppService` works with an ad-hoc signature). Discord opened from the menu connected through the gate. "Check connection" passed. Reopening the app reached the running instance, which showed its status. "Quit" undid the changes and deleted the journal, and no process was left. After a `kill -9` of the app, the next launch undid the previous session's changes, stopped the orphaned engine and connected again. Voice chat worked in Discord opened from the Dock (reported by the user; voice UDP goes direct). At registration, launchd ran the login item once (`RunAtLoad`); that second process said DiscordAccess was already running and exited with code 0, which is harmless. |
| 2026-10-15 | Downloaded release package (quarantined) | After "Open Anyway", macOS ran the app under App Translocation; the quarantined embedded engine started, and the connection came up. |

The live checks above ran with macos-v0.1. macos-v0.2 changes only the interface text, the hook's messages and the package's file names.

Not verified live: a real login launch (`--startup`) and "Also open Discord at login"; `--restore` with a real journal; signing with a certificate; macOS 13–26.

## 11. Stages and status

| Stage | Scope | Status |
| --- | --- | --- |
| 0 | Feasibility: throwaway experiments outside Git. Engine build for darwin/arm64, Discord through an experimental gate, the update path, the wrapper from the Dock, the profile layout. | Done (2026-10-14). Opening Discord from its own icon (run-time proxy) and the update path (the native updater through the bridge) confirmed; see section 3. |
| 1a | Engine layer and the build script's pins | Done |
| 1b | The gate's request policy and peer identity | Done |
| 1c | The gate's network layer; the peer search limited to candidates | Done |
| 1d | Connectivity checks and automatic DNS selection | Done, verified live |
| 1e | Update bridge | Done, verified live |
| 1f | `Discord.app` validation, the Full Disk Access check, careful file operations, the session wrapper with the embedded hook, journaled profile changes | Done |
| 1g | Session controller and Terminal mode (`--console`, `--restore`, `--verify-package`, `--discord-path`, `--dns`, `--help`), the single-session lock, opening Discord through Launch Services, attachment download from Terminal | Done, verified live |
| 2a–2c | `ResidentLifecycle`, the AppKit menu bar app and its notifications, `StartupRegistration`, `ResidentPreferences`; bundle id `io.github.alpertknn.DiscordAccess` | Done, verified live |
| 2d | `build-app.sh`: the bundle with the embedded engine, hook and launch agent; signed with `DISCORDACCESS_SIGN_IDENTITY` or ad hoc; verified with `codesign --strict --deep` and `--verify-package` | Done |
| 2e | Usage and trust boundary documents | Done |
| 3 | Release | Pre-releases `macos-v0.1` and `macos-v0.2` (English interface) published (2026-10-15) |

From stage 1e on, each stage was written directly in the repository together with its tests, and an independent critical review checked the security-relevant code.

**Stage 3 (macos-v0.1):**

- No universal binary was built, because there was no Intel Mac to test on.
- The app is distributed unsigned; a quarantined download works after "Open Anyway" (see section 10).
- `macos/scripts/release.py notices` filters the license and source inventory from the verified Windows records. The Mac engine is built from the same main source, Go version, locks and patches. All 61 modules it records appear in the Windows list with the same version and h1; the Windows-only `go-ole`, `mousetrap` and Wintun/WireGuard native components are absent. That makes 63 components (61 modules, Go and sing-box) and 129 notices, stored in `macos/licenses/`.
- `release.py fetch` downloads the 63 source archives and verifies their SHA-256, size and Go h1.
- `release.py package` prepares the four release assets: `DiscordAccess-macos-v<version>.zip` (the app, `README.md`, `LICENSES.txt` and `SOURCE.txt`; in macos-v0.1 these were `OKU-BENI.md`, `LISANSLAR.txt` and `KAYNAK.txt`), `DiscordAccess-macos-v<version>-engine-source.zip`, `engine-source-manifest.json` and `SHA256SUMS.txt`. It needs a clean HEAD, an app bundle built from it, and the evidence of a tested engine build with the current recipe.
- The modified main source in the source package is assembled from the hash-pinned inputs the recipe applies: the upstream source, the locks and the patches.
- Open decisions: a universal binary (Intel Macs, up to macOS 26), Developer ID signing and notarization.

## 12. Risks and fallbacks

| Risk | Status | Fallback |
| --- | --- | --- |
| The run-time proxy (wrapper) might not work for Dock launches. | Stage 0: works (two launches, including a real Dock click). | Open Discord from the menu with arguments (confirmed by experiment A); a small launcher that can sit in the Dock. |
| Host app updates might not pass through the bridge. | Stage 0: they do (the native updater and ShipIt, 0.0.397 → 0.0.415). | Write `SKIP_HOST_UPDATE` as an owned setting for the session and undo it afterwards. |
| macOS blocks access to Discord's data folder and shows no prompt (TCC). | Stage 0: confirmed. | Full Disk Access is required, and the app guides the user. Without it no profile change is possible; only opening with arguments remains, and Discord cannot update. |
| With an ad-hoc signature, the TCC grant is lost when a rebuild changes the binary. | Open: releases are unsigned (ad hoc). | A stable signing identity (a local personal certificate or a Developer ID). |
| If DiscordAccess's own data folder gets labeled, Discord cannot read a hook file stored there. | 1f: handled | The hook is embedded in the wrapper, and Discord writes its records into its own folder. |
| If the updater installs a new core generation during a session, that generation has no wrapper. | 1f: all generations are wrapped | As on Windows, every generation is wrapped and checked again at session start. |
| The launch order cannot be coordinated with Discord's own login item. | By design | Documented; a warning when Discord opens early. |
| Without a Job object, an engine can stay behind after a crash. | Handled; verified live after a crash (section 10). | The orphaned engine is stopped at the next launch. Cleanup matches the engine's path, so if the app was moved, an orphan at the old path remains; it is harmless, because no one knows its credential. Matching by cdhash, or by a process ID and start-time record in the state folder, remains an option. |
| macOS scans a new executable on its first run, which delays the engine's first start. `syspolicyd` sometimes hangs, and launches of new files wait until the system restarts it. | 1a: 0.4 seconds for a new copy, 7–10 seconds under system load. 1b: one build waited 160 seconds while `syspolicyd` hung and restarted. | A DNS candidate's engine must open its listener within 30 seconds; if the engine exits, the limit is not awaited. `EngineProcess.start` keeps the Windows default of 12 seconds. |
| A new DNS candidate must not start while the previous candidate's engine cannot be reaped (Windows `DnsConnection.Dispose`). | 1d: handled | `DnsConnection.close()` gives DNS-E03 when the engine cannot be reaped, and the selection starts no other candidate. |
| The engine starts with only the `PATH` environment variable, and outbound DoH and TLS are not exercised by the automated tests. | 1d live check (2026-10-15, blocked network): see section 10. | Live checks are compiled separately with `-Xswiftc -DDISCORDACCESS_LIVE`. |
| A bridge request, download included, takes at most 180 seconds (the same on Windows). On a slow network a large download is cut off; for example, the 154 MB full package below 0.85 MB/s. | Accepted, as on Windows. | If needed, downloads can get an idle timeout instead of a total limit. |
| `SMAppService` and notifications may be restricted for a locally signed app. | Stage 2: `SMAppService` works with an ad-hoc signature. | A user LaunchAgent plist; the menu's status line instead of notifications. |
| A Discord update may change the profile structure. | Ongoing | No change in a structure that is not recognized; the user is told. |
| Not tested on macOS 13–26. | Open | The documents state that it is verified only on macOS 27. |

## 13. Out of scope

- A system-wide proxy, DNS or routing change; network extensions; TUN; administrator privileges.
- Modifying the `Discord.app` bundle.
- Discord PTB and Canary.
- Changes to the Windows sources, build, tests or release scripts.
