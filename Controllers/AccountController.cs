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
                using var user = UserPrincipal.FindByIdentity(ctx, model.Benutzername);
                if (user != null)
                {
                    anzeigeName = user.DisplayName ?? user.Name ?? model.Benutzername;
                    email = user.EmailAddress ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "User-Info konnte nicht gelesen werden");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, model.Benutzername),
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
        catch (Exception ex)
        {
            _log.LogError(ex, "AD-Anmeldung fehlgeschlagen");
            var details = ex.GetBaseException().Message;
            ModelState.AddModelError(string.Empty, $"Verzeichnisdienst ist aktuell nicht erreichbar: {details}");
            return View(model);
        }
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
