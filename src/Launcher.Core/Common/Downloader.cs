namespace Launcher.Core.Common;

public class HashMismatchException(string url, string expected, string actual)
    : Exception($"SHA-1 mismatch for {url}: expected {expected}, got {actual}")
{
    public string Url { get; } = url;
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}

public sealed class Downloader(HttpClient http)
{
    public async Task<string> FetchTextAsync(Uri url, CancellationToken ct = default)
    {
        return await http.GetStringAsync(url, ct);
    }

    public async Task<byte[]> FetchBytesAsync(Uri url, CancellationToken ct = default)
    {
        return await http.GetByteArrayAsync(url, ct);
    }

    public async Task DownloadToFileAsync(
        Uri url,
        string destPath,
        string? expectedSha1 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (expectedSha1 is not null && File.Exists(destPath) && Sha1.VerifyFile(destPath, expectedSha1))
            return;

        var temp = destPath + ".tmp";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(temp);
            var buf = new byte[81920];
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                total += read;
                progress?.Report(new DownloadProgress(total, contentLength));
            }
        }

        if (expectedSha1 is not null)
        {
            var actual = Sha1.ComputeFile(temp);
            if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new HashMismatchException(url.ToString(), expectedSha1, actual);
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(temp, destPath);
    }
}

public readonly record struct DownloadProgress(long BytesRead, long? TotalBytes);
