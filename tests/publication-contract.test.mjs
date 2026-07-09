import assert from "node:assert/strict";
import test from "node:test";

import { preserveUnselectedIndexEntry } from "../scripts/publication-contract.mjs";

const digest = "a".repeat(64);
const existing = {
  id: "tmdb",
  path: "plugins/tmdb/tmdb.zip",
  sha256: digest,
  manifestVersion: 1,
  version: "1.0.0",
};

test("partial publication preserves the exact existing index row", () => {
  assert.equal(preserveUnselectedIndexEntry(existing, "tmdb", digest), existing);
});

test("partial publication rejects a stale unselected zip", () => {
  assert.throws(
    () => preserveUnselectedIndexEntry(existing, "tmdb", "b".repeat(64)),
    /checksum no longer matches/,
  );
});

test("partial publication cannot synthesize an unselected row from a future manifest", () => {
  assert.throws(
    () => preserveUnselectedIndexEntry(undefined, "tmdb", digest),
    /requires an existing index entry/,
  );
});
