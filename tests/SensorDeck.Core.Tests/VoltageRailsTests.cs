using SensorDeck.Core.Formatting;
using SensorDeck.Core.Model;

namespace SensorDeck.Core.Tests;

public class VoltageRailsTests
{
    [Theory]
    [InlineData("+12V", 12.0)]
    [InlineData("12V", 12.0)]
    [InlineData("+5V", 5.0)]
    [InlineData("+3.3V", 3.3)]
    [InlineData("+3.3V Standby", 3.3)]
    [InlineData("+3.3VSB", 3.3)]
    [InlineData("-12 V", -12.0)]
    [InlineData("3VSB", 3.3)]
    [InlineData("5VSB", 5.0)]
    [InlineData("AVCC", 3.3)]
    [InlineData("AVCC3", 3.3)]
    [InlineData("AVSB", 3.3)]
    [InlineData("VSB", 3.3)]
    [InlineData("3VCC", 3.3)]
    [InlineData("VBAT", 3.0)]
    [InlineData("CMOS Battery", 3.0)]
    public void InferNominal_RecognizesStandardRails(string name, double expected)
    {
        Assert.Equal((double?)expected, VoltageRails.InferNominal(name));
    }

    [Theory]
    [InlineData("Vcore")]
    [InlineData("CPU Core")]
    [InlineData("Voltage #3")]
    [InlineData("VDDSOC")]
    [InlineData("1.35V")]
    [InlineData("DRAM")]
    [InlineData("")]
    [InlineData(null)]
    public void InferNominal_IgnoresNonRails(string? name)
    {
        Assert.Null(VoltageRails.InferNominal(name));
    }

    [Theory]
    [InlineData(12.10f, 12.0, RailStatus.Ok)]
    [InlineData(11.45f, 12.0, RailStatus.Ok)]        // -4.6%, inside ATX ±5%
    [InlineData(11.30f, 12.0, RailStatus.Warning)]   // -5.8%
    [InlineData(10.90f, 12.0, RailStatus.Critical)]  // -9.2%
    [InlineData(5.20f, 5.0, RailStatus.Ok)]          // +4%
    [InlineData(3.45f, 3.3, RailStatus.Ok)]          // +4.5%
    [InlineData(3.10f, 3.3, RailStatus.Warning)]     // -6.1%
    [InlineData(-11.0f, -12.0, RailStatus.Ok)]       // -12V allows ±10%
    [InlineData(3.05f, 3.0, RailStatus.Ok)]          // battery
    [InlineData(2.70f, 3.0, RailStatus.Warning)]
    [InlineData(2.40f, 3.0, RailStatus.Critical)]
    public void Evaluate_AppliesTolerances(float value, double nominal, RailStatus expected)
    {
        Assert.Equal(expected, VoltageRails.Evaluate(value, nominal));
    }

    [Fact]
    public void Evaluate_UnknownWithoutValueOrNominal()
    {
        Assert.Equal(RailStatus.Unknown, VoltageRails.Evaluate(null, 12.0));
        Assert.Equal(RailStatus.Unknown, VoltageRails.Evaluate(12.0f, null));
    }
}
