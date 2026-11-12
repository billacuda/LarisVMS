# Überblick

Wie LarisVMS zusammenspielt: der Webserver, Aufzeichnungsknoten, Medien-Proxys und Kameras.

## Die Bausteine

| Baustein | Was er tut |
|---|---|
| **Webserver** | Die LarisVMS-Webanwendung. Speichert die Konfiguration in SQL Server oder MariaDB, stellt Oberfläche und API bereit und leitet Live- und Wiedergabevideo weiter, sofern direktes Streaming nicht aktiviert ist. Läuft als Windows-Dienst *LarisVMS Web*. |
| **Aufzeichnungsknoten** | Ein Windows-Dienst auf jedem Aufzeichnungsrechner. Holt RTSP von seinen Kameras, schreibt Segmente in seinen Speicherpfad, setzt die Aufbewahrung durch, führt Bewegungs- und KI-Erkennung aus und meldet seinen Zustand. Ein Server kann viele Knoten haben. |
| **Vision-Dienst** | Optionaler Unterprozess eines Knotens, der die KI-Objekterkennung auf der GPU (oder CPU) des Knotens ausführt. Ein Absturz hier stoppt nie die Aufzeichnung. |
| **Medien-Proxy** | Optionales Relay zwischen Browsern und Knoten, für entfernte Standorte oder Verbindungen mit geringer Bandbreite. |
| **Kameras** | ONVIF-Kameras. Jede ist genau einem Aufzeichnungsknoten zugewiesen. |

## Wie das Video fließt

