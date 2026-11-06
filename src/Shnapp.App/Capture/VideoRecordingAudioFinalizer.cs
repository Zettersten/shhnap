namespace Shnapp.App.Capture;

internal sealed record VideoRecordingAudioResult(
    string? SystemAudioPath,
    string? MicrophoneAudioPath,
    string? Warning);

internal static class VideoRecordingAudioFinalizer
{
    internal static VideoRecordingAudioResult Finalize(
        string? systemAudioPath,
        Exception? systemAudioFailure,
        string? microphoneAudioPath,
        Exception? microphoneAudioFailure)
    {
        string? systemPath = KeepHealthyTrack(systemAudioPath, systemAudioFailure);
        string? microphonePath = KeepHealthyTrack(microphoneAudioPath, microphoneAudioFailure);
        List<string> warnings = [];
        if (systemAudioFailure is not null)
            warnings.Add("Computer audio could not be added: " + systemAudioFailure.Message);
        if (microphoneAudioFailure is not null)
            warnings.Add("Microphone audio could not be added: " + microphoneAudioFailure.Message);
        return new VideoRecordingAudioResult(systemPath, microphonePath,
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private static string? KeepHealthyTrack(string? path, Exception? failure)
    {
        if (failure is null || path is null)
            return path;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
