using System;
namespace DiscordAccess
{
    internal static class TestRunner
    {
        internal static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--resident-namespace-child")
            {
                Guid fixture;
                if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out fixture)) return 64;
                return ResidentInstanceChecks.RunNamespaceChild(args[1]);
            }
            if (args.Length > 0 && args[0] == "--resident-namespace-hold-child")
            {
                Guid fixture;
                if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out fixture)) return 64;
                return ResidentInstanceChecks.RunHoldingNamespaceChild(args[1]);
            }
            if (args.Length > 0 && args[0] == "--resident-static-alias-hold-child")
            {
                Guid fixture;
                if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out fixture)) return 64;
                return ResidentInstanceChecks.RunHoldingStaticNamespaceChild(args[1]);
            }
            if (args.Length > 0 && args[0] == "--resident-signal-child")
            {
                Guid fixture;
                if ((args.Length != 3 && args.Length != 4) || !Guid.TryParseExact(args[1], "N", out fixture) ||
                    (args[2] != "open" && args[2] != "status" && args[2] != "startup") ||
                    (args.Length == 4 && args[3] != "--no-owner")) return 64;
                return ResidentInstanceChecks.RunSignalChild(args[1], args[2], args.Length == 4);
            }
            Program.PackageDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            if (args.Length == 1 && args[0] == "--startup-package-checks")
            {
                try
                {
                    Console.WriteLine("Startup executable trust: PASS (" + StartupPathTrustChecks.Run(Program.PackageDirectory) + " checks)");
                    Console.WriteLine("Personal startup package: PASS (" + StartupPackageChecks.Run(Program.PackageDirectory) + " checks; inert files only)");
                    Console.WriteLine("Ordered Windows startup: PASS (" + StartupCoordinatorChecks.Run(Program.PackageDirectory) + " checks; synthetic registry only)");
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
            }
            if (args.Length == 1 && args[0] == "--resident-ipc-checks")
            {
                // A bounded CI preflight for namespace/ownership regressions;
                // no engine, Discord installation, profile or production lock.
                try
                {
                    Console.WriteLine("Resident launch signal: PASS (" + ManualLaunchSignal.RunOfflineChecks() + " checks)");
                    Console.WriteLine("Resident instance: PASS (" + ResidentInstanceChecks.Run() + " checks)");
                    Console.WriteLine("Resident startup intent queue: PASS (" + ResidentStartupRequests.RunOfflineChecks() + " checks)");
                    return 0;
                }
                catch (Exception ex)
                { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
            }
            if (args.Length == 1 && args[0] == "--resident-only")
            {
                try
                {
                    Console.WriteLine("Resident launch modes: PASS (" + ResidentOptions.RunOfflineChecks() + " checks)");
                    Console.WriteLine("Resident startup intent queue: PASS (" + ResidentStartupRequests.RunOfflineChecks() + " checks)");
                    Console.WriteLine("Background lifecycle: PASS (" + BackgroundLifecycle.RunOfflineChecks() + " checks)");
                    Console.WriteLine("Ordered Windows startup: PASS (" + StartupCoordinatorChecks.Run(Program.PackageDirectory) + " checks; synthetic registry only)");
                    Console.WriteLine("Console lifecycle: PASS (" + ConsoleHost.RunOfflineChecks() + " checks)");
                    return 0;
                }
                catch (Exception ex)
                { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
            }
            if (args.Length == 2 && args[0] == "--probe-install")
            {
                // Read-only signer/layout verification; no cache, journals,
                // profile reads, process creation or shortcut mutations.
                try
                {
                    var installation = DiscordInstallation.VerifyRoot(args[1]);
                    string image = installation.FindImage();
                    Console.WriteLine("Signed Stable installation probe: PASS");
                    Console.WriteLine("Root: " + installation.Root);
                    Console.WriteLine("Image: " + image);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Signed Stable installation probe: FAIL " + ex.GetType().Name + ": " + ex.Message);
                    return 1;
                }
            }
            if (args.Length == 1 && args[0] == "--after-native-output")
            {
                // Execute the independent remaining groups when the operating
                // system refuses a generated NativeOutputChecks child image.
                // This is partial evidence, never a full regression pass.
                try
                {
                    string root = Program.PackageDirectory;
                    Console.WriteLine("Partial regression: independent groups after NativeOutputChecks.");
                    Console.WriteLine("Proxy transport and half-close: PASS (" + ProxyTransportChecks.Run(root) + " checks; loopback fixtures only)");
                    Console.WriteLine("Private message diagnostics report: PASS (" + MessageReport.RunOfflineChecks(root) + " checks; synthetic log only)");
                    Console.WriteLine("Update bridge checks: PASS (" + UpdateBridge.RunOfflineTests() + " checks)");
                    IsolatedDiscord.FixtureCheck(System.IO.Path.Combine(root, "work", "f"));
                    Console.WriteLine("Isolated Discord checks: PASS (synthetic application/profile only)");
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    using (var job = new NativeSessionJob())
                    using (var first = new ProcessGate(process, job, "fixture", null, 1, "fixture", "fixture"))
                    using (var second = new ProcessGate(process, job, "fixture", null, 1, "fixture", "fixture"))
                    {
                        first.Start(); second.Start();
                        if (first.Port == second.Port || first.Port == 0 || second.Port == 0)
                            throw new InvalidOperationException("Concurrent gate port allocation failed.");
                    }
                    Console.WriteLine("Independent dynamic proxy ports: PASS");
                    LifetimeChecks.Run();
                    Console.WriteLine("Owned process relaunch and unrelated-process isolation: PASS");
                    Console.WriteLine("Package diagnostics: PASS (" + PackageChecks.Run(root) + " checks; engine not started)");
                    using (Package.Load()) { }
                    Console.WriteLine("Package engine hash/direct policy validation: PASS (engine not started)");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                    var native = ex as System.ComponentModel.Win32Exception;
                    if (native != null) Console.WriteLine("Native error code: " + native.NativeErrorCode);
                    return 1;
                }
            }
            return RegressionChecks.Run(Program.PackageDirectory);
        }
    }
}
