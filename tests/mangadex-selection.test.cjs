const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const repoRoot = path.resolve(__dirname, "..");
const manifest = require("../plugins/mangadex/manifest.json");

test("MangaDex manifest declares the Prismedia dotnet-process contract", () => {
  assert.equal(manifest.manifestVersion, 1);
  assert.deepEqual(manifest.apiTags, ["prismedia"]);
  assert.equal(manifest.runtime, "dotnet-process");
  assert.equal(manifest.entry, "dist/Prismedia.Plugin.MangaDex.dll");
  assert.deepEqual(manifest.supports, [
    { entityKind: "book", actions: ["lookup-id", "lookup-url", "search", "cascade"] },
    { entityKind: "book-volume", actions: ["lookup-id", "lookup-url", "search"] },
    { entityKind: "book-chapter", actions: ["lookup-id", "lookup-url", "search"] },
  ]);
});

test("MangaDex process returns a Prismedia none result for empty book input", () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "prismedia-mangadex-test-"));
  const requestPath = path.join(tempDir, "request.json");

  fs.writeFileSync(
    requestPath,
    JSON.stringify({
      protocolVersion: 1,
      action: "search",
      auth: {},
      entity: {
        id: "00000000-0000-0000-0000-000000000001",
        kind: "book",
        title: "",
      },
      query: { title: null, url: null, externalIds: null },
      hints: { externalIds: {}, urls: [], title: null, filePath: null },
    }),
  );

  try {
    const dll = path.join(repoRoot, "plugins", "mangadex", manifest.entry);
    const stdout = execFileSync("dotnet", [dll, requestPath], { encoding: "utf8" });
    const response = JSON.parse(stdout);

    assert.equal(response.ok, true);
    assert.equal(response.error, null);
    assert.equal(response.result.type, "none");
    assert.deepEqual(response.result.candidates, []);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});
