using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Rta.App.Node;
using Rta.Core;
using Rta.Core.Calibration;

namespace Rta.App.Security;

public sealed record PasswordRecord(string Salt, string Hash, int Iterations);

/// <summary>
/// One admin password per machine (PBKDF2-SHA256). Viewing is open; anything that changes
/// settings, calibration, services, alerts or nodes needs the admin cookie. Until a password is
/// set, changes are allowed only from this PC (loopback).
/// </summary>
public sealed class AdminAuth
{
    private const int Iterations = 210_000;
    private readonly string _file;
    private readonly object _lock = new();
    private PasswordRecord? _record;
    private int _failures;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public AdminAuth(NodePaths paths)
    {
        _file = Path.Combine(paths.DataDir, "admin.json");
        _record = SafeFile.ReadJson<PasswordRecord>(_file, CalibrationStore.Json);
    }

    public bool PasswordSet { get { lock (_lock) return _record is not null; } }

    public void SetPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var rec = new PasswordRecord(Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
        lock (_lock)
        {
            SafeFile.WriteJson(_file, rec, CalibrationStore.Json);
            _record = rec;
        }
    }

    /// <summary>Checks a password, with a short lockout after repeated failures.</summary>
    public bool Verify(string? password, out string? error)
    {
        error = null;
        lock (_lock)
        {
            if (_record is null) { error = "No admin password has been set yet."; return false; }
            if (DateTimeOffset.UtcNow < _lockedUntil) { error = "Too many attempts. Try again in a minute."; return false; }
            var hash = Rfc2898DeriveBytes.Pbkdf2(password ?? "", Convert.FromBase64String(_record.Salt), _record.Iterations, HashAlgorithmName.SHA256, 32);
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(_record.Hash)))
            {
                _failures = 0;
                return true;
            }
            if (++_failures >= 5) { _lockedUntil = DateTimeOffset.UtcNow.AddMinutes(1); _failures = 0; }
            error = "Wrong password.";
            return false;
        }
    }

    public static bool IsLoopback(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    /// <summary>True if this request may change things.</summary>
    public bool IsAdmin(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true || (!PasswordSet && IsLoopback(ctx));

    public static Task SignInAsync(HttpContext ctx) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });
}

public static class AdminAuthExtensions
{
    /// <summary>Requires admin for every non-GET /api call except login/setup.</summary>
    public static IApplicationBuilder UseAdminGuard(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        var mutating = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method);
        if (mutating && path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth"))
        {
            var auth = ctx.RequestServices.GetRequiredService<AdminAuth>();
            if (!auth.IsAdmin(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    title = "Admin sign-in required",
                    detail = auth.PasswordSet
                        ? "Sign in with the admin password to make changes."
                        : "Set an admin password on this PC first (open the app on the PC itself), then sign in.",
                    passwordSet = auth.PasswordSet,
                });
                return;
            }
        }
        await next();
    });

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");
        g.MapGet("/status", (HttpContext ctx, AdminAuth auth) => new
        {
            passwordSet = auth.PasswordSet,
            signedIn = ctx.User.Identity?.IsAuthenticated == true,
            canEdit = auth.IsAdmin(ctx),
            local = AdminAuth.IsLoopback(ctx),
        });
        g.MapPost("/setup", async (PasswordBody body, HttpContext ctx, AdminAuth auth) =>
        {
            if (auth.PasswordSet) return Results.Problem("A password is already set. Sign in and use Change password.", statusCode: 409);
            if (!AdminAuth.IsLoopback(ctx)) return Results.Problem("The first password must be set on the PC running the app.", statusCode: 403);
            try { auth.SetPassword(body.Password); } catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            await AdminAuth.SignInAsync(ctx);
            return Results.Ok();
        });
        g.MapPost("/login", async (PasswordBody body, HttpContext ctx, AdminAuth auth) =>
        {
            if (!auth.Verify(body.Password, out var error))
            {
                await Task.Delay(400);
                return Results.Problem(error, statusCode: 401);
            }
            await AdminAuth.SignInAsync(ctx);
            return Results.Ok();
        });
        g.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
        g.MapPost("/change", (ChangePasswordBody body, HttpContext ctx, AdminAuth auth) =>
        {
            if (!auth.IsAdmin(ctx)) return Results.Problem("Sign in first.", statusCode: 401);
            if (auth.PasswordSet && !auth.Verify(body.Current, out var error)) return Results.Problem(error, statusCode: 401);
            try { auth.SetPassword(body.Password); } catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            return Results.Ok();
        });
    }
}

public sealed record PasswordBody(string Password);
public sealed record ChangePasswordBody(string? Current, string Password);
