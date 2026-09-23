#!/usr/bin/env python3
"""Black-box LSP tests. Pass the server launch command after --."""
import json
import queue
import subprocess
import sys
import threading
import time
import unittest
from pathlib import Path

COMMAND = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not COMMAND:
    raise SystemExit("Usage: python3 tests/stdio_smoke.py -- dotnet path/to/kusto-lsp.dll --stdio")
sys.argv = [sys.argv[0]]
URI = "file:///smoke.kql"
SCHEMA = {
    "version": 1, "cluster": "https://example.kusto.windows.net", "database": "Demo",
    "tables": [{"name": "Events", "columns": [{"name": "Count", "type": "long"}]}],
    "functions": [{"name": "Twice", "parameters": [{"name": "x", "type": "long"}], "body": "{ x * 2 }"}],
}


class Client:
    def __init__(self, extra=()):
        self.process = subprocess.Popen(COMMAND + list(extra), stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.messages = queue.Queue()
        self.pending = []
        self.errors = []
        self.next_id = 0
        threading.Thread(target=self.read, daemon=True).start()
        threading.Thread(target=self.read_errors, daemon=True).start()

    def read_errors(self):
        for line in self.process.stderr:
            self.errors.append(line.decode("utf-8", errors="replace").strip())

    def read(self):
        try:
            while True:
                headers = {}
                while True:
                    line = self.process.stdout.readline()
                    if not line:
                        return
                    if line == b"\r\n":
                        break
                    key, value = line.decode("ascii").split(":", 1)
                    headers[key.lower()] = value.strip()
                body = self.process.stdout.read(int(headers["content-length"]))
                message = json.loads(body)
                assert message["jsonrpc"] == "2.0"
                self.messages.put(message)
        except Exception as error:
            self.messages.put(error)

    def send(self, method, params=None, request=False):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if request:
            self.next_id += 1
            message["id"] = self.next_id
        self.raw(json.dumps(message, ensure_ascii=False).encode())
        return self.next_id

    def raw(self, body):
        self.process.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
        self.process.stdin.flush()

    def wait(self, predicate, timeout=20):
        deadline = time.monotonic() + timeout
        while True:
            for index, message in enumerate(self.pending):
                if predicate(message):
                    return self.pending.pop(index)
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError(f"LSP timeout; pending={self.pending!r}; stderr={self.errors!r}")
            try:
                message = self.messages.get(timeout=remaining)
            except queue.Empty:
                raise AssertionError(f"LSP timeout; stderr={self.errors!r}") from None
            if isinstance(message, Exception):
                raise message
            self.pending.append(message)

    def request(self, method, params=None):
        request_id = self.send(method, params, True)
        return self.wait(lambda m: m.get("id") == request_id)

    def initialize(self):
        result = self.request("initialize", {"capabilities": {}})
        self.send("initialized", {})
        return result["result"]["capabilities"]

    def open(self, text, version=1):
        self.send("textDocument/didOpen", {"textDocument": {
            "uri": URI, "languageId": "kusto", "version": version, "text": text}})

    def change(self, text, version):
        self.send("textDocument/didChange", {"textDocument": {"uri": URI, "version": version},
                                            "contentChanges": [{"text": text}]})

    def diagnostics(self, version):
        return self.wait(lambda m: m.get("method") == "textDocument/publishDiagnostics"
                         and m["params"].get("version") == version)["params"]["diagnostics"]

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=5)
        self.process.stdin.close()
        self.process.stdout.close()
        self.process.stderr.close()


