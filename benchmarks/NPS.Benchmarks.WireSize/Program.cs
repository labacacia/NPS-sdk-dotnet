// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

// Tier-2 MsgPack wire-size benchmark. Encodes representative IFrame fixtures
// with both Tier1JsonCodec and Tier2MsgPackCodec and reports the payload-byte
// reduction. Phase 2 exit criterion: Tier-2 ≤ JSON × 50 %.
//
// Run: dotnet run --project impl/dotnet/benchmarks/NPS.Benchmarks.WireSize
//
// The program prints a Markdown report to stdout and writes it to
// docs/benchmarks/wire-size.md when --emit is passed.

using System.Text;
using NPS.Benchmarks.WireSize;

var emit = args.Contains("--emit");
var report = Benchmark.Run();

Console.WriteLine(report);

if (emit)
{
    var repoRoot = FindRepoRoot();
    var outPath  = Path.Combine(repoRoot, "docs", "benchmarks", "wire-size.md");
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
