using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Canonical Arr image roles at the external protocol boundary.</summary>
internal static class ArrCoverType {
    #region Static Variables
    internal const string Poster = "poster";
    internal const string Fanart = "fanart";
    internal const string Headshot = "headshot";
    #endregion
}

/// <summary>External Arr artwork shape; only its public remote URL is eligible for presentation.</summary>
internal sealed record ArrImage(string? CoverType, string? RemoteUrl, string? Url);

/// <summary>Maps optional Arr metadata into the bounded connected-library presentation contract.</summary>
internal static class ArrPresentation {
    #region Actions - Presentation
    internal static ManagedLibraryPresentation? Map(
        string? overview,
        IReadOnlyList<ArrImage?>? images,
        IReadOnlyList<string>? genres,
        int? runtimeMinutes,
        string? certification) {
        var presentation = new ManagedLibraryPresentation(
            Text(overview, 32_768, allowLineBreaks: true),
            Image(images, ArrCoverType.Poster),
            Image(images, ArrCoverType.Fanart),
            Genres(genres),
            runtimeMinutes is >= 1 and <= 10_080 ? runtimeMinutes : null,
            Text(certification, 128));
        return presentation is { Overview: null, PosterUrl: null, BackdropUrl: null, Genres: null, RuntimeMinutes: null, ContentRating: null }
            ? null
            : presentation;
    }

    /// <summary>Returns the public upstream URL for the first image with the requested Arr role.</summary>
    internal static string? PublicImage(IReadOnlyList<ArrImage?>? images, string coverType) =>
        Image(images, coverType);

    private static string? Image(IReadOnlyList<ArrImage?>? images, string coverType) =>
        images?.Where(image => image is not null && string.Equals(image.CoverType, coverType, StringComparison.OrdinalIgnoreCase))
            .Select(image => PublicRemoteUrl(image!.RemoteUrl))
            .FirstOrDefault(url => url is not null);

    private static IReadOnlyList<string>? Genres(IReadOnlyList<string>? genres) {
        var values = genres?.Select(genre => Text(genre, 128))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return values is { Length: > 0 } ? values : null;
    }

    private static string? Text(string? value, int maxLength, bool allowLineBreaks = false) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength
            || value.Any(character => char.IsControl(character)
                && (!allowLineBreaks || character is not ('\r' or '\n' or '\t')))) return null;
        return value.Trim();
    }

    private static string? PublicRemoteUrl(string? value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8_192
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || uri.Host.Length == 0 || uri.IsLoopback || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        return uri.AbsoluteUri;
    }
    #endregion
}
