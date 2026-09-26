using System.Net.Http.Headers;
using System.Text.Json;

namespace INTVoiceToText.Services;

public sealed record AvailableUpdate(string Version, string ReleaseUrl, string AssetUrl, string AssetName);

public sealed class UpdateService
{
    public const string Repository = "intenso2891/INT_Voice_To_Text";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
        await using var input = await Http.GetStreamAsync(update.AssetUrl, ct);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, ct);
        return path;
    }
}
