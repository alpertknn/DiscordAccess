using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DiscordAccess
{
    // Changes existing, current-user Discord links for one connection session.
    // The journal is durable before any link is replaced. It contains only link
    // metadata and original bytes, never Discord account/profile information.
    internal sealed class DiscordShortcuts : IDisposable
    {
        internal sealed class Target
        {
            internal string Path;
            internal string Arguments;
            internal string Icon;
        }

        internal interface IAdapter
        {
            Target Read(string path);
            void Rewrite(string path, string image, string arguments, string workingDirectory, string icon);
            void VerifyDiscordImage(string path);
        }

        internal sealed class Location
        {
            internal readonly string Path;
            internal readonly bool Recursive;
            internal Location(string path, bool recursive) { Path = System.IO.Path.GetFullPath(path); Recursive = recursive; }
        }

        // Tests supply disposable fixture roots, a fake link adapter and an ACL
        // predicate. Production always obtains all roots from Windows itself.
        internal sealed class EnvironmentPaths
        {
            internal string Journal;
            internal string DiscordRoot;
            internal string Image;
            internal Location[] Locations;
            internal IAdapter Adapter;
            internal Action PrepareState;
            internal Func<string, bool> OwnedByUser;
            internal string LegacyDiscordRoot;
            internal Func<string, string> VerifyRecoveryRoot;
        }

        private sealed class Record
        {
            public Record() { }
            public int Version { get; set; }
            public string Id { get; set; }
            public string Path { get; set; }
            public string Original { get; set; }
            public string OriginalHash { get; set; }
            public string InstalledHash { get; set; }
            public string Image { get; set; }
            public string Root { get; set; }
            public string ProxyArguments { get; set; }
        }

        private const int MaxShortcutBytes = 1024 * 1024;
        private const int MaxRecordBytes = 2 * 1024 * 1024;
        private const int MaxLinks = 64;
        private const int MaxDirectories = 256;
        private const int MaxDepth = 4;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex IdPattern = new Regex(@"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);
        private readonly EnvironmentPaths paths;
        private readonly Action<string> log;
        private readonly string proxyArguments;
        private readonly List<string> records = new List<string>();
        private bool disposed;
        internal int BoundCount { get { return records.Count; } }
        internal static bool HasPendingRecovery { get { return Pending(CreateRecoveryEnvironment()); } }

        private DiscordShortcuts(EnvironmentPaths paths, Action<string> log, string proxyArguments)
        {
            this.paths = paths; this.log = log; this.proxyArguments = proxyArguments;
        }

        internal static DiscordShortcuts BindDirect(Action<string> log, string proxyArguments)
        { return BindDirect(CreateEnvironment(), log, proxyArguments); }
        internal static void Recover(Action<string> log) { Recover(CreateRecoveryEnvironment(), log); }

        internal static IEnumerable<DiscordShortcutTarget> DiscoverTargets()
        {
            // Discovery reads only current-user links in the bounded normal
            // shortcut locations. A link is a candidate, never authority to
            // launch an image; the installation resolver validates it later.
            EnvironmentPaths paths = CreateEnvironment(Safety.DefaultDiscordRoot);
            var locations = new List<Location>(paths.Locations);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (!String.IsNullOrWhiteSpace(common) && !locations.Any(item => SamePath(item.Path, common)))
                locations.Add(new Location(common, true));
            paths.Locations = locations.ToArray();
            // Machine-wide Start Menu links are read only. SYSTEM/admin-owned
            // links can contribute a candidate, but never enter Bind's mutable
            // current-user locations or grant launch authority by themselves.
            paths.OwnedByUser = DiscoveryOwner;
            return DiscoverTargets(paths).ToArray();
        }

        internal static IEnumerable<DiscordShortcutTarget> DiscoverTargets(EnvironmentPaths paths)
        {
            var targets = new List<DiscordShortcutTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in FindCandidates(paths))
            {
                if (targets.Count == MaxLinks) break;
                try
                {
                    if (!paths.OwnedByUser(path)) continue;
                    using (var lease = Safety.ReadLease(path))
                    {
                        if (lease.Length < 1 || lease.Length > MaxShortcutBytes ||
                            !SamePath(path, NativeFile.FinalPath(lease.SafeFileHandle))) continue;
                        Target target = paths.Adapter.Read(path);
                        if (target == null || String.IsNullOrWhiteSpace(target.Path) || !Path.IsPathRooted(target.Path) ||
                            !Safety.NoReparse(target.Path)) continue;
                        string full = Path.GetFullPath(target.Path), name = Path.GetFileName(full);
                        if (!String.Equals(name, "Discord.exe", StringComparison.OrdinalIgnoreCase) &&
                            !String.Equals(name, "Update.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string arguments = target.Arguments ?? "";
                        if (arguments.Length > 4096 || !seen.Add(full + "\n" + arguments)) continue;
                        targets.Add(new DiscordShortcutTarget { Path = full, Arguments = arguments });
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (COMException) { }
            }
            return targets;
        }

        internal static bool Pending(EnvironmentPaths paths)
        {
            ValidateEnvironment(paths);
            if (!Directory.Exists(paths.Journal)) return false;
            PrepareJournal(paths);
            return Directory.EnumerateFiles(paths.Journal, "*.json", SearchOption.TopDirectoryOnly).Any();
        }

        internal static DiscordShortcuts BindDirect(EnvironmentPaths paths, Action<string> log, string proxyArguments)
        {
            ValidateProxyArguments(proxyArguments);
            ValidateEnvironment(paths);
            PrepareJournal(paths);
            Recover(paths, log);
            // Do not mix a new session with records that could not be restored.
            // Besides ambiguous ownership, adding fresh records could exceed
            // recovery's bounded record count and strand otherwise valid links.
            if (Pending(paths))
                throw new InvalidOperationException("The previous Discord shortcut session could not be fully restored. No new shortcut change was made; the recovery records were kept.");
            if (!File.Exists(paths.Image) || !Safety.NoReparse(paths.Image))
                throw new InvalidDataException("The DiscordAccess app path could not be verified; the Discord shortcuts were not changed.");
            var binding = new DiscordShortcuts(paths, log, proxyArguments);
            try
            {
                foreach (string shortcut in FindCandidates(paths))
                {
                    if (binding.records.Count == MaxLinks) break;
                    binding.BindOne(shortcut);
                }
                if (log != null) log(binding.BoundCount == 0
                    ? "No compatible existing Discord shortcut was found; you can open the same installation with A + Enter in CMD."
                    : "Existing Discord shortcuts prepared for the connection: " + binding.BoundCount + ". Discord does not open on its own.");
                return binding;
            }
            catch { binding.Restore(); throw; }
        }

        internal static void Recover(EnvironmentPaths paths, Action<string> log)
        {
            ValidateEnvironment(paths);
            if (!Directory.Exists(paths.Journal)) return;
            PrepareJournal(paths);
            string[] entries = Directory.GetFiles(paths.Journal, "*.json", SearchOption.TopDirectoryOnly);
            if (entries.Length > MaxLinks)
                throw new InvalidDataException("The shortcut recovery record limit was exceeded; existing shortcuts were not touched.");
            foreach (string entry in entries) RestoreRecord(paths, entry, log);
        }

        internal int RefreshOfficialReplacements()
        {
            if (disposed) return 0;
            int refreshed = 0;
            // Poll only the paths this session already bound. Do not scan new
            // folders or touch an unrelated/user-replaced shortcut during refresh.
            foreach (string entry in records.ToArray())
            {
                try
                {
                    if (!File.Exists(entry)) { records.Remove(entry); continue; }
                    Record record = ReadRecord(paths, entry);
                    if (!File.Exists(record.Path) || !paths.OwnedByUser(record.Path)) continue;
                    string current = FileHash(record.Path);
                    if (current == record.InstalledHash) continue;
                    EnvironmentPaths recordPaths = RecordEnvironment(paths, record);
                    using (var lease = Safety.ReadLease(record.Path))
                        if (!SamePath(recordPaths.DiscordRoot, paths.DiscordRoot) ||
                            !VerifiedOfficialLink(recordPaths, record, current, lease)) continue;
                    // Recovery independently verifies the current target again.
                    // Its new bytes become the original of the fresh transaction.
                    RestoreRecord(paths, entry, null);
                    if (File.Exists(entry)) continue;
                    records.Remove(entry);
                    int previousCount = records.Count;
                    BindOne(record.Path, null);
                    if (records.Count > previousCount) refreshed++;
                }
                catch { /* A locked/foreign link stays unchanged and journaled. */ }
            }
            if (refreshed != 0 && log != null)
                log("Shortcuts refreshed to the official Discord target during the session were prepared again for the connection: " + refreshed + ".");
            return refreshed;
        }

        internal void Restore()
        {
            if (disposed) return;
            disposed = true;
            foreach (string record in records) RestoreRecord(paths, record, log);
            records.Clear();
        }

        public void Dispose() { Restore(); }

        private void BindOne(string shortcut) { BindOne(shortcut, log); }

        private void BindOne(string shortcut, Action<string> trace)
        {
            string id = Guid.NewGuid().ToString("N");
            string inspection = JournalChild(paths, id + ".lnk");
            string entry = JournalChild(paths, id + ".json");
            Record record = null;
            bool replaced = false;
            try
            {
                if (!AllowedShortcut(paths, shortcut) || !paths.OwnedByUser(shortcut)) return;
                byte[] original = ReadBytes(shortcut, MaxShortcutBytes);
                WriteNew(inspection, original);
                Target target = paths.Adapter.Read(inspection);
                if (!AcceptedTarget(paths, target, true))
                {
                    if (LegacyIsolatedTarget(paths, target) && trace != null)
                        trace("A shortcut to the old separate Discord test copy was found; this shortcut was not changed. Use the shortcut of the official Discord installed in Windows.");
                    return;
                }
                string arguments = DirectArguments(paths, target, proxyArguments);
                // Keep the official executable as the actual shortcut target.
                // Only bounded proxy arguments and its working directory change.
                paths.Adapter.Rewrite(inspection, target.Path, arguments, Path.GetDirectoryName(target.Path), target.Icon ?? "");
                Target changed = paths.Adapter.Read(inspection);
                if (!String.Equals(changed.Path, target.Path, StringComparison.Ordinal) || changed.Arguments != arguments)
                    throw new InvalidDataException("The shortcut change could not be verified.");
                byte[] installed = ReadBytes(inspection, MaxShortcutBytes);
                record = new Record { Version = 3, Id = id, Path = Path.GetFullPath(shortcut), Original = Convert.ToBase64String(original),
                    OriginalHash = Safety.HashBytes(original), InstalledHash = Safety.HashBytes(installed), Image = target.Path,
                    Root = Path.GetFullPath(paths.DiscordRoot).TrimEnd(Path.DirectorySeparatorChar), ProxyArguments = proxyArguments };
                // The immutable record is flushed before the original shortcut
                // can change, including the intended installed byte hash.
                WriteNew(entry, Utf8.GetBytes(Serializer().Serialize(record)));
                string temporary = Sidecar(record, "install.lnk");
                string backup = Sidecar(record, "install.bak");
                WriteNew(temporary, installed);
                replaced = ReplaceIfHash(paths, record.Path, temporary, backup, record.OriginalHash, record.InstalledHash);
                if (!replaced)
                {
                    CleanupByHash(temporary, record.InstalledHash);
                    DeletePlain(entry);
                    if (trace != null) trace("A Discord shortcut changed during preparation; the user change was kept.");
                    return;
                }
                CleanupByHash(backup, record.OriginalHash);
                records.Add(entry);
            }
            catch (Exception ex)
            {
                if (record != null && File.Exists(entry))
                {
                    // A failed API call may have committed before throwing.
                    // The journal determines whether rollback is appropriate.
                    RestoreRecord(paths, entry, trace);
                }
                if (trace != null) trace("A Discord shortcut could not be prepared; the other shortcuts are kept (" + ex.GetType().Name + ").");
            }
            finally { DeletePlain(inspection); }
        }

        private static void RestoreRecord(EnvironmentPaths paths, string entry, Action<string> log)
        {
            string inspection = null;
            string temporary = null, temporaryHash = null;
            try
            {
                if (!File.Exists(entry)) return;
                Record record = ReadRecord(paths, entry);
                // A pending transaction belongs to the installation selected
                // when it was written, even if today's selection is different.
                // Schema 1 was restricted to the original default installation.
                paths = RecordEnvironment(paths, record);
                byte[] original = Convert.FromBase64String(record.Original);
                // Validate the original target independently of its saved hash;
                // a journal must never authorize a restore outside our roots or
                // turn an unrelated shortcut into an arbitrary launcher.
                inspection = JournalChild(paths, record.Id + ".check.lnk");
                if (File.Exists(inspection)) DeletePlain(inspection);
                WriteNew(inspection, original);
                Target originalTarget = paths.Adapter.Read(inspection);
                if (!AcceptedTarget(paths, originalTarget, false) ||
                    (record.Version == 3 && !String.Equals(record.Image, originalTarget.Path, StringComparison.Ordinal)))
                    throw new InvalidDataException("The original target in the shortcut record was not recognized.");
                if (!File.Exists(record.Path) || !paths.OwnedByUser(record.Path))
                {
                    CleanupRecord(record);
                    ArchiveConflict(paths, entry, log); return;
                }
                string current = FileHash(record.Path);
                string previous = Sidecar(record, "install.bak");
                if (File.Exists(previous) && FileHash(previous) != record.OriginalHash)
                {
                    // A concurrent edit captured by File.Replace is not our
                    // original. Keep that backup and do not overwrite it.
                    ArchiveConflict(paths, entry, log); return;
                }
                if (current == record.OriginalHash)
                {
                    CleanupRecord(record); DeletePlain(entry); return;
                }
                if (current != record.InstalledHash)
                {
                    if (KeepOfficialReplacement(paths, record, entry, current, log)) return;
                    CleanupRecord(record);
                    ArchiveConflict(paths, entry, log); return;
                }
                // Windows can end the process after CreateNew but before the
                // staging bytes are flushed. A fixed name would then block
                // every later recovery: the partial file cannot pass hash
                // cleanup, and CreateNew cannot overwrite it. Give each retry
                // its own staging file; preserve unknown bytes from old tries.
                // Keep the legacy staging-name length so valid shortcut paths
                // do not newly exceed Windows' path limit during recovery.
                temporary = Path.Combine(Path.GetDirectoryName(record.Path),
                    ".DiscordAccess-" + Guid.NewGuid().ToString("N") + "-restore.lnk");
                temporaryHash = record.OriginalHash;
                string backup = Sidecar(record, "restore.bak");
                CleanupByHash(backup, record.InstalledHash);
                WriteNew(temporary, original);
                if (!ReplaceIfHash(paths, record.Path, temporary, backup, record.InstalledHash, record.OriginalHash))
                {
                    CleanupByHash(temporary, record.OriginalHash);
                    ArchiveConflict(paths, entry, log); return;
                }
                CleanupByHash(backup, record.InstalledHash);
                CleanupRecord(record); DeletePlain(entry);
            }
            catch (Exception ex)
            {
                if (log != null) log("The recovery record of a Discord shortcut was kept; the automatic restore could not be completed (" + ex.GetType().Name + ").");
            }
            finally
            {
                if (temporary != null) CleanupByHash(temporary, temporaryHash);
                if (inspection != null) DeletePlain(inspection);
            }
        }

        private static bool KeepOfficialReplacement(EnvironmentPaths paths, Record record, string entry, string expectedHash, Action<string> log)
        {
            try
            {
                // The native Discord installer can rewrite its Start Menu link
                // while this session is open. Preserve that new official link,
                // but independently validate its location, arguments and signed
                // target before retiring our journal. Holding the current link
                // read lease prevents a concurrent write/rename after validation.
                using (var lease = Safety.ReadLease(record.Path))
                {
                    if (!VerifiedOfficialLink(paths, record, expectedHash, lease)) return false;
                    CleanupRecord(record);
                    DeletePlain(entry);
                    if (File.Exists(entry)) throw new IOException("The shortcut session record could not be removed.");
                    if (log != null) log("A shortcut refreshed to the official Discord target during the session was kept; the temporary shortcut record was cleared.");
                    return true;
                }
            }
            catch { return false; }
        }

        private static bool VerifiedOfficialLink(EnvironmentPaths paths, Record record, string expectedHash, FileStream lease)
        {
            return AllowedShortcut(paths, record.Path) && paths.OwnedByUser(record.Path) &&
                SamePath(record.Path, NativeFile.FinalPath(lease.SafeFileHandle)) && FileHash(record.Path) == expectedHash &&
                AcceptedTarget(paths, paths.Adapter.Read(record.Path), true);
        }

        private static Record ReadRecord(EnvironmentPaths paths, string entry)
        {
            string id = Path.GetFileNameWithoutExtension(entry);
            if (!IdPattern.IsMatch(id) || !SamePath(entry, JournalChild(paths, id + ".json")))
                throw new InvalidDataException("The shortcut record name is invalid.");
            Record record = Serializer().Deserialize<Record>(Utf8.GetString(ReadBytes(entry, MaxRecordBytes)));
            if (record == null || (record.Version != 1 && record.Version != 2 && record.Version != 3) || record.Id != id || !AllowedShortcut(paths, record.Path) ||
                !HashPattern.IsMatch(record.OriginalHash ?? "") || !HashPattern.IsMatch(record.InstalledHash ?? "") ||
                String.IsNullOrEmpty(record.Image) || !Path.IsPathRooted(record.Image) ||
                (record.Version != 3 && !String.Equals(Path.GetFileName(record.Image), "DiscordAccess.exe", StringComparison.OrdinalIgnoreCase)) ||
                (record.Version == 3 && !String.Equals(Path.GetFileName(record.Image), "Discord.exe", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(Path.GetFileName(record.Image), "Update.exe", StringComparison.OrdinalIgnoreCase)) ||
                (record.Version == 1 && !String.IsNullOrEmpty(record.Root)) ||
                (record.Version != 3 && !String.IsNullOrEmpty(record.ProxyArguments)) ||
                (record.Version >= 2 && (String.IsNullOrEmpty(record.Root) || !Path.IsPathRooted(record.Root) ||
                    !String.Equals(record.Root, Path.GetFullPath(record.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))))
                throw new InvalidDataException("The shortcut recovery record is invalid.");
            if (record.Version == 3) ValidateProxyArguments(record.ProxyArguments);
            byte[] original = Convert.FromBase64String(record.Original ?? "");
            if (original.Length == 0 || original.Length > MaxShortcutBytes || Safety.HashBytes(original) != record.OriginalHash)
                throw new InvalidDataException("The original shortcut record is corrupt.");
            return record;
        }

        private static EnvironmentPaths RecordEnvironment(EnvironmentPaths paths, Record record)
        {
            string root;
            if (record.Version == 1)
                root = paths.LegacyDiscordRoot ?? Safety.DefaultDiscordRoot;
            else
            {
                if (paths.VerifyRecoveryRoot == null) throw new InvalidDataException("The shortcut recovery root could not be verified.");
                root = paths.VerifyRecoveryRoot(record.Root);
                if (!SamePath(root, record.Root)) throw new InvalidDataException("The shortcut recovery root changed.");
            }
            var result = new EnvironmentPaths {
                Journal = paths.Journal, DiscordRoot = root, Image = paths.Image,
                Locations = paths.Locations, Adapter = paths.Adapter, PrepareState = paths.PrepareState,
                OwnedByUser = paths.OwnedByUser, LegacyDiscordRoot = paths.LegacyDiscordRoot,
                VerifyRecoveryRoot = paths.VerifyRecoveryRoot
            };
            ValidateEnvironment(result);
            return result;
        }

        // File.Replace preserves the original link's ACL. The backup also lets
        // us detect a concurrent rename after the hash comparison, preserving
        // the user's new bytes instead of silently discarding them.
        private static bool ReplaceIfHash(EnvironmentPaths paths, string destination, string temporary, string backup,
            string expectedHash, string installedHash)
        {
            if (!AllowedShortcut(paths, destination) || !File.Exists(destination) || !paths.OwnedByUser(destination) ||
                !Safety.NoReparse(temporary) || !Safety.NoReparse(backup) || File.Exists(backup) ||
                FileHash(destination) != expectedHash) return false;
            File.Replace(temporary, destination, backup, true);
            if (FileHash(backup) == expectedHash) return true;
            // Never discard a concurrently edited original. Leave its backup
            // and journal available if another edit also prevents rollback.
            if (FileHash(destination) == installedHash && Safety.NoReparse(backup))
            {
                string rollbackBackup = backup + ".rollback.bak";
                if (!File.Exists(rollbackBackup) && Safety.NoReparse(rollbackBackup))
                {
                    // Another edit can occur after the hash check above. Keep
                    // what this rollback actually replaced, too; delete it only
                    // if it is still our exact installed bytes. A user's second
                    // edit remains in this backup for conflict recovery.
                    File.Replace(backup, destination, rollbackBackup, true);
                    CleanupByHash(rollbackBackup, installedHash);
                }
            }
            throw new IOException("A user change was found while changing the shortcut.");
        }

        private static void CleanupRecord(Record record)
        {
            CleanupByHash(Sidecar(record, "install.lnk"), record.InstalledHash);
            CleanupByHash(Sidecar(record, "install.bak"), record.OriginalHash);
            CleanupByHash(Sidecar(record, "install.bak") + ".rollback.bak", record.InstalledHash);
            CleanupByHash(Sidecar(record, "restore.lnk"), record.OriginalHash);
            CleanupByHash(Sidecar(record, "restore.bak"), record.InstalledHash);
            CleanupByHash(Sidecar(record, "restore.bak") + ".rollback.bak", record.OriginalHash);
        }

        private static void ArchiveConflict(EnvironmentPaths paths, string entry, Action<string> log)
        {
            string archive = JournalChild(paths, Path.GetFileNameWithoutExtension(entry) + ".conflict");
            if (!File.Exists(archive) && Safety.NoReparse(entry) && Safety.NoReparse(archive)) File.Move(entry, archive);
            if (log != null) log("A Discord shortcut was changed or removed during the session; it was not overwritten. Its original copy was saved in the recovery record.");
        }

        private static string Sidecar(Record record, string suffix)
        {
            if (!IdPattern.IsMatch(record.Id ?? "")) throw new InvalidDataException("The shortcut operation ID is invalid.");
            return Path.Combine(Path.GetDirectoryName(record.Path), ".DiscordAccess-" + record.Id + "-" + suffix);
        }

        private static IEnumerable<string> FindCandidates(EnvironmentPaths paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int directories = 0, inspectedLinks = 0;
            foreach (Location location in paths.Locations)
            {
                if (!Directory.Exists(location.Path) || !Safety.NoReparse(location.Path)) continue;
                var pending = new Queue<KeyValuePair<string, int>>();
                pending.Enqueue(new KeyValuePair<string, int>(location.Path, 0));
                while (pending.Count != 0 && directories < MaxDirectories && inspectedLinks < 1024)
                {
                    var current = pending.Dequeue(); directories++;
                    if (!Safety.NoReparse(current.Key)) continue;
                    string[] files, children;
                    try { files = Directory.GetFiles(current.Key, "*.lnk"); children = location.Recursive && current.Value < MaxDepth ? Directory.GetDirectories(current.Key) : new string[0]; }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    foreach (string file in files)
                    {
                        if (++inspectedLinks > 1024) break;
                        if (Path.GetFileName(file).StartsWith(".DiscordAccess-", StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(file) && AllowedShortcut(paths, file)) yield return file;
                    }
                    foreach (string child in children)
                        if (pending.Count + directories < MaxDirectories && Safety.NoReparse(child))
                            pending.Enqueue(new KeyValuePair<string, int>(child, current.Value + 1));
                }
            }
        }

        private static bool AllowedShortcut(EnvironmentPaths paths, string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) ||
                    !Safety.NoReparse(path) || Path.GetFileName(path).StartsWith(".DiscordAccess-", StringComparison.OrdinalIgnoreCase)) return false;
                string full = Path.GetFullPath(path), parent = Path.GetDirectoryName(full);
                foreach (Location location in paths.Locations)
                {
                    if (SamePath(parent, location.Path)) return true;
                    if (location.Recursive && Safety.Under(parent, location.Path))
                    {
                        string relative = parent.Substring(location.Path.TrimEnd(Path.DirectorySeparatorChar).Length).Trim(Path.DirectorySeparatorChar);
                        if (relative.Split(Path.DirectorySeparatorChar).Length <= MaxDepth) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        private static bool AcceptedTarget(EnvironmentPaths paths, Target target, bool verifyImage)
        {
            if (target == null || String.IsNullOrWhiteSpace(target.Path) || !Path.IsPathRooted(target.Path) || !Safety.NoReparse(target.Path)) return false;
            string full = Path.GetFullPath(target.Path);
            string arguments = (target.Arguments ?? "").Trim();
            bool accepted = false;
            if (SamePath(full, Path.Combine(paths.DiscordRoot, "Update.exe")))
                accepted = SafeProcessStart(arguments);
            else if (SamePath(full, Path.Combine(paths.DiscordRoot, "Discord.exe")))
                // Current Discord installs use a signed root launcher in their
                // normal Start Menu link, rather than Squirrel's Update.exe.
                accepted = arguments.Length == 0 || SafeProcessStart(arguments);
            else if (String.Equals(Path.GetFileName(full), "Discord.exe", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(full), folder = Path.GetFileName(parent);
                Version version;
                accepted = SamePath(Path.GetDirectoryName(parent), paths.DiscordRoot) && folder.StartsWith("app-", StringComparison.Ordinal) &&
                    Version.TryParse(folder.Substring(4), out version) && (arguments.Length == 0 || arguments == "--start-minimized");
            }
            if (!accepted) return false;
            if (verifyImage) paths.Adapter.VerifyDiscordImage(full);
            return true;
        }

        private static bool SafeProcessStart(string arguments)
        {
            return Regex.IsMatch(arguments, "\\A--processStart[ \\t]+(?:Discord\\.exe|\"Discord\\.exe\")\\z", RegexOptions.CultureInvariant);
        }

        internal static void ValidateProxyArguments(string arguments)
        {
            Match match = Regex.Match(arguments ?? "", @"\A--proxy-server=http://127\.0\.0\.1:([1-9][0-9]{0,4}) --proxy-bypass-list=(?:""127\.0\.0\.1;localhost""|127\.0\.0\.1;localhost) --disable-quic\z", RegexOptions.CultureInvariant);
            int port;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out port) || port < 1 || port > 65535)
                throw new InvalidDataException("Only local connection options can be used for a Discord shortcut.");
        }

        private static string DirectArguments(EnvironmentPaths paths, Target target, string arguments)
        {
            ValidateProxyArguments(arguments);
            if (SamePath(target.Path, Path.Combine(paths.DiscordRoot, "Update.exe")) ||
                SamePath(target.Path, Path.Combine(paths.DiscordRoot, "Discord.exe")))
                return "--processStart Discord.exe --process-start-args " + QuoteWindows(arguments);
            return ((target.Arguments ?? "").Trim() == "--start-minimized" ? "--start-minimized " : "") + arguments;
        }

        private static string QuoteWindows(string value)
        {
            var quoted = new StringBuilder(); quoted.Append('"');
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append('"'); }
                else { quoted.Append('\\', slashes); quoted.Append(c); }
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"'); return quoted.ToString();
        }

        private static bool LegacyIsolatedTarget(EnvironmentPaths paths, Target target)
        {
            // Recognition is diagnostic only. Never recover or bind an old
            // isolated client as though it were the Windows installation.
            try
            {
                if (target == null || String.IsNullOrWhiteSpace(target.Path) || !Path.IsPathRooted(target.Path)) return false;
                string full = Path.GetFullPath(target.Path), parent = Path.GetDirectoryName(full);
                string legacyRoot = Path.Combine(Path.GetDirectoryName(paths.Journal), "client");
                Version version;
                string folder = Path.GetFileName(parent);
                return String.Equals(Path.GetFileName(full), "Discord.exe", StringComparison.OrdinalIgnoreCase) &&
                    SamePath(Path.GetDirectoryName(parent), legacyRoot) && folder.StartsWith("app-", StringComparison.Ordinal) &&
                    Version.TryParse(folder.Substring(4), out version);
            }
            catch { return false; }
        }

        private static void ValidateEnvironment(EnvironmentPaths paths)
        {
            if (paths == null || paths.Adapter == null || paths.PrepareState == null || paths.OwnedByUser == null || paths.Locations == null ||
                paths.Locations.Length == 0 || paths.VerifyRecoveryRoot == null || String.IsNullOrEmpty(paths.Journal) || String.IsNullOrEmpty(paths.DiscordRoot) || String.IsNullOrEmpty(paths.Image) ||
                !Path.IsPathRooted(paths.Journal) || !Path.IsPathRooted(paths.DiscordRoot) || !Path.IsPathRooted(paths.Image))
                throw new ArgumentException("The shortcut environment is invalid.");
            if (!Safety.NoReparse(paths.Journal) || !Safety.NoReparse(paths.DiscordRoot) || !Safety.NoReparse(paths.Image))
                throw new InvalidDataException("The shortcut environment contains a redirected file path.");
        }

        private static void PrepareJournal(EnvironmentPaths paths)
        {
            paths.PrepareState();
            if (!Safety.NoReparse(paths.Journal)) throw new InvalidDataException("The shortcut record path is not safe.");
            Directory.CreateDirectory(paths.Journal);
            if (!Safety.NoReparse(paths.Journal)) throw new InvalidDataException("The shortcut record path changed.");
        }

        private static string JournalChild(EnvironmentPaths paths, string name)
        {
            if (Path.GetFileName(name) != name || String.IsNullOrEmpty(name)) throw new InvalidDataException("The shortcut record file is invalid.");
            string result = Path.Combine(paths.Journal, name);
            if (!Safety.Under(result, paths.Journal) || !Safety.NoReparse(result)) throw new InvalidDataException("The shortcut record file is not safe.");
            return result;
        }

        private static byte[] ReadBytes(string path, int limit)
        {
            using (var stream = Safety.ReadLease(path))
            {
                if (stream.Length < 1 || stream.Length > limit) throw new InvalidDataException("The shortcut file size is not within the expected limit.");
                var bytes = new byte[(int)stream.Length];
                int position = 0;
                while (position < bytes.Length)
                {
                    int n = stream.Read(bytes, position, bytes.Length - position);
                    if (n == 0) throw new EndOfStreamException();
                    position += n;
                }
                return bytes;
            }
        }

        private static string FileHash(string path) { return Safety.HashBytes(ReadBytes(path, MaxShortcutBytes)); }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaxRecordBytes, RecursionLimit = 12 }; }
        private static bool SamePath(string first, string second)
        {
            try { return !String.IsNullOrEmpty(first) && !String.IsNullOrEmpty(second) && String.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void WriteNew(string path, byte[] bytes)
        {
            if (!Safety.NoReparse(path)) throw new InvalidDataException("The shortcut write path is not safe.");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!SamePath(path, NativeFile.FinalPath(stream.SafeFileHandle))) throw new InvalidDataException("The shortcut write target changed.");
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
        }

        private static void CleanupByHash(string path, string hash)
        {
            try { if (File.Exists(path) && Safety.NoReparse(path) && FileHash(path) == hash) File.Delete(path); }
            catch { }
        }
        private static void DeletePlain(string path)
        {
            try { if (File.Exists(path) && Safety.NoReparse(path)) File.Delete(path); }
            catch { }
        }

        private static EnvironmentPaths CreateEnvironment() { return CreateEnvironment(Safety.DiscordRoot); }
        // Scanning old journals must not require today's installation selection
        // or even an intact default installation. RecordEnvironment substitutes
        // and validates each transaction's actual root before any link write.
        private static EnvironmentPaths CreateRecoveryEnvironment() { return CreateEnvironment(Safety.StateRoot); }

        private static Location[] CurrentUserLocations()
        {
            var roots = new List<Location>();
            foreach (var item in new[] {
                new Location(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
                new Location(Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
                new Location(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false) })
                if (!roots.Any(existing => SamePath(existing.Path, item.Path))) roots.Add(item);
            return roots.ToArray();
        }

        private static bool DiscoveryOwner(string path)
        {
            try
            {
                if (!Safety.NoReparse(path)) return false;
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    string owner = ((SecurityIdentifier)File.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier))).Value;
                    return owner == identity.User.Value || owner == "S-1-5-18" || owner == "S-1-5-32-544";
                }
            }
            catch { return false; }
        }

        private static EnvironmentPaths CreateEnvironment(string discordRoot)
        {
            string journal = Path.Combine(Safety.StateRoot, "v3", "shortcut-session");
            return new EnvironmentPaths {
                Journal = journal, DiscordRoot = discordRoot, Image = Path.Combine(Safety.Base, "DiscordAccess.exe"),
                LegacyDiscordRoot = Safety.DefaultDiscordRoot,
                VerifyRecoveryRoot = delegate(string root) { return DiscordInstallation.VerifyRecoveryRoot(root, null).Root; },
                Locations = CurrentUserLocations(), Adapter = new ShellAdapter(),
                OwnedByUser = delegate(string path) {
                    try
                    {
                        if (!Safety.NoReparse(path)) return false;
                        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                            return File.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier)).Equals(identity.User);
                    }
                    catch { return false; }
                },
                PrepareState = delegate {
                    Safety.EnsureStateDirectory();
                    if (Directory.Exists(journal))
                    {
                        if (!Safety.NoReparse(journal)) throw new InvalidDataException("The shortcut record is not safe.");
                        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        {
                            var acl = Directory.GetAccessControl(journal);
                            string[] allowed = { identity.User.Value, "S-1-5-18", "S-1-5-32-544" };
                            if (!allowed.Contains(((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))).Value) ||
                                acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                                    .Any(rule => rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value)))
                                throw new InvalidDataException("The permissions of the shortcut record are not limited to the expected users.");
                        }
                    }
                }
            };
        }

        private sealed class ShellAdapter : IAdapter
        {
            public Target Read(string path)
            {
                return Use(path, delegate(object shortcut) {
                    return new Target { Path = (string)Get(shortcut, "TargetPath"), Arguments = (string)Get(shortcut, "Arguments"), Icon = (string)Get(shortcut, "IconLocation") };
                });
            }
            public void Rewrite(string path, string image, string arguments, string workingDirectory, string icon)
            {
                Use<object>(path, delegate(object shortcut) {
                    Set(shortcut, "TargetPath", image); Set(shortcut, "Arguments", arguments); Set(shortcut, "WorkingDirectory", workingDirectory);
                    Set(shortcut, "IconLocation", icon);
                    shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
                    return null;
                });
            }
            public void VerifyDiscordImage(string path) { using (var image = new TrustedImage(path, true)) { } }
            private static T Use<T>(string path, Func<object, T> action)
            {
                object shell = null, shortcut = null;
                try
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
                    shell = Activator.CreateInstance(shellType);
                    shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    return action(shortcut);
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                }
            }
            private static object Get(object target, string property) { return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null); }
            private static void Set(object target, string property, string value) { target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new object[] { value }); }
        }
    }
}
