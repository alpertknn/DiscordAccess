using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DiscordAccess
{
    // This is a read-only boundary for a persistent executable path. Ancestor
    // sibling creation cannot replace an existing protected path component,
    // but new files beside the executable can influence its startup behavior.
    internal static class StartupPathTrust
    {
        private const string SystemSid = "S-1-5-18";
        private const string AdministratorsSid = "S-1-5-32-544";
        private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        private const string EveryoneSid = "S-1-1-0";
        private const string OwnerRightsSid = "S-1-3-4";
        private const uint GenericAll = 0x10000000, GenericWrite = 0x40000000;
        // Content/EA/attribute writes, DELETE, WRITE_DAC and WRITE_OWNER.
        private const uint FileMutation = 0x000d0116;
        // Ancestor content bits mean creation of siblings, not replacement of
        // an existing child. DELETE_CHILD is checked independently on parents.
        private const uint DirectoryMutation = 0x000d0150;
        private const uint DirectoryCreation = 0x00000006;

        internal static void Verify(string executable)
        {
            try
            {
                string path = LocalPath(executable);
                SecurityIdentifier user;
                using (var identity = WindowsIdentity.GetCurrent()) user = identity.User;
                if (user == null) throw Rejected();
                using (var lease = Safety.ReadLease(path))
                {
                    VerifyChain(path, user, ReadDescriptor, Safety.NoReparse);
                    if (!Safety.NoReparse(path) || !String.Equals(path, NativeFile.FinalPath(lease.SafeFileHandle),
                        StringComparison.OrdinalIgnoreCase)) throw Rejected();
                }
            }
            catch (UserActionException) { throw; }
            catch { throw Rejected(); }
        }

        // Installation must prove the directory before creating any children.
        // Existing ACLs are observed, never repaired or relaxed.
        internal static void VerifyDirectory(string directory)
        {
            try
            {
                string path = LocalPath(Path.Combine(directory, "DiscordAccess.exe"));
                using (var identity = WindowsIdentity.GetCurrent())
                    VerifyChain(Path.GetDirectoryName(path), identity.User, ReadDescriptor, Safety.NoReparse, true);
            }
            catch (UserActionException) { throw; }
            catch { throw Rejected(); }
        }

        private static string LocalPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 32760) throw Rejected();
            foreach (char character in value)
                if (Char.IsControl(character) || character == '/' || character == '"' || character == '*' ||
                    character == '?' || character == '<' || character == '>' || character == '|') throw Rejected();
            if (value.Length < 4 || !Char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' ||
                value.IndexOf(':', 2) >= 0) throw Rejected();
            string full = Path.GetFullPath(value);
            if (String.IsNullOrEmpty(Path.GetFileName(full))) throw Rejected();
            return full;
        }

        private static RawSecurityDescriptor ReadDescriptor(string path, bool directory)
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            byte[] bytes = directory ? Directory.GetAccessControl(path, sections).GetSecurityDescriptorBinaryForm() :
                File.GetAccessControl(path, sections).GetSecurityDescriptorBinaryForm();
            return new RawSecurityDescriptor(bytes, 0);
        }

        private static void VerifyChain(string path, SecurityIdentifier user,
            Func<string, bool, RawSecurityDescriptor> read, Func<string, bool> ordinaryPath, bool directoryRoot = false)
        {
            int depth = directoryRoot ? 1 : 0;
            for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                bool directory = depth != 0;
                if (!ordinaryPath(current) || !DescriptorTrusted(read(current, directory), directory, user, depth == 1)) throw Rejected();
                depth++;
            }
        }

        private static bool Trusted(SecurityIdentifier sid, SecurityIdentifier user)
        {
            if (sid == null || user == null) return false;
            string value = sid.Value;
            return value == user.Value || value == SystemSid || value == AdministratorsSid || value == TrustedInstallerSid;
        }

        private static uint Expanded(uint mask)
        {
            if ((mask & GenericAll) != 0) mask |= 0x001f01ff;
            if ((mask & GenericWrite) != 0) mask |= 0x00120116;
            return mask;
        }

        private static bool DescriptorTrusted(RawSecurityDescriptor security, bool directory, SecurityIdentifier user,
            bool executableDirectory = false)
        {
            if (security == null || !Trusted(security.Owner, user) ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || security.DiscretionaryAcl == null) return false;
            uint mutation = directory ? DirectoryMutation : FileMutation;
            if (directory && executableDirectory) mutation |= DirectoryCreation;
            // Only unconditional, preceding denies for the same trustee or
            // Everyone are conclusive without resolving arbitrary group claims.
            // Other overlapping groups are conservatively rejected, never used
            // as evidence that a foreign grant is harmless.
            var denies = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (GenericAce entry in security.DiscretionaryAcl)
            {
                if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
                var qualified = entry as QualifiedAce;
                if (qualified == null || qualified.SecurityIdentifier == null ||
                    (qualified.AceQualifier != AceQualifier.AccessAllowed && qualified.AceQualifier != AceQualifier.AccessDenied)) return false;
                string sid = qualified.SecurityIdentifier.Value;
                uint rights = Expanded(unchecked((uint)qualified.AccessMask)) & mutation;
                if (qualified.AceQualifier == AceQualifier.AccessDenied)
                {
                    if (entry is CommonAce && !qualified.IsCallback)
                    {
                        uint previous; denies.TryGetValue(sid, out previous); denies[sid] = previous | rights;
                    }
                    continue;
                }
                if (rights == 0 || Trusted(qualified.SecurityIdentifier, user) || sid == OwnerRightsSid) continue;
                uint blocked, everyone;
                denies.TryGetValue(sid, out blocked); denies.TryGetValue(EveryoneSid, out everyone);
                if ((rights & ~(blocked | everyone)) != 0) return false;
            }
            return true;
        }

        // The same owner/effective-rights policy is also used for private
        // executable data opened by handle. Startup chain behavior is unchanged.
        internal static bool DescriptorTrustedFile(RawSecurityDescriptor security, SecurityIdentifier user)
        { return DescriptorTrusted(security, false, user); }

        private static UserActionException Rejected()
        {
            return new UserActionException("Bu klasör otomatik başlangıç için güvenli doğrulanamadı. DiscordAccess'i elle açarak kişisel kurulumu hazırlayın. Kullanıcı klasörünüz de güvenli doğrulanamıyorsa yalnız sizin yazabildiğiniz bir klasör kullanın.");
        }

#if TEST_BUILD
        internal static bool DescriptorTrustedForChecks(RawSecurityDescriptor security, bool directory, SecurityIdentifier user,
            bool executableDirectory = false)
        { return DescriptorTrusted(security, directory, user, executableDirectory); }
        internal static void VerifyChainForChecks(string executable, SecurityIdentifier user,
            Func<string, bool, RawSecurityDescriptor> read, Func<string, bool> ordinaryPath)
        { VerifyChain(LocalPath(executable), user, read, ordinaryPath); }
#endif
    }
}
