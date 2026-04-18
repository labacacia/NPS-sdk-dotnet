[English Version](./README.md) | 中文版

# NPS .NET SDK

[![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.Core)](https://www.nuget.org/packages/LabAcacia.NPS.Core)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

**Neural Protocol Suite (NPS)** 的 .NET 10 / C# 参考实现 —— 专为 AI Agent 与神经模型设计的完整互联网协议族。

---

## NPS 仓库导航

| 仓库 | 职责 | 语言 |
|------|------|------|
| [NPS-Release](https://github.com/labacacia/NPS-Release) | 协议规范（权威来源） | Markdown / YAML |
| **[NPS-sdk-dotnet](https://github.com/labacacia/NPS-sdk-dotnet)**（本仓库） | 参考实现 | C# / .NET 10 |
| [NPS-sdk-py](https://github.com/labacacia/NPS-sdk-py) | 异步 Python SDK | Python 3.11+ |
| [NPS-sdk-ts](https://github.com/labacacia/NPS-sdk-ts) | Node/浏览器 SDK | TypeScript |
| [NPS-sdk-java](https://github.com/labacacia/NPS-sdk-java) | JVM SDK | Java 21+ |
| [NPS-sdk-rust](https://github.com/labacacia/NPS-sdk-rust) | 异步 SDK | Rust stable |
| [NPS-sdk-go](https://github.com/labacacia/NPS-sdk-go) | Go SDK | Go 1.23+ |

---

## NuGet 包

所有包均基于 **.NET 10**，在 NuGet 以 `LabAcacia.NPS.*` 前缀发布。

| 包 | 版本 | 说明 |
|----|------|------|
| [`LabAcacia.NPS.Core`](https://www.nuget.org/packages/LabAcacia.NPS.Core) | 1.0.0-alpha.1 | NCP 帧类型、双层编解码（JSON / MsgPack）、AnchorFrame 缓存、帧注册表 |
| [`LabAcacia.NPS.NWP`](https://www.nuget.org/packages/LabAcacia.NPS.NWP) | 1.0.0-alpha.1 | NWM manifest、`QueryFrame` / `ActionFrame`、Memory Node 中间件（SQL Server / PostgreSQL） |
| [`LabAcacia.NPS.NIP`](https://www.nuget.org/packages/LabAcacia.NPS.NIP) | 1.0.0-alpha.1 | Ed25519 密钥管理、CA 签发 / 吊销 / OCSP / CRL、IdentFrame 验证 |
| [`LabAcacia.NPS.NDP`](https://www.nuget.org/packages/LabAcacia.NPS.NDP) | 1.0.0-alpha.1 | Announce / Resolve 帧、内存注册表、Ed25519 Announce 验证器 |
| [`LabAcacia.NPS.NOP`](https://www.nuget.org/packages/LabAcacia.NPS.NOP) | 1.0.0-alpha.1 | Task / Delegate / Sync / AlignStream 帧、DAG 校验器、编排引擎 |

## 仓库结构

```
NPS-sdk-dotnet/
├── doc/                # SDK 文档（见下）
├── src/
│   ├── NPS.Core/       # 帧编解码、缓存、注册表、异常
│   ├── NPS.NWP/        # Neural Web Protocol
│   ├── NPS.NIP/        # Neural Identity Protocol
│   ├── NPS.NDP/        # Neural Discovery Protocol
│   └── NPS.NOP/        # Neural Orchestration Protocol
├── tests/NPS.Tests/    # 5 个协议共 429 个测试
├── nip-ca-server/      # 独立 NIP CA Server（ASP.NET Core + SQLite + Docker）
└── NPS.sln
```

## 安装

```bash
dotnet add package LabAcacia.NPS.Core --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NWP  --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NIP  --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NDP  --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NOP  --version 1.0.0-alpha.1
```

## 快速开始

### 编解码 AnchorFrame

```csharp
using Microsoft.Extensions.DependencyInjection;
using NPS.Core.Codecs;
using NPS.Core.Caching;
using NPS.Core.Extensions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

var services = new ServiceCollection().AddNpsCore();
using var sp  = services.BuildServiceProvider();

var codec = sp.GetRequiredService<NpsFrameCodec>();
var cache = sp.GetRequiredService<AnchorFrameCache>();

var schema = new FrameSchema
{
    Fields = new[]
    {
        new SchemaField("id",    "uint64"),
        new SchemaField("price", "decimal", Semantic: "commerce.price.usd"),
    }
};

var anchorId = AnchorIdComputer.Compute(schema); // "sha256:..."
var frame    = new AnchorFrame { AnchorId = anchorId, Schema = schema, Ttl = 3600 };

byte[] wire  = codec.Encode(frame);                       // MsgPack，比 JSON 小约 60%
var    clone = (AnchorFrame)codec.Decode(wire);
cache.Set(clone);
```

### 运行 Memory Node（NWP）

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddNpsCore()
    .AddNpsNwp()
    .AddMemoryNode<OrdersProvider>(options =>
    {
        options.AnchorId = "sha256:...";
        options.DefaultLimit = 50;
    });

var app = builder.Build();
app.UseMemoryNode("/orders");
app.Run();
```

### 签发 NIP 证书（CA）

```csharp
var services = new ServiceCollection()
    .AddNpsCore()
    .AddNpsNip(o => o.CaName = "urn:nps:ca:labacacia.com")
    .AddSingleton<INipCaStore, PostgreSqlNipCaStore>()
    .BuildServiceProvider();

var ca   = services.GetRequiredService<NipCaService>();
var cert = await ca.IssueAsync(new NipCsr
{
    Subject  = "urn:nps:agent:example.com:worker-1",
    PubKey   = publicKey,
    ValidFor = TimeSpan.FromDays(90),
});
```

### 提交 NOP 任务 DAG

```csharp
var dag = new TaskDag
{
    Nodes =
    [
        new TaskNode { Id = "fetch",     Action = "data.fetch",      Agent = "urn:nps:node:data.example.com" },
        new TaskNode { Id = "summarise", Action = "llm.summarise",   Agent = "urn:nps:node:llm.example.com",
                       Inputs = new Dictionary<string, string> { ["text"] = "$fetch.output" } }
    ],
    Edges = [new TaskEdge { From = "fetch", To = "summarise" }]
};

var task   = new TaskFrame { TaskId = Guid.NewGuid().ToString(), Dag = dag };
var result = await orchestrator.SubmitAsync(task);
await orchestrator.WaitAsync(result.TaskId, timeout: TimeSpan.FromMinutes(2));
```

---

## 文档

覆盖所有公开类与方法的完整 API 文档：

| 文档 | 范围 |
|------|------|
| [doc/overview.cn.md](./doc/overview.cn.md) | SDK 架构、DI 模型、编码分层 |
| [doc/NPS.Core.cn.md](./doc/NPS.Core.cn.md) | 帧头、编解码、AnchorFrame 缓存、注册表、异常 |
| [doc/NPS.NWP.cn.md](./doc/NPS.NWP.cn.md) | NWM manifest、Query/Action 帧、Memory Node 中间件 |
| [doc/NPS.NIP.cn.md](./doc/NPS.NIP.cn.md) | 密钥管理、CA 服务、OCSP/CRL、Ident 验证 |
| [doc/NPS.NDP.cn.md](./doc/NPS.NDP.cn.md) | Announce/Resolve/Graph 帧、注册表、验证器 |
| [doc/NPS.NOP.cn.md](./doc/NPS.NOP.cn.md) | DAG 模型、校验器、编排引擎 |
| [doc/sdk-usage.cn.md](./doc/sdk-usage.cn.md) · [English](./doc/sdk-usage.md) | 端到端使用说明 |

---

## NIP CA Server

`nip-ca-server/` 目录下提供一个独立 NIP 证书颁发机构服务 —— 基于 ASP.NET Core，SQLite 存储，开箱即用的 Docker 部署。

```bash
cd nip-ca-server
docker-compose up
```

---

## 构建与测试

```bash
dotnet build NPS.sln
dotnet test                # 5 个协议共 429 个测试
```

覆盖率目标：**≥ 90%** 行覆盖率（当前：≥ 95%）。

---

## 协议

| 协议 | 类比 | 版本 |
|------|------|------|
| NCP — Neural Communication Protocol | Wire / 帧格式 | v0.4 |
| NWP — Neural Web Protocol | HTTP | v0.4 |
| NIP — Neural Identity Protocol | TLS / PKI | v0.2 |
| NDP — Neural Discovery Protocol | DNS | v0.2 |
| NOP — Neural Orchestration Protocol | SMTP / MQ | v0.3 |

协议依赖关系：`NCP ← NWP ← NIP ← NDP` / `NCP + NWP + NIP ← NOP`。

---

## 许可证

Apache 2.0 —— 详见 [LICENSE](./LICENSE) 与 [NOTICE](./NOTICE)。

Copyright 2026 INNO LOTUS PTY LTD
