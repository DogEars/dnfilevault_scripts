using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnFileVault.Sync;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(x => x is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var settings = Settings.Parse(args);
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DNFileVaultDotNetSync/1.0 (+support@deltaneutral.com)"
            );
            var result = await new VaultClient(httpClient, settings).SyncAsync(
                CancellationToken.None
            );
            Console.WriteLine(
                $"Complete via {result.ApiHost}: {result.Listed} listed, "
                    + $"{result.Downloaded} downloaded, {result.Present} already present, "
                    + $"{result.OutsideLookback} outside the lookback."
            );
            return 0;
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Authorization error: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Synchronization failed: {ex.Message}");
            return 4;
        }
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            """
            DNFileVault Sync

              dnfilevault-sync --group GROUP --output DIRECTORY [--days 7]

            Required environment variables:
              DNFV_EMAIL
              DNFV_PASSWORD

            Optional environment defaults:
              DNFV_GROUP
              DNFV_OUT_DIR
              DNFV_DAYS
            """
        );
}

internal sealed record Settings(
    string Email,
    string Password,
    string Group,
    string OutputDirectory,
    int LookbackDays
)
{
    public static Settings Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ConfigurationException($"Unexpected argument: {argument}");
            var separator = argument.IndexOf('=');
            if (separator > 2)
            {
                values[argument[2..separator]] = argument[(separator + 1)..];
                continue;
            }
            if (
                index + 1 >= args.Count
                || args[index + 1].StartsWith("--", StringComparison.Ordinal)
            )
                throw new ConfigurationException($"No value was provided for {argument}.");
            values[argument[2..]] = args[++index];
        }

        var email = Environment.GetEnvironmentVariable("DNFV_EMAIL");
        var password = Environment.GetEnvironmentVariable("DNFV_PASSWORD");
        var group = Get(values, "group", "DNFV_GROUP");
        var output = Get(values, "output", "DNFV_OUT_DIR");
        var daysText = Get(values, "days", "DNFV_DAYS") ?? "7";
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new ConfigurationException("DNFV_EMAIL and DNFV_PASSWORD must be set.");
        if (string.IsNullOrWhiteSpace(group))
            throw new ConfigurationException("--group or DNFV_GROUP is required.");
        if (string.IsNullOrWhiteSpace(output))
            throw new ConfigurationException("--output or DNFV_OUT_DIR is required.");
        if (
            !int.TryParse(daysText, CultureInfo.InvariantCulture, out var days)
            || days is < 1 or > 35
        )
            throw new ConfigurationException("--days must be between 1 and 35.");
        foreach (var key in values.Keys)
        {
            if (key is not ("group" or "output" or "days"))
                throw new ConfigurationException($"Unknown option: --{key}");
        }
        return new Settings(email, password, group, Path.GetFullPath(output), days);
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> values,
        string key,
        string environmentVariable
    ) =>
        values.TryGetValue(key, out var value)
            ? value
            : Environment.GetEnvironmentVariable(environmentVariable);
}

