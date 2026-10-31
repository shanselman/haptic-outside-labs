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
        try
        {
            await rig.Send(string.Concat(Enumerable.Repeat(valid, 64)));
        }
        catch (IOException)
        {
            // Closing at the line cap can race the writer's completion; delivery is asserted below.
        }
        await rig.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(64, rig.Waveforms.Count);
        Assert.All(rig.Waveforms, w => Assert.Equal("Align", w));
    }

    [Fact]
    public async Task CallbackFaultIsReportedAndTheNextClientStillDelivers()
    {
        var name = $"haptics-tests-callback-{Guid.NewGuid():N}";
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var server = new CooperativeServer((waveform, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("fixture callback fault");
            }

            received.TrySetResult(waveform);
        }, name, 5000);
        server.Diagnostic += message => reported.TrySetResult(message);
        server.Start();
        using (var first = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await first.ConnectAsync(5000);
            await first.WriteAsync(Encoding.UTF8.GetBytes("Align\n"));
            await first.FlushAsync();
        }

        Assert.Contains("fixture callback fault", await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(server.IsRunning);
        using var second = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await second.ConnectAsync(5000);
        await second.WriteAsync(Encoding.UTF8.GetBytes("Hover\n"));
        await second.FlushAsync();
        Assert.Equal("Hover", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConnectionDeliversNoMoreThan64Lines()
    {
        using var rig = new PipeRig(5000, completeAfter: 65);
        try
        {
            await rig.Send(string.Concat(Enumerable.Repeat("Align\n", 65)));
        }
        catch (IOException)
        {
            // The server intentionally closes this connection before accepting the 65th line.
        }
        await rig.Send("Hover\n");
        Assert.Equal("Hover", await rig.Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        rig.Server.Dispose(); // Join so the assertion sees every callback from this connection.
        Assert.Equal(64, rig.Waveforms.Count(w => w == "Align"));
    }

    [Fact]
    public async Task ClientDeadlineIsAbsoluteNotRenewedByPartialInput()
    {
        const int deadlineMs = 600;
        using var rig = new PipeRig(deadlineMs);
        using var slow = await rig.Connect();
        using var trickleCancellation = new CancellationTokenSource();
        async Task Trickle()
        {
            try
            {
                for (var i = 0; i < 11; i++)
                {
                    await slow.WriteAsync(Encoding.UTF8.GetBytes("x"), trickleCancellation.Token);
                    await slow.FlushAsync(trickleCancellation.Token);
                    await Task.Delay(deadlineMs / 3, trickleCancellation.Token);
                }
            }
            catch (IOException)
            {
                // The absolute deadline closes this client while it is still trickling.
            }
            catch (OperationCanceledException) when (trickleCancellation.IsCancellationRequested)
            {
            }
        }

        var trickle = Trickle();
        using var limit = new CancellationTokenSource(deadlineMs * 2);
        try
        {
            await rig.Send("Hover\n", limit.Token);
            Assert.Equal("Hover", await rig.Received.Task.WaitAsync(limit.Token));
        }
        finally
        {
            trickleCancellation.Cancel();
            await trickle;
        }

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
