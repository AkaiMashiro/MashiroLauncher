using MashiroLauncher.Core.Auth;

namespace MashiroLauncher.Core.Tests.Auth;

public class OfflineAccountTests
{
    [Fact]
    public void SameUsername_DeterministicUuid()
    {
        var a = OfflineAccount.ComputeOfflineUuid("Steve");
        var b = OfflineAccount.ComputeOfflineUuid("Steve");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentUsername_DifferentUuid()
    {
        var a = OfflineAccount.ComputeOfflineUuid("Steve");
        var b = OfflineAccount.ComputeOfflineUuid("Alex");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Uuid_IsVersion3()
    {
        var uuid = OfflineAccount.ComputeOfflineUuid("Steve");
        var bytes = uuid.ToByteArray();
        // .NET Guid stores first 3 fields little-endian. Variant byte is at offset 8.
        // For version, we need byte 7 in Java/RFC layout, which after Guid.ToByteArray() lives at index 7.
        // Reconstruct from string to avoid endian confusion.
        var hex = uuid.ToString("N");
        // Char at position 12..13 in the canonical hex form is the version nibble.
        Assert.Equal('3', hex[12]);
    }

    [Fact]
    public void Account_ExposesExpectedDefaults()
    {
        var account = new OfflineAccount("Steve");
        Assert.Equal("Steve", account.Username);
        Assert.Equal("0", account.AccessToken);
        Assert.Equal("legacy", account.UserType);
        Assert.Equal("0", account.Xuid);
        Assert.Equal(string.Empty, account.ClientId);
    }

    [Fact]
    public void Constructor_RejectsEmptyUsername()
    {
        Assert.Throws<ArgumentException>(() => new OfflineAccount(""));
        Assert.Throws<ArgumentException>(() => new OfflineAccount("   "));
    }
}
