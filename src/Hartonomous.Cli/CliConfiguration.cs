using System;

namespace Hartonomous.Cli;

/// <summary>
/// Shared CLI configuration helpers used by both <c>Program.cs</c> and
/// individual command classes that still accept an explicit <c>--connection</c>
/// override (infrastructure commands: bootstrap, migrate, phases run).
/// </summary>
internal static class CliConfiguration
{
    internal static readonly string[] ConnAliases = ["--connection", "-c"];

    internal static string DefaultConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=/var/run/postgresql;Port=5432;Database=hartonomous;" +
           "Include Error Detail=true;" +
           "Minimum Pool Size=8;Maximum Pool Size=32;Multiplexing=true;" +
           "Command Timeout=600;" +
           "Application Name=hartonomous-cli;";
}
