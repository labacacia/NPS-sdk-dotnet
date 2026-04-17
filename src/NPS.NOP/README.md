# LabAcacia.NPS.NOP

> **Neural Orchestration Protocol** frames, DAG validation, and multi-Agent task orchestration for the **NPS — Neural Protocol Suite**.
>
> [![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.NOP.svg)](https://www.nuget.org/packages/LabAcacia.NPS.NOP/)
> Target: `net10.0` · License: Apache-2.0 · Spec: [NPS-5 NOP v0.3](https://github.com/labacacia/nps/blob/main/spec/NPS-5-NOP.md)

NOP is the SMTP/MQ of NPS — how multi-Agent workloads get planned, delegated
and joined. This package provides the four NOP frames
(`TaskFrame` 0x40, `DelegateFrame` 0x41, `SyncFrame` 0x42, `AlignStreamFrame` 0x43),
a full `NopOrchestrator` with K-of-N aggregation, retry + exponential backoff,
condition evaluation, JSONPath input mapping, DAG validation (Kahn's algorithm),
and an SSRF-guarded callback validator.

## Install

```bash
dotnet add package LabAcacia.NPS.NOP
```

## Quick start — run a 3-node DAG

```csharp
using NPS.NOP;
using NPS.NOP.Extensions;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using Microsoft.Extensions.DependencyInjection;

services
    .AddNpsCore()
    .AddNop()                                     // registers NOP frames
    .AddNopOrchestrator(o =>
    {
        o.MaxConcurrentNodes = Environment.ProcessorCount * 2;
    })
    .AddSingleton<INopWorkerClient, MyHttpWorkerClient>();

var orchestrator = sp.GetRequiredService<INopOrchestrator>();

var dag = new TaskDag
{
    Nodes =
    [
        new DagNode { Id = "fetch",    Capability = "http:get"   },
        new DagNode { Id = "classify", Capability = "ai:classify" },
        new DagNode { Id = "route",    Capability = "router:pick" },
    ],
    Edges =
    [
        new DagEdge { From = "fetch",    To = "classify" },
        new DagEdge { From = "classify", To = "route",
                      Condition = "$.classify.score > 0.7" },
    ],
};

NopTaskResult result = await orchestrator.ExecuteAsync(new TaskFrame
{
    TaskId   = Guid.NewGuid().ToString("N"),
    Dag      = dag,
    Priority = TaskPriority.Normal,
});
```

The orchestrator enforces the NOP spec limits:
`MaxDagNodes = 32`, `MaxDelegateChainDepth = 3`, `MaxTimeoutMs = 3_600_000`,
`CallbackMaxRetries = 3`. Callback URLs are HTTPS-only and rejected if they
resolve to IPv4/IPv6 private ranges.

## Key types

| Type | Purpose |
|------|---------|
| `TaskFrame`, `DelegateFrame`, `SyncFrame`, `AlignStreamFrame` | NOP frame set |
| `TaskDag`, `DagNode`, `DagEdge`, `TaskContext` | DAG model + OTel trace context |
| `RetryPolicy`, `BackoffStrategy`, `AggregateStrategy` | Execution policy |
| `INopOrchestrator`, `NopOrchestrator`, `NopOrchestratorOptions` | Execution engine |
| `DagValidator`, `DagValidationResult` | Kahn's algorithm cycle detection + 8 structural checks |
| `NopConditionEvaluator`, `NopInputMapper`, `NopResultAggregator` | CEL subset, JSONPath, K-of-N aggregation |
| `NopCallbackValidator` | HTTPS + SSRF IP-range guard |
| `INopTaskStore`, `InMemoryNopTaskStore` | Task persistence |
| `NopErrorCodes` | `NOP-*` error code constants |

## Documentation

- **Full API reference:** [`doc/NPS.NOP.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/NPS.NOP.md)
- **SDK overview:** [`doc/overview.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/overview.md)
- **Spec:** [NPS-5 NOP](https://github.com/labacacia/nps/blob/main/spec/NPS-5-NOP.md)

## NPS Repositories

| Purpose | Repo |
|---------|------|
| Spec + Release notes | [NPS-Release](https://github.com/labacacia/NPS-Release) |
| .NET SDK (this package) | [NPS-sdk-dotnet](https://github.com/labacacia/NPS-sdk-dotnet) |
| Python SDK | [NPS-sdk-py](https://github.com/labacacia/NPS-sdk-py) |
| TypeScript SDK | [NPS-sdk-ts](https://github.com/labacacia/NPS-sdk-ts) |
| Java SDK | [NPS-sdk-java](https://github.com/labacacia/NPS-sdk-java) |
| Rust SDK | [NPS-sdk-rust](https://github.com/labacacia/NPS-sdk-rust) |
| Go SDK | [NPS-sdk-go](https://github.com/labacacia/NPS-sdk-go) |

## License

Apache 2.0 © LabAcacia / INNO LOTUS PTY LTD
