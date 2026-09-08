using System;
using Microsoft.Win32;

namespace S880Tray;

internal sealed record StartupRegistrationState(bool Enabled, bool Owned, string? Error);

internal interface IStartupRegistryAdapter
{
    object? Read(string valueName);
    void Write(string valueName, string value);
    void Delete(string valueName);
}

internal sealed class WindowsStartupRegistryAdapter : IStartupRegistryAdapter
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public object? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Write(string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Windows startup settings could not be opened.");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

internal sealed class StartupRegistration
{
    internal const string ValueName = "S880Controller";
    private readonly IStartupRegistryAdapter _registry;
    private readonly Func<string?> _processPath;

    internal StartupRegistration(IStartupRegistryAdapter registry, Func<string?> processPath)
    {
        _registry = registry;
        _processPath = processPath;
    }

    internal static StartupRegistrationState Read() => CreateForCurrentProcess().ReadState();
    internal static StartupRegistrationState SetEnabled(bool enabled) => CreateForCurrentProcess().Update(enabled);

    private static StartupRegistration CreateForCurrentProcess() =>
        new(new WindowsStartupRegistryAdapter(), () => Environment.ProcessPath);

    internal StartupRegistrationState ReadState()
    {
        try
        {
            var expected = ExpectedCommand();
            var actual = _registry.Read(ValueName);
            if (actual is null) return new StartupRegistrationState(false, false, null);
            if (actual is not string text)
                return new StartupRegistrationState(false, true, "The existing startup entry has an unsupported value. Turn the switch on to replace this app's entry.");
            if (string.Equals(text, expected, StringComparison.OrdinalIgnoreCase))
                return new StartupRegistrationState(true, true, null);
            return new StartupRegistrationState(false, true, "The existing startup entry points to another controller location. Turn the switch on to update it.");
        }
        catch (Exception error)
        {
            return new StartupRegistrationState(false, false, "Windows startup status could not be read: " + error.Message);
        }
    }

    internal StartupRegistrationState Update(bool enabled)
    {
        try
        {
            var current = ReadState();
            if (current.Error is not null && !current.Owned)
                return current;
            if (enabled)
            {
                if (current.Enabled) return current;
                _registry.Write(ValueName, ExpectedCommand());
                var verified = ReadState();
                return verified.Enabled
                    ? verified
                    : verified with { Error = verified.Error ?? "Windows did not retain the startup setting." };
            }

            if (!current.Owned) return new StartupRegistrationState(false, false, null);
            _registry.Delete(ValueName);
            var removed = ReadState();
            return !removed.Owned
                ? removed
                : removed with { Error = removed.Error ?? "Windows did not remove the startup setting." };
        }
        catch (Exception error)
        {
            var after = ReadState();
            return after with { Error = "Windows startup setting could not be changed: " + error.Message };
        }
    }

    private string ExpectedCommand()
    {
        var path = _processPath();
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"'))
            throw new InvalidOperationException("The controller executable path is unavailable.");
        return "\"" + path + "\" --start-in-tray";
    }
}
