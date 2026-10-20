namespace DesktopMcp.Server.Cli;

/// <summary>A bad option value; reported in click's usage-error shape with exit code 2.</summary>
internal sealed class CliUsageException(string message) : Exception(message);
