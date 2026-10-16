using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pufferfin.Server.Requests.Search;

/// <summary>
/// The titles a film is searched by, and how a release title is recognized as that film.
/// </summary>
/// <remarks>
/// A film is searched under its French title, its original title, and its English title when that one differs:
/// "Psychomagie, un art pour guérir" finds one dead release where "Psychomagic, A Healing Art" finds a dozen, French
/// ones included. Releases are often dated by the year of shooting rather than TMDB's release year
/// ("Fando y Lis 1968" for a film TMDB dates 1972), so a year a few years before the film's counts.
/// </remarks>
public static partial class ReleaseTitles
{
    /// <summary>The most searches made for one title.</summary>
    public const int MaxQueries = 6;

    // A release year earlier than TMDB's by this much still is the film: the year of shooting or of a festival.
    private const int YearsEarlier = 6;

    // A release year later than TMDB's by this much still is the film.
    private const int YearsLater = 1;

    // The number of words after a title where its year is looked for: "Title (Director, 1990)".
    private const int YearReach = 4;

    // Tokens of a release that tell its languages, kept when they come before the title.
    private static readonly HashSet<string> _languageTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "french", "truefrench", "multi", "vff", "vfq", "vfi", "vf", "vf2", "vostfr", "subfrench", "vof", "vo"
    };

    // A word right after a title that makes it another film: a sequel or a part.
    private static readonly HashSet<string> _sequelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "2", "3", "4", "5", "6", "7", "8", "9", "ii", "iii", "iv", "v", "vi", "part", "partie", "chapter", "chapitre", "returns", "reloaded"
    };

    /// <summary>
    /// Builds the searches of a film: each distinct title with its year, then without it.
    /// </summary>
    /// <param name="titles">The titles, in order of preference: French, English, original. Empty ones are skipped.</param>
    /// <param name="year">The year of the film, if known.</param>
    /// <returns>At most <see cref="MaxQueries"/> searches.</returns>
    public static IReadOnlyList<string> BuildQueries(IEnumerable<string?> titles, int? year)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var distinct = Distinct(titles);
        var queries = new List<string>();
        foreach (var title in distinct.Where(t => year is not null))
        {
            queries.Add(string.Create(CultureInfo.InvariantCulture, $"{title} {year}"));
        }

        queries.AddRange(distinct);
        return queries.Distinct(StringComparer.Ordinal).Take(MaxQueries).ToList();
    }

    /// <summary>
    /// Gets the distinct titles, normalized, in their order.
    /// </summary>
    /// <param name="titles">The titles.</param>
    /// <returns>The normalized titles, without duplicates or empty ones.</returns>
    public static IReadOnlyList<string> Distinct(IEnumerable<string?> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return titles
            .Select(t => Normalize(t ?? string.Empty))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Normalizes a title for comparison: lower case, no accent, every punctuation a single space.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Recognizes a release of a film by its title: one of the film's titles, followed by a year close to the film's.
    /// </summary>
    /// <param name="releaseTitle">The release title, as the indexer names it.</param>
    /// <param name="titles">The film's titles.</param>
    /// <param name="year">The film's year, if known.</param>
    /// <returns>Where the title and the year end in the release title, or <c>null</c> for another film.</returns>
    public static TitleMatch? MatchMovie(string releaseTitle, IEnumerable<string?> titles, int? year)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);
        ArgumentNullException.ThrowIfNull(titles);
        var tokens = Tokenize(releaseTitle);
        if (tokens.Count == 0 || tokens.Any(t => EpisodeTokenRegex().IsMatch(t.Normalized)))
        {
            return null;
        }

        TitleMatch? undated = null;
        foreach (var variant in Distinct(titles).Select(v => v.Split(' ')))
        {
            for (var start = 0; start + variant.Length <= tokens.Count; start++)
            {
                if (!Matches(tokens, start, variant))
                {
                    continue;
                }

                var end = start + variant.Length;
                if (end < tokens.Count && _sequelTokens.Contains(tokens[end].Normalized) && !_sequelTokens.Contains(variant[^1]))
                {
                    continue;
                }

                var yearIndex = FindYear(tokens, end);
                if (yearIndex is int index)
                {
                    if (year is null || IsCloseYear(int.Parse(tokens[index].Normalized, CultureInfo.InvariantCulture), year.Value))
                    {
                        return new TitleMatch(start, tokens[index].End, tokens[start].Start, int.Parse(tokens[index].Normalized, CultureInfo.InvariantCulture));
                    }

                    // The year right after the title is another film's: a remake or a namesake
                    continue;
                }

                // Without a year, only a title of several words at the start of the release name counts
                if (undated is null && start == 0 && variant.Length >= 2 && !tokens.Any(t => YearRegex().IsMatch(t.Normalized)))
                {
                    undated = new TitleMatch(start, tokens[end - 1].End, tokens[start].Start, null);
                }
            }
        }

        return undated;
    }

    /// <summary>
    /// Rewrites a release title with the name and year the service knows the film by, keeping what follows (quality,
    /// languages, group) and any language word written before the title, so the service maps it to the film.
    /// </summary>
    /// <param name="releaseTitle">The release title, as the indexer names it.</param>
    /// <param name="match">Where the film's title and year end in it.</param>
    /// <param name="serviceTitle">The title the service knows the film by.</param>
    /// <param name="serviceYear">The year the service knows the film by.</param>
    /// <returns>The rewritten title.</returns>
    public static string Rewrite(string releaseTitle, TitleMatch match, string serviceTitle, int serviceYear)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(serviceTitle);
        var name = string.Join('.', Tokenize(serviceTitle).Select(t => t.Text));
        var before = Tokenize(releaseTitle[..match.TitleStartChar])
            .Where(t => _languageTokens.Contains(t.Normalized))
            .Select(t => t.Text.ToUpperInvariant());
        var tail = Dotted(releaseTitle[match.EndChar..]);
        var parts = new List<string> { name, serviceYear.ToString(CultureInfo.InvariantCulture) };
        parts.AddRange(before);
        if (tail.Length > 0)
        {
            parts.Add(tail);
        }

        return string.Join('.', parts);
    }

    private static bool IsCloseYear(int releaseYear, int year)
        => releaseYear >= year - YearsEarlier && releaseYear <= year + YearsLater;

    private static int? FindYear(List<Token> tokens, int from)
    {
        for (var i = from; i < tokens.Count && i < from + YearReach; i++)
        {
            if (YearRegex().IsMatch(tokens[i].Normalized))
            {
                return i;
            }
        }

        return null;
    }

    private static bool Matches(List<Token> tokens, int start, string[] variant)
    {
        for (var i = 0; i < variant.Length; i++)
        {
            if (!string.Equals(tokens[start + i].Normalized, variant[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // The rest of a release title, from its first letter or digit, with spaces as dots like a scene name.
    private static string Dotted(string rest)
    {
        var first = 0;
        while (first < rest.Length && !char.IsLetterOrDigit(rest[first]))
        {
            first++;
        }

        // Brackets and spaces become dots, as in a scene name: "(1080p) [BluRay]" reads "1080p.BluRay"
        var dotted = SeparatorsRegex().Replace(rest[first..], ".");
        return DotsRegex().Replace(dotted, ".").Trim('.');
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        foreach (Match m in WordRegex().Matches(text))
        {
            var normalized = Normalize(m.Value);
            if (normalized.Length == 0)
            {
                continue;
            }

            // A word normalized into several ("Œuvre" stays one, a word with an inner mark may split)
            foreach (var part in normalized.Split(' '))
            {
                tokens.Add(new Token(m.Value, part, m.Index, m.Index + m.Length));
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^s\d{1,2}(e\d{1,3})?$")]
    private static partial Regex EpisodeTokenRegex();

    [GeneratedRegex(@"[\s()\[\]{}]+")]
    private static partial Regex SeparatorsRegex();

    [GeneratedRegex(@"\.{2,}")]
    private static partial Regex DotsRegex();

    private sealed record Token(string Text, string Normalized, int Start, int End);
}
