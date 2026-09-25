using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantAnalyst.Avanza.Auth;

/// <summary>Persisted login state (<c>state/auth.json</c>, git-ignored). Contains no secrets.</summary>
internal sealed record AuthState
{
    public int Version { get; init; } = 1;

    public bool Locked { get; init; }

    public DateTimeOffset? LockedAtUtc { get; init; }

    public string? LockReason { get; init; }

    public DateTimeOffset? LastFailureUtc { get; init; }

    public string? LastFailureReason { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuthState))]
internal sealed partial class AuthStateContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="AuthState"/> atomically. A second recorded failure within 24 h locks login
/// (ADR 0002 §2): the program then refuses to try again until a human clears the lock.
/// </summary>
internal sealed class AuthStateStore(string stateDirectory, TimeProvider time)
{
    public static readonly TimeSpan LockWindow = TimeSpan.FromHours(24);

    public string FilePath { get; } = Path.Combine(stateDirectory, "auth.json");

    public AuthState Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AuthState();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), AuthStateContext.Default.AuthState) ?? new AuthState();
        }
        catch (JsonException)
        {
            // Unreadable state is treated as locked: fail safe, never as "no failures".
            return new AuthState { Locked = true, LockReason = $"{FilePath} is unreadable", LockedAtUtc = time.GetUtcNow() };
        }
    }

    /// <summary>Records a failed attempt; returns the new state (Locked when this is the second failure within 24 h).</summary>
    public AuthState RecordFailure(string reason, bool lockNow = false)
    {
        AuthState current = Load();
        DateTimeOffset now = time.GetUtcNow();
        bool secondFailure = current.LastFailureUtc is { } last && now - last < LockWindow;
        AuthState next = current with
        {
            LastFailureUtc = now,
            LastFailureReason = reason,
            Locked = current.Locked || lockNow || secondFailure,
            LockedAtUtc = current.Locked ? current.LockedAtUtc : (lockNow || secondFailure ? now : null),
            LockReason = current.Locked
                ? current.LockReason
                : lockNow ? reason : secondFailure ? $"second failed login within 24 h ({reason})" : null,
        };
        Save(next);
        return next;
    }

    public void RecordSuccess() =>
        Save(Load() with { LastFailureUtc = null, LastFailureReason = null, LastSuccessUtc = time.GetUtcNow() });

    public void ClearLock() =>
        Save(new AuthState { LastSuccessUtc = Load().LastSuccessUtc });

    private void Save(AuthState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, AuthStateContext.Default.AuthState));
        File.Move(temp, FilePath, overwrite: true);
    }
}
