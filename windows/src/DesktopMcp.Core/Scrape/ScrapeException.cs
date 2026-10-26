namespace DesktopMcp.Core.Scrape;

/// <summary>A Scrape failure whose message the tool reports as <c>Error calling tool 'Scrape': ...</c>.</summary>
public sealed class ScrapeException(string message) : Exception(message);
