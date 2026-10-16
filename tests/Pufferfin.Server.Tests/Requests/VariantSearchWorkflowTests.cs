using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Pufferfin.Server.Requests;
using Pufferfin.Server.Requests.Integration;
using Pufferfin.Server.Tests.TestHelpers;
using Xunit;

namespace Pufferfin.Server.Tests.Requests;

/// <summary>
/// A film whose releases are named after another of its titles, or dated by another year: Pufferfin finds them
/// through Prowlarr and pushes the best to Radarr under the name Radarr knows.
/// </summary>
public sealed class VariantSearchWorkflowTests : IDisposable
{
    private readonly RequestTestKit _kit = new();

    private readonly ConcurrentQueue<JsonObject> _pushes = new();

    private bool _approve = true;

    public VariantSearchWorkflowTests()
    {
        _kit.Metadata.Movies[438631] = new TitleMetadata(438631, "Dune : Première partie", "en", [878], ["US"], OriginalTitle: "Dune");
        var movie = _kit.AddMovie();
        movie["year"] = 2021;
        _kit.Server.Resources["qualityprofile"] = (JsonArray)JsonNode.Parse("""
            [{"id":7,"items":[{"quality":{"id":3,"name":"WEBDL-1080p"},"allowed":true},{"quality":{"id":19,"name":"Bluray-2160p"},"allowed":false}]},
             {"id":8,"items":[{"quality":{"id":3,"name":"WEBDL-1080p"},"allowed":true}]}]
            """)!;

        // Radarr's and Prowlarr's indexers share the fake server: Prowlarr's YggTest is Radarr's "YggTest (Prowlarr)"
        _kit.Server.Resources["indexer"] = (JsonArray)JsonNode.Parse("""
            [{"id":1,"name":"YggTest","privacy":"private"},{"id":9,"name":"YggTest (Prowlarr)"},{"id":3,"name":"PrivateUnknown","privacy":"private"}]
            """)!;
        _kit.Server.Overrides.Insert(0, (method, path, body) =>
        {
            if (method == HttpMethod.Get && path.StartsWith("parse?", StringComparison.Ordinal))
            {
                var title = Uri.UnescapeDataString(path["parse?title=".Length..]);
                var french = title.Contains("FRENCH", StringComparison.OrdinalIgnoreCase) || title.Contains("MULTI", StringComparison.OrdinalIgnoreCase);
                return FakeArrServer.Json(new JsonObject
                {
                    ["movie"] = title.StartsWith("Dune.2021.", StringComparison.Ordinal) ? new JsonObject { ["id"] = 50 } : null,
                    ["languages"] = new JsonArray(new JsonObject { ["id"] = french ? 2 : 1, ["name"] = french ? "French" : "English" }),
                    ["parsedMovieInfo"] = new JsonObject { ["quality"] = new JsonObject { ["quality"] = new JsonObject { ["id"] = 3, ["name"] = "WEBDL-1080p", ["resolution"] = 1080 } } }
                });
            }

            if (method == HttpMethod.Post && path == "release/push")
            {
                var pushed = JsonNode.Parse(body!)!.AsObject();
                _pushes.Enqueue(pushed);
                return FakeArrServer.Json(new JsonArray(new JsonObject
                {
                    ["title"] = pushed["title"]!.GetValue<string>(),
                    ["approved"] = _approve,
                    ["mappedMovieId"] = 50,
                    ["rejections"] = _approve ? new JsonArray() : new JsonArray("Not wanted")
                }));
            }

            return null;
        });
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task AFrenchReleaseNamedAfterTheFrenchTitle_IsFound_AndPushedUnderRadarrsName()
    {
        Radarr(RequestTestKit.ReleaseJson("Dune.2021.1080p.ENG", ["English"], seeders: 1));
        Prowlarr(Found("Dune Premiere partie 2021 FRENCH 1080p WEB-GRP", seeders: 9));

        var result = await Search();

        Assert.Equal(RequestState.Downloading, result.State);
        Assert.Equal("fr", result.ChosenLanguage);
        Assert.Empty(_kit.Grabs);
        var pushed = Assert.Single(_pushes);
        Assert.Equal("Dune.2021.FRENCH.1080p.WEB-GRP", pushed["title"]!.GetValue<string>());
        Assert.Equal(9, pushed["indexerId"]!.GetValue<int>());
        Assert.Equal("Dune Premiere partie 2021 FRENCH 1080p WEB-GRP", result.ReleaseTitle);
    }

    [Fact]
    public async Task WhenRadarrRefusesThePush_TheNextReleaseIsTaken()
    {
        _approve = false;
        Radarr(RequestTestKit.ReleaseJson("Dune.2021.MULTi.1080p", ["French"], seeders: 4));
        Prowlarr(Found("Dune Premiere partie 2021 FRENCH 1080p WEB-GRP", seeders: 9));

        var result = await Search();

        Assert.Single(_pushes);
        Assert.Equal(["Dune.2021.MULTi.1080p"], _kit.Grabs);
        Assert.Equal(RequestState.Downloading, result.State);
    }

    [Fact]
    public async Task AReleaseOfAnotherFilm_OrOfAPrivateTrackerRadarrDoesNotHave_OrWithoutSeed_IsNeverPushed()
    {
        Radarr(RequestTestKit.ReleaseJson("Dune.2021.MULTi.1080p", ["French"], seeders: 4));
        Prowlarr(
            Found("Dune 1984 FRENCH 1080p", seeders: 50),
            Found("Dune Premiere partie 2021 FRENCH 1080p", seeders: 50, indexerId: 3),
            Found("Dune Premiere partie 2021 MULTI 1080p", seeders: 0));

        await Search();

        Assert.Empty(_pushes);
        Assert.Equal(["Dune.2021.MULTi.1080p"], _kit.Grabs);
    }

    [Fact]
    public async Task WithoutProwlarr_RadarrsOwnSearchGoesOnAlone()
    {
        Radarr(RequestTestKit.ReleaseJson("Dune.2021.MULTi.1080p", ["French"]));

        await _kit.Movies.SearchAsync(RequestTestKit.Request(), RequestTestKit.Context(), CancellationToken.None);

        Assert.DoesNotContain(_kit.Server.Calls, c => c.Path.StartsWith("search?", StringComparison.Ordinal));
        Assert.Equal(["Dune.2021.MULTi.1080p"], _kit.Grabs);
    }

    [Fact]
    public async Task AFailingProwlarr_LeavesRadarrsOwnSearch()
    {
        Radarr(RequestTestKit.ReleaseJson("Dune.2021.MULTi.1080p", ["French"]));
        _kit.Server.Overrides.Insert(0, (method, path, _) => method == HttpMethod.Get && path.StartsWith("search?", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            : null);

        var result = await Search();

        Assert.Equal(RequestState.Downloading, result.State);
        Assert.Equal(["Dune.2021.MULTi.1080p"], _kit.Grabs);
    }

    private static JsonObject Found(string title, int seeders, int indexerId = 1)
        => new()
        {
            ["guid"] = "g-" + title,
            ["title"] = title,
            ["indexerId"] = indexerId,
            ["indexer"] = "YggTest",
            ["size"] = 4_000_000_000,
            ["seeders"] = seeders,
            ["protocol"] = "torrent",
            ["downloadUrl"] = "http://prowlarr:9696/1/download?link=x"
        };

    private Task<MediaRequest> Search()
        => _kit.Movies.SearchAsync(RequestTestKit.Request(), RequestTestKit.Context(withProwlarr: true), CancellationToken.None);

    private void Radarr(params JsonObject[] releases)
        => _kit.Server.Resources["release"] = new JsonArray(releases.Select(r => (JsonNode)r).ToArray());

    private void Prowlarr(params JsonObject[] releases)
        => _kit.Server.Overrides.Add((method, path, _) => method == HttpMethod.Get && path.StartsWith("search?", StringComparison.Ordinal)
            ? FakeArrServer.Json(new JsonArray(releases.Select(r => (JsonNode)r.DeepClone()).ToArray()))
            : null);
}
