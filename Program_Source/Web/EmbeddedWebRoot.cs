// ============================================================================
// INT VoiceToText — embedded wwwroot fallback (single-file exe without disk folder)
// ============================================================================

using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace INTVoiceToText.Web;

/// <summary>
/// Serves the React app from assembly-embedded resources when no physical
/// wwwroot folder exists next to the executable (true single-file scenario).
/// Physical files always win — this middleware only patches 404s for GET requests.
/// </summary>
public static class EmbeddedWebRoot
{
    private static readonly Assembly Asm = Assembly.GetExecutingAssembly();

    // resource-name suffix → MIME type, resolved lazily once
    /// <summary>Diagnostic helper (used by /api/debug/serve).</summary>
    public static string? FindResource(string path) => TryGetResourceSuffix(path);

    private static string? TryGetResourceSuffix(string path)
    {
        if (string.IsNullOrEmpty(path)) path = "index.html";
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(normalized)) normalized = "index.html"; // root "/" → SPA entry
        var suffix = "." + normalized.Replace('/', '.'); // e.g. .wwwroot.assets.index-x.js
        foreach (var name in Asm.GetManifestResourceNames())
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name;
        return null;
    }

    public static string MimeType(string path)
    {
        var p = path.TrimEnd('/').ToLowerInvariant();
        if (p.EndsWith(".html")) return "text/html; charset=utf-8";
        if (p.EndsWith(".js") || p.EndsWith(".mjs")) return "application/javascript; charset=utf-8";
        if (p.EndsWith(".css")) return "text/css; charset=utf-8";
        if (p.EndsWith(".svg")) return "image/svg+xml";
        if (p.EndsWith(".png") || p.EndsWith(".jpg") || p.EndsWith(".jpeg") || p.EndsWith(".gif") || p.EndsWith(".ico")) return "image/*";
        if (p.EndsWith(".json")) return "application/json; charset=utf-8";
        if (p.EndsWith(".woff2")) return "font/woff2";
        return "application/octet-stream";
    }

    public static async Task<bool> TryServeAsync(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Get) return false;
        var raw = ctx.Request.Path.Value ?? "/";

        // Never hijack API / hub routes.
        if (raw.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (raw.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)) return false;

        // SPA fallback: a missing non-file route serves index.html.
        var candidates = new List<string> { raw };
        if (!raw.Contains('.')) candidates.Add("/");

        foreach (var candidate in candidates)
        {
            string? resName = TryGetResourceSuffix(candidate);
            if (resName == null) continue;

            using var stream = Asm.GetManifestResourceStream(resName);
            if (stream == null) continue;

            ctx.Response.StatusCode = 200; // terminal "not found" set 404 — override before writing
            ctx.Response.ContentType = MimeType(candidate);
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            return true;
        }

        return false;
    }
}
