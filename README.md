# AutoTranslator

AutoTranslator übersetzt chinesischen Text auf dem Bildschirm **automatisch und ohne Klick** ins Englische –
auf Webseiten in Chrome und in normalen Windows-Programmen. Die englische Übersetzung wird direkt über dem
chinesischen Original eingeblendet.

Es gibt zwei Übersetzer zur Auswahl:

- **Chrome (Standard):** kostenlos, findet auf dem eigenen Computer statt. Genutzt wird die in Google Chrome
  eingebaute Übersetzungsfunktion.
- **Claude Haiku (optional):** meist deutlich bessere Übersetzungen, besonders bei Shop-Apps und
  Umgangssprache. Kostet wenig Geld, braucht einen eigenen API-Schlüssel von Anthropic, und der Text wird über
  das Internet an Anthropic gesendet. Siehe „Optional: Claude Haiku als Übersetzer“.

> **Hinweis:** Das ist eine erste Version. Sie funktioniert in vielen, aber nicht in allen Situationen
> (siehe „Bekannte Grenzen“).

## Aus welchen Teilen besteht es?

| Teil | Aufgabe |
|---|---|
| **Windows-Programm** (`AutoTranslator.exe`) | Läuft als Symbol neben der Uhr, erkennt chinesischen Text auf dem Bildschirm und blendet die Übersetzung ein. |
| **Chrome-Erweiterung** | Übersetzt Webseiten direkt in Chrome und liefert dem Windows-Programm die Übersetzungen. |

Beide Teile werden gebraucht – auch wenn Claude als Übersetzer gewählt ist (Chrome ist dann der Ersatz, falls
Claude ausfällt, und Webseiten werden weiterhin von der Erweiterung umgeschrieben). Chrome sollte geöffnet
sein, solange übersetzt werden soll.

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

## Optional: Claude Haiku als Übersetzer

**Was ist das?** Claude Haiku ist ein KI-Modell der Firma Anthropic. Es versteht den Zusammenhang eines ganzen
Bildschirms und übersetzt deshalb meist natürlicher als der eingebaute Chrome-Übersetzer. Es läuft nicht auf
dem eigenen PC, sondern bei Anthropic im Internet.

**Was kostet es?** Abgerechnet wird nach Textmenge über das eigene Anthropic-Konto (Guthaben vorab aufladen,
kein Abo). Als grobe Schätzung: **etwa 0,03 Cent pro Bildschirm** voller Text. Bereits übersetzte Texte merkt
sich das Programm, sie kosten kein zweites Mal. Zusätzlich gibt es ein **Tageslimit** als Kostenbremse:
Voreingestellt sind 300.000 chinesische Zeichen pro Tag, das sind nur wenige Cent. Ist das Limit erreicht,
übersetzt bis zum nächsten Tag automatisch wieder Chrome (kostenlos). Die genauen Preise legt Anthropic fest
und können sich ändern.

**So wird es eingerichtet:**

1. Auf <https://console.anthropic.com> ein Konto anlegen und unter **Billing** etwas Guthaben aufladen.
2. Dort unter **API Keys** einen neuen Schlüssel erstellen und kopieren (er beginnt mit `sk-ant-`).
3. Rechtsklick auf das AutoTranslator-Symbol neben der Uhr → **Einstellungen…**
4. Den Schlüssel in das Feld einfügen und auf **Speichern** klicken.
5. Auf **Verbindung testen** klicken. Erscheint „Verbindung zu Claude funktioniert“, ist alles in Ordnung.
6. Oben **Claude Haiku** auswählen. Die Änderung gilt sofort.

Der Schlüssel wird verschlüsselt nur auf diesem PC und nur für das eigene Windows-Benutzerkonto gespeichert
(Datei `claude.key`). Er wird nie angezeigt und nicht in der Chrome-Erweiterung abgelegt. Mit **Entfernen**
wird er wieder gelöscht.

**Webseiten in Chrome:** Solange das Windows-Programm läuft und dort Claude eingeschaltet ist, übersetzt die
Chrome-Erweiterung chinesische Webseiten automatisch ebenfalls mit Claude – der Schlüssel muss dafür nicht
noch einmal eingegeben werden. Läuft das Windows-Programm nicht, ist Chrome als Übersetzer gewählt, fehlt der
Schlüssel oder ist das Tageslimit erreicht, nimmt die Erweiterung von selbst wieder den kostenlosen
Chrome-Übersetzer. Webseiten und Bildschirmtexte teilen sich dasselbe Tageslimit.

