using DesktopMcp.Core.Scrape;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/scrape.py:20-73 without sampling (Decisions 17): use_dom and use_sampling are loose booleans.</summary>
public sealed class ScrapeTool(Tool protocolTool, ScrapeClient scrape) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var url = arguments.RequiredString("url");
        _ = arguments.OptionalString("query");          // used by sampling, which arrives in P5
        var useDom = arguments.LooseBool("use_dom", false);
        _ = arguments.LooseBool("use_sampling", true);  // P1 always returns the raw content (upstream's fallback)
        if (useDom)
        {
            // The browser DOM arrives with Snapshot (P2) and its DOM mode (P6); until then there is never a DOM node.
            return ToolResults.Text($"No DOM information found. Please open {url} in browser first.");
        }
        var content = await scrape.FetchMarkdownAsync(url, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text($"URL: {url}\nContent:\n{content}");
    }
}
