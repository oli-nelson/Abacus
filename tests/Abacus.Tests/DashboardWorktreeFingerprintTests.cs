using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardWorktreeFingerprintTests
{
    [Fact]
    public void ParsesRenameOriginalWithoutTreatingFilenameAsAnotherRecord()
    {
        var paths = DashboardGit.ChangedWorktreePaths("2 R. N... 100644 100644 100644 abc def R100 new name\0" +
            "2 old name\0? next\tname\n.txt\0" + "1 .M N... 100644 100644 100644 abc def changed\0");
        Assert.Equal(new[] { "2 old name", "changed", "new name", "next\tname\n.txt" }, paths);
    }

    [Theory]
    [InlineData("unsupported\0")]
    [InlineData("2 R. N... 100644 100644 100644 abc def R100 new name\0")]
    [InlineData("1 missing fields\0")]
    public void MalformedStatusCannotSupplyAPartialFingerprint(string status) =>
        Assert.Throws<InvalidDataException>(() => DashboardGit.ChangedWorktreePaths(status));
}
