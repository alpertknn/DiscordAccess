import AppKit
import DiscordAccessKit
import UserNotifications

/// The menu bar app (design 4-5, 7.4; Windows `TrayHost`): it connects when it starts and keeps the connection ready.
/// Its menu holds the status line, Open Discord, Reconnect, Check connection, Start at login, Also open Discord at login
/// and Quit. After the first connection that works it starts at login unless the user chose otherwise, as on Windows;
/// a login launch opens Discord once when "Also open Discord at login" is on. Quitting, logging out or a
/// termination signal shut the session down as the terminal's Q does.
final class MenuBarApp: NSObject, NSApplicationDelegate, NSMenuDelegate {
    /// Runs the app on the main thread until it terminates.
    static func run(options: CommandOptions, engine: String, hook: String) -> Int32 {
        let app = NSApplication.shared
        let delegate = MenuBarApp(options: options, engine: engine, hook: hook)
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
        return 0
    }

    private let options: CommandOptions
    private let engine: String
    private let hook: String
    private let output: ConsoleOutput
    private let notifier = Notifier()
    /// Outside an app bundle (a development build run from a terminal) macOS charges the terminal for Full Disk Access.
    private let bundled = Bundle.main.bundleURL.pathExtension == "app"
    private var item: NSStatusItem?
    private let statusLine = NSMenuItem(title: MenuState(.connecting).status, action: nil, keyEquivalent: "")
    private let openItem = NSMenuItem(title: "Open Discord", action: #selector(openDiscord), keyEquivalent: "")
    private let reconnectItem = NSMenuItem(title: "Reconnect", action: #selector(reconnect), keyEquivalent: "")
    private let checkItem = NSMenuItem(title: "Check connection", action: #selector(check), keyEquivalent: "")
    private let startItem = NSMenuItem(title: "Start at login", action: #selector(toggleStartAtLogin), keyEquivalent: "")
    private let discordAtLoginItem = NSMenuItem(title: "Also open Discord at login", action: #selector(toggleDiscordAtLogin), keyEquivalent: "")
    private let quitItem = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
    private var state = MenuState(.connecting)
    private var controller: SessionController?
    private var lifecycle: ResidentLifecycle?
    private var connecting = false
    private var checking = false
    private var closing = false
    /// 130 when shutting down took too long, 1 when cleaning up failed (Windows `ExitCode`).
    private var exitCode: Int32 = 0
    private var startupPending: Bool
    private var signals: [DispatchSourceSignal] = []

    private init(options: CommandOptions, engine: String, hook: String) {
        self.options = options
        self.engine = engine
        self.hook = hook
        let log = try? SessionLog()
        output = ConsoleOutput(file: { log?.write($0) }, screen: { _ in })
        if case .resident(let startup) = options.mode {
            startupPending = startup && ResidentPreferences.load().openDiscordAtLogin
        } else {
            startupPending = false
        }
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if offerInstallationLocation() { return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let menu = NSMenu()
        menu.autoenablesItems = false
        statusLine.isEnabled = false
        for entry in [openItem, reconnectItem, checkItem, startItem, discordAtLoginItem, quitItem] { entry.target = self }
        menu.items = [statusLine, .separator(), openItem, reconnectItem, checkItem, .separator(), startItem, discordAtLoginItem, .separator(), quitItem]
        menu.delegate = self
        item.menu = menu
        self.item = item
        // A termination signal (or an interrupt in the terminal of a development build) quits as Quit does.
        signals = [SIGTERM, SIGINT, SIGHUP].map { number in
            signal(number, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
            source.setEventHandler { NSApp.terminate(nil) }
            source.resume()
            return source
        }
        output.file("DiscordAccess started in the menu bar.")
        show(.connecting)
        if case .resident(startup: true) = options.mode { checkStartupLocation() }
        connect()
    }

    /// Opening a ZIP's app does not install it. Offer Finder before creating a session, so moving the bundle does not
    /// leave a running connection tied to the download or a translocated copy. The user performs the move in Finder.
    private func offerInstallationLocation() -> Bool {
        guard bundled else { return false }
        if case .resident(startup: true) = options.mode { return false }
        let path = Bundle.main.bundleURL.path
        let locations = ["/Applications/", FileManager.default.homeDirectoryForCurrentUser.path + "/Applications/"]
        guard !locations.contains(where: { path.hasPrefix($0) }) else { return false }
        let prompt = NSAlert()
        prompt.messageText = "Install DiscordAccess in Applications"
        prompt.informativeText = "Opening the downloaded app does not install it in your Apps list. Drag DiscordAccess.app from the " +
            "extracted download into Applications, then open that installed copy. Open Applications quits this copy first. " +
            "DiscordAccess runs in the menu bar, so it has no Dock window. Open Anyway approves this download; it does not install it."
        prompt.addButton(withTitle: "Open Applications")
        prompt.addButton(withTitle: "Continue Here")
        if #available(macOS 14, *) { NSApp.activate() } else { NSApp.activate(ignoringOtherApps: true) }
        guard prompt.runModal() == .alertFirstButtonReturn else { return false }
        NSWorkspace.shared.open(URL(fileURLWithPath: "/Applications", isDirectory: true))
        NSApp.terminate(nil)
        return true
    }

    /// Opening the app again while it runs (Windows: a second launch asks for the status).
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        guard StartupRegistration.isAvailable else {
            tell(state.status + ".")
            return false
        }
        tell(state.status + ". " + (StartupRegistration.state == .enabled ? "Start at login is on." : "Start at login is off."))
        return false
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        refreshStartup()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !closing else { return .terminateLater }
        closing = true
        show(.closing)
        guard let lifecycle else { return .terminateNow }
        lifecycle.shutdown().observe { [self] result in
            switch result {
            case .success(true):
                break
            case .success(false):
                // An engine left by a shutdown that took too long would outlive the app.
                if let resolved = realpath(engine, nil) {
                    EngineProcess.terminateChildren(enginePath: String(cString: resolved))
                    free(resolved)
                }
                output.file("Shutdown timed out; the connection engine was stopped.")
                exitCode = 130
            case .failure(let error):
                // Quitting never waits for an alert, as on Windows: a logout must not stall here.
                let message = describe(error)
                output.file("Session cleanup: " + message)
                _ = notifier.post(message)
                exitCode = 1
            }
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        output.file("DiscordAccess quit.")
        output.close()
        exit(exitCode)
    }

    // MARK: - Session

    /// Connects with a new session once the previous one has shut down (Windows `ConnectAsync`). A previous session that
    /// ended with only its profile changes kept (SES-E01) does not block the new one, which undoes them first while
    /// Discord is closed. One that did not end in time, failed otherwise, or left an engine it could not reap (DNS-E03)
    /// does: no new engine may start then.
    private func connect() {
        guard !connecting, !closing, !(lifecycle?.isReady ?? false) else { return }
        connecting = true
        show(.connecting)
        guard let previous = lifecycle else {
            startSession()
            return
        }
        let previousController = controller
        previous.shutdown().observe { [self] result in
            switch result {
            case .success(true):
                break
            case .failure(let error as KitError) where error.code == "SES-E01" && previousController?.hasUnreapedEngine == false:
                output.file("Previous session: " + error.description)
            case .success(false), .failure:
                if case .failure(let error) = result { output.file("Previous session: " + describe(error)) }
                connected(.failure(ResidentFailure.previousSessionOpen))
                return
            }
            startSession()
        }
    }

    private func startSession() {
        guard !closing else { return }
        let settings = SessionController.Settings(engine: engine, hook: hook, dns: options.dns, discordPath: options.discordPath)
        let controller = SessionController(settings: settings, log: output.file, changed: { [weak self] active, message in
            DispatchQueue.main.async { self?.sessionChanged(active: active, message: message) }
        })
        let lifecycle = ResidentLifecycle(operations: ResidentLifecycle.Operations(controller), changed: { [output] _, message in output.file(message) })
        self.controller = controller
        self.lifecycle = lifecycle
        lifecycle.connect().observe { [self] result in connected(result) }
    }

    private func connected(_ result: Result<Void, Error>) {
        connecting = false
        guard !closing else { return }
        let ready = lifecycle?.isReady ?? false
        show(ready ? .ready : .failed)
        switch result {
        case .success:
            ensureStartup()
            tell("Connection ready. You can open Discord from its own icon.")
            // A login launch opens Discord once, after the first connection that works (Windows `ConnectAsync`); the
            // lifecycle does not open a running Discord again.
            if startupPending {
                startupPending = false
                openDiscord()
            }
        case .failure(let error):
            output.file("Background start: " + describe(error))
            report(error)
        }
    }

    /// The controller's own state changes: an engine that stopped ends the session (Windows `Changed`).
    private func sessionChanged(active: Bool, message: String) {
        guard !closing, !connecting else { return }
        show(active ? .ready : .closed)
        if !active && !message.isEmpty { warn(message) }
    }

    @objc private func openDiscord() {
        guard !closing, let lifecycle else { return }
        lifecycle.open().observe { [self] result in
            guard !closing else { return }
            switch result {
            case .success(let message): tell(message)
            case .failure(let error): report(error)
            }
        }
    }

    @objc private func reconnect() {
        connect()
    }

    @objc private func check() {
        guard !closing, !connecting, !checking, let controller, controller.isActive else { return }
        checking = true
        checkItem.isEnabled = false
        Thread.detachNewThread { [self] in
            let result = Result { try controller.diagnose() }
            DispatchQueue.main.async { [self] in
                checking = false
                guard !closing else { return }
                checkItem.isEnabled = state.canCheck
                switch result {
                case .success(let message): announce("Connection check", message)
                case .failure(let error): announce("Connection check", "Diagnostics could not finish: " + describe(error))
                }
            }
        }
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    // MARK: - Starting at login

    /// A login launch from a location that is no longer trusted (the app was moved, or its volume changed) opens nothing
    /// by itself and turns starting at login off (Windows refuses a startup registration of another path).
    private func checkStartupLocation() {
        guard StartupRegistration.isAvailable else { return }
        do {
            try StartupRegistration.verifyLocation()
        } catch {
            startupPending = false
            var preferences = ResidentPreferences.load()
            preferences.startAtLogin = false
            try? preferences.save()
            try? StartupRegistration.disable()
            warn("DiscordAccess was started at login from a location that is not trusted, so start at login was turned off. Move the " +
                 "app to the Applications folder and open it from there.")
        }
    }

    /// After the first connection that works, starts at login unless the user chose otherwise (Windows `EnsureEnabled`).
    /// A location that is not trusted is only logged; the next launch tries again.
    private func ensureStartup() {
        guard StartupRegistration.isAvailable else { return }
        var preferences = ResidentPreferences.load()
        guard preferences.startAtLogin == nil else { return }
        do {
            let state = try StartupRegistration.enable()
            preferences.startAtLogin = true
            try preferences.save()
            output.file(state == .needsApproval ? "Start at login is waiting for approval in macOS." : "Start at login was turned on.")
        } catch {
            output.file("Start at login: " + describe(error))
        }
    }

    /// The choice is saved before the registration changes, so a choice that could not be saved changes nothing.
    @objc private func toggleStartAtLogin() {
        guard !closing else { return }
        var preferences = ResidentPreferences.load()
        do {
            if StartupRegistration.state == .disabled {
                preferences.startAtLogin = true
                try preferences.save()
                let state = try StartupRegistration.enable()
                if state == .needsApproval {
                    let message = "macOS needs your approval: allow DiscordAccess under System Settings > General > Login Items."
                    output.file(message)
                    if alert("Approval needed", message, action: "Open Login Items") { StartupRegistration.openLoginItemsSettings() }
                } else {
                    tell("Start at login is on.")
                }
            } else {
                preferences.startAtLogin = false
                try preferences.save()
                try StartupRegistration.disable()
                tell("Start at login is off.")
            }
        } catch {
            warn(describe(error))
        }
        refreshStartup()
    }

    @objc private func toggleDiscordAtLogin() {
        guard !closing else { return }
        var preferences = ResidentPreferences.load()
        preferences.openDiscordAtLogin.toggle()
        do {
            try preferences.save()
            // Discord's own login item would open Discord before the connection is ready, and connecting then stops.
            tell(preferences.openDiscordAtLogin ? "Discord will also open at login. Turn off Discord's own open-at-login setting; otherwise " +
                "Discord opens before the connection is ready." : "Discord will not open at login.")
        } catch {
            warn(describe(error))
        }
        refreshStartup()
    }

    private func refreshStartup() {
        guard StartupRegistration.isAvailable else {
            for entry in [startItem, discordAtLoginItem] {
                entry.isEnabled = false
                entry.toolTip = "Available in the app bundle."
            }
            return
        }
        let state = StartupRegistration.state
        startItem.state = state == .enabled ? .on : state == .needsApproval ? .mixed : .off
        discordAtLoginItem.state = ResidentPreferences.load().openDiscordAtLogin ? .on : .off
        startItem.isEnabled = !closing
        discordAtLoginItem.isEnabled = !closing
    }

    // MARK: - Showing

    private func show(_ phase: MenuState.Phase) {
        state = MenuState(phase)
        statusLine.title = state.status
        openItem.isEnabled = state.canOpenDiscord
        checkItem.isEnabled = state.canCheck && !checking
        reconnectItem.isEnabled = state.canReconnect && !connecting
        quitItem.isEnabled = state.canQuit
        refreshStartup()
        guard let button = item?.button else { return }
        button.image = BrandIcon.statusImage
        button.appearsDisabled = phase != .ready
        button.toolTip = state.tooltip
    }

    /// Information: a notification, or only the status line when notifications cannot be shown.
    private func tell(_ message: String) {
        output.file(message)
        _ = notifier.post(message)
    }

    /// A warning: a notification, or an alert when notifications cannot be shown.
    private func warn(_ message: String) {
        output.file(message)
        if !notifier.post(message) { alert("DiscordAccess", message) }
    }

    /// The answer to something the user asked for: a notification, or an alert.
    private func announce(_ title: String, _ message: String) {
        output.file(message)
        if !notifier.post(message) { alert(title, message) }
    }

    /// A failure; missing Full Disk Access explains how to add the installed app and opens its settings pane.
    private func report(_ error: Error) {
        if let error = error as? KitError, error.code == "PRF-E04" {
            let message = KitError.profileAccessDenied(inTerminal: !bundled).description
            output.file(message)
            if alert("Full Disk Access required", message, action: "Open Full Disk Access") {
                DiscordProfile.openFullDiskAccessSettings()
            }
            return
        }
        warn(describe(error))
    }

    /// The text of a failure: the app's own carry fixed text, as DiscordAccessKit's do.
    private func describe(_ error: Error) -> String {
        (error as? ResidentFailure)?.description ?? ConsoleSession.message(error)
    }

    /// Shows an alert in front; true when its `action` button was chosen.
    @discardableResult
    private func alert(_ title: String, _ message: String, action: String? = nil) -> Bool {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        if let action { alert.addButton(withTitle: action) }
        alert.addButton(withTitle: "OK")
        if #available(macOS 14, *) {
            NSApp.activate()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }
        return alert.runModal() == .alertFirstButtonReturn && action != nil
    }
}

/// A failure of the app itself, shown with its fixed text.
enum ResidentFailure: Error, CustomStringConvertible {
    case previousSessionOpen

    var description: String {
        "The previous connection did not finish closing. Quit Discord completely and restart DiscordAccess."
    }
}

/// Notifications, when macOS lets this app show them: only an app bundle can, and only once the user allowed it. They
/// show while the app is in front as well, for example with its menu open.
private final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    private let lock = NSLock()
    private var allowed = false

    override init() {
        super.init()
        guard Bundle.main.bundleURL.pathExtension == "app", Bundle.main.bundleIdentifier != nil else { return }
        UNUserNotificationCenter.current().delegate = self
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { [weak self] granted, _ in
            guard let self else { return }
            self.lock.lock()
            self.allowed = granted
            self.lock.unlock()
        }
    }

    /// Posts `message`; false when it could not be shown, so the caller may show it another way.
    func post(_ message: String) -> Bool {
        lock.lock()
        let allowed = self.allowed
        lock.unlock()
        guard allowed else { return false }
        let content = UNMutableNotificationContent()
        content.title = "DiscordAccess"
        content.body = message.count > 240 ? String(message.prefix(240)) + "…" : message
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        return true
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }
}
