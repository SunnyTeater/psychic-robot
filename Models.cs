using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NamingStandardizer.Services;

/// <summary>
/// Eine geplante Umbenennung innerhalb eines Ordners (nur Dateinamen, kein Pfad).
/// </summary>
public sealed class RenameEntry
{
    /// <summary>Gets or sets den bisherigen Dateinamen.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets den neuen Dateinamen.</summary>
    public string To { get; set; } = string.Empty;
}

/// <summary>
/// Alles, was in einem Ordner umbenannt werden soll. Ein Ordner wird immer ganz oder gar nicht umbenannt,
/// weil Jellyfin Versionen nur zusammenfasst, wenn alle Dateien passen.
/// </summary>
public sealed class FolderPlan
{
    /// <summary>Gets or sets die Kennung: Pruefsumme ueber Ordner, Aenderungen und Dateistand.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets die Art: "Film" oder "Serie".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets den vollstaendigen Ordnerpfad (nur serverseitig).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder { get; set; } = string.Empty;

    /// <summary>Gets or sets den Ordner relativ zur Bibliothek, fuer die Anzeige.</summary>
    public string Display { get; set; } = string.Empty;

    /// <summary>Gets or sets die Videodateien, die umbenannt werden.</summary>
    public List<RenameEntry> Videos { get; set; } = new();

    /// <summary>Gets or sets Begleitdateien (Untertitel, NFO, Bilder, Extras), die mitwandern.</summary>
    public List<RenameEntry> Companions { get; set; } = new();

    /// <summary>Gets or sets Videodateien, die schon passen und bleiben.</summary>
    public List<string> Kept { get; set; } = new();

    /// <summary>Gets or sets einen Hinweis, der vor dem Umbenennen geprueft werden sollte.</summary>
    public string? Warning { get; set; }

    /// <summary>Gets or sets den Grund, warum der Ordner nicht umbenannt werden kann.</summary>
    public string? Blocked { get; set; }

    /// <summary>Gets or sets a value indicating whether der Ordner in der Vorschau vorausgewaehlt ist.</summary>
    public bool Recommended { get; set; }
}

/// <summary>
/// Stand einer Pruefung der Bibliothek.
/// </summary>
public sealed class ScanState
{
    /// <summary>Gets or sets a value indicating whether gerade geprueft wird.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets die Zahl der gepruefte Ordner.</summary>
    public int Checked { get; set; }

    /// <summary>Gets or sets die Zahl aller Ordner.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets das Ende der letzten Pruefung.</summary>
    public DateTime? Finished { get; set; }

    /// <summary>Gets or sets die Zahl der Ordner, die schon dem Standard entsprechen.</summary>
    public int Standard { get; set; }

    /// <summary>Gets or sets die Zahl der Filme ohne eigenen Ordner (nicht bearbeitbar).</summary>
    public int Flat { get; set; }

    /// <summary>Gets or sets einen Fehlertext der letzten Pruefung.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Gesehen-Status, Fortschritt und Favorit eines Benutzers.
/// </summary>
public sealed class UserState
{
    /// <summary>Gets or sets den Benutzer.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets a value indicating whether gesehen.</summary>
    public bool Played { get; set; }

    /// <summary>Gets or sets a value indicating whether Favorit.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Gets or sets die Wiedergabeposition.</summary>
    public long PlaybackPositionTicks { get; set; }

    /// <summary>Gets or sets die Zahl der Wiedergaben.</summary>
    public int PlayCount { get; set; }

    /// <summary>Gets or sets den Zeitpunkt der letzten Wiedergabe.</summary>
    public DateTime? LastPlayedDate { get; set; }
}

/// <summary>
/// Eine einzelne Verschiebung im Journal.
/// </summary>
public sealed class Move
{
    /// <summary>Gets or sets die Quelle.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets das Ziel.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Gets or sets einen Zwischennamen, falls Namen getauscht werden.</summary>
    public string? Temp { get; set; }
}

/// <summary>
/// Gesehen-Status einer Videodatei, der nach dem Umbenennen uebertragen wird.
/// </summary>
public sealed class StateTransfer
{
    /// <summary>Gets or sets den alten Pfad.</summary>
    public string OldPath { get; set; } = string.Empty;

    /// <summary>Gets or sets den neuen Pfad.</summary>
    public string NewPath { get; set; } = string.Empty;

    /// <summary>Gets or sets den gemerkten Stand.</summary>
    public List<UserState> States { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether der Stand uebertragen ist.</summary>
    public bool Restored { get; set; }
}

/// <summary>
/// Die Umbenennung eines Ordners. Wird vor dem ersten Schritt gespeichert, damit sie nach einem Absturz
/// zu Ende gefuehrt werden kann.
/// </summary>
public sealed class Batch
{
    /// <summary>Gets or sets die Kennung.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets den Ordner.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Gets or sets die Verschiebungen.</summary>
    public List<Move> Moves { get; set; } = new();

    /// <summary>Gets or sets den zu uebertragenden Gesehen-Status.</summary>
    public List<StateTransfer> Transfers { get; set; } = new();

    /// <summary>Gets or sets die Phase beim Tauschen ueber Zwischennamen: 1 = zu Zwischennamen, 2 = zu Endnamen.</summary>
    public int Phase { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether alle Verschiebungen erledigt sind.</summary>
    public bool Done { get; set; }

    /// <summary>Gets or sets den Zeitpunkt des Anlegens.</summary>
    public DateTime Created { get; set; }
}
