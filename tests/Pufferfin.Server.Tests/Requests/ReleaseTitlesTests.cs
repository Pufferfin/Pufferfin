using System.Collections.Generic;
using System.Linq;
using Pufferfin.Server.Arr.Models;
using Pufferfin.Server.Requests.Search;
using Xunit;

namespace Pufferfin.Server.Tests.Requests;

public sealed class ReleaseTitlesTests
{
    private static readonly string?[] _psychomagic = ["Psychomagie, un art pour guérir", "Psychomagic: A Healing Art", "Psychomagie, un art pour guérir"];

    private static readonly string?[] _fando = ["Fando et Lis", "Fando and Lis", "Fando y Lis"];

    [Fact]
    public void TheSearches_AreEachDistinctTitle_WithItsYearFirst_ThenWithout()
    {
        var queries = ReleaseTitles.BuildQueries(_psychomagic, 2019);

        Assert.Equal(
            ["psychomagie un art pour guerir 2019", "psychomagic a healing art 2019", "psychomagie un art pour guerir", "psychomagic a healing art"],
            queries);
    }

    [Fact]
    public void TheSearches_AreCapped_AndSkipEmptyTitles()
    {
        var queries = ReleaseTitles.BuildQueries(["Un", "Deux", null, "Trois", "Quatre", " "], 2000);

        Assert.Equal(ReleaseTitles.MaxQueries, queries.Count);
        Assert.DoesNotContain(queries, q => q.Trim().Length == 0);
    }

    [Theory]
    [InlineData("Psychomagic A Healing Art 2019 FRENCH 1080p BluRay H264")]
    [InlineData("Psychomagic.A.Healing.Art.2019.720P.BLURAY.X264-WATCHABLE")]
    [InlineData("(Psychomagie, un art pour guérir).Psychomagic.A.Healing.Art.2019.1080p.BluRay")]
    [InlineData("Psychomagie un art pour guérir (2019) MULTi 1080p")]
    public void AReleaseOfTheFilm_UnderAnyOfItsTitles_IsRecognized(string title)
        => Assert.NotNull(ReleaseTitles.MatchMovie(title, _psychomagic, 2019));

    [Theory]
    [InlineData("Fando.et.Lis.1968.VOSTFR.1080p.BDrip.x265.Flac-AZAZE")]
    [InlineData("Fando y Lis (1968) DVDRip.x264-Distanasia")]
    [InlineData("Fando And Lis (1968) (1080p) [BluRay] [YTS MX]")]
    public void AReleaseDatedByTheYearOfShooting_IsTheFilm(string title)
        => Assert.NotNull(ReleaseTitles.MatchMovie(title, _fando, 1972));

    [Theory]
    [InlineData("Tusk.2014.1080p.BluRay.x264")]
    [InlineData("Psychomagic.2.2021.1080p.WEB")]
    [InlineData("Psychomagic.A.Healing.Art.S01E02.1080p")]
    [InlineData("The.Psychomagic.Documentary.2019.1080p")]
    public void AnotherFilm_IsNotTaken(string title)
    {
        Assert.Null(ReleaseTitles.MatchMovie(title, ["Tusk", "Tusk", "Tusk"], 1980));
        Assert.Null(ReleaseTitles.MatchMovie(title, _psychomagic, 2019));
    }

    [Fact]
    public void ARemakeOfTheSameName_IsNotTheFilm()
        => Assert.Null(ReleaseTitles.MatchMovie("Fando y Lis 2010 1080p", _fando, 1972));

    [Fact]
    public void AnUndatedRelease_CountsOnlyWhenItStartsWithASeveralWordTitle()
    {
        Assert.NotNull(ReleaseTitles.MatchMovie("The Rainbow Thief, Alejandro Jodorowsky", ["Le Voleur d'arc-en-ciel", "The Rainbow Thief", "The Rainbow Thief"], 1994));
        Assert.Null(ReleaseTitles.MatchMovie("Tusk DVDRip", ["Tusk", "Tusk", "Tusk"], 1980));
    }

    [Fact]
    public void TheRewrittenTitle_CarriesRadarrsNameAndYear_AndKeepsQualityLanguagesAndGroup()
    {
        const string Title = "(Psychomagie, un art pour guérir).Psychomagic.A.Healing.Art.2019.1080p.BluRay.FRENCH-GRP";
        var match = ReleaseTitles.MatchMovie(Title, _psychomagic, 2019)!;

        Assert.Equal("Psychomagic.A.Healing.Art.2019.1080p.BluRay.FRENCH-GRP", ReleaseTitles.Rewrite(Title, match, "Psychomagic: A Healing Art", 2019));
    }

    [Fact]
    public void TheRewrittenTitle_TakesRadarrsYear_AndKeepsALanguageWrittenBeforeTheTitle()
    {
        const string Title = "[FRENCH] Fando y Lis (1968) 720p BluRay";
        var match = ReleaseTitles.MatchMovie(Title, _fando, 1972)!;

        Assert.Equal(1968, match.Year);
        Assert.Equal("Fando.and.Lis.1972.FRENCH.720p.BluRay", ReleaseTitles.Rewrite(Title, match, "Fando and Lis", 1972));
    }

    [Fact]
    public void TheBracketsOfAReleaseName_BecomeDots()
    {
        const string Title = "Fando And Lis (1968) (1080p) [BluRay] [YTS MX]";
        var match = ReleaseTitles.MatchMovie(Title, _fando, 1972)!;

        Assert.Equal("Fando.and.Lis.1972.1080p.BluRay.YTS.MX", ReleaseTitles.Rewrite(Title, match, "Fando and Lis", 1972));
    }

    [Fact]
    public void TheSameTorrent_FromBothSearches_IsKeptOnce_AsRadarrsOwn()
    {
        var own = new ArrRelease { Title = "A.2019.1080p", InfoHash = "abc", Seeders = 5, Protocol = "torrent" };
        var found = new ArrRelease { Title = "A.2019.1080p", InfoHash = "ABC", Seeders = 5, Protocol = "torrent", Push = Push() };

        var merged = ReleaseRanking.Merge([own], [found]);

        Assert.Same(own, Assert.Single(merged));
    }

    [Fact]
    public void AWellSeededRelease_ComesBeforeABetterOneWithFewSeeders()
    {
        var weak = Release("Weak.1080p", 1080, 1);
        var healthy = Release("Healthy.720p", 720, 9);
        var better = Release("Better.1080p", 1080, 12);

        var ranked = ReleaseRanking.Rank([weak, healthy, better]);

        Assert.Equal(["Better.1080p", "Healthy.720p", "Weak.1080p"], ranked.Select(r => r.Title));
    }

    [Fact]
    public void AmongEquals_TheOrderOfTheServiceIsKept()
    {
        var first = Release("First", 1080, 20);
        var second = Release("Second", 1080, 20);

        Assert.Equal(["First", "Second"], ReleaseRanking.Rank([first, second]).Select(r => r.Title));
    }

    private static ArrRelease Release(string title, int resolution, int seeders)
        => new()
        {
            Title = title,
            Seeders = seeders,
            Protocol = "torrent",
            Quality = new ArrQualityModel { Quality = new ArrQuality { Resolution = resolution } }
        };

    private static ReleasePush Push() => new("t", "t", null, null, 0, "x", null, "q");
}
