using System.Text.Json;
using StreamJsonRpc;

namespace KustoLsp;

public sealed class LanguageServer : IDisposable
{
    private sealed class Document(string uri, string text, int version, long generation, SchemaContext? schema)
    {
        public readonly string Uri = uri;
        public readonly string Text = text;
        public readonly int Version = version;
        public readonly long Generation = generation;
        public readonly SchemaContext? Schema = schema;
        public readonly CancellationTokenSource Lifetime = new();
        public Analysis? Analysis;
    }

    private readonly JsonRpc rpc;
    private readonly SchemaContext? defaultSchema;
    private readonly object gate = new();
    private readonly Dictionary<string, Document> documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SchemaContext> schemas = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim worker = new(1);
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool initialized;
    private bool shutdown;
    private bool semanticRefresh;
    private long generation;
    public Task<int> ExitTask => exit.Task;
    public bool WasShutdown => shutdown;

    public LanguageServer(JsonRpc rpc, SchemaContext? defaultSchema)
    {
        this.rpc = rpc;
        this.defaultSchema = defaultSchema;
    }

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public object Initialize(JsonElement parameters)
    {
        lock (gate)
        {
            if (initialized || shutdown) throw Protocol.Invalid("Already initialized");
            if (parameters.ValueKind != JsonValueKind.Object) throw Protocol.Invalid("Expected initialize object");
            semanticRefresh = parameters.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("workspace", out var workspace)
                && workspace.ValueKind == JsonValueKind.Object
                && workspace.TryGetProperty("semanticTokens", out var semanticTokens)
                && semanticTokens.ValueKind == JsonValueKind.Object
                && semanticTokens.TryGetProperty("refreshSupport", out var refresh)
                && refresh.ValueKind == JsonValueKind.True;
            initialized = true;
            return new
            {
                capabilities = new
                {
                    positionEncoding = "utf-16",
                    textDocumentSync = new { openClose = true, change = 1 },
                    semanticTokensProvider = new { legend = new { tokenTypes = Analysis.TokenTypes, tokenModifiers = System.Array.Empty<string>() }, full = true },
                    completionProvider = new { triggerCharacters = new[] { ".", "|" }, resolveProvider = false },
                    hoverProvider = true,
                    experimental = new { kustoSchemaVersion = 1 }
                },
                serverInfo = new { name = "kusto-lsp", version = "0.1.0" }
            };
        }
    }

    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
    public void Initialized(JsonElement parameters) => Notification(Ready);

    [JsonRpcMethod("shutdown")]
    public object? Shutdown()
    {
        lock (gate)
        {
            Ready();
            shutdown = true;
            foreach (var document in documents.Values) document.Lifetime.Cancel();
            return null;
        }
    }

