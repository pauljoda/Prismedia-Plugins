const SHA256 = /^[a-f0-9]{64}$/;
const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

function compareSemver(left, right) {
  const a = left.split(".").map(Number);
  const b = right.split(".").map(Number);
  for (let index = 0; index < 3; index += 1) {
    if (a[index] !== b[index]) return a[index] - b[index];
  }
  return 0;
}

/**
 * Keeps an unselected plugin's published index row byte-for-byte compatible with its existing zip.
 * Source manifests may already contain future work during a partial build; they must never leak into
 * the index until that plugin is selected and repackaged.
 */
export function preserveUnselectedIndexEntry(existing, pluginId, zipDigest) {
  if (!existing || typeof existing !== "object" || existing.id !== pluginId) {
    throw new Error(`Partial build requires an existing index entry for unselected plugin: ${pluginId}`);
  }
  const expectedPath = `plugins/${pluginId}/${pluginId}.zip`;
  if (existing.path !== expectedPath) {
    throw new Error(`Unselected plugin ${pluginId} has unexpected published path: ${existing.path}`);
  }
  if (typeof existing.sha256 !== "string" || !SHA256.test(existing.sha256)) {
    throw new Error(`Unselected plugin ${pluginId} has no canonical published checksum`);
  }
  if (existing.sha256 !== zipDigest) {
    throw new Error(`Unselected plugin ${pluginId} zip checksum no longer matches index.yml`);
  }
  return existing;
}

/**
 * Prevents a selected plugin from replacing published bytes without advancing its version. Hosts
 * compare semantic versions before downloading, so changing a same-version zip strands existing
 * installations on whichever bytes they cached first.
 */
export function requireVersionBumpForChangedArtifact(existing, manifest, pluginId, zipDigest) {
  if (!existing) return;
  if (existing.id !== pluginId) {
    throw new Error(`Selected plugin ${pluginId} does not match its existing index entry`);
  }
  if (!SEMVER.test(existing.version ?? "") || !SEMVER.test(manifest.version ?? "")) {
    throw new Error(`Selected plugin ${pluginId} requires valid published semantic versions`);
  }
  if (typeof existing.sha256 !== "string" || !SHA256.test(existing.sha256) || !SHA256.test(zipDigest)) {
    throw new Error(`Selected plugin ${pluginId} requires canonical artifact checksums`);
  }

  const comparison = compareSemver(manifest.version, existing.version);
  if (comparison < 0) {
    throw new Error(`Selected plugin ${pluginId} cannot publish an older version`);
  }
  if (zipDigest !== existing.sha256 && comparison === 0) {
    throw new Error(`Selected plugin ${pluginId} changed its artifact without a version bump`);
  }
}
