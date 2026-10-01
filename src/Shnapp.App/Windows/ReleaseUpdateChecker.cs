using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Shnapp.App.Windows;

/// <summary>Checks published stable GitHub releases without installing software.</summary>
internal sealed class ReleaseUpdateChecker
{
    private static readonly Uri DefaultApiUri = new("https://api.github.com/repos/Zettersten/shhnap/releases/latest");
    private static readonly HttpClient Client = CreateClient();
    private readonly string _statePath;
    private readonly HttpClient _client;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReleaseState? _state;

    internal ReleaseUpdateChecker(string dataRoot, HttpClient? client = null, Func<DateTimeOffset>? utcNow = null)
    {
        _statePath = Path.Combine(dataRoot, "release-check.json");
        _client = client ?? Client;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<bool> MarkNotifiedAsync(string tag, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReleaseState state = _state ??= await LoadAsync(cancellationToken);
            if (string.Equals(state.LastNotifiedTag, tag, StringComparison.Ordinal))
            {
                return false;
            }

            await SaveAsync(state with { LastNotifiedTag = tag }, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<ReleaseCheckResult> CheckAsync(Version installedVersion, bool manual, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReleaseState state = _state ??= await LoadAsync(cancellationToken);
            DateTimeOffset now = _utcNow();
            if (state.RetryAfterUtc is { } retryAfter && retryAfter > now)
            {
                return FromCache(state, installedVersion, $"GitHub asked Shnapp to wait until {retryAfter.ToLocalTime():g} before checking again.");
            }

            if (!manual && state.LastAttemptUtc is { } lastAttempt && now - lastAttempt < TimeSpan.FromDays(1))
            {
                return FromCache(state, installedVersion, null);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                Uri requestUri = ValidApiUri(state.ApiUrl) ?? DefaultApiUri;
                Uri? permanentUri = null;
                for (int redirects = 0; redirects < 4; redirects++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    request.Headers.UserAgent.ParseAdd("Shnapp/1.0");
                    request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                    if (EntityTagHeaderValue.TryParse(state.ETag, out EntityTagHeaderValue? etag))
                    {
                        request.Headers.IfNoneMatch.Add(etag);
                    }

                    using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token);
                    if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect)
                    {
                        Uri? location = response.Headers.Location;
                        if (location is null)
                        {
                            throw new HttpRequestException("GitHub returned a redirect without a destination.");
                        }

                        Uri nextUri = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
                        if (ValidApiUri(nextUri.AbsoluteUri) is null)
                        {
                            throw new HttpRequestException("GitHub returned an unexpected update address.");
                        }

                        if (response.StatusCode == HttpStatusCode.MovedPermanently)
                        {
                            permanentUri = nextUri;
                        }

                        requestUri = nextUri;
                        continue;
                    }

                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                    {
                        DateTimeOffset nextTry = RetryAfter(response, now);
                        state = state with { LastAttemptUtc = now, RetryAfterUtc = nextTry };
                        await SaveAsync(state, cancellationToken);
                        return FromCache(state, installedVersion, $"GitHub's update check is temporarily limited. Try again after {nextTry.ToLocalTime():g}.", isError: true);
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        state = state with
                        {
                            LastAttemptUtc = now,
                            RetryAfterUtc = null,
                            ApiUrl = permanentUri?.AbsoluteUri ?? state.ApiUrl,
                            ETag = null,
                            LatestTag = null,
                            LatestPage = null,
                        };
                        await SaveAsync(state, cancellationToken);
                        return new("No public stable GitHub release is available yet.");
                    }

                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        state = state with { LastAttemptUtc = now, RetryAfterUtc = null, ApiUrl = permanentUri?.AbsoluteUri ?? state.ApiUrl };
                        await SaveAsync(state, cancellationToken);
                        return FromCache(state, installedVersion, null);
                    }

                    response.EnsureSuccessStatusCode();
                    await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using JsonDocument release = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                    JsonElement root = release.RootElement;
                    string? tag = root.TryGetProperty("tag_name", out JsonElement tagValue) &&
                        tagValue.ValueKind == JsonValueKind.String ? tagValue.GetString() : null;
                    string? page = root.TryGetProperty("html_url", out JsonElement pageValue) &&
                        pageValue.ValueKind == JsonValueKind.String ? pageValue.GetString() : null;
                    string canonicalApiUrl = permanentUri?.AbsoluteUri ?? state.ApiUrl ?? DefaultApiUri.AbsoluteUri;
                    if (!TryParseVersion(tag, out _) || !IsReleasePage(page, canonicalApiUrl))
                    {
                        throw new InvalidDataException("GitHub returned release details Shnapp could not verify.");
                    }

                    state = state with
                    {
                        LastAttemptUtc = now,
                        RetryAfterUtc = null,
                        ETag = response.Headers.ETag?.ToString(),
                        LatestTag = tag,
                        LatestPage = page,
                        ApiUrl = canonicalApiUrl,
                    };
                    await SaveAsync(state, cancellationToken);
                    return FromCache(state, installedVersion, null);
                }

                throw new HttpRequestException("GitHub redirected the update check too many times.");
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            {
                state = state with { LastAttemptUtc = now };
                await SaveAsync(state, cancellationToken);
                return FromCache(state, installedVersion, "Could not reach GitHub to check for updates. Your current version still works offline.", isError: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ReleaseCheckResult FromCache(ReleaseState state, Version installedVersion, string? overrideMessage,
        bool isError = false)
    {
        if (TryParseVersion(state.LatestTag, out Version? latest) && IsReleasePage(state.LatestPage, state.ApiUrl))
        {
            bool newer = Normalize(latest!).CompareTo(Normalize(installedVersion)) > 0;
            string message = overrideMessage ?? (newer
                ? $"Shnapp {state.LatestTag} is available on GitHub. Package manager releases may follow later."
                : "Shnapp is up to date with the latest stable GitHub release.");
            return new(message, newer, state.LatestTag, new Uri(state.LatestPage!), isError);
        }

        return new(overrideMessage ?? "No public stable GitHub release has been found yet.", IsError: isError);
    }

    private static bool TryParseVersion(string? tag, out Version? version)
    {
        string text = tag?.Trim().TrimStart('v', 'V') ?? string.Empty;
        version = text.Contains('-') ? null : Version.TryParse(text, out Version? parsed) ? parsed : null;
        return version is not null;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static bool IsReleasePage(string? value, string? apiUrl)
    {
        Uri api = ValidApiUri(apiUrl) ?? DefaultApiUri;
        string[] segments = api.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string releasePrefix = $"/{segments[1]}/{segments[2]}/releases/tag/";
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Length > releasePrefix.Length;
    }

    private static Uri? ValidApiUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 5 && segments[0] == "repos" &&
            segments[1].Length > 0 && segments[2].Length > 0 &&
            segments[3] == "releases" && segments[4] == "latest" ? uri : null;
    }

    private static DateTimeOffset RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return now + delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date;
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out IEnumerable<string>? values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { }
        }

        return now.AddMinutes(1);
    }

    private async Task<ReleaseState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream file = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<ReleaseState>(file, cancellationToken: cancellationToken) ?? new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private async Task SaveAsync(ReleaseState state, CancellationToken cancellationToken)
    {
        _state = state;
        string? directory = Path.GetDirectoryName(_statePath);
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            string temp = _statePath + ".tmp";
            await using (FileStream file = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(file, state, cancellationToken: cancellationToken);
            }

            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Update status remains available for this session even if its cache cannot be written.
        }
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private sealed record ReleaseState
    {
        public DateTimeOffset? LastAttemptUtc { get; init; }
        public DateTimeOffset? RetryAfterUtc { get; init; }
        public string? ApiUrl { get; init; }
        public string? ETag { get; init; }
        public string? LatestTag { get; init; }
        public string? LatestPage { get; init; }
        public string? LastNotifiedTag { get; init; }
    }
}

internal sealed record ReleaseCheckResult(string Message, bool UpdateAvailable = false,
    string? LatestTag = null, Uri? ReleasePage = null, bool IsError = false);
