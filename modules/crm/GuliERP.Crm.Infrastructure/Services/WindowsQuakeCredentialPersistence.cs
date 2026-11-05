using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GuliERP.Crm.Infrastructure.Services;

public interface IQuakeCredentialPersistence
{
    IReadOnlyList<QuakeStoredCredential> Load(long tenantId, long companyId);
    void Write(long tenantId, long companyId, QuakeStoredCredential credential);
    void Delete(long tenantId, long companyId, int slot);
}

public sealed record QuakeStoredCredential(Guid Id, string Value, long Sequence, int Slot);

/// <summary>Stores each company-scoped key as a Windows Generic Credential under the API process account.</summary>
public sealed class WindowsQuakeCredentialPersistence(IConfiguration configuration) : IQuakeCredentialPersistence
{
    private const int GenericType = 1;
    private const int LocalMachinePersist = 2;
    private const int NotFound = 1168;
    private const int MaxSlots = 5;
    private readonly string _environmentId = EnvironmentId(configuration);

    private static string EnvironmentId(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("GuliERP")
            ?? configuration["GULIERP_ConnectionStrings:GuliERP"];
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Cannot scope Quake credentials without a database connection.");
        var target = new NpgsqlConnectionStringBuilder(connection);
        if (string.IsNullOrWhiteSpace(target.Host) || string.IsNullOrWhiteSpace(target.Database))
            throw new InvalidOperationException("Cannot scope Quake credentials without a database host and name.");
        var identity = $"{target.Host.ToLowerInvariant()}|{target.Port}|{target.Database.ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    public IReadOnlyList<QuakeStoredCredential> Load(long tenantId, long companyId)
    {
        EnsureWindows();
        var result = new List<QuakeStoredCredential>();
        for (var slot = 1; slot <= MaxSlots; slot++)
        {
            if (!CredRead(Target(tenantId, companyId, slot), GenericType, 0, out var ptr))
            {
                if (Marshal.GetLastWin32Error() == NotFound) continue;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read Quake credential.");
            }
            try
            {
                var native = Marshal.PtrToStructure<NativeCredential>(ptr);
                if (native.CredentialBlobSize <= 0 || native.CredentialBlobSize > 2560)
                    throw new InvalidOperationException("Stored Quake credential has invalid size.");
                var bytes = new byte[native.CredentialBlobSize];
                try
                {
                    Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
                    var item = JsonSerializer.Deserialize<QuakeStoredCredential>(bytes)
                        ?? throw new InvalidOperationException("Stored Quake credential is invalid.");
                    if (item.Slot != slot || item.Id == Guid.Empty || item.Sequence <= 0
                        || string.IsNullOrWhiteSpace(item.Value) || item.Value.Length > 512)
                        throw new InvalidOperationException("Stored Quake credential is invalid.");
                    result.Add(item);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CredFree(ptr); }
        }
        return result.OrderBy(x => x.Sequence).ToArray();
    }

    public void Write(long tenantId, long companyId, QuakeStoredCredential credential)
    {
        EnsureWindows();
        if (credential.Slot is < 1 or > MaxSlots) throw new ArgumentOutOfRangeException(nameof(credential));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credential);
        if (bytes.Length > 2560) throw new ArgumentException("Quake key is too long for Windows Credential Manager.");
        var target = IntPtr.Zero;
        var user = IntPtr.Zero;
        var blob = IntPtr.Zero;
        try
        {
            target = Marshal.StringToCoTaskMemUni(Target(tenantId, companyId, credential.Slot));
            user = Marshal.StringToCoTaskMemUni("gulierp-crm");
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = GenericType, TargetName = target, UserName = user,
                CredentialBlob = blob, CredentialBlobSize = bytes.Length,
                Persist = LocalMachinePersist
            };
            if (!CredWrite(ref native, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save Quake credential.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (blob != IntPtr.Zero)
            {
                for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
            if (target != IntPtr.Zero) Marshal.FreeCoTaskMem(target);
            if (user != IntPtr.Zero) Marshal.FreeCoTaskMem(user);
        }
    }

    public void Delete(long tenantId, long companyId, int slot)
    {
        EnsureWindows();
        if (slot is < 1 or > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        if (!CredDelete(Target(tenantId, companyId, slot), GenericType, 0)
            && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to delete Quake credential.");
    }

    private string Target(long tenantId, long companyId, int slot)
    {
        if (tenantId <= 0 || companyId <= 0) throw new ArgumentException("Tenant and company are required.");
        // v1 targets omitted the database and are deliberately not imported.
        return $"GuliERP-CRM-Quake-v2-{_environmentId}-{tenantId}-{companyId}-{slot}";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Quake credential persistence requires Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
