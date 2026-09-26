using System.Diagnostics;
using System.Globalization;

namespace QuantAnalyst.Trading.Kill;

/// <summary>
/// <c>state/session.lock</c>: marks a running trading session (process id and start time), so a second session cannot
/// start and <c>qa kill --reset</c> cannot clear a kill underneath one. A lock whose process is gone is stale and is
/// taken over.
/// </summary>
public sealed class SessionLock : IDisposable
{
    public const string FileName = "session.lock";

    private readonly string _path;
    private bool _released;

    private SessionLock(string path) => _path = path;

    /// <summary>Takes the lock, or throws with the holder's details when another live process has it.</summary>
    public static SessionLock Acquire(string stateDirectory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, FileName);
        if (Holder(stateDirectory) is { } holder)
        {
            throw new InvalidOperationException($"A trading session is already running ({holder}). Stop it first.");
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId} {time.GetUtcNow():O}\n");
        File.WriteAllText(path, text);
        return new SessionLock(path);
    }

    /// <summary>"pid N since T" when a live process holds the lock, else null (no file, or a stale one).</summary>
    public static string? Holder(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        string[] parts = File.ReadAllText(path).Split(' ', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            return "an unreadable session.lock (delete it if no session runs)";
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : $"pid {pid} since {(parts.Length > 1 ? parts[1] : "?")}";
        }
        catch (ArgumentException)
        {
            return null; // not running: stale
        }
    }

    public void Dispose()
    {
        if (!_released)
        {
            _released = true;
            File.Delete(_path);
        }
    }
}
