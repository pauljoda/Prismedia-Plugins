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

const liveTest = process.env.PRISMEDIA_LIVE_PLUGIN_TESTS === "1" ? test : test.skip;

liveTest("MangaDex process accepts Bad Ending Party when aggregate volumes is empty", { timeout: 30_000 }, () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "prismedia-mangadex-live-test-"));
  const requestPath = path.join(tempDir, "request.json");

  fs.writeFileSync(
    requestPath,
    JSON.stringify({
      protocolVersion: 1,
      action: "lookup-id",
      auth: {},
      entity: {
        id: "00000000-0000-0000-0000-000000000001",
        kind: "book",
        title: "Bad Ending Party",
      },
      query: {
        title: "Bad Ending Party",
        url: null,
        externalIds: { mangadex: "a95830f3-a5a4-47b7-8163-9c3c0ba0b14b" },
      },
      hints: { externalIds: {}, urls: [], title: null, filePath: null },
      includeNsfw: true,
    }),
  );

  try {
    const dll = path.join(repoRoot, "plugins", "mangadex", manifest.entry);
    const stdout = execFileSync("dotnet", [dll, requestPath], { encoding: "utf8" });
    const response = JSON.parse(stdout);

    assert.equal(response.ok, true);
    assert.equal(response.error, null);
    assert.equal(response.result.type, "proposal");
    assert.equal(response.result.proposal.patch.title, "Bad Ending Party");
    assert.equal(response.result.proposal.patch.flags.isNsfw, true);
    assert.ok(response.result.proposal.images.length > 0);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

liveTest("MangaDex process hydrates cover-only volumes with matching feed chapters", { timeout: 30_000 }, () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "prismedia-mangadex-live-test-"));
  const requestPath = path.join(tempDir, "request.json");

  fs.writeFileSync(
    requestPath,
    JSON.stringify({
      protocolVersion: 1,
      action: "lookup-id",
      auth: {},
      entity: {
        id: "00000000-0000-0000-0000-000000000001",
        kind: "book",
        title: "Blonde Gal with Huge Tits Treats Me Like a Manslut",
      },
      query: {
        title: "Blonde Gal with Huge Tits Treats Me Like a Manslut",
        url: null,
        externalIds: { mangadex: "a27a0cc2-5cc8-4247-b305-3019f493a40f" },
      },
      hints: { externalIds: {}, urls: [], title: null, filePath: null },
      includeNsfw: true,
    }),
  );

  try {
    const dll = path.join(repoRoot, "plugins", "mangadex", manifest.entry);
    const stdout = execFileSync("dotnet", [dll, requestPath], { encoding: "utf8" });
    const response = JSON.parse(stdout);

    assert.equal(response.ok, true);
    assert.equal(response.error, null);
    assert.equal(response.result.type, "proposal");

    const proposal = response.result.proposal;
    assert.deepEqual(proposal.children.map((child) => child.patch.title), ["Volume 1", "Volume 2"]);
    assert.equal(proposal.children.some((child) => child.patch.title === "Volume none"), false);

    for (const [index, volume] of proposal.children.entries()) {
      assert.equal(volume.images.length, 1);
      assert.equal(volume.patch.stats.chapterCount, 1);
      assert.equal(volume.patch.stats.pageCount, 33);
      assert.match(volume.patch.description, new RegExp(`Chapter ${index + 1}`));

      const chapter = volume.children[0];
      assert.ok(chapter);
      assert.equal(chapter.images.length, 1);
      assert.equal(chapter.patch.stats.pageCount, 33);
      assert.match(chapter.patch.description, /English translation/);
    }
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});
