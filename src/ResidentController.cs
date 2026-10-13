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
            ? "Bağlantı hazır. Discord'u kendi simgesinden açabilirsiniz; kısayol hedefi resmi Discord olarak korunur."
            : "Bağlantı hazır. Bu kurulumun kısayolu bulunamadı; Discord'u A + Enter ile açabilirsiniz."; } }
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
            changed(false, "Bağlanıyor");
            log("Discord Erişim v4.21: bağlantı hazırlanıyor.");
            try
            {
                if (InstalledClientRunning())
                    throw new UserActionException("Önce Discord'u sistem tepsisinden tamamen kapatın; ardından DiscordAccess.exe'yi açın. Açık Discord değiştirilmedi.");
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
                    throw new UserActionException("Hazırlık sırasında Discord açıldı. Discord'u tamamen kapatıp DiscordAccess.exe'yi yeniden açın; ayarlar değiştirilmedi.");
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
                changed(true, "Bağlantı hazır; resmi Discord simgesinden açılabilir.");
            }
            catch (Exception ex)
            {
                Exception reported = ex is UserActionException || ex is PackageLoadException ? ex : StartupFailureException.From(stage, ex);
                try { DisconnectCore(); } catch { log("Başlangıç sonrası geri alma tamamlanamadı; kurtarma kaydı korundu."); }
                changed(false, "Bağlantı kurulamadı"); throw reported;
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
                throw new UserActionException("Bağlantı hazır değil. DiscordAccess.exe penceresindeki sonucu kontrol edin.");
            connectLifetime.Token.ThrowIfCancellationRequested();
            if (!String.Equals(Safety.FindDiscord(), image.Path, StringComparison.OrdinalIgnoreCase))
                throw new UserActionException("Discord sürümü değişti. Discord'u tamamen kapatıp Q ile çıkın; DiscordAccess.exe'yi yeniden açın.");
            if (InstalledClientRunning()) return "Discord zaten açık; ikinci açılış yapılmadı.";
            using (var launched = Process.Start(new ProcessStartInfo(image.Path, launchArguments) {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(image.Path) })) { }
            return "Resmi Discord'a açılış isteği gönderildi.";
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
                if (!shortcutRefreshFailed) log("Discord kısayolunun bağlantı seçenekleri yenilenemedi; mevcut kayıtlar korundu.");
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
                    { DisconnectCore(); changed(false, "Bağlantı motoru durdu; bağlantı kapatıldı."); }
                }
                finally { operations.Release(); }
            }
            catch { }
        }
        internal async Task DisconnectAsync()
        {
            CancelConnect();
            await operations.WaitAsync().ConfigureAwait(false);
            try { await Task.Run((Action)DisconnectCore).ConfigureAwait(false); changed(false, "Bağlantı kesildi"); }
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
                catch { restoreFailed = true; log("Kısayol seçenekleri geri alınamadı; kurtarma kaydı korundu."); }
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
                catch { restoreFailed = true; log("Discord ayarları geri alınamadı; kurtarma kaydı korundu."); }
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
            if (restoreFailed) throw new UserActionException("Bağlantı kapandı. Discord'u sistem tepsisinden tamamen kapatıp DiscordAccess.exe --restore çalıştırın veya DiscordAccess.exe'yi yeniden açın. Geçici ayarların kurtarma kaydı korundu.");
        }
        internal async Task<long> DownloadFileAsync(string link, string destination, CancellationToken token, IProgress<long> progress)
        {
            CancellationTokenSource session; int port;
            await operations.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (disposed || !active || gate == null || downloadLifetime == null)
                    throw new UserActionException("Dosya indirmek için önce DiscordAccess.exe ile bağlantıyı açın.");
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
                if (!active || connection == null || connection.Engine.HasExited || gate == null) return "Bağlantı açık değil.";
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