class StdioTests(unittest.TestCase):
    def setUp(self):
        self.client = Client()
        self.addCleanup(self.client.close)

    def test_lifecycle_and_malformed_params(self):
        c = self.client
        self.assertEqual(-32002, c.request("textDocument/hover", {})["error"]["code"])
        capabilities = c.initialize()
        self.assertEqual("utf-16", capabilities["positionEncoding"])
        self.assertEqual(1, capabilities["textDocumentSync"]["change"])
        self.assertEqual(1, capabilities["experimental"]["kustoSchemaVersion"])
        self.assertEqual(12, len(capabilities["semanticTokensProvider"]["legend"]["tokenTypes"]))
        self.assertEqual(-32602, c.request("initialize", {})["error"]["code"])
        self.assertEqual(-32601, c.request("not/aMethod", {})["error"]["code"])
        self.assertEqual(-32601, c.request("Dispose")["error"]["code"])
        c.open("print x = 1")
        self.assertEqual(-32602, c.request("textDocument/hover", {
            "textDocument": {"uri": URI}, "position": {"line": "bad", "character": 0}})["error"]["code"])
        self.assertIn("result", c.request("shutdown"))
        self.assertEqual(-32602, c.request("textDocument/hover", {})["error"]["code"])
        c.send("exit")
        self.assertEqual(0, c.process.wait(timeout=5))

    def test_offline_and_schema_updates(self):
        c = self.client
        c.initialize()
        c.open("Events | project Missing")
        self.assertEqual([], c.diagnostics(1))
        c.send("kusto/setSchema", {"uri": URI, "schema": SCHEMA})
        self.assertTrue(c.diagnostics(1))
        c.send("kusto/setSchema", {"uri": URI, "schema": None})
        self.assertEqual([], c.diagnostics(1))
        c.change("print x = (", 2)
        self.assertTrue(c.diagnostics(2))
        c.change("print x = 1", 3)
        self.assertEqual([], c.diagnostics(3))
        c.send("textDocument/didClose", {"textDocument": {"uri": URI}})
        self.assertEqual([], c.diagnostics(3))
        self.assertEqual(-32602, c.request("textDocument/semanticTokens/full",
                                         {"textDocument": {"uri": URI}})["error"]["code"])

    def test_completion_hover_and_function_schema(self):
        c = self.client
        c.initialize()
        c.send("kusto/setSchema", {"uri": URI, "schema": SCHEMA})
        c.open("Events | project ")
        result = c.request("textDocument/completion", {
            "textDocument": {"uri": URI}, "position": {"line": 0, "character": 17}})
        self.assertIn("Count", [item["label"] for item in result["result"]["items"]])
        count = next(item for item in result["result"]["items"] if item["label"] == "Count")
        self.assertEqual("Count", count["textEdit"]["newText"])
        c.change("Events | project Twice(Count)", 2)
        self.assertEqual([], c.diagnostics(2))
        hover = c.request("textDocument/hover", {
            "textDocument": {"uri": URI}, "position": {"line": 0, "character": 24}})
        self.assertIn("Count", hover["result"]["contents"]["value"])
        self.assertIn("long", hover["result"]["contents"]["value"])

    def test_unicode_tokens_and_version_races(self):
        c = self.client
        c.initialize()
        text = "print value = ```a😀\r\nb```\r\n| project value"
        c.open(text)
        tokens = c.request("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}})["result"]
        data = tokens["data"]
        line = character = 0
        strings = []
        lengths = [len(s.encode("utf-16-le")) // 2 for s in text.split("\r\n")]
        for i in range(0, len(data), 5):
            delta_line, delta_character, length, kind, modifiers = data[i:i + 5]
            character = character + delta_character if delta_line == 0 else delta_character
            line += delta_line
            self.assertGreater(length, 0)
            self.assertLessEqual(character + length, lengths[line])
            self.assertEqual(0, modifiers)
            if kind == 2:
                strings.append(line)
        self.assertIn(0, strings)
        self.assertIn(1, strings)
        # Flood edits without waiting for analysis; only the latest snapshot may survive.
        for version in range(2, 41):
            c.change("print bad = (" if version < 40 else "print good = 1", version)
        c.change("print stale = (", 39)
        latest = c.request("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}})["result"]
        self.assertTrue(latest["resultId"].startswith("40:"))
        self.assertEqual([], c.diagnostics(40))
        c.send("textDocument/didChange", {"textDocument": {"uri": URI, "version": 41},
                                         "contentChanges": [{"range": {}, "text": "("}]})
        unchanged = c.request("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}})["result"]
        self.assertEqual(latest["resultId"], unchanged["resultId"])

    def test_bad_schema_preserves_context(self):
        c = self.client
        c.initialize()
        c.open("Events | project Count")
        c.send("kusto/setSchema", {"uri": URI, "schema": SCHEMA})
        c.diagnostics(1)
        before = c.request("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}})["result"]["resultId"]
        c.send("kusto/setSchema", {"uri": URI, "schema": {"version": 99}})
        after = c.request("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}})["result"]["resultId"]
        self.assertEqual(before, after)

    def test_empty_schema_and_utf16_diagnostic(self):
        c = self.client
        c.initialize()
        text = "print a = '😀', b = Missing"
        c.open(text)
        self.assertEqual([], c.diagnostics(1))
        c.send("kusto/setSchema", {"uri": URI, "schema": dict(SCHEMA, tables=[], functions=[])})
        diagnostics = c.diagnostics(1)
        missing = next(d for d in diagnostics if "Missing" in d["message"])
        self.assertEqual(len(text[:text.index("Missing")].encode("utf-16-le")) // 2,
                         missing["range"]["start"]["character"])
        c.send("kusto/setSchema", {"uri": URI, "schema": None})
        self.assertEqual([], c.diagnostics(1))

    def test_default_schema_file_and_override_clear(self):
        schema_file = str(Path(__file__).resolve().parents[1] / "samples" / "schema.json")
        c = Client(["--schema-file", schema_file])
        self.addCleanup(c.close)
        c.initialize()
        c.open("Events | project Count")
        self.assertEqual([], c.diagnostics(1))
        c.send("kusto/setSchema", {"uri": URI, "schema": dict(SCHEMA, tables=[], functions=[])})
        self.assertTrue(c.diagnostics(1))
        c.send("kusto/setSchema", {"uri": URI, "schema": None})
        self.assertEqual([], c.diagnostics(1))

    def test_requests_cancel_and_discard_old_schema_generation(self):
        c = self.client
        c.initialize()
        text = "\n".join(f"let value{i} = {i};" for i in range(2500)) + "\nprint value2499"
        c.open(text)
        request = c.send("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}}, True)
        c.send("$/cancelRequest", {"id": request})
        cancelled = c.wait(lambda m: m.get("id") == request)
        self.assertEqual(-32800, cancelled["error"]["code"])
        # A new generation supersedes a running request even without a document edit.
        stale = c.send("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}}, True)
        c.send("kusto/setSchema", {"uri": URI, "schema": SCHEMA})
        result = c.wait(lambda m: m.get("id") == stale)
        self.assertEqual(-32801, result["error"]["code"])
        current = c.request("textDocument/semanticTokens/full", {"textDocument": {"uri": URI}})["result"]
        self.assertTrue(current["resultId"].startswith("1:2"))

    def test_schema_refresh_request_when_client_supports_it(self):
        c = self.client
        c.request("initialize", {"capabilities": {"workspace": {"semanticTokens": {"refreshSupport": True}}}})
        c.send("initialized", {})
        c.open("Events")
        c.send("kusto/setSchema", {"uri": URI, "schema": SCHEMA})
        refresh = c.wait(lambda m: m.get("method") == "workspace/semanticTokens/refresh")
        self.assertIn("id", refresh)
        c.raw(json.dumps({"jsonrpc": "2.0", "id": refresh["id"], "result": None}).encode())
        self.assertEqual([], c.diagnostics(1))

    def test_invalid_schema_file_fails_at_startup(self):
        c = Client(["--schema-file", str(Path(__file__).resolve().parent / "does-not-exist.json")])
        self.addCleanup(c.close)
        self.assertNotEqual(0, c.process.wait(timeout=5))

    def test_exit_without_shutdown(self):
        self.client.send("exit")
        self.assertEqual(1, self.client.process.wait(timeout=5))

    def test_malformed_json_does_not_hang(self):
        c = self.client
        c.raw(b"{bad json")
        # StreamJsonRpc closes an unrecoverable malformed JSON connection.
        self.assertNotEqual(0, c.process.wait(timeout=5))


if __name__ == "__main__":
    unittest.main(verbosity=2)
