using System;
using System.IO;

namespace AnoMech.Core.Recording;

/// <summary>
/// Serializes recording lines and lifecycle operations. A failed operation faults this
/// session; callers must stop advertising it as an active recording and surface the error.
/// </summary>
internal sealed class RecordingWriter : IDisposable
{
    private readonly TextWriter writer;
    private readonly object gate = new();
    private bool disposed;
    private bool faulted;
    private int errorCount;
    private string? lastError;

    public RecordingWriter(TextWriter writer)
        => this.writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public bool IsHealthy
    {
        get
        {
            lock (gate) return !disposed && !faulted;
        }
    }

    public int ErrorCount
    {
        get
        {
            lock (gate) return errorCount;
        }
    }

    public string? LastError
    {
        get
        {
            lock (gate) return lastError;
        }
    }

    public bool TryWriteLine(string line)
    {
        if (line is null) throw new ArgumentNullException(nameof(line));
        lock (gate)
        {
            if (disposed || faulted) return false;
            try
            {
                writer.WriteLine(line);
                return true;
            }
            catch (Exception ex)
            {
                FaultLocked(ex);
                return false;
            }
        }
    }

    public bool TryFlush()
    {
        lock (gate)
        {
            if (disposed || faulted) return false;
            try
            {
                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                FaultLocked(ex);
                return false;
            }
        }
    }

    /// <summary>Flushes and releases the underlying writer; safe to call repeatedly.</summary>
    public bool Close()
    {
        lock (gate)
        {
            if (disposed) return errorCount == 0;
            try
            {
                if (!faulted) writer.Flush();
            }
            catch (Exception ex)
            {
                FaultLocked(ex);
            }
            try
            {
                writer.Dispose();
            }
            catch (Exception ex)
            {
                FaultLocked(ex);
            }
            finally
            {
                disposed = true;
            }
            return errorCount == 0;
        }
    }

    public void Dispose() => Close();

    private void FaultLocked(Exception ex)
    {
        faulted = true;
        errorCount++;
        lastError = $"{ex.GetType().Name}: {ex.Message}";
    }
}