1. Ein Knoten öffnet eine RTSP-Sitzung zu jeder seiner Kameras und zeichnet mit `ffmpeg -c copy` auf, es wird also nicht neu kodiert.
2. Aufnahmen werden als kurze Segmentdateien (siehe [Aufzeichnung](/Help/recording)) im Speicherpfad des Knotens abgelegt.
3. Live-Ansicht und Wiedergabe werden vom Knoten bereitgestellt und über diesen Server, einen Medien-Proxy oder direkt an den Browser weitergeleitet (siehe [Knoten](/Help/nodes#direct-streaming)).

## Datenschutz

LarisVMS wird selbst betrieben: Ihr Video und Ihre Einstellungen bleiben auf Ihren eigenen Rechnern, und über Ihre Installation wird nichts gesammelt oder weitergegeben. Es gibt keine Telemetrie und kein „Nach-Hause-Telefonieren“. Die einzige regelmäßige ausgehende Anfrage ist eine tägliche Prüfung der GitHub-Releases von LarisVMS auf eine neue Version (siehe [Aktualisierungen](/Help/installation#updates)); sie lässt sich deaktivieren. Der andere integrierte Download ist ein YOLOX-KI-Modell aus den GitHub-Releases dieses Projekts, wenn die Erkennung zum ersten Mal eines verwendet, sofern Sie die Dateien nicht selbst auf den Server legen. Straßenkarten auf der Seite [Karte](/Help/live-and-playback#map) laden ihre Kartenbilder in Ihrem Browser von OpenStreetMap oder einem Kachelserver Ihrer Wahl; Grundrisse nicht. Alles andere geht nur an Ihre Kameras, Ihre eigenen Server und Dienste, die Sie einrichten, etwa E-Mail und Alarmkanäle.

## Vererbung von Einstellungen

Die meisten Einstellungen gibt es auf drei Ebenen. Die spezifischste gewinnt:

1. **Kamera**: auf der eigenen Bearbeitungsseite der Kamera.
2. **Knoten**: auf der Bearbeitungsseite des Knotens unter **Knoten**.
3. **Global**: auf der passenden Seite unter **Einstellungen**.

Ein leeres Feld (oder eine Option „übernehmen – …“) verwendet die nächsthöhere Ebene und zeigt den Wert, den es aktuell übernimmt.

## Dashboard

Das Dashboard besteht aus Widgets. Jeder Benutzer wählt seine eigenen, und das Layout wird in seinem Konto gespeichert, sodass es ihm in andere Browser folgt.

- **Anpassen** (oben rechts; ausgeblendet, wenn das Fenster für das volle Raster zu schmal ist) entsperrt das Layout. Ziehen Sie ein Widget an seiner Titelleiste, ändern Sie seine Größe an der Ecke oder entfernen Sie es mit ×. **Widget hinzufügen** listet alles auf, was Sie hinzufügen können, und **Layout zurücksetzen** kehrt zum Standard zurück. Klicken Sie auf **Fertig**, wenn Sie fertig sind.
- Beim Anpassen hat jedes Widget Schaltflächen zum Verschieben und Skalieren ohne Ziehen.
- **Aktualisierungen pausieren** verhindert, dass sich Widgets ändern, während Sie sie lesen.
- Die meisten Widgets sind Top-10-Listen von Kameras oder Knoten, etwa belegter Speicher, Bildrate, Bandbreite, älteste Aufnahmen, Offline-Kameras, Zeit seit der letzten Aufnahme und Erkennungen. Einige haben ein Zeitfenster (1 Std., 24 Std., 7 T., 30 T.), und einige können zwischen höchsten und niedrigsten Werten zuerst wechseln.
- Klicken Sie auf den Titel eines Widgets, um seinen vollständigen Bericht zu öffnen: jede Kamera oder jeder Knoten, 100 pro Seite, mit Filterfeld, Knotenfilter, sortierbaren Spalten und CSV-Download.
- **Kameraaktivität** zeigt die letzten 7 Tage einer Kamera als Heatmap nach Tageszeit; wählen Sie die Kamera in der Titelleiste und klicken Sie auf den Titel für die vollständige Seite [Aktivität](/Help/live-and-playback#activity).
- **Neueste Erkennungen** zeigt die neuesten Schnappschüsse. Die Schaltfläche ⚙ wählt, welche Erkennungstypen einbezogen werden; einfache Bewegung (nichts erkannt) ist aus, sofern Sie sie nicht ankreuzen.
- **Zeit seit der letzten Aufnahme** markiert eine Kamera mit durchgehender Aufzeichnung, die seit mehr als 5 Minuten nichts aufgezeichnet hat.
- Knoten-Widgets, die auf Kameras basieren (Kameraanzahl, Offline-Kameras, Bildrate, verbleibende Speicherzeit), lassen Knoten ohne Kameras weg.
- Sie sehen nur Kameras, auf die Sie Zugriff haben. **Aktuelle Alarme** wird nur Benutzern angeboten, die Alarmregeln verwalten dürfen.

Widgets aktualisieren sich jede Minute.

## Auf dem Telefon

Die gesamte Website funktioniert im Browser eines Telefons. Die Seitenleiste öffnet sich über die Schaltfläche ☰ oben links.

Um sie als App zu installieren, öffnen Sie die Website in Chrome unter Android und wählen Sie im Menü ⋮ **App installieren**. Unter iOS öffnen Sie sie in Safari und wählen **Teilen → Zum Home-Bildschirm**. Die App öffnet sich im Vollbild, ohne die Adressleiste des Browsers.

Für die Installation ist ein Zertifikat nötig, dem das Telefon vertraut. Browser installieren keine Website mit dem selbstsignierten Zertifikat, mit dem eine neue Installation beginnt; siehe [Zertifikate](/Help/security#certificates).

## Wo Sie anfangen

- Neue Installation: [Installation](/Help/installation)
- Kameras hinzufügen: [Kameras](/Help/cameras)
- Festlegen, was aufbewahrt wird: [Aufzeichnung](/Help/recording) und [Speicher und Aufbewahrung](/Help/storage)
- Eigene Werkzeuge anbinden: [API](/Help/api)
- Eine andere Sprache verwenden: [Sprachen](/Help/languages)
- Tastatur und Bildschirmleser: [Barrierefreiheit](/Help/accessibility)
- Etwas stimmt nicht: [Fehlerbehebung](/Help/troubleshooting)
