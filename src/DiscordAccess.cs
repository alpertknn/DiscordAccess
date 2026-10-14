using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DiscordAccess
{
    internal static class Program
    {
        internal static string PackageDirectory = AppDomain.CurrentDomain.BaseDirectory;
        [STAThread]
        internal static int Main(string[] args)
        {
            args = args ?? new string[0];
            string[] launchArguments = args.ToArray();
            bool fromIcon = args.Length == 1 && args[0] == "--open-discord";
            bool background = !ResidentOptions.ConsoleRequested(args);
            try
            {
                if (!fromIcon && !background) ConsoleWindow.Ensure();
                TlsPolicy.UseSystemDefaults();
                DiscordPathOptions pathOptions;
                try { pathOptions = DiscordPathOptions.Parse(args); }
                catch (ArgumentException ex) { return ReportArgument(ex, background); }
                args = pathOptions.RemainingArgs;
                ResidentOptions mode;
                try { mode = ResidentOptions.Parse(args); }
                catch (ArgumentException ex) { return ReportArgument(ex, background); }
                args = mode.Arguments; background = mode.Background;
                fromIcon = args.Length == 1 && args[0] == "--open-discord";
                if (pathOptions.ExplicitPath != null && (fromIcon || args.Contains("--restore")))
                { return ReportArgument(new ArgumentException("--discord-path bağlantıyı başlatırken kullanılmalıdır; --restore veya --open-discord ile birleştirmeyin."), background); }
                DnsOptions dns;
                try { dns = DnsOptions.Parse(args); }
                catch (ArgumentException ex) { return ReportArgument(ex, background); }
                Safety.RejectElevation();
                if (fromIcon)
                {
                    string response;
                    if (ManualLaunchSignal.RequestOpen(out response)) return 0;
                }
                if (args.Contains("--self-test"))
                {
#if TEST_BUILD
                    return RegressionChecks.Run(Safety.Base);
#else
                    Console.Error.WriteLine("Bu dağıtımda geliştirici kontrolleri bulunmaz."); return 64;
#endif
                }
                if (args.Contains("--verify-package")) { using (Package.Load(dns.Automatic ? DnsOptions.FixedDefault : dns)) { } return 0; }
                if (args.Any(arg => arg == "--help" || arg == "-h" || arg == "/?")) return ConsoleHost.Run(args, dns);
                bool restoring = args.Length == 1 && args[0] == "--restore";
                if (!fromIcon && !restoring && args.Any(arg => arg != "--connect" && arg != "--start" && !DnsOptions.IsArgument(arg)))
                {
                    if (background) return ReportArgument(new ArgumentException("Tanınmayan seçenek. Kullanım için --help yazın."), true);
                    return ConsoleHost.Run(args, dns);
                }
                if (StartupPackage.RelaunchIfNeeded(launchArguments, background, mode.Startup, restoring, fromIcon)) return 0;
                using (var instance = ResidentInstance.Acquire())
                {
                    if (!instance.IsPrimary)
                    {
                        if (restoring) throw new UserActionException("DiscordAccess hâlâ açık. Önce mevcut bağlantıyı kapatın.");
                        string response;
                        if (background)
                        {
                            // The owner registers this signal before recovery
                            // or discovery; cover the tiny constructor gap only.
                            bool delivered = false;
                            for (int attempt = 0; attempt < 20; attempt++)
                            {
                                delivered = mode.Startup ? ManualLaunchSignal.RequestStartup(out response) : ManualLaunchSignal.RequestStatus(out response);
                                if (delivered) break;
                                Thread.Sleep(50);
                            }
                            if (!delivered) throw new UserActionException("Mevcut DiscordAccess oturumu yanıt vermedi. Açık bağlantıyı kapatıp yeniden deneyin.");
                        }
                        else Console.WriteLine("DiscordAccess zaten çalışıyor; ikinci bağlantı başlatılmadı.");
                        return 0;
                    }
                    using (var startupRequests = ResidentStartupRequests.ForLaunch(restoring, fromIcon))
                    {
                        if (Controller.InstalledClientRunning() && InstalledDiscord.HasPendingRecovery)
                            throw new UserActionException("Önce Discord'u sistem tepsisinden tamamen kapatın; ardından DiscordAccess.exe'yi açın. Bekleyen geri alma kaydı korunuyor.");
                        try { InstalledDiscord.Recover(SessionLog.Write); }
                        catch
                        {
                            throw new UserActionException("Geçici Discord ayarları geri alınamadı. Discord kapalıyken DiscordAccess.exe --restore deneyin. Çakışan kullanıcı değişiklikleri ve kurtarma kaydı korundu.");
                        }
                        DiscordShortcuts.Recover(SessionLog.Write);
                        if (DiscordShortcuts.HasPendingRecovery)
                            throw new UserActionException("Kısayol geri alma tamamlanamadı. Kurtarma kaydı korundu; mevcut kısayolların üzerine yazılmadı.");
                        DiscordInstallation installation = null;
                        if (!restoring) { installation = DiscordInstallation.Resolve(pathOptions.ExplicitPath); Safety.SelectDiscord(installation); }
                        if (fromIcon)
                        {
                            using (var official = new TrustedImage(Safety.FindDiscord(), true))
                            using (var launched = Process.Start(new ProcessStartInfo(official.Path) {
                                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(official.Path) })) { }
                            return 0;
                        }
                        if (restoring)
                        {
                            StartupCoordinator.Restore(SessionLog.Write);
                            Console.WriteLine("Discord bağlantı ayarları, kısayolları ve otomatik başlatma ayarları geri alındı."); return 0;
                        }
                        if (background) return TrayHost.Run(dns, installation, mode.Startup, startupRequests);
                        var consoleStartup = StartupCoordinator.Create(Path.Combine(Safety.Base,"DiscordAccess.exe"), installation,
                            new string[0], SessionLog.Write);
                        return ConsoleHost.Run(args, dns, startupRequests, consoleStartup.StartupIntent);
                    }
                }
            }
            catch (Exception ex)
            {
                if (background && !fromIcon)
                {
                    string message = ConsoleHost.SafeError(ex);
                    SessionLog.Write("DiscordAccess açılamadı: " + message);
                    System.Windows.Forms.MessageBox.Show(message, "DiscordAccess", System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return 3;
                }
                ConsoleWindow.Ensure();
                Console.Error.WriteLine("DiscordAccess açılamadı: " + ConsoleHost.SafeError(ex));
                if (fromIcon) { Console.WriteLine("Kapatmak için Enter'a basın."); Console.ReadLine(); }
                return 3;
            }
        }
        private static int ReportArgument(ArgumentException error, bool background)
        {
            if (background) System.Windows.Forms.MessageBox.Show(error.Message, "DiscordAccess", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            else { ConsoleWindow.Ensure(); Console.Error.WriteLine(error.Message); }
            return 64;
        }
    }
    internal static class Safety
    {
        internal static readonly string DefaultDiscordRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord"));
        private static DiscordInstallation selectedDiscord;
        internal static string DiscordRoot { get { return selectedDiscord == null ? DefaultDiscordRoot : selectedDiscord.Root; } }
        internal static void SelectDiscord(DiscordInstallation installation)
        { if (installation == null) throw new ArgumentNullException("installation"); selectedDiscord = installation; }
        internal static readonly string StateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordAccess");
        internal static string Base { get { return Path.GetFullPath(Program.PackageDirectory ?? AppDomain.CurrentDomain.BaseDirectory); } }
        internal static void RejectElevation()
        {
            using (var id = WindowsIdentity.GetCurrent())
                if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Programı yönetici olarak açmayın. Standart kullanıcı yetkisi yeterlidir.");
        }
        internal static bool Under(string path, string root)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        internal static bool NoReparse(string path)
        {
            try
            {
                for (string p = Path.GetFullPath(path); !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                {
                    FileAttributes attributes;
                    // GetAttributes also sees dangling links, which Exists hides.
                    try { attributes = File.GetAttributes(p); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && !NativeFile.CloudPlaceholder(p)) return false;
                }
                return true;
            }
            catch { return false; }
        }
        internal static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        internal static string Hash(Stream file)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        internal static FileStream ReadLease(string path)
        {
            if (!NoReparse(path)) throw new InvalidDataException("Dosya yolu güvenli değil.");
            var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (!NoReparse(path) || !String.Equals(Path.GetFullPath(path), NativeFile.FinalPath(f.SafeFileHandle), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Dosya hedefi değişti.");
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal static string FindDiscord()
        {
            return (selectedDiscord ?? DiscordInstallation.VerifyRoot(DefaultDiscordRoot)).FindImage();
        }
        internal static void EnsureStateDirectory()
        {
            if (!NoReparse(StateRoot)) throw new InvalidDataException("Ayar klasörü güvenli değil.");
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            string dir = Path.Combine(StateRoot, "v3");
            if (!NoReparse(dir)) throw new InvalidDataException("Ayar klasörü güvenli değil.");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, acl);
            if (!NoReparse(dir)) throw new InvalidDataException("Ayar klasörü değişti.");
            var actual = Directory.GetAccessControl(dir);
            var owner = (SecurityIdentifier)actual.GetOwner(typeof(SecurityIdentifier));
            var allowed = new[] { WindowsIdentity.GetCurrent().User.Value, "S-1-5-18", "S-1-5-32-544" };
            if (!actual.AreAccessRulesProtected || !allowed.Contains(owner.Value) ||
                actual.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Any(rule => rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value)))
                throw new InvalidDataException("Yeni ayar klasörünün erişim izinleri beklenen kullanıcılarla sınırlı değil. Mevcut izinler değiştirilmedi.");
        }
    }
    internal static class NativeFile
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTagInfo
        { internal uint Attributes, ReparseTag; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenAttributes(string path, uint access,
            uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            int informationClass, out AttributeTagInfo information, uint size);
        // The sixteen Microsoft Cloud tags differ only in bits 12..15. They
        // represent placeholders at this path, rather than name-surrogate links.
        // Every other reparse provider, junction and symbolic link stays denied.
        internal static bool IsCloudTag(uint tag)
        { return (tag & 0xffff0fffu) == 0x9000001au; }
        internal static bool CloudPlaceholder(string path)
        {
            try
            {
                // Read attributes of the reparse object itself; do not follow it
                // or hydrate its contents merely to decide whether it is Cloud.
                using (var handle = OpenAttributes(Path.GetFullPath(path), 0x80, 7, IntPtr.Zero,
                    3, 0x00200000 | 0x02000000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) return false;
                    AttributeTagInfo information;
                    return GetFileInformationByHandleEx(handle, 9, out information,
                        (uint)Marshal.SizeOf(typeof(AttributeTagInfo))) &&
                        (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0 &&
                        IsCloudTag(information.ReparseTag);
                }
            }
            catch { return false; }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        internal static string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            var b = new StringBuilder(32768); uint n = GetFinalPathNameByHandle(handle, b, (uint)b.Capacity, 0);
            if (n == 0 || n >= b.Capacity) throw new IOException("Dosya hedefi doğrulanamadı.");
            string path = b.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }
    }
    internal sealed class TrustedImage : IDisposable
    {
        internal readonly string Path;
        private FileStream lease;
        internal TrustedImage(string path, bool discord)
        {
            Path = System.IO.Path.GetFullPath(path); lease = Safety.ReadLease(Path);
            try
            {
                if (discord) VerifyDiscord(Path);
                else if (Safety.Hash(lease) != Package.EngineSha256) throw Package.Error("PKG-E03", "engine\\sing-box.exe bütünlük kontrolünden geçemedi. Tam paketi yeni bir klasöre yeniden çıkarın.");
            }
            catch { Dispose(); throw; }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { internal uint Size; [MarshalAs(UnmanagedType.LPWStr)] internal string Path; internal IntPtr File, Subject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData { internal uint Size; internal IntPtr Policy, Sip; internal uint UI, Revocation, Choice; internal IntPtr Info; internal uint State; internal IntPtr StateData, URL; internal uint Flags, Context; }
        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid action, [In, Out] ref TrustData data);
        private static void VerifyDiscord(string path)
        {
            var file = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = path };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UI = 2, Choice = 1, Info = ptr, State = 1, Flags = 0x1000 };
            try
            {
                Marshal.StructureToPtr(file, ptr, false);
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new InvalidDataException("Discord'un resmi imzası doğrulanamadı.");
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    if (cert.GetNameInfo(X509NameType.SimpleName, false) != "Discord Inc.") throw new InvalidDataException("Discord yayımlayıcısı beklenen resmi şirket değil.");
            }
            finally { data.State = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); Marshal.DestroyStructure(ptr, typeof(TrustFile)); Marshal.FreeHGlobal(ptr); }
        }
        public void Dispose() { if (lease != null) { lease.Dispose(); lease = null; } }
    }
    internal sealed class PackageLoadException : InvalidOperationException
    {
        internal readonly string Code;
        internal PackageLoadException(string code, string message) : base("[" + code + "] " + message) { Code = code; }
    }
    internal sealed class Package : IDisposable
    {
        internal const string EngineSha256 = "8bb84e511d9c4dd86e7685c9654d25248b7ff23ea0226fc52a64f9a4066587a0";
        internal readonly string Engine;
        private readonly DnsOptions dns;
        private TrustedImage image;
        private Package(string engine, DnsOptions dns) { Engine = engine; this.dns = dns; }
        internal static Package Load()
        {
            return Load(DnsOptions.FixedDefault);
        }
        internal static Package Load(DnsOptions dns) { return LoadFrom(Safety.Base, dns); }
        internal static PackageLoadException Error(string code, string message) { return new PackageLoadException(code, message); }
        internal static Package LoadFrom(string root)
        {
            return LoadFrom(root, DnsOptions.FixedDefault);
        }
        internal static Package LoadFrom(string root, DnsOptions dns)
        {
            if (dns == null) throw new ArgumentNullException("dns");
            var p = new Package(System.IO.Path.Combine(root, "engine", "sing-box.exe"), dns);
            try
            {
                try { p.image = new TrustedImage(p.Engine, false); }
                catch (PackageLoadException) { throw; }
                catch (FileNotFoundException) { throw Missing("PKG-E01", "engine\\sing-box.exe"); }
                catch (DirectoryNotFoundException) { throw Missing("PKG-E01", "engine\\sing-box.exe"); }
                catch (InvalidDataException) { throw Error("PKG-E04", "engine\\sing-box.exe dosya yolu güvenli biçimde doğrulanamadı. Tam paketi normal bir yerel klasöre çıkarın."); }
                catch { throw Error("PKG-E02", "engine\\sing-box.exe okunamadı veya başka bir işlem tarafından kilitlenmiş. Tam paketi yeni bir yerel klasöre çıkarıp yeniden deneyin."); }
                return p;
            }
            catch (PackageLoadException) { p.Dispose(); throw; }
            catch { p.Dispose(); throw Error("PKG-E02", "engine\\sing-box.exe okunamadı. Tam paketi yeni bir yerel klasöre çıkarıp yeniden deneyin."); }
        }
        private static PackageLoadException Missing(string code, string file)
        {
            return Error(code, file + " bulunamadı. Bu klasörde tam paket yok. Tam sürüm ZIP'ini tamamen yeni bir klasöre çıkarın; DiscordAccess.exe ile engine klasörü yan yana olmalı.");
        }
        internal string Config(string username, string password, int port)
        {
            // Generate all policy in memory. Old registration/configuration files
            // are neither required nor read, even when left beside the engine.
            var dnsTls = new Dictionary<string, object> { { "enabled", true }, { "server_name", dns.ServerName } };
            if (dns.RecordFragment) dnsTls.Add("record_fragment", true);
            var config = new {
                log = new { disabled = true },
                dns = new { servers = new[] { new { type = "https", tag = "discord-dns", server = dns.Address, server_port = 443, path = "/dns-query", tls = dnsTls } }, final = "discord-dns", strategy = "ipv4_only" },
                inbounds = new[] { new { type = "http", tag = "private-backend", listen = "127.0.0.1", listen_port = port, users = new[] { new { username = username, password = password } } } },
                outbounds = new[] { new { type = "direct", tag = "discord-direct", domain_resolver = new { server = "discord-dns", strategy = "ipv4_only" } } },
                route = new { rules = new object[] {
                    new { inbound = new[] { "private-backend" }, network = "tcp", domain_suffix = ProcessGate.AllowedDomains, port = 443, action = "route", outbound = "discord-direct", tls_record_fragment = true },
                    // Preserve Discord media's dynamic WSS ports. Fragmentation
                    // is intentionally limited to the HTTPS rule above.
                    new { inbound = new[] { "private-backend" }, network = "tcp", domain_suffix = new[] { "discord.media" }, port_range = new[] { "1:65535" }, action = "route", outbound = "discord-direct" },
                    new { inbound = new[] { "private-backend" }, network = "tcp", domain_suffix = ProcessGate.AllowedDomains, port = 80, action = "route", outbound = "discord-direct" },
                    // Discord's official attachment bucket uses an external
                    // storage provider. Match only this exact HTTPS host.
                    new { inbound = new[] { "private-backend" }, network = "tcp", domain = ProcessGate.UploadHosts, port = 443, action = "route", outbound = "discord-direct" },
                    // Login challenge assets use normal HTTPS without Discord's
                    // fragmentation or media-port exceptions.
                    new { inbound = new[] { "private-backend" }, network = "tcp", domain_suffix = ProcessGate.ChallengeDomains, port = 443, action = "route", outbound = "discord-direct" },
                    new { action = "reject" }
                }, default_domain_resolver = "discord-dns" }
            };
            return new JavaScriptSerializer().Serialize(config);
        }
        public void Dispose() { if (image != null) { image.Dispose(); image = null; } }
    }
}
