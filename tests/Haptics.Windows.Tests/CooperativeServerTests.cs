using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Haptics.Windows.Tests;

public class CooperativeServerTests
{
    [Fact]
    public async Task DeniedPipeCreationReportsErrorAndRecoversAfterTheNameIsReleased()
    {
        var name = $"haptics-tests-denied-{Guid.NewGuid():N}";
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var blocker = NamedPipeServerStreamAcl.Create(name, PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new CooperativeServer((waveform, _) => received.TrySetResult(waveform), name, 5000);
        server.Diagnostic += message => reported.TrySetResult(message);
        server.Start();
        Assert.Contains("Cooperative pipe error", await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(server.IsRunning);
        blocker.Dispose();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        await client.WriteAsync(Encoding.UTF8.GetBytes("Align\n"));
        await client.FlushAsync();
        Assert.Equal("Align", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HeldClientCannotBlockTheNextClientPastItsDeadline()
    {
        using var rig = new PipeRig(150);
        using var held = await rig.Connect();
        await rig.Send("Align\n");
        Assert.Equal("Align", await rig.Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(rig.Diagnostics, m => m.Contains("deadline"));
        Assert.Single(rig.Waveforms);
    }

    [Fact]
    public async Task OversizedUnterminatedLineIsRejectedBeforeWaitingForEndOfLine()
    {
        using var rig = new PipeRig(5000);
        using var oversized = await rig.Connect();
        await oversized.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 257)));
        await oversized.FlushAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await rig.Send("Hover\n", limit.Token);
        Assert.Equal("Hover", await rig.Received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains(rig.Diagnostics, m => m.Contains("256"));
        Assert.Single(rig.Waveforms);
    }

    [Fact]
    public async Task DisposeClosesActiveClientAndStopsTheLoop()
    {
        using var rig = new PipeRig(10000);
        using var held = await rig.Connect();
        rig.Server.Dispose();
        Assert.False(rig.Server.IsRunning);
        Assert.Empty(rig.Waveforms);
        var error = await Record.ExceptionAsync(async () =>
        {
            await held.WriteAsync(Encoding.UTF8.GetBytes("Align\n"));
            await held.FlushAsync();
        });
        Assert.IsAssignableFrom<IOException>(error);
        rig.Server.Dispose();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task Exactly256CharactersAndAll64LinesRemainAccepted(string ending)
    {
        using var rig = new PipeRig(5000, completeAfter: 64);
        var valid = "{\"waveform\":\"Align\"}".PadRight(256) + ending;
        await rig.Send(string.Concat(Enumerable.Repeat(valid, 64)));
        await rig.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(64, rig.Waveforms.Count);
        Assert.All(rig.Waveforms, w => Assert.Equal("Align", w));
    }

    [Fact]
    public async Task ClientDeadlineIsAbsoluteNotRenewedByPartialInput()
    {
        using var rig = new PipeRig(250);
        using var slow = await rig.Connect();
        await slow.WriteAsync(Encoding.UTF8.GetBytes("A"));
        await slow.FlushAsync();
        await Task.Delay(150);
        try
        {
            await slow.WriteAsync(Encoding.UTF8.GetBytes("l"));
            await slow.FlushAsync();
        }
        catch (IOException)
        {
            // A delayed test runner may resume after the absolute deadline already closed this client.
        }
        await rig.Send("Hover\n");
        Assert.Equal("Hover", await rig.Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(rig.Diagnostics, m => m.Contains("deadline"));
        Assert.Single(rig.Waveforms);
    }

    private sealed class PipeRig : IDisposable
    {
        private readonly string _name = $"haptics-tests-{Guid.NewGuid():N}";
        public CooperativeServer Server { get; }
        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Waveforms { get; } = new();
        public ConcurrentQueue<string> Diagnostics { get; } = new();

        public PipeRig(int deadlineMs, int completeAfter = 1)
        {
            Server = new CooperativeServer((waveform, _) =>
            {
                Waveforms.Enqueue(waveform);
                if (Waveforms.Count == completeAfter)
                {
                    Received.TrySetResult(waveform);
                }
            }, _name, deadlineMs);
            Server.Diagnostic += Diagnostics.Enqueue;
            Server.Start();
            Assert.True(Server.IsRunning);
        }

        public async Task<NamedPipeClientStream> Connect(CancellationToken token = default)
        {
            var client = new NamedPipeClientStream(".", _name, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(5000, token);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task Send(string message, CancellationToken token = default)
        {
            using var client = await Connect(token);
            await client.WriteAsync(Encoding.UTF8.GetBytes(message), token);
            await client.FlushAsync(token);
        }

        public void Dispose() => Server.Dispose();
    }
}
