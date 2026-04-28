# MBToolsIntern

Interne Variante von MerkurTools (ohne KI-Assistent), zum Hosten auf einem Windows Server unter IIS.

Funktionen:

- PDF verschlüsseln (AES-256, Zufallspasswort)
- Dokument → PDF (LibreOffice Headless)
- Text aus PDF extrahieren
- PDF aufteilen (Seitenbereich)
- PDFs zusammenführen
- PDF-Informationen
- Speichern-Dialog mit Pfadauswahl
- E-Mail-Versand mit verschlüsseltem Anhang via Outlook (mailto-Hybrid)

## Tech-Stack

- ASP.NET Core 8 (Microsoft.NET.Sdk.Web)
- PDFsharp 6.2 (PDF-Verschlüsselung)
- UglyToad.PdfPig (Text-Extraktion, Split, Merge)
- LibreOffice Headless (Dokument → PDF)

## Voraussetzungen am Server

1. **Windows Server** (2019 / 2022 / 2025) mit IIS-Rolle.
2. **ASP.NET Core 8 Hosting Bundle** installiert: <https://dotnet.microsoft.com/download/dotnet/8.0> → "Hosting Bundle".
   - Nach der Installation IIS neu starten: `iisreset` in einer Admin-PowerShell.
3. **LibreOffice** installiert (für die "Dokument → PDF"-Konvertierung).
   - Empfohlen: `C:\Program Files\LibreOffice\` (Standard-Pfad).
   - Entweder den Pfad in `appsettings.json` eintragen (`LibreOfficePath`) oder `C:\Program Files\LibreOffice\program\` in die System-`PATH`-Variable aufnehmen, damit `soffice` erreichbar ist.

## Veröffentlichung (Publish)

Auf dem Build-Rechner:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Publish\MBToolsIntern
```

Der Output enthält die `MBToolsIntern.dll`, alle Assets (`wwwroot`) und die `web.config`.

## IIS-Konfiguration

1. **Site-Verzeichnis** anlegen, z. B. `C:\inetpub\MBToolsIntern\`.
2. Den Inhalt aus `C:\Publish\MBToolsIntern` dorthin kopieren.
3. **Application-Pool** anlegen: Name z. B. `MBToolsIntern`, **.NET CLR-Version: "Kein verwalteter Code"** (No Managed Code), Identität: `ApplicationPoolIdentity`.
4. Im IIS-Manager: **Sites → Website hinzufügen…**
   - Site-Name: `MBToolsIntern`
   - App-Pool: `MBToolsIntern`
   - Physischer Pfad: `C:\inetpub\MBToolsIntern\`
   - Bindung: HTTP / HTTPS, Port nach Wahl (z. B. 8080) oder Hostname `mbtools.intern.merkur.local`.
5. **Schreibrechte** für das Upload-Verzeichnis vergeben (App-Pool-Identität benötigt Write auf `wwwroot\uploads` und auf das `logs`-Verzeichnis):
   ```powershell
   icacls "C:\inetpub\MBToolsIntern\wwwroot\uploads" /grant "IIS AppPool\MBToolsIntern:(OI)(CI)M"
   icacls "C:\inetpub\MBToolsIntern\logs" /grant "IIS AppPool\MBToolsIntern:(OI)(CI)M"
   ```
6. **HTTPS empfohlen**: Zertifikat im IIS-Manager unter "Serverzertifikate" hinzufügen, dann der Site eine HTTPS-Bindung mit dem Zertifikat zuweisen. Damit funktioniert auch `showSaveFilePicker` (benötigt `secure context`) für den Speicherort-Dialog.

## Konfiguration

`appsettings.json` (oder besser: `appsettings.Production.json` neben `appsettings.json` im Site-Verzeichnis):

```json
{
  "LibreOfficePath": "C:\\Program Files\\LibreOffice\\program\\soffice.exe"
}
```

Wenn das Feld leer ist, versucht die App `soffice` aus dem PATH (Windows) bzw. `libreoffice` (Linux).

## Logs

In der `web.config` ist `stdoutLogEnabled` standardmäßig `false`. Für Diagnose temporär auf `true` setzen — Logs landen unter `logs\stdout_*.log`.

## Updates

1. Neu publizieren: `dotnet publish -c Release -r win-x64 --self-contained false -o C:\Publish\MBToolsIntern`.
2. App-Pool stoppen: `Stop-WebAppPool -Name "MBToolsIntern"`.
3. Dateien überschreiben.
4. App-Pool starten: `Start-WebAppPool -Name "MBToolsIntern"`.

Alternativ funktioniert ein einfaches "Anstoßen": Eine Datei `app_offline.htm` in den Site-Ordner legen, Dateien überschreiben, `app_offline.htm` löschen.