Aus Sicherheitsgründen darf nur die Chrome-Erweiterung Claude benutzen, die sich als erste mit dem Programm
verbunden hat. Wurde die Erweiterung neu installiert und Webseiten werden nicht mehr mit Claude übersetzt:
im Menü **Erweiterung neu koppeln** wählen.

**Datenschutz – bitte lesen:** Mit Claude wird der **gesamte chinesische Text, der im aktiven Fenster (bzw. auf
dem Hauptbildschirm) sichtbar ist**, an Anthropic gesendet – also auch Namen, Adressen, Chat-Nachrichten oder
Bestelldaten, wenn sie gerade zu sehen sind. Dasselbe gilt für chinesische Webseiten in Chrome. Wer das nicht
möchte, bleibt beim Übersetzer „Chrome“; dann verlässt nichts den eigenen PC.

## Bedienung

- **Strg+Alt+T** schaltet die Übersetzung ein oder aus. Benutzt ein anderes Programm dieses Tastenkürzel
  schon, weist AutoTranslator darauf hin; dann geht das Ein- und Ausschalten nur über das Menü.
- **Rechtsklick auf das Symbol** neben der Uhr öffnet das Menü:
  - **Übersetzung aktiv** – ein/aus
  - **Übersetzer** – zeigt, wer gerade übersetzt, und schaltet zwischen *Chrome* und *Claude Haiku* um
  - **Texterkennung** – *OCR* liest alles, was auf dem Bildschirm zu sehen ist. *UI-Automation* liest Texte
    direkt aus Programmen (genauer, funktioniert aber nicht in jedem Programm). *Beides* kombiniert die zwei Wege.
  - **Bereich** – nur das aktive Fenster (schneller) oder der ganze Hauptbildschirm
  - **Einstellungen…** – Übersetzer, API-Schlüssel, Tageslimit, heutiger Verbrauch und die Option
    „Zeilen zu Absätzen zusammenfassen“ (mehrzeilige Texte werden als ein Absatz übersetzt)
  - **Erweiterung neu koppeln** – nur nötig, wenn die Chrome-Erweiterung neu installiert wurde
  - **Einrichtung / Hilfe…** – die Anleitung mit aktuellem Zustand
  - **Beenden**
- Die Statuszeile im Menü zeigt, ob gerade etwas fehlt (z. B. „Warte auf Chrome-Erweiterung“). Steht beim
  Übersetzer „Chrome (Claude nicht bereit)“, fehlt der Schlüssel, das Tageslimit ist erreicht oder Claude ist
  nicht erreichbar – Näheres steht unter „Einrichtung / Hilfe…“.

## Bekannte Grenzen

- **Noch nicht auf einem echten Windows-PC erprobt:** Das Programm wurde bisher nur automatisch gebaut und
  in Teilen automatisch getestet. Das gilt besonders für die neue Claude-Anbindung und das Einstellungsfenster.
  Fehler bitte mit der Datei `log.txt` (siehe unten) melden.
- **Chrome sollte laufen.** Mit dem Übersetzer „Chrome“ gibt es ohne geöffnetes Chrome mit der Erweiterung
  keine Übersetzung. Mit Claude funktioniert die Bildschirmübersetzung voraussichtlich auch ohne Chrome, es
  fehlt dann aber der Ersatz, wenn Claude ausfällt.
- **Claude braucht Internet** und antwortet bei neuem Text voraussichtlich etwas langsamer als Chrome
  (noch nicht gemessen). Die Kostenangaben oben sind Schätzungen.
- **Verzögerung:** Die Übersetzung erscheint etwa 0,5 bis 1 Sekunde nach dem Text, beim Scrollen hinkt sie kurz hinterher.
- **Erkennungsfehler:** Die Texterkennung (OCR) liest nicht perfekt, besonders bei sehr kleiner Schrift,
  Zierschrift oder unruhigem Hintergrund. Das Zusammenfassen von Zeilen zu Absätzen klappt nicht bei jedem
  Layout; notfalls lässt es sich in den Einstellungen ausschalten.
- **Nicht lesbar:** Spiele im exklusiven Vollbildmodus und kopiergeschützte Inhalte (z. B. manche Videos).
- Es wird nur Chinesisch → Englisch übersetzt.
- Die Einstellungen, der Übersetzungs-Zwischenspeicher, der Tagesverbrauch, der verschlüsselte API-Schlüssel
  und eine Fehlerprotokoll-Datei (`log.txt`) liegen unter `%AppData%\AutoTranslator`. Der Schlüssel wird nie
  ins Protokoll geschrieben.

## Für Entwickler

Aufbau und Schnittstellen sind in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) beschrieben (englisch).
Das Windows-Programm wird von GitHub Actions gebaut (`.github/workflows/build.yml`).
