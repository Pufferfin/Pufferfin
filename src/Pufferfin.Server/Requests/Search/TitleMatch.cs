namespace Pufferfin.Server.Requests.Search;

/// <summary>
/// Where a film's title and year sit in a release title.
/// </summary>
/// <param name="TitleStartToken">The index of the first word of the title among the release's words.</param>
/// <param name="EndChar">The position just after the year, or after the title when the release has no year.</param>
/// <param name="TitleStartChar">The position of the first letter of the title.</param>
/// <param name="Year">The year of the release, if it has one.</param>
public sealed record TitleMatch(int TitleStartToken, int EndChar, int TitleStartChar, int? Year);