    [JsonRpcMethod("exit")]
    public void Exit() => exit.TrySetResult(shutdown ? 0 : 1);

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(JsonElement parameters) => Notification(() =>
    {
        Ready();
        var item = Protocol.Property(parameters, "textDocument");
        var uri = Protocol.Uri(item);
        if (documents.ContainsKey(uri)) throw Protocol.Invalid("Document is already open");
        Replace(uri, Protocol.String(item, "text", allowEmpty: true), Protocol.Integer(item, "version"));
    });

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(JsonElement parameters) => Notification(() =>
    {
        Ready();
        var item = Protocol.Property(parameters, "textDocument");
        var uri = Protocol.Uri(item);
        var document = Find(uri);
        var version = Protocol.Integer(item, "version");
        if (version <= document.Version) throw Protocol.Invalid("Ignored non-increasing document version");
        var changes = Protocol.Array(parameters, "contentChanges").ToArray();
        if (changes.Length == 0) throw Protocol.Invalid("Expected a full-document change");
        var text = document.Text;
        foreach (var change in changes)
        {
            if (change.ValueKind != JsonValueKind.Object || change.TryGetProperty("range", out _))
                throw Protocol.Invalid("Only full-document changes are supported");
            text = Protocol.String(change, "text", allowEmpty: true);
        }
        Replace(uri, text, version);
    });

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(JsonElement parameters) => Notification(() =>
    {
        Ready();
        var uri = Protocol.Uri(Protocol.Property(parameters, "textDocument"));
        var document = Find(uri);
        document.Lifetime.Cancel();
        documents.Remove(uri);
        schemas.Remove(uri);
        Observe(rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new { uri, version = document.Version, diagnostics = System.Array.Empty<object>() }));
    });

    [JsonRpcMethod("kusto/setSchema", UseSingleObjectParameterDeserialization = true)]
    public void SetSchema(JsonElement parameters) => Notification(() =>
    {
        Ready();
        var uri = Protocol.Uri(parameters);
        var schema = Protocol.Property(parameters, "schema");
        // Validate fully before mutating context. Null removes the override.
        var context = schema.ValueKind == JsonValueKind.Null ? null : SchemaContext.Parse(schema);
        if (context is null) schemas.Remove(uri);
        else schemas[uri] = context;
        if (documents.TryGetValue(uri, out var document)) Replace(uri, document.Text, document.Version);
        if (semanticRefresh) Observe(rpc.InvokeWithParameterObjectAsync<object?>("workspace/semanticTokens/refresh", null));
    });

    [JsonRpcMethod("textDocument/semanticTokens/full", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> SemanticTokens(JsonElement parameters, CancellationToken cancellationToken) =>
        Request(parameters, false, (analysis, _, document, token) =>
            new { resultId = $"{document.Version}:{document.Generation}", data = analysis.Tokens(token) }, cancellationToken);

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> Completion(JsonElement parameters, CancellationToken cancellationToken) =>
        Request(parameters, true, (analysis, offset, _, token) => analysis.Completion(offset, token), cancellationToken);

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> Hover(JsonElement parameters, CancellationToken cancellationToken) =>
        Request(parameters, true, (analysis, offset, _, token) => analysis.Hover(offset, token), cancellationToken);

    private Task<object?> Request(JsonElement parameters, bool needsPosition,
        Func<Analysis, int, Document, CancellationToken, object?> action, CancellationToken cancellation)
    {
        lock (gate)
        {
            Ready();
            var document = Find(Protocol.Uri(Protocol.Property(parameters, "textDocument")));
            Position? requestedPosition = null;
            if (needsPosition)
            {
                var position = Protocol.Property(parameters, "position");
                requestedPosition = new(Protocol.Integer(position, "line"), Protocol.Integer(position, "character"));
            }
            return RunRequest(document, requestedPosition, action, cancellation);
        }
    }

    private async Task<object?> RunRequest(Document document, Position? position,
        Func<Analysis, int, Document, CancellationToken, object?> action, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, document.Lifetime.Token);
        try
        {
            var result = await Work(document, (analysis, token) =>
                action(analysis, position is null ? 0 : analysis.Offset(position), document, token), linked.Token);
            lock (gate)
            {
                if (!Current(document)) throw Protocol.Error(-32801, "Document or schema changed");
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            throw cancellation.IsCancellationRequested
                ? Protocol.Error(-32800, "Request cancelled")
                : Protocol.Error(-32801, "Document or schema changed");
        }
    }

    private async Task<T> Work<T>(Document document, Func<Analysis, CancellationToken, T> action, CancellationToken cancellation)
    {
        await worker.WaitAsync(cancellation);
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                document.Analysis ??= new Analysis(document.Text, document.Schema, cancellation);
                var result = action(document.Analysis, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }, cancellation);
        }
        finally { worker.Release(); }
    }

    private void Replace(string uri, string text, int version)
    {
        if (documents.TryGetValue(uri, out var previous)) previous.Lifetime.Cancel();
        var document = new Document(uri, text, version, ++generation, schemas.TryGetValue(uri, out var schema) ? schema : defaultSchema);
        documents[uri] = document;
        Observe(PublishDiagnostics(document));
    }

    private async Task PublishDiagnostics(Document document)
    {
        try
        {
            await Task.Delay(120, document.Lifetime.Token);
            var diagnostics = await Work(document, (analysis, token) => analysis.Diagnostics(token), document.Lifetime.Token);
            Task publish;
            lock (gate)
            {
                if (!Current(document)) return;
                // Enqueue while holding the generation gate, so a later edit/close cannot
                // enqueue its diagnostics before this snapshot's notification.
                publish = rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                    new { uri = document.Uri, version = document.Version, diagnostics });
            }
            await publish;
        }
        catch (OperationCanceledException) when (document.Lifetime.IsCancellationRequested) { }
    }

    private bool Current(Document document) => !shutdown && documents.TryGetValue(document.Uri, out var current) && ReferenceEquals(document, current);
    private Document Find(string uri) => documents.GetValueOrDefault(uri) ?? throw Protocol.Invalid("Document is not open");
    private void Ready()
    {
        if (!initialized) throw Protocol.Error(-32002, "Server not initialized");
        if (shutdown) throw Protocol.Invalid("Server has shut down");
    }

    private void Notification(Action action)
    {
        try { lock (gate) action(); }
        catch (LocalRpcException ex) { Console.Error.WriteLine($"kusto-lsp: {ex.Message}"); }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception ex) { Console.Error.WriteLine($"kusto-lsp: background operation failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            shutdown = true;
            foreach (var document in documents.Values) document.Lifetime.Cancel();
            documents.Clear();
            schemas.Clear();
        }
    }
}
