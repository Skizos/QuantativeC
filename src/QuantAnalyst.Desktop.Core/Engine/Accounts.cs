using QuantAnalyst.Cli.Commands;

namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>Where the Accounts page gets your accounts: one Avanza login per load (tests pass a fake).</summary>
public interface IAccountSource
{
    Task<AccountOverview> LoadAsync(string login);
}

/// <summary>The real source: the CLI's own account reads, run in-process by the engine (BankID QR in the window).</summary>
public sealed class EngineAccountSource(QaEngine engine, Workspace workspace) : IAccountSource
{
    public Task<AccountOverview> LoadAsync(string login) =>
        engine.QueryAsync("Load my Avanza accounts", services => AccountOverview.LoadAsync(services, workspace.StateDir, login));
}

/// <summary>Your user environment variables (the live-trading account, R1, is one); tests pass a fake.</summary>
public interface IUserEnvironment
{
    string? Read(string name);

    /// <summary>Sets (or with null clears) a variable for you and for this app's own commands.</summary>
    void Write(string name, string? value);
}

/// <summary>
/// The real user environment: on Windows a variable is stored for your user (new terminals see it, as with
/// <c>[Environment]::SetEnvironmentVariable(…, 'User')</c>) and set in this process, so the app's commands see it at once.
/// </summary>
public sealed class UserEnvironment : IUserEnvironment
{
    public string? Read(string name) => Environment.GetEnvironmentVariable(name);

    public void Write(string name, string? value)
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        }

        Environment.SetEnvironmentVariable(name, value);
    }
}
