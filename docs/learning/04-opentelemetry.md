# Step 04 — OpenTelemetry

> **Topic:** traces, metrics and logs with OpenTelemetry, a custom `ActivitySource` and `Meter`, and W3C `traceparent` propagation between two APIs, all viewed in the Aspire dashboard.
> **Tag when approved:** `step-04-opentelemetry`

## Why this matters for this project
One customer order will pass through Gateway → Order → Catalog → Payment → Delivery, partly over HTTP and partly over RabbitMQ. When it's slow or fails you need to answer *which service, which call, how long, and what did it log*, for **that one order**. Logs on their own can't answer that across 10 processes. Distributed tracing can, as long as every service passes the trace context along. This step sets that up once in ServiceDefaults, so every future service gets it for free.

## What was added
| File | Role |
|---|---|
| [ServiceDefaults Extensions.cs](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/Extensions.cs) | `ConfigureOpenTelemetry()`: logs + metrics + tracing, ASP.NET Core / HttpClient / runtime instrumentation, the service's own source and meter, OTLP exporter. Called from `AddServiceDefaults()`. |
| [ServiceDefaults .csproj](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/FoodDelivery.ServiceDefaults.csproj) and [Directory.Packages.props](../../Directory.Packages.props) | + `OpenTelemetry.Extensions.Hosting`, `.Exporter.OpenTelemetryProtocol`, `.Instrumentation.AspNetCore`, `.Instrumentation.Http`, `.Instrumentation.Runtime` (all 1.21.0) |
| [KitchenTelemetry.cs](../../src/Sandbox/FoodDelivery.Sandbox.Kitchen/KitchenTelemetry.cs) | `ActivitySource`, `KitchenMetrics` (counter + histogram via `IMeterFactory`), source-generated log methods |
| [Kitchen Program.cs](../../src/Sandbox/FoodDelivery.Sandbox.Kitchen/Program.cs) | `/dish-of-the-day` cooks inside a custom `cook dish` span, records metrics, logs; `?burn=true` fails it. The response shows the received `traceparent`, the trace id, and the custom span id. |
| [Waiter Program.cs](../../src/Sandbox/FoodDelivery.Sandbox.Waiter/Program.cs) | **No tracing code**, only passes `?burn` on and returns its own trace id to compare. |
| [tools/http/sandbox.http](../../tools/http/sandbox.http) | + three Step 04 requests |

No new containers. The Aspire dashboard was already running and is the telemetry backend.

---

## Concepts

