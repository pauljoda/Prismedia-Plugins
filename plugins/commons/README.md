# Wikimedia Commons

Search the public Wikimedia Commons catalog and import original JPEG, PNG, and WebP
still images through Prismedia's verified catalog importer. No account or API key
is required.

## Connection

1. Install and enable **Wikimedia Commons**.
2. Add a Connection with URL `https://commons.wikimedia.org` and enable catalog
   discovery and acquisition source capabilities.
3. Test the connection, then open **Requests → Browse catalogs** and search.
4. Choose a writable image library, review **Source attribution**, and select an
   original image offer.

The API URL `https://commons.wikimedia.org/w/api.php` is also accepted. This adapter
is specific to the public Commons service. It does not accept arbitrary MediaWiki
servers, credentials, source execution, or external library management.

## Selection and attribution

- Search pages request at most ten results with image metadata. Unsupported formats,
  files larger than 64 MiB, images above 100 million pixels, and files without exact
  revision/checksum evidence are omitted.
- Selections retain the Commons page ID, upload timestamp, and published SHA-1.
  A changed file version requires a new selection. Prismedia verifies the source
  checksum and stores its own SHA-256 evidence for import and recovery.
- Original bytes come only from the declared anonymous HTTPS file origin
  `https://upload.wikimedia.org`. Tracking query parameters are removed. Requests
  use no authentication headers or cookies, and redirects cannot leave that origin.
- Creator, credit, source page, license name/link, usage terms, and the source's
  attribution requirement are normalized to plain text. The host retains these
  source statements in the accepted encrypted import record and displays them in
  **Recent imports → Source attribution**. They do not overwrite curated metadata.
- License and authorship statements are supplied by Commons. Missing fields stay
  absent; the plugin does not invent a creator or license. Open the source page for
  its full current description.
- Search does not add a content-rating filter. SVG, GIF, PDFs, videos, audio, and
  animated outputs are outside this still-image importer.

## Protocol references

- [Commons API](https://commons.wikimedia.org/wiki/Commons:API)
- [MediaWiki imageinfo](https://www.mediawiki.org/wiki/API:Imageinfo)
- [MediaWiki search](https://www.mediawiki.org/wiki/API:Search)

The plugin uses bounded anonymous Action API GET requests with `maxlag`, an
identifying User-Agent, and declared invocation pacing. Upstream errors and rate
limits leave the operation retryable without exposing response bodies.
