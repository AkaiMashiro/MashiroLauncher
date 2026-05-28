namespace Launcher.App.Services;

public enum MojangStatus { Unknown, Operational, Degraded, Down }

public sealed class MojangStatusService(HttpClient http)
{
    private static readonly Uri[] Endpoints =
    [
        new("https://api.minecraftservices.com/minecraft/profile/lookup/name/Notch"),
        new("https://sessionserver.mojang.com/blockedservers"),
    ];

    public async Task<MojangStatus> CheckAsync(CancellationToken ct = default)
    {
        var probes = Endpoints.Select(async url =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return (int)resp.StatusCode < 500;
            }
            catch { return false; }
        });
        var results = await Task.WhenAll(probes);
        var up = results.Count(r => r);
        return up switch
        {
            2 => MojangStatus.Operational,
            1 => MojangStatus.Degraded,
            _ => MojangStatus.Down,
        };
    }
}
