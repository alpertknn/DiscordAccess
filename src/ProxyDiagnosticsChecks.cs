using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAccess
{
    // Fixtures never launch Discord or the engine and never use account data.
    internal static class ProxyDiagnosticsChecks
    {
        private sealed class PrivateMessageException : IOException
        {
            internal PrivateMessageException(Exception inner) : base("PRIVATE_FIXTURE", inner) { }
            public override string Message { get { throw new InvalidOperationException("Private error text must not be read."); } }
        }

        internal static int Run()
        {
            int checks = 0;
            Action<bool, string> require = (condition, label) =>
            { if (!condition) throw new InvalidOperationException("Proxy diagnostic fixture: " + label); checks++; };

            string chromium = "CONNECT gateway.discord.gg:443 HTTP/1.1\r\nHost: gateway.discord.gg:443\r\nProxy-Connection: keep-alive\r\nUser-Agent: Mozilla/5.0 Chrome/138.0.0.0 Electron/37.0.0\r\n\r\n";
            ProcessGate.RequestFailure refusal;
            string rewritten = ProcessGate.RewriteHeader(chromium, "Basic fixture-only", out refusal);
            require(refusal == ProcessGate.RequestFailure.None && rewritten.StartsWith("CONNECT gateway.discord.gg:443 HTTP/1.1\r\n", StringComparison.Ordinal), "Chromium CONNECT accepted");
            require(ProcessGate.SafeTarget(chromium) == "gateway.discord.gg:443", "Chromium safe authority");

            CheckRejection("CONNECT telemetry.example.invalid:443 HTTP/1.1\r\nHost: telemetry.example.invalid:443\r\nAuthorization: Bearer PRIVATE_FIXTURE\r\n\r\n",
                ProcessGate.RequestFailure.ForbiddenHost, "telemetry.example.invalid:443", require);
            CheckRejection("CONNECT discord.com:444 HTTP/1.1\r\nHost: discord.com:444\r\n\r\n",
                ProcessGate.RequestFailure.InvalidTarget, "discord.com:444", require);
            CheckRejection("CONNECT discord.com:443 HTTP/1.1\r\nHost: conflict.example.invalid\r\n\r\n",
                ProcessGate.RequestFailure.ConflictingHost, "discord.com:443", require);
            CheckRejection("GET http://discord.com/PRIVATE_FIXTURE?token=PRIVATE_FIXTURE HTTP/1.1\r\nHost: discord.com\r\nBad(Name): PRIVATE_FIXTURE\r\n\r\n",
                ProcessGate.RequestFailure.InvalidHeader, "discord.com:80", require);
            CheckRejection("GET http://PRIVATE_FIXTURE@discord.com/private HTTP/1.1\r\nHost: discord.com\r\n\r\n",
                ProcessGate.RequestFailure.InvalidTarget, "unknown", require);
            CheckRejection("CONNECT discord.com:443/PRIVATE_FIXTURE HTTP/1.1\r\n\r\n",
                ProcessGate.RequestFailure.InvalidTarget, "unknown", require);
            CheckRejection("GET /PRIVATE_FIXTURE?token=PRIVATE_FIXTURE HTTP/1.1\r\nHost: discord.com\r\n\r\n",
                ProcessGate.RequestFailure.InvalidTarget, "unknown", require);
            require(ProcessGate.SafeTarget("CONNECT discord.com:443\r\nPRIVATE_FIXTURE HTTP/1.1\r\n\r\n") == "unknown", "CRLF request injection hidden");
            require(ProcessGate.SafeTarget("CONNECT 127.0.0.1:443 HTTP/1.1\r\n\r\n") == "unknown", "IP literal not logged");
            require(ProcessGate.SafeTarget("CONNECT bad..discord.com:443 HTTP/1.1\r\n\r\n") == "unknown", "Malformed host not logged");
            require(ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.BackendConnect, "discord.com:443", ProcessGate.RequestFailure.None,
                new IOException("PRIVATE_FIXTURE")) == "Failure stage=BackendConnect target=discord.com:443 category=IO", "Exception message suppressed");
            require(ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.Relay, "discord.com:443\r\nPRIVATE_FIXTURE", ProcessGate.RequestFailure.None,
                new Exception("PRIVATE_FIXTURE")) == "Failure stage=Relay target=unknown category=Unexpected", "Diagnostic target revalidated");
            require(ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.BackendConnect, "discord.com:443", ProcessGate.RequestFailure.None,
                new SocketException((int)SocketError.ConnectionRefused)) == "Failure stage=BackendConnect target=discord.com:443 category=Socket socket=ConnectionRefused",
                "Direct socket failure classified by defined enum");
            Exception nested = new PrivateMessageException(new SocketException((int)SocketError.ConnectionReset));
            require(ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.Relay, "discord.com:443", ProcessGate.RequestFailure.None, nested) ==
                "Failure stage=Relay target=discord.com:443 category=IO socket=ConnectionReset", "Inner socket classified without reading private Message");
            nested = new PrivateMessageException(new PrivateMessageException(nested));
            require(ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.Relay, "discord.com:443", ProcessGate.RequestFailure.None, nested).EndsWith(" socket=ConnectionReset"),
                "Socket at fourth exception node included");
            nested = new PrivateMessageException(nested);
            require(!ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.Relay, "discord.com:443", ProcessGate.RequestFailure.None, nested).Contains(" socket="),
                "Exception inspection stops after four nodes");
            require(!ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.Relay, "discord.com:443", ProcessGate.RequestFailure.None,
                new SocketException(Int32.MaxValue)).Contains(" socket="), "Undefined socket enum never emitted numerically");
            foreach (string direction in new[] { "client_to_engine", "engine_to_client" })
                require(ProcessGate.RelayFailureDiagnostic("discord.com:443", new PrivateMessageException(new SocketException((int)SocketError.ConnectionReset)), direction, 24) ==
                    "Failure stage=Relay target=discord.com:443 category=IO socket=ConnectionReset direction=" + direction + " elapsed_ms=24",
                    "Relay direction and elapsed metadata fixed and private");
            require(ProcessGate.RelayFailureDiagnostic("discord.com:443/PRIVATE_FIXTURE", new IOException("PRIVATE_FIXTURE"), "PRIVATE_FIXTURE", -1) ==
                "Failure stage=Relay target=unknown category=IO direction=unknown elapsed_ms=0", "Relay metadata rejects arbitrary text and clamps negative duration");
            require(ProcessGate.RelayFailureDiagnostic("discord.com:443", new TimeoutException("PRIVATE_FIXTURE"), null, Int64.MaxValue).EndsWith("direction=unknown elapsed_ms=86400000"),
                "Relay elapsed duration upper bound");

            var budget = new ProcessGate.TraceBudget();
            DateTime instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int successes = 0, failures = 0;
            for (int i = 0; i < 100; i++) if (budget.Admit(false, instant)) successes++;
            for (int i = 0; i < 100; i++) if (budget.Admit(true, instant)) failures++;
            require(successes == ProcessGate.TraceBudget.SuccessLimit, "Success budget bounded");
            require(failures == ProcessGate.TraceBudget.FailureLimit, "Failure budget independent of success chatter");
            require(!budget.Admit(true, instant.AddSeconds(59)), "Failure burst bounded within window");
            require(budget.Admit(true, instant.AddMinutes(1)), "Later session errors recover after window");
            require(budget.Admit(false, instant.AddMinutes(1)), "Success window also recovers");
            require(budget.Admit(true, instant.AddMinutes(-1)), "Clock rollback does not permanently suppress errors");

            // Exercise the real catch path, including TCP-owner authorization,
            // instead of only testing the diagnostic string helper.
            using (var current = Process.GetCurrentProcess())
            using (var job = new NativeSessionJob())
            using (var gate = new ProcessGate(current, job, "fixture-unused", null, 1, "fixture", "fixture"))
            {
                var logged = new List<string>();
                gate.DiagnosticAllowed = true;
                gate.DiagnosticTrace = message => { lock (logged) logged.Add(message); };
                gate.Start();
                using (var client = new TcpClient())
                {
                    Task connected = client.ConnectAsync(IPAddress.Loopback, gate.Port);
                    require(connected.Wait(4000), "Loopback diagnostic fixture connected");
                    connected.GetAwaiter().GetResult();
                    byte[] request = Encoding.ASCII.GetBytes("GET http://telemetry.example.invalid/PRIVATE_FIXTURE?token=PRIVATE_FIXTURE HTTP/1.1\r\nHost: telemetry.example.invalid\r\nAuthorization: PRIVATE_FIXTURE\r\n\r\n");
                    client.GetStream().Write(request, 0, request.Length);
                    var watch = Stopwatch.StartNew();
                    bool found = false;
                    while (watch.ElapsedMilliseconds < 4000)
                    {
                        lock (logged) { found = logged.Count > 0; }
                        if (found) break;
                        Thread.Sleep(10);
                    }
                    require(found, "Real gate emitted rejection diagnostic");
                    lock (logged)
                    {
                        require(logged.Count == 1 && logged[0] == "Failure stage=RequestPolicy target=telemetry.example.invalid:80 category=ForbiddenHost", "Real gate emitted safe stage/target/category only: " + String.Join("; ", logged));
                    }
                }
                gate.Dispose();
                require(gate.TrackedClients == 0, "Diagnostic fixture releases clients");
            }
            return checks;
        }

        private static void CheckRejection(string request, ProcessGate.RequestFailure expected, string target, Action<bool, string> require)
        {
            ProcessGate.RequestFailure refusal = ProcessGate.RequestFailure.None;
            bool rejected = false;
            try { ProcessGate.RewriteHeader(request, "Basic fixture-only", out refusal); }
            catch (InvalidDataException) { rejected = true; }
            require(rejected && refusal == expected, "Fixed rejection category " + expected);
            string safe = ProcessGate.SafeTarget(request);
            require(safe == target, "Sanitized target " + expected);
            string diagnostic = ProcessGate.FailureDiagnostic(ProcessGate.RelayStage.RequestPolicy, safe, refusal, new InvalidDataException("PRIVATE_FIXTURE"));
            require(diagnostic == "Failure stage=RequestPolicy target=" + target + " category=" + expected &&
                diagnostic.IndexOf("PRIVATE_FIXTURE", StringComparison.Ordinal) < 0, "No URL/header/exception credential leak " + expected);
        }
    }
}
