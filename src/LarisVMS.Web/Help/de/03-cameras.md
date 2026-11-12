# Kameras

Kameras hinzufügen, die Kameraliste, Streams, Geräteeinstellungen, Gruppen, Mehrobjektivgeräte und Hersteller-Integrationen.

## Eine Kamera hinzufügen

Verwenden Sie **Kameras → Suchen**, um das LAN nach ONVIF-Geräten zu durchsuchen, oder **Kameras → Hinzufügen**, um eine Gerätedienst-URL (zum Beispiel `http://192.168.1.50/onvif/device_service`) und Zugangsdaten einzugeben. LarisVMS **fragt** die Kamera dann **ab**: Es liest ihre ONVIF-Fähigkeiten und Streams und wählt den Haupt- und den Nebenstream aus.

Weisen Sie einen **Aufzeichnungsknoten** zu, sonst zeichnet die Kamera nicht auf.

## Die Kameraliste

**Kameras** listet jede Kamera auf, die Sie sehen können. Die Zeile unter den Spaltenüberschriften filtert nach jeder Spalte, etwa Hersteller, Modell, Knoten oder Gruppe, und das Suchfeld über der Tabelle filtert nach Text an beliebiger Stelle einer Zeile. Filter werden kombiniert, und **Filter zurücksetzen** setzt sie zurück. **Spalten** wählt, welche Spalten angezeigt werden. Ein Filter auf eine ausgeblendete Spalte wird ignoriert.

Ihre Filter, Spalten, Sortierung und Seitengröße werden in Ihrem Konto gespeichert, sodass sie bei der nächsten Anmeldung zurückkommen, auf jedem Gerät.

