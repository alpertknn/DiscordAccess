using System;
using System.Diagnostics;

namespace DiscordAccess
{
    // A temporary scheduling preference for the helper's own sign-in connection.
    // It does not change Windows' logon order or another application's priority.
    internal sealed class StartupPriority : IDisposable
    {
        private readonly Func<ProcessPriorityClass> read;
        private readonly Action<ProcessPriorityClass> write;
        private bool boosted;

        private StartupPriority(bool enabled, Func<ProcessPriorityClass> read, Action<ProcessPriorityClass> write)
        {
            this.read = read; this.write = write;
            try
            {
                if (enabled && read() == ProcessPriorityClass.Normal)
                { write(ProcessPriorityClass.AboveNormal); boosted = true; }
            }
            catch { /* Scheduling is optional; failure cannot block a connection. */ }
        }

        internal static StartupPriority Begin(bool enabled)
        {
            return new StartupPriority(enabled,
                () => { using (var process = Process.GetCurrentProcess()) return process.PriorityClass; },
                value => { using (var process = Process.GetCurrentProcess()) process.PriorityClass = value; });
        }

        public void Dispose()
        {
            if (!boosted) return;
            boosted = false;
            try
            {
                // Preserve an independent change made while the connection started.
                if (read() == ProcessPriorityClass.AboveNormal) write(ProcessPriorityClass.Normal);
            }
            catch { }
        }

#if TEST_BUILD
        internal static int RunOfflineChecks()
        {
            int checks = 0, writes = 0;
            Action<bool> require = ok => { if (!ok) throw new InvalidOperationException("Startup priority policy failed."); checks++; };
            ProcessPriorityClass current = ProcessPriorityClass.Normal;
            Func<ProcessPriorityClass> read = () => current;
            Action<ProcessPriorityClass> write = value => { current = value; writes++; };
            using (var manual = new StartupPriority(false, read, write)) require(writes == 0);
            var startup = new StartupPriority(true, read, write);
            require(current == ProcessPriorityClass.AboveNormal && writes == 1);
            startup.Dispose(); startup.Dispose();
            require(current == ProcessPriorityClass.Normal && writes == 2);
            using (var changed = new StartupPriority(true, read, write)) current = ProcessPriorityClass.High;
            require(current == ProcessPriorityClass.High && writes == 3);
            using (var higher = new StartupPriority(true, read, write)) require(current == ProcessPriorityClass.High && writes == 3);
            using (var denied = new StartupPriority(true, () => { throw new UnauthorizedAccessException(); }, write)) { }
            require(writes == 3);
            using (var denied = new StartupPriority(true, () => ProcessPriorityClass.Normal, value => { throw new UnauthorizedAccessException(); })) { }
            require(writes == 3);
            return checks;
        }
#endif
    }
}