### 1. Three signals, one standard
| Signal | Question it answers | .NET API you write against | Cost |
|---|---|---|---|
| **Traces** | What happened in *this one request*, across services, and where did the time go? | `ActivitySource` → `Activity` | Per request (so it's sampled in production) |
| **Metrics** | How many / how fast / how many errors *over time*? | `Meter` → `Counter`, `Histogram`, ... | Fixed: aggregated in-process, exported every few seconds |
| **Logs** | What did the code say? | `ILogger` | Per log line |

**OpenTelemetry** is the vendor-neutral standard: an API, an SDK, semantic conventions (standard names such as `http.request.method`), and a wire protocol (**OTLP**). Your code emits telemetry once, and the backend is swappable: the Aspire dashboard here, Jaeger/Tempo/Grafana/Azure Monitor elsewhere.

**.NET twist:** you never write against OpenTelemetry types in application code. `Activity`, `ActivitySource`, `Meter` and `ILogger` are built into .NET (`System.Diagnostics`). The OTel SDK is a **listener** that subscribes to them. So [KitchenTelemetry.cs](../../src/Sandbox/FoodDelivery.Sandbox.Kitchen/KitchenTelemetry.cs) has no OpenTelemetry `using` at all, and libraries can be instrumented without depending on OTel.

### 2. Traces and spans
A **trace** is a tree of **spans**. A span has a name, start and end time, attributes (tags), a status (Unset / Ok / Error), events, and a parent. In .NET a span is called an **`Activity`**, for historical reasons.

`GET /order` produces one trace with four spans:
```
waiter   GET /order                         (server span, ASP.NET Core instrumentation)
└─ waiter   GET                             (client span, HttpClient instrumentation)
   └─ kitchen  GET /dish-of-the-day         (server span, continued from traceparent)
      └─ kitchen  cook dish                 (our custom span, ActivitySource)
```
Only the last one is our code. The other three come from **auto-instrumentation**: ASP.NET Core and HttpClient already create Activities, and `.AddAspNetCoreInstrumentation()` / `.AddHttpClientInstrumentation()` subscribe to them and add the standard attributes.

```csharp
using (var activity = KitchenTelemetry.ActivitySource.StartActivity("cook dish"))
{
    activity?.SetTag("kitchen.dish.name", dish);
    ...
    activity?.SetStatus(ActivityStatusCode.Error, "Dish burnt");
}
```
- The parent is implicit: `Activity.Current` (an `AsyncLocal`) is the server span, so the new span becomes its child.
- **`StartActivity` returns `null` when nobody is listening** (or when the sampler drops it with no parent). That's why you always write `activity?.`, and why an unsubscribed source costs almost nothing.

### 3. Subscribing by name: the silent-drop trap
```csharp
var serviceSourceName = builder.Environment.ApplicationName;   // "FoodDelivery.Sandbox.Kitchen"
tracing.AddSource(serviceSourceName);
metrics.AddMeter(serviceSourceName);
```
The SDK only records sources and meters it was told about, **by exact name**. Our convention: each service names its `ActivitySource` and `Meter` after its assembly, so ServiceDefaults can subscribe generically. Get the name wrong and nothing fails: the data just isn't there (experiment 2).

### 4. Context propagation: how two processes share one trace
When the Waiter calls the Kitchen, an HTTP header carries the context ([W3C Trace Context](https://www.w3.org/TR/trace-context/)):
```
traceparent: 00-0bd0df89ca72cf3831245f250873e9ca-ad31cc3e351f4495-03
             │  └──────── trace-id (16 bytes) ───┘ └─ parent span ─┘ └ flags
             version
```
- **trace-id** stays the same for the whole trace, in every service.
- **parent-id** is the span id of the caller's *client* span; the Kitchen's server span becomes its child.
- **flags:** bit `0x01` = *sampled* (record this trace), bit `0x02` = *random* (W3C Trace Context Level 2: the trace id is truly random). We observed `03` (sampled) and `02` (not sampled) on .NET 10.

Who writes and reads it:
- **Outgoing:** HttpClient injects the header. There are **two layers** here: .NET's own `DistributedContextPropagator` *and* OTel's HttpClient instrumentation, which injects with OTel's propagator (W3C TraceContext + Baggage by default). Experiment 1 shows you must turn off both before the header disappears.
- **Incoming:** ASP.NET Core reads `traceparent` and starts its server span as a child of it.

Messaging works the same way later: Wolverine puts the context into RabbitMQ message headers, so a trace can follow an order through the outbox, the broker and the consumers.

### 5. Metrics: counters, histograms, cardinality
```csharp
_dishesCooked = meter.CreateCounter<long>("kitchen.dishes.cooked", unit: "{dish}");
_cookDuration = meter.CreateHistogram<double>("kitchen.dish.cook.duration", unit: "s");
```
- **Counter:** only goes up; the backend derives a rate.
- **Histogram:** records a distribution in buckets, so you can read p50/p95/p99. Averages hide the slow tail that users actually feel.
- **Unit `s`:** OTel semantic conventions use seconds for durations (`http.server.request.duration` is in seconds too).
- **Tags must be low-cardinality.** Every distinct tag combination is a separate time series held in memory. `dish.name` (a handful of values) and `burnt` (2) are fine; `order.id` or `user.id` would grow without limit. That kind of detail belongs on **span** attributes, which are per request anyway.
- `IMeterFactory` (from DI) creates the `Meter`. It's the .NET 8+ recommendation: DI owns its lifetime, and tests can get isolated meters.

You also get built-in metrics for free: `http.server.request.duration`, `http.client.request.duration`, `kestrel.*`, and runtime `dotnet.gc.*`, `dotnet.thread_pool.*`, etc.

### 6. Logs, correlated with traces
`builder.Logging.AddOpenTelemetry(...)` sends `ILogger` output over OTLP as well. Each log record is stamped with the **current TraceId and SpanId**. `Cooked {Dish} in {ElapsedMs} ms` is logged while `cook dish` is `Activity.Current`, so in the dashboard that log line links straight to that span, and the trace view shows the logs written during it.

The `[LoggerMessage]` source generator turns `{Dish}` and `{ElapsedMs}` into **structured attributes** (filterable), and avoids formatting and boxing when the level is disabled.

### 7. Export: OTLP, configured by the AppHost
```csharp
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
```
The AppHost injects standard `OTEL_*` environment variables into every project: the endpoint (the dashboard's OTLP port), headers (the dashboard's OTLP API key), protocol, `OTEL_SERVICE_NAME` (`waiter`, `kitchen`), and short export intervals so data shows up quickly in development. Open a resource's details in the dashboard to see them all. `UseOtlpExporter()` reads them for all three signals. Run a service outside the AppHost and the variable is missing, so nothing is exported and nothing breaks.

The SDK also reads **standard OTel environment variables** such as `OTEL_TRACES_SAMPLER`. Experiment 3 changes sampling without touching any code.

---

## Trade-offs and alternatives
| Choice | Why here | Alternatives / when to switch |
|---|---|---|
| OTel SDK in-process, OTLP straight to the dashboard | Simplest for local development; no extra container (RAM). | **OpenTelemetry Collector** in between: batching, retries, tail sampling, scrubbing secrets, fan-out to several backends. Standard in production. |
| Aspire dashboard as the backend | Free, already running, one UI for all three signals. **Keeps data in memory only**, and it's lost on restart. | Jaeger or Grafana Tempo (traces), Prometheus + Grafana (metrics), Loki or Seq (logs). Heavier; maybe at step 57/58. |
| Code-based instrumentation | Explicit, compiles with the app, visible in a learning project. | **Zero-code auto-instrumentation** (the OTel .NET agent via environment variables, no code change). Handy for legacy apps, less control. |
| Head sampling (decided at the root) | Simple, built into the SDK, and the parent decision is propagated so a trace is all-or-nothing. | **Tail sampling** in a Collector: decide *after* the trace ends, e.g. keep 100% of errors and slow traces, 5% of the rest. Needs a Collector that holds full traces in memory. |
| One source and meter per service, named after the assembly | ServiceDefaults can subscribe generically. | One source per component (`FoodDelivery.Order.Saga`, …) when a service grows. Then subscribe with a wildcard like `AddSource("FoodDelivery.Order.*")`. |
| Metrics pushed over OTLP | Same pipe as traces and logs. | Prometheus **pull** (`/metrics` scrape endpoint): the classic for Kubernetes. |

---

## Break-it experiments
All were run while building this step; the JSON outputs below are real. What you see in the **dashboard** (span counts, red spans, metrics, log links) was not checked by script, so confirm it during review. Each experiment needs a code change, then a stop and restart of the AppHost. **Undo each change afterwards.**

The `/order` response is designed to make propagation visible without opening the dashboard:
```json
{ "fromKitchen": { "trace": {
      "receivedTraceparent": "00-0bd0df89ca72cf3831245f250873e9ca-ad31cc3e351f4495-03",
      "traceId": "0bd0df89ca72cf3831245f250873e9ca",
      "cookSpanId": "dca34b23d713789b" } },
  "waiterTraceId": "0bd0df89ca72cf3831245f250873e9ca" }
```
Same `traceId` on both sides = one trace. In the dashboard, **Traces** shows `waiter: GET /order` with 4 spans across 2 resources.

### 1. Drop propagation and watch the trace split in two
**1a.** In [Waiter Program.cs](../../src/Sandbox/FoodDelivery.Sandbox.Waiter/Program.cs), after `builder.AddServiceDefaults();`, add:
```csharp
DistributedContextPropagator.Current = DistributedContextPropagator.CreateNoOutputPropagator();
```
Restart, call `/order` → **still the same trace id, `receivedTraceparent` still present.** Turning off .NET's propagator isn't enough, because OTel's HttpClient instrumentation injects the header itself with OTel's propagator.

**1b.** Add a second line:
```csharp
OpenTelemetry.Sdk.SetDefaultTextMapPropagator(new OpenTelemetry.Context.Propagation.CompositeTextMapPropagator([]));
```
Restart, call `/order` →
```json
"trace": { "receivedTraceparent": null, "traceId": "c8d83a297373228458d4387ba2c607ea", ... },
"waiterTraceId": "e47cfb7d5081e51b0ad2f8442d2fa455"
```
Two different trace ids. In the dashboard you now get **two traces** per request: `waiter: GET /order` (2 spans) and `kitchen: GET /dish-of-the-day` (2 spans), with nothing linking them. That's what a missing propagator looks like in production: "the trace stops at service X". Common real causes: a proxy or gateway that strips headers, a message bus without context injection, or a hand-built `HttpClient` / raw socket that bypasses instrumentation.

### 2. Forget to register your ActivitySource
In [KitchenTelemetry.cs](../../src/Sandbox/FoodDelivery.Sandbox.Kitchen/KitchenTelemetry.cs), change `SourceName` to `"Kitchen.Cooking"`. Restart, call `/order` →
```json
"trace": { "receivedTraceparent": "00-9d36...-03", "traceId": "9d369d683267f75a5788baf72f887454", "cookSpanId": null }
```
**No error, no warning.** `StartActivity` returned `null` because no listener subscribed to that name. The trace has 3 spans instead of 4. The `Meter` uses the same constant, so `kitchen.dishes.cooked` and `kitchen.dish.cook.duration` also stop appearing under **Metrics → kitchen**; check that in the dashboard. (Fix: the name must match `AddSource`/`AddMeter`. In real services, an integration test that asserts a span is exported catches this.)

### 3. Sample 25% of traces, with no code change
In [AppHost.cs](../../src/AppHost/FoodDelivery.AppHost/AppHost.cs), on the `waiter` resource, add:
```csharp
.WithEnvironment("OTEL_TRACES_SAMPLER", "traceidratio")
.WithEnvironment("OTEL_TRACES_SAMPLER_ARG", "0.25")
```
Restart and send 40 requests from Git Bash, counting the flags byte of the `traceparent` the Kitchen received:
```bash
for i in $(seq 1 40); do curl -sk https://localhost:7240/order | grep -o '"receivedTraceparent":"[^"]*"' | grep -o -- '-0[0-9]"$'; done | sort | uniq -c
```
→ measured: `31 -02"` and `9 -03"`. **22.5% sampled** (it's probabilistic; you'll get roughly 25%). The dashboard shows about 1 in 4 `/order` traces.

What to notice:
- The **Kitchen wasn't configured at all**, yet it obeys. The default sampler is `ParentBased(AlwaysOn)`: it follows the parent's sampled flag, so a trace is kept or dropped *as a whole* and you never get half traces.
- Unsampled requests still return a `traceId` and even a `cookSpanId`. The Activity exists, so ids still propagate and logs still carry a TraceId, but it's **not recorded or exported** (`Activity.Recorded == false`).
- Metrics are **not** sampled by a trace sampler, so `http.server.request.duration` should still count all 40 requests; confirm it in the dashboard.

### 4. An error that crosses services
`GET https://localhost:7240/order?burn=true` → `500` from the Waiter (measured: three calls, three 500s).
- The Kitchen sets the `cook dish` span status to **Error** and returns 500. ASP.NET Core marks its server span as an error.
- The Waiter's `GetFromJsonAsync` throws on the 500, so the Waiter's client and server spans are errors too.
- In **Traces**, the trace is flagged with errors, and the span details show `http.response.status_code=500` plus an `error.type` attribute.
- In **Metrics → kitchen → kitchen.dishes.cooked**, filter by `kitchen.dish.burnt = true`.
- In **Structured logs**, `Burnt Dal Baati Churma after N ms` is a Warning, with a link to its trace.

### 5. Dashboard tour: logs ↔ traces ↔ metrics
Observation rather than breaking something:
1. **Structured logs:** find `Cooked Dal Baati Churma in N ms`. Open it: `Dish` and `ElapsedMs` are separate attributes, and there are TraceId and SpanId fields. Click the trace link to land on the trace, with the `cook dish` span selected.
2. **Traces:** open a `/order` trace. Look at the waterfall. The `cook dish` width matches the 50–300 ms delay. The gap between the Waiter's client span and the Kitchen's server span is network and TLS time.
3. **Metrics:** `kitchen` → `kitchen.dish.cook.duration` shows the histogram and percentiles. `http.server.request.duration` is free from ASP.NET Core. Also look at `dotnet.gc.collections` and `dotnet.process.memory.working_set`, which relate to the RAM budget.
4. Call the Kitchen **directly** (`GET https://localhost:7148/dish-of-the-day`): `receivedTraceparent` is `null`, so it becomes a new root trace of its own.

---

## Interview questions

**1. What's the difference between traces, metrics and logs, and when do you use each?**
Metrics are cheap aggregate numbers over time (rate, errors, latency percentiles): use them for dashboards and **alerts**. Traces show one request's path and timing across services: use them to find *where* a slow or failed request spent its time. Logs are detailed discrete events: use them for *why* (the exception, the business decision). A typical investigation: an alert fires on a metric → you open an example trace (an exemplar) → you read the logs correlated by TraceId.

**2. How does a trace survive a call from one service to another?**
Context propagation. The caller injects the current trace id, span id and sampled flag into the outgoing request (HTTP header `traceparent`, W3C Trace Context; message headers for queues). The callee extracts it and starts its span as a child, with the same trace id. If any hop doesn't inject or extract (a header-stripping proxy, a raw socket, a broker without context headers), the trace splits there.

**3. What is a span, and what makes a good one?**
A timed operation with a name, start/end, parent, attributes, status and events. Good spans have a **low-cardinality name** (`cook dish`, `GET /orders/{id}`, never with ids in it), detail in **attributes** following semantic conventions where they exist, an **Error status** set on failures, and cover a meaningful unit of work. Not every method: too many spans add cost and noise.

**4. Why does .NET use `Activity` and `ActivitySource` instead of OpenTelemetry's own API?**
.NET's tracing types predate OTel and are built into the runtime, so the OTel .NET API is implemented *on top of* them: `Activity` is the span, `ActivitySource` is the tracer. Libraries (ASP.NET Core, HttpClient, Npgsql) can emit spans with no dependency on OTel. The OTel SDK registers an `ActivityListener` for the source names you configure, and when there's no listener, `StartActivity` returns null at almost zero cost.

**5. Head vs tail sampling?**
**Head:** decided when the trace starts (e.g. 10% by trace id), and the decision travels in the `sampled` flag so all services agree. It's cheap and simple, but it drops errors as readily as successes. **Tail:** a Collector buffers complete traces and decides afterwards, e.g. keep all errors and anything slower than 2 s plus 5% of the rest. It keeps the interesting traces but costs memory and needs all spans of a trace to reach the same Collector. Metrics are never sampled, so rates and percentiles stay accurate either way.

**6. What is metric cardinality and why does it matter?**
The number of distinct tag-value combinations of a metric; each one is a separate time series stored in memory and in the backend. Adding `user_id` or `order_id` to a metric multiplies series without limit and can take down the metrics backend (or the bill). Keep metric tags bounded (status code, route template, dish category); put high-cardinality ids on spans and logs instead.

**7. Why use a histogram for latency instead of an average?**
An average hides the tail: 99 requests at 50 ms and one at 5 s average to 100 ms, which looks fine, while 1% of users wait 5 seconds. A histogram keeps the distribution, so you can alert on p95/p99. SLOs are usually stated as percentiles ("99% of `/orders` under 300 ms").

**8. What does the OpenTelemetry Collector add over exporting straight to a backend?**
A separate process (agent or gateway) that receives OTLP and does batching, retries and buffering when the backend is down, tail sampling, attribute processing (dropping PII, adding environment tags), and routing to one or more backends. Apps then only know "send OTLP to the Collector", so backends can change without redeploying services. We skip it locally to save RAM; it would arrive with a production setup.

**9. How do logs get correlated with traces in .NET?**
With OTel logging enabled, each `ILogger` record captures `Activity.Current`'s TraceId and SpanId at the moment it's written. The backend can then show "logs for this trace" and link a log line to its span. It works only if the log is written while the right Activity is current. Logging after `using (activity)` has ended attributes the line to the parent span instead.

---

## Review checklist
- [ ] Run the AppHost, call `GET /order` a few times, and find one trace with **4 spans across waiter and kitchen** in the dashboard.
- [ ] Find the `Cooked ...` structured log and jump from it to its trace.
- [ ] Find `kitchen.dishes.cooked` and `kitchen.dish.cook.duration` under Metrics.
- [ ] Experiments 1–4 reproduced (undo each change afterwards).
- [ ] You can explain: what `traceparent` contains, why 1a alone didn't split the trace, why a wrong source name fails silently, how the Kitchen followed the Waiter's sampling decision, and why `order.id` doesn't belong on a metric tag.
- [ ] Approved, then commit + tag `step-04-opentelemetry`.
