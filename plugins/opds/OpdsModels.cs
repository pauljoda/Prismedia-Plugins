using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Opds;

internal sealed record OpdsDocument(string Title, IReadOnlyList<OpdsEntry> Entries, Uri? Next = null, OpdsSearch? Search = null);
internal sealed record OpdsEntry(CatalogItem Item, IReadOnlyDictionary<string, OpdsAcquisition> Acquisitions);
internal sealed record OpdsAcquisition(Uri Url, string? MediaType);
internal sealed record OpdsSearch(Uri Url, bool IsDescription);
