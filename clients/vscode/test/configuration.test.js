"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { serverOptions } = require("../configuration");
const configuration = values => ({ get: (key, fallback) => values[key] ?? fallback });

test("launches stdio without a shell by default", () => {
  assert.deepEqual(serverOptions(configuration({})), { command: "kusto-lsp", args: ["--stdio"] });
});

test("preserves paths with spaces, DLL args and schema", () => {
  const args = ["/my files/kusto-lsp.dll", "--stdio"];
  const options = serverOptions(configuration({
    serverPath: "/dotnet path/dotnet", serverArgs: args, schemaFile: "/my files/schema.json"
  }));
  assert.deepEqual(options, {
    command: "/dotnet path/dotnet",
    args: ["/my files/kusto-lsp.dll", "--stdio", "--schema-file", "/my files/schema.json"]
  });
  assert.equal(args.length, 2);
});

test("invalid settings fail explicitly", () => {
  assert.throws(() => serverOptions(configuration({ serverPath: "" })), /executable/);
  assert.throws(() => serverOptions(configuration({ serverArgs: "--stdio" })), /array/);
  assert.throws(() => serverOptions(configuration({ schemaFile: 123 })), /string/);
});

test("all advertised token types have theme fallback scopes", () => {
  const manifest = require("../package.json");
  const scopes = manifest.contributes.semanticTokenScopes[0].scopes;
  assert.equal(Object.keys(scopes).length, 12);
  assert.deepEqual(manifest.contributes.languages[0].extensions, [".kql", ".csl", ".kusto"]);
  assert.equal(manifest.contributes.configurationDefaults["[kusto]"]["editor.semanticHighlighting.enabled"], true);
});
