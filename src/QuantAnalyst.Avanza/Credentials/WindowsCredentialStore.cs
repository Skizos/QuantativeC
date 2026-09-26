using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using QuantAnalyst.Core;

namespace QuantAnalyst.Avanza.Credentials;

/// <summary>
/// Windows Credential Manager (generic credentials), the primary secret store (master plan §5). Two entries:
/// <list type="bullet">
/// <item><c>QuantAnalyst:Avanza</c>: user name = Avanza username, password = Avanza password</item>
/// <item><c>QuantAnalyst:Avanza:TOTP</c>: password = the Base32 TOTP secret</item>
/// </list>
/// Written by <c>qa secrets set</c> (or <c>cmdkey /generic:&lt;target&gt; /user:&lt;name&gt; /pass</c>, which prompts).
/// Blobs are UTF-16LE, as <c>cmdkey</c> stores them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore(string targetPrefix = WindowsCredentialStore.DefaultPrefix) : ISecretStore
{
    public const string DefaultPrefix = "QuantAnalyst:Avanza";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Name => "Windows Credential Manager";

    public string LoginTarget => targetPrefix;

    public string TotpTarget => targetPrefix + ":TOTP";

    public AvanzaCredentials GetAvanzaCredentials()
    {
        (string User, string Blob)? login = Read(LoginTarget);
        (string User, string Blob)? totp = Read(TotpTarget);
        var missing = new List<string>();
        if (login is null || login.Value.User.Length == 0 || login.Value.Blob.Length == 0)
        {
            missing.Add(LoginTarget);
        }

        if (totp is null || totp.Value.Blob.Length == 0)
        {
            missing.Add(TotpTarget);
        }

        return missing.Count > 0
            ? throw new SecretStoreException($"Missing in {Name}: {string.Join(", ", missing)}. Run 'qa secrets set'.")
            : new AvanzaCredentials(new Secret(login!.Value.User), new Secret(login.Value.Blob), new Secret(totp!.Value.Blob));
    }

    /// <summary>True when a generic credential with this target exists (the value is not returned).</summary>
    public static bool Exists(string target) => Read(target) is not null;

    /// <summary>The password of a generic credential as a <see cref="Secret"/>, or null when it does not exist or is empty.</summary>
    public static Secret? ReadSecret(string target) =>
        Read(target) is { Blob.Length: > 0 } c ? new Secret(c.Blob) : null;

    /// <summary>Creates or replaces a generic credential (local machine persistence).</summary>
    public static unsafe void Write(string target, string userName, Secret secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(secret);
        byte[] blob = Encoding.Unicode.GetBytes(secret.Reveal());
        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(target);
        IntPtr userPtr = Marshal.StringToCoTaskMemUni(userName);
        try
        {
            fixed (byte* blobPtr = blob)
            {
                var credential = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = targetPtr,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)blobPtr,
                    Persist = CredPersistLocalMachine,
                    UserName = userPtr,
                };
                if (!CredWrite(in credential, 0))
                {
                    throw new SecretStoreException($"CredWrite failed for '{target}' (Win32 error {Marshal.GetLastPInvokeError()}).");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
        }
    }

    /// <summary>Deletes a generic credential; returns false when it did not exist.</summary>
    public static bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        return error == ErrorNotFound
            ? false
            : throw new SecretStoreException($"CredDelete failed for '{target}' (Win32 error {error}).");
    }

    private static unsafe (string User, string Blob)? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out IntPtr ptr))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound
                ? null
                : throw new SecretStoreException($"CredRead failed for '{target}' (Win32 error {error}).");
        }

        try
        {
            NativeCredential* c = (NativeCredential*)ptr;
            string user = c->UserName == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(c->UserName) ?? string.Empty;
            string blob = c->CredentialBlobSize == 0
                ? string.Empty
                : Encoding.Unicode.GetString((byte*)c->CredentialBlob, (int)c->CredentialBlobSize);
            return (user, blob);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    // CREDENTIALW (wincred.h). Pointers as IntPtr keep the struct blittable for [LibraryImport].
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
