using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PingMeter.Update;

internal abstract record UpdateResult
{
    public sealed record UpdateAvailable(Version Latest, string Url) : UpdateResult;

    public sealed record UpToDate(Version Current) : UpdateResult;

    public sealed record Failed(string Reason) : UpdateResult;
}

internal static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/jefuriiij/ping-meter/releases/latest";
    public const string ReleasesPage = "https://github.com/jefuriiij/ping-meter/releases";

    // Note: initialized before Http, which reads it for the User-Agent header.
    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PingMeter/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateResult> CheckAsync()
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi);
            if (!response.IsSuccessStatusCode)
                return new UpdateResult.Failed($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}");

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string? tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string url = json.RootElement.TryGetProperty("html_url", out var u)
                ? u.GetString() ?? ReleasesPage
                : ReleasesPage;

            if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return new UpdateResult.Failed($"Unrecognized release tag '{tag}'");

            return Normalize(latest) > CurrentVersion
                ? new UpdateResult.UpdateAvailable(Normalize(latest), url)
                : new UpdateResult.UpToDate(CurrentVersion);
        }
        catch (Exception ex)
        {
            return new UpdateResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Compare on exactly Major.Minor.Build: assembly versions carry a 4th component and
    /// tags may omit the 3rd, and Version treats a missing part as -1 (so 0.2 &lt; 0.2.0).
    /// </summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
