namespace DesktopMcp.Core.Config;

/// <summary>upstream validate_idle_timeout (infrastructure/config.py:95-105), shared by TOML and the CLI.</summary>
public static class IdleTimeout
{
    public const string Message = "control.idle_timeout_seconds must be a finite positive number";

    public static double Validate(object? raw) => raw switch
    {
        long seconds when seconds > 0 => seconds,
        double seconds when double.IsFinite(seconds) && seconds > 0 => seconds,
        _ => throw new ConfigException(Message),
    };
}
