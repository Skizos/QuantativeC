using QuantAnalyst.Core;

namespace QuantAnalyst.Avanza.Credentials;

/// <summary>Avanza login material. Every value is a <see cref="Secret"/>: never logged, never persisted by us.</summary>
public sealed record AvanzaCredentials(Secret Username, Secret Password, Secret TotpSecret);

/// <summary>Missing or unreadable credentials. Messages name the store and the missing items, never values.</summary>
public sealed class SecretStoreException(string message) : Exception(message);

/// <summary>Source of the Avanza credentials (master plan §5: OS secret store first; env vars only for development).</summary>
public interface ISecretStore
{
    /// <summary>Human-readable store name for messages, e.g. "Windows Credential Manager".</summary>
    string Name { get; }

    /// <summary>Returns the credentials or throws <see cref="SecretStoreException"/>. Called once per login trigger.</summary>
    AvanzaCredentials GetAvanzaCredentials();
}

/// <summary>
/// Development fallback: <c>QA_AVANZA_USERNAME</c>, <c>QA_AVANZA_PASSWORD</c>, <c>QA_AVANZA_TOTP_SECRET</c>.
/// Selected explicitly (<c>--secret-store env</c>); never used in CI.
/// </summary>
public sealed class EnvironmentSecretStore(Func<string, string?>? getVariable = null) : ISecretStore
{
    public const string UsernameVariable = "QA_AVANZA_USERNAME";
    public const string PasswordVariable = "QA_AVANZA_PASSWORD";
    public const string TotpSecretVariable = "QA_AVANZA_TOTP_SECRET";

    private readonly Func<string, string?> _get = getVariable ?? Environment.GetEnvironmentVariable;

    public string Name => "environment variables";

    public AvanzaCredentials GetAvanzaCredentials()
    {
        string? user = _get(UsernameVariable);
        string? password = _get(PasswordVariable);
        string? totp = _get(TotpSecretVariable);
        string[] missing =
        [
            .. new[] { (UsernameVariable, user), (PasswordVariable, password), (TotpSecretVariable, totp) }
                .Where(v => string.IsNullOrEmpty(v.Item2))
                .Select(v => v.Item1),
        ];
        return missing.Length > 0
            ? throw new SecretStoreException($"Missing in {Name}: {string.Join(", ", missing)}.")
            : new AvanzaCredentials(new Secret(user!), new Secret(password!), new Secret(totp!));
    }
}
