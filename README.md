# kusto-lsp

A standalone, local-only Kusto language server using Microsoft's official
[`Microsoft.Azure.Kusto.Language`](https://www.nuget.org/packages/Microsoft.Azure.Kusto.Language)
NuGet package. The executable is usable by any LSP client; it does not depend on
Monaco or a particular editor. It never executes queries, connects to clusters,
or accepts Azure credentials.

The implementation uses `KustoCode.Parse` / `GetSyntaxDiagnostics` for offline
diagnostics, `ParseAndAnalyze` / `GetDiagnostics` when a schema is supplied, and
`KustoCodeService.GetClassifications`, `GetCompletionItems`, and `GetQuickInfo`
for editor features. Schemas become official `ClusterSymbol`, `DatabaseSymbol`,
`TableSymbol`, `ColumnSymbol`, and `FunctionSymbol` objects. There is no
handwritten KQL parser. Maintained
[`StreamJsonRpc`](https://github.com/microsoft/vs-streamjsonrpc) handles JSON-RPC,
Content-Length framing, request IDs, dispatch, and request cancellation.

## Build and run

Requires the .NET 10 SDK. If it is already installed:

```sh
dotnet build src/KustoLsp -c Release
dotnet src/KustoLsp/bin/Release/net10.0/kusto-lsp.dll --stdio
# Optional default context:
dotnet src/KustoLsp/bin/Release/net10.0/kusto-lsp.dll --stdio --schema-file samples/schema.json
```

If `dotnet --version` fails, install Microsoft's SDK into this workspace, not a
system directory. On macOS/Linux:

```sh
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/kusto-dotnet-install.sh
bash /tmp/kusto-dotnet-install.sh --channel 10.0 --install-dir "$PWD/.dotnet" --no-path
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$PWD/.dotnet/home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
dotnet --version
```

PowerShell:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP/kusto-dotnet-install.ps1"
& "$env:TEMP/kusto-dotnet-install.ps1" -Channel 10.0 -InstallDir "$PWD/.dotnet" -NoPath
$env:DOTNET_ROOT = "$PWD/.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = "$PWD/.dotnet/home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
dotnet --version
```

The installer needs outbound HTTPS. `.dotnet`, `.tools`, build outputs, publish
directories, `node_modules`, and VSIX packages are gitignored. The server only
accepts `--stdio [--schema-file PATH]`; `--help` prints usage to stderr. Schema
files are read once at startup; invalid/missing files fail explicitly with a
nonzero exit. Use a client-configured executable, not `dotnet run`, so build
output cannot contaminate protocol stdout.

## Self-contained distribution

Publish a separate folder per runtime; end users do not need .NET:

```sh
dotnet publish src/KustoLsp -c Release -r osx-arm64 --self-contained true -o publish/osx-arm64
./publish/osx-arm64/kusto-lsp --stdio
```

Replace the runtime/output directory with `osx-x64`, `linux-x64`, `linux-arm64`,
`win-x64`, or `win-arm64`. On Windows the executable is `kusto-lsp.exe`.
Distribute the **entire publish directory**, including runtime libraries and
dependencies; this is not a single-file or NativeAOT build. Linux requires the
normal .NET OS runtime prerequisites (including ICU unless using invariant
globalization). Cross-publishing does not imply cross-platform runtime testing.

## Protocol

LSP 3.17 over stdin/stdout with UTF-8 JSON and byte-counted
`Content-Length: N\r\n\r\n` framing. **Stdout is protocol-only; stderr is logs.**
Positions, lengths, and token deltas use **UTF-16 code units**, not bytes or
Unicode scalar counts. CRLF, LF, and CR are supported. Multiline classifications
are split into nonempty line-local tokens; line terminators are never tokens.
Out-of-line character offsets clamp to line end and lines past the document
clamp to document end per LSP; negative positions and positions inside a
surrogate pair are rejected.

| Surface | Behavior |
| --- | --- |
| `initialize` / `initialized` | Advertises UTF-16, full document sync (`change: 1`), semantic tokens, completion, hover, `experimental.kustoSchemaVersion: 1`. |
| `textDocument/didOpen` | Requires URI, full text, integer version. Duplicate open is rejected and logged. Any absolute URI is accepted, including untitled documents. |
| `textDocument/didChange` | Full replacements only. Versions must strictly increase; stale/duplicate versions or ranged changes are rejected without changing document state. Multiple full replacements are applied in order. |
| `textDocument/didClose` | Cancels analysis, drops document and schema override, publishes empty diagnostics with the last document version. |
| `textDocument/publishDiagnostics` | Always contains `uri`, `version`, and `diagnostics`. Analysis is debounced 120 ms. Results are gated by document identity/version and internal schema generation. |
| `textDocument/semanticTokens/full` | Standard delta-encoded token data. `resultId` is opaque (`documentVersion:generation` currently). No range/delta endpoint or token modifiers. |
| `textDocument/completion` | Official suggestions with replacement `textEdit`, plain-text insertion, kind, sorting/filter text. No resolve endpoint or snippet tab stops. |
| `textDocument/hover` | Official QuickInfo as plaintext; `null` when none. No rendered HTML or Markdown. |
| `$/cancelRequest` | Cooperative request cancellation; errors use `-32800`. |
| `shutdown` / `exit` | Shutdown returns `null` and cancels analysis. Exit after shutdown returns process code 0; exit/EOF without shutdown returns 1. No work accepted after shutdown. |

The token legend, in order, is:

```text
comment keyword string number type function parameter variable property class namespace operator
```

Kusto columns map to `property`, tables/materialized views to `class`,
databases to `namespace`, query operators/commands to `keyword`. Punctuation and
unclassified text are not emitted. Kusto's generic non-string literal
classification maps to `number` (including boolean and typed scalar literals).
Clients should read the advertised legend rather than hardcode indices.

Input notifications are applied in arrival order. CPU analysis runs on a worker
separate from protocol dispatch, with bounded concurrency. New edits, schema
updates, close, and shutdown cancel obsolete work. Requests capture a snapshot;
superseded results return `ContentModified` (`-32801`). Clients should still
version-gate diagnostics and token responses, since messages already in transit
cannot be recalled. Cancellation is cooperative, not a hard execution deadline.

Before initialization, requests return `-32002`; invalid parameters return
`-32602`; unsupported request methods return `-32601`. Invalid notifications
are logged to stderr and leave state unchanged. Unknown notifications are
ignored by JSON-RPC. Malformed JSON/framing is fatal to the connection:
StreamJsonRpc disconnects, the server logs the error and exits nonzero. Clients
should restart rather than expect stream resynchronization. There is no query
execution, authentication, network server, file watcher, or workspace indexing.

## Schema context (optional)

Without a schema, **only syntax diagnostics** are published. Tables and columns
not known offline are not spuriously reported as missing. Official completions,
classification, and built-in/local symbol hover still work. This intentionally
does not diagnose semantic/type errors offline. A supplied schema, including an
**empty** one, enables full binding diagnostics against that context.

`--schema-file PATH` loads a default context for all documents. Use
[`samples/schema.json`](samples/schema.json) as a starting point. Schema names
are case-sensitive, duplicate member/column/parameter names are rejected, and
column/parameter types must be official Kusto scalar type names or recognized
aliases (for example `string`, `long`, `datetime`, `dynamic`). Function bodies
are KQL bodies such as `{ x * 2 }`, analyzed by the official library, never
executed. This v1 adapter currently supports scalar function parameters; tabular
parameters/defaults, external tables, materialized-view metadata, and
multi-database catalogs are not modeled.

Custom clients can send the **notification** `kusto/setSchema`:

```json
{
  "jsonrpc": "2.0",
  "method": "kusto/setSchema",
  "params": {
    "uri": "file:///queries/example.kql",
    "schema": {
      "version": 1,
      "cluster": "https://example.kusto.windows.net",
      "database": "Demo",
      "tables": [
        {
          "name": "Events",
          "columns": [
            { "name": "Timestamp", "type": "datetime" },
            { "name": "Count", "type": "long" }
          ]
        }
      ],
      "functions": [
        {
          "name": "Twice",
          "parameters": [{ "name": "x", "type": "long" }],
          "body": "{ x * 2 }"
        }
      ]
    }
  }
}
```

All displayed schema properties are required, though `tables`, `columns`,
`functions`, and `parameters` arrays may be empty. `version: 1` is the **schema
format version**, not the document revision. Extra properties are ignored.
Unsupported versions and invalid schemas are logged and the previous context
is preserved. Cluster strings are symbol identities, not connection requests.
Do not put credentials in schemas.

An override wins over the default and can be set before `didOpen`.
`{"uri":"file:///queries/example.kql","schema":null}` removes the override:
the document reverts to the default schema, or syntax-only mode if none exists.
Closing a document removes its override. Schema updates increment an internal
generation and republish diagnostics without changing the document version.
The server requests `workspace/semanticTokens/refresh` when the client
advertises `capabilities.workspace.semanticTokens.refreshSupport`; custom
clients without that support should request tokens again after schema updates.
Basic IDE use never requires this extension.

## Generic IDE and Neovim

Configure an LSP client with command `["/absolute/path/kusto-lsp", "--stdio"]`,
language ID `kusto`, extensions `.kql`, `.csl`, `.kusto`, UTF-16 positions and full
sync. Add `["--schema-file", "/absolute/path/schema.json"]` for optional context.
Do not shell-quote individual argument strings in an argument array.

Neovim 0.11+ (built-in LSP, no plugin required):

```lua
vim.filetype.add({ extension = { kql = "kusto", csl = "kusto", kusto = "kusto" } })
vim.lsp.config("kusto", {
  cmd = { "/absolute/path/kusto-lsp", "--stdio",
          "--schema-file", "/absolute/path/schema.json" },
  filetypes = { "kusto" },
  root_markers = { ".git" },
  workspace_required = false,
})
vim.lsp.enable("kusto")
```

Remove the last two command arguments to work without a schema. Neovim handles
standard semantic tokens, hover (`K`), and diagnostics; use its built-in LSP
omnifunc or your completion frontend for suggestions.

The minimal [VS Code extension](clients/vscode/README.md) uses
`vscode-languageclient`, includes semantic theme fallbacks, and launches a
configurable executable. It is a thin client, not an embedded server.

## Tests

```sh
dotnet test tests/KustoLsp.Tests
dotnet build src/KustoLsp -c Release
python3 tests/stdio_smoke.py -- dotnet "$PWD/src/KustoLsp/bin/Release/net10.0/kusto-lsp.dll" --stdio
# Or verify the distributable, with no SDK in the child process:
python3 tests/stdio_smoke.py -- "$PWD/publish/osx-arm64/kusto-lsp" --stdio
cd clients/vscode
npm ci
npm run check
npm test
npm run package
# Optional real-process check with VS Code's JSON-RPC transport:
KUSTO_LSP_EXECUTABLE="/absolute/path/publish/osx-arm64/kusto-lsp" npm test
```

The Python tests use only the standard library and drive a real child process,
checking actual framing, lifecycle, invalid inputs, offline/empty/full schemas,
schema clearing/defaults, official completion/hover, UTF-16/CRLF tokens and
diagnostics, version races, generation invalidation, and cancellation. Unit tests
exercise the official language APIs and position conversions directly.

Validated locally on macOS ARM64 with the .NET 10 SDK and a self-contained
publish. Extension checks/package creation are not proof of a real VS Code
extension-host session; VS Code and Neovim UI behavior and other OS runtimes
require separate manual verification.
