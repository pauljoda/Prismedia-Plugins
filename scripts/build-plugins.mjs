#!/usr/bin/env node
/**
 * Build a distributable `.zip` for every plugin in `plugins/` and
 * write a sha256 checksum back into `index.yml` so Prismedia clients
 * can verify downloads.
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

const index = yaml.load(readFileSync(indexPath, "utf8"));
if (!Array.isArray(index)) {
  throw new Error("index.yml must be a YAML list");
}

if (requestedIds.size > 0) {
  const knownIds = new Set(index.map((entry) => String(entry.id)));
  for (const id of requestedIds) {
    if (!knownIds.has(id)) {
      throw new Error(`Unknown plugin id: ${id}`);
    }
  }
}

for (const entry of index) {
  const id = String(entry.id);
  if (requestedIds.size > 0 && !requestedIds.has(id)) {
    continue;
  }

  const pluginDir = join(pluginsDir, id);
  if (!existsSync(pluginDir)) {
    console.warn(`skip ${id}: ${pluginDir} missing`);
    continue;
  }
  const manifest = loadManifest(pluginDir);
  if (!manifest) {
    console.warn(`skip ${id}: no manifest.json`);
    continue;
  }

  if (manifest.runtime !== "dotnet-process") {
    throw new Error(`unsupported runtime for ${id}: ${manifest.runtime}`);
  }
  buildDotnet(pluginDir);

  const zipBuf = zipPlugin(pluginDir);
  const zipPath = join(pluginDir, `${id}.zip`);
  writeFileSync(zipPath, zipBuf);
  const digest = sha256(zipBuf);

  entry.sha256 = digest;
  entry.version = String(manifest.version ?? entry.version);
  entry.runtime = String(manifest.runtime ?? entry.runtime);
  if (manifest.manifestVersion) {
    entry.manifestVersion = manifest.manifestVersion;
    entry.apiTags = manifest.apiTags ?? [];
    entry.compat = manifest.compat;
    entry.supports = manifest.supports;
    entry.isNsfw = Boolean(manifest.isNsfw ?? entry.isNsfw);
  }

  console.log(
    `built ${id} v${entry.version} (${zipBuf.length} bytes, sha256=${digest.slice(0, 12)}…)`,
  );
}

const dumped = yaml.dump(index, { lineWidth: 200 });
writeFileSync(indexPath, `# Prismedia Community Plugins Index\n# This file is fetched by Prismedia to discover available plugins.\n\n${dumped}`);

console.log("\nindex.yml updated with fresh sha256 + versions.");
console.log("Commit the updated index + plugins/*/<id>.zip to publish.");
