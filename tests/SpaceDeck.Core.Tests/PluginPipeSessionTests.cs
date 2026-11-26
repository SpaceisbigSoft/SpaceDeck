using System.Text;
using SpaceDeck.Core.PluginPipe;

namespace SpaceDeck.Core.Tests;

public class PluginPipeSessionTests
{
    private const string Hello = """{"t":"hello","v":1,"plugin":"Lian Li Uni (Lighting)","version":"2.0.7","instance":"9f1c"}""";

    private const string Catalog = """
        {"t":"catalog",
         "hardware":[{"id":"LianLi/0","name":"Lian Li Galahad II LCD","kind":"cooler"},
                     {"id":"LianLi/wa1b2c3d4e5f6","name":"Lian Li TL Wireless (3 fans)","kind":"fans"}],
         "sensors":[{"id":"LianLi/0/coolant/temp","hardware":"LianLi/0","name":"Coolant","kind":"temperature"},
                    {"id":"LianLi/0/ch1/fan","hardware":"LianLi/0","name":"Pump","kind":"fan"},
                    {"id":"LianLi/wa1b2c3d4e5f6/f0/control","hardware":"LianLi/wa1b2c3d4e5f6","name":"Fan 1 duty","kind":"control"}]}
        """;

    private static PluginLineResult Send(PluginPipeSession session, string line) =>
        session.Accept(Encoding.UTF8.GetBytes(line.ReplaceLineEndings("")));

    private static void AssertValues(PluginValues values, params double?[] expected) => Assert.Equal(expected, values.Values);

    private static PluginPipeSession Connected()
    {
        var session = new PluginPipeSession();
        Assert.False(Send(session, Hello).IsError);
        Assert.False(Send(session, Catalog).IsError);
        return session;
    }

    private static string CatalogWith(string hardware, string sensors) =>
        $$"""{"t":"catalog","hardware":[{{hardware}}],"sensors":[{{sensors}}]}""";

    private const string OneHardware = """{"id":"h","name":"Cooler","kind":"cooler"}""";

    private static string Sensor(string id = "s", string hardware = "h", string name = "Coolant", string kind = "temperature") =>
        $$"""{"id":"{{id}}","hardware":"{{hardware}}","name":"{{name}}","kind":"{{kind}}"}""";

    private static void AssertRejected(string catalogLine)
    {
        var session = new PluginPipeSession();
        Assert.False(Send(session, Hello).IsError);
        var result = Send(session, catalogLine);
        Assert.True(result.IsError, $"expected a rejection for {catalogLine}");
        Assert.Null(result.Message);
        Assert.Null(session.Catalog);
    }

    [Fact]
    public void Hello_IsRead()
    {
        var session = new PluginPipeSession();
        var hello = Assert.IsType<PluginHello>(Send(session, Hello).Message);
        Assert.Equal(1, hello.Version);
        Assert.Equal("Lian Li Uni (Lighting)", hello.Plugin);
        Assert.Equal("2.0.7", hello.PluginVersion);
        Assert.Equal("9f1c", hello.Instance);
        Assert.Same(hello, session.Hello);
    }

    [Fact]
    public void Hello_FieldsAreCleanedForLogs()
    {
        var session = new PluginPipeSession();
        var hello = Assert.IsType<PluginHello>(Send(session, "{\"t\":\"hello\",\"v\":1,\"plugin\":\"evil\\r\\nFAKE LOG LINE" + new string('x', 100) + "\"}").Message);
        Assert.DoesNotContain("\n", hello.Plugin, StringComparison.Ordinal);
        Assert.Equal(64, hello.Plugin.Length);
        Assert.Equal("", hello.PluginVersion);
    }

    [Theory]
    [InlineData("""{"t":"hello","v":2}""")]
    [InlineData("""{"t":"hello","v":0}""")]
    [InlineData("""{"t":"hello","v":"1"}""")]
    [InlineData("""{"t":"hello","v":1.5}""")]
    [InlineData("""{"t":"hello"}""")]
    public void Hello_WrongOrMissingVersion_Disconnects(string line)
    {
        Assert.True(Send(new PluginPipeSession(), line).IsError);
    }