Kreuzen Sie Kameras an, um mehrere gleichzeitig zu bearbeiten: **Ausgewählte einem Knoten zuweisen** oder **Geräteeinstellungen…** für Kameras desselben Herstellers und Modells (siehe [Sammeländerungen](#bulk-changes)). **Alle auswählen** wählt nur die Kameras, die den aktuellen Filtern entsprechen.

## Neu abfragen

**Neu abfragen** auf der Seite einer Kamera liest ihre Fähigkeiten und Streams erneut. Tun Sie das, nachdem Sie Auflösung, Codec oder Firmware der Kamera auf ihrer eigenen Weboberfläche geändert haben. Änderungen in den [Geräteeinstellungen](#device-settings) fragen automatisch neu ab. Eine Änderung der Geräte-URL fragt beim Speichern automatisch neu ab. **Einstellungen → Kameraeinstellungen** kann jede Kamera einmal täglich neu abfragen. Kameras werden nacheinander abgefragt, damit günstige Kameras ihre Aufzeichnungssitzungen nicht abbrechen. Stream-Namen und Aktivierungsstatus bleiben bei einer Neuabfrage erhalten.

## Streams

Jede Kamera hat meist einen **Hauptstream** (volle Auflösung, für die Aufzeichnung) und einen **Nebenstream** (geringere Auflösung, für kleine Live-Kacheln und die KI-Erkennung). Klappen Sie **Gerät** auf der Seite der Kamera auf, um einen Stream umzubenennen oder zu deaktivieren. Fahren Sie mit der Maus über einen Stream, um seine RTSP-URL zu sehen. LarisVMS verwendet die Adresse, mit der die Kamera hinzugefügt wurde, und übernimmt Port und Pfad, die die Kamera meldet, sodass eine Kamera, die eine interne Adresse meldet (oder jemand, der sich als sie ausgibt), den Aufzeichnungsknoten nicht mit ihrem Passwort woandershin schicken kann.

## Geräteeinstellungen

**Geräteeinstellungen** auf der Seite einer Kamera ändert die eigenen Einstellungen der Kamera über ONVIF, sodass Sie sich nicht auf der Weboberfläche der Kamera anmelden müssen. Die Einstellungen werden live von der Kamera gelesen, und nur die unterstützten werden angezeigt.

| Abschnitt | Einstellungen |
|---|---|
| **Videostreams** | Für jeden Stream: Codec (H.264, H.265 oder MJPEG), Kodierungsprofil, Auflösung, Bildrate, Bitratenmodus (CBR oder VBR), Bitrate, Qualität und Keyframe-Intervall |
| **Bild** | Helligkeit, Kontrast, Sättigung und Schärfe; Tag/Nacht-Modus; WDR; Gegenlichtkompensation; Belichtungsmodus und Weißabgleich |
| **Audio** | Codec, Bitrate und Abtastrate |

Jeder Abschnitt hat eine eigene Schaltfläche **Anwenden**, und nur die geänderten Einstellungen werden gesendet. Eine Änderung an Stream oder Audio lässt den Stream neu verbinden, was eine Lücke von einigen Sekunden in der Aufzeichnung hinterlässt; LarisVMS fragt die Kamera dann neu ab, und der Aufzeichnungsknoten übernimmt das neue Format sofort. Bildänderungen werden ohne Neuverbindung wirksam.

Manche Kameras teilen einen Satz Encoder-Einstellungen zwischen Streams. Die Seite weist darauf hin, da eine Änderung des einen auch den anderen ändert.

H.265 und die Wahl zwischen CBR/VBR erfordern eine Kamera mit ONVIF Profil T (Media2). Bei älteren Kameras erscheinen diese Optionen nicht.

## Sammeländerungen

So ändern Sie mehrere Kameras desselben Herstellers und Modells gleichzeitig:

1. Filtern Sie unter **Kameras** die Spalte **Modell** und kreuzen Sie dann die Kameras an (oder **Alle auswählen**).
2. Klicken Sie auf **Geräteeinstellungen…**. Das ist nur verfügbar, wenn alle angekreuzten Kameras denselben Hersteller und dasselbe Modell haben.
3. Ändern Sie, was nötig ist, und klicken Sie in diesem Abschnitt auf **Anwenden**.

Sie können auch auf der Seite **Geräteeinstellungen** einer Kamera beginnen und unter **Anwenden auf** weitere Kameras desselben Modells wählen. Ein Abzeichen *Firmware weicht ab* markiert Kameras mit anderer Firmware, die leicht abweichende Optionen bieten können.

Die Seite zeigt die aktuellen Einstellungen der ersten Kamera. An jede Kamera werden nur die geänderten Einstellungen gesendet, sodass die übrigen eigenen Einstellungen jeder Kamera unverändert bleiben. Streams werden nach Rolle zugeordnet (Haupt, Neben oder Dritter). Jede Kamera prüft die Änderung gegen ihre eigenen Optionen, und die Ergebnisliste zeigt für jede Kamera, ob die Änderung angewendet, teilweise angewendet, übersprungen (zum Beispiel bei einer Kamera ohne dritten Stream) oder fehlgeschlagen ist (zum Beispiel offline). **Fehlgeschlagene wiederholen** sendet die Änderung erneut an die Kameras, bei denen sie fehlgeschlagen ist.

## Gruppen

Gruppen ordnen Kameras nach Standorten, Gebäuden und Etagen. Eine **Gruppe der obersten Ebene ist ein Standort**, und Gruppen darunter sind Gebäude oder Etagen. Eine Kamera kann in beliebig vielen Gruppen sein, solange sie alle zum selben Standort gehören. Gruppen dienen zum Filtern, für Überschreibungen von Einstellungen und für den [Kamerazugriff](/Help/users#camera-access). Die integrierte Gruppe *Alle Kameras* enthält immer jede Kamera.

## Mehrkanal

Manche Geräte haben mehrere Objektive in einem Gehäuse. Findet eine Abfrage mehr als eine Videoquelle, bietet die Seite der Kamera **In N Kameras aufteilen** an. Jedes Objektiv wird dann zu einer eigenen Kamera mit eigener Aufzeichnung und Aufbewahrung. Die vorhandene Kamera wird Kanal 1 und behält ihre Aufnahmen.

## Integrationen

Manche Funktionen sind über ONVIF nicht verfügbar. Integrationen (aufgeführt unter **Einstellungen → Plugins**) werden bei einer Abfrage automatisch anhand von Hersteller und Modell der Kamera zugeordnet. Es gibt nichts einzuschalten. Die Integration **Dahua / Amcrest Smart Events** liest zum Beispiel Personen- und Fahrzeugerkennungen aus der eigenen Analyse der Kamera. Das Feld **Gerät** der Kamera zeigt an, wenn eine Integration aktiv ist.
