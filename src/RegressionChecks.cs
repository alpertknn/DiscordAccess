using System;
using System.Diagnostics;
using System.IO;

namespace DiscordAccess
{
    internal static class RegressionChecks
    {
        internal static int Run(string packageRoot)
        {
            if (SecurityChecks.Run(packageRoot) != 0) return 1;
            try
            {
                Console.WriteLine("Cloud paths and redirect rejection: PASS (" + CloudPathChecks.Run(packageRoot) + " checks)");
                Console.WriteLine("Discord installation discovery: PASS (" + DiscordDiscoveryChecks.Run(packageRoot) + " checks; inert installations only)");
                ConnectivityChecks.RunOfflineChecks();
                Console.WriteLine("Connectivity checks: PASS (loopback only; no direct fallback)");
                Console.WriteLine("Automatic DNS selection: PASS (" + DnsSelection.RunOfflineChecks() + " checks; synthetic candidates only)");
                Console.WriteLine("Private startup diagnostics: PASS (" + StartupFailureException.RunOfflineChecks() + " checks; synthetic failures only)");
                GatewayChecks.RunOfflineChecks();
                Console.WriteLine("Gateway HELLO validation and fixed-proxy refusal: PASS (no remote socket)");
                Console.WriteLine("Proxy diagnostics: PASS (" + ProxyDiagnosticsChecks.Run() + " checks)");
                Console.WriteLine("Discord media port and upload host policy: PASS (" + MediaPolicyChecks.Run() + " checks)");
                Console.WriteLine("Discord attachment download: PASS (" + AttachmentDownloadChecks.Run(packageRoot) + " checks; no remote socket)");
                Console.WriteLine("Isolated session integration: PASS (" + DesktopIntegration.RunOfflineChecks(packageRoot) + " checks)");
                Console.WriteLine("Console lifecycle: PASS (" + ConsoleHost.RunOfflineChecks() + " checks)");
                Console.WriteLine("Resident launch signal: PASS (" + ManualLaunchSignal.RunOfflineChecks() + " checks)");
                Console.WriteLine("Resident instance ownership: PASS (" + ResidentInstanceChecks.Run() + " checks; fixture mutex only)");
                Console.WriteLine("Startup executable trust: PASS (" + StartupPathTrustChecks.Run(packageRoot) + " checks; fixture ACLs only)");
                Console.WriteLine("Personal startup package: PASS (" + StartupPackageChecks.Run(packageRoot) + " checks; inert files only)");
                Console.WriteLine("Private support-file trust: PASS (" + PrivateFileTrustChecks.Run(packageRoot) + " checks; fixture ACLs only)");
                Console.WriteLine("Resident launch modes: PASS (" + ResidentOptions.RunOfflineChecks() + " checks)");
                Console.WriteLine("Resident startup intent queue: PASS (" + ResidentStartupRequests.RunOfflineChecks() + " checks; synthetic signals only)");
                Console.WriteLine("Background lifecycle: PASS (" + BackgroundLifecycle.RunOfflineChecks() + " checks; synthetic operations only)");
                Console.WriteLine("Ordered Windows startup: PASS (" + StartupCoordinatorChecks.Run(packageRoot) + " checks; synthetic registry/files only)");
                Console.WriteLine("Installed Discord transactions: PASS (" + InstalledDiscord.RunOfflineChecks(packageRoot) + " checks)");
                Console.WriteLine("External official Discord identity: PASS (" + ExternalDiscordScopeChecks.Run() + " checks; inert processes only)");
                Console.WriteLine("Separate application ownership: PASS (" + ProcessGate.RunApplicationOwnershipChecks() + " checks)");
                Console.WriteLine("Owned application termination: PASS (" + NativeSessionJob.RunTerminationChecks() + " checks)");
                Console.WriteLine("Exited process observation: PASS (" + ProcessObservationChecks.Run() + " checks)");
                Console.WriteLine("Console download destinations: PASS (" + ConsoleDownload.RunOfflineChecks(packageRoot) + " checks)");
                Console.WriteLine("Child process output isolation: PASS (" + NativeOutputChecks.Run(packageRoot) + " checks; inert fixtures only)");
                Console.WriteLine("Proxy transport and half-close: PASS (" + ProxyTransportChecks.Run(packageRoot) + " checks; loopback fixtures only)");
                Console.WriteLine("Private message diagnostics report: PASS (" + MessageReport.RunOfflineChecks(packageRoot) + " checks; synthetic log only)");
                Console.WriteLine("Update bridge checks: PASS (" + UpdateBridge.RunOfflineTests() + " checks)");
                // FixtureCheck appends its own unique directory; avoid doubling
                // GUID path segments on Windows' legacy MAX_PATH file APIs.
                string fixture = Path.Combine(Path.GetFullPath(packageRoot), "work", "f");
                IsolatedDiscord.FixtureCheck(fixture);
                Console.WriteLine("Isolated Discord checks: PASS (synthetic application/profile only)");
                using (var process = Process.GetCurrentProcess())
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
                Console.WriteLine("Package diagnostics: PASS (" + PackageChecks.Run(packageRoot) + " synthetic checks; engine not started)");
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
    }
}
