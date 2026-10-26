using System.Text.Json.Serialization;
using SensorDeck.Core.Model;

namespace SensorDeck.Core.Calculated;

/// <summary>How a calculated sensor combines its inputs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CalcOperation>))]
public enum CalcOperation
{
    Sum,
    Average,
    Min,
    Max,

    /// <summary>First input minus all the others (e.g. GPU hot spot − GPU core).</summary>
    Difference,
}

/// <summary>
/// A sensor the app computes from others: <c>op(inputs) × Factor + Offset</c>, e.g. CPU + GPU power, hot spot − core,
/// average case-fan speed. Lives in settings; shows up everywhere a sensor can (id <c>calc:&lt;Id&gt;</c>).
/// </summary>
public sealed class CalculatedSensor
{
    public const string IdPrefix = "calc:";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "Calculated";

    public CalcOperation Operation { get; set; }

    /// <summary>Input sensor ids, in order (order matters for <see cref="CalcOperation.Difference"/>).</summary>
    public List<string> Inputs { get; set; } = [];

    public double Factor { get; set; } = 1;

    public double Offset { get; set; }

    /// <summary>Kind and unit of the result (taken from the first input when created).</summary>
    public SensorKind Kind { get; set; } = SensorKind.Other;

    public string Unit { get; set; } = "";

    /// <summary>The id dashboards, alerts and history use.</summary>
    [JsonIgnore]
    public string SensorId => IdPrefix + Id;

    public static bool IsCalculated(string sensorId) =>
        sensorId?.StartsWith(IdPrefix, StringComparison.Ordinal) == true;

    public CalculatedSensor Clone() => new()
    {
        Id = Id,
        Name = Name,
        Operation = Operation,
        Inputs = [.. Inputs],
        Factor = Factor,
        Offset = Offset,
        Kind = Kind,
        Unit = Unit,
    };

    /// <summary>
    /// The result for these input values (null/NaN = no reading). Sum/average/min/max use the inputs that have a
    /// reading; a difference needs all of them. Null when nothing can be computed.
    /// </summary>
    public static double? Evaluate(CalcOperation operation, IReadOnlyList<double?> values, double factor = 1, double offset = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        var present = values.Where(v => v is { } x && !double.IsNaN(x)).Select(v => v!.Value).ToList();
        double? result = operation switch
        {
            _ when values.Count == 0 => null,
            CalcOperation.Difference when present.Count != values.Count => null,
            CalcOperation.Difference => present[0] - present.Skip(1).Sum(),
            _ when present.Count == 0 => null,
            CalcOperation.Sum => present.Sum(),
            CalcOperation.Average => present.Average(),
            CalcOperation.Min => present.Min(),
            CalcOperation.Max => present.Max(),
            _ => null,
        };

        return result is { } r ? (r * factor) + offset : null;
    }

    public double? Evaluate(IReadOnlyList<double?> values) => Evaluate(Operation, values, Factor, Offset);
}
