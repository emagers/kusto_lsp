using System.Text.Json;
using KustoLsp;
using StreamJsonRpc;

namespace KustoLsp.Tests;

public sealed class LanguageTests
{
    private static SchemaContext Schema(string tables = """[{"name":"Events","columns":[{"name":"Count","type":"long"}]}]""") =>
        SchemaContext.Parse(JsonDocument.Parse($$"""
        {"version":1,"cluster":"https://example.kusto.windows.net","database":"Demo","tables":{{tables}},
         "functions":[{"name":"Twice","parameters":[{"name":"x","type":"long"}],"body":"{ x * 2 }"}]}
        """).RootElement);

    [Fact]
    public void OfflineDiagnosticsAreSyntaxOnly()
    {
        Assert.Empty(new Analysis("UnknownTable | where Missing > 1", null, default).Diagnostics(default));
        Assert.NotEmpty(new Analysis("print x = (", null, default).Diagnostics(default));
        Assert.Empty(new Analysis("print x = 1", null, default).Diagnostics(default));
    }

    [Fact]
    public void SchemaEnablesBindingAndStoredFunctions()
    {
        Assert.Empty(new Analysis("Events | project Twice(Count)", Schema(), default).Diagnostics(default));
        Assert.NotEmpty(new Analysis("Events | project Missing", Schema(), default).Diagnostics(default));
        Assert.NotEmpty(new Analysis("MissingTable", Schema(), default).Diagnostics(default));
    }

    [Fact]
    public void EmptySchemaIsNotTheSameAsAbsentSchema()
    {
        Assert.Empty(new Analysis("UnknownTable", null, default).Diagnostics(default));
        Assert.NotEmpty(new Analysis("UnknownTable", Schema("[]"), default).Diagnostics(default));
    }

    [Fact]
    public void DiagnosticAndTokenOffsetsAfterEmojiAreUtf16()
    {
        const string text = "print a = '😀', b = Missing";
        var diagnostics = JsonSerializer.SerializeToElement(new Analysis(text, Schema("[]"), default).Diagnostics(default));
        var missing = diagnostics.EnumerateArray().Single(d => d.GetProperty("message").GetString()!.Contains("Missing"));
        Assert.Equal(text.IndexOf("Missing", StringComparison.Ordinal),
            missing.GetProperty("range").GetProperty("Start").GetProperty("Character").GetInt32());

        const string valid = "print a = '😀', b = 123";
        var tokens = new Analysis(valid, null, default).Tokens(default);
        var character = 0;
        var found = false;
        for (var i = 0; i < tokens.Length; i += 5)
        {
            character += tokens[i + 1];
            if (tokens[i + 3] != 3) continue;
            Assert.Equal(valid.IndexOf("123", StringComparison.Ordinal), character);
            Assert.Equal(3, tokens[i + 2]);
            found = true;
        }
        Assert.True(found);
    }

    [Fact]
    public void OfficialCompletionAndHoverExposeSchema()
    {
        var analysis = new Analysis("Events | project ", Schema(), default);
        var completion = JsonSerializer.Serialize(analysis.Completion(17, default));
        Assert.Contains("Count", completion);
        var hover = JsonSerializer.Serialize(new Analysis("Events | project Count", Schema(), default).Hover(18, default));
        Assert.Contains("Count", hover);
        Assert.Contains("long", hover);
    }

    [Fact]
    public void LinesUseUtf16AndHandleEveryNewline()
    {
        var lines = new TextLines("a😀b\r\ncd\re\n");
        Assert.Equal(4, lines.Offset(new(0, 4)));
        Assert.Equal(4, lines.Offset(new(0, 99)));
        Assert.Equal(11, lines.Offset(new(99, 0)));
        Assert.Equal(6, lines.Offset(new(1, 0)));
        Assert.Equal(new Position(1, 2), lines.PositionAt(8));
        Assert.Equal(new Position(3, 0), lines.PositionAt(11));
        Assert.Throws<LocalRpcException>(() => lines.Offset(new(0, 2)));
        Assert.Throws<LocalRpcException>(() => lines.Offset(new(-1, 0)));
        Assert.Equal(new[] { (0, 0, 4), (1, 0, 2), (2, 0, 1) }, lines.Split(0, 11));
    }

    [Fact]
    public void MultilineClassificationsNeverContainLineBreaks()
    {
        const string text = "print value = ```a😀\r\nb```\r\n| project value";
        var tokens = new Analysis(text, null, default).Tokens(default);
        Assert.NotEmpty(tokens);
        var line = 0;
        var character = 0;
        var sourceLines = text.Split("\r\n");
        var strings = new List<int>();
        for (var i = 0; i < tokens.Length; i += 5)
        {
            character = tokens[i] == 0 ? character + tokens[i + 1] : tokens[i + 1];
            line += tokens[i];
            Assert.InRange(tokens[i + 2], 1, sourceLines[line].Length - character);
            if (tokens[i + 3] == 2) strings.Add(line);
        }
        Assert.Contains(0, strings);
        Assert.Contains(1, strings);
    }

    [Theory]
    [InlineData("""[{"name":"T","columns":[{"name":"x","type":"not_a_type"}]}]""")]
    [InlineData("""[{"name":"T","columns":[]},{"name":"T","columns":[]}]""")]
    [InlineData("""[{"name":"T","columns":[{"name":"x","type":"long"},{"name":"x","type":"long"}]}]""")]
    public void InvalidSchemasAreRejected(string tables) =>
        Assert.Throws<LocalRpcException>(() => Schema(tables));
}
