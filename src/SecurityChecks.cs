using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAccess
{
    // No config, production process, remote connection or production credential access.
    // Network fixtures bind only 127.0.0.1 and use OS-selected ephemeral ports.
    // The backend-owner fixture starts one inert system PowerShell child in its own job.
    // Compile alongside the application so these checks exercise its actual helpers.
    public static class SecurityChecks
    {
        private const string TestAuthorization = "Basic Zml4dHVyZS1vbmx5Om5vdC1hLXJlYWwtc2VjcmV0";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
        }

        private static string Connect(string authority, params string[] headers)
        {
            return "CONNECT " + authority + " HTTP/1.1\r\n" +
                (headers.Length == 0 ? "" : String.Join("\r\n", headers) + "\r\n") + "\r\n";
        }

        private static void Rejected(string request)
        {
            bool denied = false;
            try { ProcessGate.RewriteHeader(request, TestAuthorization); }
            catch (InvalidDataException) { denied = true; }
            Require(denied, "Request was accepted instead of rejected.");
        }

        private static void Accepted(string request)
        {
            string rewritten = ProcessGate.RewriteHeader(request, TestAuthorization);
            Require(rewritten.EndsWith("\r\n\r\n", StringComparison.Ordinal), "Missing final header delimiter.");
            string[] lines = rewritten.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] credentials = lines.Where(x => x.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)).ToArray();
            Require(credentials.Length == 1 && credentials[0] == "Proxy-Authorization: " + TestAuthorization,
                "Expected exactly one fixture-only backend authorization header.");
        }

        private static string[] WindowsArguments(string commandLine)
        {
            int count;
            IntPtr argv = CommandLineToArgvW(commandLine, out count);
            if (argv == IntPtr.Zero) throw new InvalidOperationException("Windows command-line decoder failed.");
            try
            {
                string[] result = new string[count];
                for (int i = 0; i < count; i++)
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return result;
            }
            finally { LocalFree(argv); }
        }

        private static void QuoteRoundTrip(string[] values)
        {
            // argv[0] has different Windows parsing rules, so use a fixed executable name.
            string commandLine = "security-fixture.exe " + String.Join(" ", values.Select(NativeProcess.Quote));
            string[] decoded = WindowsArguments(commandLine);
            Require(decoded.Length == values.Length + 1, "Quoted arguments changed the argument count.");
            for (int i = 0; i < values.Length; i++)
                Require(String.Equals(values[i], decoded[i + 1], StringComparison.Ordinal),
                    "Quoted arguments failed Windows decoder roundtrip.");
        }

        private static void Until(Func<bool> condition, string failure)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > 4000) throw new InvalidOperationException(failure);
                Thread.Sleep(10);
            }
        }

        private static TcpClient FixtureClient(int port)
        {
            var client = new TcpClient();
            try
            {
                Task connection = client.ConnectAsync(IPAddress.Loopback, port);
                if (!connection.Wait(4000)) throw new InvalidOperationException("Loopback fixture connection timed out.");
                connection.GetAwaiter().GetResult();
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        private static int ReadOrClosed(TcpClient client)
        {
            byte[] one = new byte[1];
            try
            {
                Task<int> read = client.GetStream().ReadAsync(one, 0, one.Length);
                Task done = Task.WhenAny(read, Task.Delay(3000)).GetAwaiter().GetResult();
                Require(Object.ReferenceEquals(done, read), "Loopback fixture did not close promptly.");
                return read.GetAwaiter().GetResult();
            }
            catch (IOException) { return 0; }
            catch (SocketException) { return 0; }
            catch (ObjectDisposedException) { return 0; }
        }

        private static void CapFixture()
        {
            // The test host is the permitted diagnostic client, never a fake Discord process.
            using (var current = Process.GetCurrentProcess())
            using (var job = new NativeSessionJob())
            using (var gate = new ProcessGate(current, job, "fixture-unused", null, 1, "fixture-user", "fixture-password", 0))
            {
                var held = new List<TcpClient>();
                var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
                gate.DiagnosticTrace = diagnostics.Enqueue;
                TcpClient overflow = null, replacement = null;
                try
                {
                    gate.DiagnosticAllowed = true; gate.Start(); int port = gate.Port;
                    for (int i = 0; i < ProcessGate.MaxConnections; i++) held.Add(FixtureClient(port));
                    Until(() => gate.TrackedClients == ProcessGate.MaxConnections, "Connection fixture did not reach the admission cap.");
                    overflow = FixtureClient(port);
                    Require(ReadOrClosed(overflow) == 0, "Overflow connection remained admitted.");
                    Require(gate.TrackedClients == ProcessGate.MaxConnections, "Admission cap exceeded.");
                    held[0].Dispose(); held.RemoveAt(0);
                    Until(() => gate.TrackedClients == ProcessGate.MaxConnections - 1, "Closed client did not release capacity.");
                    replacement = FixtureClient(port);
                    Until(() => gate.TrackedClients == ProcessGate.MaxConnections, "Released capacity could not be reused.");
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(error.Message + " Tracked=" + gate.TrackedClients + "; " + String.Join("; ", diagnostics.ToArray()), error);
                }
                finally
                {
                    gate.Dispose();
                    foreach (var client in held) client.Dispose();
                    if (overflow != null) overflow.Dispose();
                    if (replacement != null) replacement.Dispose();
                }
                Require(gate.TrackedClients == 0, "Disposed gate kept tracked connections.");
            }
        }

        private static void SlowHeaderCancellationFixture()
        {
            using (var current = Process.GetCurrentProcess())
            using (var job = new NativeSessionJob())
            using (var gate = new ProcessGate(current, job, "fixture-unused", null, 1, "fixture-user", "fixture-password", 0))
            {
                gate.DiagnosticAllowed = true; gate.Start(); int port = gate.Port;
                using (var client = FixtureClient(port))
                {
                    byte[] partial = Encoding.ASCII.GetBytes("CONNECT discord.com:443 HTTP/1.1\r\nHost: discord.com:443\r\n");
                    client.GetStream().Write(partial, 0, partial.Length);
                    Until(() => gate.TrackedClients == 1, "Slow-header fixture was not tracked.");
                    gate.Dispose();
                    Require(ReadOrClosed(client) == 0, "Partial-header connection survived cancellation.");
                    Require(gate.TrackedClients == 0, "Cancellation retained client references.");
                }
                // A canceled instance must stop listening as well as close accepted clients.
                using (var probe = new TcpClient())
                {
                    bool refused = false;
                    try { probe.Connect(IPAddress.Loopback, port); }
                    catch (SocketException) { refused = true; }
                    Require(refused, "Disposed listener still accepted connections.");
                }
            }
        }

        private static void BackendOwnershipFixture()
        {
            // Start only our own inert, trusted Windows helper. It is alive and in
            // the session job; the fake backend belongs to this test host instead.
            // Therefore rejection must reach the actual TCP-owner/PID comparison.
            using (var job = new NativeSessionJob())
            {
                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string powershell = Path.Combine(systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                using (var expected = NativeProcess.Start(job, powershell,
                    "-NoProfile -NonInteractive -Command " + NativeProcess.Quote("[Threading.Thread]::Sleep(15000)"),
                    systemDirectory, null, null))
                {
                Require(!expected.HasExited && job.IsMember(expected), "Fixture helper is not alive in its job.");
                var listener = new TcpListener(IPAddress.Loopback, 0);
                TcpClient peer = null;
                try
                {
                    listener.Start(); int backendPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    using (var current = Process.GetCurrentProcess())
                        Require(TcpOwner.BackendReady(current, backendPort) && expected.Id != current.Id,
                            "Fixture listener is not owned by a PID distinct from the expected engine.");
                    Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                    using (var gate = new ProcessGate(expected, job, "fixture-unused", null, backendPort, "fixture-only-user", "fixture-only-password", 0))
                    {
                        gate.DiagnosticAllowed = true; gate.Start();
                        using (var client = FixtureClient(gate.Port))
                        {
                            byte[] header = Encoding.ASCII.GetBytes(Connect("discord.com:443", "Host: discord.com:443"));
                            client.GetStream().Write(header, 0, header.Length);
                            Require(accepted.Wait(4000), "Ownership fixture was not reached.");
                            peer = accepted.GetAwaiter().GetResult();
                            Require(!expected.HasExited && job.IsMember(expected), "Fixture helper exited before ownership validation.");
                            Require(ReadOrClosed(peer) == 0, "Request or fixture credential reached an unowned backend.");
                            Require(ReadOrClosed(client) == 0, "Rejected ownership connection stayed open.");
                            Until(() => gate.TrackedClients == 0, "Backend rejection retained connections.");
                        }
                    }
                }
                finally
                {
                    if (peer != null) peer.Dispose(); listener.Stop();
                    job.Dispose();
                    Require(expected.WaitForExit(4000), "Fixture helper survived job disposal.");
                }
                }
            }
        }

        public static int Run(string packageRoot)
        {
            // Use a lexical fixture path only. No files are created or read.
            string fixtureRoot = Path.Combine(Path.GetFullPath(packageRoot), "security-fixture-root");
            var checks = new List<KeyValuePair<string, Action>>();
            Action<string, Action> add = (name, check) => checks.Add(new KeyValuePair<string, Action>(name, check));

            add("Pinned domain policy", () =>
            {
                string[] expected = { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com", "discordstatus.com", "discord.gift" };
                Require(ProcessGate.AllowedDomains.Length == expected.Length &&
                    new HashSet<string>(ProcessGate.AllowedDomains, StringComparer.Ordinal).SetEquals(expected),
                    "Discord domain policy changed unexpectedly.");
            });
            add("Pinned HTTPS challenge domain policy", () =>
            {
                Require(ProcessGate.ChallengeDomains.Length == 1 && ProcessGate.ChallengeDomains[0] == "hcaptcha.com" &&
                    !ProcessGate.AllowedDomains.Contains("hcaptcha.com"),
                    "Login challenge domain escaped its separate HTTPS boundary.");
            });

            foreach (string domain in new[] { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com", "discordstatus.com", "discord.gift" })
            {
                string candidate = domain;
                add("Allowed CONNECT domain " + candidate, () => Accepted(Connect(candidate + ":443", "Host: " + candidate + ":443")));
            }
            add("Allowed Discord subdomain and case", () => Accepted(Connect("CDN.DISCORDAPP.COM:443", "Host: CDN.DISCORDAPP.COM:443")));
            add("Empty hostname label denied", () => Rejected(Connect("bad..discord.com:443")));
            add("Leading empty hostname label denied", () => Rejected(Connect(".discord.com:443")));
            add("Leading hostname label hyphen denied", () => Rejected(Connect("-bad.discord.com:443")));
            add("Trailing hostname label hyphen denied", () => Rejected(Connect("bad-.discord.com:443")));
            add("Overlong hostname label denied", () => Rejected(Connect(new string('a', 64) + ".discord.com:443")));
            add("Overlong hostname denied", () => Rejected(Connect(String.Join(".", new[] { new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 63), "discord", "com" }) + ":443")));
            add("HTTP absolute URI on port 80", () => Accepted("GET http://discord.com:80/api/v10/gateway HTTP/1.1\r\nHost: discord.com:80\r\n\r\n"));
            add("HTTP default port", () => Accepted("GET http://discord.com/api/v10/gateway HTTP/1.1\r\nHost: discord.com\r\n\r\n"));
            foreach (string headers in new[] {
                "Connection: Upgrade\r\nUpgrade: websocket",
                "connection: keep-alive, uPgRaDe\r\nupgrade: h2c",
                "Connection: keep-alive\r\nConnection: upgrade",
                "Upgrade: websocket", "Upgrade:", "Proxy-Connection: keep-alive, Upgrade" })
            {
                string upgradeHeaders = headers;
                add("Plain HTTP upgrade rejected before proxy authentication", () =>
                {
                    ProcessGate.RequestFailure refusal = ProcessGate.RequestFailure.None;
                    try
                    {
                        ProcessGate.RewriteHeader("GET http://discord.com/ HTTP/1.1\r\nHost: discord.com\r\n" +
                            upgradeHeaders + "\r\n\r\n", TestAuthorization, out refusal);
                        throw new InvalidOperationException("Plain HTTP upgrade accepted.");
                    }
                    catch (InvalidDataException)
                    { Require(refusal == ProcessGate.RequestFailure.UnsupportedUpgrade, "HTTP upgrade rejection category changed."); }
                });
            }
            add("Normal HTTP keep-alive remains available", () => Accepted(
                "GET http://discord.com/ HTTP/1.1\r\nHost: discord.com\r\nConnection: keep-alive\r\n\r\n"));
            add("TLS WebSocket CONNECT remains available", () => Accepted(Connect("gateway.discord.gg:443", "Host: gateway.discord.gg:443")));
            add("Existing proxy credentials removed", () =>
            {
                string request = Connect("discord.com:443", "Host: discord.com:443",
                    "Proxy-Authorization: Basic obsolete-fixture", "pRoXy-AuThOrIzAtIoN: Bearer other-fixture", "User-Agent: fixture");
                string result = ProcessGate.RewriteHeader(request, TestAuthorization);
                Accepted(request);
                Require(result.IndexOf("obsolete-fixture", StringComparison.Ordinal) < 0 &&
                    result.IndexOf("other-fixture", StringComparison.Ordinal) < 0, "Incoming credentials survived rewriting.");
                Require(result.Contains("User-Agent: fixture\r\n"), "Unrelated safe header was lost.");
            });

            string[] forbiddenAuthorities = {
                "example.com:443", "discord.com.evil.example:443", "evildiscord.com:443", "discordcom:443",
                "127.0.0.1:443", "2130706433:443", "0x7f000001:443", "[::1]:443", "[::ffff:127.0.0.1]:443",
                "user@discord.com:443", "user:pass@discord.com:443",
                "discord.com", "discord.com:80", "discord.com:444", "discord.com:0", "discord.com:65536",
                "discord.com:+443", "discord.com:-443", "discord.com:443:443", "discord.com:443/",
                "discord.com:443/path", "discord.com:443?query", "discord.com:443#fragment",
                "discord.com\\@example.com:443", "discord.com%2eexample.com:443"
            };
            for (int i = 0; i < forbiddenAuthorities.Length; i++)
            {
                string authority = forbiddenAuthorities[i];
                int caseNumber = i + 1;
                add("Forbidden CONNECT authority " + caseNumber, () => Rejected(Connect(authority)));
            }

            add("Absolute URI userinfo", () => Rejected("GET http://user:pass@discord.com/api HTTP/1.1\r\nHost: discord.com\r\n\r\n"));
            add("Absolute URI non-Discord", () => Rejected("GET http://example.com/ HTTP/1.1\r\nHost: discord.com\r\n\r\n"));
            add("Absolute URI wrong port", () => Rejected("GET http://discord.com:8080/ HTTP/1.1\r\nHost: discord.com:8080\r\n\r\n"));
            add("Origin-form request denied", () => Rejected("GET /api/v10/gateway HTTP/1.1\r\nHost: discord.com\r\n\r\n"));
            add("Unsupported scheme denied", () => Rejected("GET ftp://discord.com/file HTTP/1.1\r\nHost: discord.com\r\n\r\n"));
            add("Conflicting Host denied", () => Rejected(Connect("discord.com:443", "Host: example.com:443")));
            add("Duplicate Host denied", () => Rejected(Connect("discord.com:443", "Host: discord.com:443", "host: example.com:443")));
            add("Folded header space denied", () => Rejected(Connect("discord.com:443", "Host: discord.com:443", " continued: forbidden")));
            add("Folded header tab denied", () => Rejected(Connect("discord.com:443", "Host: discord.com:443", "\tcontinued: forbidden")));
            add("Whitespace before header colon denied", () => Rejected(Connect("discord.com:443", "Host : discord.com:443")));
            add("Invalid header name denied", () => Rejected(Connect("discord.com:443", "Bad(Name): fixture")));
            add("Missing header colon denied", () => Rejected(Connect("discord.com:443", "Malformed")));
            add("Embedded NUL denied", () => Rejected(Connect("discord.com:443", "X-Fixture: invalid\0value")));
            add("Bare LF denied", () => Rejected("CONNECT discord.com:443 HTTP/1.1\nHost: discord.com:443\n\n"));
            add("Bare CR denied", () => Rejected("CONNECT discord.com:443 HTTP/1.1\rHost: discord.com:443\r\r"));
            add("Extra request-line whitespace denied", () => Rejected("CONNECT  discord.com:443 HTTP/1.1\r\n\r\n"));
            add("Ambiguous HTTP version denied", () => Rejected("CONNECT discord.com:443 HTTP/1.1evil\r\n\r\n"));
            add("HTTP 2 text form denied", () => Rejected("CONNECT discord.com:443 HTTP/2.0\r\n\r\n"));
            add("Unknown method denied", () => Rejected("TRACE http://discord.com/ HTTP/1.1\r\nHost: discord.com\r\n\r\n"));
            add("Request body framing ambiguity denied", () => Rejected("POST http://discord.com/ HTTP/1.1\r\nHost: discord.com\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n"));
            add("Duplicate Content-Length denied", () => Rejected("POST http://discord.com/ HTTP/1.1\r\nHost: discord.com\r\nContent-Length: 1\r\ncontent-length: 2\r\n\r\n"));
            add("Overlarge header denied", () => Rejected(Connect("discord.com:443", "X-Fixture: " + new string('a', 32768))));
            add("Null header denied", () => Rejected(null));
            add("Incomplete header denied", () => Rejected("CONNECT discord.com:443 HTTP/1.1\r\n"));

            add("Path descendant accepted", () => Require(Safety.Under(Path.Combine(fixtureRoot, "app-1.2.3", "Discord.exe"), fixtureRoot), "Descendant rejected."));
            add("Path case normalization accepted", () => Require(Safety.Under(Path.Combine(fixtureRoot.ToUpperInvariant(), "child"), fixtureRoot), "Windows path case rejected."));
            add("Path internal dot normalization accepted", () => Require(Safety.Under(Path.Combine(fixtureRoot, "child", "..", "safe.exe"), fixtureRoot), "Safe normalized descendant rejected."));
            add("Path sibling prefix denied", () => Require(!Safety.Under(fixtureRoot + "-other\\Discord.exe", fixtureRoot), "Sibling prefix accepted."));
            add("Path parent traversal denied", () => Require(!Safety.Under(Path.Combine(fixtureRoot, "..", "outside.exe"), fixtureRoot), "Outside traversal accepted."));
            add("Path root itself denied", () => Require(!Safety.Under(fixtureRoot, fixtureRoot), "Root accepted as descendant."));
            add("SHA256 abc vector", () => Require(Safety.HashBytes(Encoding.ASCII.GetBytes("abc")) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA256 known vector failed."));
            add("SHA256 empty vector", () => Require(Safety.HashBytes(new byte[0]) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "SHA256 empty vector failed."));

            string[] quoteCases = {
                "", "plain", "two words", "\t", "a\tb", "a\"b", "\"quoted\"", "\\", "\\\\",
                "C:\\path with spaces\\", "C:\\path with spaces\\\\", "ends\\\"", "\\\\\"middle\\",
                "&|<>^%PATH%!`$()", "Türkçe İı şğüöç", "unicode \uD83D\uDE80", "first\r\nsecond"
            };
            for (int i = 0; i < quoteCases.Length; i++)
            {
                string value = quoteCases[i];
                int caseNumber = i + 1;
                add("Windows argv roundtrip " + caseNumber, () => QuoteRoundTrip(new[] { value }));
            }
            add("Windows argv multiple boundaries", () => QuoteRoundTrip(quoteCases));
            add("Windows argv deterministic escape coverage", () =>
            {
                char[] alphabet = { 'a', ' ', '\t', '\\', '"', 'İ' };
                var random = new Random(17884);
                for (int i = 0; i < 120; i++)
                {
                    var value = new StringBuilder();
                    int count = random.Next(0, 40);
                    for (int j = 0; j < count; j++) value.Append(alphabet[random.Next(alphabet.Length)]);
                    QuoteRoundTrip(new[] { "before", value.ToString(), "after" });
                }
            });
            add("Null argument rejected", () =>
            {
                bool rejected = false;
                try { NativeProcess.Quote(null); }
                catch (ArgumentNullException) { rejected = true; }
                Require(rejected, "Null argument did not raise ArgumentNullException.");
            });
            add("Embedded NUL argument rejected", () =>
            {
                bool rejected = false;
                try { NativeProcess.Quote("fixture\0truncated"); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "NUL argument was allowed to truncate at the Win32 boundary.");
            });

            add("Loopback connection cap and capacity reuse", CapFixture);
            add("Loopback slow-header cancellation", SlowHeaderCancellationFixture);
            add("Loopback unowned wrong-PID backend receives zero bytes", BackendOwnershipFixture);

            int failures = 0;
            foreach (var check in checks)
            {
                try { check.Value(); }
                catch (Exception ex)
                {
                    failures++;
                    // Only public fixture labels and exception types; no request or credential dumps.
                    Console.WriteLine("FAIL " + check.Key + " (" + ex.GetType().Name + ")");
                }
            }
            Console.WriteLine("SecurityChecks: " + (checks.Count - failures) + "/" + checks.Count + " passed.");
            return failures == 0 ? 0 : 1;
        }
    }
}
