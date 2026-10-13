using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAccess
{
    internal static class ConsoleHost
    {
        internal sealed class SessionState : IDisposable
        {
            private readonly AutoResetEvent changed = new AutoResetEvent(false);
            private readonly ConcurrentQueue<string> commands = new ConcurrentQueue<string>();
            private int stop, exitCode, connected, ended, inputClosed, queued, startupConsumed;
            internal bool StopRequested { get { return Volatile.Read(ref stop) != 0; } }
            internal bool Ended { get { return Volatile.Read(ref ended) != 0; } }
            internal int ExitCode { get { return Volatile.Read(ref exitCode); } }
            internal bool InputClosed { get { return Volatile.Read(ref inputClosed) != 0; } }
            internal void EndInput() { Interlocked.Exchange(ref inputClosed, 1); Wake(); }
            private void Wake() { try { changed.Set(); } catch (ObjectDisposedException) { } }
            internal void RequestStop(int code)
            {
                if (code != 0) Interlocked.Exchange(ref exitCode, code);
                Interlocked.Exchange(ref stop, 1); Wake();
            }
            internal void StateChanged(bool active)
            {
                if (active) Interlocked.Exchange(ref connected, 1);
                else if (Volatile.Read(ref connected) != 0) Interlocked.Exchange(ref ended, 1);
                Wake();
            }
            internal void Command(string text)
            {
                if (StopRequested || Ended) return;
                if (text == null) { EndInput(); return; }
                string command = text.Trim();
                if (command.Length == 0) return;
                if (command.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("çıkış", StringComparison.OrdinalIgnoreCase) || command.Equals("cikis", StringComparison.OrdinalIgnoreCase))
                    RequestStop(0);
                else if (command.Length <= 8192)
                {
                    if (Interlocked.Increment(ref queued) <= 64) { commands.Enqueue(command); Wake(); }
                    else Interlocked.Decrement(ref queued);
                }
            }
            internal void Startup(Func<bool> intent)
            {
                if (StopRequested || Ended || Volatile.Read(ref startupConsumed) != 0 || intent == null || !intent()) return;
                if (Interlocked.CompareExchange(ref startupConsumed,1,0) == 0) Command("A");
            }
            internal bool TryCommand(out string command)
            {
                if (!commands.TryDequeue(out command)) return false;
                Interlocked.Decrement(ref queued); return true;
            }
            internal void Wait() { changed.WaitOne(100); }
            public void Dispose() { changed.Dispose(); }
        }

        // Delegates let offline checks exercise console shutdown without starting
        // an engine, Discord, account session or network connection.
        internal sealed class SessionOperations
        {
            internal Func<Task> Connect, Disconnect;
            internal Func<string> ReadyMessage;
            internal Func<Task<string>> Diagnose, OpenDiscord;
            internal Func<string, Task<string>> Download;
            internal Func<string> MessageReport;
            internal Action Dispose;
        }

        internal static int Run(string[] args, DnsOptions dns, ResidentStartupRequests startupRequests = null, Func<bool> startupIntent = null)
        {
            args = args ?? new string[0];
            if (args.Any(arg => arg == "--help" || arg == "-h" || arg == "/?")) { Help(); return 0; }
            if (args.Any(arg => arg != "--connect" && arg != "--start" && !DnsOptions.IsArgument(arg)))
            { WriteConsole("Tanınmayan seçenek. Kullanım için --help yazın."); return 64; }
            try { Console.OutputEncoding = new UTF8Encoding(false); Console.Title = "Discord Erişim v4.21"; } catch { }
            using (var output = new ConsoleOutput(SessionLog.Write, WriteConsole))
            using (var state = new SessionState())
            using (var launchSignal = ManualLaunchSignal.Listen(() => state.Command("A")))
            {
                // Listener/state exist before delivering a startup intent that
                // arrived while the resident was recovering or discovering.
                if (startupRequests != null) startupRequests.Listen(() => {
                    try { state.Startup(startupIntent); }
                    catch (Exception error) { SessionLog.Write("Konsol otomatik açılışı: " + SafeError(error)); }
                });
                output.Show("Discord Erişim v4.21 | Bağlanıyor...");
                var controller = new Controller(output.File, (active, message) =>
                {
                    state.StateChanged(active);
                    if (!String.IsNullOrWhiteSpace(message))
                    { if (state.Ended) output.Show(message); else output.File(message); }
                }, dns);
                controller.Notice = output.Show;
                var operations = new SessionOperations { Connect = controller.ConnectAsync,
                    ReadyMessage = () => controller.ReadyMessage,
                    OpenDiscord = controller.OpenDiscordAsync,
                    Disconnect = controller.DisconnectAsync, Diagnose = controller.DiagnoseAsync, Dispose = controller.Dispose,
                    Download = link => Task.Run(() => ConsoleDownload.DownloadAsync(controller, link)),
                    MessageReport = () => "Bu sürümde Discord içindeki mesaj gözlemcisi kaldırıldı." };
                ConsoleCancelEventHandler cancel = (sender, e) => { e.Cancel = true; state.RequestStop(130); };
                Console.CancelKeyPress += cancel;
                var input = new Thread(() =>
                {
                    try
                    {
                        while (!state.StopRequested && !state.Ended)
                        {
                            string command = Console.ReadLine();
                            state.Command(command);
                            if (command == null) return;
                        }
                    }
                    catch (IOException) { state.EndInput(); }
                    catch (InvalidOperationException) { state.EndInput(); }
                });
                input.IsBackground = true; input.Name = "DiscordAccess console input";
                try { input.Start(); return RunSession(operations, state, output.Show, 15000); }
                finally { Console.CancelKeyPress -= cancel; }
            }
        }

        private static bool WaitOperation(Task operation, SessionState state)
        {
            // A canceled console may leave a controller operation finishing on a
            // background thread; observe its eventual fault without dumping data.
            operation.ContinueWith(failed => { var ignored = failed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            while (!operation.IsCompleted && !state.StopRequested && !state.Ended) state.Wait();
            return operation.IsCompleted && !state.StopRequested && !state.Ended;
        }

        internal static int RunSession(SessionOperations session, SessionState state, Action<string> log, int shutdownTimeoutMilliseconds)
        {
            int result = 0;
            Task pendingDownload = null, pendingOpen = null;
            try
            {
                if (!state.StopRequested)
                {
                    Task connecting = session.Connect();
                    if (WaitOperation(connecting, state))
                    {
                        connecting.GetAwaiter().GetResult();
                        log(session.ReadyMessage == null ? "Bağlantı hazır. Discord'u kendi simgesinden açabilirsiniz." : session.ReadyMessage());
                        while (!state.StopRequested && !state.Ended)
                        {
                            string command;
                            if (!state.TryCommand(out command)) { state.Wait(); continue; }
                            if (command.Equals("a", StringComparison.OrdinalIgnoreCase))
                            {
                                OpenDiscord(session, state, log, ref pendingOpen);
                                continue;
                            }
                            if (command.Equals("m", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    if (session.MessageReport == null) log("Mesaj tanısı bu oturumda kullanılamıyor.");
                                    else foreach (string line in (session.MessageReport() ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) log(line);
                                }
                                catch { log("Yerel mesaj tanı kaydı okunamadı; yeni ağ isteği gönderilmedi."); }
                                continue;
                            }
                            if (command.Equals("d", StringComparison.OrdinalIgnoreCase))
                            {
                                Download(session, state, log, ref pendingDownload, ref pendingOpen);
                                continue;
                            }
                            if (!command.Equals("t", StringComparison.OrdinalIgnoreCase))
                            { log("A: Discord aç | Q: kapat | T: tanılama | D: dosya indir"); continue; }
                            try
                            {
                                Task<string> diagnosis = session.Diagnose();
                                if (WaitOperation(diagnosis, state)) log(diagnosis.GetAwaiter().GetResult());
                            }
                            catch (Exception error) { log("Tanılama tamamlanamadı: " + ErrorMessage(error)); }
                        }
                    }
                }
                if (state.StopRequested) result = state.ExitCode;
            }
            catch (Exception error) { log("Bağlantı kurulamadı: " + ErrorMessage(error)); result = 1; }
            finally
            {
                var shutdownClock = Stopwatch.StartNew();
                try
                {
                    Task disconnecting = session.Disconnect();
                    if (WaitWithinShutdown(disconnecting, shutdownClock, shutdownTimeoutMilliseconds))
                    {
                        disconnecting.GetAwaiter().GetResult();
                        session.Dispose();
                    }
                    else
                    {
                        disconnecting.ContinueWith(failed => { var ignored = failed.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        // Never enter Dispose while Connect holds its lock.
                        // Main returns, background work ends and the OS closes
                        // the non-inherited kill-on-close job for our tree only.
                        log("Kapanış süresi doldu; bağlantı motoru program kapanırken durdurulacak.");
                        result = 130;
                    }
                }
                catch (Exception error) { log("Oturum temizliği: " + ErrorMessage(error)); result = result == 0 ? 1 : result; }
                // Disconnect cancels the session token. Await the download's
                // final cleanup too, so its partial file can be removed before
                // Main exits. This uses the same total shutdown budget.
                if (pendingDownload != null)
                {
                    if (WaitWithinShutdown(pendingDownload, shutdownClock, shutdownTimeoutMilliseconds))
                    {
                        try { pendingDownload.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { }
                        catch (Exception error) { log("İndirme temizliği: " + ErrorMessage(error)); result = result == 0 ? 1 : result; }
                    }
                    else
                    {
                        log("İndirmenin kapanış süresi doldu; yarım kalan dosya temizliği tamamlanamadı.");
                        result = 130;
                    }
                }
                if (pendingOpen != null)
                {
                    if (WaitWithinShutdown(pendingOpen, shutdownClock, shutdownTimeoutMilliseconds))
                    {
                        try { pendingOpen.GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { }
                        catch (Exception error) { log("Discord açılış temizliği: " + ErrorMessage(error)); result = result == 0 ? 1 : result; }
                    }
                    else
                    {
                        log("Discord açılışının kapanış süresi doldu.");
                        result = 130;
                    }
                }
                if (result != 130) log("Bağlantı kapatıldı.");
            }
            return result;
        }

        private static bool WaitWithinShutdown(Task operation, Stopwatch elapsed, int totalMilliseconds)
        {
            int remaining = (int)Math.Max(0L, (long)totalMilliseconds - elapsed.ElapsedMilliseconds);
            try { return operation.Wait(remaining); }
            catch (AggregateException) { return true; } // Completed fault/cancel; caller observes the result.
        }

        private static void OpenDiscord(SessionOperations session, SessionState state, Action<string> show, ref Task pendingOpen)
        {
            if (session.OpenDiscord == null) { show("Discord açma bu oturumda kullanılamıyor."); return; }
            Task<string> opening = null;
            try
            {
                opening = session.OpenDiscord(); pendingOpen = opening;
                if (WaitOperation(opening, state))
                {
                    string message = opening.GetAwaiter().GetResult();
                    if (!String.IsNullOrWhiteSpace(message)) show(Clean(message));
                }
            }
            catch (Exception error) { show("Discord açılamadı: " + ErrorMessage(error)); }
            finally { if (opening != null && opening.IsCompleted) pendingOpen = null; }
        }

        private static void Download(SessionOperations session, SessionState state, Action<string> show, ref Task pendingDownload, ref Task pendingOpen)
        {
            if (session.Download == null) { show("Dosya indirme bu oturumda kullanılamıyor."); return; }
            show("Discord ek dosyasının bağlantısını yapıştırıp Enter'a basın. Q: kapat.");
            string link;
            while (!state.StopRequested && !state.Ended)
            {
                if (!state.TryCommand(out link))
                {
                    if (state.InputClosed) { show("Dosya bağlantısı alınamadı; komut girişi kapalı."); return; }
                    state.Wait(); continue;
                }
                // An icon click is an open command even while D waits for a URL.
                if (link.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    OpenDiscord(session, state, show, ref pendingOpen);
                    continue;
                }
                Task<string> download = null;
                try
                {
                    download = session.Download(link);
                    pendingDownload = download;
                    if (WaitOperation(download, state))
                    {
                        string saved = download.GetAwaiter().GetResult();
                        show("Kaydedildi: " + Clean(Path.GetFileName(saved)) + " | " + Clean(Path.GetDirectoryName(saved)));
                    }
                }
                catch (Exception error) { show("Dosya indirilemedi: " + ErrorMessage(error)); }
                finally { if (download != null && download.IsCompleted) pendingDownload = null; }
                return;
            }
        }

        internal static string ErrorMessage(Exception error)
        {
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerExceptions[0];
            // Application validation errors use fixed actionable text. Framework
            // errors can contain paths or URL tokens, so show their category only.
            if (error is PackageLoadException || error is AttachmentDownloadException ||
                error is StartupFailureException || error is ConnectivityFailureException || error is GatewayProbeException ||
                error is DnsSelectionException || error is UserActionException) return Clean(error.Message);
            if (error is OperationCanceledException) return "İşlem iptal edildi.";
            return "İşlem tamamlanamadı (" + (error == null ? "Unknown" : error.GetType().Name) + ").";
        }
        internal static string SafeError(Exception error) { return ErrorMessage(error); }

        internal static int RunOfflineChecks()
        {
            int checks = 0;
            Action<bool, string> require = (condition, message) => { if (!condition) throw new InvalidOperationException(message); };
            Func<Task> complete = () => Task.FromResult(0);
            using (var state = new SessionState())
            {
                string command;
                state.Startup(() => false);
                require(!state.TryCommand(out command), "Disabled startup queued an open."); checks++;
                state.Startup(() => true); state.Startup(() => true);
                require(state.TryCommand(out command) && command == "A" && !state.TryCommand(out command), "Startup intent queued more than one open."); checks++;
                state.RequestStop(0); state.Startup(() => true);
                require(!state.TryCommand(out command), "Stopped console accepted startup."); checks++;
            }
            using (var state = new SessionState())
            {
                int opened = 0, disconnected = 0, disposed = 0;
                var logs = new List<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); return complete(); },
                    OpenDiscord = () => { opened++; return Task.FromResult("must-not-open"); },
                    Disconnect = () => { disconnected++; return complete(); }, Dispose = () => { disposed++; } };
                int result = RunSession(session, state, line => { logs.Add(line); if (line.StartsWith("Bağlantı hazır.")) state.Command("Q"); }, 20);
                require(result == 0 && opened == 0 && disconnected == 1 && disposed == 1 &&
                    logs.All(line => !line.Contains("Discord başlatıldı")), "Connect opened Discord without an A request."); checks++;
            }
            using (var state = new SessionState())
            {
                int opened = 0, connected = 0, disconnected = 0, disposed = 0;
                bool ready = false;
                state.Command("A"); // An icon click while Connect is still starting.
                var session = new SessionOperations {
                    Connect = () => { connected++; state.StateChanged(true); ready = true; return complete(); },
                    OpenDiscord = () => { require(ready && disconnected == 0, "Open ran before connection or after cleanup."); opened++; return Task.FromResult("opened-fixture"); },
                    Disconnect = () => { disconnected++; return complete(); }, Dispose = () => { disposed++; } };
                int result = RunSession(session, state, line => {
                    if (line == "opened-fixture") state.Command(opened == 1 ? "A" : "Q");
                }, 20);
                require(result == 0 && connected == 1 && opened == 2 && disconnected == 1 && disposed == 1,
                    "Repeated A ended/reconnected the connection or skipped final cleanup."); checks++;
            }
            using (var state = new SessionState())
            {
                int opened = 0, disconnected = 0; var logs = new List<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("A"); return complete(); },
                    OpenDiscord = () => { opened++; if (opened == 1) { state.Command("A"); throw new IOException("SYNTHETIC_PRIVATE_PATH"); } return Task.FromResult("opened-fixture"); },
                    Disconnect = () => { disconnected++; return complete(); }, Dispose = () => { } };
                int result = RunSession(session, state, line => { logs.Add(line); if (line == "opened-fixture") state.Command("Q"); }, 20);
                require(result == 0 && opened == 2 && disconnected == 1 && logs.Any(line => line.StartsWith("Discord açılamadı:")) &&
                    logs.All(line => !line.Contains("SYNTHETIC_PRIVATE_PATH")), "Recoverable launch error ended the connection or exposed data."); checks++;
            }
            using (var state = new SessionState())
            {
                int opened = 0; string received = null;
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("D"); state.Command("A"); state.Command("fixture-link"); return complete(); },
                    OpenDiscord = () => { opened++; return Task.FromResult("opened-fixture"); },
                    Download = link => { received = link; return Task.FromResult(@"C:\fixture\sample.txt"); },
                    Disconnect = complete, Dispose = () => { } };
                int result = RunSession(session, state, line => { if (line.StartsWith("Kaydedildi:")) state.Command("Q"); }, 20);
                require(result == 0 && opened == 1 && received == "fixture-link", "Icon open request was consumed as an attachment link."); checks++;
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<string>(); int cleaned = 0;
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("A"); return complete(); },
                    OpenDiscord = () => { state.Command("Q"); return pending.Task; },
                    Disconnect = () => {
                        Task.Run(async () => { await Task.Delay(60).ConfigureAwait(false); Interlocked.Exchange(ref cleaned, 1); pending.SetCanceled(); });
                        return complete();
                    }, Dispose = () => { } };
                require(RunSession(session, state, ignored => { }, 2000) == 0 && Volatile.Read(ref cleaned) == 1,
                    "Q returned before pending open cleanup completed."); checks++;
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("A"); return complete(); },
                    OpenDiscord = () => { state.Command("Q"); return pending.Task; },
                    Disconnect = complete, Dispose = () => { } };
                var watch = Stopwatch.StartNew();
                require(RunSession(session, state, ignored => { }, 20) == 130 && watch.ElapsedMilliseconds < 1000,
                    "Pending open prevented bounded Q shutdown."); checks++;
                pending.SetCanceled();
            }
            using (var state = new SessionState())
            {
                state.Command("Q"); state.Command("A"); string ignored;
                require(!state.TryCommand(out ignored), "Open request queued after stop."); checks++;
            }
            using (var state = new SessionState())
            {
                int reports = 0, diagnosis = 0, downloads = 0, disconnected = 0, disposed = 0;
                var logs = new List<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("M"); return complete(); },
                    MessageReport = () => { reports++; state.Command("Q"); return "yerel rapor\r\nikinci satır"; },
                    Diagnose = () => { diagnosis++; return Task.FromResult(""); },
                    Download = link => { downloads++; return Task.FromResult(""); },
                    Disconnect = () => { disconnected++; return complete(); }, Dispose = () => { disposed++; } };
                require(RunSession(session, state, logs.Add, 20) == 0 && reports == 1 && diagnosis == 0 && downloads == 0 &&
                    disconnected == 1 && disposed == 1 && logs.Contains("yerel rapor") && logs.Contains("ikinci satır"),
                    "M invoked network operations, lost report lines or changed session cleanup."); checks++;
            }
            using (var state = new SessionState())
            {
                var logs = new List<string>(); int disconnected = 0;
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("m"); return complete(); },
                    MessageReport = () => { state.Command("q"); throw new IOException("SYNTHETIC_SECRET_URL"); },
                    Diagnose = () => { throw new InvalidOperationException("M must not probe HTTP."); },
                    Disconnect = () => { disconnected++; return complete(); }, Dispose = () => { } };
                require(RunSession(session, state, logs.Add, 20) == 0 && disconnected == 1 &&
                    logs.Any(line => line.Contains("yeni ağ isteği gönderilmedi")) && logs.All(line => !line.Contains("SYNTHETIC_SECRET_URL")),
                    "M diagnostic error leaked data or prevented cleanup."); checks++;
            }
            using (var state = new SessionState())
            {
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("M"); return complete(); },
                    Diagnose = () => { throw new InvalidOperationException("Missing M must not probe HTTP."); },
                    Disconnect = complete, Dispose = () => { } };
                require(RunSession(session, state, line => { if (line.Contains("bu oturumda kullanılamıyor")) state.Command("Q"); }, 20) == 0,
                    "Unavailable M handler could not exit cleanly."); checks++;
            }
            using (var state = new SessionState())
            {
                int diagnosis = 0, disconnected = 0, disposed = 0;
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("T"); return complete(); },
                    Diagnose = () => { diagnosis++; state.Command("q"); return Task.FromResult("fixture"); },
                    Disconnect = () => { disconnected++; return complete(); }, Dispose = () => { disposed++; } };
                require(RunSession(session, state, ignored => { }, 20) == 0 && diagnosis == 1 && disconnected == 1 && disposed == 1,
                    "Console Q/diagnosis did not clean up exactly once."); checks++;
            }
            using (var state = new SessionState())
            {
                var fault = new TaskCompletionSource<int>(); fault.SetException(new IOException("SYNTHETIC_SECRET_URL"));
                var logs = new List<string>(); int disposed = 0;
                var session = new SessionOperations { Connect = () => fault.Task, Diagnose = () => Task.FromResult(""),
                    Disconnect = complete, Dispose = () => { disposed++; } };
                require(RunSession(session, state, logs.Add, 20) == 1 && disposed == 1 &&
                    logs.All(line => !line.Contains("SYNTHETIC_SECRET_URL")), "Failed console connection leaked data or skipped cleanup."); checks++;
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<int>(); int disposed = 0;
                var session = new SessionOperations { Connect = () => { state.RequestStop(130); return pending.Task; },
                    Diagnose = () => Task.FromResult(""), Disconnect = complete, Dispose = () => { disposed++; } };
                require(RunSession(session, state, ignored => { }, 20) == 130 && disposed == 1,
                    "Ctrl+C during connection did not request orderly cleanup."); checks++;
                pending.SetResult(0);
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<int>(); bool disposed = false;
                var session = new SessionOperations { Connect = () => { state.RequestStop(130); return pending.Task; },
                    Diagnose = () => Task.FromResult(""), Disconnect = () => pending.Task, Dispose = () => { disposed = true; } };
                var watch = Stopwatch.StartNew();
                require(RunSession(session, state, ignored => { }, 20) == 130 && !disposed && watch.ElapsedMilliseconds < 2000,
                    "Pending operation blocked bounded console shutdown or entered Dispose."); checks++;
                pending.SetResult(0);
            }
            using (var state = new SessionState())
            {
                bool disposed = false;
                var session = new SessionOperations { Connect = () => { state.StateChanged(true); state.StateChanged(false); return complete(); },
                    Diagnose = () => Task.FromResult(""), Disconnect = complete, Dispose = () => { disposed = true; } };
                require(RunSession(session, state, ignored => { }, 20) == 0 && disposed,
                    "Ended owned connection left the console waiting for input."); checks++;
            }
            using (var state = new SessionState())
            {
                var logs = new List<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("t"); return complete(); },
                    Diagnose = () => { state.Command("Q"); throw new IOException("SYNTHETIC_SECRET_TOKEN"); },
                    Disconnect = complete, Dispose = () => { } };
                require(RunSession(session, state, logs.Add, 20) == 0 &&
                    logs.Any(line => line.StartsWith("Tanılama tamamlanamadı:")) && logs.All(line => !line.Contains("SYNTHETIC_SECRET_TOKEN")),
                    "Console diagnostic failure leaked data or prevented user exit."); checks++;
            }
            using (var state = new SessionState())
            {
                state.Command(""); state.Command(" "); state.Command(null);
                require(!state.StopRequested && state.InputClosed, "Blank Enter or EOF stopped the connection."); checks++;
            }
            require(!Clean("bad\x1b[31m\r\ntext\0").Any(Char.IsControl), "Console output allowed control characters."); checks++;
            require(!SafeError(new InvalidOperationException("SYNTHETIC_SECRET_TOKEN")).Contains("SYNTHETIC_SECRET_TOKEN"),
                "Console generic validation error exposed an untrusted message."); checks++;
            using (var state = new SessionState())
            {
                const string link = "https://cdn.discordapp.com/attachments/123/456/sample.txt?hm=FIXTURE_SECRET";
                string received = null; var messages = new List<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("D"); state.Command(link); return complete(); },
                    Download = value => { received = value; return Task.FromResult(@"C:\fixture\sample.txt"); },
                    Disconnect = complete, Dispose = () => { } };
                int result = RunSession(session, state, line => { messages.Add(line); if (line.StartsWith("Kaydedildi:")) state.Command("Q"); }, 20);
                require(result == 0 && received == link && messages.Any(line => line.StartsWith("Kaydedildi: sample.txt")) &&
                    messages.All(line => !line.Contains("FIXTURE_SECRET") && !line.Contains("https://")),
                    "D command changed or exposed the signed attachment URL."); checks++;
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<string>(); int disconnected = 0;
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("d"); state.Command("fixture-link"); return complete(); },
                    Download = value => { state.Command("q"); return pending.Task; },
                    Disconnect = () => { disconnected++; pending.SetCanceled(); return complete(); }, Dispose = () => { } };
                require(RunSession(session, state, ignored => { }, 20) == 0 && disconnected == 1,
                    "Q could not disconnect during a pending download."); checks++;
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<string>(); int cleanupComplete = 0;
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("d"); state.Command("fixture-link"); return complete(); },
                    Download = value => { state.Command("q"); return pending.Task; },
                    Disconnect = () => {
                        Task.Run(async () => { await Task.Delay(60).ConfigureAwait(false); Interlocked.Exchange(ref cleanupComplete, 1); pending.SetCanceled(); });
                        return complete();
                    }, Dispose = () => { } };
                require(RunSession(session, state, ignored => { }, 2000) == 0 && Volatile.Read(ref cleanupComplete) == 1,
                    "Console returned before canceled download cleanup completed after disconnect."); checks++;
            }
            using (var state = new SessionState())
            {
                var pending = new TaskCompletionSource<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("d"); state.Command("fixture-link"); return complete(); },
                    Download = value => { state.Command("q"); return pending.Task; },
                    Disconnect = complete, Dispose = () => { } };
                var watch = Stopwatch.StartNew();
                require(RunSession(session, state, ignored => { }, 20) == 130 && watch.ElapsedMilliseconds < 1000,
                    "Unresponsive download exceeded the bounded cleanup budget."); checks++;
                pending.SetCanceled();
            }
            using (var state = new SessionState())
            {
                var messages = new List<string>();
                var session = new SessionOperations {
                    Connect = () => { state.StateChanged(true); state.Command("D"); state.Command(null); return complete(); },
                    Download = value => { throw new InvalidOperationException("EOF should not request a file."); },
                    Disconnect = complete, Dispose = () => { } };
                int result = RunSession(session, state, line => { messages.Add(line); if (line.Contains("komut girişi kapalı")) state.Command("Q"); }, 20);
                require(result == 0 && messages.Any(line => line.Contains("komut girişi kapalı")), "D command waited forever after EOF."); checks++;
            }
            using (var state = new SessionState())
            {
                for (int i = 0; i < 1000; i++) state.Command("T");
                int count = 0; string command;
                while (state.TryCommand(out command)) count++;
                require(count == 64, "Console input queue was not bounded."); checks++;
            }
            using (var state = new SessionState())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                using (var output = new ConsoleOutput(ignored => { }, ignored => { entered.Set(); release.WaitOne(); }))
                {
                    try
                    {
                        output.Show("hold console"); require(entered.WaitOne(2000), "Cleanup sink fixture did not start.");
                        state.RequestStop(130); bool disconnected = false, disposed = false;
                        var session = new SessionOperations { Connect = complete,
                            Disconnect = () => { disconnected = true; return complete(); }, Dispose = () => { disposed = true; } };
                        var watch = Stopwatch.StartNew();
                        require(RunSession(session, state, output.Show, 20) == 130 && disconnected && disposed && watch.ElapsedMilliseconds < 1000,
                            "Paused console output prevented cleanup or exit."); checks++;
                    }
                    finally { release.Set(); }
                }
            }
            checks += ConsoleOutput.RunOfflineChecks();
            return checks;
        }
        private static string Clean(string text)
        {
            return ConsoleOutput.Clean(text);
        }
        private static void WriteConsole(string text)
        {
            try { Console.WriteLine(Clean(text)); } catch (IOException) { }
        }
        private static void Help()
        {
            WriteConsole("Discord Erişim v4.21 — sistem tepsisi ve komut satırı");
            WriteConsole("DiscordAccess.exe                 Sistem tepsisinde bağlantıyı hazır tutar; açık CMD bırakmaz.");
            WriteConsole("DiscordAccess.exe --console       Önceki Q / T / D konsolunu açar.");
            WriteConsole("--tray                           Sistem tepsisi modu (varsayılan).");
            WriteConsole("--startup                        Kayıtlı Windows açılış tercihini uygular; elle açılış değildir.");
            WriteConsole("DiscordAccess.exe --open-discord  Açık bağlantı oturumuna Discord açma isteği gönderir.");
            WriteConsole("DiscordAccess.exe --verify-package  Motoru başlatmadan paketi doğrular.");
            WriteConsole("DiscordAccess.exe --restore       Discord kapalıyken bağlantı ve başlangıç ayarlarını geri alır.");
            WriteConsole("DiscordAccess.exe --discord-path \"D:\\Programlar\\Discord\\Discord.exe\"  Özel kurulum yolu; hatırlanır.");
            WriteConsole("--dns=auto                        Discord erişimiyle otomatik DNS seçimi (varsayılan).");
            WriteConsole("--dns=cloudflare                  Sabit Cloudflare DoH (v4.7 ile aynı).");
            WriteConsole("--dns=cloudflare-ip               Cloudflare DoH; sertifika 1.1.1.1 adresiyle doğrulanır.");
            WriteConsole("--dns=google                      Sabit Google DoH; yalnız bu sağlayıcı kullanılır.");
            WriteConsole("--dns-record-fragment             Seçilen DoH bağlantısında TLS kayıt parçalamayı açar.");
            WriteConsole("A + Enter: Discord aç. Q + Enter / Ctrl+C: bağlantıyı kapat. T: tanılama. D: dosya indir.");
            WriteConsole("D ile indirilen dosyalar İndirilenler/DiscordAccess altında kaydedilir. Boş Enter bağlantıyı kapatmaz.");
            WriteConsole("--start ve --connect eski başlatıcılar için desteklenir.");
        }
    }
}
