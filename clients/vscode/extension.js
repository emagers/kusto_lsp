"use strict";

const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");
const { serverOptions } = require("./configuration");

let client;
let restarting;

async function start() {
  const options = serverOptions(vscode.workspace.getConfiguration("standaloneKusto"));
  options.transport = TransportKind.stdio;
  client = new LanguageClient("standaloneKusto", "Kusto Language Server", options, {
    documentSelector: [{ language: "kusto" }],
    outputChannelName: "Kusto Language Server"
  });
  await client.start();
}

async function restart() {
  if (restarting) return restarting;
  restarting = (async () => {
    try {
      if (client) await client.stop();
      await start();
    } catch (error) {
      await vscode.window.showErrorMessage(`Kusto language server: ${error.message}`);
    } finally {
      restarting = undefined;
    }
  })();
  return restarting;
}

async function activate(context) {
  context.subscriptions.push(vscode.commands.registerCommand("standaloneKusto.restart", restart));
  await restart();
}

async function deactivate() {
  if (restarting) await restarting;
  if (client) await client.stop();
}

module.exports = { activate, deactivate };
