using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace AnoMech.Core.Map;

// Managed ownership and completion ordering for one client-side zone session.
// Native callbacks are supplied by ZoneSession; this type never owns or clears
// native recovery data itself.
internal sealed class ZoneRestoration : IDisposable
{
    private enum Phase
    {
        Idle,
        Active,
        Pending,
        Failed,
        Disposed,
    }

    // A position tolerance of 0.01 is one centimetre in world units.
    private const float PositionToleranceWorldUnits = 0.01f;

    // The saved and read-back headings may differ by at most 0.001 radians
    // (about 0.057 degrees), measured on the shortest arc modulo 2*pi.
    private const float RotationToleranceRadians = 0.001f;

    private readonly object gate = new();
    private readonly Action restoreNative;
    private readonly Action completeNative;
    private readonly Func<Action, Task> queueCompletion;
    private readonly Action<Exception> reportFailure;

    private Phase phase;
    private long generation;
    private bool completionStarted;
    private bool enterInProgress;
    private Exception? lastError;

    internal ZoneRestoration(
        Action restoreNative,
        Action completeNative,
        Func<Action, Task> queueCompletion,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(restoreNative);
        ArgumentNullException.ThrowIfNull(completeNative);
        ArgumentNullException.ThrowIfNull(queueCompletion);
        ArgumentNullException.ThrowIfNull(reportFailure);

        this.restoreNative = restoreNative;
        this.completeNative = completeNative;
        this.queueCompletion = queueCompletion;
        this.reportFailure = reportFailure;
    }

    internal bool IsActive
    {
        get
        {
            lock (gate)
                return phase is Phase.Active or Phase.Pending or Phase.Failed;
        }
    }

    internal bool IsRestoring
    {
        get
        {
            lock (gate)
                return phase is Phase.Pending or Phase.Failed;
        }
    }

    internal bool HasFailed
    {
        get
        {
            lock (gate)
                return phase == Phase.Failed;
        }
    }

    internal Exception? LastError
    {
        get
        {
            lock (gate)
                return lastError;
        }
    }

    // Validation and native loading are one serialized enter operation. The
    // validation callback runs while still idle; ownership is claimed only
    // after it succeeds, and a load exception therefore leaves ownership held
    // in Failed rather than guessing that the client returned to safety.
    internal void Enter(Action validateAndSave, Action loadNative)
    {
        ArgumentNullException.ThrowIfNull(validateAndSave);
        ArgumentNullException.ThrowIfNull(loadNative);

        lock (gate)
        {
            EnsureCanEnterLocked();
            enterInProgress = true;
            try
            {
                validateAndSave();
                if (phase == Phase.Disposed)
                    throw new ObjectDisposedException(nameof(ZoneRestoration));
                if (phase != Phase.Idle)
                    throw new InvalidOperationException("Zone restoration state changed during validation.");

                var ticket = ++generation;
                phase = Phase.Active;
                completionStarted = false;
                lastError = null;

                try
                {
                    loadNative();
                }
                catch (Exception ex)
                {
                    if (phase == Phase.Active && generation == ticket)
                    {
                        phase = Phase.Failed;
                        lastError = ex;
                        ReportFailureSafely(ex);
                    }

                    // Preserve the original load exception for Start's caller.
                    throw;
                }
            }
            finally
            {
                enterInProgress = false;
            }
        }
    }

    // Starts one recovery attempt. Native recovery runs immediately; only a
    // successful native call is allowed to enqueue the delayed framework-side
    // completion. All recovery failures are observed and become Failed.
    internal void Revert()
    {
        long ticket;
        lock (gate)
        {
            if (phase is Phase.Idle or Phase.Pending or Phase.Disposed)
                return;
            if (phase is not (Phase.Active or Phase.Failed))
                return;

            ticket = ++generation;
            phase = Phase.Pending;
            completionStarted = false;
            lastError = null;
            try
            {
                // Serialize native work against disposal; never start it after
                // Dispose has invalidated this session.
                restoreNative();
            }
            catch (Exception ex)
            {
                FailPending(ticket, ex);
                return;
            }
        }

        // Dispose or another invalidating transition may have happened while
        // the native delegate ran. Never enqueue a stale completion in that case.
        if (!IsCurrentPending(ticket))
            return;

        Task completionTask;
        try
        {
            completionTask = queueCompletion(() => RunCompletion(ticket));
            if (completionTask is null)
                throw new InvalidOperationException("The restoration completion queue returned null.");
        }
        catch (Exception ex)
        {
            FailPending(ticket, ex);
            return;
        }

        ObserveQueueTask(ticket, completionTask);
    }

