using Kusto.Language;
using Kusto.Language.Editor;

namespace KustoLsp;

public sealed class Analysis
{
    public static readonly string[] TokenTypes =
        ["comment", "keyword", "string", "number", "type", "function", "parameter", "variable", "property", "class", "namespace", "operator"];
    private readonly KustoCode code;
    private readonly KustoCodeService service;
    private readonly TextLines lines;
    private readonly bool hasSchema;

    public Analysis(string text, SchemaContext? schema, CancellationToken cancellation)
    {
        hasSchema = schema is not null;
        code = hasSchema
            ? KustoCode.ParseAndAnalyze(text, schema!.Globals, cancellation)
            : KustoCode.Parse(text);
        service = new KustoCodeService(code);
        lines = new TextLines(text);
    }

    public object[] Diagnostics(CancellationToken cancellation)
    {
        var diagnostics = hasSchema ? code.GetDiagnostics(cancellation) : code.GetSyntaxDiagnostics(cancellation);
        return diagnostics.Select(d => (object)new
        {
            range = lines.Range(d.Start, d.Length),
            severity = d.Severity switch { "Warning" => 2, "Suggestion" => 4, _ => 1 },
            code = d.Code,
            source = "kusto",
            message = d.Message
        }).ToArray();
    }

    public int Offset(Position position) => lines.Offset(position);

    public int[] Tokens(CancellationToken cancellation)
    {
        var classifications = service.GetClassifications(0, code.Text.Length, cancellationToken: cancellation);
        var data = new List<int>();
        var previousLine = 0;
        var previousCharacter = 0;
        foreach (var classification in classifications.Classifications.OrderBy(c => c.Start))
        {
            var type = TokenType(classification.Kind);
            if (type < 0) continue;
            foreach (var part in lines.Split(classification.Start, classification.Length))
            {
                data.Add(part.Line - previousLine);
                data.Add(part.Line == previousLine ? part.Character - previousCharacter : part.Character);
                data.Add(part.Length);
                data.Add(type);
                data.Add(0);
                previousLine = part.Line;
                previousCharacter = part.Character;
            }
        }
        return data.ToArray();
    }

    public object Completion(int offset, CancellationToken cancellation)
    {
        var info = service.GetCompletionItems(offset, cancellationToken: cancellation);
        return new
        {
            isIncomplete = false,
            items = info.Items.Select(item => new
            {
                label = item.DisplayText,
                kind = CompletionType(item.Kind),
                sortText = item.OrderText,
                filterText = item.MatchText,
                insertTextFormat = 1,
                textEdit = new
                {
                    range = lines.Range(info.EditStart, info.EditLength),
                    newText = string.Concat(item.ApplyTexts.Select(t => t.Text))
                }
            }).ToArray()
        };
    }

    public object? Hover(int offset, CancellationToken cancellation)
    {
        var info = service.GetQuickInfo(offset,
            QuickInfoOptions.Default.WithShowDiagnostics(hasSchema), cancellation);
        return string.IsNullOrWhiteSpace(info.Text) ? null : new { contents = new { kind = "plaintext", value = info.Text } };
    }

    private static int TokenType(ClassificationKind kind) => kind switch
    {
        ClassificationKind.Comment => 0,
        ClassificationKind.Keyword or ClassificationKind.QueryOperator or ClassificationKind.Command or ClassificationKind.Directive => 1,
        ClassificationKind.StringLiteral => 2,
        ClassificationKind.Literal => 3,
        ClassificationKind.Type => 4,
        ClassificationKind.Function => 5,
        ClassificationKind.Parameter or ClassificationKind.ClientParameter or ClassificationKind.QueryParameter or ClassificationKind.SignatureParameter => 6,
        ClassificationKind.Variable or ClassificationKind.Identifier => 7,
        ClassificationKind.Column or ClassificationKind.SchemaMember => 8,
        ClassificationKind.Table or ClassificationKind.MaterializedView => 9,
        ClassificationKind.Database => 10,
        ClassificationKind.ScalarOperator or ClassificationKind.MathOperator => 11,
        _ => -1
    };

    private static int CompletionType(CompletionKind kind) => kind switch
    {
        CompletionKind.BuiltInFunction or CompletionKind.LocalFunction or CompletionKind.DatabaseFunction or CompletionKind.AggregateFunction => 3,
        CompletionKind.Column => 5,
        CompletionKind.Table or CompletionKind.MaterialiedView => 7,
        CompletionKind.Variable or CompletionKind.Parameter => 6,
        CompletionKind.Database or CompletionKind.Cluster => 9,
        CompletionKind.ScalarType => 25,
        CompletionKind.Keyword => 14,
        _ => 1
    };
}
