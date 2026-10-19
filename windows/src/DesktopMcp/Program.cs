using DesktopMcp.Core;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// P0: stdio only. The CLI (serve/install/uninstall/auth) arrives in P1, HTTP in P5.
var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = ServerText.Name, Version = ServerText.Version };
        options.ServerInstructions = ServerText.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools(ToolRegistry.CreateAll(ToolSpecCatalog.LoadEmbedded()));

await builder.Build().RunAsync();
