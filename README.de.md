# Offline Chinese Screen Translator

*Das Programm selbst heißt **AutoTranslator**.*

*English version: [README.md](README.md)*

AutoTranslator übersetzt chinesischen Text auf dem Bildschirm **automatisch und ohne Klick** ins Englische –
auf Webseiten in Chrome und in normalen Windows-Programmen. Die englische Übersetzung wird direkt über dem
chinesischen Original eingeblendet.

Übersetzt wird mit der in Google Chrome eingebauten Übersetzungsfunktion. Das ist **kostenlos und läuft
komplett auf dem eigenen Computer**: Nach dem einmaligen Herunterladen des Sprachpakets wird kein Text ins
Internet geschickt. Auch die Texterkennung (Windows) und die Verbindung zwischen Programm und Erweiterung
bleiben auf dem PC.

> **🤖 Gevibecoded.** Dieses Projekt ist komplett mit KI entstanden: Der Autor hat in normaler Sprache
> beschrieben, was er möchte, und [Claude Code](https://claude.com/claude-code) hat den Code geplant,
> geschrieben, getestet und dokumentiert. Keine Zeile wurde von Hand programmiert. Das Programm wurde
> automatisch unter Windows gebaut und getestet und läuft beim Autor gut, ist aber ein Hobbyprojekt.

> **Hinweis:** Sie funktioniert in vielen, aber nicht in allen Situationen (siehe „Bekannte Grenzen“).

## Aus welchen Teilen besteht es?

| Teil | Aufgabe |
|---|---|
| **Windows-Programm** (`AutoTranslator.exe`) | Läuft als Symbol neben der Uhr, erkennt chinesischen Text auf dem Bildschirm und blendet die Übersetzung ein. |
| **Chrome-Erweiterung** | Übersetzt Webseiten direkt in Chrome und liefert dem Windows-Programm die Übersetzungen. |

Chrome muss geöffnet sein, solange übersetzt werden soll.

### Welcher Teil wofür?

**Für Webseiten ist die Chrome-Erweiterung die bessere Wahl, gerade für Alltägliches wie Online-Shopping
oder Essen bestellen bei Uber Eats.** Sie liest den echten Text der Seite statt eines Bildschirmfotos und
ist dadurch schneller und genauer: Das Englische steht direkt in der Seite, bleibt beim Scrollen an seinem
Platz, und Kleingedrucktes, Preise und Menüs werden ohne Erkennungsfehler übersetzt.

| Was übersetzt werden soll | Was man nimmt | Warum |
|---|---|---|
| Webseiten in Chrome: Shops, Essenslieferung, Nachrichten, soziale Netzwerke | **Chrome-Erweiterung** ⭐ | Sofort, genau, direkt in der Seite übersetzt |
| Programme außerhalb des Browsers: Desktop-Apps, Spiele, Installationsprogramme | **Windows-Programm** (`AutoTranslator.exe`) | Liest den Bildschirm per Texterkennung und blendet Englisch darüber ein |

Die Erweiterung funktioniert auch allein: Wer nur Webseiten übersetzen will, braucht nur die Schritte 1 und 2.
Beim Surfen das Windows-Programm am besten ausschalten (Strg+Alt+T), damit es über schon übersetzte Seiten
keine zweite Einblendung legt.

## Voraussetzungen

| Teil | Läuft auf |
|---|---|
| **Chrome-Erweiterung** (Webseiten) | **Windows, macOS und Linux**: Google Chrome 138 oder neuer auf einem Desktop-Computer |
| **Windows-Programm** (andere Programme) | Windows 10 (Version 2004 oder neuer) oder Windows 11, 64 Bit |

- Nicht auf dem Handy: Chrome für Android und iPhone kann keine Erweiterungen und hat keinen Offline-Übersetzer.

**Auf einem Mac oder Linux-PC** braucht man nur die Erweiterung: die Schritte 1 und 2 der Anleitung
(nur `AutoTranslator-chrome-extension.zip` herunterladen). Alles andere funktioniert genau wie unter Windows,
auch die Offline-Übersetzung.

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
   **„Sprachpakete laden“** anklicken. Warten, bis es fertig ist. Ein Klick lädt beide Pakete:
   Chinesisch → Englisch für Webseiten und Englisch → Chinesisch für die Schreibhilfe. Das ist nur einmal nötig.

Der Ordner darf danach nicht gelöscht oder verschoben werden.

> **Nur Webseiten übersetzen (Einkaufen, Uber Eats, Nachrichten …)? Dann ist man hier schon fertig.**
> Die Schritte 3 und 4 braucht man nur für Programme außerhalb des Browsers.

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

## Text in Bildern

Shops packen viel Text in Bilder: Preise und Angebote in Bannern, Größen und Material in langen Detailbildern.
Die Erweiterung übersetzt auch diese, automatisch und offline.

- Größere Bilder im sichtbaren Bereich einer chinesischen Seite werden nacheinander gelesen, etwa 0,5 s pro
  Bild (ein langes Detailbild dauert ein paar Sekunden). Das Englische erscheint als dunkle Kästchen über dem
  Chinesischen.
- Der kleine Knopf **原文** am Bild zeigt das Original, ein weiterer Klick wieder die Übersetzung.
- **Rechtsklick auf ein Bild → „Bild übersetzen“** übersetzt ein einzelnes Bild sofort, auch kleine.
- Abschalten lässt es sich im Popup der Erweiterung (*Bilder automatisch übersetzen*).

Die Texterkennung (PaddleOCR PP-OCRv4 mit ONNX Runtime) steckt in der Erweiterung selbst; deshalb ist der
Download der Erweiterung etwa 30 MB groß. Bilder werden auf dem eigenen PC gelesen und nie hochgeladen.
Verzierte Schriften, sehr kleiner oder gebogener Text und Text auf unruhigem Hintergrund werden manchmal
falsch gelesen.

## Auf Chinesisch schreiben

Eine Nachricht an den Verkäufer, eine Suche im chinesischen Shop, ein Formular? Einfach auf Englisch tippen.

1. In ein Textfeld auf einer Seite mit chinesischem Text klicken (Chat, Suche, Kommentar, Nachricht …).
2. Auf Englisch tippen. Nach einer kurzen Pause zeigt eine dunkle Leiste unter dem Feld die chinesische
   Übersetzung.
3. Auf **Einfügen** klicken oder **Strg+Umschalt+Enter** drücken: Der chinesische Text ersetzt das Getippte.
   **Esc** oder **×** blendet die Leiste für dieses Feld aus.

Die Schreibhilfe drängt sich nicht auf: Sie erscheint nur auf Seiten mit chinesischem Text, nur bei englischer
Eingabe und nie bei Passwort-, E-Mail-, Telefon-, Zahlen- oder Kartennummer-Feldern. Das Getippte wird auf dem
eigenen PC übersetzt, nicht gespeichert und nur nach „Einfügen“ ins Feld geschrieben. Abschalten lässt sie sich
im Popup der Erweiterung (*Schreibhilfe in Eingabefeldern*).

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

Vor dem Laden der Erweiterung aus einer Git-Kopie einmal `node tools/fetch-ocr-assets.mjs` ausführen;
das lädt die Texterkennung für Bilder (ca. 29 MB, nicht im Git). Ein Ablaufdiagramm steht in [docs/workflow.svg](docs/workflow.svg). Aufbau und Schnittstellen sind in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) beschrieben (englisch).
Das Windows-Programm wird von GitHub Actions gebaut (`.github/workflows/build.yml`).

## Lizenz

[MIT](LICENSE): frei nutzbar, veränderbar und weitergebbar, auch kommerziell, solange der Lizenzhinweis erhalten bleibt.
