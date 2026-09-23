import { isIP } from "node:net";

const VALID_ACTIONS = new Set(["lookup-id", "lookup-url", "search"]);
const VALID_FIELD_TYPES = new Set(["text", "number", "year"]);
const VALID_ENTITY_KINDS = new Set([
  "audio",
  "audio-library",
  "audio-track",
  "book",
  "book-author",
  "book-chapter",
  "book-volume",
  "collection",
  "comic-installment",
  "comic-series",
  "comic-volume",
  "gallery",
  "image",
  "movie",
  "music-artist",
  "person",
  "studio",
  "tag",
  "video",
  "video-episode",
  "video-season",
  "video-series",
]);

const IDENTITY_NAMESPACE = /^[a-z0-9][a-z0-9._-]*$/;
const IDENTITY_URL_TOKEN = /^[A-Za-z][A-Za-z0-9._-]*$/;
const SEARCH_FIELD_KEY = /^[A-Za-z][A-Za-z0-9._-]*$/;
const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
const ENTRY_PATH = /^dist\/[A-Za-z0-9._-]+\.dll$/;
const MAX_IDENTITY_VALUE_PATTERN_LENGTH = 512;
const MAX_IDENTITY_URL_TEMPLATE_LENGTH = 2048;

function requireObject(value, field, pluginId) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${pluginId} manifest requires an object at ${field}`);
  }
  return value;
}

function requireString(value, field, pluginId) {
  if (typeof value !== "string" || value.length === 0 || value.trim() !== value) {
    throw new Error(`${pluginId} manifest requires a non-empty trimmed string at ${field}`);
  }
  return value;
}

function requireArray(value, field, pluginId, { allowEmpty = false } = {}) {
  if (!Array.isArray(value) || (!allowEmpty && value.length === 0)) {
    throw new Error(`${pluginId} manifest requires ${allowEmpty ? "an" : "a non-empty"} ${field} array`);
  }
  return value;
}

function requireUnique(values, field, pluginId) {
  if (new Set(values).size !== values.length) {
    throw new Error(`${pluginId} manifest contains duplicate ${field}`);
  }
}

function requireSemver(value, field, pluginId) {
  const version = requireString(value, field, pluginId);
  if (!SEMVER.test(version)) throw new Error(`${pluginId} manifest has invalid semantic version at ${field}`);
  return version;
}

function semverParts(value) {
  return value.split(".").map(Number);
}

function compareSemver(left, right) {
  const a = semverParts(left);
  const b = semverParts(right);
  for (let index = 0; index < 3; index += 1) {
    if (a[index] !== b[index]) return a[index] - b[index];
  }
  return 0;
}

function optionalSemver(value, field, pluginId) {
  return value === null ? null : requireSemver(value, field, pluginId);
}

function validateCompatibility(value, pluginId) {
  const compat = requireObject(value, "compat", pluginId);
  const pluginApiMin = requireSemver(compat.pluginApiMin, "compat.pluginApiMin", pluginId);
  const pluginApiMax = optionalSemver(compat.pluginApiMax, "compat.pluginApiMax", pluginId);
  const prismediaMin = requireSemver(compat.prismediaMin, "compat.prismediaMin", pluginId);
  const prismediaMax = optionalSemver(compat.prismediaMax, "compat.prismediaMax", pluginId);
  if (compareSemver(pluginApiMin, "2.0.0") < 0) {
    throw new Error(`${pluginId} manifestVersion 2 requires compat.pluginApiMin >= 2.0.0`);
  }
  if (pluginApiMax !== null && compareSemver(pluginApiMin, pluginApiMax) > 0) {
    throw new Error(`${pluginId} manifest plugin API compatibility range is inverted`);
  }
  if (prismediaMax !== null && compareSemver(prismediaMin, prismediaMax) > 0) {
    throw new Error(`${pluginId} manifest Prismedia compatibility range is inverted`);
  }
}

function validateAuth(value, pluginId) {
  const auth = requireArray(value, "auth", pluginId, { allowEmpty: true });
  requireUnique(auth.map((field) => field?.key), "auth keys", pluginId);
  for (const field of auth) {
    requireObject(field, "auth[]", pluginId);
    requireString(field.key, "auth[].key", pluginId);
    requireString(field.label, "auth[].label", pluginId);
    if (typeof field.required !== "boolean") {
      throw new Error(`${pluginId} manifest auth field '${field.key}' requires a boolean required flag`);
    }
    if (field.url !== null && field.url !== undefined) requireString(field.url, `auth[${field.key}].url`, pluginId);
  }
}

function validateSearch(search, pluginId, entityKind) {
  const fields = requireArray(search?.fields, `supports[${entityKind}].search.fields`, pluginId);
  requireUnique(fields.map((field) => String(field?.key ?? "").toLowerCase()), `search fields for ${entityKind}`, pluginId);
  for (const field of fields) {
    if (!field || !SEARCH_FIELD_KEY.test(field.key ?? "")) {
      throw new Error(`${pluginId} ${entityKind} search field has an invalid key`);
    }
    requireString(field.label, `supports[${entityKind}].search.fields[${field.key}].label`, pluginId);
    if (!VALID_FIELD_TYPES.has(field.type) || typeof field.required !== "boolean") {
      throw new Error(`${pluginId} ${entityKind} search field '${field.key}' has an invalid type or required flag`);
    }
    for (const optional of ["placeholder", "help"]) {
      if (field[optional] !== undefined && field[optional] !== null) {
        requireString(field[optional], `supports[${entityKind}].search.fields[${field.key}].${optional}`, pluginId);
      }
    }
  }
}

function parseIdentityUrlTemplate(value, field, pluginId, {
  maximumLength,
  rejectAdjacentTokens = false,
  rejectDuplicateTokens = false,
} = {}) {
  const template = requireString(value, field, pluginId);
  if (template.length > maximumLength) {
    throw new Error(`${pluginId} manifest ${field} is too long`);
  }

  const parts = [];
  const tokens = new Set();
  let literalStart = 0;
  for (let index = 0; index < template.length; index += 1) {
    const character = template[index];
    if (character === "}") {
      throw new Error(`${pluginId} manifest has an invalid ${field}`);
    }
    if (character !== "{") continue;

    if (index > literalStart) parts.push({ token: false, value: template.slice(literalStart, index) });
    const close = template.indexOf("}", index + 1);
    const nested = template.indexOf("{", index + 1);
    if (close < 0 || (nested >= 0 && nested < close)) {
      throw new Error(`${pluginId} manifest has an invalid ${field}`);
    }

    const token = template.slice(index + 1, close);
    const duplicate = tokens.has(token);
    const adjacent = parts.at(-1)?.token === true;
    if (!IDENTITY_URL_TOKEN.test(token) ||
        (rejectDuplicateTokens && duplicate) ||
        (rejectAdjacentTokens && adjacent)) {
      throw new Error(`${pluginId} manifest has an invalid ${field}`);
    }

    tokens.add(token);
    parts.push({ token: true, value: token });
    index = close;
    literalStart = close + 1;
  }

  if (literalStart < template.length) parts.push({ token: false, value: template.slice(literalStart) });
  if (tokens.size === 0) throw new Error(`${pluginId} manifest ${field} requires a placeholder`);
  return { parts, tokens };
}

function validateIdentityUrls(value, namespaces, pluginId, entityKind) {
  if (value === undefined || value === null) return;

  const field = `supports[${entityKind}].identityUrls`;
  const identityUrls = requireArray(value, field, pluginId, { allowEmpty: true });
  requireUnique(identityUrls.map((format) => format?.identityNamespace), `identity URL namespaces for ${entityKind}`, pluginId);

  for (const format of identityUrls) {
    requireObject(format, `${field}[]`, pluginId);
    const identityNamespace = requireString(format.identityNamespace, `${field}[].identity URL namespace`, pluginId);
    if (!IDENTITY_NAMESPACE.test(identityNamespace) || !namespaces.includes(identityNamespace)) {
      throw new Error(`${pluginId} ${entityKind} identity URL namespace must be canonical and declared by the same support`);
    }

    const valuePattern = parseIdentityUrlTemplate(
      format.valuePattern,
      `${field}[${identityNamespace}].identity URL value pattern`,
      pluginId,
      {
        maximumLength: MAX_IDENTITY_VALUE_PATTERN_LENGTH,
        rejectAdjacentTokens: true,
        rejectDuplicateTokens: true,
      },
    );
    const urlTemplate = parseIdentityUrlTemplate(
      format.urlTemplate,
      `${field}[${identityNamespace}].identity URL template`,
      pluginId,
      { maximumLength: MAX_IDENTITY_URL_TEMPLATE_LENGTH },
    );

    if ([...urlTemplate.tokens].some((token) => !valuePattern.tokens.has(token))) {
      throw new Error(`${pluginId} ${entityKind} identity URL template references an unknown placeholder`);
    }
    if ([...valuePattern.tokens].some((token) => !urlTemplate.tokens.has(token))) {
      throw new Error(`${pluginId} ${entityKind} identity URL template omits an identity placeholder`);
    }

    const sampleUrl = urlTemplate.parts
      .map((part) => part.token ? "sample" : part.value)
      .join("");
    let parsedUrl;
    try {
      parsedUrl = new URL(sampleUrl);
    } catch {
      throw new Error(`${pluginId} ${entityKind} identity URL template must be an absolute HTTPS URL`);
    }
    if (parsedUrl.protocol !== "https:" ||
        parsedUrl.hostname.length === 0 ||
        parsedUrl.username.length > 0 ||
        parsedUrl.password.length > 0) {
      throw new Error(`${pluginId} ${entityKind} identity URL template must be an absolute HTTPS URL without credentials`);
    }
  }
}

const INTEGRATION_OPERATIONS = {
  "catalog-discovery": ["search", "browse", "inspect"],
  "acquisition-source": ["resolve", "request-source", "observe-source"],
  "transfer-executor": ["submit", "find-submission", "cancel-submission", "get-job", "cancel", "list-artifacts", "authorize-artifact", "renew-retention", "acknowledge"],
  "external-manager": ["manager-options", "discover-managed", "lookup-managed", "ensure-managed", "request-managed", "configure-managed", "reconcile-managed", "inspect-managed-release"],
  "connected-library": ["search-library", "get-library-item", "list-libraries"],
};

function validateIntegration(integration, auth, pluginId) {
  requireObject(integration, "integration", pluginId);
  if (integration.protocolVersion !== 1) throw new Error(`${pluginId} integration protocolVersion must be 1`);
  const capabilities = requireArray(integration.capabilities, "integration.capabilities", pluginId);
  if (capabilities.length > 8) throw new Error(`${pluginId} has too many integration capabilities`);
  requireUnique(capabilities.map(item => item.kind), "integration capabilities", pluginId);
  for (const capability of capabilities) {
    const allowed = INTEGRATION_OPERATIONS[capability.kind];
    if (!allowed) throw new Error(`${pluginId} declares an unknown integration capability`);
    const operations = requireArray(capability.operations, "integration operations", pluginId);
    requireUnique(operations, "integration operations", pluginId);
    if (operations.some(operation => !allowed.includes(operation))) throw new Error(`${pluginId} declares an operation outside its capability`);
    const kinds = requireArray(capability.entityKinds, "integration entityKinds", pluginId);
    requireUnique(kinds, "integration entityKinds", pluginId);
    if (kinds.some(kind => !VALID_ENTITY_KINDS.has(kind))) throw new Error(`${pluginId} declares an unknown integration entity kind`);
  }
  const origins = integration.anonymousArtifactOrigins ?? [];
  const invalidOrigins = () => new Error(`${pluginId} anonymous artifact origins must be unique bounded HTTPS origins for acquisition sources`);
  if (!Array.isArray(origins) || origins.length > 8) throw invalidOrigins();
  if (origins.length && !capabilities.some(item => item.kind === "acquisition-source" && item.operations.includes("resolve"))) throw invalidOrigins();
  const normalizedOrigins = new Set();
  for (const value of origins) {
    if (typeof value !== "string" || value.length > 2048 || !/^https:\/\//iu.test(value) || /[\s\p{Cc}\\?#]/u.test(value)) throw invalidOrigins();
    let url;
    try { url = new URL(value); } catch { throw invalidOrigins(); }
    const pathStart = value.indexOf("/", value.indexOf("://") + 3);
    if (url.protocol !== "https:" || !url.hostname || url.username || url.password
      || (pathStart >= 0 && value.slice(pathStart) !== "/") || normalizedOrigins.has(url.origin)) throw invalidOrigins();
    normalizedOrigins.add(url.origin);
  }
  const suffixes = integration.anonymousArtifactHostSuffixes ?? [];
  const invalidSuffixes = () => new Error(`${pluginId} anonymous artifact host suffixes must be unique bounded DNS hosts for acquisition sources`);
  if (!Array.isArray(suffixes) || suffixes.length + origins.length > 8) throw invalidSuffixes();
  if (suffixes.length && !capabilities.some(item => item.kind === "acquisition-source" && item.operations.includes("resolve"))) throw invalidSuffixes();
  const normalizedSuffixes = new Set();
  for (const value of suffixes) {
    if (typeof value !== "string" || value.length > 253 || isIP(value)
      || !value.includes(".") || !value.split(".").every(label => label.length > 0 && label.length <= 63
        && /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/iu.test(label))
      || normalizedSuffixes.has(value.toLowerCase())) throw invalidSuffixes();
    normalizedSuffixes.add(value.toLowerCase());
  }
  const settings = requireArray(integration.settings, "integration.settings", pluginId, { allowEmpty: true });
  if (settings.length > 32) throw new Error(`${pluginId} has too many connection settings`);
  if (settings.length) validateSearch({ fields: settings }, pluginId, "connection");
  if (settings.some(field => auth.some(credential => credential.key === field.key)))
    throw new Error(`${pluginId} cannot declare a credential as a nonsecret setting`);
}

function validateTopLevel(manifest, directoryId) {
  requireObject(manifest, "root", directoryId);
  const pluginId = requireString(manifest.id, "id", directoryId);
  if (!IDENTITY_NAMESPACE.test(pluginId)) throw new Error(`${pluginId} manifest id is not canonical lowercase`);
  if (pluginId !== directoryId) {
    throw new Error(`Plugin directory '${directoryId}' contains manifest id '${pluginId}'`);
  }
  if (manifest.manifestVersion !== 2) throw new Error(`${pluginId} manifestVersion must be 2`);
  requireString(manifest.name, "name", pluginId);
  const icon = requireString(manifest.icon, "icon", pluginId);
  if (icon.length > 256 || icon.includes("\\") || icon.startsWith("/") ||
      icon.split("/").some((segment) => !segment || segment === "." || segment === "..") ||
      !/\.(svg|png)$/i.test(icon)) {
    throw new Error(`${pluginId} manifest icon must be a contained package-relative SVG or PNG path`);
  }
  requireSemver(manifest.version, "version", pluginId);
  const date = requireString(manifest.date, "date", pluginId);
  const parsedDate = new Date(`${date}T00:00:00Z`);
  if (!ISO_DATE.test(date) || Number.isNaN(parsedDate.valueOf()) || parsedDate.toISOString().slice(0, 10) !== date) {
    throw new Error(`${pluginId} manifest date must be a real YYYY-MM-DD date`);
  }
  if (requireString(manifest.runtime, "runtime", pluginId) !== "dotnet-process") {
    throw new Error(`${pluginId} manifest runtime must be dotnet-process`);
  }
  const entry = requireString(manifest.entry, "entry", pluginId);
  if (!ENTRY_PATH.test(entry)) throw new Error(`${pluginId} manifest entry must be a DLL below dist/`);
  const apiTags = requireArray(manifest.apiTags, "apiTags", pluginId);
  if (apiTags.some((tag) => typeof tag !== "string" || tag.trim() !== tag || tag.length === 0) ||
      !apiTags.includes("prismedia")) {
    throw new Error(`${pluginId} manifest apiTags must contain canonical 'prismedia'`);
  }
  requireUnique(apiTags, "apiTags", pluginId);
  if (typeof manifest.isNsfw !== "boolean") throw new Error(`${pluginId} manifest requires boolean isNsfw`);
  validateCompatibility(manifest.compat, pluginId);
  validateAuth(manifest.auth, pluginId);
  if (manifest.execution !== undefined) {
    const execution = requireObject(manifest.execution, "execution", pluginId);
    if (!Number.isInteger(execution.maxConcurrentInvocations)
        || execution.maxConcurrentInvocations < 1
        || execution.maxConcurrentInvocations > 64) {
      throw new Error(`${pluginId} manifest execution.maxConcurrentInvocations must be an integer from 1 through 64`);
    }
    if (!Number.isInteger(execution.minimumStartIntervalMs)
        || execution.minimumStartIntervalMs < 0
        || execution.minimumStartIntervalMs > 86_400_000) {
      throw new Error(`${pluginId} manifest execution.minimumStartIntervalMs must be an integer from 0 through 86400000`);
    }
  }
  return pluginId;
}

export function validateManifest(manifest, directoryId = manifest?.id ?? "unknown") {
  const pluginId = validateTopLevel(manifest, directoryId);
  if (manifest.integration != null) validateIntegration(manifest.integration, manifest.auth, pluginId);
  const supports = requireArray(manifest.supports, "supports", pluginId, { allowEmpty: Boolean(manifest.integration) });
  requireUnique(supports.map((support) => support?.entityKind), "entity kind declarations", pluginId);
  for (const support of supports) {
    const kind = support?.entityKind;
    if (!VALID_ENTITY_KINDS.has(kind)) throw new Error(`${pluginId} manifest declares unknown entity kind '${kind}'`);

    const actions = requireArray(support.actions, `supports[${kind}].actions`, pluginId);
    requireUnique(actions, `actions for ${kind}`, pluginId);
    if (actions.some((action) => !VALID_ACTIONS.has(action))) {
      throw new Error(`${pluginId} ${kind} declares an unknown action`);
    }

    const namespaces = requireArray(support.identityNamespaces, `supports[${kind}].identityNamespaces`, pluginId);
    requireUnique(namespaces, `identity namespaces for ${kind}`, pluginId);
    if (namespaces.some((identityNamespace) => !IDENTITY_NAMESPACE.test(identityNamespace))) {
      throw new Error(`${pluginId} ${kind} declares a non-canonical identity namespace`);
    }
    validateIdentityUrls(support.identityUrls, namespaces, pluginId, kind);

    const searchable = actions.includes("search");
    if (searchable && !actions.includes("lookup-id")) {
      throw new Error(`${pluginId} ${kind} search requires lookup-id so selected candidates can round-trip`);
    }
    if (searchable !== Boolean(support.search)) {
      throw new Error(`${pluginId} ${kind} must declare search fields exactly when search is supported`);
    }
    if (searchable) validateSearch(support.search, pluginId, kind);
  }

  return manifest;
}
