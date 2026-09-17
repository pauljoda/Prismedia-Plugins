using System.Globalization;

internal static partial class AniListPlugin {
    private static class MangaCodes {
        public const string Kind = "comic-series";
        public const string Type = "MANGA";
        public const string OneShot = "ONE_SHOT";
        public const string Creator = "creator";
        public const string Published = "published";
        public const string Ended = "ended";
        public const string ChapterCount = "chapterCount";
        public const string VolumeCount = "volumeCount";
        public const string Poster = "poster";
        public const string Backdrop = "backdrop";
        public const string LookupId = "lookup-id";
        public const string LookupUrl = "lookup-url";
        public const string Reason = "external-id";
        public const string Story = "Story";
        public const string Art = "Art";
        public const string StoryAndArt = "Story & Art";
        public const string OriginalStory = "Original Story";
    }

    /// <summary>Identifies a manga work without assigning its identity to a particular edition or synthesizing installments.</summary>
    private static async Task<IdentifyPluginResult> IdentifyMangaAsync(IdentifyPluginRequest request) {
        var id = MangaIdentity(request);
        if (!IsExplicitSearch(request)) {
            if (id is { } exactId) {
                var data = await GraphQlAsync<DetailData>(MangaDetailQuery, new { id = exactId });
                return data.Media is { } media && media.Id == exactId && IsManga(media, request.IncludeNsfw)
                    ? IdentifyPluginResult.ForProposal(MangaProposal(media, request.Entity.Id))
                    : IdentifyPluginResult.None();
            }
            if (request.Action is MangaCodes.LookupId or MangaCodes.LookupUrl ||
                request.Query.ExternalIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.Query.Url)) return IdentifyPluginResult.None();
        }
        var (title, year) = SearchInput(request);
        if (string.IsNullOrWhiteSpace(title)) return IdentifyPluginResult.None();
        var dataSearch = await GraphQlAsync<SearchData>(MangaSearchQuery(request.IncludeNsfw), new {
            search = title, year = year is { } y ? y.ToString(CultureInfo.InvariantCulture) + "%" : null,
            perPage = SearchLimit(request)
        });
        var candidates = (dataSearch.Page?.Media ?? []).Where(media => IsManga(media, request.IncludeNsfw) &&
            (year is null || media.StartDate?.Year == year)).DistinctBy(media => media.Id).Take(SearchLimit(request))
            .Select(media => new EntitySearchCandidate(new Dictionary<string, string> { [PrimaryIdentityNamespace] = MediaId(media.Id) },
                Title(media), Year(media.StartDate), StripHtml(media.Description), media.CoverImage?.Large ?? media.CoverImage?.ExtraLarge, media.Popularity)).ToArray();
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static bool IsManga(Media media, bool includeNsfw) => media.Id > 0 && media.Type == MangaCodes.Type &&
        media.Format is MangaCodes.Type or MangaCodes.OneShot && media.IsAdult is { } adult && (includeNsfw || !adult);

    private static int? MangaIdentity(IdentifyPluginRequest request) {
        if (!string.IsNullOrWhiteSpace(request.Query.Url)) {
            var urlId = MangaUrlId(request.Query.Url);
            return request.Query.ExternalIds is { Count: > 0 } && Identity(request.Query.ExternalIds) != urlId ? null : urlId;
        }
        if (request.Query.ExternalIds is { Count: > 0 }) return Identity(request.Query.ExternalIds);
        var identity = Identity(request.Entity.ExternalIds) ?? Identity(request.Hints.ExternalIds);
        return identity ?? (request.Entity.Urls ?? []).Concat(request.Hints.Urls).Select(MangaUrlId).FirstOrDefault(id => id is not null);

        static int? Identity(IReadOnlyDictionary<string, string>? values) => TryGetIdentity(values, PrimaryIdentityNamespace, out var value) &&
            TryCanonicalId(value, out var id) ? id : null;
    }

