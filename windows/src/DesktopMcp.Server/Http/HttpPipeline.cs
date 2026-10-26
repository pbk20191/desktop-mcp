using DesktopMcp.Core.Config;
using DesktopMcp.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace DesktopMcp.Server.Http;

/// <summary>
/// The request checks in front of the MCP endpoints, in upstream's order (spec §8 HTTP pipeline; Decisions 9, 10, 13,
/// 15, 16, 17): (1) OPTIONS, (2) trusted host, (3) Origin, (4) CORS response headers, (5) IP allowlist (not for
/// <c>/health</c>), (6) auth (not for the public paths; only with an auth key or an OAuth token validator). Each
/// rejection is JSON (<c>application/json</c>, <c>X-Content-Type-Options: nosniff</c>), except the host check's plain text.
/// </summary>
internal static class HttpPipeline
{
    private const string HealthPath = "/health";
    private const string Json = "application/json";
    private const string BearerPrefix = "Bearer ";

    /// <summary>Adds the checks to <paramref name="app"/>; call it before the endpoints are mapped.</summary>
    internal static void Use(IApplicationBuilder app, ServePlan plan, Func<string, bool>? validateOAuthToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(plan);
        var corsOrigins = plan.CorsOrigins;
        var allowedHosts = plan.AllowedHosts;
        var allowlist = plan.IpAllowlist;
        var authKey = plan.AuthKey;

        // (1) Every OPTIONS request is answered here with 200 and no body (__main__.py:143-176).
        app.Use((context, next) =>
        {
            if (!HttpMethods.IsOptions(context.Request.Method)) return next(context);
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentLength = 0;
            if (ListedOrigin(context, corsOrigins) is { } origin)
            {
                response.Headers.AccessControlAllowOrigin = origin;
                response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                response.Headers.AccessControlAllowHeaders = "content-type, authorization, mcp-session-id";
                response.Headers.Append(HeaderNames.Vary, "Origin");
            }
            return Task.CompletedTask;
        });

        // (2) DNS rebinding: Starlette's TrustedHostMiddleware answer.
        if (allowedHosts is not null)
        {
            app.Use((context, next) =>
            {
                if (HttpGuards.IsTrustedHost(context.Request.Host.Host, allowedHosts)) return next(context);
                var response = context.Response;
                response.StatusCode = StatusCodes.Status400BadRequest;
                response.ContentType = "text/plain; charset=utf-8";
                response.Headers.XContentTypeOptions = "nosniff";
                return response.WriteAsync("Invalid host header");
            });
        }

        // (3) Cross-site browser requests; a request without Origin passes.
        app.Use((context, next) =>
        {
            var origin = Origin(context);
            return HttpGuards.IsAllowedOrigin(origin, corsOrigins)
                ? next(context)
                : WriteErrorAsync(context, StatusCodes.Status403Forbidden, $"Forbidden: origin '{origin}' is not allowed");
        });

        // (4) Starlette CORSMiddleware's simple-response headers for a listed origin; no credentials, no exposed headers.
        if (corsOrigins.Count > 0)
        {
            app.Use((context, next) =>
            {
                if (ListedOrigin(context, corsOrigins) is { } origin)
                {
                    context.Response.Headers.AccessControlAllowOrigin = origin;
                    context.Response.Headers.Append(HeaderNames.Vary, "Origin");
                }
                return next(context);
            });
        }

        // (5) The client address as Kestrel saw it; forwarding headers are never read.
        if (allowlist.Count > 0)
        {
            app.Use((context, next) =>
            {
                if (context.Request.Path.Value == HealthPath) return next(context);
                return HttpGuards.AllowlistRejection(context.Connection.RemoteIpAddress, allowlist) is { } rejection
                    ? WriteErrorAsync(context, StatusCodes.Status403Forbidden, rejection)
                    : next(context);
            });
        }

        // (6) Bearer auth key, with OAuth access tokens as the fallback (auth.py:23-79).
        if (authKey is not null)
        {
            app.Use((context, next) =>
            {
                if (HttpGuards.IsPublicPath(context.Request.Path.Value ?? "")) return next(context);
                var authorization = Authorization(context);
                if (!authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
                    return WriteUnauthorizedAsync(context, "Missing or invalid Authorization header. Expected: Bearer <token>");
                var token = authorization[BearerPrefix.Length..];
                if (HttpGuards.FixedTimeEquals(token, authKey) || validateOAuthToken?.Invoke(token) == true) return next(context);
                return WriteUnauthorizedAsync(context, "Invalid authentication token");
            });
        }
        else if (validateOAuthToken is not null)
        {
            app.Use((context, next) =>
            {
                if (HttpGuards.IsPublicPath(context.Request.Path.Value ?? "")) return next(context);
                var authorization = Authorization(context);
                return authorization.StartsWith(BearerPrefix, StringComparison.Ordinal) && validateOAuthToken(authorization[BearerPrefix.Length..])
                    ? next(context)
                    : WriteUnauthorizedAsync(context, "Unauthorized");
            });
        }
    }

    /// <summary><c>GET /health</c>: 200 <c>{"status":"ok"}</c> (Decisions 17).</summary>
    internal static Task Health(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = Json;
        response.Headers.XContentTypeOptions = "nosniff";
        return response.WriteAsync(SecurityJson.Health);
    }

    /// <summary>The first <c>Origin</c> header, or null when there is none.</summary>
    private static string? Origin(HttpContext context)
    {
        var values = context.Request.Headers.Origin;
        return values.Count == 0 ? null : values[0];
    }

    /// <summary>The first <c>Authorization</c> header, or the empty string (Starlette's <c>headers.get(..., "")</c>).</summary>
    private static string Authorization(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        return values.Count == 0 ? "" : values[0] ?? "";
    }

    /// <summary>The request's Origin when it is listed in <paramref name="corsOrigins"/>; the CORS headers echo its own spelling.</summary>
    private static string? ListedOrigin(HttpContext context, IReadOnlyList<string> corsOrigins)
    {
        var origin = Origin(context);
        return HttpGuards.IsListedOrigin(origin, corsOrigins) ? origin : null;
    }

    private static Task WriteUnauthorizedAsync(HttpContext context, string error)
    {
        // Every 401 names the scheme (Decisions 13; RFC 6750), the OAuth-only one too.
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return WriteErrorAsync(context, StatusCodes.Status401Unauthorized, error);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string error)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = Json;
        // The body may echo request text (an Origin), so it must never be sniffed as HTML.
        response.Headers.XContentTypeOptions = "nosniff";
        return response.WriteAsync(SecurityJson.Error(error));
    }
}
