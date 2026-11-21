using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NamingStandardizer.Services;

/// <summary>
/// Fuehrt die ausgewaehlten Plaene aus. Jeder Ordner wird erst ins Journal geschrieben und dann umbenannt;
/// nach einem Absturz wird er beim naechsten Start zu Ende gefuehrt. Den Gesehen-Status uebertraegt der
/// Dienst, sobald Jellyfin die Dateien unter neuem Namen eingelesen hat.
/// </summary>
public sealed class Renamer : IHostedService, IDisposable
{
    private const int MaxBatches = 2000;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly Planner _planner;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<Renamer> _logger;
    private readonly LiveLog _log;
    private readonly string _journalFile;
    private List<Batch> _batches = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private int _applyTotal;
    private int _applyDone;
    private int _applyFailed;
    private bool _applyRunning;

    /// <summary>
    /// Initializes a new instance of the <see cref="Renamer"/> class.
    /// </summary>
    /// <param name="planner">Die Planung.</param>
    /// <param name="libraryManager">Die Bibliothek.</param>
    /// <param name="libraryMonitor">Die Dateiueberwachung.</param>
    /// <param name="sessionManager">Die Sitzungen.</param>
    /// <param name="userManager">Die Benutzer.</param>
    /// <param name="userDataManager">Die Benutzerdaten.</param>
    /// <param name="paths">Die Serverpfade.</param>
    /// <param name="logger">Der Logger.</param>
    /// <param name="log">Das Protokoll fuer die Seite.</param>
    public Renamer(
        Planner planner,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ISessionManager sessionManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IApplicationPaths paths,
        ILogger<Renamer> logger,
        LiveLog log)
    {
        _planner = planner;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _log = log;
        _journalFile = Path.Combine(paths.DataPath, "naming-standardizer", "journal.json");
    }

    /// <summary>
    /// Gets den Fortschritt des Umbenennens.
    /// </summary>
    public (bool Running, int Total, int Done, int Failed) Progress
    {
        get
        {
            lock (_lock)
            {
                return (_applyRunning, _applyTotal, _applyDone, _applyFailed);
            }
        }
    }

    /// <summary>
    /// Startet das Umbenennen der ausgewaehlten Ordner im Hintergrund.
    /// </summary>
    /// <param name="ids">Kennungen aus der aktuellen Vorschau.</param>
    /// <returns>Fehlertext oder null.</returns>
    public string? Start(IReadOnlyList<string> ids)
    {
        if (_planner.State.Running)
        {
            return "Die Prüfung läuft noch. Bitte warten, bis sie fertig ist.";
        }

        if (!_applyLock.Wait(0))
        {
            return "Es wird gerade schon umbenannt.";
        }

        var plans = ids.Distinct(StringComparer.Ordinal).Select(_planner.Find).Where(p => p is not null).Cast<FolderPlan>().ToList();
        if (plans.Count == 0)
        {
            _applyLock.Release();
            return "Keiner der ausgewählten Einträge ist (noch) in der Vorschau. Bitte neu prüfen.";
        }

        lock (_lock)
        {
            _applyRunning = true;
            _applyTotal = plans.Count;
            _applyDone = 0;
            _applyFailed = 0;
        }

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var plan in plans)
                {
                    var ok = ApplyOne(plan);
                    lock (_lock)
                    {
                        _applyDone++;
                        _applyFailed += ok ? 0 : 1;
                    }
                }

