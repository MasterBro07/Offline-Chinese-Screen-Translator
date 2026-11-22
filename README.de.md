# Offline Chinese Screen Translator

*Die Chrome-Erweiterung heißt **Auto Translator**.*

*English version: [README.md](README.md)*

Auto Translator ist eine Chrome-Erweiterung, die chinesische Webseiten **automatisch und ohne Klick** ins
Englische übersetzt: den Seitentext, den Text in Bildern und, umgekehrt, das, was man selbst tippt.

Übersetzt wird mit der in Google Chrome eingebauten Übersetzungsfunktion, die Texterkennung für Bilder
steckt in der Erweiterung selbst. Das ist **kostenlos und läuft komplett auf dem eigenen Computer**: Nach dem
einmaligen Herunterladen der Sprachpakete wird kein Text und kein Bild ins Internet geschickt. Kein Konto,
kein API-Schlüssel, kein Abo.

> **🤖 Gevibecoded.** Dieses Projekt ist komplett mit KI entstanden: Der Autor hat in normaler Sprache
> beschrieben, was er möchte, und [Claude Code](https://claude.com/claude-code) hat den Code geplant,
> geschrieben, getestet und dokumentiert. Keine Zeile wurde von Hand programmiert. Die Erweiterung wird
> automatisch unter Windows, macOS und Linux getestet und läuft beim Autor gut, ist aber ein Hobbyprojekt.

Ein Ablaufdiagramm steht in [docs/workflow.svg](docs/workflow.svg).

## Was sie kann

- **Ohne Klick:** Chinesischer Text wird übersetzt, sobald er erscheint, auch beim Scrollen, in Pop-ups,
  Chats, eingebetteten Bereichen, Auswahllisten und Bildbeschreibungen.
- **Direkt in der Seite:** Das Englische ersetzt das Chinesische; mit der Maus darüberfahren zeigt das
  Original. Eingabefelder und Passwörter werden nie angefasst.
- **Text in Bildern** wird erkannt und übersetzt (siehe unten).
- **Schreibhilfe:** Englisch tippen, Chinesisch einfügen (siehe unten).
- **Vereinfachtes und traditionelles Chinesisch** (Festland, Taiwan, Hongkong): Jeder Text geht an das
  passende Modell von Chromes Übersetzer.

## Voraussetzungen

- **Google Chrome 138 oder neuer** auf einem Computer: **Windows, macOS oder Linux**
- Nicht auf dem Handy: Chrome für Android und iPhone kann keine Erweiterungen und hat keinen Offline-Übersetzer.

## 1. Herunterladen

1. Das [**neueste Release**](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/releases/latest)
   öffnen und `AutoTranslator-chrome-extension.zip` herunterladen.

   *Noch kein Release da?* Unter **Actions** → **Build** den neuesten grünen Lauf öffnen und ganz unten bei
   **Artifacts** die Erweiterung herunterladen (dafür muss man bei GitHub angemeldet sein).
2. Die ZIP-Datei entpacken (Rechtsklick → „Alle extrahieren…“), am besten an einen festen Ort,
   z. B. `Dokumente\AutoTranslator`. Der Ordner darf danach nicht gelöscht oder verschoben werden.

## 2. In Chrome laden

1. In Chrome die Adresse `chrome://extensions` öffnen.
2. Oben rechts den **Entwicklermodus** einschalten.
3. Auf **„Entpackte Erweiterung laden“** klicken und den entpackten Ordner auswählen.
4. Auf der Willkommensseite (oder im Popup der Erweiterung) **„Sprachpakete laden“** anklicken und warten,
   bis *bereit* dasteht. Ein Klick lädt alle Pakete: vereinfachtes und traditionelles Chinesisch → Englisch
   für Webseiten und Englisch → Chinesisch für die Schreibhilfe. Das ist nur einmal nötig.

**Fertig.** Chinesische Webseiten werden jetzt automatisch übersetzt. Schon offene Tabs einmal neu laden.

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
Download etwa 17 MB groß (entpackt etwa 30 MB). Bilder werden auf dem eigenen PC gelesen und nie hochgeladen.
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

Alles über das Symbol der Erweiterung (eventuell zuerst auf das Puzzle-Symbol klicken):

- **Webseiten automatisch übersetzen** – Übersetzung ein/aus
- **Auf dieser Seite deaktivieren** – für eine einzelne Website abschalten
- **Bilder automatisch übersetzen** – Texterkennung in Bildern ein/aus
- **Schreibhilfe in Eingabefeldern** – ein/aus
- Oben steht, ob die Sprachpakete bereit sind.

## Wenn etwas nicht klappt

- **Nach einem Update oder Neuinstallieren der Erweiterung wird nichts übersetzt:** offene Tabs einmal neu laden
  (**F5**). Chrome setzt die neue Version nur in Seiten ein, die nach dem Update geöffnet werden.
- **Im Popup steht „Sprachpakete fehlen“:** im Popup auf **„Sprachpakete laden“** klicken und warten, bis
  *bereit* dasteht.
- **Das Popup meldet, dass der Übersetzer nicht verfügbar ist:** Chrome aktualisieren (Version 138 oder neuer)
  und auf einem Computer verwenden.
- **Seiten in traditionellem Chinesisch (Taiwan, Hongkong) bleiben nach einem Update chinesisch:** Das
  Paket dafür ist neu; im Popup einmal **„Sprachpakete laden“** klicken.
- **Ein Bild wird nicht übersetzt:** Sehr kleine Bilder werden ausgelassen; Rechtsklick → **„Bild übersetzen“**.

## Bekannte Grenzen

- Nur Chinesisch → Englisch (und Englisch → Chinesisch für die Schreibhilfe).
- Nur in Google Chrome auf dem Computer, nicht in anderen Browsern oder auf dem Handy.
- Chromes Offline-Übersetzer ist ein kleines Modell; sein Englisch klingt manchmal holpriger als bei
  Online-Übersetzern.
- Die Texterkennung in Bildern liest nicht perfekt, besonders bei Zierschrift, sehr kleinem Text oder
  unruhigem Hintergrund.

## Für Entwickler

Vor dem Laden der Erweiterung aus einer Git-Kopie einmal `node tools/fetch-ocr-assets.mjs` ausführen;
das lädt die Texterkennung für Bilder (ca. 29 MB, nicht im Git). Aufbau und Schnittstellen stehen in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (englisch).

## Lizenz

[MIT](LICENSE): frei nutzbar, veränderbar und weitergebbar, auch kommerziell, solange der Lizenzhinweis erhalten bleibt.
