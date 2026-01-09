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
            // Commonly this is UTF-16LE (Unicode) text, but it can vary depending on how the credential was created.
            var blobBytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blobBytes, 0, blobBytes.Length);

            var candidates = new List<string>();

            // UTF-16LE (only if byte count is even)
            if (blobBytes.Length % 2 == 0)
                candidates.Add(Encoding.Unicode.GetString(blobBytes).TrimEnd('\0').Trim());

            // UTF-8
            candidates.Add(Encoding.UTF8.GetString(blobBytes).TrimEnd('\0').Trim());

            // ASCII
            candidates.Add(Encoding.ASCII.GetString(blobBytes).TrimEnd('\0').Trim());

            foreach (var c in candidates)
            {
                if (LooksLikeGitHubToken(c))
                    return c;
            }

            // As a last resort, return the first non-empty candidate.
            return candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static bool LooksLikeGitHubToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();

        // Common token prefixes
        if (token.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase)) return token.Length >= 20;
        if (token.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase)) return token.Length >= 20;

        // Generic fallback: long-ish, url-safe-ish
        if (token.Length < 20) return false;

        foreach (var ch in token)
        {
            var ok = char.IsLetterOrDigit(ch) || ch is '_' or '-' ;
            if (!ok) return false;
        }

        return true;
    }
}