    [Fact]
    public void ValidCatalog_IsRead()
    {
        var session = Connected();
        var catalog = session.Catalog!;

        Assert.Equal(2, catalog.Hardware.Count);
        Assert.Equal(new PluginHardware("LianLi/0", "Lian Li Galahad II LCD", PluginHardwareKind.Cooler), catalog.Hardware[0]);
        Assert.Equal(PluginHardwareKind.Fans, catalog.Hardware[1].Kind);
        Assert.Equal(3, catalog.Sensors.Count);
        Assert.Equal(new PluginSensor("LianLi/0/coolant/temp", "LianLi/0", "Coolant", PluginSensorKind.Temperature), catalog.Sensors[0]);
        Assert.Equal(PluginSensorKind.Fan, catalog.Sensors[1].Kind);
        Assert.Equal(PluginSensorKind.Control, catalog.Sensors[2].Kind);
    }

    [Fact]
    public void EmptyCatalog_IsValid()
    {
        var session = new PluginPipeSession();
        Send(session, Hello);
        var catalog = Assert.IsType<PluginCatalog>(Send(session, """{"t":"catalog","hardware":[],"sensors":[]}""").Message);
        Assert.Empty(catalog.Sensors);
        Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[]}""").Message);
    }

    [Fact]
    public void UnknownHardwareKind_BecomesOther()
    {
        var session = new PluginPipeSession();
        Send(session, Hello);
        var catalog = Assert.IsType<PluginCatalog>(Send(session, CatalogWith(
            """{"id":"a","name":"A","kind":"pump-hub"},{"id":"b","name":"B"},{"id":"c","name":"C","kind":7}""", "")).Message);
        Assert.All(catalog.Hardware, h => Assert.Equal(PluginHardwareKind.Other, h.Kind));
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        var session = new PluginPipeSession();
        Assert.False(Send(session, """{"t":"hello","v":1,"future":{"x":[1,2]}}""").IsError);
        var catalog = Assert.IsType<PluginCatalog>(Send(session,
            """{"t":"catalog","extra":true,"hardware":[{"id":"h","name":"Cooler","kind":"cooler","colour":"red"}],"sensors":[{"id":"s","hardware":"h","name":"T","kind":"temperature","min":0}]}""").Message);
        Assert.Single(catalog.Sensors);
        Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[30],"seq":5}""").Message);
    }

    [Theory]
    [InlineData("""{"t":"lighting","mode":"rainbow"}""")]
    [InlineData("""{"t":"Values","values":[1,2]}""")] // types are case-sensitive: an unknown type, not "values"
    [InlineData("")]
    [InlineData("   \r")]
    public void UnknownTypeOrBlankLine_IsIgnored(string line)
    {
        var session = Connected();
        var result = Send(session, line);
        Assert.False(result.IsError);
        Assert.Null(result.Message);
    }

    [Fact]
    public void UnknownType_BeforeHello_IsIgnored()
    {
        var result = Send(new PluginPipeSession(), """{"t":"ping"}""");
        Assert.False(result.IsError);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"hello\"")]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"t":5}""")]
    [InlineData("""{"t":"hello","v":1""")]
    public void Garbage_Disconnects(string line)
    {
        Assert.True(Send(new PluginPipeSession(), line).IsError);
    }

    [Fact]
    public void CatalogOrValuesBeforeHello_Disconnects()
    {
        Assert.True(Send(new PluginPipeSession(), Catalog).IsError);
        Assert.True(Send(new PluginPipeSession(), """{"t":"values","values":[]}""").IsError);
    }

    [Fact]
    public void ValuesBeforeCatalog_Disconnects()
    {
        var session = new PluginPipeSession();
        Send(session, Hello);
        Assert.True(Send(session, """{"t":"values","values":[]}""").IsError);
    }

    [Fact]
    public void LineOver64KiB_Disconnects()
    {
        var session = new PluginPipeSession();
        Send(session, Hello);
        var padding = new string(' ', PluginPipeProtocol.MaxLineBytes);
        Assert.True(session.Accept(Encoding.UTF8.GetBytes("""{"t":"values","values":[]}""" + padding)).IsError);
    }

    [Fact]
    public void LineOfExactly64KiB_IsAccepted()
    {
        var session = new PluginPipeSession();
        var line = """{"t":"ping"}""";
        var bytes = Encoding.UTF8.GetBytes(line + new string(' ', PluginPipeProtocol.MaxLineBytes - line.Length));
        Assert.Equal(PluginPipeProtocol.MaxLineBytes, bytes.Length);
        Assert.False(session.Accept(bytes).IsError);
    }

    [Fact]
    public void TooDeeplyNested_Disconnects()
    {
        var session = new PluginPipeSession();
        Assert.True(Send(session, """{"t":"x","a":[[[[[[[[[[[[1]]]]]]]]]]]]}""").IsError);
    }

    [Fact]
    public void InvalidUtf8_Disconnects()
    {
        var session = new PluginPipeSession();
        Send(session, Hello);
        byte[] bytes = [.. Encoding.UTF8.GetBytes("""{"t":"catalog","hardware":[{"id":"h","name":"""), (byte)'"', 0xFF, 0xFE, (byte)'"',
            .. Encoding.UTF8.GetBytes(""","kind":"cooler"}],"sensors":[]}""")];
        Assert.True(session.Accept(bytes).IsError);
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("has space")]
    [InlineData("bad#char")]
    [InlineData("ümlaut")]
    [InlineData("back\\\\slash")]
    public void InvalidSensorId_Disconnects(string id)
    {
        AssertRejected(CatalogWith(OneHardware, Sensor(id: id)));
    }

    [Fact]
    public void IdLength_IsLimitedTo128()
    {
        var session = new PluginPipeSession();
        Send(session, Hello);
        Assert.False(Send(session, CatalogWith(OneHardware, Sensor(id: new string('a', 128)))).IsError);
        AssertRejected(CatalogWith(OneHardware, Sensor(id: new string('a', 129))));
    }

    [Fact]
    public void IdCharset_AllowsTheDocumentedCharacters()
    {
        Assert.True(PluginPipeSession.IsValidId("AZaz09/._:-"));
        Assert.False(PluginPipeSession.IsValidId("a b"));
        Assert.False(PluginPipeSession.IsValidId("a\u0000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\\nbreak")]
    [InlineData("Tab\\there")]
    [InlineData("Bidi\\u202Eoverride")]
    [InlineData("Zero\\u200Bwidth")]
    [InlineData("Lone \\uD800 surrogate")]
    public void InvalidName_Disconnects(string name)
    {
        AssertRejected(CatalogWith(OneHardware, Sensor(name: name)));
    }

    [Fact]
    public void NameLength_IsLimitedTo64Characters()
    {
        Assert.True(PluginPipeSession.IsValidName(new string('n', 64)));
        Assert.False(PluginPipeSession.IsValidName(new string('n', 65)));

        // Characters, not UTF-16 units: 64 emoji (128 units) fit.
        Assert.True(PluginPipeSession.IsValidName(string.Concat(Enumerable.Repeat("\U0001F321", 64))));
        Assert.True(PluginPipeSession.IsValidName("Kühlmittel °C"));
    }

    [Fact]
    public void MissingName_Disconnects()
    {
        AssertRejected(CatalogWith("""{"id":"h","kind":"cooler"}""", ""));
        AssertRejected(CatalogWith(OneHardware, """{"id":"s","hardware":"h","name":5,"kind":"fan"}"""));
    }

    [Fact]
    public void SensorWithUnknownHardware_Disconnects()
    {
        AssertRejected(CatalogWith(OneHardware, Sensor(hardware: "other")));
        AssertRejected(CatalogWith(OneHardware, """{"id":"s","name":"T","kind":"fan"}"""));
    }

    [Fact]
    public void DuplicateIds_Disconnect()
    {
        AssertRejected(CatalogWith(OneHardware + "," + OneHardware, ""));
        AssertRejected(CatalogWith(OneHardware, Sensor() + "," + Sensor(kind: "fan")));
    }

    [Theory]
    [InlineData("voltage")]
    [InlineData("Temperature")]
    [InlineData("")]
    public void UnknownSensorKind_Disconnects(string kind)
    {
        AssertRejected(CatalogWith(OneHardware, Sensor(kind: kind)));
    }

    [Fact]
    public void MissingLists_Disconnect()
    {
        AssertRejected("""{"t":"catalog","sensors":[]}""");
        AssertRejected("""{"t":"catalog","hardware":[]}""");
        AssertRejected("""{"t":"catalog","hardware":{},"sensors":[]}""");
        AssertRejected("""{"t":"catalog","hardware":[1],"sensors":[]}""");
        AssertRejected(CatalogWith(OneHardware, "\"s\""));
    }

    [Fact]
    public void HardwareCount_IsLimitedTo32()
    {
        static string Hardware(int count) =>
            string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"id":"h{{i}}","name":"H{{i}}","kind":"fans"}"""));

        var session = new PluginPipeSession();
        Send(session, Hello);
        Assert.False(Send(session, CatalogWith(Hardware(32), "")).IsError);
        AssertRejected(CatalogWith(Hardware(33), ""));
    }

    [Fact]
    public void SensorCount_IsLimitedTo512()
    {
        static string Sensors(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => Sensor(id: $"s{i}", kind: "fan")));

        var session = new PluginPipeSession();
        Send(session, Hello);
        Assert.False(Send(session, CatalogWith(OneHardware, Sensors(512))).IsError);
        AssertRejected(CatalogWith(OneHardware, Sensors(513)));
    }

    [Fact]
    public void InvalidCatalog_KeepsThePreviousOne()
    {
        var session = Connected();
        var before = session.Catalog;
        Assert.True(Send(session, CatalogWith(OneHardware, Sensor(kind: "voltage"))).IsError);
        Assert.Same(before, session.Catalog);
    }

    [Fact]
    public void Values_AreRead()
    {
        var session = Connected();
        var values = Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[21.6,2910,null]}""").Message);
        AssertValues(values, 21.6, 2910, null);
    }

    [Fact]
    public void OutOfRangeValues_BecomeNull()
    {
        var session = Connected();

        // temperature −50…150, fan 0…20000, control 0…100: the edges are in range.
        var edges = Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[-50,20000,100]}""").Message);
        AssertValues(edges, -50, 20000, 100);
        var low = Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[150,0,0]}""").Message);
        AssertValues(low, 150, 0, 0);

        var odd = Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[-50.1,20001,100.5]}""").Message);
        AssertValues(odd, null, null, null);
        var negative = Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[151,-1,-0.1]}""").Message);
        AssertValues(negative, null, null, null);
    }

    [Theory]
    [InlineData("""{"t":"values","values":[1,2]}""")]
    [InlineData("""{"t":"values","values":[1,2,3,4]}""")]
    [InlineData("""{"t":"values","values":[]}""")]
    public void ValuesCountMismatch_Disconnects(string line)
    {
        Assert.True(Send(Connected(), line).IsError);
    }

    [Theory]
    [InlineData("""{"t":"values","values":[1,"2",3]}""")]
    [InlineData("""{"t":"values","values":[1,true,3]}""")]
    [InlineData("""{"t":"values","values":[1,[2],3]}""")]
    [InlineData("""{"t":"values","values":[1,1e400,3]}""")]
    [InlineData("""{"t":"values","values":[1,NaN,3]}""")]
    [InlineData("""{"t":"values"}""")]
    [InlineData("""{"t":"values","values":{}}""")]
    public void NonFiniteOrNonNumericValues_Disconnect(string line)
    {
        Assert.True(Send(Connected(), line).IsError);
    }

    [Fact]
    public void NewCatalog_ChangesWhatValuesAreCheckedAgainst()
    {
        var session = Connected();
        Assert.False(Send(session, CatalogWith(OneHardware, Sensor(kind: "fan"))).IsError);

        var values = Assert.IsType<PluginValues>(Send(session, """{"t":"values","values":[1500]}""").Message);
        AssertValues(values, 1500);
        Assert.True(Send(session, """{"t":"values","values":[1,2,3]}""").IsError);
    }
}
