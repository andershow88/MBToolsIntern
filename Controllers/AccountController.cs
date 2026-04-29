using System.ComponentModel.DataAnnotations;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MBToolsIntern.Controllers;

[AllowAnonymous]
[SupportedOSPlatform("windows")]
public class AccountController : Controller
{
    private readonly IConfiguration _config;
    private readonly ILogger<AccountController> _log;

    public AccountController(IConfiguration config, ILogger<AccountController> log)
    {
        _config = config;
        _log = log;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var domain = Environment.GetEnvironmentVariable("AD_DOMAIN")
                     ?? _config["Auth:Domain"]
                     ?? "idfp.rz.bankenit.de";

        // Dev-Bypass für lokales Testen ohne AD-Anbindung
        var bypass = string.Equals(Environment.GetEnvironmentVariable("AUTH_BYPASS"), "true", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(_config["Auth:DevBypass"], "true", StringComparison.OrdinalIgnoreCase);
        if (bypass)
        {
            return await LoginAbschliessen(model, model.Benutzername, model.Benutzername, string.Empty);
        }

        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain, domain);
            var ok = ctx.ValidateCredentials(model.Benutzername, model.Passwort);

            if (!ok)
            {
                ModelState.AddModelError(string.Empty, "Anmeldung fehlgeschlagen — Benutzername oder Passwort falsch.");
                return View(model);
            }

            string anzeigeName = model.Benutzername;
            string email = string.Empty;
            try
            {
                var info = SucheADUser(ctx, domain, model.Benutzername);
                if (info != null)
                {
                    var vollName = $"{info.Value.Vorname} {info.Value.Nachname}".Trim();
                    if (!string.IsNullOrWhiteSpace(vollName)) anzeigeName = vollName;
                    else if (!string.IsNullOrWhiteSpace(info.Value.DisplayName)) anzeigeName = info.Value.DisplayName;
                    else if (!string.IsNullOrWhiteSpace(info.Value.Name)) anzeigeName = info.Value.Name;
                    email = info.Value.Mail;
                }
                else
                {
                    _log.LogWarning("AD-User '{User}' wurde nicht gefunden — User-ID bleibt als Anzeigename", model.Benutzername);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "User-Info konnte nicht gelesen werden");
            }

            return await LoginAbschliessen(model, model.Benutzername, anzeigeName, email);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AD-Anmeldung fehlgeschlagen");
            var details = ex.GetBaseException().Message;
            ModelState.AddModelError(string.Empty, $"Verzeichnisdienst ist aktuell nicht erreichbar: {details}");
            return View(model);
        }
    }

    private record struct ADInfo(string Vorname, string Nachname, string DisplayName, string Name, string Mail);

    private ADInfo? SucheADUser(PrincipalContext primaryCtx, string primaryDomain, string benutzer)
    {
        // Mehrere Schreibweisen
        var sam = benutzer.Contains('\\') ? benutzer.Split('\\').Last() : benutzer;
        sam = sam.Contains('@') ? sam.Split('@')[0] : sam;
        var versuche = new[] { benutzer, sam }.Distinct().ToArray();
        var idTypes = new[] { IdentityType.SamAccountName, IdentityType.UserPrincipalName, IdentityType.Name };

        // 1) primary context
        foreach (var v in versuche)
            foreach (var t in idTypes)
                try
                {
                    using var u = UserPrincipal.FindByIdentity(primaryCtx, t, v);
                    if (u != null) { _log.LogInformation("AD via primary {T}/{V}", t, v); return MapUser(u); }
                }
                catch { }

        // 2) lokale Server-Domain (falls anders)
        var lokal = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        if (!string.IsNullOrWhiteSpace(lokal) && !string.Equals(lokal, primaryDomain, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var v in versuche)
                foreach (var t in idTypes)
                    try
                    {
                        using var c = new PrincipalContext(ContextType.Domain, lokal);
                        using var u = UserPrincipal.FindByIdentity(c, t, v);
                        if (u != null) { _log.LogInformation("AD via lokale Domain {D} {T}/{V}", lokal, t, v); return MapUser(u); }
                    }
                    catch { }
        }

        // 3) Global-Catalog-Search (durchsucht den ganzen Forest)
        foreach (var d in new[] { primaryDomain, lokal }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            try
            {
                var sf = LdapEscape(sam);
                using var root = new DirectoryEntry($"GC://{d}");
                using var ds = new DirectorySearcher(root)
                {
                    Filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={sf}))",
                    SearchScope = SearchScope.Subtree
                };
                ds.PropertiesToLoad.AddRange(new[] { "givenName", "sn", "displayName", "name", "mail" });
                var r = ds.FindOne();
                if (r != null)
                {
                    _log.LogInformation("AD via Global-Catalog {D}", d);
                    var given = r.Properties["givenName"].Cast<object>().FirstOrDefault()?.ToString() ?? "";
                    var sn = r.Properties["sn"].Cast<object>().FirstOrDefault()?.ToString() ?? "";
                    var dn = r.Properties["displayName"].Cast<object>().FirstOrDefault()?.ToString() ?? "";
                    var nm = r.Properties["name"].Cast<object>().FirstOrDefault()?.ToString() ?? "";
                    var ml = r.Properties["mail"].Cast<object>().FirstOrDefault()?.ToString() ?? "";
                    _log.LogInformation("GC-Felder: GivenName='{G}', Surname='{S}', DisplayName='{DN}', Name='{N}', Mail='{M}'", given, sn, dn, nm, ml);
                    return new ADInfo(given.Trim(), sn.Trim(), dn.Trim(), nm.Trim(), ml.Trim());
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "GC-Search auf {D} fehlgeschlagen", d);
            }
        }

        return null;
    }

    private ADInfo MapUser(UserPrincipal u)
    {
        var v = (u.GivenName ?? "").Trim();
        var n = (u.Surname ?? "").Trim();
        var dn = (u.DisplayName ?? "").Trim();
        var nm = (u.Name ?? "").Trim();
        var m = u.EmailAddress ?? "";
        _log.LogInformation("AD-Felder: GivenName='{G}', Surname='{S}', DisplayName='{DN}', Name='{N}', Mail='{M}'", v, n, dn, nm, m);
        return new ADInfo(v, n, dn, nm, m);
    }

    private static string LdapEscape(string s)
        => s.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    private async Task<IActionResult> LoginAbschliessen(LoginViewModel model, string userId, string anzeigeName, string email)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userId),
            new("DisplayName", anzeigeName),
            new(ClaimTypes.Email, email)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}

public class LoginViewModel
{
    [Required(ErrorMessage = "Bitte Benutzernamen eingeben.")]
    [Display(Name = "Benutzername")]
    public string Benutzername { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bitte Passwort eingeben.")]
    [DataType(DataType.Password)]
    [Display(Name = "Passwort")]
    public string Passwort { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
