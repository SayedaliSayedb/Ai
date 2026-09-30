using Aiagent.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Aiagent.Controllers;

public class AuthController : Controller
{
    private const string CookieName = "aiagent_session";

    private readonly IAuthService _auth;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, ILogger<AuthController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    [HttpGet("/login")]
    public IActionResult Login() => View();

    [HttpPost("/login")]
    public async Task<IActionResult> LoginPost(
        [FromForm] string phoneNumber,
        [FromForm] string password,
        [FromForm] bool rememberMe = true)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();

        var (success, token, error) = await _auth.LoginAsync(phoneNumber, password, ip, ua);

        if (!success || token == null)
        {
            ViewBag.Error = error ?? "خطا در ورود";
            return View("Login");
        }

        Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = rememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(1)
        });

        _logger.LogInformation("کاربر {Phone} وارد شد", phoneNumber);
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet("/register")]
    public IActionResult Register() => View();

    [HttpPost("/register")]
    public async Task<IActionResult> RegisterPost(
        [FromForm] string phoneNumber,
        [FromForm] string password,
        [FromForm] string? displayName)
    {
        if (password.Length < 6)
        {
            ViewBag.Error = "رمز عبور باید حداقل ۶ کاراکتر باشد.";
            return View("Register");
        }

        var ok = await _auth.RegisterAsync(phoneNumber, password, displayName);
        if (!ok)
        {
            ViewBag.Error = "این شماره قبلاً ثبت شده یا نامعتبر است.";
            return View("Register");
        }

        return RedirectToAction("Login");
    }

    [HttpGet("/logout")]
    public async Task<IActionResult> Logout()
    {
        var token = Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(token))
            await _auth.LogoutAsync(token);

        Response.Cookies.Delete(CookieName);
        return RedirectToAction("Login");
    }
}