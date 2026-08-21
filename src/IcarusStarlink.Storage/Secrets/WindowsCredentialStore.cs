using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.Storage.Secrets;

/// <summary>
/// Wraps Windows' own Credential Manager (advapi32.dll's CredWrite/CredRead/CredDelete) via
/// P/Invoke — there's no BCL API for this, and the native API is old/stable enough (unchanged
/// since Windows XP) not to warrant a third-party NuGet dependency for something this narrow.
/// Stores under CRED_TYPE_GENERIC with LOCAL_MACHINE persistence (survives reboots, scoped to the
/// current Windows user account — matches how a password manager's own "remember me" behaves).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public void Save(string target, string secret)
    {
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);

            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "IcarusStarlink",
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"Windows Credential Manager write failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPtr))
        {
            // By far the most common case is ERROR_NOT_FOUND (nothing saved yet) — every failure
            // mode here means the same thing to a caller: there's no usable secret to return.
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void Delete(string target)
    {
        // Best-effort: "nothing was stored under this target" isn't a failure condition a caller
        // needs to know about — Delete is idempotent either way.
        CredDelete(target, CredTypeGeneric, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credentialPtr);
}
