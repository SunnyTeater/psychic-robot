using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NamingStandardizer.Services;

/// <summary>
/// Prueft die Bibliothek und plant je Ordner die Umbenennungen nach Jellyfins Regeln:
/// Filme beginnen mit dem Ordnernamen ("Ordner-HEVC"), Folgen tragen Staffel und Folge ("Serie S01E05").
/// Umbenannt wird hier nichts.
/// </summary>
public sealed class Planner
{
    private const int MaxNameBytes = 240;

    // Unterordner, die Jellyfin als Extras liest; sie machen aus einem Filmordner keine Sammlung.
    private static readonly HashSet<string> _extraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "extra", "trailers", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes",
        "samples", "sample", "shorts", "other", "clips", "backdrops", "theme-music", "subs", "subtitles"
    };

    private static readonly (Regex Pattern, string Label)[] _editions =
    {
        (new Regex(@"(?<![a-z0-9])extended(?![a-z0-9])", RegexOptions.IgnoreCase), "Extended"),
        (new Regex(@"(?<![a-z0-9])director'?s[ ._-]?cut(?![a-z0-9])", RegexOptions.IgnoreCase), "DirectorsCut"),
        (new Regex(@"(?<![a-z0-9])final[ ._-]?cut(?![a-z0-9])", RegexOptions.IgnoreCase), "FinalCut"),
        (new Regex(@"(?<![a-z0-9])unrated(?![a-z0-9])", RegexOptions.IgnoreCase), "Unrated"),
        (new Regex(@"(?<![a-z0-9])uncut(?![a-z0-9])", RegexOptions.IgnoreCase), "Uncut"),
        (new Regex(@"(?<![a-z0-9])remaster(ed)?(?![a-z0-9])", RegexOptions.IgnoreCase), "Remastered"),
        (new Regex(@"(?<![a-z0-9])theatrical(?![a-z0-9])", RegexOptions.IgnoreCase), "Theatrical"),
        (new Regex(@"(?<![a-z0-9])imax(?![a-z0-9])", RegexOptions.IgnoreCase), "IMAX"),
        (new Regex(@"(?<![a-z0-9])special[ ._-]?edition(?![a-z0-9])", RegexOptions.IgnoreCase), "SpecialEdition"),
        (new Regex(@"(?<![a-z0-9])ultimate(?![a-z0-9])", RegexOptions.IgnoreCase), "Ultimate"),
        (new Regex(@"(?<![a-z0-9])kinofassung(?![a-z0-9])", RegexOptions.IgnoreCase), "Kinofassung"),
        (new Regex(@"(?<![a-z0-9])langfassung(?![a-z0-9])", RegexOptions.IgnoreCase), "Langfassung")
    };

    private static readonly Regex _hevcToken = new(@"(?<![a-z0-9])(x\.?265|h\.?265|hevc)(?![a-z0-9])", RegexOptions.IgnoreCase);
    private static readonly Regex _h264Token = new(@"(?<![a-z0-9])(x\.?264|h\.?264|avc)(?![a-z0-9])", RegexOptions.IgnoreCase);
    private static readonly Regex _av1Token = new(@"(?<![a-z0-9])av1(?![a-z0-9])", RegexOptions.IgnoreCase);
    private static readonly Regex _xvidToken = new(@"(?<![a-z0-9])(xvid|divx)(?![a-z0-9])", RegexOptions.IgnoreCase);
    private static readonly Regex _seasonFolder = new(@"^(season|staffel|s)[ ._-]*\d+$|^specials$", RegexOptions.IgnoreCase);

    private readonly object _lock = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<Planner> _logger;
    private readonly LiveLog _log;
    private ScanState _state = new();
    private List<FolderPlan> _plans = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Planner"/> class.
    /// </summary>
    /// <param name="libraryManager">Die Bibliothek.</param>
    /// <param name="paths">Die Serverpfade.</param>
    /// <param name="logger">Der Logger.</param>
    /// <param name="log">Das Protokoll fuer die Seite.</param>
    public Planner(ILibraryManager libraryManager, IApplicationPaths paths, ILogger<Planner> logger, LiveLog log)
    {
        _libraryManager = libraryManager;
        _paths = paths;
        _logger = logger;
        _log = log;
    }

    /// <summary>
    /// Gets den Stand der Pruefung (Kopie).
    /// </summary>
    public ScanState State
    {
        get
        {
            lock (_lock)
            {
                return new ScanState
                {
                    Running = _state.Running,
                    Checked = _state.Checked,
                    Total = _state.Total,
                    Finished = _state.Finished,
                    Standard = _state.Standard,
                    Flat = _state.Flat,
                    Error = _state.Error
                };
            }
        }
    }

    /// <summary>
    /// Gets die Ergebnisse der letzten Pruefung.
    /// </summary>
    public List<FolderPlan> Plans
    {
        get
        {
            lock (_lock)
            {
                return _plans.ToList();
            }
        }
    }

    /// <summary>
    /// Startet eine Pruefung im Hintergrund.
    /// </summary>
    /// <returns>False, wenn schon eine laeuft.</returns>
    public bool StartScan()
    {
        lock (_lock)
        {
            if (_state.Running)
            {
                return false;
            }

            _state = new ScanState { Running = true };
        }

        _ = Task.Run(Scan);
        return true;
    }

    /// <summary>
    /// Sucht einen Plan der letzten Pruefung.
    /// </summary>
    /// <param name="id">Die Kennung.</param>
    /// <returns>Der Plan oder null.</returns>
    public FolderPlan? Find(string id)
    {
        lock (_lock)
        {
            return _plans.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Nimmt einen erledigten Plan aus der Liste.
    /// </summary>
    /// <param name="id">Die Kennung.</param>
    public void Remove(string id)
    {
        lock (_lock)
        {
            _plans.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Plant einen Ordner frisch, direkt vor dem Umbenennen.
    /// </summary>
    /// <param name="kind">"Film" oder "Serie".</param>
    /// <param name="folder">Der Ordner.</param>
    /// <returns>Der Plan oder null, wenn nichts (mehr) zu tun ist.</returns>
    public FolderPlan? Replan(string kind, string folder)
    {
        var converter = ConverterFolders();
        return kind == "Film" ? PlanMovieFolder(folder, converter, out _) : PlanEpisodeFolder(folder, converter, out _);
    }

    /// <summary>
    /// Ob ein Pfad in einer Bibliothek liegt.
    /// </summary>
    /// <param name="path">Der Pfad.</param>
    /// <returns>True, wenn ja.</returns>
    public bool InLibrary(string path)
    {
        var full = Path.GetFullPath(path);
        return LibraryRoots().Any(r => full.StartsWith(WithSeparator(r), StringComparison.Ordinal));
    }

    private static string WithSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private List<string> LibraryRoots()
        => _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Select(l => Path.GetFullPath(l).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();

    private void Scan()
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true
            });

            var movieFolders = new HashSet<string>(StringComparer.Ordinal);
            var episodeFolders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item is not Video video || !video.IsFileProtocol || video.VideoType != VideoType.VideoFile || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                var folder = Path.GetDirectoryName(item.Path);
                if (folder is null)
                {
                    continue;
                }

                (item is Episode ? episodeFolders : movieFolders).Add(folder);
            }

            // Ein Ordner mit Filmen und Folgen zugleich ist unklar: nicht anfassen.
            var mixed = movieFolders.Intersect(episodeFolders).ToList();
            movieFolders.ExceptWith(mixed);
            episodeFolders.ExceptWith(mixed);

            var work = movieFolders.Select(f => ("Film", f)).Concat(episodeFolders.Select(f => ("Serie", f))).ToList();
            lock (_lock)
            {
                _state.Total = work.Count;
            }

            var converter = ConverterFolders();
            var plans = new List<FolderPlan>();
            int standard = 0, flat = 0;
            foreach (var (kind, folder) in work)
            {
                string outcome;
                FolderPlan? plan;
                try
                {
                    plan = kind == "Film" ? PlanMovieFolder(folder, converter, out outcome) : PlanEpisodeFolder(folder, converter, out outcome);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    plan = new FolderPlan { Kind = kind, Folder = folder, Display = DisplayName(folder), Blocked = "Ordner nicht lesbar: " + ex.Message };
                    plan.Id = Hash(plan, Array.Empty<string>());
                    outcome = "plan";
                }

                if (plan is not null)
                {
                    plans.Add(plan);
                }
                else if (outcome == "standard")
                {
                    standard++;
                }
                else if (outcome == "flat")
                {
                    flat++;
                }

                lock (_lock)
                {
                    _state.Checked++;
                }
            }

            plans = plans.OrderBy(p => p.Blocked is null ? 0 : 1).ThenBy(p => p.Kind).ThenBy(p => p.Display, StringComparer.OrdinalIgnoreCase).ToList();
            lock (_lock)
            {
                _plans = plans;
                _state.Standard = standard;
                _state.Flat = flat;
            }

            var open = plans.Count(p => p.Blocked is null);
            _log.Add("Prüfung fertig: " + work.Count + " Ordner, " + standard + " passen schon, " + open + " lassen sich standardisieren, "
                + (plans.Count - open) + " gehen nicht" + (flat > 0 ? ", " + flat + " Film(e) ohne eigenen Ordner" : string.Empty) + ".");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Naming Standardizer: Prüfung fehlgeschlagen");
            lock (_lock)
            {
                _state.Error = ex.Message;
            }
        }
        finally
        {
            lock (_lock)
            {
                _state.Running = false;
                _state.Finished = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Filmordner: jede Videodatei muss mit dem Ordnernamen beginnen. Eine einzelne Datei heisst wie der Ordner,
    /// mehrere bekommen "Ordner-Bezeichnung" (Codec, ggf. Fassung und Aufloesung).
    /// </summary>
    private FolderPlan? PlanMovieFolder(string folder, HashSet<string> converter, out string outcome)
    {
        outcome = "nothing";
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var files = Directory.GetFiles(folder);
        var videos = files.Where(f => JellyfinNaming.IsVideoFile(f) && !JellyfinNaming.IsExtra(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (videos.Count == 0)
        {
            return null;
        }

        var name = Path.GetFileName(folder);
        if (IsLibraryRoot(folder) || name.Length <= 1)
        {
            outcome = "flat";
            return null;
        }

        var nonEligible = videos.Where(v => !JellyfinNaming.IsEligible(name, Path.GetFileNameWithoutExtension(v))).ToList();
        if (nonEligible.Count == 0)
        {
            outcome = "standard";
            return null;
        }

        outcome = "plan";
        var plan = new FolderPlan { Kind = "Film", Folder = folder, Display = DisplayName(folder) };
        plan.Kept = videos.Except(nonEligible).Select(Path.GetFileName).ToList()!;
        if (converter.Contains(folder) || files.Any(f => f.EndsWith(".ccpart", StringComparison.OrdinalIgnoreCase)))
        {
            return Blocked(plan, "Der Codec Converter bearbeitet diesen Film gerade (Job offen oder wartet auf Freigabe). Bitte danach erneut prüfen.", videos);
        }

        if (HasMovieSubfolders(folder))
        {
            return Blocked(plan, "Im Ordner liegen weitere Ordner mit Videos (etwa eine Filmsammlung). Er wird nicht als Filmordner behandelt.", videos);
        }

        var items = videos.ToDictionary(v => v, v => _libraryManager.FindByPath(v, false), StringComparer.Ordinal);
        var taken = new HashSet<string>(plan.Kept.Select(k => Path.GetFileNameWithoutExtension(k)), StringComparer.OrdinalIgnoreCase);
        var moves = new List<(string From, string To)>();
        if (videos.Count == 1)
        {
            moves.Add((nonEligible[0], Path.Combine(folder, name + Path.GetExtension(nonEligible[0]))));
        }
        else
        {
            foreach (var (video, newBase) in AssignNames(name, nonEligible.Select(v => (v, items[v])).ToList(), taken))
            {
                moves.Add((video, Path.Combine(folder, newBase + Path.GetExtension(video))));
            }
        }

        var problem = CheckMoves(folder, moves);
        if (problem is null && videos.Count > 1)
        {
            var bases = plan.Kept.Select(Path.GetFileNameWithoutExtension).Concat(moves.Select(m => Path.GetFileNameWithoutExtension(m.To))).ToList();
            if (!JellyfinNaming.WouldGroup(name, bases!))
            {
                problem = "Jellyfin würde die Dateien auch danach nicht zusammenfassen (unterschiedliche Jahreszahlen in den Dateinamen).";
            }
        }

        plan.Videos = moves.Select(m => new RenameEntry { From = Path.GetFileName(m.From), To = Path.GetFileName(m.To) }).ToList();
        if (problem is not null)
        {
            return Blocked(plan, problem, videos);
        }

        plan.Companions = CompanionMoves(folder, files, moves);
        var same = SameMovie(videos.Select(v => items[v]).ToList(), name);
        plan.Recommended = same;
        if (!same)
        {
            plan.Warning = videos.Count == 1
                ? "Jellyfin hat den Film nicht eindeutig erkannt (keine IMDb- oder TMDb-Zuordnung, die zum Ordner passt). Bitte prüfen, ob der Ordner wirklich zu diesem Film gehört."
                : "Die Dateien gehören vielleicht zu verschiedenen Filmen. Nach dem Umbenennen zeigt Jellyfin sie als Versionen eines Films. Bitte prüfen.";
        }

        plan.Id = Hash(plan, videos);
        return plan;
    }

    /// <summary>
    /// Folgen: Jellyfin fasst nur zusammen, was Staffel und Folge eindeutig im Namen traegt ("S01E05", "1x05").
    /// Folgen ohne das bekommen "Serienordner S01E05", bei mehreren Dateien derselben Folge mit Bezeichnung.
    /// </summary>
    private FolderPlan? PlanEpisodeFolder(string folder, HashSet<string> converter, out string outcome)
    {
        outcome = "nothing";
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var files = Directory.GetFiles(folder);
        var videos = files.Where(f => JellyfinNaming.IsVideoFile(f) && !JellyfinNaming.IsExtra(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (videos.Count == 0)
        {
            return null;
        }

        var kept = new List<(string Path, string Key)>();
        var planned = new List<(string Path, Episode Item, int Season, int Number, int? End, string Key)>();
        var unresolved = new List<string>();
        foreach (var video in videos)
        {
            var key = EpisodeNaming.VersionKey(video);
            if (key is not null)
            {
                kept.Add((video, key));
                continue;
            }

            var item = _libraryManager.FindByPath(video, false) as Episode;
            var season = item?.ParentIndexNumber ?? item?.Season?.IndexNumber;
            if (item?.IndexNumber is not int number || season is not int s)
            {
                unresolved.Add(Path.GetFileName(video));
                continue;
            }

            planned.Add((video, item, s, number, item.IndexNumberEnd, FormattableString.Invariant($"S{s}E{number}")));
        }

        if (planned.Count == 0)
        {
            if (unresolved.Count == 0)
            {
                outcome = "standard";
                return null;
            }

            outcome = "plan";
            var none = new FolderPlan { Kind = "Serie", Folder = folder, Display = DisplayName(folder), Kept = kept.Select(k => Path.GetFileName(k.Path)).ToList() };
            return Blocked(none, "Jellyfin kennt Staffel und Folge dieser Dateien nicht: " + string.Join(", ", unresolved.Take(10)) + (unresolved.Count > 10 ? " …" : string.Empty), videos);
        }

        outcome = "plan";
        var plan = new FolderPlan { Kind = "Serie", Folder = folder, Display = DisplayName(folder), Kept = kept.Select(k => Path.GetFileName(k.Path)).ToList() };
        if (converter.Contains(folder) || files.Any(f => f.EndsWith(".ccpart", StringComparison.OrdinalIgnoreCase)))
        {
            return Blocked(plan, "Der Codec Converter bearbeitet hier gerade eine Folge (Job offen oder wartet auf Freigabe). Bitte danach erneut prüfen.", videos);
        }

        var series = SeriesFolderName(folder, planned[0].Item);
        if (string.IsNullOrWhiteSpace(series))
        {
            return Blocked(plan, "Der Serienordner ließ sich nicht bestimmen.", videos);
        }

        var groups = kept.Select(k => k.Key).Concat(planned.Select(p => p.Key)).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var taken = new HashSet<string>(kept.Select(k => Path.GetFileNameWithoutExtension(k.Path)), StringComparer.OrdinalIgnoreCase);
        var moves = new List<(string From, string To)>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in planned.GroupBy(p => (p.Season, p.Number, p.End)))
        {
            var baseName = series + FormattableString.Invariant($" S{group.Key.Season:D2}E{group.Key.Number:D2}")
                + (group.Key.End > group.Key.Number ? FormattableString.Invariant($"-E{group.Key.End:D2}") : string.Empty);
            var members = group.ToList();
            if (groups[members[0].Key] == 1 && !taken.Contains(baseName))
            {
                names[members[0].Path] = baseName;
                taken.Add(baseName);
                continue;
            }

            foreach (var (video, newBase) in AssignNames(baseName, members.Select(m => (m.Path, (BaseItem?)m.Item)).ToList(), taken))
            {
                names[video] = newBase;
            }
        }

        foreach (var episode in planned)
        {
            var newBase = names[episode.Path];
            var target = Path.Combine(folder, newBase + Path.GetExtension(episode.Path));

            // Nur umbenennen, wenn Jellyfin den neuen Namen sicher als diese Folge liest.
            if (!string.Equals(EpisodeNaming.VersionKey(target), episode.Key, StringComparison.Ordinal))
            {
                unresolved.Add(Path.GetFileName(episode.Path));
                continue;
            }

            moves.Add((episode.Path, target));
        }

        plan.Videos = moves.Select(m => new RenameEntry { From = Path.GetFileName(m.From), To = Path.GetFileName(m.To) }).ToList();
        if (moves.Count == 0)
        {
            return Blocked(plan, "Für diese Dateien ließ sich kein eindeutiger Name bilden: " + string.Join(", ", unresolved.Take(10)), videos);
        }

        var problem = CheckMoves(folder, moves);
        if (problem is not null)
        {
            return Blocked(plan, problem, videos);
        }

        plan.Companions = CompanionMoves(folder, files, moves);
        plan.Recommended = true;
        if (unresolved.Count > 0)
        {
            plan.Warning = "Bleiben unverändert, weil Staffel oder Folge unbekannt ist: " + string.Join(", ", unresolved.Take(10)) + (unresolved.Count > 10 ? " …" : string.Empty);
        }

        plan.Id = Hash(plan, videos);
        return plan;
    }

    private static FolderPlan Blocked(FolderPlan plan, string reason, IEnumerable<string> videos)
    {
        plan.Blocked = reason;
        plan.Recommended = false;
        plan.Id = Hash(plan, videos);
        return plan;
    }

    /// <summary>
    /// Prueft die neuen Namen: gueltig, nicht zu lang, nicht schon vergeben.
    /// </summary>
    private static string? CheckMoves(string folder, List<(string From, string To)> moves)
    {
        var sources = new HashSet<string>(moves.Select(m => m.From), StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, to) in moves)
        {
            var fileName = Path.GetFileName(to);
            if (!string.Equals(Path.GetDirectoryName(to), folder, StringComparison.Ordinal)
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName.Any(char.IsControl)
                || fileName.StartsWith('.'))
            {
                return "Ungültiger neuer Dateiname: " + fileName;
            }

            if (Encoding.UTF8.GetByteCount(fileName) > MaxNameBytes)
            {
                return "Der neue Dateiname wäre zu lang: " + fileName;
            }

            if (!targets.Add(to) || ((File.Exists(to) || Directory.Exists(to)) && !sources.Contains(to)))
            {
                return "Die Datei " + fileName + " gibt es schon.";
            }
        }

        return null;
    }

    /// <summary>
    /// Begleitdateien wandern mit ihrer Videodatei. Eine Begleitdatei gehoert zu dem Video mit dem laengsten
    /// passenden Namen ("Film-HEVC.de.srt" gehoert zu "Film-HEVC", nicht zu "Film").
    /// </summary>
    private static List<RenameEntry> CompanionMoves(string folder, string[] files, List<(string From, string To)> moves)
    {
        var videoBases = files.Where(f => JellyfinNaming.IsVideoFile(f) && !JellyfinNaming.IsExtra(Path.GetFileNameWithoutExtension(f)))
            .Select(Path.GetFileNameWithoutExtension).ToList();
        var renamed = moves.ToDictionary(m => Path.GetFileNameWithoutExtension(m.From), m => Path.GetFileNameWithoutExtension(m.To), StringComparer.Ordinal);
        var sources = new HashSet<string>(moves.Select(m => m.From), StringComparer.Ordinal);
        var targets = new HashSet<string>(moves.Select(m => m.To), StringComparer.OrdinalIgnoreCase);
        var result = new List<RenameEntry>();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (sources.Contains(file)
                || (JellyfinNaming.IsVideoFile(file) && !JellyfinNaming.IsExtra(Path.GetFileNameWithoutExtension(file)))
                || new FileInfo(file).LinkTarget is not null)
            {
                continue;
            }

            var owner = videoBases
                .Where(b => name.Length > b!.Length && name.StartsWith(b, StringComparison.Ordinal) && name[b.Length] is '.' or '-' or '_')
                .OrderByDescending(b => b!.Length)
                .FirstOrDefault();
            if (owner is null || !renamed.TryGetValue(owner, out var newBase))
            {
                continue;
            }

            var target = Path.Combine(folder, newBase + name[owner.Length..]);
            if (targets.Contains(target) || File.Exists(target) || Directory.Exists(target)
                || Encoding.UTF8.GetByteCount(Path.GetFileName(target)) > 255)
            {
                continue;
            }

            targets.Add(target);
            result.Add(new RenameEntry { From = name, To = Path.GetFileName(target) });
        }

        return result;
    }

    // Bezeichnung einer Version: Fassung (falls im Namen) und Codec, ohne Leerzeichen.
    private static string Label(string path, BaseItem? item)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = _editions.Where(e => e.Pattern.IsMatch(name)).Select(e => e.Label).ToList();
        var codec = item?.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Codec;
        var codecLabel = !string.IsNullOrEmpty(codec) ? CodecLabel(codec.ToLowerInvariant())
            : _hevcToken.IsMatch(name) ? "HEVC"
            : _h264Token.IsMatch(name) ? "H264"
            : _av1Token.IsMatch(name) ? "AV1"
            : _xvidToken.IsMatch(name) ? "MPEG4"
            : null;
        if (codecLabel is not null)
        {
            parts.Add(codecLabel);
        }

        return parts.Count > 0 ? string.Join("-", parts) : "Version";
    }

    /// <summary>
    /// Kurzname eines Codecs, nur Buchstaben und Ziffern (wie im Codec Converter).
    /// </summary>
    /// <param name="codec">Der Codec, wie ffprobe ihn nennt.</param>
    /// <returns>Der Kurzname.</returns>
    public static string CodecLabel(string codec)
    {
        var label = codec switch
        {
            "hevc" => "HEVC",
            "av1" => "AV1",
            "h264" => "H264",
            "mpeg2video" => "MPEG2",
            "mpeg1video" => "MPEG1",
            "mpeg4" or "msmpeg4v3" or "msmpeg4v2" => "MPEG4",
            "vc1" => "VC1",
            "wmv3" or "wmv2" or "wmv1" => "WMV",
            "vp9" => "VP9",
            "vp8" => "VP8",
            _ => new string((codec ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant()
        };
        return label.Length == 0 ? "VIDEO" : label;
    }

    private static string? Resolution(BaseItem? item)
    {
        var stream = item?.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (stream?.Width is not int width || width <= 0)
        {
            return null;
        }

        return width >= 3200 ? "2160p" : width >= 1800 ? "1080p" : width >= 1200 ? "720p"
            : stream.Height is int height && height > 0 ? height.ToString(CultureInfo.InvariantCulture) + "p" : null;
    }

    /// <summary>
    /// Vergibt "Praefix-Bezeichnung" fuer mehrere Dateien. Haben mehrere dieselbe Bezeichnung, kommt die Aufloesung
    /// dazu, wenn sie die Dateien unterscheidet, sonst ein Zaehler ("-2", "-3"). Belegte Namen werden ausgelassen.
    /// </summary>
    private static List<(string Video, string Base)> AssignNames(string prefix, List<(string Video, BaseItem? Item)> videos, HashSet<string> taken)
    {
        var labelled = videos.OrderBy(v => v.Video, StringComparer.Ordinal)
            .Select(v => (v.Video, Label: Label(v.Video, v.Item), Resolution: Resolution(v.Item)))
            .ToList();
        var result = new List<(string Video, string Base)>();
        foreach (var group in labelled.GroupBy(v => v.Label, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            var byResolution = members.Count > 1
                && members.All(m => m.Resolution is not null)
                && members.Select(m => m.Resolution).Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Count;
            for (var i = 0; i < members.Count; i++)
            {
                var label = byResolution ? members[i].Label + "-" + members[i].Resolution
                    : i == 0 ? members[i].Label
                    : members[i].Label + "-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                var candidate = prefix + "-" + label;
                for (var n = 2; taken.Contains(candidate); n++)
                {
                    candidate = prefix + "-" + label + "-" + n.ToString(CultureInfo.InvariantCulture);
                }

                taken.Add(candidate);
                result.Add((members[i].Video, candidate));
            }
        }

        return result;
    }

    /// <summary>
    /// Ob alle Dateien zu demselben Film gehoeren und der Ordner zu diesem Film passt. Nur dann wird der Ordner
    /// vorausgewaehlt; sonst muss ein Mensch entscheiden.
    /// </summary>
    private static bool SameMovie(List<BaseItem?> items, string folderName)
    {
        if (items.Any(i => i is not Movie))
        {
            return false;
        }

        var movies = items.Cast<Movie>().ToList();
        if (!movies.Any(m => FolderMatches(m, folderName)))
        {
            return false;
        }

        if (movies.Count == 1)
        {
            // Bei einem einzelnen Film benennt Jellyfin ihn nach dem Ordner. Sicher ist das nur, wenn Jellyfin ihn
            // auch einem Eintrag bei IMDb oder TMDb zugeordnet hat.
            return !string.IsNullOrEmpty(movies[0].GetProviderId(MetadataProvider.Imdb)) || !string.IsNullOrEmpty(movies[0].GetProviderId(MetadataProvider.Tmdb));
        }

        var ids = movies.Select(m => m.GetProviderId(MetadataProvider.Imdb) ?? m.GetProviderId(MetadataProvider.Tmdb)).ToList();
        if (ids.All(id => !string.IsNullOrEmpty(id)) && ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return true;
        }

        if (movies.Select(m => m.PrimaryVersionId ?? m.Id).Distinct().Count() == 1)
        {
            return true;
        }

        return movies.Select(m => Normalize(m.Name) + "|" + m.ProductionYear).Distinct(StringComparer.Ordinal).Count() == 1
            && movies[0].ProductionYear.HasValue;
    }

    private static bool FolderMatches(Movie movie, string folderName)
    {
        var folder = Normalize(folderName);
        var imdb = movie.GetProviderId(MetadataProvider.Imdb);
        var tmdb = movie.GetProviderId(MetadataProvider.Tmdb);
        if ((!string.IsNullOrEmpty(imdb) && folderName.Contains(imdb, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(tmdb) && folderName.Contains("tmdbid-" + tmdb, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (movie.ProductionYear is int year && folderName.Contains(year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return true;
        }

        return new[] { movie.Name, movie.OriginalTitle }
            .Select(Normalize)
            .Any(n => n.Length >= 2 && folder.Contains(n, StringComparison.Ordinal));
    }

    private static string Normalize(string? text)
        => new string((text ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool HasMovieSubfolders(string folder)
    {
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            if (_extraFolders.Contains(Path.GetFileName(directory)))
            {
                continue;
            }

            if (Directory.EnumerateFiles(directory).Any(JellyfinNaming.IsVideoFile))
            {
                return true;
            }
        }

        return false;
    }

    private static string? SeriesFolderName(string folder, Episode item)
    {
        var seriesPath = item.Series?.Path;
        if (!string.IsNullOrEmpty(seriesPath))
        {
            return Path.GetFileName(seriesPath.TrimEnd(Path.DirectorySeparatorChar));
        }

        var name = Path.GetFileName(folder);
        return _seasonFolder.IsMatch(name) ? Path.GetFileName(Path.GetDirectoryName(folder)) : name;
    }

    private bool IsLibraryRoot(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        return LibraryRoots().Any(r => string.Equals(r, full, StringComparison.Ordinal));
    }

    private string DisplayName(string folder)
    {
        foreach (var library in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in library.Locations ?? Array.Empty<string>())
            {
                var root = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar);
                if (folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return library.Name + ": " + Path.GetRelativePath(root, folder);
                }
            }
        }

        return Path.GetFileName(folder);
    }

    /// <summary>
    /// Ordner, in denen der Codec Converter einen offenen Job hat. Dort wird nichts umbenannt, sonst fande der
    /// Job seine Dateien nicht mehr.
    /// </summary>
    private HashSet<string> ConverterFolders()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var file = Path.Combine(_paths.DataPath, "codec-converter", "jobs.json");
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 64L * 1024 * 1024)
            {
                return result;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var job in document.RootElement.EnumerateArray())
            {
                var state = job.TryGetProperty("State", out var s) ? s.ToString() : string.Empty;
                if (state is not ("Queued" or "Running" or "Paused" or "Finalizing" or "AwaitingApproval" or "0" or "1" or "2" or "3" or "7"))
                {
                    continue;
                }

                foreach (var property in new[] { "SourcePath", "TargetPath" })
                {
                    if (job.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String && Path.GetDirectoryName(p.GetString()) is { } directory)
                    {
                        result.Add(directory);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Naming Standardizer: Warteschlange des Codec Converters nicht lesbar");
        }

        return result;
    }

    // Kennung: aendert sich, sobald sich im Ordner etwas aendert, was die Planung beeinflusst.
    private static string Hash(FolderPlan plan, IEnumerable<string> videos)
    {
        var text = new StringBuilder(plan.Kind).Append('|').Append(plan.Folder).Append('|').Append(plan.Blocked);
        foreach (var entry in plan.Videos)
        {
            text.Append('|').Append(entry.From).Append('>').Append(entry.To);
        }

        foreach (var video in videos)
        {
            var info = new FileInfo(video);
            text.Append('|').Append(info.Name).Append(':').Append(info.Exists ? info.Length : -1);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..20].ToLowerInvariant();
    }
}
