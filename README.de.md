# AutoTranslator

*English version: [README.md](README.md)*

AutoTranslator übersetzt chinesischen Text auf dem Bildschirm **automatisch und ohne Klick** ins Englische –
auf Webseiten in Chrome und in normalen Windows-Programmen. Die englische Übersetzung wird direkt über dem
chinesischen Original eingeblendet.

Übersetzt wird mit der in Google Chrome eingebauten Übersetzungsfunktion. Das ist **kostenlos und läuft
komplett auf dem eigenen Computer**: Nach dem einmaligen Herunterladen des Sprachpakets wird kein Text ins
Internet geschickt. Auch die Texterkennung (Windows) und die Verbindung zwischen Programm und Erweiterung
bleiben auf dem PC.

> **Hinweis:** Sie funktioniert in vielen, aber nicht in allen Situationen (siehe „Bekannte Grenzen“).

## Aus welchen Teilen besteht es?

| Teil | Aufgabe |
|---|---|
| **Windows-Programm** (`AutoTranslator.exe`) | Läuft als Symbol neben der Uhr, erkennt chinesischen Text auf dem Bildschirm und blendet die Übersetzung ein. |
| **Chrome-Erweiterung** | Übersetzt Webseiten direkt in Chrome und liefert dem Windows-Programm die Übersetzungen. |

Beide Teile werden gebraucht. Chrome muss geöffnet sein, solange übersetzt werden soll.

## Voraussetzungen

- Windows 10 (Version 2004 oder neuer) oder Windows 11, 64 Bit
- Google Chrome in aktueller Version (auf dem Desktop-PC)

## 1. Herunterladen

1. Auf der GitHub-Seite dieses Projekts oben auf **Actions** klicken.
2. Den obersten (neuesten) Eintrag mit grünem Haken anklicken.
3. Ganz unten bei **Artifacts** beide Dateien herunterladen (dafür muss man bei GitHub angemeldet sein):
   - `AutoTranslator-windows` – das Windows-Programm
   - `AutoTranslator-chrome-extension` – die Chrome-Erweiterung
4. Beide ZIP-Dateien entpacken (Rechtsklick → „Alle extrahieren…“), am besten an einen festen Ort,
   z. B. `Dokumente\AutoTranslator`.

## 2. Chrome-Erweiterung laden

1. In Chrome die Adresse `chrome://extensions` öffnen.
2. Oben rechts den **Entwicklermodus** einschalten.
3. Auf **„Entpackte Erweiterung laden“** klicken und den entpackten Ordner der Erweiterung auswählen.
4. Auf das Symbol der Erweiterung klicken (eventuell zuerst auf das Puzzle-Symbol) und im kleinen Fenster
   **„Sprachpaket laden“** anklicken. Warten, bis es fertig ist. Das ist nur einmal nötig.

Der Ordner darf danach nicht gelöscht oder verschoben werden.

## 3. Chinesische Texterkennung für Windows installieren

Damit Windows chinesische Schriftzeichen auf dem Bildschirm lesen kann, wird einmalig ein Sprachpaket gebraucht.

**Weg A – über die Einstellungen:**
Einstellungen → Zeit und Sprache → Sprache und Region → Sprache hinzufügen → **Chinesisch (vereinfacht)**.
Die Anzeigesprache von Windows muss dabei nicht geändert werden.

**Weg B – per Befehl:** Rechtsklick auf den Start-Knopf → „Terminal (Administrator)“ und diesen Befehl ausführen:

```powershell
Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"
```

## 4. Starten

1. `AutoTranslator.exe` doppelklicken. Falls Windows warnt („Der Computer wurde durch Windows geschützt“):
   „Weitere Informationen“ → „Trotzdem ausführen“.
2. Beim ersten Start öffnet sich ein Fenster mit der Einrichtungshilfe. Dort steht auch, ob Chrome-Erweiterung
   und Texterkennung schon bereit sind.
3. Das Programm läuft danach als rundes Symbol im Infobereich neben der Uhr (eventuell hinter dem Pfeil „^“).

## Bedienung

- **Strg+Alt+T** schaltet die Übersetzung ein oder aus. Benutzt ein anderes Programm dieses Tastenkürzel
  schon, weist AutoTranslator darauf hin; dann geht das Ein- und Ausschalten nur über das Menü.
- **Rechtsklick auf das Symbol** neben der Uhr öffnet das Menü:
  - **Übersetzung aktiv** – ein/aus
  - **Texterkennung** – *OCR* liest alles, was auf dem Bildschirm zu sehen ist. *UI-Automation* liest Texte
    direkt aus Programmen (genauer, funktioniert aber nicht in jedem Programm). *Beides* kombiniert die zwei Wege.
  - **Bereich** – nur das aktive Fenster (schneller) oder der ganze Hauptbildschirm
  - **Zeilen zu Absätzen zusammenfassen** – mehrzeilige Texte werden als ein Absatz übersetzt (an/aus)
  - **Einrichtung / Hilfe…** – die Anleitung mit aktuellem Zustand
  - **Beenden**
- Die Statuszeile im Menü zeigt, ob gerade etwas fehlt (z. B. „Warte auf Chrome-Erweiterung“). Näheres steht
  unter „Einrichtung / Hilfe…“.

## Bekannte Grenzen

- **Chrome muss laufen.** Ohne geöffnetes Chrome mit der Erweiterung gibt es keine Übersetzung.
- **Verzögerung:** Die Übersetzung erscheint etwa 0,5 bis 1 Sekunde nach dem Text, beim Scrollen hinkt sie kurz hinterher.
- **Erkennungsfehler:** Die Texterkennung (OCR) liest nicht perfekt, besonders bei sehr kleiner Schrift,
  Zierschrift oder unruhigem Hintergrund. Das Zusammenfassen von Zeilen zu Absätzen klappt nicht bei jedem
  Layout; notfalls lässt es sich im Menü ausschalten.
- **Nicht lesbar:** Spiele im exklusiven Vollbildmodus und kopiergeschützte Inhalte (z. B. manche Videos).
- Es wird nur Chinesisch → Englisch übersetzt.
- Die Einstellungen, der Übersetzungs-Zwischenspeicher und eine Fehlerprotokoll-Datei (`log.txt`) liegen unter
  `%AppData%\AutoTranslator`.

## Für Entwickler

Ein Ablaufdiagramm steht in [docs/workflow.svg](docs/workflow.svg). Aufbau und Schnittstellen sind in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) beschrieben (englisch).
Das Windows-Programm wird von GitHub Actions gebaut (`.github/workflows/build.yml`).

## Lizenz

[PolyForm Strict 1.0.0](LICENSE). Kurz gesagt:

- **Erlaubt:** kostenlose Nutzung für **private und andere nicht kommerzielle Zwecke** (privat, Lernen, Hobby,
  gemeinnützige Vereine, Schulen, öffentliche Einrichtungen).
- **Nicht erlaubt:** kommerzielle Nutzung, das Programm **weiterzugeben oder erneut hochzuladen** (bitte stattdessen
  auf dieses Repository verlinken) sowie veränderte Versionen oder darauf aufbauende Werke zu veröffentlichen.

Der Quellcode ist öffentlich einsehbar, damit man sieht, was das Programm tut; es ist aber keine Open-Source-Lizenz.
Für kommerzielle Nutzung oder andere Erlaubnisse bitte beim Autor nachfragen.
