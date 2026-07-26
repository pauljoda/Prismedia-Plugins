#!/usr/bin/env node
/**
 * Build distributable plugin `.zip` artifacts and regenerate `index.yml`
 * from each plugin manifest. The index remains only a remote discovery
 * catalog: manifest.json is the source of truth for plugin metadata.
 *
 * Usage: node scripts/build-plugins.mjs [plugin-id ...]
 */

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, existsSync, readdirSync, statSync, rmSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { zipSync } from "fflate";
import yaml from "js-yaml";
import { validateManifest } from "./manifest-contract.mjs";
import { preserveUnselectedIndexEntry } from "./publication-contract.mjs";

const __filename = fileURLToPath(import.meta.url);
const repoRoot = resolve(dirname(__filename), "..");
const pluginsDir = join(repoRoot, "plugins");
const indexPath = join(repoRoot, "index.yml");
const requestedIds = new Set(process.argv.slice(2).map((id) => id.trim()).filter(Boolean));

function sha256(buf) {
  return createHash("sha256").update(buf).digest("hex");
}

function walk(dir, prefix = "") {
  const out = [];
  for (const name of readdirSync(dir)) {
    if (name === "node_modules" || name === "bin" || name === "obj" || name.startsWith(".")) continue;
    if (name.endsWith(".zip")) continue;
    const full = join(dir, name);
    const rel = prefix ? `${prefix}/${name}` : name;
    const st = statSync(full);
    if (st.isDirectory()) {
      out.push(...walk(full, rel));
    } else if (st.isFile()) {
      out.push({ rel, full });
    }
  }
  return out;
}

function loadManifest(pluginDir) {
  const path = join(pluginDir, "manifest.json");
  if (!existsSync(path)) return null;
  return JSON.parse(readFileSync(path, "utf8"));
}

function buildDotnet(pluginDir) {
  const project = readdirSync(pluginDir).find((name) => name.endsWith(".csproj"));
  if (!project) {
    throw new Error(`dotnet-process plugin is missing a .csproj: ${pluginDir}`);
  }
  const distDir = join(pluginDir, "dist");
  if (existsSync(distDir)) rmSync(distDir, { recursive: true, force: true });
  const res = spawnSync(
    "dotnet",
    ["publish", project, "-c", "Release", "-o", "dist", "/p:UseAppHost=false"],
    { cwd: pluginDir, stdio: "inherit" },
  );
  if (res.status !== 0) {
    throw new Error(`dotnet publish failed for ${pluginDir}`);
  }
}

function zipPlugin(pluginDir) {
  const files = walk(pluginDir);
  const bundle = {};
  for (const { rel, full } of files) {
    bundle[rel] = new Uint8Array(readFileSync(full));
  }
  return Buffer.from(zipSync(bundle, { level: 9 }));
}

function loadExistingIndex() {
  if (!existsSync(indexPath)) return [];
  const parsed = yaml.load(readFileSync(indexPath, "utf8"));
  if (!Array.isArray(parsed)) {
    throw new Error("index.yml must be a YAML list");
  }
  return parsed;
}

function discoverPluginIds() {
  return readdirSync(pluginsDir)
    .filter((name) => {
      const pluginDir = join(pluginsDir, name);
      return statSync(pluginDir).isDirectory();
    })
    .sort((a, b) => a.localeCompare(b));
}

function requireString(value, field, id) {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`${id} manifest is missing required string field: ${field}`);
  }
  return value;
}

function optionalArray(value) {
  return Array.isArray(value) ? value : [];
}

function indexEntryFromManifest(manifest, id, digest) {
  if (manifest.id !== id) {
    throw new Error(`Plugin directory '${id}' contains manifest id '${manifest.id}'`);
  }

  const entry = {
    id: requireString(manifest.id, "id", id),
    name: requireString(manifest.name, "name", id),
    version: requireString(manifest.version, "version", id),
    date: requireString(manifest.date, "date", id),
    path: `plugins/${id}/${id}.zip`,
    sha256: digest,
    runtime: requireString(manifest.runtime, "runtime", id),
    isNsfw: Boolean(manifest.isNsfw ?? false),
    manifestVersion: Number(manifest.manifestVersion ?? 1),
    apiTags: optionalArray(manifest.apiTags),
    compat: manifest.compat,
    supports: optionalArray(manifest.supports),
  };

  if (manifest.execution !== undefined) {
    entry.execution = manifest.execution;
  }

  for (const key of ["description", "author", "capabilities"]) {
    if (manifest[key] !== undefined) {
      entry[key] = manifest[key];
    }
  }

  return entry;
}

const existingIndex = loadExistingIndex();
const existingById = new Map(existingIndex.map((entry) => [String(entry.id), entry]));
const discoveredIds = discoverPluginIds();
const knownIds = new Set(discoveredIds);

if (requestedIds.size > 0) {
  for (const id of requestedIds) {
    if (!knownIds.has(id)) {
      throw new Error(`Unknown plugin id: ${id}`);
    }
  }
}

const orderedIds = [
  ...existingIndex.map((entry) => String(entry.id)).filter((id) => knownIds.has(id)),
  ...discoveredIds.filter((id) => !existingById.has(id)),
];
const manifestsById = new Map();
const preservedById = new Map();

// Preflight the whole publication before mutating any dist directory or zip. A malformed source
// manifest or stale unselected artifact therefore fails without leaving a half-updated package set.
for (const id of orderedIds) {
  const pluginDir = join(pluginsDir, id);
  const manifest = loadManifest(pluginDir);
  if (!manifest) throw new Error(`Plugin is missing manifest.json: ${id}`);
  validateManifest(manifest, id);
  if (manifest.runtime !== "dotnet-process") throw new Error(`unsupported runtime for ${id}: ${manifest.runtime}`);
  manifestsById.set(id, manifest);

  if (requestedIds.size > 0 && !requestedIds.has(id)) {
    const zipPath = join(pluginDir, `${id}.zip`);
    if (!existsSync(zipPath)) {
      throw new Error(`Partial build requires the existing zip for unselected plugin: ${id}`);
    }
    const digest = sha256(readFileSync(zipPath));
    preservedById.set(id, preserveUnselectedIndexEntry(existingById.get(id), id, digest));
  }
}

const index = [];

for (const id of orderedIds) {
  const pluginDir = join(pluginsDir, id);
  const manifest = manifestsById.get(id);

  const zipPath = join(pluginDir, `${id}.zip`);
  if (requestedIds.size > 0 && !requestedIds.has(id)) {
    const preserved = preservedById.get(id);
    index.push(preserved);
    console.log(`preserved ${id} from its existing index entry (${preserved.sha256.slice(0, 12)}...)`);
    continue;
  }

  {
    buildDotnet(pluginDir);

    const zipBuf = zipPlugin(pluginDir);
    writeFileSync(zipPath, zipBuf);
    const digest = sha256(zipBuf);

    console.log(
      `built ${id} v${manifest.version} (${zipBuf.length} bytes, sha256=${digest.slice(0, 12)}...)`,
    );
    index.push(indexEntryFromManifest(manifest, id, digest));
  }
}

const dumped = yaml.dump(index, { lineWidth: 200 });
writeFileSync(indexPath, `# Prismedia Community Plugins Index\n# This file is fetched by Prismedia to discover available plugins.\n\n${dumped}`);

console.log("\nindex.yml regenerated from plugin manifests.");
console.log("Commit the updated index + plugins/*/<id>.zip to publish.");
