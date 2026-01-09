using System.Runtime.InteropServices;
using System.Text;

namespace AitApplicationDeployedVersions.Security;

public static class WindowsCredentialManager
{
    private const int CRED_TYPE_GENERIC = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out nint credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] nint buffer);

    public static string? TryReadSecret(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return null;

        if (!CredReadW(targetName, CRED_TYPE_GENERIC, 0, out var credPtr) || credPtr == nint.Zero)
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == nint.Zero || cred.CredentialBlobSize == 0)
                return null;

            // Generic credentials store secret in CredentialBlob as bytes.
            // Commonly this is UTF-16LE (Unicode) text.
            var blobBytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blobBytes, 0, blobBytes.Length);

            // Try UTF-16LE first.
            var unicode = Encoding.Unicode.GetString(blobBytes).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(unicode)) return unicode;

            // Fallback to UTF-8.
            var utf8 = Encoding.UTF8.GetString(blobBytes).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(utf8) ? null : utf8;
        }
        finally
        {
            CredFree(credPtr);
        }
    }
}
