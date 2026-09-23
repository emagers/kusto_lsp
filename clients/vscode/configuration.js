"use strict";

function serverOptions(configuration) {
  const command = configuration.get("serverPath", "kusto-lsp");
  const args = configuration.get("serverArgs", ["--stdio"]);
  const schema = configuration.get("schemaFile", "");
  if (typeof command !== "string" || !command.trim()) {
    throw new Error("standaloneKusto.serverPath must name an executable.");
  }
  if (!Array.isArray(args) || !args.every(value => typeof value === "string")) {
    throw new Error("standaloneKusto.serverArgs must be an array of strings.");
  }
  if (typeof schema !== "string") {
    throw new Error("standaloneKusto.schemaFile must be a string.");
  }
  return { command, args: schema ? [...args, "--schema-file", schema] : [...args] };
}

module.exports = { serverOptions };
