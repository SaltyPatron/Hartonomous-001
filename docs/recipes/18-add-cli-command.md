# Recipe 18: Add a CLI Command

Intent: add a new CLI command (e.g., `infer`, `ingest-{kebab-source}`, `governance-simulate`) that drives substrate operations from the command line.

CLI commands are wired through `System.CommandLine` and exposed as PowerShell scripts in `scripts/`. Library code is invoked through DI; no business logic lives in the command class.

---

## Prerequisites

- Decide what underlying engine call the command makes (e.g., `IDecomposer.DecomposeAsync`, `IInferenceEngine.InferAsync`).
- All dependencies are registered in DI (see `Program.cs` composition root).
- The command's name is kebab-case (`ingest-tatoeba`, `governance-simulate`).

---

## Steps

### 1. Create the command class

`src/Hartonomous.Cli/Commands/{Pascal}Command.cs`:

```csharp
namespace Hartonomous.Cli.Commands;

public sealed class {Pascal}Command : Command
{
    public {Pascal}Command() : base("{kebab-name}", "{Description shown in --help}")
    {
        var pathOption = new Option<string>("--path", "Path to input file") { IsRequired = true };
        var connectionOption = new Option<string?>("--connection-string", "Override the default connection string");

        AddOption(pathOption);
        AddOption(connectionOption);

        this.SetHandler(InvokeAsync, pathOption, connectionOption);
    }

    private static async Task<int> InvokeAsync(string path, string? connectionString)
    {
        using var host = HostBuilderFactory.Build(connectionString);
        await host.StartAsync();

        var decomposer = host.Services.GetRequiredService<{Pascal}Decomposer>();
        var reporter = host.Services.GetRequiredService<IProgressReporter>();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await decomposer.ValidateSourceAsync(cts.Token);
            await decomposer.DecomposeAsync(reporter, cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            host.Services.GetRequiredService<ILogger<{Pascal}Command>>()
                .LogError(ex, "{Command} failed", "{kebab-name}");
            return 1;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
```

Rules:
- `IsRequired = true` on options that have no sensible default.
- Handler is `private static async Task<int>`. Return codes: `0` success, `1` failure, `130` cancelled.
- `Console.WriteLine` is allowed in the CLI (anti-pattern AP-CS-7 exempts `Hartonomous.Cli`); for structured output, prefer `_logger.LogInformation`.
- DI host is created per command; no static state.

### 2. Register the command in `Program.cs`

`src/Hartonomous.Cli/Program.cs`:

```csharp
var root = new RootCommand("Hartonomous CLI");
root.AddCommand(new IngestUcdCommand());
root.AddCommand(new IngestWordnetCommand());
// ... existing
root.AddCommand(new {Pascal}Command());

return await root.InvokeAsync(args);
```

### 3. Add the PowerShell entrypoint

`scripts/{folder}/{Verb}.ps1` — pick the folder by domain (`seed/`, `ops/`, `db/`, etc.):

```powershell
param(
    [string]$Path,
    [string]$ConnectionString = $env:HARTONOMOUS_DB
)

$ErrorActionPreference = 'Stop'
$cliProject = Join-Path $PSScriptRoot '../../src/Hartonomous.Cli'

$args = @('{kebab-name}', '--path', $Path)
if ($ConnectionString) { $args += @('--connection-string', $ConnectionString) }

& dotnet run --project $cliProject -- @args
```

Make the script execution-policy-friendly:

```powershell
$ErrorActionPreference = 'Stop'
```

### 4. Add tests

`tests/Hartonomous.Cli.Tests/Commands/{Pascal}CommandTests.cs`:

```csharp
public class {Pascal}CommandTests
{
    [Fact]
    public async Task Invoke_MissingRequiredOption_ReturnsNonZero()
    {
        var command = new {Pascal}Command();
        var result = await command.InvokeAsync(new[] { /* no --path */ });
        result.Should().NotBe(0);
    }
}
```

For end-to-end coverage, an integration test invokes the CLI in-process (via `IntegrationTestBase.RunCliAsync`) — see recipe `17`.

### 5. Document

`docs/specs/csharp/api-layer.md` and a CLI-commands inventory section: add the command with its kebab name, description, options, exit codes.

### 6. Run and verify

```pwsh
pwsh scripts/build/Dotnet.ps1
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}CommandTests
pwsh scripts/{folder}/{Verb}.ps1 -Path some/test/file
```

---

## Composing engine calls

For commands that need to chain operations (e.g., decompose → score → recompose), do the chaining in the command handler. The chain itself is short and visible:

```csharp
private static async Task<int> InvokeAsync(string query, int maxDepth, string? connectionString)
{
    using var host = HostBuilderFactory.Build(connectionString);
    await host.StartAsync();

    var engine = host.Services.GetRequiredService<IInferenceEngine>();
    var recomposer = host.Services.GetRequiredService<ITextRecomposer>();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var inference = await engine.InferAsync(
        new InferenceQuery { Text = query, MaxDepth = maxDepth, MaxResults = 20 },
        cts.Token);

    if (inference.Paths.Count == 0)
    {
        Console.WriteLine("No paths found.");
        return 2;
    }

    var output = await recomposer.RecomposeAsync(inference.Paths[0].Endpoint, RecompositionOptions.Default, cts.Token);
    Console.WriteLine(output);
    return 0;
}
```

If the chain grows beyond ~5 lines or starts containing decision logic, lift it into a service in `Hartonomous.Engine.Orchestration` and call that service from the command. The command stays thin.

---

## Anti-patterns

- **DON'T** put business logic in the command class. The command parses args, builds the host, calls one or two service methods, returns.
- **DON'T** hardcode connection strings. The `--connection-string` option overrides; otherwise `HARTONOMOUS_DB` env var; otherwise `DefaultConnectionString()` (CLI-only fallback).
- **DON'T** invoke `dotnet` directly in docs or other scripts. Always go through the PowerShell entrypoint.
- **DON'T** use a static `Main` to do work. The handler does the work; `Main` only wires `RootCommand`.
- **DON'T** swallow exceptions in the CLI without logging them. The user needs the error message AND a non-zero exit code.

---

## Verification checklist

- [ ] Command class at `src/Hartonomous.Cli/Commands/{Pascal}Command.cs`, one type
- [ ] Registered in `Program.cs`
- [ ] PowerShell entrypoint at `scripts/{folder}/{Verb}.ps1`
- [ ] Required options marked `IsRequired = true`
- [ ] Handler returns proper exit codes (0 / 1 / 130)
- [ ] Tests cover at least the missing-required-arg case
- [ ] Documented in CLI inventory

---

## Related recipes

- `08-add-decomposer.md` — for ingest-* commands
- `10-add-recomposer.md` — for commands that emit output
- `19-add-phase.md` — for commands that introduce a new orchestration phase
