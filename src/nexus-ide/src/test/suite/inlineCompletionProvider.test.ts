import * as assert from "assert";
import * as vscode from "vscode";
import { GxInlineCompletionItemProvider } from "../../inlineCompletionProvider";
import { GxFileSystemProvider } from "../../gxFileSystem";

async function openDoc(content: string): Promise<vscode.TextDocument> {
  return vscode.workspace.openTextDocument({ content, language: "genexus" });
}

const NO_CONTEXT = {} as vscode.InlineCompletionContext;
const NOT_CANCELLED = {
  isCancellationRequested: false,
  onCancellationRequested: () => ({ dispose: () => {} }),
} as vscode.CancellationToken;

function labelsOf(
  result: vscode.InlineCompletionItem[] | vscode.InlineCompletionList,
): string[] {
  const items = Array.isArray(result) ? result : result.items;
  return items.map((i) =>
    typeof i.insertText === "string" ? i.insertText : i.insertText.value,
  );
}

const AI_SETTING = "inlineCompletion.ai";

// #324: the AI tests below are the only ones in the suite that do real
// configuration I/O, and they target the Global store, so they depend on a
// disk-backed settings.json under the (cwd-derived) .vscode-test user-data-dir.
// Two consequences, both fixed here rather than by raising a timeout:
//
//  1. A mocha timeout does not cancel a pending async function. If the write of
//     `true` lands after the test already timed out, the paired restore can run
//     out of order, or never run at all if the host exits first, and
//     `inlineCompletion.ai = true` persists into the profile and breaks the
//     "disabled" test on every later run.
//  2. A test whose precondition is "the profile happens to be clean" is not a
//     test. Each test below now writes the value it needs before asserting, and
//     the hooks neutralize whatever a previous or leaked run left behind, so the
//     suite is self-healing instead of order- and history-dependent.
async function setAiEnabled(enabled: boolean): Promise<void> {
  await vscode.workspace
    .getConfiguration("genexus")
    .update(AI_SETTING, enabled, vscode.ConfigurationTarget.Global);
}

suite("GxInlineCompletionItemProvider - context-aware ghost text", function () {
  // Justified by measurement, and scoped to this suite rather than the runner:
  // the two configuration tests measured 46-426 ms in isolation and 184 ms under
  // the release-preflight parallel wave, against mocha's 2000 ms default. A
  // disk-backed Global write on a host contended by the CLI, Python, PowerShell
  // and dotnet lanes can exceed 2 s without indicating a defect, so this suite
  // gets headroom; the global timeout is untouched so the other 109 tests are
  // not slowed or masked.
  this.timeout(10000);

  suiteSetup(async () => {
    await setAiEnabled(false);
  });

  teardown(async () => {
    // Runs after every test, including a timed-out one, so a leak from this run
    // cannot become the next run's starting state.
    await setAiEnabled(false);
  });

  test("suggests real structure fields and methods after '&var.' when the variable is an SDT", async () => {
    const doc = await openDoc("&cliente.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [
      { name: "cliente", type: "SDTCliente", length: 0 },
    ];
    (fsProvider as any).getStructure = async () => ({
      children: [{ name: "Nombre", type: "Character" }],
    });

    const provider = new GxInlineCompletionItemProvider(fsProvider);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    const labels = labelsOf(result);
    assert.ok(labels.includes("Nombre"), "expected the real SDT field as ghost text");
  });

  test("emits nothing for '&var.' when the variable is unknown (no guess)", async () => {
    const doc = await openDoc("&missing.");
    const fsProvider = new GxFileSystemProvider();
    (fsProvider as any).readObjectVariables = async () => [];

    const provider = new GxInlineCompletionItemProvider(fsProvider);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    assert.strictEqual(labelsOf(result).length, 0);
  });

  test("genexus.inlineCompletion.ai declares false as its default", () => {
    // The declared default is what makes the AI path opt-in. Asserting it from
    // the package contribution is a pure read, so it does not depend on a clean
    // profile at all.
    const declared = vscode.workspace
      .getConfiguration("genexus")
      .inspect<boolean>(AI_SETTING)?.defaultValue;
    assert.strictEqual(declared, false, "the AI completion setting must default to off");
  });

  test("AI path stays empty when genexus.inlineCompletion.ai is disabled", async () => {
    await setAiEnabled(false);

    const doc = await openDoc("&x = 1");
    const fsProvider = new GxFileSystemProvider();
    let called = false;
    (fsProvider as any).callMcpTool = async () => {
      called = true;
      return { completion: "should not be reached" };
    };

    const provider = new GxInlineCompletionItemProvider(fsProvider);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    assert.strictEqual(labelsOf(result).length, 0);
    assert.strictEqual(called, false, "AI tool must not be called when the setting is off");
  });

  test("AI path degrades cleanly (no throw, empty ghost text) when genexus_ai_complete reports AiEndpointNotConfigured", async () => {
    // Inside the try, so the restore is always paired with this invocation's
    // write even if the body is interrupted.
    try {
      await setAiEnabled(true);

      const doc = await openDoc("&x = 1");
      const fsProvider = new GxFileSystemProvider();
      (fsProvider as any).callMcpTool = async () => ({
        code: "AiEndpointNotConfigured",
      });

      const provider = new GxInlineCompletionItemProvider(fsProvider);
      const position = new vscode.Position(0, doc.getText().length);

      const result = await provider.provideInlineCompletionItems(
        doc,
        position,
        NO_CONTEXT,
        NOT_CANCELLED,
      );

      assert.strictEqual(labelsOf(result).length, 0);
    } finally {
      await setAiEnabled(false);
    }
  });

  test("AI path emits the completion as ghost text when configured and reachable", async () => {
    try {
      await setAiEnabled(true);

      const doc = await openDoc("&x = 1");
      const fsProvider = new GxFileSystemProvider();
      (fsProvider as any).callMcpTool = async () => ({
        completion: "&y = &x + 1",
      });

      const provider = new GxInlineCompletionItemProvider(fsProvider);
      const position = new vscode.Position(0, doc.getText().length);

      const result = await provider.provideInlineCompletionItems(
        doc,
        position,
        NO_CONTEXT,
        NOT_CANCELLED,
      );

      assert.ok(labelsOf(result).includes("&y = &x + 1"));
    } finally {
      await setAiEnabled(false);
    }
  });

  test("emits nothing when there is no active provider", async () => {
    const doc = await openDoc("&cliente.");
    const provider = new GxInlineCompletionItemProvider(undefined);
    const position = new vscode.Position(0, doc.getText().length);

    const result = await provider.provideInlineCompletionItems(
      doc,
      position,
      NO_CONTEXT,
      NOT_CANCELLED,
    );

    assert.strictEqual(labelsOf(result).length, 0);
  });
});
