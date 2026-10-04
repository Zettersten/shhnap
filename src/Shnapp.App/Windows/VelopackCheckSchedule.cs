using System.Text.Json;

namespace Shnapp.App.Windows;

/// <summary>Keeps automatic Velopack checks from repeating after an app restart.</summary>
internal sealed class VelopackCheckSchedule
{
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FailureInterval = TimeSpan.FromHours(1);
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private State? _state;

    internal VelopackCheckSchedule(string dataRoot, Func<DateTimeOffset>? utcNow = null)
    {
        _path = Path.Combine(dataRoot, "velopack-update-check.json");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<(bool Check, DateTimeOffset? RetryAfterUtc)> ShouldCheckAsync(
        bool manual, CancellationToken cancellationToken)
    {
        State state = await LoadAsync(cancellationToken);
        DateTimeOffset now = _utcNow();
        if (state.RetryAfterUtc is { } retryAfter && retryAfter > now)
        {
            return (false, retryAfter);
        }

        return (manual || state.NextAutomaticCheckUtc is null || state.NextAutomaticCheckUtc <= now, null);
    }

    internal Task RecordSuccessAsync(CancellationToken cancellationToken) =>
        SaveAsync(new State(_utcNow() + AutomaticInterval, null), cancellationToken);

    internal Task RecordFailureAsync(bool rateLimited, CancellationToken cancellationToken)
    {
        DateTimeOffset retryAfter = _utcNow() + FailureInterval;
        return SaveAsync(new State(retryAfter, rateLimited ? retryAfter : null), cancellationToken);
    }

    private async Task<State> LoadAsync(CancellationToken cancellationToken)
    {
        if (_state is not null) { return _state; }
        try
        {
            await using FileStream stream = File.OpenRead(_path);
            _state = await JsonSerializer.DeserializeAsync<State>(stream, cancellationToken: cancellationToken) ?? new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _state = new();
        }

        return _state;
    }

    private async Task SaveAsync(State state, CancellationToken cancellationToken)
    {
        _state = state;
        string temporaryPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory schedule still protects this process when the user folder is unavailable.
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record State(DateTimeOffset? NextAutomaticCheckUtc = null,
        DateTimeOffset? RetryAfterUtc = null);
}
