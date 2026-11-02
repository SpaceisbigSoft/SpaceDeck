using SpaceDeck.Core.Calculated;

namespace SpaceDeck.Core.Tests;

public class CalculatedSensorTests
{
    [Theory]
    [InlineData(CalcOperation.Sum, 60.0)]
    [InlineData(CalcOperation.Average, 20.0)]
    [InlineData(CalcOperation.Min, 10.0)]
    [InlineData(CalcOperation.Max, 30.0)]
    [InlineData(CalcOperation.Difference, -40.0)]
    public void Evaluate_Operations(CalcOperation op, double expected) =>
        Assert.Equal(expected, CalculatedSensor.Evaluate(op, [10.0, 20.0, 30.0]));

    [Fact]
    public void Evaluate_AppliesFactorAndOffset() =>
        Assert.Equal(25.0, CalculatedSensor.Evaluate(CalcOperation.Sum, [10.0, 2.0], factor: 2, offset: 1));

    [Fact]
    public void Evaluate_SkipsMissingInputs_ExceptForDifference()
    {
        Assert.Equal(15.0, CalculatedSensor.Evaluate(CalcOperation.Average, [10.0, null, 20.0, double.NaN]));
        Assert.Null(CalculatedSensor.Evaluate(CalcOperation.Difference, [10.0, null]));
        Assert.Null(CalculatedSensor.Evaluate(CalcOperation.Sum, [null, null]));
        Assert.Null(CalculatedSensor.Evaluate(CalcOperation.Sum, []));
    }

    [Fact]
    public void Ids_AreRecognized()
    {
        var calc = new CalculatedSensor();
        Assert.True(CalculatedSensor.IsCalculated(calc.SensorId));
        Assert.False(CalculatedSensor.IsCalculated("lhm:/cpu/0/temperature/0"));
        Assert.Equal(calc.Id, calc.Clone().Id);
    }
}
