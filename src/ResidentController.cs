using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAccess
{
    internal sealed class UserActionException : Exception
    {
        internal UserActionException(string message) : base(message) { }
    }
    // The engine belongs to this helper. Official Windows launches of Discord
    // remain outside a terminating job and are never killed by this controller.
    internal sealed class Controller : IDisposable
    {
        private readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource connectLifetime = new CancellationTokenSource();
        private readonly Action<string> log;
        private readonly Action<bool, string> changed;
        private readonly DnsOptions dns;
        private DnsConnection connection;
        private ProcessGate gate;
        private UpdateBridge bridge;
        private DiscordShortcuts shortcuts;
        private Timer shortcutRefresh;
        private bool shortcutRefreshFailed;
        private ExternalDiscordScope external;
        private InstalledDiscord integration;
        private TrustedImage image;
        private CancellationTokenSource downloadLifetime;
        private string discordVersion, launchArguments;
        private volatile bool active, disposed, stopping;
        internal bool Active { get { return active; } }
        internal string ReadyMessage { get { return shortcuts != null && shortcuts.BoundCount != 0
            ? "Connection ready. You can open Discord from your own icon; the shortcut target stays the official Discord."
            : "Connection ready. No shortcut was found for this installation; you can open Discord with A + Enter."; } }
        internal Action<string> Notice;
        internal Controller(Action<string> log, Action<bool, string> changed, DnsOptions dns)
        {
            if (dns == null) throw new ArgumentNullException("dns");
            this.log = log; this.changed = changed; this.dns = dns;
        }
        internal async Task ConnectAsync()
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException("Controller");
                if (!active) await Task.Run((Action)ConnectCore).ConfigureAwait(false);
            }
            finally { operations.Release(); }
        }
        private void ConnectCore()
        {
            StartupStage stage = StartupStage.Source;
            changed(false, "Connecting");
            log("DiscordAccess v4.23: preparing the connection.");
            try
            {
                if (InstalledClientRunning())
                    throw new UserActionException("First fully close Discord from the system tray, then open DiscordAccess.exe. The open Discord was not changed.");
                image = new TrustedImage(Safety.FindDiscord(), true);
                discordVersion = Path.GetFileName(Path.GetDirectoryName(image.Path)).Substring(4);
                stage = StartupStage.Package;
                using (Package.Load(dns.Automatic ? DnsOptions.FixedDefault : dns)) { }
                connectLifetime.Token.ThrowIfCancellationRequested();
                stage = dns.Automatic ? StartupStage.Preflight : StartupStage.Engine;
                connection = (DnsConnection)DnsSelection.Select(dns,
                    (candidate, token) => DnsConnection.Open(candidate, image.Path, log, token), log, connectLifetime.Token);
                var observed = connection;
                gate = observed.Gate;
                observed.Engine.Exited += delegate { if (!stopping) BackendFailed(observed); };
                observed.Engine.EnableRaisingEvents = true;
                gate.Faulted = ignored => BackendFailed(observed);
                stage = StartupStage.UpdateBridge;
                bridge = new UpdateBridge(gate.Port, gate.Permitted, log);
                bridge.Faulted = () => BackendFailed(observed);
                bridge.Start();
                stage = StartupStage.Configure;
                connectLifetime.Token.ThrowIfCancellationRequested();
                if (InstalledClientRunning())
                    throw new UserActionException("Discord was opened during preparation. Fully close Discord and open DiscordAccess.exe again; settings were not changed.");
                string proxy = "http://127.0.0.1:" + gate.Port;
                launchArguments = "--proxy-server=" + proxy + " --proxy-bypass-list=" + NativeProcess.Quote("127.0.0.1;localhost") + " --disable-quic";
                integration = InstalledDiscord.PrepareExternal(image.Path, proxy, log);
                integration.Configure(bridge.LegacyEndpoint, bridge.NewEndpoint);
                var approved = new List<string> { image.Path };
                foreach (string name in new[] { "Discord.exe", "Update.exe" })
                {
                    string launcher = Path.Combine(Safety.DiscordRoot, name);
                    if (File.Exists(launcher)) approved.Add(launcher);
                }
                external = new ExternalDiscordScope(approved, DateTime.UtcNow);
                gate.ExternalApplication = pid => { var scope = Volatile.Read(ref external); return scope != null && scope.Permits(pid); };
                shortcuts = DiscordShortcuts.BindDirect(log, launchArguments);
                downloadLifetime = new CancellationTokenSource();
                if (observed.Engine.HasExited) throw new IOException("Engine exited");
                active = true;
                shortcutRefresh = new Timer(RefreshShortcuts, observed, 500, 1000);
                changed(true, "Connection ready; Discord can be opened from its official icon.");
            }
            catch (Exception ex)
            {
                Exception reported = ex is UserActionException || ex is PackageLoadException ? ex : StartupFailureException.From(stage, ex);
                try { DisconnectCore(); } catch { log("Cleanup after startup could not be completed; the recovery record was kept."); }
                changed(false, "Could not connect"); throw reported;
            }
        }
        internal async Task<string> OpenDiscordAsync()
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(() => OpenDiscordCore()).ConfigureAwait(false); }
            finally { operations.Release(); }
        }
        private string OpenDiscordCore()
        {
            if (disposed || !active || connection == null || image == null || external == null)
                throw new UserActionException("The connection is not ready. Check the result in the DiscordAccess.exe window.");
            connectLifetime.Token.ThrowIfCancellationRequested();
            if (!String.Equals(Safety.FindDiscord(), image.Path, StringComparison.OrdinalIgnoreCase))
                throw new UserActionException("The Discord version changed. Fully close Discord and quit with Q, then open DiscordAccess.exe again.");
            if (InstalledClientRunning()) return "Discord is already open; it was not opened a second time.";
            using (var launched = Process.Start(new ProcessStartInfo(image.Path, launchArguments) {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(image.Path) })) { }
            return "Launch request sent to the official Discord.";
        }
        internal static bool InstalledClientRunning()
        {
            foreach (var process in Process.GetProcessesByName("Discord"))
                using (process)
                {
                    string observedImage;
                    var observation = NativeProcess.ObserveRunningImage(process.Id, out observedImage);
                    if (InstalledObservationBlocksCleanup(observation, observedImage)) return true;
                }
            return false;
        }
        private void RefreshShortcuts(object observed)
        {
            if (!active || stopping || disposed || !operations.Wait(0)) return;
            try
            {
                if (!active || stopping || disposed || connection != observed || shortcuts == null) return;
                shortcuts.RefreshOfficialReplacements();
                shortcutRefreshFailed = false;
            }
            catch
            {
                if (!shortcutRefreshFailed) log("The connection options of the Discord shortcut could not be refreshed; the existing records were kept.");
                shortcutRefreshFailed = true;
            }
            finally { operations.Release(); }
        }
        internal static bool InstalledObservationBlocksCleanup(NativeProcess.ImageObservation observation, string observedImage)
        {
            if (observation == NativeProcess.ImageObservation.Gone) return false;
            if (observation == NativeProcess.ImageObservation.Unknown || observedImage == null) return true;
            if (Safety.Under(observedImage, Safety.DiscordRoot) || Safety.Under(observedImage, Safety.DefaultDiscordRoot)) return true;
            string pendingRoot = InstalledDiscord.PendingRecoveryRoot;
            return pendingRoot != null && Safety.Under(observedImage, pendingRoot);
        }
        internal static bool WaitForInstalledClientExit(Func<bool> running, int milliseconds)
        {
            if (running == null) throw new ArgumentNullException("running");
            if (milliseconds < 1 || milliseconds > 10000) throw new ArgumentOutOfRangeException("milliseconds");
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                bool blocked;
                try { blocked = running(); } catch { blocked = true; }
                if (!blocked) return true;
                int remaining = milliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                Thread.Sleep(Math.Min(50, remaining));
            }
        }
        private async void BackendFailed(DnsConnection observed)
        {
            try { observed.Gate.Dispose(); } catch { }
            try { observed.Job.Dispose(); } catch { }
            try
            {
                await operations.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!stopping && connection == observed)
                    { DisconnectCore(); changed(false, "The connection engine stopped; the connection was closed."); }
                }
                finally { operations.Release(); }
            }
            catch { }
        }
        internal async Task DisconnectAsync()
        {
            CancelConnect();
            await operations.WaitAsync().ConfigureAwait(false);
            try { await Task.Run((Action)DisconnectCore).ConfigureAwait(false); changed(false, "Disconnected"); }
            finally { operations.Release(); }
        }
        private void DisconnectCore()
        {
            stopping = true; active = false;
            if (shortcutRefresh != null) { shortcutRefresh.Dispose(); shortcutRefresh = null; }
            bool restoreFailed = false;
            try
            {
                if (downloadLifetime != null) { downloadLifetime.Cancel(); downloadLifetime.Dispose(); downloadLifetime = null; }
                try { if (shortcuts != null) shortcuts.Restore(); }
                catch { restoreFailed = true; log("The shortcut options could not be restored; the recovery record was kept."); }
                shortcuts = null;
                try
                {
                    if (integration != null)
                    {
                        // A not-yet-admitted native client can also be writing.
                        bool running = external != null && external.AnyRunning;
                        if (running || !WaitForInstalledClientExit(InstalledClientRunning, 1000)) restoreFailed = true;
                        else { integration.Restore(); integration = null; }
                    }
                    if (InstalledDiscord.HasPendingRecovery || DiscordShortcuts.HasPendingRecovery) restoreFailed = true;
                }
                catch { restoreFailed = true; log("The temporary Discord settings could not be restored; the recovery record was kept."); }
                try { if (bridge != null) bridge.Dispose(); } finally { bridge = null; }
            }
            finally
            {
                // Support has ended even when a running client defers restore.
                // Release its file lease without discarding recovery evidence.
                if (integration != null) integration.Dispose();
                try { if (connection != null) connection.Dispose(); }
                finally
                {
                    connection = null; gate = null;
                    if (external != null) { external.Dispose(); external = null; }
                    if (image != null) { image.Dispose(); image = null; }
                    stopping = false;
                }
            }
            if (restoreFailed) throw new UserActionException("The connection closed. Fully close Discord from the system tray and run DiscordAccess.exe --restore, or open DiscordAccess.exe again. The recovery record for the temporary settings was kept.");
        }
        internal async Task<long> DownloadFileAsync(string link, string destination, CancellationToken token, IProgress<long> progress)
        {
            CancellationTokenSource session; int port;
            await operations.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (disposed || !active || gate == null || downloadLifetime == null)
                    throw new UserActionException("To download a file, first open the connection with DiscordAccess.exe.");
                port = gate.Port; session = CancellationTokenSource.CreateLinkedTokenSource(token, downloadLifetime.Token);
            }
            finally { operations.Release(); }
            using (session) return await AttachmentDownloader.DownloadAsync(link, destination, port, session.Token, progress).ConfigureAwait(false);
        }
        internal async Task<string> DiagnoseAsync()
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!active || connection == null || connection.Engine.HasExited || gate == null) return "The connection is not open.";
                return await Task.Run(() => { log(ConnectivityChecks.CheckLaunch(gate.Port, discordVersion)); return GatewayChecks.Check(gate.Port); }).ConfigureAwait(false);
            }
            finally { operations.Release(); }
        }
        public void Dispose()
        {
            disposed = true; CancelConnect(); operations.Wait();
            try { DisconnectCore(); } finally { operations.Release(); connectLifetime.Dispose(); }
        }
        private void CancelConnect() { try { connectLifetime.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
