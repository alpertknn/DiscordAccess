#if TEST_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DiscordAccess
{
    // Inert file snapshots only; never launches a process or edits the user's
    // real installation, ACL, startup registry, preference or Discord profile.
    internal static class StartupPackageChecks
    {
        internal static int Run(string packageRoot)
        {
            int count = 0;
            Action<bool, string> assert = (ok, message) => { count++; if (!ok) throw new InvalidOperationException(message); };
            Action<Action, string> reject = (action, message) => {
                bool failed = false; try { action(); } catch (IOException) { failed = true; }
                catch (UserActionException) { failed = true; }
                catch (PackageLoadException) { failed = true; }
                assert(failed, message);
            };
            assert(StartupPackage.ShouldPrepareForChecks(true, false, false, false), "Manual tray launch did not prepare a personal package.");
            foreach (bool[] flags in new[] { new[] { false,false,false,false }, new[] { true,true,false,false },
                new[] { true,false,true,false }, new[] { true,false,false,true } })
                assert(!StartupPackage.ShouldPrepareForChecks(flags[0],flags[1],flags[2],flags[3]), "Startup/restore/icon/console unexpectedly installed a package.");
            string work = Path.Combine(packageRoot, "work"), fixture = Path.Combine(work, "startup-package-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(fixture, "source"), profile = Path.Combine(fixture, "kişisel klasör");
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                foreach (var sid in new[] { identity.User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            try
            {
                Directory.CreateDirectory(Path.Combine(source, "engine"));
                Directory.CreateDirectory(profile, acl);
                File.WriteAllText(Path.Combine(source, "DiscordAccess.exe"), "INERT_APPLICATION");
                File.WriteAllText(Path.Combine(source, "engine", "sing-box.exe"), "INERT_PINNED_ENGINE");
                File.WriteAllText(Path.Combine(source, "LISANSLAR.txt"), "INERT_LICENSE");
                File.WriteAllText(Path.Combine(source, "unapproved.json"), "PRIVATE_CONFIGURATION_CANARY");
                string hash;
                using (var input = Safety.ReadLease(Path.Combine(source,"engine","sing-box.exe"))) hash = Safety.Hash(input);
                var visited = new List<string>();
                Action<string> directory = path => {
                    assert(Safety.Under(path, profile) || path == profile, "Install path escaped the personal root.");
                    assert(Directory.Exists(path) && Safety.NoReparse(path), "Install directory unverified.");
                    visited.Add(path);
                };
                int imageChecks = 0;
                Action<string> image = path => { imageChecks++; using (PrivateFileTrust.ReadLease(path)) { } };
                string target = StartupPackage.PrepareForChecks(source, profile, hash, directory, image);
                assert(visited[0] == profile && imageChecks == 1, "Profile was not proved before installation.");
                assert(File.ReadAllText(target) == "INERT_APPLICATION", "Application snapshot changed bytes.");
                assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(target),"engine","sing-box.exe")) == "INERT_PINNED_ENGINE", "Engine snapshot changed bytes.");
                assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(target),"LISANSLAR.txt")) == "INERT_LICENSE", "License was omitted.");
                assert(!File.Exists(Path.Combine(Path.GetDirectoryName(target),"unapproved.json")), "Private/unapproved package file was copied.");
                byte[] before = File.GetAccessControl(target).GetSecurityDescriptorBinaryForm();
                assert(StartupPackage.PrepareForChecks(source, profile, hash, directory, image) == target, "Repeated launch produced a second installation.");
                assert(before.SequenceEqual(File.GetAccessControl(target).GetSecurityDescriptorBinaryForm()), "Repeated install changed existing ACLs.");
                File.WriteAllText(target, "FOREIGN_IMAGE_CANARY");
                reject(() => StartupPackage.PrepareForChecks(source, profile, hash, directory, image), "Different existing executable was overwritten.");
                assert(File.ReadAllText(target) == "FOREIGN_IMAGE_CANARY", "Conflicting executable was not preserved.");
                int verified = imageChecks;
                reject(() => StartupPackage.PrepareForChecks(source, profile, new string('0',64), directory, image), "Wrong engine pin was accepted.");
                assert(imageChecks == verified, "Wrong engine reached launch verification.");
                string deniedProfile = Path.Combine(fixture, "denied"); Directory.CreateDirectory(deniedProfile);
                reject(() => StartupPackage.PrepareForChecks(source, deniedProfile, hash,
                    path => { throw new UserActionException("Synthetic untrusted profile."); }, image), "Unsafe profile was accepted.");
                assert(Directory.GetFileSystemEntries(deniedProfile).Length == 0, "Unsafe profile was mutated.");
                File.WriteAllText(Path.Combine(source,"DiscordAccess.exe"), "INERT_NEW_VERSION");
                string next = StartupPackage.PrepareForChecks(source,profile,hash,directory,image);
                assert(next != target && File.ReadAllText(target) == "FOREIGN_IMAGE_CANARY", "New release changed a previous package.");
                string quoted = NativeProcess.Quote("D:\\kişisel klasör\\Discord\\");
                assert(quoted.StartsWith("\"") && quoted.EndsWith("\\\\\""), "Relaunch argument with trailing slash was not escaped.");
                return count;
            }
            finally { if (Safety.Under(fixture,work) && Safety.NoReparse(fixture)) Directory.Delete(fixture,true); }
        }
    }
}
#endif
