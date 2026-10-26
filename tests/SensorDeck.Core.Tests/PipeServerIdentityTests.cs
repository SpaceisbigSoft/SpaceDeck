using SensorDeck.Core.Protocol;

namespace SensorDeck.Core.Tests;

public class PipeServerIdentityTests
{
    private static readonly string[] Trusted = [@"C:\Program Files", @"C:\Program Files (x86)\", @"D:\dev\sd\src\SensorDeck.Collector\bin\", ""];

    [Theory]
    [InlineData(@"C:\Program Files\SensorDeck\Collector\SensorDeck.Collector.exe")]
    [InlineData(@"c:\program files\sensordeck\collector\sensordeck.collector.exe")]
    [InlineData(@"D:\dev\sd\src\SensorDeck.Collector\bin\Debug\net10.0-windows\SensorDeck.Collector.exe")]
    public void TrustedCollectorPaths(string image)
    {
        Assert.True(PipeServerIdentity.IsTrustedCollectorPath(image, Trusted));
    }

    [Theory]
    [InlineData(@"C:\Users\Bob\Downloads\SensorDeck.Collector.exe")] // not in a trusted folder
    [InlineData(@"C:\Program FilesX\SensorDeck.Collector.exe")] // prefix of a folder name only
    [InlineData(@"C:\Program Files\SensorDeck\Collector\Evil.exe")] // wrong program
    [InlineData(@"C:\Program Files\..\Users\Bob\SensorDeck.Collector.exe")]
    [InlineData(@"\\server\share\SensorDeck.Collector.exe")]
    [InlineData(@"D:\dev\sd\src\SensorDeck.App\bin\SensorDeck.Collector.exe")] // the source tree, but not the collector's output
    [InlineData("")]
    public void UntrustedCollectorPaths(string image)
    {
        Assert.False(PipeServerIdentity.IsTrustedCollectorPath(image, Trusted));
    }

    [Fact]
    public void DevelopmentFolder_OnlyFromTheSourceTreesBuildOutput()
    {
        Assert.Equal(@"D:\dev\sd\src\SensorDeck.Collector\bin\",
            PipeServerIdentity.DevelopmentCollectorFolder(@"D:\dev\sd\src\SensorDeck.App\bin\Debug\net10.0-windows\", "SensorDeck.App"));
        Assert.Null(PipeServerIdentity.DevelopmentCollectorFolder(@"C:\Program Files\SensorDeck\", "SensorDeck.App"));
        Assert.Null(PipeServerIdentity.DevelopmentCollectorFolder(@"D:\dev\sd\src\SensorDeck.Cli\bin\Debug\", "SensorDeck.App"));
    }
}
