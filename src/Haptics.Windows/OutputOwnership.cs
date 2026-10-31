using Haptics.Core;

namespace Haptics.Windows;

/// <summary>Session-wide, non-blocking lease for production haptic output and physical lab sessions.</summary>
public sealed class OutputOwnership : IDisposable
{
    private const string DefaultLeaseName = @"Local\HapticsEverywhereOutputOwner";
    private static readonly ThreadLocal<HashSet<string>> s_ownedNames =
        new(() => new HashSet<string>(StringComparer.Ordinal));
    private readonly object _sync = new();
    private readonly object _sendSync = new();
    private readonly Mutex _lease;
    private readonly string _leaseName;
    private int _owns;
    private int _disposed;
    private int _acceptingOutput = 1;
    private int _owningThreadId;
    private Thread? _owningThread;

    private OutputOwnership(string owner, string leaseName)
    {
        Owner = owner;
        _leaseName = leaseName;
        _lease = new Mutex(false, leaseName);
    }

    public string Owner { get; }

    public bool IsOwner =>
        Volatile.Read(ref _owns) != 0 && Volatile.Read(ref _owningThread)?.IsAlive == true;

    public static OutputOwnership Create(string owner) => Create(owner, DefaultLeaseName);

    internal static OutputOwnership Create(string owner, string leaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);
        return new OutputOwnership(owner, leaseName);
    }

    public bool TryAcquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (IsOwner)
            {
                return _owningThreadId == Environment.CurrentManagedThreadId;
            }

            var ownedNames = s_ownedNames.Value!;
            if (ownedNames.Contains(_leaseName))
            {
                return false;
            }

            try
            {
                if (!_lease.WaitOne(0))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The prior owner exited without cleanup; WaitOne transfers ownership to this thread.
            }

            _owningThreadId = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _owningThread, Thread.CurrentThread);
            ownedNames.Add(_leaseName);
            Volatile.Write(ref _owns, 1);
            return true;
        }
    }

    public void Record(TrialRecord trial)
    {
        ArgumentNullException.ThrowIfNull(trial);
        trial.Context["outputOwner"] = Owner;
        trial.Context["outputOwnershipGranted"] = IsOwner;
    }

    public bool TryRun(Action send)
    {
        ArgumentNullException.ThrowIfNull(send);
        lock (_sendSync)
        {
            if (!IsOwner || Volatile.Read(ref _acceptingOutput) == 0)
            {
                return false;
            }

            send();
            return true;
        }
    }

    public void StopSending()
    {
        lock (_sendSync)
        {
            Volatile.Write(ref _acceptingOutput, 0);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            lock (_sendSync)
            {
                if (IsOwner && _owningThreadId != Environment.CurrentManagedThreadId)
                {
                    throw new InvalidOperationException("Output ownership must be released by the thread that acquired it.");
                }

                Volatile.Write(ref _acceptingOutput, 0);
                if (IsOwner)
                {
                    _lease.ReleaseMutex();
                    s_ownedNames.Value!.Remove(_leaseName);
                    Volatile.Write(ref _owns, 0);
                    Volatile.Write(ref _owningThread, null);
                }

                _disposed = 1;
                _lease.Dispose();
            }
        }
    }
}
