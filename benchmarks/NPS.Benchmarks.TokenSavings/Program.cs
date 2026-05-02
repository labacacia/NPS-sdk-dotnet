// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

// Token-savings benchmark: REST-style HTTP+JSON traffic vs NWP traffic for three
// representative Agent-Node interactions. CGN is counted per NPS-0 §token-budget
// using the ceil(UTF-8 bytes / 4) fallback — both protocols are measured with the
// same counter so the ratio is an apples-to-apples comparison.
//
// Run: dotnet run --project impl/dotnet/benchmarks/NPS.Benchmarks.TokenSavings
//
// The program prints a Markdown report to stdout and writes it to
// docs/benchmarks/token-savings.md when --emit is passed.

using System.Globalization;
using System.Text;
using NPS.Benchmarks.TokenSavings;

var emit = args.Contains("--emit");
var report = Benchmark.Run();

Console.WriteLine(report);

if (emit)
{
    var repoRoot = FindRepoRoot();
    var outPath  = Path.Combine(repoRoot, "docs", "benchmarks", "token-savings.md");
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, report, new UTF8Encoding(false));
    Console.Error.WriteLine($"Report written to {outPath}");
}

return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        dir = dir.Parent;
    return dir?.FullName
        ?? throw new InvalidOperationException("Could not locate repo root (CLAUDE.md).");
}
