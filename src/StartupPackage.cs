using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DiscordAccess
{
    // A writable download/Desktop package is fine for an explicit launch, but
    // cannot be a persistent Run target. Snapshot the approved runtime into a
    // verified personal directory and relaunch it before acquiring any session.
    // No existing ACL, package, startup preference or recovery record is changed.
    internal static class StartupPackage
    {
        private static readonly string[] Notes = { "OKU-BENI.txt", "LISANSLAR.txt", "KAYNAK.txt" };

        internal static bool RelaunchIfNeeded(string[] arguments, bool background, bool startup, bool restoring, bool fromIcon)
        {
            if (!ShouldPrepare(background, startup, restoring, fromIcon)) return false;
            string image = Assembly.GetExecutingAssembly().Location;
            try { StartupPathTrust.Verify(image); return false; }
            catch (UserActionException) { }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string target = Prepare(Path.GetDirectoryName(image), profile, Package.EngineSha256,
                StartupPathTrust.VerifyDirectory, StartupPathTrust.Verify);
            using (var lease = Safety.ReadLease(target))
            {
                StartupPathTrust.Verify(target);
                using (var process = Process.Start(new ProcessStartInfo {
                    FileName = target, Arguments = String.Join(" ", arguments.Select(NativeProcess.Quote)),
                    WorkingDirectory = Path.GetDirectoryName(target), UseShellExecute = false, CreateNoWindow = true }))
                { if (process == null) throw new IOException("Kişisel kurulum başlatılamadı."); }
            }
            return true;
        }

        private static bool ShouldPrepare(bool background, bool startup, bool restoring, bool fromIcon)
        { return background && !startup && !restoring && !fromIcon; }

        private static string Prepare(string source, string profile, string engineHash,
            Action<string> verifyDirectory, Action<string> verifyImage)
        {
            if (String.IsNullOrWhiteSpace(profile) || !Directory.Exists(profile)) throw new IOException("Kişisel kurulum klasörü bulunamadı.");
            source = Path.GetFullPath(source); profile = Path.GetFullPath(profile);
            // Verify the profile itself before adding a directory. A secure
            // child cannot compensate for an ancestor's DELETE_CHILD rights.
            verifyDirectory(profile);
            using (var image = Safety.ReadLease(Path.Combine(source, "DiscordAccess.exe")))
            using (var engine = Safety.ReadLease(Path.Combine(source, "engine", "sing-box.exe")))
            {
                string imageHash = Safety.Hash(image);
                if (!String.Equals(Safety.Hash(engine), engineHash, StringComparison.Ordinal))
                    throw Package.Error("PKG-E03", "engine\\sing-box.exe bütünlük kontrolünden geçemedi. Tam paketi yeniden çıkarın.");
                string parent = Path.Combine(profile, "DiscordAccess");
                CreateDirectory(parent, verifyDirectory);
                parent = Path.Combine(parent, "packages"); CreateDirectory(parent, verifyDirectory);
                string destination = Path.Combine(parent, imageHash.Substring(0, 16));
                CreateDirectory(destination, verifyDirectory);
                string engineDirectory = Path.Combine(destination, "engine");
                CreateDirectory(engineDirectory, verifyDirectory);
                // Copy the image last: a partially prepared package is never
                // considered launchable. Concurrent installers compare bytes;
                // they do not replace files belonging to another process/user.
                CopyVerified(engine, Path.Combine(engineDirectory, "sing-box.exe"));
                foreach (string note in Notes)
                {
                    string notePath = Path.Combine(source, note);
                    if (File.Exists(notePath))
                        using (var input = Safety.ReadLease(notePath)) CopyVerified(input, Path.Combine(destination, note));
                }
                string result = Path.Combine(destination, "DiscordAccess.exe");
                CopyVerified(image, result);
                verifyImage(result);
                return result;
            }
        }

        private static void CreateDirectory(string path, Action<string> verify)
        {
            if (!Safety.NoReparse(path)) throw new IOException("Kişisel kurulum yolu güvenli değil.");
            // The parent was verified first. New objects inherit that restricted
            // directory's ACL; existing objects must independently pass trust.
            Directory.CreateDirectory(path);
            verify(path);
        }

        private static void CopyVerified(FileStream input, string path)
        {
            if (!Safety.NoReparse(path)) throw new IOException("Kişisel kurulum dosya yolu güvenli değil.");
            input.Position = 0; string hash = Safety.Hash(input); input.Position = 0;
            try
            {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
            }
            catch (IOException)
            {
                if (!File.Exists(path)) throw;
                // A different/partial existing file is preserved and refused.
            }
            using (var actual = PrivateFileTrust.ReadLease(path))
                if (Safety.Hash(actual) != hash) throw new IOException("Kişisel kurulum dosyası farklı veya eksik. Mevcut dosya değiştirilmedi.");
        }

#if TEST_BUILD
        internal static string PrepareForChecks(string source, string profile, string engineHash,
            Action<string> verifyDirectory, Action<string> verifyImage)
        { return Prepare(source, profile, engineHash, verifyDirectory, verifyImage); }
        internal static bool ShouldPrepareForChecks(bool background, bool startup, bool restoring, bool fromIcon)
        { return ShouldPrepare(background, startup, restoring, fromIcon); }
#endif
    }
}