    private static int? MangaUrlId(string? value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host.Equals("anilist.co", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.anilist.co", StringComparison.OrdinalIgnoreCase))) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0] == "manga" && TryCanonicalId(segments[1], out var id) ? id : null;
    }

    private static bool TryCanonicalId(string value, out int id) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
        id > 0 && value == MediaId(id);
    private static string MediaId(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static EntityMetadataProposal MangaProposal(Media media, Guid targetId) {
        var ids = new Dictionary<string, string> { [PrimaryIdentityNamespace] = MediaId(media.Id) };
        if (media.IdMal is > 0) ids[MalIdentityNamespace] = MediaId(media.IdMal.Value);
        var dates = new Dictionary<string, string>();
        if (PublicationDate(media.StartDate) is { } start) dates[MangaCodes.Published] = start;
        if (PublicationDate(media.EndDate) is { } end) dates[MangaCodes.Ended] = end;
        var stats = new Dictionary<string, int>();
        if (media.Chapters is > 0) stats[MangaCodes.ChapterCount] = media.Chapters.Value;
        if (media.Volumes is > 0) stats[MangaCodes.VolumeCount] = media.Volumes.Value;
        var tags = (media.Genres ?? []).Concat((media.Tags ?? []).Where(tag => tag.Rank is >= 60).Select(tag => tag.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var creators = (media.Staff?.Edges ?? []).Where(edge => edge.Role is MangaCodes.Story or MangaCodes.Art or MangaCodes.StoryAndArt or MangaCodes.OriginalStory)
            .Select(edge => edge.Node?.Name?.Full).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(25).Select((name, i) => new CreditPatch(name, MangaCodes.Creator, null, i)).ToArray();
        var images = new List<ImageCandidate>();
        if (media.CoverImage?.ExtraLarge is { Length: > 0 } large) images.Add(new(MangaCodes.Poster, large, PluginId, 10, null, null, null));
        if (media.CoverImage?.Large is { Length: > 0 } cover && !images.Any(image => image.Url == cover)) images.Add(new(MangaCodes.Poster, cover, PluginId, 8, null, null, null));
        if (media.BannerImage is { Length: > 0 } banner) images.Add(new(MangaCodes.Backdrop, banner, PluginId, 7, null, null, null));
        return new($"{PluginId}:{media.Id}", PluginId, MangaCodes.Kind, 0.9m, MangaCodes.Reason,
            new EntityMetadataPatch(Title(media), StripHtml(media.Description), ids, [$"https://anilist.co/manga/{media.Id}"], tags, null,
                creators, dates, stats, new Dictionary<string, int>(), media.Format) {
                Flags = media.IsAdult == true ? new(null, true, null) : null,
                AlternativeTitles = new[] { media.Title?.English, media.Title?.Romaji, media.Title?.Native }
                    .Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!.Trim())
                    .Where(title => !title.Equals(Title(media), StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            }, images, [], [], targetId, []);
    }

    /// <summary>Preserves the upstream date precision; missing month/day never become January 1.</summary>
    internal static string? PublicationDate(FuzzyDate? date) {
        if (date?.Year is not (>= 1 and <= 9999)) return null;
        var year = date.Year.Value; var result = year.ToString("D4", CultureInfo.InvariantCulture);
        if (date.Month is not (>= 1 and <= 12)) return result;
        result += "-" + date.Month.Value.ToString("D2", CultureInfo.InvariantCulture);
        if (date.Day is not { } day || day < 1 || day > DateTime.DaysInMonth(year, date.Month.Value)) return result;
        return result + "-" + day.ToString("D2", CultureInfo.InvariantCulture);
    }

    // prism-vocab: external GraphQL field names are decoded once by the Media/Staff records.
    private const string MangaFields = """
        id idMal type format isAdult description chapters volumes popularity siteUrl bannerImage genres
        title { english romaji native } startDate { year month day } endDate { year month day }
        coverImage { extraLarge large } tags { name rank }
        staff(perPage: 25) { edges { role node { name { full } } } }
        """;
    private static readonly string MangaDetailQuery = $"query ($id: Int!) {{ Media(id: $id, type: {MangaCodes.Type}) {{ {MangaFields} }} }}";
    // AniList treats an explicit null adult filter differently from an omitted argument.
    private static string MangaSearchQuery(bool includeNsfw) => $"query ($search: String!, $year: String, $perPage: Int!) {{ Page(perPage: $perPage) {{ media(search: $search, startDate_like: $year, type: {MangaCodes.Type}, format_in: [{MangaCodes.Type}, {MangaCodes.OneShot}]{(includeNsfw ? string.Empty : ", isAdult: false")}, sort: [SEARCH_MATCH, POPULARITY_DESC]) {{ {MangaFields} }} }} }}";
    internal sealed record StaffConnection(StaffEdge[]? Edges);
    internal sealed record StaffEdge(string? Role, Staff? Node);
    internal sealed record Staff(CharacterName? Name);
}
