using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

public class MachineProfileTests
{
    [Fact]
    public void Windows11IsRecognizedFromTheBuildNumber()
    {
        Assert.Equal("Windows 11 Pro 25H2 build 26200.9168", MachineProfile.OsLabel("Windows 10 Pro", "25H2", 26200, 9168));
        Assert.Equal("Windows 10 Home 22H2 build 19045.5000", MachineProfile.OsLabel("Windows 10 Home", "22H2", 19045, 5000));
        Assert.Equal("Windows", MachineProfile.OsLabel("Windows", "", 0, 0));
    }

    [Fact]
    public void DescribeIsOneNonEmptyLine()
    {
        var line = MachineProfile.Describe();
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain("\n", line);
        Assert.Contains("threads", line);
        Assert.Contains("tier", line);
    }
}
