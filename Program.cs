using ShyFtp.Cli;
using ShyFtp.Configuration;

string? configPath = null;
string? startFolder = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" or "-c":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("Error: --config requires a path argument.");
                return 1;
            }
            configPath = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("""
ShyFTP - console FTP/FTPS sync client

Usage:
  shyftp [folder] [--config <path>]

  folder            Start in this local folder (defaults to the current directory)
  --config <path>   Use a specific config file (default: %APPDATA%\ShyFTP\config.json)
  -h, --help        Show this help

Inside the shell, type 'help' for the full command list.
""");
            return 0;
        default:
            if (!args[i].StartsWith('-') && startFolder == null)
                startFolder = args[i];
            break;
    }
}

ConfigStore store;
AppConfig config;
try
{
    store = new ConfigStore(configPath ?? ConfigStore.ResolveDefaultPath());
    config = store.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to load config: " + ex.Message);
    return 1;
}

Shell shell;
try
{
    shell = new Shell(config, store, startFolder);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to start: " + ex.Message);
    return 1;
}

using (shell)
{
    return shell.Run();
}