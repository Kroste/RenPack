# RenPack

[![CI](https://github.com/Kroste/RenPack/actions/workflows/ci.yml/badge.svg)](https://github.com/Kroste/RenPack/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Kroste/RenPack)](https://github.com/Kroste/RenPack/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Ren'Py-Archive (.rpa) entpacken und packen — Desktop-App für Windows und Linux (C# / .NET 10 / Avalonia 12).

<!-- Screenshot: docs/screenshot.png einfügen, sobald die UI steht -->

## Features

- **Archiv öffnen & durchsuchen:** `.rpa`-Datei öffnen (Button oder per Drag & Drop), Inhalt als sortierbare Dateiliste mit Größenangabe, Filter nach Dateiname.
- **Entpacken:** alle Dateien oder gezielt eine Auswahl (Häkchen) in einen Zielordner extrahieren — die Ordnerstruktur des Archivs bleibt erhalten.
- **Packen:** aus einem Ordner ein neues Archiv im Format **RPA-3.0** erstellen (das Standardformat, das Ren'Py selbst liest).
- **Formate:** liest **RPA-2.0**, **RPA-3.0** und **RPA-3.2** (inkl. XOR-Verschleierung); schreibt RPA-3.0/RPA-2.0/RPA-3.2.
- **Fortschritt & Log:** Fortschrittsanzeige beim Ent-/Packen, umfassendes Log mit automatischer Maskierung von Passwörtern/Tokens.
- 🔄 **Update-Check:** Prüft GitHub-Releases (proxy-fähig) und meldet neue Versionen.

## Installation

Fertige Pakete gibt es auf der [Releases-Seite](https://github.com/Kroste/RenPack/releases):

**Windows:** `RenPack-X.Y.Z-win-x64.zip` herunterladen, entpacken,
`RenPack.exe` starten. Keine Installation nötig (self-contained, .NET ist enthalten).

**Linux (AppImage, empfohlen):** `RenPack-X.Y.Z-x86_64.AppImage` herunterladen,
ausführbar machen und starten:

```bash
chmod +x RenPack-*-x86_64.AppImage
./RenPack-*-x86_64.AppImage
```

**Linux (tar.gz):** `RenPack-X.Y.Z-linux-x64.tar.gz` entpacken und
`./RenPack` starten.

## Bedienung

**Archiv öffnen:** Auf „📂 Archiv öffnen" klicken und eine `.rpa`-Datei wählen — oder die
Datei einfach ins Fenster ziehen. Die enthaltenen Dateien erscheinen in der Liste; oben
lässt sich nach Dateiname filtern.

**Alles entpacken:** „⬇ Alles entpacken" klicken und einen Zielordner wählen. Alle Dateien
werden dorthin extrahiert, Unterordner inklusive.

**Nur bestimmte Dateien entpacken:** Die gewünschten Dateien in der Liste anhaken (die
Buttons „Alle" / „Keine" helfen bei der Auswahl), dann „⬇ Auswahl entpacken" klicken.

**Ordner zu Archiv packen:** „📦 Ordner packen …" klicken, den Quellordner wählen und
danach den Zielpfad für die neue `.rpa`. RenPack erzeugt ein RPA-3.0-Archiv, das Ren'Py
direkt laden kann. Auf Wunsch wird das frisch erstellte Archiv gleich geöffnet.

## Einstellungen

RenPack ist bewusst einstellungsarm. Beim Packen wird das Format RPA-3.0 mit dem
üblichen Standard-Key verwendet — das ist mit Ren'Py voll kompatibel. Es gibt keine
Konfigurationsdatei; alle Aktionen laufen über die Dialoge.

## Logs & Fehlersuche

Logdateien liegen im Unterordner `logs/` neben der Anwendung (Tagesarchiv,
14 Tage). Bei einem Problem bitte ein Issue mit der aktuellen Logdatei eröffnen —
Passwörter und Tokens werden automatisch maskiert.

## Entwicklung

```bash
dotnet build   # bauen
dotnet test    # Tests (inkl. echter Ren'Py-Kompatibilitätsprüfung gegen Python)
dotnet run --project RenPack
```

Release: VS-Code-Task „release (tag + push)" — prüft den Git-Zustand, setzt den
Tag und stößt die GitHub-Action an, die alle Pakete baut.

## Lizenz

MIT — siehe [LICENSE](LICENSE).

---

☕ Gefällt dir das Tool? [Buy me a coffee](https://buymeacoffee.com/kroste)
