using System.Diagnostics;
using System.Reflection;
using Haptics.Core;
using Haptics.Lab;
using Haptics.Lab.Experiments;

namespace Haptics.Windows.Tests;

public class ExperimentHarnessTests
{
    public static IEnumerable<object[]> NonzeroExits =>
        from command in new[] { "doctor", "EXP-070", "EXP-081", "EXP-043", "report" }
        from code in new[] { 1, 2, 3, 4, -1073741819 }
        select new object[] { command, code };

    [Theory]
    [MemberData(nameof(NonzeroExits))]
    public void CiRejectsEveryNonzeroNativeExit(string command, int code) =>
        Assert.NotEqual(0, RunCiStep(command, code));

    [Fact]
    public void CiAcceptsSuccessfulCommands() => Assert.Equal(0, RunCiStep("", 0));

    [Theory]
    [InlineData(ExperimentStatus.PASS, 0, 0)]
    [InlineData(ExperimentStatus.FAIL, 1, 1)]
    [InlineData(ExperimentStatus.BLOCKED, 0, 1)]
    [InlineData(ExperimentStatus.INDETERMINATE, 0, 1)]
    [InlineData(ExperimentStatus.NOT_RUN, 0, 1)]
    [InlineData(ExperimentStatus.SKIPPED_BY_POLICY, 0, 1)]
    public void StrictPassIsOptIn(ExperimentStatus status, int normalExit, int strictExit)
    {
        Assert.Equal(normalExit, Program.ExperimentExitCode(status));
        Assert.Equal(strictExit, Program.ExperimentExitCode(status, requirePass: true));
    }

    [Theory]
    [InlineData(false, ExperimentStatus.INDETERMINATE)]
    [InlineData(true, ExperimentStatus.FAIL)]
    public void TimedOutProbeIsIndeterminateUnlessAnAssertionAlreadyFailed(bool assertionFailed, ExperimentStatus expected)
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var result = new ExperimentResult { RunId = "test", ExperimentId = "EXP-023" };
        result.Assertions.Add(new Assertion { Name = "manager-thread-id-matches-owner", Passed = !assertionFailed });
        var background = false;
        try
        {
            RunProbe(result, () =>
            {
                background = Thread.CurrentThread.IsBackground;
                started.Set();
                release.Wait();
                finished.Set();
            }, TimeSpan.FromMilliseconds(20));
            Assert.Equal(expected, result.Status);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(background);
            result.Finalize();
            Assert.Equal(expected, result.Status);
        }
        finally
        {
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void CompletedProbeRecordsObservedPass()
    {
        var result = new ExperimentResult { RunId = "test", ExperimentId = "EXP-023" };
        RunProbe(result, () => { }, TimeSpan.FromSeconds(5));
        Assert.Equal(ExperimentStatus.PASS, result.Status);
        Assert.Equal(EvidenceClass.OBSERVED, result.EvidenceClass);
    }

    private static void RunProbe(ExperimentResult result, Action probe, TimeSpan timeout)
    {
        var method = typeof(Exp023ThreadAffinity).GetMethod("RunProbe", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [result, probe, timeout]);
    }

    private static int RunCiStep(string failAt, int code)
    {
        var repo = ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!;
        var workflow = File.ReadAllLines(Path.Combine(repo, ".github", "workflows", "ci.yml"));
        var step = Array.FindIndex(workflow, l => l.Contains("name: Automated experiments", StringComparison.Ordinal));
        var start = Array.FindIndex(workflow, step, l => l.Trim() == "run: |") + 1;
        var body = workflow.Skip(start).TakeWhile(l => l.StartsWith("          ", StringComparison.Ordinal))
            .Select(l => l[10..]).ToArray();
        Assert.NotEmpty(body);
        Assert.Contains(body, l => l.Contains("run $e --require-pass", StringComparison.Ordinal));
        var root = Path.Combine(Path.GetTempPath(), $"haptics-ci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stub = Path.Combine(root, "lab.cmd");
            File.WriteAllText(stub, """
                @echo off
                if "%1"=="%FAIL_AT%" exit /b %STUB_EXIT%
                if "%2"=="%FAIL_AT%" exit /b %STUB_EXIT%
                exit /b 0
                """);
            body[0] = $"$exe = Get-Item '{stub.Replace("'", "''", StringComparison.Ordinal)}'";
            var script = Path.Combine(root, "step.ps1");
            File.WriteAllLines(script, body.Append("exit $LASTEXITCODE"));
            var startInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script })
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment["FAIL_AT"] = failAt;
            startInfo.Environment["STUB_EXIT"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("CI stub step did not exit within 30 seconds.");
            }

            Task.WaitAll(stdout, stderr);
            return process.ExitCode;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
