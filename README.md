# Naming Standardizer (Jellyfin 12)

Bringt Dateinamen von Filmen und Folgen auf Jellyfins Benennungsstandard, damit mehrere Dateien eines Films
oder einer Folge als **Versionen** erkannt werden statt als getrennte Einträge.

## Was es macht

- **Filme:** Jede Videodatei im Filmordner beginnt mit dem Ordnernamen.
  - Eine einzelne Datei heißt wie der Ordner: `8 Mile 2002/dfd-8mile-1080p.mkv` → `8 Mile 2002.mkv`
  - Mehrere Dateien bekommen `Ordnername-Bezeichnung`, ohne Leerzeichen um den Trenner:
    `Der Pate 1972 [imdbid-tt0068646]-H264.mkv`, `…-HEVC.mkv`
  - Die Bezeichnung besteht aus Codec und ggf. Fassung (`Extended-H264`); unterscheiden sich gleiche
    Bezeichnungen nur in der Auflösung, kommt sie dazu (`H264-1080p`), sonst ein Zähler (`H264-2`).
- **Serien:** Jede Folge trägt Staffel und Folge eindeutig im Namen, sonst fasst Jellyfin keine Versionen zusammen.
  - `Season 01/06 - Titel.mkv` → `Serie S01E06.mkv`
  - Mehrere Dateien derselben Folge: `Serie S01E06-H264.mkv`, `Serie S01E06-HEVC.mkv`
  - Folgen mit `S01E06` oder `1x06` im Namen bleiben unverändert.
- **Was schon passt, bleibt unverändert.** Ordner werden nie umbenannt.
- **Begleitdateien wandern mit:** Untertitel, NFO, Bilder (`-poster.jpg` …) und Extras (`-trailer.mkv` …),
  die zum Namen der Videodatei gehören.
- **Gesehen-Status, Fortschritt und Favorit** werden auf die umbenannten Dateien übertragen.

## Ablauf

1. Plugin-Seite öffnen (Dashboard → „Naming Standardizer“) und **Bibliothek prüfen**. Dabei wird nichts verändert.
2. Die **Vorschau** zeigt je Ordner alt → neu. Vorausgewählt ist nur, was eindeutig ist; Einträge mit Hinweis
   (z. B. „vielleicht verschiedene Filme“) bitte erst prüfen.
3. **Ausgewählte umbenennen.** Jellyfin liest die Änderungen in etwa einer Minute ein.

## Schutzmechanismen

- Umbenannt wird nur, was der Server selbst geplant hat; direkt vor dem Umbenennen wird jeder Ordner neu geprüft.
  Hat er sich seit der Vorschau geändert, wird er übersprungen.
- Vor dem Umbenennen prüft das Plugin mit einem Nachbau von Jellyfins Regeln (Jellyfin 12.4), dass Jellyfin die
  Dateien danach wirklich zusammenfasst.
- Nicht angefasst werden: Filme ohne eigenen Ordner, Ordner mit weiteren Filmordnern darin (Sammlungen),
  Ordner mit offenem Job im Codec Converter und Ordner, in denen gerade etwas abgespielt wird.
- Jeder Ordner wird vor dem ersten Schritt in ein Journal geschrieben und nach einem Absturz beim nächsten Start
  zu Ende geführt. Vorhandene Dateien werden nie überschrieben.
- Nur Administratoren können die Seite und die API nutzen.

## Installation

**Über den Katalog:** Dashboard → Plugins → Repositories → hinzufügen:

    https://raw.githubusercontent.com/SunnyTeater/psychic-robot/main/manifest.json

Danach unter „Katalog“ den Naming Standardizer installieren und Jellyfin neu starten.

**Von Hand:** `naming-standardizer_1.0.0.0.zip` aus den Releases laden, in den Plugin-Ordner entpacken und
Jellyfin neu starten:

    sudo mkdir -p /var/lib/jellyfin/plugins/NamingStandardizer_1.0.0.0
    sudo unzip naming-standardizer_1.0.0.0.zip -d /var/lib/jellyfin/plugins/NamingStandardizer_1.0.0.0
    sudo chown -R jellyfin: /var/lib/jellyfin/plugins/NamingStandardizer_1.0.0.0
    sudo systemctl restart jellyfin

Voraussetzungen: Jellyfin 12.0 oder neuer, Schreibrechte des Jellyfin-Dienstes in den Medienordnern.
Zusätzliche Software braucht es nicht.

## Gut zu wissen

- Andere Programme, die die Dateien verwalten (Radarr, Sonarr), sehen die neuen Namen erst nach einem
  erneuten Einlesen. Sie benennen Dateien ggf. nach ihrem eigenen Schema wieder um.
- Bei Filmen, die Jellyfin keinem IMDb-/TMDb-Eintrag zugeordnet hat, ist die Vorauswahl aus: Jellyfin benennt
  einen Film in einem eigenen Ordner nach dem Ordner, auch wenn der Ordnername nicht passt.
- Die Folgen-Regel ist die von Jellyfin 12.4 (strenger als 12.0). Die neuen Namen funktionieren in beiden Versionen.
- Der Codec Converter (ab 1.4) benennt nach denselben Regeln.

## Bauen

    ./build.sh    # braucht das .NET 10 SDK, legt dist/NamingStandardizer_1.0.0.0/ an
