using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace GuliERP.Api;

public static class WindowsDatabaseCredentialSource
{
    public static string Resolve(string? baseConnection, string targetName)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Windows database credential source requires Windows.");
        if (string.IsNullOrWhiteSpace(baseConnection) || string.IsNullOrWhiteSpace(targetName) || targetName.Any(char.IsControl))
            throw new InvalidOperationException("Database credential reference or target connection is missing.");

        NpgsqlConnectionStringBuilder connection;
        try { connection = new NpgsqlConnectionStringBuilder(baseConnection); }
        catch (Exception) { throw new InvalidOperationException("Database target connection cannot be parsed."); }
        if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) ||
            string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("Database target connection is incomplete.");
        if (!string.IsNullOrWhiteSpace(connection.Password) && connection.Password != "CHANGE_ME")
            throw new InvalidOperationException("Database password must not also be present in configuration.");

        connection.Password = ReadPassword(targetName, connection.Username);
        return connection.ConnectionString;
    }

    private static string ReadPassword(string target, string expectedUser)
    {
        if (!CredRead(target, 1, 0, out var handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Database credential reference could not be read.");
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (!string.Equals(credential.UserName, expectedUser, StringComparison.Ordinal) ||
                credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("Database credential identity or size is invalid.");
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var password = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Database credential is empty.");
                return password;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);
}
