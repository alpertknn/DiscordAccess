using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace DiscordAccess
{
    // Owns one candidate's complete engine/proxy lifetime. It has no Controller
    // callbacks until the winning connection is promoted into the real session.
    internal sealed class DnsConnection : IDnsCandidateConnection
    {
        internal Package Package { get; private set; }
        internal NativeSessionJob Job { get; private set; }
        internal Process Engine { get; private set; }
        internal ProcessGate Gate { get; private set; }
        public DnsOptions Options { get; private set; }
        private int disposed;
        private DnsConnection(DnsOptions option) { Options = option; }

        internal static DnsConnection Open(DnsOptions option, string discordImage, Action<string> log, CancellationToken token)
        {
            if (option == null) throw new ArgumentNullException("option");
            if (option.Automatic) throw new ArgumentException("A specific candidate is required for the DNS connection.", "option");
            token.ThrowIfCancellationRequested();
            var connection = new DnsConnection(option);
            try
            {
                connection.Package = Package.Load(option);
                token.ThrowIfCancellationRequested();
                connection.Job = new NativeSessionJob();
                int port = TcpOwner.ReserveEphemeralPort();
                string username = RandomSecret(), password = RandomSecret();
                connection.Engine = NativeProcess.Start(connection.Job, connection.Package.Engine,
                    "run -c stdin --disable-color", Path.GetDirectoryName(connection.Package.Engine), null,
                    connection.Package.Config(username, password, port));
                var deadline = Stopwatch.StartNew();
                while (!TcpOwner.BackendReady(connection.Engine, port))
                {
                    token.ThrowIfCancellationRequested();
                    if (connection.Engine.HasExited)
                        throw StartupFailureException.From(StartupStage.Engine, new IOException("Engine exited before readiness."));
                    if (deadline.ElapsedMilliseconds >= 12000)
                        throw StartupFailureException.From(StartupStage.Engine, new TimeoutException());
                    if (token.WaitHandle.WaitOne(40)) token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                connection.Gate = new ProcessGate(connection.Engine, connection.Job, discordImage, null, port, username, password);
                connection.Gate.DiagnosticAllowed = true;
                connection.Gate.DiagnosticTrace = log == null ? null : (Action<string>)(message => log("Proxy: " + message));
                connection.Gate.Start();
                token.ThrowIfCancellationRequested();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static string RandomSecret()
        {
            byte[] bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        public void Verify(CancellationToken token)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("DnsConnection");
            token.ThrowIfCancellationRequested();
            try { ConnectivityChecks.CheckDnsCandidate(Gate.Port, token); }
            catch (ConnectivityFailureException)
            {
                token.ThrowIfCancellationRequested();
                if (Engine.HasExited) throw new InvalidOperationException("The connection engine stopped.");
                throw;
            }
            token.ThrowIfCancellationRequested();
            if (Engine.HasExited) throw new InvalidOperationException("The connection engine stopped.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            bool exited = true;
            try
            {
                if (Gate != null) Gate.Dispose();
            }
            finally
            {
                try
                {
                    if (Job != null) Job.Dispose();
                }
                finally
                {
                    try
                    {
                        if (Engine != null) exited = Engine.WaitForExit(5000);
                    }
                    finally
                    {
                        try { if (Engine != null) Engine.Dispose(); }
                        finally { if (Package != null) Package.Dispose(); }
                    }
                }
            }
            if (!exited) throw new InvalidOperationException("The DNS connection could not be closed safely; no other candidate was started.");
        }
    }
}
