using DesktopMcp.Server.Cli;

// The CLI (spec §8) lives in DesktopMcp.Server so it can be tested; this exe is only the composition root.
return await CliApp.RunAsync(args);
