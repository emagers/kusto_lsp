"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const {
  createMessageConnection, StreamMessageReader, StreamMessageWriter
} = require("vscode-jsonrpc/node");

test("VS Code JSON-RPC transport interoperates with the published server", {
  skip: !process.env.KUSTO_LSP_EXECUTABLE,
  timeout: 15000
}, async context => {
  const child = spawn(process.env.KUSTO_LSP_EXECUTABLE, ["--stdio"]);
  let stderr = "";
  child.stderr.on("data", chunk => { stderr += chunk; });
  const exited = new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("exit", (code, signal) => resolve({ code, signal }));
  });
  const connection = createMessageConnection(
    new StreamMessageReader(child.stdout), new StreamMessageWriter(child.stdin)
  );
  context.after(() => {
    connection.dispose();
    if (child.exitCode === null) child.kill();
  });
  const diagnostics = [];
  connection.onNotification("textDocument/publishDiagnostics", params => diagnostics.push(params));
  let refreshCount = 0;
  connection.onRequest("workspace/semanticTokens/refresh", () => {
    refreshCount++;
    return null;
  });
  connection.listen();

  const initialization = await connection.sendRequest("initialize", {
    capabilities: { workspace: { semanticTokens: { refreshSupport: true } } }
  });
  assert.equal(initialization.capabilities.positionEncoding, "utf-16");
  assert.equal(initialization.capabilities.experimental.kustoSchemaVersion, 1);
  await connection.sendNotification("initialized", {});
  const uri = "untitled:client-interop.kql";
  await connection.sendNotification("textDocument/didOpen", {
    textDocument: { uri, languageId: "kusto", version: 1, text: "Events | project Count" }
  });
  const before = await connection.sendRequest("textDocument/semanticTokens/full", { textDocument: { uri } });
  const schema = require("../../../samples/schema.json");
  await connection.sendNotification("kusto/setSchema", { uri, schema });
  const after = await connection.sendRequest("textDocument/semanticTokens/full", { textDocument: { uri } });
  assert.notEqual(before.resultId, after.resultId);
  assert.ok(after.data.length > 0);
  assert.equal(after.data.length % 5, 0);
  const hover = await connection.sendRequest("textDocument/hover", {
    textDocument: { uri }, position: { line: 0, character: 18 }
  });
  assert.match(hover.contents.value, /Count/);
  assert.match(hover.contents.value, /long/);

  await connection.sendNotification("textDocument/didChange", {
    textDocument: { uri, version: 2 }, contentChanges: [{ text: "Events | project " }]
  });
  const completion = await connection.sendRequest("textDocument/completion", {
    textDocument: { uri }, position: { line: 0, character: 17 }
  });
  assert.ok(completion.items.some(item => item.label === "Count"));
  await connection.sendNotification("textDocument/didClose", { textDocument: { uri } });
  assert.equal(await connection.sendRequest("shutdown"), null);
  await connection.sendNotification("exit");
  assert.deepEqual(await exited, { code: 0, signal: null });
  assert.equal(refreshCount, 1);
  assert.ok(diagnostics.some(item => item.uri === uri && item.version === 2 && item.diagnostics.length === 0));
  assert.equal(stderr, "");
});
