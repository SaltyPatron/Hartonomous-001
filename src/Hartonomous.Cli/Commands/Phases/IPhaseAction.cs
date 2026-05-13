using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Cli.Commands.Phases;

/// <summary>
/// One sub-action of the <c>phases</c> CLI command (list, status, run, plan,
/// reset, ...). Each action is its own file implementing this contract; the
/// command class is a thin dispatcher that wires options and routes to the
/// action's <see cref="ExecuteAsync"/>.
///
/// <para>
/// Per the S3.L split: <c>PhasesCommand.cs</c> shrinks to argument parsing
/// + action lookup. Each action handles its own option-to-config conversion,
/// service composition, and execution body. Adding a new sub-action means
/// adding a file, not bloating the command class.
/// </para>
/// </summary>
public interface IPhaseAction
{
    /// <summary>The sub-command name (e.g. <c>"list"</c>, <c>"run"</c>).</summary>
    string Name { get; }

    /// <summary>The sub-command human description shown in <c>--help</c>.</summary>
    string Description { get; }

    /// <summary>
    /// Build the System.CommandLine <see cref="Command"/> wiring options +
    /// the SetHandler that ultimately calls <see cref="ExecuteAsync"/>. The
    /// dispatcher attaches the returned command to the <c>phases</c> parent.
    /// </summary>
    Command Build();
}
