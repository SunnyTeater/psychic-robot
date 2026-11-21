using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.NamingStandardizer.Services;

/// <summary>
/// Die Regel, nach der Jellyfin 12.4 mehrere Dateien einer Folge als Versionen zusammenfasst
/// (VideoListResolver.GetEpisodeVersionKey): nur die strengen, nicht "optimistischen" Ausdruecke des
/// EpisodePathParser zaehlen, und sie muessen Staffel und Folge liefern (oder ein Datum).
/// Die Ausdruecke sind unveraendert aus NamingOptions.EpisodeExpressions von Jellyfin 12.4 uebernommen,
/// in derselben Reihenfolge.
/// </summary>
public static class EpisodeNaming
{
    private static readonly Expression[] _expressions =
    {
        new(@".*(\\|\/)(?<seriesname>((?![Ss]([0-9]+)[][ ._-]*[Ee]([0-9]+))[^\\\/])*)?[Ss](?<seasonnumber>[0-9]+)[][ ._-]*[Ee](?<epnumber>[0-9]+)([^\\/]*)$", true, false, false, Array.Empty<string>()),
        new(@"[\._ -]()[Ee][Pp]_?([0-9]+)([^\\/]*)$", false, false, false, Array.Empty<string>()),
        new(@"[^\\/]*?()\.?[Ee]([0-9]+)\.([^\\/]*)$", false, false, false, Array.Empty<string>()),
        new(@"(?<year>[0-9]{4})[._ -](?<month>[0-9]{2})[._ -](?<day>[0-9]{2})", false, false, true, new[] { "yyyy.MM.dd", "yyyy-MM-dd", "yyyy_MM_dd", "yyyy MM dd" }),
        new(@"(?<day>[0-9]{2})[._ -](?<month>[0-9]{2})[._ -](?<year>[0-9]{4})", false, false, true, new[] { "dd.MM.yyyy", "dd-MM-yyyy", "dd_MM_yyyy", "dd MM yyyy" }),
        new(@".*[\\\/]((?<seriesname>[^\\/]+?)\s)?[Ss](?:eason)?\s*(?<seasonnumber>[0-9]+)\s+[Ee](?:pisode)?\s*(?<epnumber>[0-9]+).*$", true, false, false, Array.Empty<string>()),
        new(@".*[\\\/](?![Ee]pisode)(?![^\\\/]*[Ss][0-9]+[][ ._-]*[Ee][0-9]+)(?<seriesname>[\w\s]+?)\s(?<epnumber>[0-9]{1,4})(-(?<endingepnumber>[0-9]{2,4}))*[^\\\/x]*$", true, false, false, Array.Empty<string>()),
        new(@"[\\\/\._ \[\(-]([0-9]+)x([0-9]+(?:(?:[a-i]|\.[1-9])(?![0-9]))?)([^\\\/]*)$", false, false, false, Array.Empty<string>()),
        new(@".*[\\\/]?.*?(\[.*?\])+.*?(?<seriesname>[-\w\s]+?)[\s_]*-[\s_]*(?<epnumber>[0-9]+).*$", true, false, false, Array.Empty<string>()),
        new(@".*[\\\/](?<seriesname>[^\\\/]+?)[\s_]+-[\s_]+(?<epnumber>[0-9]+)[\s_]*(?:\[.*?\]|\(.*?\))*[\s_]*(?:\.\w+)?$", true, false, false, Array.Empty<string>()),
        new(@"[\\/._ -](?<seriesname>(?![0-9]+[0-9][0-9])([^\\\/_])*)[\\\/._ -](?<seasonnumber>[0-9]+)(?<epnumber>[0-9][0-9](?:(?:[a-i]|\.[1-9])(?![0-9]))?)([._ -][^\\\/]*)$", true, true, false, Array.Empty<string>()),
        new(@"[\/._ -]p(?:ar)?t[_. -]()([ivx]+|[0-9]+)([._ -][^\/]*)$", false, false, false, Array.Empty<string>()),
        new(@"[Ee]pisode (?<epnumber>[0-9]+)(-(?<endingepnumber>[0-9]+))?[^\\\/]*$", true, false, false, Array.Empty<string>()),
        new(@".*(\\|\/)[sS]?(?<seasonnumber>[0-9]+)[xX](?<epnumber>[0-9]+)[^\\\/]*$", true, false, false, Array.Empty<string>()),
        new(@".*(\\|\/)[sS](?<seasonnumber>[0-9]+)[x,X]?[eE](?<epnumber>[0-9]+)[^\\\/]*$", true, false, false, Array.Empty<string>()),
        new(@".*(\\|\/)(?<seriesname>((?![sS]?[0-9]{1,4}[xX][0-9]{1,3})[^\\\/])*)?([sS]?(?<seasonnumber>[0-9]{1,4})[xX](?<epnumber>[0-9]+))[^\\\/]*$", true, false, false, Array.Empty<string>()),
        new(@".*(\\|\/)(?<seriesname>[^\\\/]*)[sS](?<seasonnumber>[0-9]{1,4})[xX\.]?[eE](?<epnumber>[0-9]+)[^\\\/]*$", true, false, false, Array.Empty<string>()),
        new(@".*[\\\/](?<epnumber>[0-9]+)(-(?<endingepnumber>[0-9]+))*\.\w+$", true, true, false, Array.Empty<string>()),
        new(@"([0-9]+)-([0-9]+)", false, false, false, Array.Empty<string>()),
        new(@".*(\\|\/)(?<epnumber>[0-9]{1,3})(-(?<endingepnumber>[0-9]{2,3}))*\s?-\s?[^\\\/]*$", true, true, false, Array.Empty<string>()),
        new(@".*(\\|\/)(?<epnumber>[0-9]{1,3})(-(?<endingepnumber>[0-9]{2,3}))*\.[^\\\/]+$", true, true, false, Array.Empty<string>()),
        new(@".*[\\\/][^\\\/]* - (?<epnumber>[0-9]{1,3})(-(?<endingepnumber>[0-9]{2,3}))*[^\\\/]*$", true, true, false, Array.Empty<string>()),
        new(@"[Ss]eason[\._ ](?<seasonnumber>[0-9]+)[\\\/](?<epnumber>[0-9]{1,3})([^\\\/]*)$", true, true, false, Array.Empty<string>()),
        new(@"(.*(\\|\/))*(?<seriesname>.+)\/[Ss](eason)?[\. _\-]*(?<seasonnumber>[0-9]+)", true, false, false, Array.Empty<string>()),
        new(@"(.*(\\|\/))*(?<seriesname>.+)[\. _\-]+[sS](eason)?[\. _\-]*(?<seasonnumber>[0-9]+)", true, false, false, Array.Empty<string>()),
        new(@"(?:\[(?:[^\]]+)\]\s*)?(?<seriesname>\[[^\]]+\]|[^[\]]+)\s*\[(?<epnumber>[0-9]+)\]", true, false, false, Array.Empty<string>()),
    };

