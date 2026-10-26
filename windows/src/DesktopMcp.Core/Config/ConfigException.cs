namespace DesktopMcp.Core.Config;

/// <summary>A configuration problem; the message is printed as <c>Error: &lt;message&gt;</c> (upstream's ClickException).</summary>
public sealed class ConfigException(string message) : Exception(message);
