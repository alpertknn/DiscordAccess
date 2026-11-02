using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiscordAccess
{
    internal static class TrayHost
    {
        internal static int Run(DnsOptions dns, DiscordInstallation installation, bool fromStartup, ResidentStartupRequests requests)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var context = new TrayContext(dns, installation, fromStartup, requests))
            {
                if (context.SkipStartup) return 0;
                Application.Run(context);
                return context.ExitCode;
            }
        }

        private sealed class TrayContext : ApplicationContext
        {
            private readonly Control dispatcher = new Control();
            private readonly NotifyIcon icon = new NotifyIcon();
            private readonly ContextMenuStrip menu = new ContextMenuStrip();
            private readonly ToolStripMenuItem status = new ToolStripMenuItem("Connecting...");
            private readonly ToolStripMenuItem open = new ToolStripMenuItem("Open Discord");
            private readonly ToolStripMenuItem retry = new ToolStripMenuItem("Reconnect");
            private readonly ToolStripMenuItem diagnosis = new ToolStripMenuItem("Check connection");
            private readonly ToolStripMenuItem automatic = new ToolStripMenuItem("Start with Windows");
            private readonly ToolStripMenuItem quit = new ToolStripMenuItem("Exit");
            private readonly DnsOptions dns;
            private readonly bool fromStartup;
            private readonly StartupCoordinator startup;
            private readonly Timer refresh = new Timer { Interval = 5000 };
            private IDisposable openSignal, statusSignal;
            private Controller controller;
            private BackgroundLifecycle lifecycle;
            private SessionWindow sessionWindow;
            private bool connecting, closing, disposed, startupLaunch, startupConsumed, refreshWarning;
            internal int ExitCode;
            internal bool SkipStartup;

            internal TrayContext(DnsOptions dns, DiscordInstallation installation, bool fromStartup, ResidentStartupRequests requests)
            {
                this.dns = dns; this.fromStartup = fromStartup;
                try
                {
                IntPtr handle = dispatcher.Handle;
                string[] options = dns.Provider == "auto" && !dns.RecordFragment ? new string[0] :
                    new[] { "--dns=" + dns.Provider }.Concat(dns.RecordFragment ? new[] { "--dns-record-fragment" } : new string[0]).ToArray();
                startup = StartupCoordinator.Create(Path.Combine(Safety.Base,"DiscordAccess.exe"), installation, options, SessionLog.Write);
                // Validate the owned startup record before connecting. No Discord
                // launch occurs until the connection below has succeeded.
                if (fromStartup)
                {
                    startupLaunch = startup.StartupIntent();
                    if (!startup.Enabled) { SkipStartup = true; return; }
                }
                status.Enabled = false; automatic.Checked = startup.Enabled;
                open.Enabled = diagnosis.Enabled = retry.Enabled = false;
                menu.Items.AddRange(new ToolStripItem[] { status, new ToolStripSeparator(), open, retry, diagnosis,
                    new ToolStripSeparator(), automatic, quit });
                icon.Icon = SystemIcons.Application;
                icon.Text = "DiscordAccess — connecting";
                icon.ContextMenuStrip = menu;
                icon.Visible = true;
                icon.DoubleClick += delegate { ShowStatus(); };
                open.Click += async delegate { await OpenAsync(); };
                retry.Click += async delegate { await ConnectAsync(); };
                diagnosis.Click += async delegate { await DiagnoseAsync(); };
                automatic.Click += delegate { ToggleStartup(); };
                quit.Click += async delegate { await CloseAsync(false); };
                sessionWindow = new SessionWindow(EndWindowsSession);
                openSignal = ManualLaunchSignal.Listen(() => Post(async () => await OpenAsync()));
                statusSignal = ManualLaunchSignal.ListenStatus(() => Post(ShowStatus));
                requests.Listen(() => Post(async () => await StartupRequestAsync()));
                refresh.Tick += delegate { RefreshStartup(); };
                refresh.Start();
                Application.Idle += FirstIdle;
                }
                catch { Dispose(); throw; }
            }

            private async void FirstIdle(object sender, EventArgs args)
            {
                Application.Idle -= FirstIdle;
                await ConnectAsync();
            }

            private void Post(Action action)
            {
                if (disposed || closing) return;
                try { dispatcher.BeginInvoke((Action)(() => { if (!disposed && !closing) action(); })); }
                catch (InvalidOperationException) { }
            }

            private void Changed(bool active, string message)
            {
                Post(() => {
                    status.Text = active ? "Connection ready" : (connecting ? "Connecting..." : "Disconnected");
                    icon.Text = active ? "DiscordAccess — connection ready" : "DiscordAccess — disconnected";
                    if (!connecting)
                    {
                        open.Enabled = diagnosis.Enabled = active;
                        retry.Enabled = !active;
                        if (!active && !String.IsNullOrEmpty(message)) Notify(message,ToolTipIcon.Warning);
                    }
                });
            }

            private async Task ConnectAsync()
            {
                if (connecting || closing || (lifecycle != null && lifecycle.Ready)) return;
                connecting = true;
                open.Enabled = diagnosis.Enabled = retry.Enabled = automatic.Enabled = false;
                status.Text = "Connecting...";
                var priority = StartupPriority.Begin(fromStartup);
                try
                {
                    if (lifecycle != null)
                    {
                        bool cleaned = await lifecycle.ShutdownAsync(15000);
                        if (!cleaned) throw new UserActionException("The previous connection could not finish closing. Fully close Discord and restart DiscordAccess.");
                    }
                    controller = new Controller(SessionLog.Write, Changed, dns);
                    controller.Notice = message => Post(() => Notify(message, ToolTipIcon.Info));
                    Controller current = controller;
                    lifecycle = new BackgroundLifecycle(new BackgroundLifecycle.Operations {
                        Connect = current.ConnectAsync, Disconnect = current.DisconnectAsync, Dispose = current.Dispose,
                        OpenDiscord = current.OpenDiscordAsync, Ready = () => current.Active, Running = Controller.InstalledClientRunning
                    });
                    // Registration is attempted only after the connection works.
                    // Later launches respect the user's persisted off preference.
                    await lifecycle.ConnectAsync(false);
                    if (closing) return;
                    startup.EnsureEnabled();
                    automatic.Checked = startup.Enabled;
                    if (startupLaunch && !startupConsumed)
                    {
                        startupLaunch = false; startupConsumed = true;
                        await lifecycle.OpenAsync();
                    }
                    status.Text = "Connection ready";
                    icon.Text = "DiscordAccess — connection ready";
                    if (!String.IsNullOrEmpty(startup.Status)) SessionLog.Write(startup.Status);
                    Notify("Connection ready. You can open Discord from your own icon.", ToolTipIcon.Info);
                }
                catch (Exception error)
                {
                    status.Text = lifecycle != null && lifecycle.Ready ? "Connection ready" : "Could not connect";
                    icon.Text = "DiscordAccess — " + status.Text.ToLowerInvariant();
                    string message = ConsoleHost.SafeError(error);
                    SessionLog.Write("Background startup: " + message);
                    if (!closing) Notify(message, ToolTipIcon.Warning);
                }
                finally
                {
                    priority.Dispose();
                    connecting = false;
                    if (!closing)
                    {
                        bool ready = lifecycle != null && lifecycle.Ready;
                        open.Enabled = diagnosis.Enabled = ready;
                        retry.Enabled = !ready;
                        automatic.Enabled = true;
                        try { automatic.Checked = startup.Enabled; }
                        catch (Exception error) { StartupWarning(error); }
                    }
                }
            }

            private async Task StartupRequestAsync()
            {
                if (closing || startupConsumed) return;
                try
                {
                    if (!startup.StartupIntent() || !startup.Enabled) return;
                    startupLaunch = true;
                    if (connecting) return;
                    if (lifecycle != null && lifecycle.Ready)
                    { startupLaunch = false; startupConsumed = true; await lifecycle.OpenAsync(); }
                    else await ConnectAsync();
                }
                catch (Exception error) { StartupWarning(error); }
            }

            private void RefreshStartup()
            {
                if (closing || connecting) return;
                try
                {
                    if (startup.Enabled) startup.Refresh();
                    automatic.Checked = startup.Enabled;
                    refreshWarning = false;
                }
                catch (Exception error) { StartupWarning(error); }
            }

            private void StartupWarning(Exception error)
            {
                if (refreshWarning) return;
                refreshWarning = true;
                string message = ConsoleHost.SafeError(error);
                SessionLog.Write("Windows startup coordination: " + message);
                Notify(message,ToolTipIcon.Warning);
            }

            private async Task OpenAsync()
            {
                if (closing || lifecycle == null) return;
                try { string message = await lifecycle.OpenAsync(); if (!closing) Notify(message, ToolTipIcon.Info); }
                catch (Exception error) { if (!closing) Notify(ConsoleHost.SafeError(error), ToolTipIcon.Warning); }
            }

            private async Task DiagnoseAsync()
            {
                if (closing || connecting || controller == null || !controller.Active) return;
                diagnosis.Enabled = false;
                try { Notify(await controller.DiagnoseAsync(), ToolTipIcon.Info); }
                catch (Exception error) { Notify(ConsoleHost.SafeError(error), ToolTipIcon.Warning); }
                finally { if (!closing) diagnosis.Enabled = controller != null && controller.Active; }
            }

            private void ToggleStartup()
            {
                if (closing || connecting) return;
                try { startup.SetEnabled(!startup.Enabled); automatic.Checked = startup.Enabled; Notify(startup.Status,ToolTipIcon.Info); }
                catch (Exception error)
                {
                    StartupWarning(error);
                    try { automatic.Checked = startup.Enabled; } catch { }
                }
            }

            private void ShowStatus()
            {
                try { Notify(status.Text + ". " + (startup.Enabled ? "Start with Windows is on." : "Start with Windows is off."), ToolTipIcon.Info); }
                catch (Exception error) { StartupWarning(error); }
            }

            private void Notify(string message, ToolTipIcon kind)
            {
                if (disposed || closing || String.IsNullOrEmpty(message)) return;
                icon.ShowBalloonTip(5000,"DiscordAccess",message.Length > 240 ? message.Substring(0,240) : message,kind);
            }

            private async Task CloseAsync(bool endingSession)
            {
                if (closing) return;
                closing = true;
                quit.Enabled = open.Enabled = retry.Enabled = diagnosis.Enabled = automatic.Enabled = false;
                status.Text = "Closing...";
                try
                {
                    if (lifecycle != null && !await lifecycle.ShutdownAsync(15000)) ExitCode = 130;
                }
                catch (Exception error) { SessionLog.Write("Background shutdown: " + ConsoleHost.SafeError(error)); ExitCode = 1; }
                finally { icon.Visible = false; ExitThread(); }
            }

            private void EndWindowsSession()
            {
                if (closing) return;
                closing = true;
                try
                {
                    if (lifecycle != null && !lifecycle.ShutdownAsync(4000).GetAwaiter().GetResult()) ExitCode = 130;
                }
                catch { ExitCode = 1; }
                finally { icon.Visible = false; ExitThread(); }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed)
                {
                    disposed = true;
                    Application.Idle -= FirstIdle;
                    refresh.Stop(); refresh.Dispose();
                    if (openSignal != null) openSignal.Dispose();
                    if (statusSignal != null) statusSignal.Dispose();
                    if (sessionWindow != null) sessionWindow.Dispose();
                    icon.Visible = false; icon.Dispose(); menu.Dispose(); dispatcher.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        // Hidden top-level window receives logoff/restart notifications. It
        // never shows a form or owns another process. Pending recovery remains
        // durable if Windows ends the session before Discord has closed.
        private sealed class SessionWindow : NativeWindow, IDisposable
        {
            private readonly Action ending;
            internal SessionWindow(Action ending)
            {
                this.ending = ending;
                CreateHandle(new CreateParams { Caption="DiscordAccess.Session",Style=0,ExStyle=0x80,Parent=IntPtr.Zero });
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0011) { message.Result = new IntPtr(1); return; }
                if (message.Msg == 0x0016 && message.WParam != IntPtr.Zero)
                { ending(); message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
            }
            public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
        }
    }
}