    /// <summary>
    /// Der Schluessel, unter dem Jellyfin Versionen einer Folge zusammenfasst ("S1E5" oder "D20240101"),
    /// oder null, wenn Jellyfin die Datei mit keiner anderen zusammenfasst.
    /// </summary>
    /// <param name="path">Vollstaendiger Pfad der Datei.</param>
    /// <returns>Der Schluessel oder null.</returns>
    public static string? VersionKey(string path)
    {
        foreach (var expression in _expressions.Where(e => !e.Optimistic))
        {
            var result = Parse(path, expression);
            if (!result.Success)
            {
                continue;
            }

            // Wie Jellyfin: der erste erfolgreiche Ausdruck entscheidet.
            if (result.ByDate)
            {
                return result.Year.HasValue && result.Month.HasValue && result.Day.HasValue
                    ? FormattableString.Invariant($"D{result.Year.Value}{result.Month.Value:D2}{result.Day.Value:D2}")
                    : null;
            }

            return result.Season.HasValue && result.Episode.HasValue
                ? FormattableString.Invariant($"S{result.Season.Value}E{result.Episode.Value}")
                : null;
        }

        return null;
    }

    private static ParseResult Parse(string name, Expression expression)
    {
        var result = new ParseResult();
        if (expression.ByDate)
        {
            name = name.Replace('_', '-');
        }

        var match = expression.Regex.Match(name);
        if (!match.Success || match.Groups.Count < 3)
        {
            return result;
        }

        if (expression.ByDate)
        {
            if (expression.DateFormats.Length > 0
                && DateTime.TryParseExact(match.Groups[0].Value, expression.DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                result.Year = date.Year;
                result.Month = date.Month;
                result.Day = date.Day;
            }

            result.Success = true;
        }
        else if (expression.Named)
        {
            if (int.TryParse(match.Groups["seasonnumber"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
            {
                result.Season = season;
            }

            if (int.TryParse(match.Groups["epnumber"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode))
            {
                result.Episode = episode;
            }

            result.Success = result.Episode.HasValue;
        }
        else
        {
            if (int.TryParse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
            {
                result.Season = season;
            }

            if (int.TryParse(match.Groups[2].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode))
            {
                result.Episode = episode;
            }

            result.Success = result.Episode.HasValue;
        }

        // Wie Jellyfin: Staffeln 200 bis 1927 und ueber 2500 gelten als Fehlerkennung (etwa "1920x1080").
        if ((result.Season >= 200 && result.Season < 1928) || result.Season > 2500)
        {
            result.Success = false;
        }

        result.ByDate = expression.ByDate;
        return result;
    }

    private sealed class ParseResult
    {
        public bool Success { get; set; }

        public bool ByDate { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public int? Year { get; set; }

        public int? Month { get; set; }

        public int? Day { get; set; }
    }

    private sealed class Expression
    {
        public Expression(string pattern, bool named, bool optimistic, bool byDate, string[] dateFormats)
        {
            Regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Named = named;
            Optimistic = optimistic;
            ByDate = byDate;
            DateFormats = dateFormats;
        }

        public Regex Regex { get; }

        public bool Named { get; }

        public bool Optimistic { get; }

        public bool ByDate { get; }

        public string[] DateFormats { get; }
    }
}
