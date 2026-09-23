using KustoLsp;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using System.Reflection;

if (args is ["--help"] or ["-h"])
{
    Console.Error.WriteLine("Usage: kusto-lsp --stdio [--schema-file PATH]");
    return 0;
}
if (args is not ["--stdio"] && args is not ["--stdio", "--schema-file", _])
{
    Console.Error.WriteLine("Usage: kusto-lsp --stdio [--schema-file PATH]");
    return 2;
}

try
{
    var schema = args.Length == 3 ? SchemaContext.ParseFile(args[2]) : null;
    var formatter = new SystemTextJsonFormatter();
    formatter.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    using var handler = new HeaderDelimitedMessageHandler(
        Console.OpenStandardOutput(), Console.OpenStandardInput(), formatter);
    using var rpc = new JsonRpc(handler)
    {
        SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false),
        CancelLocallyInvokedMethodsWhenConnectionIsClosed = true
    };
    using var server = new LanguageServer(rpc, schema);
    foreach (var method in typeof(LanguageServer).GetMethods())
        if (method.GetCustomAttribute<JsonRpcMethodAttribute>() is { } attribute)
            rpc.AddLocalRpcMethod(method, server, attribute);
    rpc.StartListening();
    var finished = await Task.WhenAny(server.ExitTask, rpc.Completion);
    if (finished == server.ExitTask)
        return await server.ExitTask;
    await rpc.Completion;
    return server.WasShutdown ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"kusto-lsp: {ex.Message}");
    return 1;
}
