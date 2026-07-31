# RenPack

## Grundlagen

- **Was:** Desktop-App zum Entpacken und Packen von Ren'Py-Archiven (`.rpa`) — Alternative zu den umständlichen Windows-Tools, mit grafischer Oberfläche.
- **Stack:** C# / .NET 10 / Avalonia 12, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, NLog (mit Secret-Masking), Razorvine.Pickle (Python-Pickle), xunit.v3 + FluentAssertions 7.x.
- **Struktur:** Flach (kein `src/`), `.slnx`, Central Package Management, `Directory.Build.props`, MinVer (Tags `v*`).
- **Cross-Platform:** Windows (win-x64) und Linux (linux-x64 + AppImage). Kein plattformgebundenes Feature — bleibt bewusst cross-platform.
- **Konventionen:** ChromeWindow/TitleBar (Kroste-Look), GlobalExceptionHandler, AboutWindow mit Version + Update-Prüfung + BMC, `TreatWarningsAsErrors`.
- **Kommunikation:** Deutsch, "du". Lars entwirft, Claude implementiert.

## Aktueller Stand

- **v0.1.0** — Archive: Öffnen (Button + Drag&Drop), Inhalt auflisten/filtern, alles oder Auswahl entpacken, Ordner zu RPA-3.0 packen. Formate lesen RPA-2.0/3.0/3.2, schreiben RPA-2.0/3.0/3.2. Release-Build + linux-x64-Publish (self-contained, single-file) verifiziert.
- **v0.2 (in Arbeit)** — Save-Inspector: read-only Öffnen von `.save`-Dateien mit Screenshot, JSON-Metadaten und Store-Variablen als Tabelle mit Filter. Eigenes `SaveWindow` (ChromeWindow), aufrufbar aus der Toolbar oder per Drag&Drop ins Save-Fenster. Editieren kommt in v0.3.
- **Tests grün (14):** die 10 Archiv-Tests aus v0.1 plus 4 Save-Tests (Metadaten/Screenshot/Store aus einem Python-erzeugten Save, RevertableDict/-List via Passthrough, kaputter Log wirft nicht sondern zeigt Metadaten trotzdem, fehlendes `json` fällt sauber auf leere Metadaten). Interop-Tests überspringen sauber ohne `python3`.

## Roadmap

- **Save-Editor v0.3:** Einfache Werte (int/float/str/bool) editieren, Save byte-preserving zurückschreiben (unbekannte Klassen als opake `ClassDict` erhalten, `RollbackLog`-Skeleton unverändert lassen). Optional `_save_name` im `json` anpassen.
- Kontextmenü in der Dateiliste (einzelne Datei per Rechtsklick extrahieren/Vorschau).
- Vorschau für Text-/Bilddateien direkt im Fenster.
- Beim Packen wählbares Format/Key in einem kleinen Dialog (aktuell fix RPA-3.0 + Standard-Key).
- Headless-UI-Smoke-Test (Avalonia.Headless) — beim ersten Versuch verklemmt; braucht `[AvaloniaTestApplication]`-Setup, später sauber nachziehen.
- App-Icon durch ein richtiges Design ersetzen (aktuell schlichtes Platzhalter-Icon in `Assets/RenPack.png`/`.ico`).

## Referenz

- **RPA-Format (`Services/RenpyArchiveService.cs`):** Header-Zeile (ASCII, endet mit `\n`), dann rohe Dateidaten, am Ende ein zlib-komprimiertes Python-Pickle des Index. Header-Varianten: `RPA-3.0 <off16> <key8>`, `RPA-3.2 <off16> 0 <key8>`, `RPA-2.0 <off16>`. Bei 3.0/3.2 sind Offset und Länge im Index mit dem 32-bit-Key XOR-verschleiert. Index = `dict{ name: [[offset, length, (prefix)], …] }`. Beim Lesen werden 2- und 3-Tupel (mit Prefix) unterstützt; beim Schreiben immer 2-Tupel ohne Prefix (RPA-3.0, Standard-Key `0xDEADBEEF`).
- **Pickle:** Lesen über Razorvine.Pickle (deckt Protokoll 0–5 ab, robust für Fremd-Archive), Schreiben ebenfalls über Razorvine (Struktur bewusst als `dict[str, list[[long,long]]]`, damit Ren'Py sie liest). zlib über `System.IO.Compression.ZLibStream` (kein Extra-Paket).
- **Sicherheit:** `SafeCombine` verhindert Path-Traversal (`../`, absolute Pfade) beim Entpacken.
- **MVVM-Brücke:** `IUiInteractions` (Archiv) und `ISaveUi` (Save) entkoppeln die ViewModels von den plattformabhängigen Datei-Dialogen; `MainWindow` bzw. `SaveWindow` implementieren die Interfaces via `StorageProvider` + `MessageBox`. Die Geschäftslogik liegt in DI-Singletons (`RenpyArchiveService`, `RenpySaveService`) und bleibt UI-frei und testbar.
- **Save-Format (`Services/RenpySaveService.cs`):** ZIP mit Einträgen `log` (zlib-komprimiertes Pickle-Tupel `(roots, log)`), `json` (Kurz-Metadaten), `screenshot.png`, optional `signatures`. `roots["store"]` ist der Store-Dict mit allen Spielvariablen. Unbekannte Ren'Py-Klassen werden über einen `PassthroughConstructor` als `ClassDict` gecatcht — beim ersten Unpickle-Fehler wird die Klasse aus der Fehlermeldung extrahiert (Regex auf "for construction of ClassDict (for mod.Class)") und dynamisch registriert, dann erneut versucht (max. 50 Iterationen). So bleibt der Reader gegenüber neuen Ren'Py-Versionen tolerant.
- **Avalonia-12-Fallen beachtet:** Chrome-Rollen (`ElementRole=User` auch am ⓘ-Button), `PlaceholderText` statt `Watermark`, Drag&Drop über die neue `DataTransfer`-API (`e.DataTransfer.Formats.Contains(DataFormat.File)` + `TryGetFiles()`), DataGrid als separates Paket `Avalonia.Controls.DataGrid` inkl. Theme-Include.
- **Stolperfalle (dokumentiert):** `progress?.Report(new RpaProgress(++done, …))` — der Null-Conditional-Operator überspringt bei `progress == null` **auch** die Argument-Auswertung, also `++done`. Zähler immer VOR dem `?.`-Aufruf erhöhen.
