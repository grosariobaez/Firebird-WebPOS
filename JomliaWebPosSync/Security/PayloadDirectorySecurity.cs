using System.Security.AccessControl;
using System.Security.Principal;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Security;

public sealed class PayloadDirectorySecurity
{
    private static SecurityIdentifier[] AllowedIdentities()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return
        [
            identity.User ?? throw new SyncException("SERVICE_IDENTITY_UNAVAILABLE", "PayloadDirectory"),
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        ];
    }

    public void Prepare(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new SyncException("WINDOWS_REQUIRED", "PayloadDirectory");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new SyncException("LOCAL_DEDICATED_DIRECTORY_REQUIRED", "PayloadDirectory");
        path = Path.GetFullPath(path);
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),
            Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new SyncException("LOCAL_DEDICATED_DIRECTORY_REQUIRED", "PayloadDirectory");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new SyncException("NTFS_REQUIRED", "PayloadDirectory");
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new SyncException("REPARSE_POINT_REJECTED", "PayloadDirectory");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AllowedIdentities())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) directory.Create(security);
        else directory.SetAccessControl(security);
    }

    public void ProtectFile(string path)
    {
        var file = new FileInfo(path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new SyncException("REPARSE_POINT_REJECTED", "PayloadFile");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AllowedIdentities()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
    }
}
