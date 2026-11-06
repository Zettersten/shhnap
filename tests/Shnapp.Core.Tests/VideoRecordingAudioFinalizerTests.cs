using Shnapp.App.Capture;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class VideoRecordingAudioFinalizerTests
{
    [TestMethod]
    public void MicrophoneFailureKeepsScreenVideoAndComputerAudio()
    {
        string directory = NewTempDirectory();
        try
        {
            string video = WriteFile(directory, "source.mp4");
            string system = WriteFile(directory, "source.system.wav");
            string microphone = WriteFile(directory, "source.microphone.wav");

            VideoRecordingAudioResult result = VideoRecordingAudioFinalizer.Finalize(
                system, null, microphone, new IOException("Device disconnected."));

            Assert.AreEqual(system, result.SystemAudioPath);
            Assert.IsNull(result.MicrophoneAudioPath);
            StringAssert.Contains(result.Warning!, "Microphone audio could not be added");
            Assert.IsTrue(File.Exists(video));
            Assert.IsTrue(File.Exists(system));
            Assert.IsFalse(File.Exists(microphone));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void BothAudioFailuresKeepScreenVideo()
    {
        string directory = NewTempDirectory();
        try
        {
            string video = WriteFile(directory, "source.mp4");
            string system = WriteFile(directory, "source.system.wav");
            string microphone = WriteFile(directory, "source.microphone.wav");

            VideoRecordingAudioResult result = VideoRecordingAudioFinalizer.Finalize(
                system, new IOException("Output changed."),
                microphone, new IOException("Input changed."));

            Assert.IsNull(result.SystemAudioPath);
            Assert.IsNull(result.MicrophoneAudioPath);
            StringAssert.Contains(result.Warning!, "Computer audio could not be added");
            StringAssert.Contains(result.Warning!, "Microphone audio could not be added");
            Assert.IsTrue(File.Exists(video));
            Assert.IsFalse(File.Exists(system));
            Assert.IsFalse(File.Exists(microphone));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ShnappAudioTest-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "test");
        return path;
    }
}
