import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import test from "node:test";

import { unzipSync, strFromU8 } from "fflate";
import yaml from "js-yaml";

import { validateManifest } from "../scripts/manifest-contract.mjs";

const root = resolve(".");
const pluginsRoot = join(root, "plugins");
const pluginIds = readdirSync(pluginsRoot).sort();
const index = yaml.load(readFileSync(join(root, "index.yml"), "utf8"));

test("published index, source manifests, and all six zips agree", () => {
  assert.ok(Array.isArray(index));
  assert.deepEqual([...index.map((entry) => entry.id)].sort(), pluginIds);

  for (const pluginId of pluginIds) {
    const pluginDir = join(pluginsRoot, pluginId);
    const manifest = JSON.parse(readFileSync(join(pluginDir, "manifest.json"), "utf8"));
    validateManifest(manifest, pluginId);

    const entry = index.find((candidate) => candidate.id === pluginId);
    assert.ok(entry, `missing index entry for ${pluginId}`);
    const zipBytes = readFileSync(join(pluginDir, `${pluginId}.zip`));
    const digest = createHash("sha256").update(zipBytes).digest("hex");
    assert.equal(entry.sha256, digest, `${pluginId} checksum`);

    const files = unzipSync(zipBytes);
    assert.ok(files["manifest.json"], `${pluginId} zip manifest`);
    assert.ok(files[manifest.entry], `${pluginId} zip entry ${manifest.entry}`);
    const packagedManifest = JSON.parse(strFromU8(files["manifest.json"]));
    assert.deepEqual(packagedManifest, manifest, `${pluginId} packaged manifest`);

    for (const field of ["manifestVersion", "apiTags", "id", "name", "version", "date", "runtime", "isNsfw", "compat", "supports", "execution"]) {
      assert.deepEqual(entry[field], manifest[field], `${pluginId} index field ${field}`);
    }
    assert.equal(entry.path, `plugins/${pluginId}/${pluginId}.zip`);
  }
});
