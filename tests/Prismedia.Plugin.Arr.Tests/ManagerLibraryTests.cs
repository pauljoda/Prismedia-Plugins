using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class ManagerLibraryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRequiresAHealthyCompleteLibraryToConfirmTheMissingHolding(bool sonarr) {
        using var fixture = new Fixture(sonarr);
        var collection = sonarr ? "series" : "movie";
        var input = new ManagedItemInput(sonarr ? ManagerProtocol.Series : ManagerProtocol.Movie, "1",
            new Dictionary<string, string> { [sonarr ? ManagerProtocol.Tvdb : ManagerProtocol.Tmdb] = "1001" });
        fixture.Statuses[collection + "/1"] = HttpStatusCode.NotFound;
        fixture.Responses[collection] = Array.Empty<object>();

        var removed = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));

        Assert.Equal(IntegrationErrorCodes.ManagedItemNotFound, removed.Code);
        fixture.Statuses[collection] = HttpStatusCode.ServiceUnavailable;
        var offline = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));
        Assert.Null(offline.Code);
        fixture.Statuses[collection] = HttpStatusCode.OK;
        fixture.Responses[collection] = sonarr
            ? new[] { new { id = 1, title = "Series", year = 2000, tvdbId = 1001, qualityProfileId = 1, monitored = false, path = "/library/series" } }
            : new[] { Movie(1) };
        var inconsistent = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));
        Assert.Null(inconsistent.Code);
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData(false, "movie")]
    [InlineData(true, "video-series")]
    public async Task ListsEveryProviderLibraryWithStableRootIdentity(bool sonarr, string expectedKind) {
        using var fixture = new Fixture(sonarr);
        fixture.Responses["rootfolder"] = new[] {
            new { id = 4, path = "/media/primary/", accessible = true },
            new { id = 7, path = "D:\\Archive", accessible = false },
        };

        var catalog = Assert.IsType<ProviderLibraryCatalog>(await fixture.Call(ManagerProtocol.ListLibraries, new { }));

        Assert.Equal(["4", "7"], catalog.Libraries.Select(library => library.RemoteId));
        Assert.Equal(["primary", "Archive"], catalog.Libraries.Select(library => library.Label));
        Assert.All(catalog.Libraries, library => Assert.Equal([expectedKind], library.EntityKinds));
    }

    [Fact]
    public async Task RadarrPagesExistingHoldingsWithoutClaimsOfPersistentIdentityOrMutations() {
        using var fixture = new Fixture();
        fixture.Responses["movie"] = new[] { Movie(2), Movie(1) };
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Null(probe.InstanceId);
        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, null, 1)));
        Assert.Equal("1", Assert.Single(page.Items).RemoteId);
        Assert.Equal("1001", page.Items[0].ExternalIds[ManagerProtocol.Tmdb]);
        Assert.Null(page.Items[0].Presentation);
        var second = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, page.NextCursor, 1)));
        Assert.Equal("2", Assert.Single(second.Items).RemoteId);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, "changed query", page.NextCursor, 1)));
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task RemoteItemNumberCannotSubstituteAnotherMetadataIdentity() {
        using var fixture = new Fixture();
        fixture.Responses["movie/1"] = Movie(1);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem,
            new ManagedItemInput(ManagerProtocol.Movie, "1", new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "9999" })));
    }

    [Fact]
    public async Task RadarrMapsOptionalPresentationForListAndDetail() {
        using var fixture = new Fixture();
        var movie = new {
            id = 1, title = "Movie 1", year = 2024, tmdbId = 1001, qualityProfileId = 1, monitored = true, hasFile = false, movieFileId = 0, path = "/library/movie",
            overview = "A quiet overview.",
            images = new[] {
                new { coverType = "poster", remoteUrl = "https://image.example/poster.jpg", url = "/MediaCover/1/poster.jpg" },
                new { coverType = "fanart", remoteUrl = "https://image.example/fanart.jpg", url = "/MediaCover/1/fanart.jpg" },
                new { coverType = "banner", remoteUrl = "https://image.example/banner.jpg", url = "/MediaCover/1/banner.jpg" },
            },
            genres = new[] { " Drama ", "Drama", "Mystery" }, runtime = 123, certification = "PG-13",
        };
        fixture.Responses["movie"] = new[] { movie };
        fixture.Responses["movie/1"] = movie;

        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, null, 10)));
        var listed = Assert.Single(page.Items).Presentation;
        Assert.NotNull(listed);
        Assert.Equal("A quiet overview.", listed.Overview);
        Assert.Equal("https://image.example/poster.jpg", listed.PosterUrl);
        Assert.Equal("https://image.example/fanart.jpg", listed.BackdropUrl);
        Assert.Equal(new[] { "Drama", "Mystery" }, listed.Genres);
        Assert.Equal(123, listed.RuntimeMinutes);
        Assert.Equal("PG-13", listed.ContentRating);

        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem,
            new ManagedItemInput(ManagerProtocol.Movie, "1", new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "1001" })));
        Assert.Equal(listed.Overview, snapshot.Item.Presentation?.Overview);
        Assert.Equal(listed.PosterUrl, snapshot.Item.Presentation?.PosterUrl);
        Assert.Equal(listed.BackdropUrl, snapshot.Item.Presentation?.BackdropUrl);
        Assert.Equal(listed.Genres, snapshot.Item.Presentation?.Genres);
        Assert.Equal(listed.RuntimeMinutes, snapshot.Item.Presentation?.RuntimeMinutes);
        Assert.Equal(listed.ContentRating, snapshot.Item.Presentation?.ContentRating);
    }

    [Fact]
    public async Task SonarrMapsOptionalPresentationForListAndDetail() {
        using var fixture = new Fixture(sonarr: true);
        var series = new {
            id = 1, title = "Series", year = 2024, tvdbId = 1001, qualityProfileId = 1, monitored = true, path = "/library/series",
            statistics = new { episodeFileCount = 0 }, overview = "Series overview", images = new[] {
                new { coverType = "poster", remoteUrl = "https://image.example/series-poster.jpg", url = "/MediaCover/1/poster.jpg" },
                new { coverType = "fanart", remoteUrl = "https://image.example/series-fanart.jpg", url = "/MediaCover/1/fanart.jpg" },
            }, genres = new[] { "Sci-Fi" }, runtime = 48, certification = "TV-14",
        };
        fixture.Responses["series"] = new[] { series };
        fixture.Responses["series/1"] = series;
        fixture.Responses["episodefile?seriesId=1"] = Array.Empty<object>();
        fixture.Responses["episode?seriesId=1"] = Array.Empty<object>();

        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Series, null, null, 10)));
        Assert.Equal("https://image.example/series-fanart.jpg", Assert.Single(page.Items).Presentation?.BackdropUrl);

        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem,
            new ManagedItemInput(ManagerProtocol.Series, "1", new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "1001" })));
        Assert.Equal("Series overview", snapshot.Item.Presentation?.Overview);
        Assert.Equal(48, snapshot.Item.Presentation?.RuntimeMinutes);
    }

    [Fact]
    public async Task UnsafeOrLocalArtworkIsIgnoredWithoutBreakingTheHolding() {
        using var fixture = new Fixture();
        fixture.Responses["movie"] = new[] { new {
            id = 1, title = "Movie 1", year = 2024, tmdbId = 1001, qualityProfileId = 1, monitored = false, hasFile = false, movieFileId = 0, path = "/library/movie",
            overview = "Still available", images = new[] {
                new { coverType = "poster", remoteUrl = "https://image.example/poster.jpg?token=secret", url = "/MediaCover/1/poster.jpg" },
                new { coverType = "fanart", remoteUrl = "https://user:secret@image.example/fanart.jpg", url = "/MediaCover/1/fanart.jpg" },
                new { coverType = "banner", remoteUrl = "https://image.example/banner.jpg", url = "/MediaCover/1/banner.jpg" },
                new { coverType = "fanart", remoteUrl = "javascript:alert(1)", url = "/MediaCover/1/fanart-2.jpg" },
            }, genres = Array.Empty<string>(), runtime = 0, certification = " ",
        } };

        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, null, 10)));
        var presentation = Assert.Single(page.Items).Presentation;
        Assert.NotNull(presentation);
        Assert.Equal("Still available", presentation.Overview);
        Assert.Null(presentation.PosterUrl);
        Assert.Null(presentation.BackdropUrl);
        Assert.Null(presentation.Genres);
        Assert.Null(presentation.RuntimeMinutes);
        Assert.Null(presentation.ContentRating);
    }

    [Fact]
    public async Task OutOfRangeOptionalPresentationFieldsAreNormalizedToSafeBounds() {
        using var fixture = new Fixture();
        var genres = Enumerable.Range(1, 70).Select(index => $"Genre {index}").ToArray();
        fixture.Responses["movie"] = new[] { new {
            id = 1, title = "Movie 1", year = 2024, tmdbId = 1001, qualityProfileId = 1, monitored = false, hasFile = false, movieFileId = 0, path = "/library/movie",
            overview = new string('o', 32_769), images = new object?[] {
                null,
                new { coverType = "poster", remoteUrl = "https://image.example/" + new string('p', 8_190), url = "/MediaCover/1/poster.jpg" },
                new { coverType = "fanart", remoteUrl = "https://image.example/fanart.jpg\\unsafe", url = "/MediaCover/1/fanart.jpg" },
            }, genres, runtime = 10_081, certification = new string('R', 129),
        } };

        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, null, 10)));
        var presentation = Assert.Single(page.Items).Presentation;
        Assert.NotNull(presentation);
        Assert.Null(presentation.Overview);
        Assert.Null(presentation.PosterUrl);
        Assert.Null(presentation.BackdropUrl);
        Assert.Equal(64, presentation.Genres?.Count);
        Assert.Equal(presentation.Genres?.Count, presentation.Genres?.Distinct(StringComparer.Ordinal).Count());
        Assert.All(presentation.Genres!, genre => Assert.InRange(genre.Length, 1, 128));
        Assert.Null(presentation.RuntimeMinutes);
        Assert.Null(presentation.ContentRating);
    }

    [Fact]
    public async Task MissingMovieFileRemainsMissingAndExactFileAssociationsAreRequired() {
        using var fixture = new Fixture();
        fixture.Responses["movie/1"] = Movie(1);
        var input = new ManagedItemInput(ManagerProtocol.Movie, "1", new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "1001" });
        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, input));
        Assert.Empty(snapshot.Files);
        fixture.Responses["movie/1"] = Movie(1, true);
        fixture.Responses["moviefile/11"] = new { id = 11, movieId = 2, path = "/library/movie.mkv", size = 128 };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));
        fixture.Responses["moviefile/11"] = new { id = 11, movieId = 1, path = "/library/movie.mkv", size = 128 };
        snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, input));
        Assert.Equal("1", Assert.Single(Assert.Single(snapshot.Files).Targets).RemoteId);
    }

    [Fact]
    public async Task SonarrRetainsCombinedEpisodesSpecialsAndAbsoluteNumbers() {
        using var fixture = new Fixture(sonarr: true);
        fixture.Responses["series/1"] = new { id = 1, title = "Series", year = 2000, tvdbId = 1001, qualityProfileId = 1, monitored = true, path = "/library/series" };
        fixture.Responses["episodefile?seriesId=1"] = new[] { new { id = 10, seriesId = 1, path = "/library/series/combined.mkv", size = 128 } };
        fixture.Responses["episode?seriesId=1"] = new[] { Episode(1, 0, 1, 10), Episode(2, 0, 2, 10) };
        var input = new ManagedItemInput(ManagerProtocol.Series, "1", new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "1001" });
        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, input));
        var file = Assert.Single(snapshot.Files);
        Assert.Equal(2, file.Targets.Count);
        Assert.All(file.Targets, target => Assert.Equal(0, target.SeasonNumber));
        Assert.Equal(2, file.Targets[1].AbsoluteNumber);
        fixture.Responses["episodefile?seriesId=1"] = Array.Empty<object>();
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));
    }

    [Fact]
    public async Task SonarrAdvertisesOnlyImplementedFiniteControls() {
        using var fixture = new Fixture(sonarr: true);
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        var manager = Assert.Single(probe.Capabilities, capability => capability.Kind == ManagerProtocol.ExternalManager);
        Assert.Equal(new[] { ManagerProtocol.Options, ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request,
            ManagerCreation.Lookup, ManagerCreation.Ensure, ManagerRelease.Inspect }.Order(), manager.Operations.Order());
        Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, new { })).Outcome);
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task WrongApplicationOrUnsupportedServerVersionIsRejected() {
        using var fixture = new Fixture();
        fixture.Responses["system/status"] = new { appName = "Sonarr", version = "4.0.17.2952" };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
        fixture.Responses["system/status"] = new { appName = "Radarr", version = "7.0.0.0" };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
    }

    [Fact]
    public async Task TransportPreservesProxyPrefixAndRejectsRedirectsWithoutLeakingAuth() {
        using var fixture = new Fixture();
        fixture.Status = HttpStatusCode.Redirect;
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("http://manager.test/radarr/api/v3/system/status", request.RequestUri!.AbsoluteUri);
        Assert.Equal("test-secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
    }

    private static object Movie(int id, bool hasFile = false) => new { id, title = "Movie " + id, year = 2024, tmdbId = 1000 + id, qualityProfileId = 1, monitored = false, hasFile, movieFileId = hasFile ? 11 : 0, path = "/library/movie" };
    private static object Episode(int id, int season, int number, int fileId) => new { id, seriesId = 1, title = "Episode " + id, seasonNumber = season, episodeNumber = number, absoluteEpisodeNumber = number, episodeFileId = fileId, hasFile = true, monitored = false };

    private sealed class Fixture : HttpMessageHandler {
        internal Dictionary<string, object> Responses { get; } = [];
        internal Dictionary<string, HttpStatusCode> Statuses { get; } = [];
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "http://manager.test/radarr/", null, new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "test-secret" });
        private readonly ArrClient client;
        private readonly ArrLibrary library;
        internal Fixture(bool sonarr = false) {
            Responses["system/status"] = new { appName = sonarr ? "Sonarr" : "Radarr", version = sonarr ? "4.0.17.2952" : "6.1.1.10360" };
            client = new(connection, this);
            library = sonarr ? new SonarrLibrary(client) : new RadarrLibrary(client);
        }
        internal Task<object> Call(string operation, object input) => library.DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version,
            Guid.NewGuid(), operation, connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request);
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            return Task.FromResult(new HttpResponseMessage(Statuses.GetValueOrDefault(path, Status)) {
                Content = new StringContent(JsonSerializer.Serialize(Responses.GetValueOrDefault(path), IntegrationProtocol.Json))
            });
        }
    }
}
