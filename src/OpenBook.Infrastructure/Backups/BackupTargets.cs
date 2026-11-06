using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Infrastructure.Backups;

/// <summary>Copies backups into a folder (a network share, a synced cloud folder, an attached drive).</summary>
public sealed class FolderBackupTarget : IBackupTarget
{
    public BackupDestination Destination => BackupDestination.Folder;

    public async Task UploadAsync(BackupSchedule schedule, string? secretKey, string localPath, string name, CancellationToken ct)
    {
        var folder = Folder(schedule);
        Directory.CreateDirectory(folder);
        // Write under a temporary name, then rename, so a half-copied file never looks like a backup.
        var partial = Path.Combine(folder, name + ".partial");
        ClearStalePartials(folder);
        try
        {
            await using (var source = File.OpenRead(localPath))
            await using (var target = File.Create(partial))
                await source.CopyToAsync(target, ct);
            File.Move(partial, Path.Combine(folder, name), overwrite: true);
        }
        finally
        {
            // A copy that failed (a full drive) mustn't keep the room it took: each run has a new name.
            File.Delete(partial);
        }
    }

    /// <summary>What a run stopped mid-copy left; a day old, so a backup another copy of OpenBook is writing here is left alone.</summary>
    private static void ClearStalePartials(string folder)
    {
        foreach (var old in Directory.EnumerateFiles(folder, "openbook-scheduled-*.partial"))
        {
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                File.Delete(old);
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(BackupSchedule schedule, string? secretKey, string namePrefix, CancellationToken ct)
    {
        var folder = Folder(schedule);
        IReadOnlyList<string> names = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, namePrefix + "*.openbook.db").Select(Path.GetFileName).OfType<string>().ToList()
            : [];
        return Task.FromResult(names);
    }

    public Task DeleteAsync(BackupSchedule schedule, string? secretKey, string name, CancellationToken ct)
    {
        File.Delete(Path.Combine(Folder(schedule), Path.GetFileName(name)));
        return Task.CompletedTask;
    }

    private static string Folder(BackupSchedule schedule) =>
        schedule.FolderPath is { } path && Path.IsPathFullyQualified(path)
            ? path
            : throw new DomainException("backup.folder_invalid", "Enter the full path of the backup folder (for example D:\\Backups or /mnt/backups).");
}

/// <summary>
/// S3-compatible object storage (Amazon S3, Backblaze B2, Cloudflare R2, Wasabi, MinIO) with path-style requests
/// (https://endpoint/bucket/key), signed with AWS Signature Version 4. Uploads are single PUTs (the company file is
/// far under the 5 GB limit).
/// </summary>
public sealed class S3BackupTarget : IBackupTarget, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<DateTimeOffset> _now;

    public S3BackupTarget()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>For tests: a handler standing in for the storage service, and a fixed clock.</summary>
    public S3BackupTarget(HttpMessageHandler handler, Func<DateTimeOffset> now)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _now = now;
    }

    public BackupDestination Destination => BackupDestination.S3;

    public async Task UploadAsync(BackupSchedule schedule, string? secretKey, string localPath, string name, CancellationToken ct)
    {
        string hash;
        await using (var stream = File.OpenRead(localPath))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        await using var body = File.OpenRead(localPath);
        using var request = Request(schedule, secretKey, HttpMethod.Put, Key(schedule, name), new Dictionary<string, string>(), hash, new StreamContent(body));
        request.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        await SendAsync(request, ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(BackupSchedule schedule, string? secretKey, string namePrefix, CancellationToken ct)
    {
        var names = new List<string>();
        var prefix = Key(schedule, namePrefix);
        string? token = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2", ["prefix"] = prefix };
            if (token is not null)
                query["continuation-token"] = token;
            using var request = Request(schedule, secretKey, HttpMethod.Get, "", query, AwsSignatureV4.EmptyHash, null);
            var xml = XDocument.Parse(await SendAsync(request, ct));
            names.AddRange(xml.Descendants().Where(e => e.Name.LocalName == "Key").Select(e => e.Value[(prefix.Length - namePrefix.Length)..]));
            token = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value;
        }
        while (token is not null);
        return names;
    }

    public async Task DeleteAsync(BackupSchedule schedule, string? secretKey, string name, CancellationToken ct)
    {
        using var request = Request(schedule, secretKey, HttpMethod.Delete, Key(schedule, name), new Dictionary<string, string>(), AwsSignatureV4.EmptyHash, null);
        await SendAsync(request, ct);
    }

    public void Dispose() => _client.Dispose();

    private static string Key(BackupSchedule schedule, string name) =>
        schedule.Prefix is { Length: > 0 } prefix ? (prefix.EndsWith('/') ? prefix : prefix + "/") + name : name;

    private HttpRequestMessage Request(
        BackupSchedule schedule, string? secretKey, HttpMethod method, string key, IDictionary<string, string> query, string payloadHash, HttpContent? content)
    {
        if (schedule.ServiceUrl is null || schedule.Bucket is null || schedule.AccessKeyId is null || secretKey is null)
            throw new DomainException("backup.s3_incomplete", "The storage service URL, bucket, access key ID and secret key are all needed.");
        var endpoint = new Uri(schedule.ServiceUrl);
        var path = "/" + Uri.EscapeDataString(schedule.Bucket) + (key.Length == 0 ? "" : "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString)));
        var queryString = AwsSignatureV4.CanonicalQuery(query);
        var uri = new UriBuilder(endpoint) { Path = path, Query = queryString }.Uri;
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        var now = _now();
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
        };
        var authorization = AwsSignatureV4.Authorization(method.Method, path, queryString, headers, payloadHash, schedule.AccessKeyId, secretKey,
            schedule.Region ?? "us-east-1", "s3", now);

        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return body;
        var message = body.Contains("<Message>", StringComparison.Ordinal)
            ? XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value
            : null;
        throw new HttpRequestException($"Storage service refused the request: HTTP {(int)response.StatusCode} {message ?? response.ReasonPhrase}".Trim(), null, response.StatusCode);
    }
}

/// <summary>AWS Signature Version 4 for a request whose headers are already chosen (see the AWS "Authenticating Requests" examples).</summary>
public static class AwsSignatureV4
{
    /// <summary>SHA-256 of an empty body.</summary>
    public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <param name="canonicalPath">The URI-encoded path, e.g. "/bucket/key".</param>
    /// <param name="headers">Lower-case names; must include host and x-amz-date.</param>
    public static string Authorization(
        string method, string canonicalPath, string canonicalQuery, IReadOnlyDictionary<string, string> headers, string payloadHash,
        string accessKeyId, string secretKey, string region, string service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var sorted = headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        var signedHeaders = string.Join(';', sorted.Select(h => h.Key));
        var canonicalRequest = string.Join('\n',
            method, canonicalPath, canonicalQuery,
            string.Concat(sorted.Select(h => $"{h.Key}:{h.Value.Trim()}\n")),
            signedHeaders, payloadHash);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    /// <summary>Parameters sorted by name, names and values encoded the way AWS expects (RFC 3986 unreserved characters kept).</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join('&', query.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => $"{Encode(q.Key)}={Encode(q.Value)}"));

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
