using System.Text.Json;
using Haptics.Core;
using Haptics.Tray;
using static Haptics.Windows.Tests.PipelineCharacterizationTests;

namespace Haptics.Windows.Tests;

public class TrayPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffRevokesAndInvalidatesBeforeSaveFailureIsReported(bool accessDenied)
    {
        var clock = new FakeClock { NowMs = 100 };
        var sink = new RecordingPipelineOutputSink();
        using var pipeline = new ObservationPipeline(new TraySettings().ToPolicy(),
            new PipelineOptions { Clock = clock, OutputSink = sink });
        pipeline.Policy.ConsentGiven = true;
        pipeline.Policy.OutputRouteVerified = true;
        pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = Controller() };
        Pointer(pipeline, 100, 20, 15, 12);
        pipeline.SubmitCooperative("Align", 0);
        Assert.Equal(1, sink.Pending);
        var generation = pipeline.Interpreter.Generation;
        var result = TrayContext.RevokeAndSave(new TraySettings { Enabled = false }, pipeline, () =>
        {
            Assert.Equal(HapticsMode.Off, pipeline.Policy.Config.Mode);
            Assert.False(pipeline.Policy.ConsentGiven);
            Assert.True(pipeline.Interpreter.Generation > generation);
            Assert.Null(pipeline.LatestInput);
            sink.Flush();
            Assert.Empty(sink.Requests);
            if (accessDenied)
            {
                throw new UnauthorizedAccessException("fixture denied");
            }

            throw new IOException("fixture disk full");
        });

        Assert.Null(result.Pipeline);
        Assert.Contains("session only", result.Error);
        Assert.Contains(accessDenied ? "fixture denied" : "fixture disk full", result.Error);
    }

    [Fact]
    public void SettingsSaveReplacesCompleteJsonAndCleansTemporaryFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"haptics-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "tray.json");
        try
        {
            new TraySettings().Save(path);
            new TraySettings { Enabled = false }.Save(path);
            Assert.False(JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), HapticsJson.Indented)!.Enabled);
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AtomicReplacementDoesNotOverwriteAnOpenOriginal()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"haptics-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "tray.json");
        Directory.CreateDirectory(folder);
        try
        {
            new TraySettings().Save(path);
            var original = File.ReadAllText(path);
            using (var opened = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var error = Record.Exception(() => new TraySettings { Enabled = false }.Save(path));
                Assert.True(error is null or IOException or UnauthorizedAccessException, error?.ToString());
                using var reader = new StreamReader(opened);
                Assert.Equal(original, reader.ReadToEnd());
                if (error is not null)
                {
                    Assert.Equal(original, File.ReadAllText(path));
                }
            }

            new TraySettings { Enabled = false }.Save(path);
            Assert.False(JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), HapticsJson.Indented)!.Enabled);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void LockedSettingsFilePreservesOriginalAndCleansTemporaryFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"haptics-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "tray.json");
        Directory.CreateDirectory(folder);
        try
        {
            new TraySettings().Save(path);
            var original = File.ReadAllText(path);
            // Permit reads, but not replacing or overwriting the existing file.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => new TraySettings { Enabled = false }.Save(path));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a failed replacement");
                Assert.Equal(original, File.ReadAllText(path));
                Assert.Equal([path], Directory.GetFiles(folder));
            }

            new TraySettings { Enabled = false }.Save(path);
            Assert.False(JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), HapticsJson.Indented)!.Enabled);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
