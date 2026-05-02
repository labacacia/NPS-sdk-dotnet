[English Version](./sdk-usage.md) | 中文版

# NPS .NET SDK — 使用指南

版权所有 2026 INNO LOTUS PTY LTD — 采用 Apache 2.0 授权

---

## 目录

- [安装](#安装)
- [快速开始](#快速开始)
  - [NCP — 帧编解码](#ncp--帧编解码)
  - [NWP — Memory Node](#nwp--memory-node)
  - [NIP — 身份认证](#nip--身份认证)
  - [NDP — 节点发现](#ndp--节点发现)
  - [NOP — 编排引擎](#nop--编排引擎)
- [API 参考摘要](#api-参考摘要)
- [配置说明](#配置说明)
- [测试](#测试)

---

## 安装

SDK 拆分为五个 NuGet 包，按需引用即可；每个包会自动依赖 `NPS.Core`。

```bash
# 核心帧类型与编解码（所有包的基础依赖）
dotnet add package NPS.Core --version 1.0.0-alpha.1

# 神经网络协议 — 查询/动作帧、Memory Node 中间件
dotnet add package NPS.NWP --version 1.0.0-alpha.1

# 神经身份协议 — 身份帧、密钥管理、CA 客户端
dotnet add package NPS.NIP --version 1.0.0-alpha.1

# 神经发现协议 — 公告/解析/图谱帧、内存注册表
dotnet add package NPS.NDP --version 1.0.0-alpha.1

# 神经编排协议 — 任务/委托/同步帧、DAG 编排器
dotnet add package NPS.NOP --version 1.0.0-alpha.1
```

若包尚未发布至 nuget.org，请添加私有 NuGet 源：

```xml
<!-- nuget.config -->
<configuration>
  <packageSources>
    <add key="labacacia" value="https://nuget.pkg.github.com/LabAcacia/index.json" />
  </packageSources>
</configuration>
```

---

## 快速开始

### NCP — 帧编解码

NCP 是 NPS 的线路格式基础。所有上层帧均通过 `NpsFrameCodec` 进行编解码。

**依赖注入配置（ASP.NET Core）：**

```csharp
builder.Services.AddNpsCore(opts =>
{
    opts.DefaultTier      = EncodingTier.MsgPack; // Tier-2，生产环境默认
    opts.AnchorTtlSeconds = 3600;                 // AnchorFrame 缓存 TTL
    opts.AllowPlaintext   = false;                // 生产环境必须使用 TLS
});
```

**编码帧：**

```csharp
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

// 从 DI 获取或直接构造
var codec = serviceProvider.GetRequiredService<NpsFrameCodec>();

// 构建 AnchorFrame（发布 Schema，NCP §5）
var anchor = new AnchorFrame
{
    AnchorId = "sha256:abc123...",
    Schema   = JsonDocument.Parse(@"{""type"":""object"",""properties"":{""id"":{""type"":""string""}}}").RootElement,
    Ttl      = 3600,
};

byte[] wire = codec.Encode(anchor);        // 默认使用 Tier-2 MsgPack
```

**解码帧：**

```csharp
IFrame frame = codec.Decode(wire);

if (frame is AnchorFrame received)
{
    Console.WriteLine($"AnchorId: {received.AnchorId}");
}
```

**切换至 Tier-1 JSON（开发/调试模式）：**

```csharp
builder.Services.AddNpsCore(opts =>
{
    opts.DefaultTier = EncodingTier.Json;
});
```

**AnchorFrame 缓存：**

```csharp
using NPS.Core.Caching;

var cache = serviceProvider.GetRequiredService<AnchorFrameCache>();
cache.Store(anchor);

if (cache.TryGet("sha256:abc123...", out var cached))
{
    Console.WriteLine($"缓存命中：TTL 剩余 = {cached.Ttl}s");
}
```

---

### NWP — Memory Node

NWP 通过 HTTP 或原生模式实现结构化数据查询和动作调用。`MemoryNodeMiddleware` 可将任意 ASP.NET Core 应用转换为合规的 NWP Memory Node。

**依赖注入配置：**

```csharp
using NPS.NWP.Extensions;
using NPS.NWP.MemoryNode;

builder.Services.AddNpsCore();
builder.Services.AddNwp(opts =>
{
    opts.Port               = 17433;  // NPS 统一端口
    opts.DefaultTokenBudget = 0;      // 0 = 不限制
    opts.MaxDepth           = 5;      // X-NWP-Depth 头部上限
    opts.DefaultLimit       = 20;     // 默认查询分页大小
});

// 注册数据提供者
builder.Services.AddSingleton<IMemoryNodeProvider, MyProductProvider>();
builder.Services.AddMemoryNode<MyProductProvider>(opts =>
{
    opts.PathPrefix = "/products";    // 暴露路径：/products/.nwm, /products/query 等
    opts.NodeNid    = "urn:nps:node:example.com:products";
});
```

**实现 IMemoryNodeProvider：**

```csharp
using NPS.NWP.MemoryNode;
using NPS.NWP.Frames;

public sealed class MyProductProvider : IMemoryNodeProvider
{
    public MemoryNodeSchema GetSchema() => new()
    {
        AnchorId = "sha256:products-v1",
        Fields   =
        [
            new MemoryNodeField { Name = "id",    Type = "string",  Required = true },
            new MemoryNodeField { Name = "name",  Type = "string",  Required = true },
            new MemoryNodeField { Name = "price", Type = "number",  Required = false },
        ],
    };

    public async Task<QueryResult> QueryAsync(QueryFrame query, CancellationToken ct)
    {
        // 应用 query.Filter, query.Fields, query.Limit, query.Cursor, query.Order
        var products = await FetchFromDatabase(query, ct);
        return new QueryResult
        {
            Records    = products,
            NextCursor = products.Count == query.Limit ? "cursor:next" : null,
        };
    }
}
```

**在请求管道中挂载中间件：**

```csharp
app.UseNpsMemoryNode(); // 挂载所有已注册的 Memory Node
```

**客户端发送 QueryFrame：**

```csharp
using NPS.NWP.Frames;
using System.Text.Json;

var query = new QueryFrame
{
    AnchorRef = "sha256:products-v1",
    Filter    = JsonDocument.Parse(@"{""price"":{""$lt"":100}}").RootElement,
    Fields    = ["id", "name", "price"],
    Limit     = 10,
    Order     = [new QueryOrderClause("price", "ASC")],
};

byte[] wire = codec.Encode(query);
// 将 wire 字节通过 HTTP 模式或原生 TCP 发送至 17433 端口
```

**发送 ActionFrame：**

```csharp
using NPS.NWP.Frames;

var action = new ActionFrame
{
    Action  = "restock",
    Payload = JsonDocument.Parse(@"{""product_id"":""p1"",""quantity"":50}").RootElement,
};

byte[] wire = codec.Encode(action);
```

---

### NIP — 身份认证

NIP 使用 Ed25519 提供 Agent 身份验证、证书签发和吊销功能。

**生成 Ed25519 密钥对并管理密钥：**

```csharp
using NPS.NIP.Crypto;

var keyManager = new NipKeyManager();

// 生成并持久化（AES-256-GCM 加密，PBKDF2-SHA256 密钥派生）
keyManager.Generate("/data/agent.key.enc", passphrase: Environment.GetEnvironmentVariable("KEY_PASS")!);

// 后续运行时加载
keyManager.Load("/data/agent.key.enc", passphrase: Environment.GetEnvironmentVariable("KEY_PASS")!);

// 导出 NPS 线路格式公钥："ed25519:<base64url-DER>"
string pubKey = keyManager.ExportPublicKeyString();
```

**使用 NipSigner 签名与验证：**

```csharp
using NPS.NIP.Crypto;

var signer = new NipSigner(keyManager);

byte[] payload   = System.Text.Encoding.UTF8.GetBytes("canonical-json-payload");
string signature = signer.Sign(payload);   // "ed25519:<base64url>"

bool valid = signer.Verify(payload, signature, pubKey);
```

**构建 IdentFrame（Agent 身份声明）：**

```csharp
using NPS.NIP.Frames;

var ident = new IdentFrame
{
    Nid          = "urn:nps:agent:ca.example.com:550e8400-e29b-41d4-a716-446655440000",
    PubKey       = pubKey,
    Capabilities = ["nwp:query", "nwp:stream"],
    Scope        = JsonDocument.Parse(@"{""nodes"":[""*""],""actions"":[]}").RootElement,
    IssuedBy     = "urn:nps:org:ca.example.com",
    IssuedAt     = DateTime.UtcNow.ToString("O"),
    ExpiresAt    = DateTime.UtcNow.AddDays(30).ToString("O"),
    Serial       = "0x0001",
    Signature    = signature,
};
```

**TrustFrame（跨 CA 信任链委托）：**

```csharp
using NPS.NIP.Frames;

var trust = new TrustFrame
{
    DelegatorNid = "urn:nps:org:ca-a.example.com",
    DelegateeNid = "urn:nps:org:ca-b.example.com",
    Capabilities = ["nwp:query"],
    ExpiresAt    = DateTime.UtcNow.AddDays(7).ToString("O"),
    Signature    = delegatorSignature,
};
```

**RevokeFrame（吊销证书）：**

```csharp
using NPS.NIP.Frames;

var revoke = new RevokeFrame
{
    Nid       = "urn:nps:agent:ca.example.com:550e8400-...",
    Reason    = "cessation_of_operation",
    RevokedAt = DateTime.UtcNow.ToString("O"),
    Serial    = "0x0001",
    Signature = caSignature,
};
```

---

### NDP — 节点发现

NDP 负责节点地址公告和解析（类比 DNS）。

**使用内存注册表：**

```csharp
using NPS.NDP.Registry;

// 线程安全的内存注册表；TTL 到期在读取时懒惰清除
var registry = new InMemoryNdpRegistry();
```

**AnnounceFrame — 广播节点地址：**

```csharp
using NPS.NDP.Frames;

var announce = new AnnounceFrame
{
    Nid       = "urn:nps:node:example.com:data-store-01",
    NodeType  = "memory",
    Addresses =
    [
        new NdpAddress { Host = "10.0.0.5", Port = 17433, Protocol = "nwp+tls" },
        new NdpAddress { Host = "api.example.com", Port = 17433, Protocol = "nwp+tls" },
    ],
    Capabilities = ["nwp:query", "nwp:stream", "vector_search"],
    Ttl          = 300,   // 秒；0 = 有序关闭（从注册表中清除）
};

registry.Announce(announce);
```

**ResolveFrame — 将 NID 解析为物理端点：**

```csharp
using NPS.NDP.Frames;

var resolve = new ResolveFrame
{
    Nid      = "urn:nps:node:example.com:data-store-01",
    Protocol = "nwp+tls",
};

// 注册表返回匹配的地址列表
IReadOnlyList<NdpResolveResult> results = registry.Resolve(resolve.Nid);
foreach (var r in results)
{
    Console.WriteLine($"{r.Host}:{r.Port} TTL={r.Ttl}s");
}
```

**GraphFrame — 全量图谱同步：**

```csharp
using NPS.NDP.Frames;

var graph = new GraphFrame
{
    Nodes = registry.GetAll().Select(a => new NdpGraphNode
    {
        Nid       = a.Nid,
        NodeType  = a.NodeType,
        Addresses = a.Addresses,
    }).ToList(),
};
```

---

### NOP — 编排引擎

NOP 执行多 Agent 任务 DAG。`NopOrchestrator` 通过 `INopWorkerClient` 将子任务分发给工作 Agent。

**依赖注入配置：**

```csharp
using NPS.NOP.Extensions;
using NPS.NOP.Orchestration;

// 注册工作客户端（通过 HTTP 将 DelegateFrame 发送给 Agent）
builder.Services.AddSingleton<INopWorkerClient, MyHttpWorkerClient>();
builder.Services.AddHttpClient();

// 注册编排器
builder.Services.AddNopOrchestrator(opts =>
{
    opts.MaxConcurrentNodes = 4;
    opts.DefaultTimeoutMs   = 30_000;
},
useInMemoryStore: true);  // false → 注册自定义 INopTaskStore
```

**定义 TaskFrame：**

```csharp
using NPS.NOP.Frames;
using NPS.NOP.Models;

var task = new TaskFrame
{
    TaskId     = Guid.NewGuid().ToString(),
    Priority   = TaskPriority.Normal,
    TimeoutMs  = 60_000,
    MaxRetries = 2,
    Preflight  = true,
    CallbackUrl = "https://my-service.example.com/nop/callback",  // 必须为 https://
    Dag        = new TaskDag
    {
        Nodes =
        [
            new DagNode
            {
                Id         = "fetch-data",
                AgentNid   = "urn:nps:agent:ca.example.com:fetcher-01",
                Action     = "fetch",
                Parameters = JsonDocument.Parse(@"{""url"":""https://data.example.com""}").RootElement,
            },
            new DagNode
            {
                Id         = "analyze",
                AgentNid   = "urn:nps:agent:ca.example.com:analyzer-01",
                Action     = "analyze",
                DependsOn  = ["fetch-data"],   // 等待 fetch-data 完成
            },
        ],
    },
    Context = new TaskContext
    {
        RequestId = Guid.NewGuid().ToString(),
    },
};
```

**执行任务：**

```csharp
var orchestrator = serviceProvider.GetRequiredService<INopOrchestrator>();

NopTaskResult result = await orchestrator.ExecuteAsync(task, CancellationToken.None);

if (result.Success)
{
    Console.WriteLine($"任务 {result.TaskId} 已完成。");
    foreach (var (nodeId, nodeResult) in result.NodeResults)
        Console.WriteLine($"  {nodeId}: {nodeResult.Status}");
}
else
{
    Console.WriteLine($"任务失败：[{result.ErrorCode}] {result.ErrorMessage}");
}
```

**实现 INopWorkerClient：**

```csharp
using NPS.NOP.Orchestration;
using NPS.NOP.Frames;

public sealed class MyHttpWorkerClient : INopWorkerClient
{
    private readonly HttpClient _http;

    public MyHttpWorkerClient(IHttpClientFactory factory)
        => _http = factory.CreateClient();

    public async Task<DelegateResult> DelegateAsync(
        DelegateFrame frame, CancellationToken ct)
    {
        // 编码并 POST 到 Agent 端点（17433 端口）
        var response = await _http.PostAsync(
            $"https://{frame.AgentHost}:17433/nop/delegate",
            new ByteArrayContent(codec.Encode(frame)), ct);

        return response.IsSuccessStatusCode
            ? DelegateResult.Ok(frame.SubTaskId)
            : DelegateResult.Fail(frame.SubTaskId, "NOP-DELEGATE-FAILED", "Worker error");
    }
}
```

**AlignStreamFrame — 定向多 Agent 状态同步：**

```csharp
using NPS.NOP.Frames;

var align = new AlignStreamFrame
{
    TaskId    = task.TaskId,
    AgentNid  = "urn:nps:agent:ca.example.com:coordinator-01",
    Payload   = JsonDocument.Parse(@"{""checkpoint"":42,""state"":""ok""}").RootElement,
    Sequence  = 1,
};
```

---

## API 参考摘要

### NPS.Core

| 类型 | 命名空间 | 描述 |
|------|---------|------|
| `NpsFrameCodec` | `NPS.Core.Codecs` | 使用 Tier-1（JSON）或 Tier-2（MsgPack）编解码帧 |
| `Tier1JsonCodec` | `NPS.Core.Codecs` | 原始 JSON 编解码（开发/调试） |
| `Tier2MsgPackCodec` | `NPS.Core.Codecs` | 原始 MsgPack 编解码（生产默认） |
| `AnchorFrameCache` | `NPS.Core.Caching` | 每连接/会话的 AnchorFrame 缓存，支持 TTL |
| `FrameRegistry` | `NPS.Core.Registry` | 将 `FrameType` 字节映射到具体帧类型 |
| `AnchorFrame` | `NPS.Core.Frames.Ncp` | Schema 锚点 — 建立全局 Schema 引用 |
| `DiffFrame` | `NPS.Core.Frames.Ncp` | 增量补丁 — 仅传输变更字段 |
| `StreamFrame` | `NPS.Core.Frames.Ncp` | 有序流式数据块，支持背压 |
| `CapsFrame` | `NPS.Core.Frames.Ncp` | 引用锚点的完整响应信封 |
| `HelloFrame` | `NPS.Core.Frames.Ncp` | 原生模式客户端握手（NPS-1 §4.6） |
| `ErrorFrame` | `NPS.Core.Frames.Ncp` | 统一错误帧，适用于所有协议层（0xFE） |
| `FrameType` | `NPS.Core.Frames` | 全部帧字节码枚举（NCP 0x01–0x06、NWP 0x10–0x11、NIP 0x20–0x22、NDP 0x30–0x32、NOP 0x40–0x43） |
| `EncodingTier` | `NPS.Core.Frames` | `Json`（Tier-1）或 `MsgPack`（Tier-2） |
| `NpsCoreOptions` | `NPS.Core.Extensions` | `AddNpsCore()` 的配置选项 |
| `NpsCoreServiceExtensions` | `NPS.Core.Extensions` | `AddNpsCore()` DI 注册扩展方法 |

### NPS.NWP

| 类型 | 命名空间 | 描述 |
|------|---------|------|
| `QueryFrame` | `NPS.NWP.Frames` | 结构化数据查询（0x10）；支持过滤 DSL、分页、向量搜索 |
| `ActionFrame` | `NPS.NWP.Frames` | 操作调用（0x11） |
| `MemoryNodeMiddleware` | `NPS.NWP.MemoryNode` | ASP.NET Core 中间件，暴露 /.nwm、/.schema、/query、/stream |
| `IMemoryNodeProvider` | `NPS.NWP.MemoryNode` | 实现此接口以提供 Schema 和查询结果 |
| `MemoryNodeOptions` | `NPS.NWP.MemoryNode` | PathPrefix、NodeNid、能力标志 |
| `MemoryNodeSchema` | `NPS.NWP.MemoryNode` | `GetSchema()` 返回的 Schema 定义 |
| `NwpOptions` | `NPS.NWP.Extensions` | Port、DefaultTokenBudget、MaxDepth、DefaultLimit |
| `NwpServiceExtensions` | `NPS.NWP.Extensions` | `AddNwp()`、`AddMemoryNode<T>()` DI 注册扩展 |

### NPS.NIP

| 类型 | 命名空间 | 描述 |
|------|---------|------|
| `IdentFrame` | `NPS.NIP.Frames` | Agent 身份声明 + 证书（0x20） |
| `TrustFrame` | `NPS.NIP.Frames` | 跨 CA 信任链委托（0x21） |
| `RevokeFrame` | `NPS.NIP.Frames` | 吊销 NID 或能力授权（0x22） |
| `NipKeyManager` | `NPS.NIP.Crypto` | Ed25519 密钥对生成，AES-256-GCM 加密持久化，PBKDF2-SHA256 密钥派生 |
| `NipSigner` | `NPS.NIP.Crypto` | Ed25519 签名与验证，输出 `ed25519:<base64url>` 格式签名 |

### NPS.NDP

| 类型 | 命名空间 | 描述 |
|------|---------|------|
| `AnnounceFrame` | `NPS.NDP.Frames` | 节点/Agent 能力广播（0x30） |
| `ResolveFrame` | `NPS.NDP.Frames` | 将 nwp:// 地址解析为物理端点（0x31） |
| `GraphFrame` | `NPS.NDP.Frames` | 节点图谱全量同步（0x32） |
| `InMemoryNdpRegistry` | `NPS.NDP.Registry` | 线程安全内存注册表；懒惰 TTL 清除 |
| `INdpRegistry` | `NPS.NDP.Registry` | 自定义注册表后端接口 |
| `NdpAddress` | `NPS.NDP.Frames` | 物理地址条目（host、port、protocol） |
| `NdpResolveResult` | `NPS.NDP.Frames` | 解析结果，含证书指纹和 TTL |
| `NdpGraphNode` | `NPS.NDP.Frames` | GraphFrame 中的单个节点条目 |

### NPS.NOP

| 类型 | 命名空间 | 描述 |
|------|---------|------|
| `TaskFrame` | `NPS.NOP.Frames` | 任务定义 + DAG 分发（0x40） |
| `DelegateFrame` | `NPS.NOP.Frames` | 向工作 Agent 委托子任务（0x41） |
| `SyncFrame` | `NPS.NOP.Frames` | 多 Agent 状态同步点（0x42） |
| `AlignStreamFrame` | `NPS.NOP.Frames` | 定向任务流；AlignFrame 的升级版本（0x43） |
| `NopOrchestrator` | `NPS.NOP.Orchestration` | 核心编排器；DAG 执行、重试、条件跳过、结果聚合 |
| `INopOrchestrator` | `NPS.NOP.Orchestration` | `ExecuteAsync()` 和 `CancelAsync()` 接口 |
| `INopWorkerClient` | `NPS.NOP.Orchestration` | 实现此接口以将 DelegateFrame 分发给工作 Agent |
| `INopTaskStore` | `NPS.NOP.Orchestration` | 任务持久化接口；默认：内存实现 |
| `NopOrchestratorOptions` | `NPS.NOP.Orchestration` | MaxConcurrentNodes、DefaultTimeoutMs、重试策略 |
| `NopServiceExtensions` | `NPS.NOP.Extensions` | `AddNopOrchestrator()` DI 注册扩展 |
| `NopConstants` | `NPS.NOP` | `MaxDelegateChainDepth` = 3，`MaxDagNodes` = 32 |

---

## 配置说明

### 默认值

| 配置项 | 默认值 | 描述 |
|--------|--------|------|
| 端口 | 17433 | NPS 统一端口（所有协议共用） |
| 编码格式 | MsgPack（Tier-2） | 相比 JSON 约节省 60% 体积 |
| AnchorFrame TTL | 3600 秒 | Schema 缓存有效期 |
| 最大帧载荷 | 65 535 字节（64 KiB） | EXT=0 模式；启用 `EnableExtendedFrameHeader` 可支持最大 4 GB |
| 最大查询条数 | 1000 条 | QueryFrame 规范上限 |
| 默认查询条数 | 20 条 | `QueryFrame.Limit` 未设置时的默认值 |
| NOP DAG 最大节点数 | 32 | `NopConstants.MaxDagNodes` |
| 最大委托链深度 | 3 层 | `NopConstants.MaxDelegateChainDepth` |
| 最大图谱遍历深度 | 5 层 | `X-NWP-Depth` 头部上限 |
| Agent 证书有效期 | 30 天 | CA 服务器默认值 |
| Node 证书有效期 | 90 天 | CA 服务器默认值 |

### 扩展帧头部

对于超过 64 KiB 的载荷，启用 8 字节扩展帧头部（EXT=1）：

```csharp
builder.Services.AddNpsCore(opts =>
{
    opts.EnableExtendedFrameHeader = true;
    opts.MaxFramePayload = 4_294_967_295; // 最大 4 GB
});
```

### Token Budget

通过 `X-NWP-Budget` HTTP 头部或 `NwpOptions` 配置每连接的 Token 预算限制：

```csharp
builder.Services.AddNwp(opts =>
{
    opts.DefaultTokenBudget = 100_000; // CGN 单位；0 = 不限制
});
```

---

## 测试

运行完整测试套件（429 个测试）：

```bash
cd /path/to/release/1.0.0-alpha.1
dotnet test NPS.sln --verbosity normal
```

运行特定项目：

```bash
dotnet test tests/NPS.Tests/NPS.Tests.csproj --verbosity normal
```

按类别过滤：

```bash
# 只运行 NWP 测试
dotnet test --filter "Category=NWP"

# 只运行集成测试
dotnet test --filter "Category=Integration"
```

生成覆盖率报告：

```bash
dotnet test NPS.sln \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults

# 生成 HTML 报告（需要 reportgenerator 工具）
reportgenerator \
  -reports:"./TestResults/**/coverage.cobertura.xml" \
  -targetdir:"./TestResults/html" \
  -reporttypes:Html
```

**覆盖率目标：**
- 整体：≥ 90%
- NPS.Core：≥ 95%
- NPS.NWP：≥ 90%
- NPS.NIP：≥ 90%
- NPS.NDP：≥ 90%
- NPS.NOP：≥ 90%

---

*版权所有 2026 INNO LOTUS PTY LTD — 采用 Apache License, Version 2.0 授权*