    // Dispose is intentionally managed-only. ZoneSession retains its existing
    // synchronous native cleanup path; this invalidates all old callbacks and
    // task observers before that path runs.
    public void Dispose()
    {
        lock (gate)
        {
            if (phase == Phase.Disposed)
                return;

            ++generation;
            phase = Phase.Disposed;
            completionStarted = false;
        }
    }

    // The director marker is cleared only after successful termination. Sync
    // restoration is independent of that marker, so a sync failure can retry
    // without terminating an already-terminated director a second time.
    internal static void RestoreInstanceState(
        ref uint? directorId,
        Action<uint> terminateDirector,
        Action restoreSync)
    {
        ArgumentNullException.ThrowIfNull(terminateDirector);
        ArgumentNullException.ThrowIfNull(restoreSync);

        if (directorId is uint id)
        {
            terminateDirector(id);
            directorId = null;
        }

        restoreSync();
    }

    // Compares only observable read-back values. Territory must be a non-zero
    // exact match; positions use full 3D world distance; headings use the
    // shortest modulo-2*pi arc. Non-finite values are never accepted.
    internal static bool MatchesPose(
        uint currentTerritory,
        uint expectedTerritory,
        Vector3 currentPosition,
        Vector3 expectedPosition,
        float currentRotation,
        float expectedRotation)
    {
        if (expectedTerritory == 0 || currentTerritory != expectedTerritory)
            return false;
        if (!IsFinite(currentPosition) || !IsFinite(expectedPosition)
            || !float.IsFinite(currentRotation) || !float.IsFinite(expectedRotation))
            return false;

        if (Vector3.Distance(currentPosition, expectedPosition) > PositionToleranceWorldUnits)
            return false;

        var shortestAngle = MathF.Abs(MathF.IEEERemainder(
            currentRotation - expectedRotation, MathF.Tau));
        return shortestAngle <= RotationToleranceRadians;
    }

    private void EnsureCanEnterLocked()
    {
        if (phase == Phase.Disposed)
            throw new ObjectDisposedException(nameof(ZoneRestoration));
        if (enterInProgress || phase != Phase.Idle)
            throw new InvalidOperationException("Zone restoration is already active or restoring.");
    }

    private bool IsCurrentPending(long ticket)
    {
        lock (gate)
            return IsCurrentPendingLocked(ticket);
    }

    private bool IsCurrentPendingLocked(long ticket)
        => phase == Phase.Pending && generation == ticket;

    private void FailPending(long ticket, Exception error)
    {
        var shouldReport = false;
        lock (gate)
        {
            if (!IsCurrentPendingLocked(ticket))
                return;

            phase = Phase.Failed;
            completionStarted = false;
            lastError = error;
            shouldReport = true;
        }

        if (shouldReport)
            ReportFailureSafely(error);
    }

    private void RunCompletion(long ticket)
    {
        lock (gate)
        {
            if (!IsCurrentPendingLocked(ticket) || completionStarted)
                return;

            completionStarted = true;
            try
            {
                // Queue cancellation cannot mark this attempt failed halfway
                // through the native completion that releases isolation.
                completeNative();
            }
            catch (Exception ex)
            {
                FailPending(ticket, ex);
                return;
            }

            if (!IsCurrentPendingLocked(ticket))
                return;

            phase = Phase.Idle;
            completionStarted = false;
            lastError = null;
        }
    }

    private void ObserveQueueTask(long ticket, Task completionTask)
    {
        try
        {
            _ = completionTask.ContinueWith(
                task => ObserveQueueTaskResult(ticket, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // A scheduling/continuation failure is itself a failed attempt;
            // never let it strand the coordinator in Pending.
            FailPending(ticket, ex);
        }
    }

    private void ObserveQueueTaskResult(long ticket, Task completionTask)
    {
        if (!completionTask.IsFaulted && !completionTask.IsCanceled)
            return;

        var error = completionTask.IsCanceled
            ? new TaskCanceledException("Zone restoration completion was canceled.")
            : completionTask.Exception?.GetBaseException()
                ?? new InvalidOperationException("Zone restoration completion failed without an exception.");
        FailPending(ticket, error);
    }

    private void ReportFailureSafely(Exception error)
    {
        try
        {
            reportFailure(error);
        }
        catch
        {
            // Reporting is diagnostic only; it must not replace the failure or
            // make the state transition observable as a success.
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
