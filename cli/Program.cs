using System;
using System.Threading.Tasks;

namespace RimPilot.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args) => CliCommands.RunAsync(args,
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Console.Out, Console.Error);
}
