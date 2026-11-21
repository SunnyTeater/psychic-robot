using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.NamingStandardizer.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NamingStandardizer.Api;

/// <summary>
/// API fuer die Plugin-Seite. Nur fuer Administratoren. Pfade nimmt die API nie entgegen: umbenannt wird nur,
/// was der Server selbst geplant hat, ausgewaehlt ueber die Kennung aus der Vorschau.
/// </summary>
[ApiController]
[Route("NamingStandardizer")]
[Authorize(Policy = Policies.RequiresElevation)]
public partial class NamingController : ControllerBase
{
    private const int MaxIds = 20000;

    private readonly Planner _planner;
    private readonly Renamer _renamer;
    private readonly LiveLog _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamingController"/> class.
    /// </summary>
    /// <param name="planner">Die Planung.</param>
    /// <param name="renamer">Das Umbenennen.</param>
    /// <param name="log">Das Protokoll.</param>
    public NamingController(Planner planner, Renamer renamer, LiveLog log)
    {
        _planner = planner;
        _renamer = renamer;
        _log = log;
    }

    [GeneratedRegex("^[0-9a-f]{20}$")]
    private static partial Regex IdRegex();

    /// <summary>
    /// Stand von Pruefung und Umbenennen.
    /// </summary>
    /// <returns>Der Stand.</returns>
    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var (running, total, done, failed) = _renamer.Progress;
        var plans = _planner.Plans;
        return Ok(new
        {
            Scan = _planner.State,
            Apply = new { Running = running, Total = total, Done = done, Failed = failed },
            Open = plans.Count(p => p.Blocked is null),
            Blocked = plans.Count(p => p.Blocked is not null)
        });
    }

    /// <summary>
    /// Startet die Pruefung der Bibliothek.
    /// </summary>
    /// <returns>Ergebnis.</returns>
    [HttpPost("Scan")]
    public ActionResult StartScan()
    {
        if (_renamer.Progress.Running)
        {
            return Conflict(new { Message = "Es wird gerade umbenannt. Bitte warten." });
        }

        return _planner.StartScan() ? NoContent() : Conflict(new { Message = "Die Prüfung läuft bereits." });
    }

    /// <summary>
    /// Die Ergebnisse der letzten Pruefung.
    /// </summary>
    /// <returns>Die Plaene.</returns>
    [HttpGet("Plans")]
    public ActionResult GetPlans() => Ok(_planner.Plans);

    /// <summary>
    /// Benennt die ausgewaehlten Ordner um.
    /// </summary>
    /// <param name="request">Die Kennungen aus der Vorschau.</param>
    /// <returns>Ergebnis.</returns>
    [HttpPost("Apply")]
    public ActionResult Apply([FromBody] ApplyRequest? request)
    {
        var ids = request?.Ids;
        if (ids is null || ids.Count == 0 || ids.Count > MaxIds || ids.Any(id => id is null || !IdRegex().IsMatch(id)))
        {
            return BadRequest(new { Message = "Ungültige Auswahl." });
        }

        var error = _renamer.Start(ids);
        return error is null ? NoContent() : Conflict(new { Message = error });
    }

    /// <summary>
    /// Zeilen des Protokolls nach einer Nummer.
    /// </summary>
    /// <param name="after">Die zuletzt gesehene Nummer.</param>
    /// <returns>Die Zeilen.</returns>
    [HttpGet("Log")]
    public ActionResult GetLog([FromQuery] long after = 0) => Ok(new { Lines = _log.Since(Math.Max(0, after)) });
}

/// <summary>
/// Auswahl zum Umbenennen.
/// </summary>
public sealed class ApplyRequest
{
    /// <summary>Gets or sets die Kennungen aus der Vorschau.</summary>
    public List<string>? Ids { get; set; }
}
