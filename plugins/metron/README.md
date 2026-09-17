# Metron

Read-only comic metadata from [Metron](https://metron.cloud/): series runs,
individual issues, creator credits, publisher, publication dates, and covers.
Requires Prismedia 3.8.0 and a Metron account API token.

## Setup and use

Create a token in the API Tokens section of your Metron account, install this
plugin, and save it as **Metron API token** in Prismedia's plugin settings.
Prismedia also accepts the canonical `PRISMEDIA_PLUGIN_METRON_API_TOKEN`
environment variable. The adapter uses bearer authentication only over HTTPS.

Search series by title, start year, run volume, publisher, and language. Search
issues by series title and exact issue designation, with optional run filters.
Search results always require selection, even when the provider returns one match.
A numeric Metron API URL such as `https://metron.cloud/api/issue/50/` can identify
an exact record. Website slug URLs are not supported for lookup; links returned
in metadata retain the provider's website URL.

## Identity and metadata

- `metronseries` identifies a specific series run; `metronissue` identifies an issue.
  Numeric IDs cannot be substituted across these namespaces.
- Issue labels such as `½`, `12.5`, `12A`, and `Annual 1` stay exact. Integer position
  values are ordering hints; they never replace the issue designation or identity.
- Series expansion returns direct issue children. Metron's run-volume number does
  not create a fictional collected-volume entity. Cover variants remain part of the
  provider issue and are not represented as independently identified releases.
- Returned Comic Vine identities retain their resource prefix (`4050-` for series,
  `4000-` for issues). GCD series and issue IDs use separate namespaces. These are
  metadata identities; this plugin resolves only Metron identities.
- Store dates represent publication dates. Cover dates are not substituted for
  availability. Series start years retain year precision. Missing fields stay omitted.
- Creator credits map writing and art roles to Prismedia's corresponding roles;
  other roles use a general person credit. Issue lists contain summary metadata;
  looking up an individual issue supplies its detailed credits and description.
- Covers and metadata do not supply downloadable comic content.

## Bounds and failure behavior

One invocation has a 50-second deadline, an 8 MiB per-response limit, and a 16 MiB
aggregate response budget. Series expansion accepts at most 500 issues and 20
pages; larger or inconsistent lists fail visibly instead of returning a partial
series. Individual issue lookup remains available for larger series.

Requests within an invocation are spaced by at least 3.1 seconds. The adapter
observes burst and sustained quota headers, increases spacing for lower burst
limits, and stops when a quota is exhausted. A 429 is reported with Retry-After
when provided; it is not retried automatically. The manifest declares serialized
invocations with a 3.1-second minimum interval for host lanes that enforce execution
policies. Multiple installations and other clients share the account's upstream quota.

Redirects are disabled. Pagination cannot change origin or resource. Wrong returned
IDs, mismatched parent runs, duplicate/incomplete pages, HTML, oversized bodies,
and authentication failures are explicit errors. A missing exact ID never triggers
a title-search fallback.

## Verification

The adapter's tests use synthetic responses shaped from Metron's current API
serializers and documentation. They cover exact identity, fractional labels, run
selection, pagination, credentials, rate limits, bounded streaming, and deadlines.
Authenticated live catalog validation requires an account token.

Sources: [API documentation](https://github.com/Metron-Project/metron/blob/master/api/README.md),
[serializers](https://github.com/Metron-Project/metron/tree/master/api/v1_0/serializers),
[rate limits](https://github.com/Metron-Project/metron/blob/master/api/RATELIMIT.md).