internal sealed class VaultClient(HttpClient httpClient, Settings settings)
{
    private static readonly Uri DiscoveryUri = new("https://config.dnfilevault.com/endpoints.json");
    private static readonly Uri[] FallbackEndpoints =
    [
        new("https://api.dnfilevault.com/"),
        new("https://api-redmint.dnfilevault.com/"),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new FlexibleDateTimeOffsetConverter() },
    };

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var endpoints = await DiscoverAsync(cancellationToken);
        Exception? lastFailure = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                if (await IsHealthyAsync(endpoint, cancellationToken))
                    return await SyncFromAsync(endpoint, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastFailure = ex;
                Console.Error.WriteLine($"{endpoint.Host} failed; trying the next endpoint.");
            }
        }
        throw new HttpRequestException("No API endpoint completed synchronization.", lastFailure);
    }

    private async Task<SyncResult> SyncFromAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var token = await LoginAsync(endpoint, cancellationToken);
        var groups = await GetAsync<GroupEnvelope>(
            new Uri(endpoint, "/groups"),
            token,
            cancellationToken
        );
        var group = groups.Groups.SingleOrDefault(x =>
            x.Name.Equals(settings.Group, StringComparison.OrdinalIgnoreCase)
        );
        if (group is null)
        {
            var names = string.Join(", ", groups.Groups.Select(x => x.Name).Order());
            throw new UnauthorizedAccessException(
                $"Group '{settings.Group}' is unavailable. Authorized groups: {names}."
            );
        }

        var envelope = await GetAsync<FileEnvelope>(
            new Uri(endpoint, $"/groups/{group.Id}/files"),
            token,
            cancellationToken
        );
        var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.LookbackDays);
        var files = envelope
            .Files.Where(x => x.CreatedAt is null || x.CreatedAt >= cutoff)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var downloaded = 0;
        var present = 0;
        foreach (var file in files)
        {
            if (await DownloadAsync(endpoint, token, file, cancellationToken))
                downloaded++;
            else
                present++;
        }
        return new SyncResult(
            endpoint.Host,
            envelope.Files.Count,
            downloaded,
            present,
            envelope.Files.Count - files.Length
        );
    }

    private async Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var endpoints = new List<(Uri Uri, int Priority)>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DiscoveryUri);
            using var response = await SendAsync(request, false, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var discovery = await response.Content.ReadFromJsonAsync<DiscoveryEnvelope>(
                    JsonOptions,
                    cancellationToken
                );
                foreach (var item in discovery?.Endpoints ?? [])
                {
                    if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
                    {
                        ValidateVendorUri(uri);
                        endpoints.Add((Normalize(uri), item.Priority));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("Endpoint discovery unavailable; using fallbacks.");
        }
        endpoints.AddRange(FallbackEndpoints.Select(x => (x, int.MaxValue)));
        return endpoints
            .OrderBy(x => x.Priority)
            .Select(x => x.Uri)
            .DistinctBy(x => x.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<bool> IsHealthyAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/health"));
        using var response = await SendAsync(request, false, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return false;
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>(
            JsonOptions,
            cancellationToken
        );
        return health?.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<string> LoginAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint, "/auth/login")
        )
        {
            Content = JsonContent.Create(new LoginRequest(settings.Email, settings.Password)),
        };
        using var response = await SendAsync(request, false, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("DNFileVault rejected the credentials.");
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(
            JsonOptions,
            cancellationToken
        );
        return string.IsNullOrWhiteSpace(login?.Token)
            ? throw new InvalidDataException("Login returned no token.")
            : login.Token;
    }

    private async Task<T> GetAsync<T>(Uri uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, false, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"Access was denied for {uri.AbsolutePath}.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"No content was returned for {uri.AbsolutePath}.");
    }

    private async Task<bool> DownloadAsync(
        Uri endpoint,
        string token,
        VaultFile file,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(file.DisplayName)
            || string.IsNullOrWhiteSpace(file.UuidFilename)
        )
            throw new InvalidDataException("An incomplete file record was returned.");
        var safeName = Path.GetFileName(file.DisplayName);
        if (
            !safeName.Equals(file.DisplayName, StringComparison.Ordinal)
            || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
            throw new InvalidDataException($"Unsafe filename: {file.DisplayName}");

        Directory.CreateDirectory(settings.OutputDirectory);
        var manifestPath = Path.Combine(
            settings.OutputDirectory,
            $".{safeName}.{file.UuidFilename}.json"
        );
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        if (
            manifest is not null
            && manifest.UuidFilename == file.UuidFilename
            && File.Exists(manifest.LocalPath)
            && (file.FileSize is null || new FileInfo(manifest.LocalPath).Length == file.FileSize)
        )
        {
            Console.WriteLine($"Present: {safeName}");
            return false;
        }

        var destination = Path.Combine(settings.OutputDirectory, safeName);
        if (File.Exists(destination))
        {
            var stamp = (file.CreatedAt ?? DateTimeOffset.UtcNow).ToString(
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture
            );
            var uuidPrefix = file.UuidFilename[..Math.Min(8, file.UuidFilename.Length)];
            destination = Path.Combine(
                settings.OutputDirectory,
                $"{Path.GetFileNameWithoutExtension(safeName)}.{stamp}.{uuidPrefix}{Path.GetExtension(safeName)}"
            );
        }
        var temporary = destination + ".part";
        try
        {
            var completed =
                !string.IsNullOrWhiteSpace(file.CloudShareLink)
                && Uri.TryCreate(file.CloudShareLink, UriKind.Absolute, out var cloud)
                && await TryCloudDownloadAsync(cloud, temporary, file, cancellationToken);
            if (!completed)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(endpoint, $"/download/{Uri.EscapeDataString(file.UuidFilename)}")
                );
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                await DownloadToTemporaryAsync(request, temporary, file, cancellationToken);
            }

            File.Move(temporary, destination, overwrite: false);
            var hash = await CalculateSha256Async(destination, cancellationToken);
            var saved = new Manifest(
                file.UuidFilename,
                file.DisplayName,
                file.Checksum,
                file.CreatedAt,
                DateTimeOffset.UtcNow,
                destination,
                hash
            );
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(saved, JsonOptions),
                cancellationToken
            );
            Console.WriteLine($"Downloaded: {Path.GetFileName(destination)}");
            return true;
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private async Task<bool> TryCloudDownloadAsync(
        Uri cloudUri,
        string temporary,
        VaultFile file,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ValidateVendorUri(cloudUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, cloudUri);
            using var response = await SendAsync(request, true, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            await SaveAsync(response, temporary, file, timeout.Token);
            return true;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or IOException or TaskCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            Console.Error.WriteLine(
                $"Cloud download failed for {file.DisplayName}; trying the authenticated API."
            );
            return false;
        }
    }

    private async Task DownloadToTemporaryAsync(
        HttpRequestMessage request,
        string temporary,
        VaultFile file,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await SendAsync(request, true, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"Access was denied for {file.DisplayName}.");
        response.EnsureSuccessStatusCode();
        await SaveAsync(response, temporary, file, timeout.Token);
    }

    private static async Task SaveAsync(
        HttpResponseMessage response,
        string temporary,
        VaultFile file,
        CancellationToken cancellationToken
    )
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1_048_576,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        if (file.FileSize is not null && output.Length != file.FileSize)
            throw new InvalidDataException($"Size validation failed for {file.DisplayName}.");
        if (!string.IsNullOrWhiteSpace(file.Checksum))
            await ValidateVendorSha256Async(temporary, file.Checksum, cancellationToken);
    }

    private static async Task ValidateVendorSha256Async(
        string path,
        string checksum,
        CancellationToken cancellationToken
    )
    {
        var expected = checksum.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (expected.Length != 64)
            return;
        var actual = await CalculateSha256Async(path, cancellationToken);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"SHA-256 validation failed for {Path.GetFileName(path)}."
            );
    }

    private static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1_048_576,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<Manifest?> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Manifest>(
            stream,
            JsonOptions,
            cancellationToken
        );
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool download,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return await httpClient.SendAsync(
            request,
            download
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead,
            timeout.Token
        );
    }

    private static Uri Normalize(Uri uri) =>
        new(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);

    private static void ValidateVendorUri(Uri uri)
    {
        if (
            uri.Scheme != Uri.UriSchemeHttps
            || (
                !uri.Host.Equals("dnfilevault.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.EndsWith(".dnfilevault.com", StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new InvalidDataException("A vendor URL was outside HTTPS dnfilevault.com.");
    }

    private sealed record DiscoveryEnvelope(IReadOnlyList<DiscoveryEndpoint> Endpoints);

    private sealed record DiscoveryEndpoint(string Url, int Priority);

    private sealed record HealthResponse(string Status);

    private sealed record LoginRequest(string Email, string Password);

    private sealed record LoginResponse(string Token);

    private sealed record GroupEnvelope(IReadOnlyList<VaultGroup> Groups);

    private sealed record VaultGroup(int Id, string Name);

    private sealed record FileEnvelope(IReadOnlyList<VaultFile> Files);

    private sealed record VaultFile(
        string UuidFilename,
        string DisplayName,
        long? FileSize,
        string? Checksum,
        DateTimeOffset? CreatedAt,
        string? CloudShareLink
    );

    private sealed record Manifest(
        string UuidFilename,
        string DisplayName,
        string? VendorChecksum,
        DateTimeOffset? CreatedAt,
        DateTimeOffset ReceivedAt,
        string LocalPath,
        string LocalSha256
    );

    private sealed class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("DNFileVault timestamps must be strings or null.");

            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (
                DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces
                        | DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out var timestamp
                )
            )
                return timestamp;

            throw new JsonException($"DNFileVault returned an invalid timestamp: '{value}'.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset? value,
            JsonSerializerOptions options
        )
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(value.Value.UtcDateTime);
        }
    }
}

internal sealed record SyncResult(
    string ApiHost,
    int Listed,
    int Downloaded,
    int Present,
    int OutsideLookback
);

internal sealed class ConfigurationException(string message) : Exception(message);
