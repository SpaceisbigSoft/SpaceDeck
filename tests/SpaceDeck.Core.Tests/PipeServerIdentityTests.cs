using SpaceDeck.Core.Protocol;

namespace SpaceDeck.Core.Tests;

public class PipeServerIdentityTests
{
    private static readonly string[] Trusted = [@"C:\Program Files", @"C:\Program Files (x86)\", @"D:\dev\sd\src\SpaceDeck.Collector\bin\", ""];

    [Theory]
    [InlineData(@"C:\Program Files\SpaceDeck\Collector\SpaceDeck.Collector.exe")]
    [InlineData(@"c:\program files\spacedeck\collector\spacedeck.collector.exe")]
    [InlineData(@"D:\dev\sd\src\SpaceDeck.Collector\bin\Debug\net10.0-windows\SpaceDeck.Collector.exe")]
    public void TrustedCollectorPaths(string image)
    {
        Assert.True(PipeServerIdentity.IsTrustedCollectorPath(image, Trusted));
    }

    [Theory]
    [InlineData(@"C:\Users\Bob\Downloads\SpaceDeck.Collector.exe")] // not in a trusted folder
    [InlineData(@"C:\Program FilesX\SpaceDeck.Collector.exe")] // prefix of a folder name only
    [InlineData(@"C:\Program Files\SpaceDeck\Collector\Evil.exe")] // wrong program
    [InlineData(@"C:\Program Files\..\Users\Bob\SpaceDeck.Collector.exe")]
    [InlineData(@"\\server\share\SpaceDeck.Collector.exe")]
    [InlineData(@"D:\dev\sd\src\SpaceDeck.App\bin\SpaceDeck.Collector.exe")] // the source tree, but not the collector's output
    [InlineData("")]
    public void UntrustedCollectorPaths(string image)
    {
        Assert.False(PipeServerIdentity.IsTrustedCollectorPath(image, Trusted));
    }

    [Fact]
    public void DevelopmentFolder_OnlyFromTheSourceTreesBuildOutput()
    {
        Assert.Equal(@"D:\dev\sd\src\SpaceDeck.Collector\bin\",
            PipeServerIdentity.DevelopmentCollectorFolder(@"D:\dev\sd\src\SpaceDeck.App\bin\Debug\net10.0-windows\", "SpaceDeck.App"));
        Assert.Null(PipeServerIdentity.DevelopmentCollectorFolder(@"C:\Program Files\SpaceDeck\", "SpaceDeck.App"));
        Assert.Null(PipeServerIdentity.DevelopmentCollectorFolder(@"D:\dev\sd\src\SpaceDeck.Cli\bin\Debug\", "SpaceDeck.App"));
    }
}
