using DesktopMcp.Core.Scrape;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/scrape.py:20-73: use_dom and use_sampling are loose booleans. With use_sampling, a client that declared
/// sampling summarises the page (P5 plan, Decisions 5); <paramref name="samplingEnabled"/> is false where the transport
/// cannot send the request (stateless Streamable HTTP). Any sampling failure returns the raw content, as upstream.
/// </summary>
public sealed class ScrapeTool(Tool protocolTool, IWebScraper scraper, bool samplingEnabled) : SpecTool(protocolTool)
{
    /// <summary>upstream's system prompt (scrape.py:62-65) before the query's focus sentence.</summary>
    public const string SystemPrompt =
        "You are a web content extractor. Given raw webpage content, extract and present only the meaningful information in clean, " +
        "concise prose or structured format. Strip out navigation menus, cookie banners, ads, footer links, and all other boilerplate. " +
        "Preserve important data, facts, and structure.";

    /// <summary>upstream's max_tokens.</summary>
    internal const int MaxTokens = 2048;

    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var url = arguments.RequiredString("url");
        var query = arguments.OptionalString("query");
        var useDom = arguments.LooseBool("use_dom", false);
        var useSampling = arguments.LooseBool("use_sampling", true);
        if (useDom)
        {
            // This port has no browser DOM mode (P2 Decisions 39), so there is never a DOM node: upstream's no-DOM text.
            return ToolResults.Text($"No DOM information found. Please open {url} in browser first.");
        }
        var content = await scraper.FetchMarkdownAsync(url, cancellationToken).ConfigureAwait(false);
        if (useSampling && samplingEnabled && McpSampling.ClientCanSample(request.Server))
        {
            var focus = query is { Length: > 0 } ? $" Focus specifically on: {query}." : "";
            var summary = await McpSampling.TrySampleTextAsync(request.Server, SystemPrompt + focus,
                $"Raw scraped content from {url}:\n\n{content}", MaxTokens, cancellationToken).ConfigureAwait(false);
            if (summary is not null) return ToolResults.Text($"URL: {url}\nContent:\n{summary}");
        }
        return ToolResults.Text($"URL: {url}\nContent:\n{content}");
    }
}
