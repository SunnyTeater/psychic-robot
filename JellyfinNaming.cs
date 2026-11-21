using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.NamingStandardizer.Services;

/// <summary>
/// Die Regel, nach der Jellyfin mehrere Videodateien in einem Filmordner als Versionen eines Films
/// zusammenfasst. Nachgebaut aus dem Quellcode von Jellyfin 12.0 bis 12.4 (Emby.Naming: VideoListResolver,
/// CleanStringParser, CleanDateTimeParser, ExtraRuleResolver, NamingOptions), damit das Plugin vor dem
/// Umbenennen pruefen kann, ob Jellyfin die Dateien danach wirklich zusammenfasst.
/// </summary>
/// <remarks>
/// Kurzfassung der Regel: jede Videodatei im Ordner (ausser Extras wie "-trailer") beginnt mit dem
/// Ordnernamen, Zeichen fuer Zeichen; was danach kommt, beginnt mit "-", "_", "." oder "[...]" oder
/// verschwindet bei Jellyfins Bereinigung; und alle Dateien ergeben dasselbe Jahr.
/// </remarks>
public static class JellyfinNaming
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // NamingOptions.CleanStrings, unveraendert uebernommen.
    private static readonly Regex[] _cleanStrings =
    {
        new(@"^\s*(?<cleaned>.+?)[ _\,\.\(\)\[\]\-](3d|sbs|tab|hsbs|htab|mvc|HDR|HDC|UHD|UltraHD|4k|ac3|dts|custom|dc|divx|divx5|dsr|dsrip|dutch|dvd|dvdrip|dvdscr|dvdscreener|screener|dvdivx|cam|fragment|fs|hdtv|hdrip|hdtvrip|internal|limited|multi|subs|ntsc|ogg|ogm|pal|pdtv|proper|repack|rerip|retail|cd[1-9]|r5|bd5|bd|se|svcd|swedish|german|read.nfo|nfofix|unrated|ws|web-dl|telesync|ts|telecine|tc|brrip|bdrip|480p|480i|576p|576i|720p|720i|1080p|1080i|2160p|hrhd|hrhdtv|hddvd|bluray|blu-ray|x264|x265|h264|h265|xvid|xvidvd|xxx|www.www|AAC|DTS)(?=[ _\,\.\(\)\[\]\-]|$)", Options),
        new(@"^\s*(?<cleaned>.+?)((\s*\[[^\]]+\]\s*)+)(\.[^\s]+)?$", Options),
        new(@"^\s*(?<cleaned>.+?)\WE[0-9]+(-|~)E?[0-9]+(\W|$)", Options),
        new(@"^\s*\[[^\]]+\](?!\.\w+$)\s*(?<cleaned>.+)", Options),
        new(@"^\s*(?<cleaned>.+?)\s+-\s+[0-9]+\s*$", Options),
        new(@"^\s*(?<cleaned>.+?)(([-._ ](trailer|sample))|-(scene|clip|behindthescenes|deleted|deletedscene|featurette|short|interview|other|extra))$", Options)
    };

    // NamingOptions.CleanDateTimes, unveraendert uebernommen.
    private static readonly Regex[] _cleanDateTimes =
    {
        new(@"(.+[^_\,\.\(\)\[\]\-])[_\.\(\)\[\]\-](19[0-9]{2}|20[0-9]{2})(?![0-9]+|\W[0-9]{2}\W[0-9]{2})([ _\,\.\(\)\[\]\-][^0-9]|).*(19[0-9]{2}|20[0-9]{2})*", Options),
        new(@"(.+[^_\,\.\(\)\[\]\-])[ _\.\(\)\[\]\-]+(19[0-9]{2}|20[0-9]{2})(?![0-9]+|\W[0-9]{2}\W[0-9]{2})([ _\,\.\(\)\[\]\-][^0-9]|).*(19[0-9]{2}|20[0-9]{2})*", Options)
    };

    // VideoListResolver.CheckMultiVersionRegex (ohne IgnoreCase, wie im Original).
    private static readonly Regex _multiVersion = new(@"^\[([^]]*)\]", RegexOptions.CultureInvariant);

    // NamingOptions.VideoFileExtensions.
    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".001", ".3g2", ".3gp", ".amv", ".asf", ".asx", ".avi", ".bin", ".bivx", ".divx", ".dv", ".dvr-ms", ".f4v",
        ".fli", ".flv", ".ifo", ".img", ".iso", ".m2t", ".m2ts", ".m2v", ".m4v", ".mkv", ".mk3d", ".mov", ".mp4",
        ".mpe", ".mpeg", ".mpg", ".mts", ".mxf", ".nrg", ".nsv", ".nuv", ".ogm", ".ogv", ".pva", ".qt", ".rec",
        ".rm", ".rmvb", ".strm", ".svq3", ".tp", ".ts", ".ty", ".viv", ".vob", ".vp3", ".webm", ".wmv", ".wtv", ".xvid"
    };

    // NamingOptions.VideoExtraRules fuer Dateinamen (Ordner-Regeln betreffen nur Unterordner).
    private static readonly string[] _extraNames = { "trailer", "sample" };

    private static readonly string[] _extraSuffixes =
    {
        "-trailer", ".trailer", "_trailer", "- trailer", "-sample", ".sample", "_sample", "- sample", "-scene", "-clip",
        "-interview", "-behindthescenes", "-deleted", "-deletedscene", "-featurette", "-short", "-extra", "-other"
    };

    /// <summary>
    /// Ob Jellyfin eine Datei als Videodatei behandelt.
    /// </summary>
    /// <param name="path">Pfad oder Dateiname.</param>
    /// <returns>True fuer Videodateien.</returns>
    public static bool IsVideoFile(string path) => _videoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Ob Jellyfin eine Videodatei am Namen als Extra erkennt (Trailer, Sample, Featurette ...).
    /// Extras zaehlen bei den Versionen nicht mit.
    /// </summary>
    /// <param name="fileNameWithoutExtension">Dateiname ohne Endung.</param>
    /// <returns>True fuer Extras.</returns>
    public static bool IsExtra(string fileNameWithoutExtension)
    {
        var trimmed = fileNameWithoutExtension.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return _extraNames.Any(n => trimmed.Equals(n, StringComparison.OrdinalIgnoreCase))
            || _extraSuffixes.Any(s => trimmed.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ob ein Dateiname als Version im Ordner taugt (VideoListResolver.IsEligibleForMultiVersion).
    /// </summary>
    /// <param name="folderName">Name des Filmordners.</param>
    /// <param name="fileNameWithoutExtension">Dateiname ohne Endung.</param>
    /// <returns>True, wenn Jellyfin die Datei als Version akzeptiert.</returns>
    public static bool IsEligible(string folderName, string fileNameWithoutExtension)
    {
        if (!fileNameWithoutExtension.StartsWith(folderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = fileNameWithoutExtension;
        if (folderName.Length <= rest.Length)
        {
            rest = rest[folderName.Length..].Trim();
        }

        if (TryClean(rest, out var cleaned))
        {
            rest = cleaned.Trim();
        }

        return rest.Length == 0 || rest[0] == '-' || rest[0] == '_' || rest[0] == '.' || _multiVersion.IsMatch(rest);
    }

    /// <summary>
    /// Das Jahr, das Jellyfin aus einem Dateinamen liest (CleanDateTimeParser), oder null.
    /// </summary>
    /// <param name="fileNameWithoutExtension">Dateiname ohne Endung.</param>
    /// <returns>Das Jahr oder null.</returns>
    public static int? ParseYear(string fileNameWithoutExtension)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExtension))
        {
            return null;
        }

        foreach (var expression in _cleanDateTimes)
        {
            var match = expression.Match(fileNameWithoutExtension);
            if (match.Success && match.Groups.Count == 5 && match.Groups[1].Success && match.Groups[2].Success
                && int.TryParse(match.Groups[2].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return year;
            }
        }

        return null;
    }

    /// <summary>
    /// Ob Jellyfin diese Dateien (ohne Endung, ohne Extras) im Ordner als Versionen eines Films zusammenfasst.
    /// </summary>
    /// <param name="folderName">Name des Filmordners.</param>
    /// <param name="fileNamesWithoutExtension">Alle Videodateien des Ordners ausser Extras.</param>
    /// <returns>True, wenn Jellyfin sie zusammenfasst.</returns>
    public static bool WouldGroup(string folderName, IReadOnlyCollection<string> fileNamesWithoutExtension)
    {
        if (folderName.Length <= 1)
        {
            return false;
        }

        if (fileNamesWithoutExtension.Select(ParseYear).Distinct().Count() > 1)
        {
            return false;
        }

        return fileNamesWithoutExtension.All(name => IsEligible(folderName, name));
    }

    // CleanStringParser.TryClean: alle Ausdruecke nacheinander auf das jeweils bereinigte Ergebnis.
    private static bool TryClean(string name, out string result)
    {
        result = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var cleaned = false;
        foreach (var expression in _cleanStrings)
        {
            var match = expression.Match(name);
            if (match.Success && match.Groups.TryGetValue("cleaned", out var group))
            {
                cleaned = true;
                name = group.Value.Trim();
            }
        }

        result = cleaned ? name : string.Empty;
        return cleaned;
    }
}
