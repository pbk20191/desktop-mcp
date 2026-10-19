using DesktopMcp.Core.FileSystem;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// upstream tools/filesystem.py:26-82: paths are resolved first, then the strict booleans, then the mode. A blank
/// destination is missing (Decisions 11).
/// </summary>
public sealed class FileSystemTool(Tool protocolTool, FileSystemService fileSystem) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var path = fileSystem.ResolveArgument(arguments.RequiredString("path"));
        // An empty or blank destination counts as missing: upstream would resolve it to the server's working directory.
        var destination = arguments.OptionalString("destination");
        destination = string.IsNullOrWhiteSpace(destination) ? null : fileSystem.ResolveArgument(destination);
        var content = arguments.OptionalString("content");
        var pattern = arguments.OptionalString("pattern");
        var offset = arguments.OptionalInteger("offset");
        var limit = arguments.OptionalInteger("limit");
        var encoding = arguments.String("encoding", "utf-8");
        var recursive = arguments.StrictBool("recursive", false);
        var append = arguments.StrictBool("append", false);
        var overwrite = arguments.StrictBool("overwrite", false);
        var showHidden = arguments.StrictBool("show_hidden", false);

        var text = await Task.Run(() => mode switch
        {
            "read" => fileSystem.ReadFile(path, offset, limit, encoding),
            "write" => content is null ? "Error: content parameter is required for write mode." : fileSystem.WriteFile(path, content, append, encoding),
            "copy" => destination is null ? "Error: destination parameter is required for copy mode." : fileSystem.CopyPath(path, destination, overwrite, cancellationToken),
            "move" => destination is null ? "Error: destination parameter is required for move mode." : fileSystem.MovePath(path, destination, overwrite),
            "delete" => fileSystem.DeletePath(path, recursive),
            "list" => fileSystem.ListDirectory(path, pattern, recursive, showHidden, cancellationToken),
            "search" => pattern is null ? "Error: pattern parameter is required for search mode." : fileSystem.SearchFiles(path, pattern, recursive, cancellationToken),
            "info" => fileSystem.GetFileInfo(path),
            _ => $"Error: Unknown mode \"{mode}\". Use: read, write, copy, move, delete, list, search, info.",
        }, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text(text);
    }
}
