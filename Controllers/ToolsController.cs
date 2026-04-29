using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepL;
using Microsoft.AspNetCore.Mvc;
using PDFtoImage;
using SkiaSharp;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;
using PdfSharpDocument = PdfSharp.Pdf.PdfDocument;
using PdfSharpReader = PdfSharp.Pdf.IO.PdfReader;
using PdfSharpOpenMode = PdfSharp.Pdf.IO.PdfDocumentOpenMode;

namespace MBToolsIntern.Controllers;

[Route("api/[action]")]
public class ToolsController : Controller
{
    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ToolsController> _log;

    public ToolsController(IWebHostEnvironment env, IHttpClientFactory httpFactory, IConfiguration config, ILogger<ToolsController> log)
    {
        _env = env;
        _httpFactory = httpFactory;
        _config = config;
        _log = log;
    }

    // ── PDF zusammenführen ──────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> PdfMerge(List<IFormFile> dateien)
    {
        if (dateien == null || dateien.Count < 2)
            return Json(new { error = "Mindestens 2 PDF-Dateien erforderlich." });

        try
        {
            var streams = new List<MemoryStream>();
            foreach (var datei in dateien)
            {
                var ms = new MemoryStream();
                await datei.CopyToAsync(ms);
                ms.Position = 0;
                streams.Add(ms);
            }

            var inputPaths = new List<string>();
            var tempDir = Path.Combine(Path.GetTempPath(), "merkurtools_merge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            for (int i = 0; i < streams.Count; i++)
            {
                var p = Path.Combine(tempDir, $"{i}.pdf");
                await System.IO.File.WriteAllBytesAsync(p, streams[i].ToArray());
                inputPaths.Add(p);
            }
            var result = PdfMerger.Merge(inputPaths.ToArray());

            foreach (var s in streams) s.Dispose();
            return File(result, "application/pdf", "zusammengefuehrt.pdf");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDF-Merge fehlgeschlagen");
            return Json(new { error = "Zusammenführung fehlgeschlagen: " + ex.Message });
        }
    }

    // ── PDF aufteilen (Seiten extrahieren) ──────────────────────────────
    [HttpPost]
    public async Task<IActionResult> PdfSplit(IFormFile datei, int vonSeite = 1, int bisSeite = 0)
    {
        if (datei == null) return Json(new { error = "Keine Datei." });

        try
        {
            using var ms = new MemoryStream();
            await datei.CopyToAsync(ms);
            ms.Position = 0;

            using var pdf = PdfDocument.Open(ms);
            var total = pdf.NumberOfPages;
            if (bisSeite <= 0 || bisSeite > total) bisSeite = total;
            if (vonSeite < 1) vonSeite = 1;
            if (vonSeite > bisSeite) return Json(new { error = $"Ungültiger Seitenbereich ({vonSeite}-{bisSeite})." });

            var builder = new PdfDocumentBuilder();
            for (int i = vonSeite; i <= bisSeite; i++)
            {
                ms.Position = 0;
                builder.AddPage(PdfDocument.Open(ms), i);
            }

            var result = builder.Build();
            var name = $"seiten_{vonSeite}-{bisSeite}.pdf";
            return File(result, "application/pdf", name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDF-Split fehlgeschlagen");
            return Json(new { error = "Aufteilen fehlgeschlagen: " + ex.Message });
        }
    }

    // ── PDF Text extrahieren ────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> PdfText(IFormFile datei)
    {
        if (datei == null) return Json(new { error = "Keine Datei." });

        try
        {
            using var ms = new MemoryStream();
            await datei.CopyToAsync(ms);
            ms.Position = 0;

            using var pdf = PdfDocument.Open(ms);
            var sb = new StringBuilder();
            for (int i = 1; i <= pdf.NumberOfPages; i++)
            {
                var page = pdf.GetPage(i);
                var text = string.Join(" ", page.GetWords().Select(w => w.Text));
                sb.AppendLine($"--- Seite {i} ---");
                sb.AppendLine(text);
                sb.AppendLine();
            }

            return Json(new { text = sb.ToString(), seiten = pdf.NumberOfPages });
        }
        catch (Exception ex)
        {
            return Json(new { error = "Textextraktion fehlgeschlagen: " + ex.Message });
        }
    }

    // ── PDF Info ─────────────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> PdfInfo(IFormFile datei)
    {
        if (datei == null) return Json(new { error = "Keine Datei." });

        try
        {
            using var ms = new MemoryStream();
            await datei.CopyToAsync(ms);
            ms.Position = 0;

            using var pdf = PdfDocument.Open(ms);
            var info = pdf.Information;

            return Json(new
            {
                dateiname = datei.FileName,
                groesse = datei.Length,
                seiten = pdf.NumberOfPages,
                titel = info.Title,
                autor = info.Author,
                erstellt = info.CreationDate,
                geaendert = info.ModifiedDate,
                erzeuger = info.Creator,
                produzent = info.Producer
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = "Info konnte nicht gelesen werden: " + ex.Message });
        }
    }

    // ── Dokument → PDF konvertieren (LibreOffice headless) ──────────────
    [HttpPost]
    public async Task<IActionResult> ConvertToPdf(IFormFile datei)
    {
        if (datei == null || datei.Length == 0)
            return Json(new { error = "Keine Datei." });

        var erlaubt = new[] { ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".ods", ".odp", ".rtf", ".txt", ".html", ".csv" };
        var ext = Path.GetExtension(datei.FileName).ToLowerInvariant();
        if (!erlaubt.Contains(ext))
            return Json(new { error = $"Dateityp {ext} nicht unterstützt. Erlaubt: {string.Join(", ", erlaubt)}" });

        var tempDir = Path.Combine(Path.GetTempPath(), "merkurtools_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var inputPath = Path.Combine(tempDir, datei.FileName);
            await using (var fs = new FileStream(inputPath, FileMode.Create))
                await datei.CopyToAsync(fs);

            var sofficePfad = _config["LibreOfficePath"];
            if (string.IsNullOrWhiteSpace(sofficePfad))
                sofficePfad = OperatingSystem.IsWindows() ? "soffice" : "libreoffice";

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = sofficePfad,
                    Arguments = $"--headless --convert-to pdf --outdir \"{tempDir}\" \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();

            var pdfName = Path.GetFileNameWithoutExtension(datei.FileName) + ".pdf";
            var pdfPath = Path.Combine(tempDir, pdfName);

            if (!System.IO.File.Exists(pdfPath))
            {
                var err = await process.StandardError.ReadToEndAsync();
                _log.LogWarning("LibreOffice Fehler: {Err}", err);
                return Json(new { error = "Konvertierung fehlgeschlagen. LibreOffice konnte die Datei nicht verarbeiten." });
            }

            var pdfBytes = await System.IO.File.ReadAllBytesAsync(pdfPath);
            return File(pdfBytes, "application/pdf", pdfName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Konvertierung fehlgeschlagen");
            return Json(new { error = "Konvertierung fehlgeschlagen: " + ex.Message });
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── PDF verschlüsseln (AES-256, Random-Passwort) ────────────────────
    [HttpPost]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> PdfEncrypt(IFormFile datei, string? passwort = null)
    {
        if (datei == null || datei.Length == 0)
            return Json(new { error = "Keine Datei." });

        if (!datei.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Json(new { error = "Nur PDF-Dateien werden unterstützt." });

        try
        {
            using var inputStream = new MemoryStream();
            await datei.CopyToAsync(inputStream);
            inputStream.Position = 0;

            PdfSharpDocument document;
            try
            {
                document = PdfSharpReader.Open(inputStream, PdfSharpOpenMode.Modify);
            }
            catch (Exception ex) when (ex.GetType().Name.Contains("PdfReader"))
            {
                return Json(new { error = "PDF konnte nicht geöffnet werden. Möglicherweise ist sie bereits passwortgeschützt oder beschädigt." });
            }

            passwort = string.IsNullOrEmpty(passwort) ? GenerateSecurePassword(16) : passwort;

            var sec = document.SecuritySettings;
            sec.OwnerPassword = passwort;
            sec.UserPassword = passwort;
            sec.PermitPrint = true;
            sec.PermitFullQualityPrint = true;
            sec.PermitExtractContent = true;
            sec.PermitFormsFill = true;
            sec.PermitModifyDocument = true;
            sec.PermitAnnotations = true;
            sec.PermitAssembleDocument = true;

            try
            {
                document.SecurityHandler.SetEncryptionToV5(true);
            }
            catch
            {
                document.SecurityHandler.SetEncryptionToV4UsingAES(true);
            }

            using var outputStream = new MemoryStream();
            document.Save(outputStream);

            var dateiname = Path.GetFileNameWithoutExtension(datei.FileName) + "_verschluesselt.pdf";
            var base64 = Convert.ToBase64String(outputStream.ToArray());

            return Json(new
            {
                passwort,
                dateiname,
                groesse = outputStream.Length,
                data = base64
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDF-Verschlüsselung fehlgeschlagen");
            return Json(new { error = "Verschlüsselung fehlgeschlagen: " + ex.Message });
        }
    }

    private static string GenerateSecurePassword(int laenge)
    {
        const string klein = "abcdefghijkmnpqrstuvwxyz";
        const string gross = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string ziffer = "23456789";
        const string sonder = "-_!$";
        var alle = klein + gross + ziffer + sonder;

        var pwd = new char[laenge];
        pwd[0] = klein[RandomNumberGenerator.GetInt32(klein.Length)];
        pwd[1] = gross[RandomNumberGenerator.GetInt32(gross.Length)];
        pwd[2] = ziffer[RandomNumberGenerator.GetInt32(ziffer.Length)];
        pwd[3] = sonder[RandomNumberGenerator.GetInt32(sonder.Length)];
        for (int i = 4; i < laenge; i++)
            pwd[i] = alle[RandomNumberGenerator.GetInt32(alle.Length)];

        for (int i = pwd.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (pwd[i], pwd[j]) = (pwd[j], pwd[i]);
        }
        return new string(pwd);
    }

    // ── OCR / Handschrift-Erkennung (Tesseract) ─────────────────────────
    [HttpPost]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> PdfOcr(IFormFile datei)
    {
        if (datei == null || datei.Length == 0)
            return Json(new { error = "Keine Datei." });
        if (!datei.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Json(new { error = "Nur PDF-Dateien werden unterstützt." });

        var tessdataPfad = Path.Combine(_env.ContentRootPath, "tessdata");
        if (!System.IO.File.Exists(Path.Combine(tessdataPfad, "deu.traineddata")))
            return Json(new { error = "OCR-Sprachdaten fehlen am Server (tessdata/deu.traineddata)." });

        try
        {
            using var ms = new MemoryStream();
            await datei.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var sb = new StringBuilder();
            var konfidenzen = new List<float>();
            var seiten = 0;

            using var engine = new TesseractEngine(tessdataPfad, "deu", EngineMode.LstmOnly);

            foreach (var bild in Conversion.ToImages(pdfBytes, options: new(Dpi: 300)))
            {
                seiten++;
                using (bild)
                {
                    using var bildStream = new MemoryStream();
                    bild.Encode(bildStream, SKEncodedImageFormat.Png, 100);
                    using var pix = Pix.LoadFromMemory(bildStream.ToArray());
                    using var page = engine.Process(pix);
                    var seitenText = page.GetText() ?? "";
                    var konfidenz = page.GetMeanConfidence();
                    konfidenzen.Add(konfidenz);

                    sb.AppendLine($"--- Seite {seiten} (Erkennungskonfidenz: {konfidenz * 100:F0}%) ---");
                    sb.AppendLine(string.IsNullOrWhiteSpace(seitenText) ? "(kein Text erkannt)" : seitenText.Trim());
                    sb.AppendLine();
                }
            }

            var avg = konfidenzen.Count > 0 ? konfidenzen.Average() : 0f;
            return Json(new
            {
                text = sb.ToString(),
                seiten,
                konfidenz = avg * 100,
                hinweis = "Tesseract erkennt gedruckten Text gut, klare Druckschrift-Handschrift mässig, kursive Handschrift kaum zuverlässig. Konfidenz unter 70 % deutet auf schlechte Erkennung hin."
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OCR fehlgeschlagen");
            return Json(new { error = "OCR fehlgeschlagen: " + ex.Message });
        }
    }

    // ── Proxy-Client (intern bei der Bank: Outbound-Calls über gw-int-01) ──
    private static HttpMessageHandler BuildHttpHandler()
    {
        var host = Environment.GetEnvironmentVariable("PROXY_HOST");
        if (string.IsNullOrWhiteSpace(host)) return new HttpClientHandler();
        var user = Environment.GetEnvironmentVariable("PROXY_USER");
        var pass = Environment.GetEnvironmentVariable("PROXY_PASS");
        var domain = Environment.GetEnvironmentVariable("PROXY_DOMAIN");
        var proxyUri = host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(host) : new Uri("http://" + host);
        var proxy = new WebProxy(proxyUri);
        if (!string.IsNullOrWhiteSpace(user))
            proxy.Credentials = new NetworkCredential(user, pass ?? string.Empty, domain ?? string.Empty);
        return new HttpClientHandler { Proxy = proxy, UseProxy = true };
    }

    private static HttpClient BuildProxyHttpClient(TimeSpan? timeout = null)
        => new(BuildHttpHandler()) { Timeout = timeout ?? TimeSpan.FromMinutes(5) };

    // ── Übersetzung (DeepL) ─────────────────────────────────────────────
    private Translator? GetDeepL(string? typ = null)
    {
        var istFree = string.Equals(typ, "free", StringComparison.OrdinalIgnoreCase);
        var key = istFree
            ? (Environment.GetEnvironmentVariable("DEEPL_API_KEY_FREE") ?? _config["DeepLApiKeyFree"])
            : (Environment.GetEnvironmentVariable("DEEPL_API_KEY") ?? _config["DeepLApiKey"]);
        if (string.IsNullOrWhiteSpace(key)) return null;

        // Bei gesetztem PROXY_HOST routen wir DeepL durch den Proxy
        var hatProxy = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROXY_HOST"));
        if (!hatProxy) return new Translator(key);

        var options = new TranslatorOptions
        {
            ClientFactory = () => new HttpClientAndDisposeFlag
            {
                HttpClient = BuildProxyHttpClient(),
                DisposeClient = true
            }
        };
        return new Translator(key, options);
    }

    [HttpGet]
    public async Task<IActionResult> DeeplStatus(string? typ = null)
    {
        var dl = GetDeepL(typ);
        if (dl == null) return Json(new { konfiguriert = false });
        try
        {
            var usage = await dl.GetUsageAsync();
            var verbraucht = usage.Character?.Count ?? 0;
            var limit = usage.Character?.Limit ?? 0;
            return Json(new
            {
                konfiguriert = true,
                verbraucht,
                limit,
                prozent = limit > 0 ? Math.Round(verbraucht * 100.0 / limit, 1) : 0
            });
        }
        catch (Exception ex)
        {
            return Json(new { konfiguriert = true, fehler = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Translate(string text, string ziel, string? quelle = null, string? typ = null)
    {
        var dl = GetDeepL(typ);
        if (dl == null) return Json(new { error = "DeepL ist nicht konfiguriert (kein API-Key in appsettings.json)." });
        if (string.IsNullOrWhiteSpace(text)) return Json(new { error = "Kein Text zum Übersetzen." });
        if (string.IsNullOrWhiteSpace(ziel)) return Json(new { error = "Bitte Zielsprache wählen." });

        try
        {
            var quelleClean = string.IsNullOrWhiteSpace(quelle) || quelle.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : quelle;
            var result = await dl.TranslateTextAsync(text, quelleClean, ziel);
            return Json(new
            {
                text = result.Text,
                erkannteSprache = result.DetectedSourceLanguageCode
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DeepL-Textübersetzung fehlgeschlagen");
            return Json(new { error = "Übersetzung fehlgeschlagen: " + ex.Message });
        }
    }

    [HttpPost]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> TranslateDocument(IFormFile datei, string ziel, string? quelle = null, string? typ = null)
    {
        var dl = GetDeepL(typ);
        if (dl == null) return Json(new { error = "DeepL ist nicht konfiguriert (kein API-Key in appsettings.json)." });
        if (datei == null || datei.Length == 0) return Json(new { error = "Keine Datei." });

        var ext = Path.GetExtension(datei.FileName).ToLowerInvariant();
        var erlaubt = new[] { ".docx", ".pptx", ".xlsx", ".pdf", ".html", ".htm", ".txt", ".srt", ".xlf", ".xliff" };
        if (!Array.Exists(erlaubt, e => e == ext))
            return Json(new { error = $"Dateityp {ext} wird von DeepL nicht unterstützt. Erlaubt: {string.Join(", ", erlaubt)}" });

        try
        {
            using var input = new MemoryStream();
            await datei.CopyToAsync(input);
            input.Position = 0;

            using var output = new MemoryStream();
            var quelleClean = string.IsNullOrWhiteSpace(quelle) || quelle.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : quelle;
            await dl.TranslateDocumentAsync(input, datei.FileName, output, quelleClean, ziel);

            var name = Path.GetFileNameWithoutExtension(datei.FileName) + "_" + ziel.Replace("-", "").ToLowerInvariant() + ext;
            return Json(new
            {
                data = Convert.ToBase64String(output.ToArray()),
                dateiname = name,
                groesse = output.Length
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DeepL-Dokumentübersetzung fehlgeschlagen");
            return Json(new { error = "Dokument-Übersetzung fehlgeschlagen: " + ex.Message });
        }
    }

    // ── OpenAI-Chat ─────────────────────────────────────────────────────
    [HttpGet]
    public IActionResult KiChatStatus()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config["OpenAiApiKey"];
        return Json(new
        {
            konfiguriert = !string.IsNullOrWhiteSpace(key),
            proxyAktiv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROXY_HOST"))
        });
    }

    [HttpPost]
    public async Task<IActionResult> KiChat(string nachricht, string? verlauf, string? modell)
    {
        if (string.IsNullOrWhiteSpace(nachricht))
            return Json(new { error = "Bitte eine Nachricht eingeben." });

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config["OpenAiApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            return Json(new { error = "OpenAI ist nicht konfiguriert (kein API-Key)." });

        try
        {
            var messages = new List<object>
            {
                new { role = "system", content =
                    "Du bist der KI-Chat-Assistent von MerkurTools (Merkur Privatbank). " +
                    "Antworte praezise, freundlich und auf Deutsch (sofern nicht anders gewuenscht). " +
                    "Verwende Markdown fuer Listen, Tabellen und Code-Bloecke." }
            };
            if (!string.IsNullOrWhiteSpace(verlauf))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(verlauf);
                    if (parsed != null)
                        foreach (var m in parsed.TakeLast(20))
                        {
                            var rolle = m.GetValueOrDefault("rolle", "user") == "assistant" ? "assistant" : "user";
                            messages.Add(new { role = rolle, content = m.GetValueOrDefault("text", "") });
                        }
                }
                catch { }
            }
            messages.Add(new { role = "user", content = nachricht });

            var requestBody = new
            {
                model = string.IsNullOrWhiteSpace(modell) ? "gpt-4o-mini" : modell,
                temperature = 0.5,
                max_tokens = 2500,
                messages
            };

            using var client = BuildProxyHttpClient(TimeSpan.FromSeconds(120));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Add("Authorization", "Bearer " + key);
            req.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("OpenAI {Status}: {Body}", resp.StatusCode, body);
                return Json(new { error = $"OpenAI: HTTP {(int)resp.StatusCode}" });
            }

            using var doc = JsonDocument.Parse(body);
            var antwort = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return Json(new { antwort });
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "OpenAI-Chat: Netzwerk-/Proxy-Fehler");
            return Json(new { error = "Netzwerk-/Proxy-Fehler: " + ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OpenAI-Chat fehlgeschlagen");
            return Json(new { error = "Fehler: " + ex.Message });
        }
    }

    // ── KI-URL-Analyse (Webseite laden + an OpenAI) ────────────────────
    [HttpPost]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> KiUrl(string url, string aktion, string? extra)
    {
        if (string.IsNullOrWhiteSpace(url)) return Json(new { error = "Keine URL." });
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Json(new { error = "URL muss mit http:// oder https:// beginnen." });

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config["OpenAiApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            return Json(new { error = "OpenAI ist nicht konfiguriert (kein API-Key)." });

        try
        {
            // 1. Webseite laden
            using var client = BuildProxyHttpClient(TimeSpan.FromSeconds(20));
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; MBToolsIntern/1.0)");
            var html = await client.GetStringAsync(url);

            // 2. Titel extrahieren
            var titelMatch = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            var titel = titelMatch.Success ? System.Net.WebUtility.HtmlDecode(titelMatch.Groups[1].Value).Trim() : "";

            // 3. HTML zu Klartext
            var text = System.Text.RegularExpressions.Regex.Replace(html,
                @"<(script|style|noscript|svg)[^>]*>[\s\S]*?</\1>", " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 18000) text = text[..18000] + "\n... (gekürzt)";
            var zeichenCount = text.Length;

            if (zeichenCount < 100)
                return Json(new { error = "Konnte aus der Webseite kaum Text extrahieren — vielleicht ist es eine Login-Seite oder eine geschützte Ressource?" });

            // 4. Prompt je Aktion
            var aufgabe = (aktion ?? "zusammenfassen").ToLowerInvariant();
            string anweisung = aufgabe switch
            {
                "erklaeren" => "Erkläre den Inhalt dieser Webseite einfach und verständlich für jemand mit wenig Vorwissen. Strukturiere die Erklärung mit Markdown-Überschriften und Stichpunkten.",
                "uebersetzen" => $"Übersetze den Inhalt dieser Webseite ins {(string.IsNullOrWhiteSpace(extra) ? "Englische" : extra)}. Erhalte die Struktur (Überschriften, Absätze).",
                "frage" => $"Beantworte folgende Frage anhand des Webseiten-Inhalts. Wenn die Antwort nicht im Text steht, sage das ehrlich.\n\nFrage: {extra}",
                _ => "Fasse den Inhalt dieser Webseite kompakt zusammen — Hauptaussagen, Kernargumente, ggf. Kennzahlen. Verwende Markdown mit Stichpunkten."
            };

            var system = "Du bist ein hilfreicher Assistent von MerkurTools (Merkur Privatbank). " +
                         "Antworte praezise auf Deutsch (sofern nicht ausdruecklich uebersetzt werden soll). " +
                         "Verwende Markdown.";

            var requestBody = new
            {
                model = "gpt-4o-mini",
                temperature = 0.3,
                max_tokens = 2500,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user",   content = $"{anweisung}\n\n--- Webseiten-Inhalt (Quelle: {url}) ---\n{text}" }
                }
            };

            using var aiClient = BuildProxyHttpClient(TimeSpan.FromSeconds(120));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Add("Authorization", "Bearer " + key);
            req.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var resp = await aiClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("OpenAI {Status}: {Body}", resp.StatusCode, body);
                return Json(new { error = $"OpenAI: HTTP {(int)resp.StatusCode}" });
            }

            using var doc = JsonDocument.Parse(body);
            var antwort = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return Json(new { antwort, titel, zeichen = zeichenCount });
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "URL-Analyse: Netzwerk-/Proxy-Fehler");
            return Json(new { error = "Webseite/Proxy nicht erreichbar: " + ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "URL-Analyse fehlgeschlagen");
            return Json(new { error = "Fehler: " + ex.Message });
        }
    }
}