                _log.Add("Umbenennen fertig: " + (plans.Count - _applyFailed) + " von " + plans.Count + " Ordnern. Jellyfin liest die Änderungen in etwa einer Minute ein.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Naming Standardizer: Umbenennen abgebrochen");
                _log.Add("Umbenennen abgebrochen: " + ex.Message);
            }
            finally
            {
                lock (_lock)
                {
                    _applyRunning = false;
                }

                _applyLock.Release();
            }
        });
        return null;
    }

    private bool ApplyOne(FolderPlan preview)
    {
        // Frisch planen: nur wenn das Ergebnis genau der Vorschau entspricht, wird umbenannt.
        FolderPlan? plan;
        try
        {
            plan = _planner.Replan(preview.Kind, preview.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add("ÜBERSPRUNGEN " + preview.Display + ": Ordner nicht lesbar (" + ex.Message + ").");
            return false;
        }

        if (plan is null || !string.Equals(plan.Id, preview.Id, StringComparison.Ordinal))
        {
            _log.Add("ÜBERSPRUNGEN " + preview.Display + ": hat sich seit der Prüfung geändert. Bitte neu prüfen.");
            return false;
        }

        if (plan.Blocked is not null)
        {
            _log.Add("ÜBERSPRUNGEN " + preview.Display + ": " + plan.Blocked);
            return false;
        }

        if (!_planner.InLibrary(plan.Folder))
        {
            _log.Add("ÜBERSPRUNGEN " + preview.Display + ": liegt nicht in einer Bibliothek.");
            return false;
        }

        if (IsBeingPlayed(plan.Folder))
        {
            _log.Add("ÜBERSPRUNGEN " + preview.Display + ": wird gerade abgespielt. Bitte nach der Wiedergabe erneut umbenennen.");
            return false;
        }

        var batch = new Batch
        {
            Id = plan.Id,
            Folder = plan.Folder,
            Created = DateTime.UtcNow,
            Moves = plan.Videos.Concat(plan.Companions)
                .Select(e => new Move { From = Path.Combine(plan.Folder, e.From), To = Path.Combine(plan.Folder, e.To) })
                .ToList()
        };

        // Tauschen zwei Dateien ihre Namen, geht jede erst ueber einen Zwischennamen.
        var sources = new HashSet<string>(batch.Moves.Select(m => m.From), StringComparer.Ordinal);
        if (batch.Moves.Any(m => sources.Contains(m.To)))
        {
            foreach (var move in batch.Moves)
            {
                move.Temp = Path.Combine(plan.Folder, ".nsrename-" + batch.Id + "-" + batch.Moves.IndexOf(move).ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var video in plan.Videos)
        {
            var oldPath = Path.Combine(plan.Folder, video.From);
            batch.Transfers.Add(new StateTransfer
            {
                OldPath = oldPath,
                NewPath = Path.Combine(plan.Folder, video.To),
                States = CaptureUserStates(oldPath)
            });
        }

        lock (_lock)
        {
            _batches.Add(batch);
            Save();
        }

        var errors = Execute(batch);
        _planner.Remove(plan.Id);
        if (errors.Count > 0)
        {
            _log.Add("FEHLER " + plan.Display + ": nicht umbenannt: " + string.Join("; ", errors));
            return false;
        }

        _log.Add("UMBENANNT " + plan.Display + ": " + string.Join("; ", plan.Videos.Select(v => v.From + " → " + v.To))
            + (plan.Companions.Count > 0 ? " (+ " + plan.Companions.Count + " Begleitdatei(en))" : string.Empty));
        return true;
    }

    /// <summary>
    /// Fuehrt die Verschiebungen aus. Jeder Schritt prueft, ob er schon erledigt ist; daher darf das nach einem
    /// Absturz beliebig oft wiederholt werden.
    /// </summary>
    private List<string> Execute(Batch batch)
    {
        var errors = new List<string>();

        // Ein Fehler bei einer Datei stoppt die anderen nicht; er wird gemeldet, und der Ordner gilt als
        // abgeschlossen, sonst wuerde die Wiederaufnahme es bei jedem Start erneut versuchen.
        void TryMove(string from, string to)
        {
            try
            {
                File.Move(from, to, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(Path.GetFileName(from) + ": " + ex.Message);
            }
        }

        if (batch.Moves.Any(m => m.Temp is not null))
        {
            // Phase 1: alle auf Zwischennamen. Solange Phase 2 nicht begonnen hat, liegen unter den alten Namen
            // noch die urspruenglichen Dateien; deshalb wird die Phase gespeichert, bevor Phase 2 beginnt.
            if (batch.Phase == 1)
            {
                foreach (var move in batch.Moves)
                {
                    if (File.Exists(move.From) && !File.Exists(move.Temp))
                    {
                        TryMove(move.From, move.Temp!);
                    }
                }

                lock (_lock)
                {
                    batch.Phase = 2;
                    Save();
                }
            }

            foreach (var move in batch.Moves)
            {
                if (File.Exists(move.Temp) && !File.Exists(move.To))
                {
                    TryMove(move.Temp!, move.To);
                }
            }
        }
        else
        {
            foreach (var move in batch.Moves)
            {
                if (File.Exists(move.From) && !File.Exists(move.To))
                {
                    TryMove(move.From, move.To);
                }
            }
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Naming Standardizer: in {Folder} nicht umbenannt: {Errors}", batch.Folder, string.Join("; ", errors));
        }

        lock (_lock)
        {
            batch.Done = true;
            Save();
        }

        try
        {
            _libraryMonitor.ReportFileSystemChanged(batch.Folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Naming Standardizer: Bibliothek konnte nicht aktualisiert werden");
        }

        return errors;
    }

    private bool IsBeingPlayed(string folder)
    {
        foreach (var session in _sessionManager.Sessions)
        {
            var path = session.FullNowPlayingItem?.Path;
            if (path is not null && string.Equals(Path.GetDirectoryName(path), folder, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Liest den Stand einer Datei und ihrer Hauptversion (bei Versionen haengt er dort) und fuehrt ihn je
    /// Benutzer zusammen.
    /// </summary>
    private List<UserState> CaptureUserStates(string path)
    {
        var result = new Dictionary<Guid, UserState>();
        try
        {
            var item = _libraryManager.FindByPath(path, false);
            var items = new List<BaseItem>();
            if (item is not null)
            {
                items.Add(item);
                if (item is Video video && video.PrimaryVersionId is Guid primaryId && _libraryManager.GetItemById(primaryId) is { } primary)
                {
                    items.Add(primary);
                }
            }

            foreach (var source in items)
            {
                foreach (var user in _userManager.GetUsers())
                {
                    var data = _userDataManager.GetUserData(user, source);
                    if (data is null || (!data.Played && !data.IsFavorite && data.PlaybackPositionTicks == 0 && data.PlayCount == 0))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(user.Id, out var state))
                    {
                        result[user.Id] = state = new UserState { UserId = user.Id };
                    }

                    state.Played |= data.Played;
                    state.IsFavorite |= data.IsFavorite;
                    state.PlaybackPositionTicks = Math.Max(state.PlaybackPositionTicks, data.PlaybackPositionTicks);
                    state.PlayCount = Math.Max(state.PlayCount, data.PlayCount);
                    if (state.LastPlayedDate is null || data.LastPlayedDate > state.LastPlayedDate)
                    {
                        state.LastPlayedDate = data.LastPlayedDate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Naming Standardizer: Gesehen-Status von {Path} nicht lesbar", path);
        }

        return result.Values.ToList();
    }

    private void RestoreUserStates()
    {
        List<Batch> open;
        lock (_lock)
        {
            open = _batches.Where(b => b.Done && b.Transfers.Any(t => !t.Restored)).ToList();
        }

        var changed = false;
        foreach (var batch in open)
        {
            foreach (var transfer in batch.Transfers.Where(t => !t.Restored))
            {
                try
                {
                    if (transfer.States.Count == 0 || batch.Created < DateTime.UtcNow.AddDays(-1))
                    {
                        transfer.Restored = true;
                        changed = true;
                        continue;
                    }

                    // Erst wenn Jellyfin die alte Datei vergessen und die neue eingelesen hat.
                    if (_libraryManager.FindByPath(transfer.OldPath, false) is not null || _libraryManager.FindByPath(transfer.NewPath, false) is not { } item)
                    {
                        continue;
                    }

                    var targets = new List<BaseItem> { item };
                    if (item is Video video && video.PrimaryVersionId is Guid primaryId && _libraryManager.GetItemById(primaryId) is { } primary)
                    {
                        targets.Add(primary);
                    }

                    foreach (var target in targets)
                    {
                        foreach (var state in transfer.States)
                        {
                            var user = _userManager.GetUserById(state.UserId);
                            if (user is null)
                            {
                                continue;
                            }

                            _userDataManager.SaveUserData(
                                user,
                                target,
                                new UpdateUserItemDataDto
                                {
                                    Played = state.Played,
                                    IsFavorite = state.IsFavorite,
                                    PlaybackPositionTicks = state.PlaybackPositionTicks,
                                    PlayCount = state.PlayCount,
                                    LastPlayedDate = state.LastPlayedDate
                                },
                                UserDataSaveReason.Import);
                        }
                    }

                    transfer.Restored = true;
                    changed = true;
                    _log.Add("Gesehen-Status übertragen: " + Path.GetFileName(transfer.NewPath));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Naming Standardizer: Gesehen-Status für {Path} nicht übertragen", transfer.NewPath);
                    transfer.Restored = true;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            lock (_lock)
            {
                Save();
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Load();

        // Nach einem Absturz begonnene Umbenennungen zu Ende fuehren.
        foreach (var batch in _batches.Where(b => !b.Done).ToList())
        {
            var errors = Execute(batch);
            _log.Add("Nach einem Neustart zu Ende geführt: Umbenennung in " + batch.Folder
                + (errors.Count > 0 ? " (nicht umbenannt: " + string.Join("; ", errors) + ")" : string.Empty));
        }

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                RestoreUserStates();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Naming Standardizer: Fehler beim Übertragen des Gesehen-Status");
            }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Erwartet beim Beenden.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop?.Dispose();
        _applyLock.Dispose();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_journalFile))
            {
                _batches = JsonSerializer.Deserialize<List<Batch>>(File.ReadAllText(_journalFile), _json) ?? new List<Batch>();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "Naming Standardizer: journal.json nicht lesbar, wird als journal.json.defekt aufbewahrt");
            try
            {
                File.Copy(_journalFile, _journalFile + ".defekt", true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(copyError, "Naming Standardizer: Sicherung von journal.json fehlgeschlagen");
            }

            _batches = new List<Batch>();
        }
    }

    // Aufrufer haelt _lock.
    private void Save()
    {
        // Erledigte, uebertragene Eintraege nach einer Woche vergessen; die Liste bleibt begrenzt.
        _batches.RemoveAll(b => b.Done && b.Transfers.All(t => t.Restored) && b.Created < DateTime.UtcNow.AddDays(-7));
        if (_batches.Count > MaxBatches)
        {
            _batches.RemoveRange(0, _batches.Count - MaxBatches);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalFile)!);
            var temp = _journalFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(_batches, _json));
            File.Move(temp, _journalFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Naming Standardizer: journal.json konnte nicht gespeichert werden");
        }
    }
}
