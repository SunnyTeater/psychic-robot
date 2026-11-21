using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.NamingStandardizer.Services;

/// <summary>
/// Eine Zeile des Live-Protokolls.
/// </summary>
/// <param name="Seq">Fortlaufende Nummer.</param>
/// <param name="Text">Der Text samt Uhrzeit.</param>
public sealed record LogLine(long Seq, string Text);

/// <summary>
/// Haelt die letzten Ereignisse im Speicher, damit die Plugin-Seite zeigen kann, was gerade passiert.
/// Das Protokoll ist bewusst klein und ueberlebt keinen Neustart; dauerhaft steht alles im Jellyfin-Log.
/// </summary>
public sealed class LiveLog
{
    private const int MaxLines = 400;
    private const int MaxLength = 600;

    private readonly object _lock = new();
    private readonly LinkedList<LogLine> _lines = new();
    private long _seq;

    /// <summary>
    /// Fuegt eine Zeile hinzu.
    /// </summary>
    /// <param name="text">Der Text.</param>
    public void Add(string text)
    {
        // Zeilenumbrueche aus Dateinamen oder ffmpeg-Ausgaben duerfen keine zusaetzlichen Zeilen vortaeuschen.
        var clean = text.Replace('\r', ' ').Replace('\n', ' ');
        if (clean.Length > MaxLength)
        {
            clean = clean[..MaxLength] + " …";
        }

        lock (_lock)
        {
            _lines.AddLast(new LogLine(++_seq, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + clean));
            while (_lines.Count > MaxLines)
            {
                _lines.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// Liefert alle Zeilen nach einer Nummer.
    /// </summary>
    /// <param name="after">Die zuletzt gesehene Nummer (0 = alles).</param>
    /// <returns>Die Zeilen.</returns>
    public List<LogLine> Since(long after)
    {
        lock (_lock)
        {
            return _lines.Where(l => l.Seq > after).ToList();
        }
    }
}
