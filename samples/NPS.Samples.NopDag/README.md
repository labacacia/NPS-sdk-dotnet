English | [中文版](./README.cn.md)

# NPS Samples — NOP 3-Node DAG Demo

Phase 2 exit criterion: *"NOP Orchestrator can execute a 3-node DAG task end-to-end."*

This sample spins up **three independent NWP Action Nodes** on loopback ports
in one process, wires them together with an NOP `TaskFrame` containing a
linear DAG, and lets the `NopOrchestrator` execute the pipeline over **real
HTTP**. Output is a Markdown digest printed to stdout.

```
┌──────────────┐      ┌────────────────┐      ┌──────────────────┐
│ content.     │ ───► │ content.       │ ───► │ content.         │
│ fetch_       │      │ summarize      │      │ publish_digest   │
│ articles     │      │                │      │                  │
│ :17441       │      │ :17442         │      │ :17443           │
└──────────────┘      └────────────────┘      └──────────────────┘
     Agent #1               Agent #2                 Agent #3
```

## What it demonstrates

1. Three independent **NWP Action Nodes** (`IActionNodeProvider`) each hosting
   a single action on its own Kestrel port.
2. An **NOP TaskFrame** whose DAG has three nodes connected by input-mapping
   edges (`$.fetch.articles` → summarize, `$.summarize.summaries` → publish).
3. A minimal **`INopWorkerClient` HTTP implementation** (`HttpNopWorkerClient`)
   that maps the DAG's `agent` NID to the Action Node's base URL and dispatches
   `DelegateFrame` → `POST /invoke`.
4. End-to-end data flow: fetch emits an articles list, summarize transforms
   it, publish renders a Markdown digest that references both upstream outputs.

## Run it

```bash
dotnet run --project impl/dotnet/samples/NPS.Samples.NopDag
```

Expected output (abbreviated):

```
═══ NOP 3-node DAG demo ═══
  node 1 (urn:nps:agent:demo:fetch)     → http://127.0.0.1:17441/invoke
  node 2 (urn:nps:agent:demo:summarize) → http://127.0.0.1:17442/invoke
  node 3 (urn:nps:agent:demo:publish)   → http://127.0.0.1:17443/invoke

► Submitting task 205b06a8-…
◄ Final state: Completed
  Completed 3 / 3 nodes

══════ Published digest ══════
# Daily digest — NPS protocol suite
- Why Agents need a Schema-first Protocol — NWP treats schema as a …
- NPS Token Budget: a tokenizer-agnostic cost model — Counting in NPT …
- Three Node Types, one Port — Memory/Action/Complex nodes multiplex …
```

## Layout

```
samples/NPS.Samples.NopDag/
├── Program.cs                        # entry point: hosts + orchestrator
├── NodeHostBuilder.cs                # builds one Kestrel WebApplication per node
├── Providers/
│   ├── FetchArticlesProvider.cs      # node 1: returns hardcoded article list
│   ├── SummarizeProvider.cs          # node 2: articles → summaries
│   └── PublishDigestProvider.cs      # node 3: summaries → Markdown digest
└── Orchestration/
    └── HttpNopWorkerClient.cs        # INopWorkerClient → POST /invoke
```

## Regression guard

`impl/dotnet/tests/NPS.Tests/Samples/NopDagDemoTests.cs` runs the same pipeline
in-process with `TestServer` to keep the demo deterministically green in CI.

## Limitations

- **Happy path only.** The worker client doesn't speak the full NPS-5 §3.4
  AlignStream protocol — it synthesises a single terminal frame from the
  CapsFrame response. Streaming, cancellation, and preflight are not
  exercised.
- **No NIP.** Agent NIDs are bare identifiers; there is no certificate chain
  or capability validation.
- **Linear DAG.** The fixture is three nodes in a straight line. The NOP
  orchestrator and its existing test suite cover diamonds, K-of-N, retries,
  and condition skips separately.
