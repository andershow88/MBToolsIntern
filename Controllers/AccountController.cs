using System.ComponentModel.DataAnnotations;
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
                UserPrincipal? user = null;
                var versuche = new[]
                {
                    model.Benutzername,
                    model.Benutzername.Contains('\\') ? model.Benutzername.Split('\\').Last() : model.Benutzername,
                    model.Benutzername.Contains('@') ? model.Benutzername.Split('@')[0] : model.Benutzername,
                };
                var idTypes = new[] { IdentityType.SamAccountName, IdentityType.UserPrincipalName, IdentityType.Name };

                foreach (var v in versuche.Distinct())
                {
                    foreach (var t in idTypes)
                    {
                        try
                        {
                            user = UserPrincipal.FindByIdentity(ctx, t, v);
                            if (user != null) { _log.LogInformation("AD-User gefunden via {Type} mit '{V}'", t, v); break; }
                        }
                        catch (Exception ex) { _log.LogDebug(ex, "FindByIdentity {T}/{V} fehlgeschlagen", t, v); }
                    }
                    if (user != null) break;
                }

                if (user != null)
                {
                    var vorname = (user.GivenName ?? string.Empty).Trim();
                    var nachname = (user.Surname ?? string.Empty).Trim();
                    var displayName = (user.DisplayName ?? string.Empty).Trim();
                    var name = (user.Name ?? string.Empty).Trim();
                    _log.LogInformation("AD-Felder fuer {User}: GivenName='{Given}', Surname='{Sur}', DisplayName='{DN}', Name='{N}', Mail='{M}'",
                        model.Benutzername, vorname, nachname, displayName, name, user.EmailAddress);

                    var vollName = $"{vorname} {nachname}".Trim();
                    if (!string.IsNullOrWhiteSpace(vollName))
                        anzeigeName = vollName;
                    else if (!string.IsNullOrWhiteSpace(displayName))
                        anzeigeName = displayName;
                    else if (!string.IsNullOrWhiteSpace(name))
                        anzeigeName = name;

                    email = user.EmailAddress ?? string.Empty;
                    user.Dispose();
                }
                else
                {
                    _log.LogWarning("AD-User '{User}' wurde mit keinem der Verfahren gefunden — User-ID bleibt als Anzeigename", model.Benutzername);
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
