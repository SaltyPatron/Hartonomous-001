using System.CommandLine;

namespace Hartonomous.Cli.Commands.ExportModel;

/// <summary>
/// One sub-action of the <c>export-model</c> CLI command. Each action is its
/// own file implementing this contract; the command class dispatches.
///
/// <para>
/// Per the S3.L split: <c>ExportModelCommand.cs</c> shrinks to argument
/// parsing + action lookup. Each action (e.g. round-trip export, synthesis
/// export, recipe export) handles its own option-to-config conversion,
/// service composition, and execution body.
/// </para>
/// </summary>
public interface IExportAction
{
    /// <summary>The sub-command name.</summary>
    string Name { get; }

    /// <summary>The sub-command human description shown in <c>--help</c>.</summary>
    string Description { get; }

    /// <summary>
    /// Build the System.CommandLine <see cref="Command"/> wiring options +
    /// the SetHandler. The dispatcher attaches the returned command to the
    /// <c>export-model</c> parent.
    /// </summary>
    Command Build();
}
