using System.Net.Http.Headers;
using System.Text.Json;

namespace INTVoiceToText.Services;

public sealed record AvailableUpdate(string Version, string ReleaseUrl, string AssetUrl, string AssetName);
public sealed record UpdateProgress(long Bytes, long TotalBytes, string Phase)
{
    public int Percent => TotalBytes > 0 ? Math.Clamp((int)(Bytes * 100L / TotalBytes), 0, 100) : 0;
}

public sealed class UpdateService
{
    public const string Repository = "intenso2891/INT_Voice_To_Text";
    private static readonly HttpClient Http = CreateClient();
    public event Action<UpdateProgress>? ProgressChanged;

    private static HttpClient CreateClient()
    {
        // 10-minute timeout for large ZIP downloads on slow connections.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("INT-VoiceToText", AppState.AppVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out var remote) || !Version.TryParse(AppState.AppVersion, out var current) || remote <= current) return null;
        var assets = root.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return new AvailableUpdate(version, root.GetProperty("html_url").GetString() ?? "", asset.GetProperty("browser_download_url").GetString() ?? "", name);
        }
        return null;
    }

    public async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), update.AssetName);
        ProgressChanged?.Invoke(new UpdateProgress(0, 0, "connecting"));

        // Use ResponseHeadersRead to get content-length before downloading the body.
        using var response = await Http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        ProgressChanged?.Invoke(new UpdateProgress(0, totalBytes, "downloading"));

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            ProgressChanged?.Invoke(new UpdateProgress(downloaded, totalBytes, "downloading"));
        }
        await output.FlushAsync(ct).ConfigureAwait(false);
        ProgressChanged?.Invoke(new UpdateProgress(downloaded, totalBytes, "ready"));

        return path;
    }
}
