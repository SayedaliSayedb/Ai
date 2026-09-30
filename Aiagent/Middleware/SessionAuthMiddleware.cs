using Aiagent.Services.Auth;

namespace Aiagent.Middleware;

public class SessionAuthMiddleware
{
    private readonly RequestDelegate _next;
    private const string CookieName = "aiagent_session";

    // مسیرهای عمومی که نیازی به لاگین ندارند
    private static readonly string[] PublicPaths =
    {
        "/login", "/register", "/logout",
        "/css", "/js", "/lib", "/favicon",
        "/_framework", "/_blazor", "/chatHub"
    };

    public SessionAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAuthService auth)
    {
        var path = context.Request.Path.Value ?? "/";

        if (PublicPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var token = context.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(token))
        {
            var session = await auth.ValidateTokenAsync(token);
            if (session != null)
            {
                context.Items["PhoneNumber"] = session.PhoneNumber;
                context.Items["SessionToken"] = session.Token;
                await _next(context);
                return;
            }
        }

        // اگر API است، 401 برگردان
        if (path.StartsWith("/api/"))
        {
            context.Response.StatusCode = 401;
            return;
        }

        context.Response.Redirect("/login");
    }
}