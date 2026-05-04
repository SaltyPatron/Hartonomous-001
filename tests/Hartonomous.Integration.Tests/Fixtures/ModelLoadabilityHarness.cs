using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Hartonomous.Integration.Tests.Fixtures;

/// <summary>
/// Spawns a Python subprocess to run a tiny loadability check on a recomposed
/// model directory. Used by D-recompose-loadability gates — verifies the
/// output safetensors / config.json / tokenizer files are valid enough that
/// the upstream inference stack can load them.
///
/// The harness shells out to a small Python helper script that imports
/// transformers / diffusers / peft per the model class and runs one
/// inference token. Captures stderr; returns LoadabilityResult.
///
/// Python availability is gated: when no python3 is on PATH, the harness
/// returns Skipped=true and the test self-skips rather than fails.
/// </summary>
public static class ModelLoadabilityHarness
{
    public sealed record LoadabilityResult(bool Loaded, bool Skipped, string Detail);

    public static async Task<LoadabilityResult> CheckCausalLmAsync(string outputDir, TimeSpan timeout)
    {
        if (!HasPython())
        {
            return new LoadabilityResult(false, true, "python3 not found on PATH");
        }

        string scriptPath = WriteTempScript(@"
import sys, os
out = sys.argv[1]
try:
    from transformers import AutoModelForCausalLM, AutoTokenizer
    tokenizer = AutoTokenizer.from_pretrained(out)
    model = AutoModelForCausalLM.from_pretrained(out, torch_dtype='auto')
    inputs = tokenizer('hello', return_tensors='pt')
    _ = model(**inputs)
    print('LOADABILITY_OK')
except Exception as e:
    print('LOADABILITY_FAIL', repr(e), file=sys.stderr)
    sys.exit(1)
");

        return await RunHelperAsync(scriptPath, outputDir, timeout);
    }

    public static async Task<LoadabilityResult> CheckDiffusionPipelineAsync(string outputDir, TimeSpan timeout)
    {
        if (!HasPython())
        {
            return new LoadabilityResult(false, true, "python3 not found on PATH");
        }

        string scriptPath = WriteTempScript(@"
import sys
out = sys.argv[1]
try:
    from diffusers import DiffusionPipeline
    pipe = DiffusionPipeline.from_pretrained(out, torch_dtype='auto')
    print('LOADABILITY_OK')
except Exception as e:
    print('LOADABILITY_FAIL', repr(e), file=sys.stderr)
    sys.exit(1)
");

        return await RunHelperAsync(scriptPath, outputDir, timeout);
    }

    private static bool HasPython()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) { return false; }
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            foreach (string exe in new[] { "python.exe", "python3.exe", "python", "python3" })
            {
                if (File.Exists(Path.Combine(dir, exe))) { return true; }
            }
        }
        return false;
    }

    private static string WriteTempScript(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hartonomous_loadability_{Guid.NewGuid():N}.py");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<LoadabilityResult> RunHelperAsync(string scriptPath, string outputDir, TimeSpan timeout)
    {
        ProcessStartInfo psi = new("python", $"\"{scriptPath}\" \"{outputDir}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = new() { StartInfo = psi };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            return new LoadabilityResult(false, false, "loadability check timed out");
        }
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        try { File.Delete(scriptPath); } catch { /* best effort */ }

        if (process.ExitCode == 0 && stdout.Contains("LOADABILITY_OK", StringComparison.Ordinal))
        {
            return new LoadabilityResult(true, false, "loaded + one inference token");
        }
        return new LoadabilityResult(false, false, $"exit={process.ExitCode}; stderr={stderr}");
    }
}
