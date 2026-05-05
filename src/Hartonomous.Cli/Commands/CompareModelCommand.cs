using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Decomposers.Safetensors;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Compares two safetensors files tensor-by-tensor (relative Frobenius error).
/// No database access required.
/// </summary>
internal sealed class CompareModelCommand
{
    public static Command Build()
    {
        Option<string> origOpt = new("--original", "Original safetensors file");
        origOpt.IsRequired = true;
        Option<string> exportedOpt = new("--exported", "Substrate-exported safetensors file");
        exportedOpt.IsRequired = true;

        Command compare = new("compare-models",
            "Compare two safetensors files tensor-by-tensor (relative Frobenius error).");
        compare.AddOption(origOpt);
        compare.AddOption(exportedOpt);

        compare.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string origPath = ctx.ParseResult.GetValueForOption(origOpt)!;
            string expPath = ctx.ParseResult.GetValueForOption(exportedOpt)!;
            await CompareModelsAsync(origPath, expPath, CancellationToken.None);
        });

        return compare;
    }

    private static async Task CompareModelsAsync(string origPath, string expPath, CancellationToken ct)
    {
        List<SafetensorsTensorInfo> origInfos = SafetensorsReader.ReadHeader(origPath);
        List<SafetensorsTensorInfo> expInfos = SafetensorsReader.ReadHeader(expPath);

        Dictionary<string, SafetensorsTensorInfo> origMap = new(StringComparer.Ordinal);
        foreach (SafetensorsTensorInfo ti in origInfos)
        {
            origMap[ti.Name] = ti;
        }

        Dictionary<string, SafetensorsTensorInfo> expMap = new(StringComparer.Ordinal);
        foreach (SafetensorsTensorInfo ti in expInfos)
        {
            expMap[ti.Name] = ti;
        }

        Console.WriteLine($"Original: {origInfos.Count} tensors  |  Exported: {expInfos.Count} tensors");
        Console.WriteLine();

        int total = 0;
        int identical = 0;
        int allZero = 0;
        int matched = 0;
        double sumRelErr = 0;
        int sumRelN = 0;
        Dictionary<int, (int n, double sumRel)> byRank = new();

        foreach (string name in origMap.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!expMap.TryGetValue(name, out SafetensorsTensorInfo? expTi))
            {
                continue;
            }

            SafetensorsTensorInfo origTi = origMap[name];
            if (!origTi.Shape.SequenceEqual(expTi.Shape))
            {
                continue;
            }

            total++;

            double[] o = SafetensorsReader.ReadTensorAsDouble(origTi);
            double[] e = SafetensorsReader.ReadTensorAsDouble(expTi);
            if (o.Length != e.Length)
            {
                continue;
            }

            double normO = 0, normDiff = 0, expAbsMax = 0;
            for (int i = 0; i < o.Length; i++)
            {
                double d = o[i] - e[i];
                normO += o[i] * o[i];
                normDiff += d * d;
                double ea = Math.Abs(e[i]);
                if (ea > expAbsMax)
                {
                    expAbsMax = ea;
                }
            }
            normO = Math.Sqrt(normO);
            normDiff = Math.Sqrt(normDiff);

            bool isAllZero = expAbsMax == 0;
            double relErr = normO > 0 ? normDiff / normO : (normDiff == 0 ? 0 : double.PositiveInfinity);

            if (isAllZero)
            {
                allZero++;
            }
            else if (normDiff == 0) { identical++; matched++; }
            else { matched++; sumRelErr += relErr; sumRelN++; }

            int rank = origTi.Shape.Length;
            if (!byRank.TryGetValue(rank, out (int n, double sumRel) b))
            {
                b = (0, 0.0);
            }

            byRank[rank] = (b.n + 1, b.sumRel + (isAllZero ? 1.0 : relErr));

            if (total <= 30 || isAllZero || (relErr > 0.5 && relErr < double.PositiveInfinity))
            {
                string status = isAllZero ? "ZERO" : normDiff == 0 ? "EXACT" : $"rel_err={relErr:F4}";
                Console.WriteLine($"  [{rank}D shape={string.Join('x', origTi.Shape)}] {name}: {status}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"  total compared:   {total}");
        Console.WriteLine($"  exact match:      {identical}");
        Console.WriteLine($"  zero-filled:      {allZero}  (substrate had no content for these)");
        Console.WriteLine($"  partial reconstruction: {matched - identical}");
        if (sumRelN > 0)
        {
            Console.WriteLine($"  mean rel_err on partial: {sumRelErr / sumRelN:F4}");
        }

        Console.WriteLine();
        Console.WriteLine("=== By rank ===");
        foreach (KeyValuePair<int, (int n, double sumRel)> kv in byRank.OrderBy(k => k.Key))
        {
            Console.WriteLine($"  {kv.Key}D: {kv.Value.n} tensors, mean rel_err = {kv.Value.sumRel / kv.Value.n:F4}");
        }

        await Task.CompletedTask;
    }
}
