# Standalone Kusto for VS Code

This thin client launches the separately published `kusto-lsp` executable using
`vscode-languageclient`. It registers `.kql`, `.csl`, and `.kusto`, uses standard
LSP semantic tokens for coloring, and provides completion, hover, and diagnostics.
It never runs queries or authenticates to Azure.

1. Publish the server as described in the repository README.
2. Run `npm ci` in this directory.
3. Open this directory in VS Code and press **F5**, or run `npm run package`
   and install the resulting VSIX with **Extensions: Install from VSIX**.
4. In the extension host, set `standaloneKusto.serverPath` to the absolute
   published executable path (include `.exe` on Windows).

```json
{
  "standaloneKusto.serverPath": "/absolute/path/publish/osx-arm64/kusto-lsp",
  "standaloneKusto.serverArgs": ["--stdio"],
  "standaloneKusto.schemaFile": "/absolute/path/schema.json"
}
```

For a framework-dependent build, set `serverPath` to `dotnet` and `serverArgs`
to `["/absolute/path/kusto-lsp.dll", "--stdio"]`. The schema setting is optional;
without it the server supplies syntax diagnostics and offline language features.
No custom schema notification is needed.

Use **Kusto: Restart Language Server** after changing settings or schema files.
Logs are in the **Kusto Language Server** output channel. The extension is
disabled in untrusted workspaces because it launches a configurable executable.
Semantic highlighting is enabled for Kusto; standard token types also have
TextMate scope fallbacks for themes. There is no separate TextMate grammar.

Development checks: `npm run check && npm test && npm run package`.
