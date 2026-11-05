using GuliERP.Api;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class WindowsDatabaseCredentialSourceFacts
{
    private const string Target = "Host=192.0.2.10;Port=5432;Database=gulierp_next_prod;Username=app_runtime";

    [Theory]
    [InlineData(null, "test-reference")]
    [InlineData("", "test-reference")]
    [InlineData(Target, "")]
    [InlineData(Target, "bad\nreference")]
    public void MissingOrInvalidReferenceFailsBeforeCredentialRead(string? connection, string reference)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsDatabaseCredentialSource.Resolve(connection, reference));
    }

    [Fact]
    public void PasswordInConfigurationFailsBeforeCredentialRead()
    {
        const string canary = "canary-secret";
        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsDatabaseCredentialSource.Resolve(Target + ";Password=" + canary, "test-reference"));
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteTargetFailsBeforeCredentialRead()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsDatabaseCredentialSource.Resolve("Host=192.0.2.10;Username=app_runtime", "test-reference"));
    }

    [Fact]
    public void MissingCredentialFailsWithoutPrintingTargetOrUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string reference = "GuliERP-R135-credential-that-does-not-exist";
        var error = Assert.ThrowsAny<Exception>(() =>
            WindowsDatabaseCredentialSource.Resolve(Target, reference));
        Assert.DoesNotContain(reference, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("app_runtime", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticCredentialResolvesAndIsDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reference = "GuliERP-R139-ephemeral-" + Guid.NewGuid().ToString("N");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var bytes = Encoding.Unicode.GetBytes(password);
        var target = Marshal.StringToCoTaskMemUni(reference);
        var user = Marshal.StringToCoTaskMemUni("app_runtime");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var created = false;
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1, TargetName = target, UserName = user,
                CredentialBlob = blob, CredentialBlobSize = bytes.Length, Persist = 2
            };
            Assert.True(CredWrite(ref credential, 0), "Could not create isolated test credential.");
            created = true;
            var resolved = new NpgsqlConnectionStringBuilder(
                WindowsDatabaseCredentialSource.Resolve(Target + ";Password=CHANGE_ME", reference));
            Assert.Equal(password, resolved.Password);
            Assert.Equal("gulierp_next_prod", resolved.Database);
            Assert.Equal("app_runtime", resolved.Username);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            if (created) Assert.True(CredDelete(reference, 1, 0), "Could not delete isolated test credential.");
        }
        Assert.ThrowsAny<Exception>(() => WindowsDatabaseCredentialSource.Resolve(Target, reference));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public int Flags, Type;
        public IntPtr TargetName, Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
}
