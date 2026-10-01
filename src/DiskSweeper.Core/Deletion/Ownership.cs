using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskSweeper.Core.Deletion;

/// <summary>
/// The "take ownership" fix for leftovers owned by TrustedInstaller, SYSTEM or a deleted user account.
/// Makes you the owner of every item and replaces its permissions with full control for you and
/// Administrators, so a normal delete works afterwards. Does the same job as takeown + icacls, but
/// without their language-dependent prompts. Needs administrator rights.
/// </summary>
public static class Ownership
{
    private const string TakeOwnershipPrivilege = "SeTakeOwnershipPrivilege";

    /// <summary>Fixes ownership and permissions of <paramref name="path"/> and everything inside it.</summary>
    public static IReadOnlyList<FailedItem> Take(string path, CancellationToken cancellationToken = default)
    {
        if (!Elevation.IsElevated) throw new UnauthorizedAccessException("Taking ownership needs administrator rights.");

        Privileges.TryEnable(TakeOwnershipPrivilege);
        Privileges.TryEnable(Privileges.Backup); // So folders can be listed even before their permissions are fixed.

        var user = new SecurityIdentifier(Elevation.CurrentUserSid);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var failures = new List<FailedItem>();

        // Fix each folder before listing it, so its contents become reachable.
        var attributes = FileSystemWalk.GetAttributes(path);
        if (attributes is null) return failures;

        bool isDirectory = (attributes.Value & FileAttributes.Directory) != 0;
        Fix(path, isDirectory, user, admins, failures);
        if (!isDirectory || ((attributes.Value & FileAttributes.ReparsePoint) != 0 && FileSystemWalk.IsLink(path))) return failures;

        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = stack.Pop();
            List<FileSystemWalk.Entry> entries;
            try
            {
                entries = FileSystemWalk.List(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new FailedItem(folder, BlockReason.AccessDenied, ex.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                Fix(entry.Path, entry.IsDirectory, user, admins, failures);
                if (entry.IsDirectory && !entry.IsLink) stack.Push(entry.Path);
            }
        }
        return failures;
    }

    private static void Fix(string path, bool isDirectory, SecurityIdentifier user, SecurityIdentifier admins, List<FailedItem> failures)
    {
        string extended = LongPath.ToExtended(path);
        try
        {
            // Two separate writes: first become the owner (allowed by the take-ownership privilege),
            // then, as owner, replace the permissions.
            if (isDirectory)
            {
                var owner = new DirectorySecurity();
                owner.SetOwner(user);
                new DirectoryInfo(extended).SetAccessControl(owner);

                var access = new DirectorySecurity();
                access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                access.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(extended).SetAccessControl(access);
            }
            else
            {
                var owner = new FileSecurity();
                owner.SetOwner(user);
                new FileInfo(extended).SetAccessControl(owner);

                var access = new FileSecurity();
                access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                access.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(extended).SetAccessControl(access);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
        {
            failures.Add(new FailedItem(path, BlockReason.AccessDenied, ex.Message));
        }
    }
}
