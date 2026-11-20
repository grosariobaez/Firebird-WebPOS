using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JomliaWebPosSync.Security;

internal static class DurableFileRename
{
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);

    public static void MoveNew(string temporary, string destination)
    {
        // MOVEFILE_WRITE_THROUGH; deliberately omit REPLACE_EXISTING and COPY_ALLOWED.
        if (!MoveFileEx(temporary, destination, 0x00000008))
            throw new IOException("Durable payload rename failed", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public static void MoveReplace(string temporary, string destination)
    {
        // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
        if (!MoveFileEx(temporary, destination, 0x00000001 | 0x00000008))
            throw new IOException("Durable file replace failed", new Win32Exception(Marshal.GetLastWin32Error()));
    }
}
