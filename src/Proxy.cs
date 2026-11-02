using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAccess
{
    internal sealed class ProcessGate : IDisposable
    {
        internal static readonly string[] AllowedDomains = { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com", "discordstatus.com", "discord.gift" };
        // Discord's web application CSP names this upload bucket explicitly.
        // It is an exact HTTPS-only exception, never a Google suffix permission.
        internal static readonly string[] UploadHosts = { "discord-attachments-uploads-prd.storage.googleapis.com" };
        // hCaptcha uses changing asset subdomains. This separate boundary is
        // HTTPS-only and does not inherit Discord's HTTP or media-port rules.
        internal static readonly string[] ChallengeDomains = { "hcaptcha.com" };
        internal const int MaxConnections = 64;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly object sync = new object();
        private readonly SemaphoreSlim capacity = new SemaphoreSlim(MaxConnections, MaxConnections);
        private readonly Process engine;
        private readonly NativeSessionJob job;
        private readonly string discord, updater, auth;
        private readonly int backendPort;
        private readonly int drainTimeoutMilliseconds;
        private int disposed;
        internal volatile bool DiagnosticAllowed;
        // Controller supplies exact image + separate owned-job membership here.
        // A name/path comparison alone must never grant this permission.
        internal Func<int, bool> OwnedApplication;
        // Direct official launches have a distinct, signed current-user scope.
        internal Func<int, bool> ExternalApplication;
        internal Action<ProcessGate> Faulted;
        internal Action<string> DiagnosticTrace;
        private readonly TraceBudget traceBudget = new TraceBudget();
        internal enum RelayStage { Admission, Ownership, HeaderRead, RequestPolicy, BackendConnect, BackendIdentity, BackendWrite, Relay }
        internal enum RequestFailure { None, InvalidHeader, InvalidRequestLine, InvalidTarget, UnsupportedMethod, ForbiddenHost, ConflictingHost, InvalidFraming, UnsupportedUpgrade }
        // Success chatter must never consume the failure budget. Both budgets
        // replenish so a long session still produces useful later diagnostics.
        internal sealed class TraceBudget
        {
            internal const int SuccessLimit = 32, FailureLimit = 64;
            private readonly object gate = new object();
            private DateTime window;
            private int successes, failures;
            internal bool Admit(bool failure, DateTime now)
            {
                lock (gate)
                {
                    if (window == default(DateTime) || now < window || now - window >= TimeSpan.FromMinutes(1))
                    { window = now; successes = failures = 0; }
                    if (failure) { if (failures >= FailureLimit) return false; failures++; }
                    else { if (successes >= SuccessLimit) return false; successes++; }
                    return true;
                }
            }
        }
        private void Trace(string message, bool failure = false)
        {
            if (DiagnosticTrace != null && traceBudget.Admit(failure, DateTime.UtcNow))
            { try { DiagnosticTrace(message); } catch { } }
        }

        internal static string FailureDiagnostic(RelayStage stage, string target, RequestFailure refusal, Exception error)
        {
            // Never include an exception message, URL, request header or body.
            string category = stage == RelayStage.RequestPolicy && refusal != RequestFailure.None ? refusal.ToString() :
                error is TimeoutException ? "Timeout" : error is OperationCanceledException ? "Canceled" :
                error is SocketException ? "Socket" : error is IOException ? "IO" :
                error is InvalidDataException ? "InvalidData" : error is ObjectDisposedException ? "Closed" : "Unexpected";
            return "Failure stage=" + stage + " target=" + SafeAuthority(target) + " category=" + category + SocketDiagnostic(error);
        }

        private static string SocketDiagnostic(Exception error)
        {
            // Inspect at most four exception nodes, never their message/data.
            for (int depth = 0; error != null && depth < 4; depth++, error = error.InnerException)
            {
                var socket = error as SocketException;
                if (socket != null)
                {
                    SocketError code = socket.SocketErrorCode;
                    return Enum.IsDefined(typeof(SocketError), code) ? " socket=" + code : "";
                }
            }
            return "";
        }

        internal static string RelayFailureDiagnostic(string target, Exception error, string direction, long elapsedMilliseconds)
        {
            if (direction != "client_to_engine" && direction != "engine_to_client") direction = "unknown";
            long elapsed = Math.Min(86400000L, Math.Max(0L, elapsedMilliseconds));
            return FailureDiagnostic(RelayStage.Relay, target, RequestFailure.None, error) + " direction=" + direction +
                " elapsed_ms=" + elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SafeAuthority(string authority)
        {
            if (String.IsNullOrEmpty(authority) || authority.Length > 259) return "unknown";
            var match = Regex.Match(authority, @"\A([A-Za-z0-9.-]+):([0-9]{1,5})\z");
            int port;
            if (!match.Success || !ValidDnsHost(match.Groups[1].Value) ||
                !Int32.TryParse(match.Groups[2].Value, out port) || port < 1 || port > 65535) return "unknown";
            return match.Groups[1].Value.ToLowerInvariant() + ":" + port;
        }

        internal static string SafeTarget(string header)
        {
            if (String.IsNullOrEmpty(header) || header.Length > 32768) return "unknown";
            int end = header.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0) return "unknown";
            string[] line = header.Substring(0, end).Split(' ');
            if (line.Length != 3) return "unknown";
            if (line[0] == "CONNECT") return SafeAuthority(line[1]);
            Uri target;
            if (!line[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line[1].Contains("\\") ||
                !Uri.TryCreate(line[1], UriKind.Absolute, out target) || target.Scheme != "http" ||
                !String.IsNullOrEmpty(target.UserInfo)) return "unknown";
            return SafeAuthority(target.DnsSafeHost + ":" + target.Port);
        }
        internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        internal int TrackedClients { get { lock (sync) return clients.Count; } }
        internal ProcessGate(Process engine, NativeSessionJob job, string discord, string updater, int backendPort, string username, string password, int port = 0, int drainTimeoutMilliseconds = 30000)
        {
            if (drainTimeoutMilliseconds < 1 || drainTimeoutMilliseconds > 30000) throw new ArgumentOutOfRangeException("drainTimeoutMilliseconds");
            this.drainTimeoutMilliseconds = drainTimeoutMilliseconds;
            this.engine = engine; this.job = job; this.discord = discord; this.updater = updater; this.backendPort = backendPort;
            auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            NoInheritance(listener.Server);
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        private static void NoInheritance(Socket socket)
        {
            if (!SetHandleInformation(socket.Handle, 1, 0)) throw new IOException("The socket lifetime could not be set up safely.");
        }
        internal void Start() { listener.Start(MaxConnections); AcceptLoop(); }
        private async void AcceptLoop()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (!capacity.Wait(0)) { Trace("Failure stage=Admission target=unknown category=Capacity", true); client.Close(); continue; }
                    try
                    {
                        NoInheritance(client.Client);
                        lock (sync) { if (cancellation.IsCancellationRequested) { client.Close(); capacity.Release(); return; } clients.Add(client); }
                    }
                    catch { client.Close(); capacity.Release(); throw; }
                    Handle(client);
                }
                catch
                {
                    bool unexpected = !cancellation.IsCancellationRequested;
                    Dispose();
                    if (unexpected && Faulted != null) { try { Faulted(this); } catch { } }
                    return;
                }
            }
        }
        internal async Task<bool> Permitted(TcpClient client, int destinationPort)
        {
            var peer = client.Client.RemoteEndPoint as IPEndPoint;
            if (peer == null || !peer.Address.Equals(IPAddress.Loopback)) return false;
            int pid = await FindPeer(peer.Port, destinationPort).ConfigureAwait(false);
            return PermittedOwner(pid, Process.GetCurrentProcess().Id);
        }
        private bool PermittedOwner(int pid, int currentProcessId)
        {
            if (Volatile.Read(ref disposed) != 0 || pid < 1) return false;
            // The additional app scope cannot admit the launcher's HTTP client.
            if (pid == currentProcessId) return DiagnosticAllowed;
            // The handle pins object identity even if a PID is later reused.
            // TrustedImage leases/signature checks are held by Controller.
            if (job != null && job.OwnsImage(pid, discord, updater)) return true;
            var application = Volatile.Read(ref OwnedApplication);
            try { if (application != null && application(pid)) return true; }
            catch { return false; }
            var external = Volatile.Read(ref ExternalApplication);
            try { return external != null && external(pid); }
            catch { return false; }
        }

#if TEST_BUILD
        internal static int RunApplicationOwnershipChecks()
        {
            int checks = 0;
            Action<bool, string> require = (ok, label) => {
                if (!ok) throw new InvalidOperationException("Application ownership fixture: " + label);
                checks++;
            };
            require(NativeProcess.ReadImagePath(0) == null, "zero PID");
            require(NativeProcess.ReadImagePath(-1) == null, "negative PID");
            require(NativeProcess.ReadImagePath(Int32.MaxValue) == null, "missing PID");
            using (var current = Process.GetCurrentProcess())
            {
                string self = NativeProcess.ReadImagePath(current.Id);
                require(!String.IsNullOrEmpty(self) && Path.IsPathRooted(self) &&
                    String.Equals(Path.GetFileNameWithoutExtension(self), current.ProcessName, StringComparison.OrdinalIgnoreCase), "minimal image query");
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string image = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
                const string arguments = "-NoProfile -NonInteractive -Command \"[Threading.Thread]::Sleep(15000)\"";
                using (var legacyJob = new NativeSessionJob())
                using (var appJob = new NativeSessionJob())
                using (var unrelatedJob = new NativeSessionJob())
                using (var legacy = NativeProcess.Start(legacyJob, image, arguments, system, null, null))
                using (var app = NativeProcess.Start(appJob, image, arguments, system, null, null))
                using (var unrelated = NativeProcess.Start(unrelatedJob, image, arguments, system, null, null))
                using (var gate = new ProcessGate(legacy, legacyJob, image, null, 1, "fixture", "fixture"))
                {
                    require(String.Equals(NativeProcess.ReadImagePath(app.Id), image, StringComparison.OrdinalIgnoreCase), "inert child image");
                    require(gate.PermittedOwner(legacy.Id, current.Id), "legacy job remains permitted");
                    require(!gate.PermittedOwner(app.Id, current.Id), "missing extra scope rejects app");
                    gate.OwnedApplication = pid => appJob.OwnsImage(pid, image, null);
                    require(gate.PermittedOwner(app.Id, current.Id), "separate exact app job accepted");
                    require(!gate.PermittedOwner(unrelated.Id, current.Id), "same image in unrelated job rejected");
                    require(!gate.PermittedOwner(0, current.Id), "invalid owner cannot reach predicate");
                    gate.OwnedApplication = pid => true;
                    require(!gate.PermittedOwner(current.Id, current.Id), "extra predicate cannot bypass diagnostic flag");
                    gate.DiagnosticAllowed = true;
                    require(gate.PermittedOwner(current.Id, current.Id), "diagnostic flag still admits self");
                    gate.DiagnosticAllowed = false;
                    gate.OwnedApplication = pid => { throw new IOException("PRIVATE_FIXTURE_TEXT"); };
                    require(!gate.PermittedOwner(app.Id, current.Id), "predicate exception fails closed");
                    require(gate.PermittedOwner(legacy.Id, current.Id), "legacy allowance independent of extra predicate");
                    gate.OwnedApplication = pid => appJob.OwnsImage(pid, image, null);
                    appJob.Dispose();
                    require(app.WaitForExit(4000) && !gate.PermittedOwner(app.Id, current.Id), "closed app scope rejects dead child");
                    gate.Dispose();
                    require(!gate.PermittedOwner(legacy.Id, current.Id), "disposed gate rejects ownership");
                    legacyJob.Dispose(); unrelatedJob.Dispose();
                    require(legacy.WaitForExit(4000) && unrelated.WaitForExit(4000), "owned fixture cleanup");
                }
            }
            return checks;
        }
#endif
        private async Task<int> FindPeer(int source, int destination)
        {
            // Windows may publish a new connection's owner after the handshake.
            // Keep admission closed while allowing a bounded scheduling delay.
            for (int attempt = 0; attempt < 100; attempt++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int owner = TcpOwner.Find(source, destination);
                if (owner > 0) return owner;
                await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
            }
            return -1;
        }
        private async Task<bool> ExpectedBackend(TcpClient backend)
        {
            try
            {
                if (engine == null || engine.HasExited || job == null || !job.IsMember(engine)) return false;
                var local = backend.Client.LocalEndPoint as IPEndPoint;
                return local != null && await FindPeer(backendPort, local.Port).ConfigureAwait(false) == engine.Id && !engine.HasExited;
            }
            catch { return false; }
        }
        private sealed class Header { internal string Text; internal byte[] Remainder; }
        private async Task<Header> ReadHeader(TcpClient client)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            using (var bytes = new MemoryStream())
            {
                deadline.CancelAfter(15000);
                using (deadline.Token.Register(client.Close))
                {
                    byte[] chunk = new byte[1024]; int scan = 0;
                    while (bytes.Length < 32768)
                    {
                        int n = await client.GetStream().ReadAsync(chunk, 0, Math.Min(chunk.Length, 32768 - (int)bytes.Length), deadline.Token).ConfigureAwait(false);
                        if (n == 0) throw new IOException();
                        bytes.Write(chunk, 0, n);
                        byte[] b = bytes.GetBuffer();
                        for (; scan + 3 < bytes.Length; scan++)
                            if (b[scan] == 13 && b[scan + 1] == 10 && b[scan + 2] == 13 && b[scan + 3] == 10)
                            {
                                int end = scan + 4;
                                for (int i = 0; i < end; i++) if (b[i] > 127) throw new InvalidDataException();
                                var rest = new byte[bytes.Length - end]; Buffer.BlockCopy(b, end, rest, 0, rest.Length);
                                return new Header { Text = Encoding.ASCII.GetString(b, 0, end), Remainder = rest };
                            }
                    }
                    throw new InvalidDataException();
                }
            }
        }
        private async void Handle(TcpClient client)
        {
            TcpClient backend = null;
            RelayStage stage = RelayStage.Ownership;
            RequestFailure refusal = RequestFailure.None;
            string target = "unknown";
            try
            {
                client.NoDelay = true;
                if (!await Permitted(client, Port).ConfigureAwait(false)) { Trace("Failure stage=Ownership target=unknown category=UnownedClient", true); await Reject(client, "403 Forbidden").ConfigureAwait(false); return; }
                stage = RelayStage.HeaderRead;
                Header header = await ReadHeader(client).ConfigureAwait(false);
                target = SafeTarget(header.Text);
                stage = RelayStage.RequestPolicy;
                string outgoing = RewriteHeader(header.Text, auth, out refusal);
                Trace("Owned request " + target);
                stage = RelayStage.BackendConnect;
                backend = new TcpClient(); NoInheritance(backend.Client); backend.NoDelay = true;
                lock (sync) { if (cancellation.IsCancellationRequested) throw new OperationCanceledException(); clients.Add(backend); }
                Task connecting = backend.ConnectAsync(IPAddress.Loopback, backendPort);
                if (await Task.WhenAny(connecting, Task.Delay(5000, cancellation.Token)).ConfigureAwait(false) != connecting) throw new TimeoutException();
                await connecting.ConfigureAwait(false);
                // No request, authentication or TLS data is sent to an unverified peer.
                stage = RelayStage.BackendIdentity;
                if (!await ExpectedBackend(backend).ConfigureAwait(false)) throw new InvalidDataException();
                Trace("Backend identity verified target=" + target);
                stage = RelayStage.BackendWrite;
                byte[] b = Encoding.ASCII.GetBytes(outgoing);
                NetworkStream output = backend.GetStream(), input = client.GetStream();
                await output.WriteAsync(b, 0, b.Length, cancellation.Token).ConfigureAwait(false);
                if (header.Remainder.Length != 0) await output.WriteAsync(header.Remainder, 0, header.Remainder.Length, cancellation.Token).ConfigureAwait(false);
                stage = RelayStage.Relay;
                string direction = "unknown";
                var relayElapsed = Stopwatch.StartNew();
                Task forward = input.CopyToAsync(output, 16384, cancellation.Token), reverse = output.CopyToAsync(input, 16384, cancellation.Token);
                try
                {
                    Task completed = await Task.WhenAny(forward, reverse).ConfigureAwait(false);
                    direction = completed == forward ? "client_to_engine" : "engine_to_client";
                    // A normal TCP EOF closes only that sending direction. The
                    // peer may still owe a response; HTTP/2 stream endings stay
                    // opaque TLS bytes and never reach this TCP EOF branch.
                    await completed.ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    direction = "unknown"; // A shutdown error cannot identify the failed copy task.
                    if (completed == forward) backend.Client.Shutdown(SocketShutdown.Send);
                    else client.Client.Shutdown(SocketShutdown.Send);
                    Task remaining = completed == forward ? reverse : forward;
                    direction = remaining == forward ? "client_to_engine" : "engine_to_client";
                    using (var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                    {
                        try
                        {
                            Task deadline = Task.Delay(drainTimeoutMilliseconds, drain.Token);
                            if (await Task.WhenAny(remaining, deadline).ConfigureAwait(false) != remaining)
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                throw new TimeoutException();
                            }
                            await remaining.ConfigureAwait(false);
                        }
                        finally { drain.Cancel(); }
                    }
                }
                catch (Exception ex)
                {
                    // Record only the initiating failure, before closing sockets
                    // causes the opposite pump to fail as part of cleanup.
                    if (!cancellation.IsCancellationRequested) Trace(RelayFailureDiagnostic(target, ex, direction, relayElapsed.ElapsedMilliseconds), true);
                }
                finally
                {
                    client.Close(); backend.Close();
                }
                // Socket closure also releases NetworkStream reads on the
                // legacy runtime. Observe both tasks before releasing capacity.
                try { await Task.WhenAll(forward, reverse).ConfigureAwait(false); } catch { }
            }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) Trace(FailureDiagnostic(stage, target, refusal, ex), true); }
            finally
            {
                client.Close(); if (backend != null) backend.Close();
                lock (sync) { clients.Remove(client); if (backend != null) clients.Remove(backend); }
                capacity.Release();
            }
        }
        private static async Task Reject(TcpClient client, string status)
        {
            byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            using (var deadline = new CancellationTokenSource(2000))
            using (deadline.Token.Register(client.Close))
                await client.GetStream().WriteAsync(b, 0, b.Length, deadline.Token).ConfigureAwait(false);
        }
        private static bool ValidDnsHost(string host)
        {
            if (host.Length < 1 || host.Length > 253 || !Regex.IsMatch(host, @"\A[A-Za-z0-9.-]+\z")) return false;
            if (host.Split('.').Any(label => label.Length < 1 || label.Length > 63 ||
                !Regex.IsMatch(label, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z"))) return false;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return false;
            return true;
        }
        private static bool AllowedHost(string host)
        {
            return ValidDnsHost(host) && AllowedDomains.Any(d => String.Equals(host, d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        private static bool AllowedChallengeHost(string host)
        {
            return ValidDnsHost(host) && ChallengeDomains.Any(d => String.Equals(host, d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        internal static string RewriteHeader(string header, string authorization)
        {
            RequestFailure refusal;
            return RewriteHeader(header, authorization, out refusal);
        }
        internal static string RewriteHeader(string header, string authorization, out RequestFailure refusal)
        {
            refusal = RequestFailure.InvalidHeader;
            if (String.IsNullOrEmpty(header) || header.Length > 32768 || !header.EndsWith("\r\n\r\n", StringComparison.Ordinal) || header.Any(c => c == 0 || c > 127)) throw new InvalidDataException();
            string[] lines = header.Substring(0, header.Length - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Any(line => line.Any(c => c == '\r' || c == '\n'))) throw new InvalidDataException();
            refusal = RequestFailure.InvalidRequestLine;
            string[] request = lines[0].Split(' ');
            if (request.Length != 3 || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0")) throw new InvalidDataException();
            string host; int port; bool connect = request[0] == "CONNECT";
            if (connect)
            {
                refusal = RequestFailure.InvalidTarget;
                var match = Regex.Match(request[1], @"\A([A-Za-z0-9.-]+):([1-9][0-9]{0,4})\z");
                if (!match.Success || !Int32.TryParse(match.Groups[2].Value, out port) || port > 65535)
                    throw new InvalidDataException();
                host = match.Groups[1].Value;
            }
            else
            {
                refusal = RequestFailure.UnsupportedMethod;
                if (!new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "PATCH" }.Contains(request[0])) throw new InvalidDataException();
                refusal = RequestFailure.InvalidTarget;
                Uri target;
                if (!request[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || request[1].Contains("\\") ||
                    !Uri.TryCreate(request[1], UriKind.Absolute, out target) || target.Scheme != "http" || target.Port != 80 ||
                    !String.IsNullOrEmpty(target.UserInfo) || !String.IsNullOrEmpty(target.Fragment)) throw new InvalidDataException();
                host = target.DnsSafeHost; port = 80;
            }
            refusal = RequestFailure.ForbiddenHost;
            if (!AllowedHost(host) && !(connect && port == 443 && (AllowedChallengeHost(host) || UploadHosts.Any(d =>
                String.Equals(host, d, StringComparison.OrdinalIgnoreCase))))) throw new InvalidDataException();
            // The official app permits wss://*.discord.media:* and assigns
            // different stream-control ports. Accept valid CONNECT ports only
            // for this media DNS boundary; all other CONNECT hosts retain 443.
            refusal = RequestFailure.InvalidTarget;
            if (connect && port != 443 && !host.Equals("discord.media", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".discord.media", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var output = new StringBuilder(lines[0] + "\r\n");
            bool sawHost = false, length = false, transfer = false;
            for (int i = 1; i < lines.Length; i++)
            {
                refusal = RequestFailure.InvalidHeader;
                int colon = lines[i].IndexOf(':');
                if (colon < 1 || !Regex.IsMatch(lines[i].Substring(0, colon), @"\A[!#$%&'*+\-.^_`|~0-9A-Za-z]+\z") ||
                    lines[i].Any(c => c < 32 && c != '\t' || c == 127)) throw new InvalidDataException();
                string name = lines[i].Substring(0, colon), value = lines[i].Substring(colon + 1).Trim();
                // The backend's plain HTTP upgrade path forwards headers to
                // the remote peer. Never send its private proxy credential on
                // that path. HTTPS/WSS inside CONNECT remains an opaque tunnel.
                if (!connect && (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                    ((name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) &&
                     value.Split(',').Any(token => token.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase)))))
                {
                    refusal = RequestFailure.UnsupportedUpgrade;
                    throw new InvalidDataException();
                }
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    refusal = RequestFailure.ConflictingHost;
                    if (sawHost || !(value.Equals(host, StringComparison.OrdinalIgnoreCase) || value.Equals(host + ":" + port, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException();
                    sawHost = true;
                }
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    refusal = RequestFailure.InvalidFraming;
                    long n; if (length || !Int64.TryParse(value, out n) || n < 0 || !Regex.IsMatch(value, @"\A[0-9]+\z")) throw new InvalidDataException(); length = true;
                }
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) { refusal = RequestFailure.InvalidFraming; if (transfer) throw new InvalidDataException(); transfer = true; }
                if (!name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) output.Append(lines[i]).Append("\r\n");
            }
            refusal = RequestFailure.InvalidFraming;
            if (length && transfer) throw new InvalidDataException();
            refusal = RequestFailure.ConflictingHost;
            if (!connect && !sawHost) throw new InvalidDataException();
            output.Append("Proxy-Authorization: ").Append(authorization).Append("\r\n\r\n");
            refusal = RequestFailure.None;
            return output.ToString();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { cancellation.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            lock (sync) { foreach (var client in clients) { try { client.Close(); } catch { } } clients.Clear(); }
        }
    }
    internal static class TcpOwner
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        [StructLayout(LayoutKind.Sequential)]
        private struct Row { internal uint state, localAddr, localPort, remoteAddr, remotePort, pid; }
        private static int Port(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }
        private static Row[] Rows()
        {
            int size = 0;
            uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            if (status != 0 && status != 122) return new Row[0];
            // Other applications can add sockets between the size query and
            // the snapshot. Retry a bounded resize rather than losing all owners.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (size < 4 || size > 16 * 1024 * 1024) return new Row[0];
                int capacity = size;
                IntPtr data = Marshal.AllocHGlobal(capacity);
                try
                {
                    status = GetExtendedTcpTable(data, ref size, false, 2, 5, 0);
                    if (status == 122) continue;
                    if (status != 0 || size < 4 || size > capacity) return new Row[0];
                    int count = Marshal.ReadInt32(data), stride = Marshal.SizeOf(typeof(Row));
                    if (count < 0 || count > (size - 4) / stride) return new Row[0];
                    var rows = new Row[count];
                    for (int i = 0; i < count; i++) rows[i] = (Row)Marshal.PtrToStructure(IntPtr.Add(data, 4 + i * stride), typeof(Row));
                    return rows;
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            return new Row[0];
        }
        internal static int Find(int sourcePort, int destinationPort)
        {
            foreach (var r in Rows())
                if (r.state == 5 && r.localAddr == 0x0100007f && r.remoteAddr == 0x0100007f && Port(r.localPort) == sourcePort && Port(r.remotePort) == destinationPort) return (int)r.pid;
            return -1;
        }
        // Readiness is separate from the per-request peer check in ExpectedBackend.
        // A listener state of 0 (observed on Windows) is not treated as LISTEN.
        // Instead, an empty TCP connection must have an ESTABLISHED server-side
        // tuple owned by this still-live process. No auth or application bytes
        // are sent by this probe, including to a port captured by another process.
        internal static bool BackendReady(Process process, int port)
        {
            if (process == null || process.HasExited || port < 1 || port > 65535) return false;
            return Ready(Rows(), process.Id, port, () => ProbeOwner(process, port)) && !process.HasExited;
        }
        private static bool Ready(Row[] rows, int pid, int port, Func<bool> probe)
        {
            var endpoints = rows.Where(r => r.localAddr == 0x0100007f && Port(r.localPort) == port && r.pid == pid).ToArray();
            // Remote fields are undefined for documented LISTEN rows. For an
            // unrecognized state, require an endpoint without a peer plus the
            // independent established-connection proof.
            return endpoints.Any(r => r.state == 2) ||
                (endpoints.Any(r => r.remoteAddr == 0 && r.remotePort == 0) && probe());
        }
        private static bool ProbeOwner(Process process, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    Task connected = client.ConnectAsync(IPAddress.Loopback, port);
                    if (!connected.Wait(200)) return false;
                    connected.GetAwaiter().GetResult();
                    var local = client.Client.LocalEndPoint as IPEndPoint;
                    if (local == null || !local.Address.Equals(IPAddress.Loopback)) return false;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        if (process.HasExited) return false;
                        int owner = Find(port, local.Port);
                        if (owner > 0) return owner == process.Id && !process.HasExited;
                        Thread.Sleep(10);
                    }
                }
            }
            catch { }
            return false;
        }
#if TEST_BUILD
        internal static int RunReadinessChecks()
        {
            int checks = 0, probes = 0;
            Action<bool> require = ok => { if (!ok) throw new InvalidOperationException("Backend readiness ownership check failed."); checks++; };
            Func<bool> refused = () => { probes++; return false; };
            Func<bool> verified = () => { probes++; return true; };
            var endpoint = new Row { state = 0, localAddr = 0x0100007f, localPort = 0x2f90, pid = 42 };
            require(!Ready(new[] { endpoint }, 42, 36911, refused) && probes == 1);
            require(Ready(new[] { endpoint }, 42, 36911, verified) && probes == 2);
            require(!Ready(new[] { endpoint }, 43, 36911, verified) && probes == 2);
            require(!Ready(new[] { endpoint }, 42, 36912, verified) && probes == 2);
            endpoint.localAddr = 0;
            require(!Ready(new[] { endpoint }, 42, 36911, verified) && probes == 2);
            endpoint.localAddr = 0x0100007f; endpoint.remoteAddr = 0x0100007f;
            require(!Ready(new[] { endpoint }, 42, 36911, verified) && probes == 2);
            endpoint.remoteAddr = 0; endpoint.state = 2;
            require(Ready(new[] { endpoint }, 42, 36911, refused) && probes == 2);
            endpoint.remoteAddr = UInt32.MaxValue; endpoint.remotePort = UInt32.MaxValue;
            require(Ready(new[] { endpoint }, 42, 36911, refused) && probes == 2);
            require(!Ready(new Row[0], 42, 36911, verified) && probes == 2);
            using (var process = Process.GetCurrentProcess())
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    require(BackendReady(process, port));
                    require(!BackendReady(process, ReserveEphemeralPort()));
                }
                finally { listener.Stop(); }
            }
            return checks;
        }
#endif
        internal static int ReserveEphemeralPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
    }
}
