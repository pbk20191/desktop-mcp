namespace DesktopMcp.Core.Scrape;

/// <summary>Fetches a page as Markdown: <see cref="ScrapeClient"/> in production, a fake in tests (no network).</summary>
public interface IWebScraper
{
    /// <summary>The page at <paramref name="url"/> as Markdown; failures throw <see cref="ScrapeException"/>.</summary>
    Task<string> FetchMarkdownAsync(string url, CancellationToken cancellationToken);
}
