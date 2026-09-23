# Internet Archive Comics

Connect this plugin to `https://archive.org/` in Prismedia 3.8 or newer. It searches and browses Archive items tagged as comic books and labeled with a Creative Commons Public Domain Mark or CC0 dedication. Open an item to review its original CBZ files, then import one selected file. It does not present loan-only items, derivative files, CBR, PDFs, or ordinary ZIP archives as direct comic downloads.

An Archive label is uploader-supplied rights information, not a legal determination. The item page, license link, and source credit remain visible during review and on the imported comic. Check rights for your location and use.

The adapter reads the Archive search and metadata APIs, then rechecks the exact file, size, and SHA-1 before returning an anonymous storage URL. Prismedia permits HTTPS artifact URLs only from subdomains of the configured `archive.org` catalog host, sends no credentials to those hosts, pins the final transfer to one exact host, and verifies the file hash and comic pages before import. The plugin does not require an Archive account.

## Development

```sh
dotnet test tests/Prismedia.Plugin.ArchiveOrg.Tests
node scripts/build-plugins.mjs archiveorg
```

Archive API references: [search](https://archive.org/advancedsearch.php), [item metadata](https://archive.org/developers/md-read.html), and [automated access](https://archive.org/developers/bots.html).
