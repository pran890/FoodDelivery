# Step 05 — Health and resilience

> **Topic:** liveness vs readiness health checks, and the HTTP resilience pipeline (timeout, retry, circuit breaker) between services.
> **Tag when approved:** `step-05-health-and-resilience`

> **Since Step 12** the Sandbox (Kitchen and Waiter) is gone. Its links below lead to this step's tag, and to run its experiments check out `step-05-health-and-resilience`.

## Why this matters for this project
Distributed systems fail **partially**: Postgres restarts, Catalog is slow for 2 seconds, a network blip drops one request. Step 31 has Order calling Catalog over HTTP to re-check prices at checkout. Without this step:
- One blip becomes a failed checkout.
- A Catalog outage turns into piled-up threads in Order (a **cascading failure**).
- Nobody can tell that a "Running" service can't reach its database.

This step adds the two standard answers once, in ServiceDefaults: **health checks**, so the platform knows what's broken, and a **resilience pipeline**, so callers survive what's broken.

## What was added
| File | Role |
|---|---|
| [ServiceDefaults Extensions.cs](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/Extensions.cs) | `AddDefaultHealthChecks()` ("self", tagged `live`), `MapDefaultEndpoints()` (`/health` readiness, `/alive` liveness, Development only, JSON output), `AddStandardResilienceHandler()` on every HttpClient, health paths filtered out of traces |
| [ServiceDefaults .csproj](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/FoodDelivery.ServiceDefaults.csproj) and [Directory.Packages.props](../../Directory.Packages.props) | + `Microsoft.Extensions.Http.Resilience` 10.11.1 (Polly v8 underneath) |
| [KitchenHealthChecks.cs](https://github.com/pran890/FoodDelivery/blob/step-05-health-and-resilience/src/Sandbox/FoodDelivery.Sandbox.Kitchen/KitchenHealthChecks.cs) | Hand-written `PostgresHealthCheck` (`SELECT 1`) and `RedisHealthCheck` (`PING`) |
| [Chaos.cs](https://github.com/pran890/FoodDelivery/blob/step-05-health-and-resilience/src/Sandbox/FoodDelivery.Sandbox.Kitchen/Chaos.cs) | Fault injector: failure rate, delay, and counters of attempts that actually arrived |
| [Kitchen Program.cs](https://github.com/pran890/FoodDelivery/blob/step-05-health-and-resilience/src/Sandbox/FoodDelivery.Sandbox.Kitchen/Program.cs) | Registers the checks (3 s timeout each); `POST/GET /chaos`; `/dish-of-the-day` now reads `now()` from Postgres (`cookedAt`) |
| [Waiter Program.cs](https://github.com/pran890/FoodDelivery/blob/step-05-health-and-resilience/src/Sandbox/FoodDelivery.Sandbox.Waiter/Program.cs) | **Demo-sized** resilience options; `/order` returns 503 with `reason` = `retries-exhausted` / `timeout` / `circuit-open` |
| [AppHost.cs](../../src/AppHost/FoodDelivery.AppHost/AppHost.cs) | `.WithHttpHealthCheck("/health")` on kitchen and waiter |
| [.editorconfig](../../.editorconfig) | + constants are PascalCase (the private-field `_camelCase` rule was also catching `private const`) |
| [tools/http/sandbox.http](https://github.com/pran890/FoodDelivery/blob/step-05-health-and-resilience/tools/http/sandbox.http) | + health and chaos requests |

> Behaviour change from Step 04: `/order?burn=true` used to return the Kitchen's 500. Now it's retried 3 times, and the Waiter answers **503** with `"reason":"retries-exhausted"`.

---

## Concepts

### 1. Liveness vs readiness
| | **Liveness** `/alive` | **Readiness** `/health` |
|---|---|---|
| Question | Is the process stuck or dead? | Can it serve traffic **right now**? |
| Checks here | `self` only | `self` + `postgres` + `redis` |
| On failure the orchestrator… | **restarts** the process | **stops routing traffic** to it (no restart) |
| Kubernetes probe (Step 58) | `livenessProbe` | `readinessProbe` (+ `startupProbe` for slow starts) |

**The classic mistake:** a database check in liveness. Postgres goes down and every service is restarted, then restarted again, and again. That fixes nothing, adds a thundering herd on recovery, and loses in-flight work. A dependency outage is a *readiness* problem.

How the split works here: every check is registered once, and **tags** select which endpoint runs it.
```csharp
// ServiceDefaults: every service
.AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
// Kitchen: dependencies, no "live" tag
.AddCheck<PostgresHealthCheck>("postgres", timeout: TimeSpan.FromSeconds(3))
.AddCheck<RedisHealthCheck>("redis",       timeout: TimeSpan.FromSeconds(3));
// Endpoints
app.MapHealthChecks("/health", ...);                                         // all checks
app.MapHealthChecks("/alive", new() { Predicate = r => r.Tags.Contains("live") });
```
- **Status codes:** Healthy and Degraded → **200**, Unhealthy → **503**. Load balancers and orchestrators only look at the code; the JSON body is for humans.
- **A check that throws** is reported as Unhealthy (the registration's `failureStatus`) with the exception message. That's why the checks have no try/catch.
- **The check timeout matters.** A hanging dependency must not hang the probe. Experiment 4 shows a check giving up at exactly 3 s.
- **Development only:** a health body that says "postgres: password authentication failed" helps an attacker. In production the endpoints would sit on an internal port or network, or return status only.
- **Traces:** probes run every few seconds, so `/health` and `/alive` are filtered out of the ASP.NET Core instrumentation. Otherwise they'd bury real traces.
- **Aspire:** `.WithHttpHealthCheck("/health")` makes the dashboard poll readiness and show the resource as Healthy or Unhealthy. `WaitFor(kitchen)` now waits for *healthy*, not just *started*.

### 2. Transient vs permanent failures
Resilience is about **transient** faults: a timeout, a dropped connection, 503 (overloaded or restarting), 429 (rate limited), 408. Retrying those usually works. A 400 (bad input), 401/403 or 404 will fail the same way every time; retrying them only adds load. The standard pipeline retries `HttpRequestException`, Polly's `TimeoutRejectedException`, **5xx**, **408** and **429**.

### 3. The standard resilience pipeline
`http.AddStandardResilienceHandler()` adds five strategies. A request passes through them **outermost first**:
```
request ─▶ ① rate limiter ─▶ ② total timeout ─▶ ③ retry ─▶ ④ circuit breaker ─▶ ⑤ attempt timeout ─▶ service discovery ─▶ network
```
| # | Strategy | Default | Sandbox demo value (Waiter) | Job |
|---|---|---|---|---|
| ① | Rate limiter | 1000 concurrent, no queue | — | Protects *us* from overloading the dependency |
| ② | Total timeout | 30 s | 8 s | Upper bound on the whole call, **including all retries** |
| ③ | Retry | 3 retries, exponential from 2 s, jitter | delay 200 ms | Hides transient faults |
| ④ | Circuit breaker | opens at ≥10% failures, ≥100 calls in 30 s; break 5 s | 50%, ≥10 calls in 10 s; break 10 s | Stops calling a dependency that's clearly down |
| ⑤ | Attempt timeout | 10 s | 2 s | Bounds each single try, so a slow attempt is abandoned and retried |

Why this order:
- **Total timeout outside retry**, so retries can't extend the call forever. Experiment 3 gives up at exactly 8.04 s.
- **Attempt timeout inside retry**, so *each* slow attempt is cancelled and retried.
- **Breaker inside retry**, so it sees **every attempt**: one order with 3 retries is 4 breaker samples. Experiment 2 shows it opening on the 10th attempt, during the 3rd order.
- **Service discovery innermost** (it's registered after the resilience handler), so each retry resolves the address again and can land on a different instance.

**Exponential backoff with jitter:** retries wait about 200 ms, 400 ms, 800 ms here (2 s, 4 s, 8 s by default), each randomised. Without jitter, 1,000 clients that failed at the same moment retry at the same moment, a self-inflicted spike (the **thundering herd**).

**Validation:** the options are validated at startup. For example, the breaker's sampling duration must be at least 2× the attempt timeout, and the total timeout must exceed the attempt timeout. Verified: setting the Waiter's attempt timeout to 6 s (sampling 10 s) makes the host refuse to start with `OptionsValidationException: The sampling duration of circuit breaker strategy needs to be at least double of an attempt timeout strategy's timeout interval ... Sampling Duration: 10s, Attempt Timeout: 6s`.

### 4. Circuit breaker states
```
        failure ratio ≥ threshold                break duration elapsed
CLOSED ───(with ≥ minimum throughput)───▶ OPEN ───────────────────────▶ HALF-OPEN
  ▲                                      (fail instantly,                  │
  │                                       BrokenCircuitException)          │
  └──────────────── trial call succeeds ◀──────────────────────────────────┘
                    (trial call fails ─▶ back to OPEN)
```
What it buys:
- **The caller** stops wasting threads and time: experiment 2 shows 16 ms instead of about 1 s.
- **The dependency** gets breathing room to recover instead of being hammered by retries.
- **Users** get a fast, clear error you can turn into a fallback: "menu temporarily unavailable", a cached value, a queued request.

**Minimum throughput** stops the breaker from opening on "1 of 1 calls failed". It also has a side effect experiment 4 runs into: **slow** failures produce few calls per window, so a breaker can stay closed under light traffic.

### 5. Retries amplify load, and repeat side effects
Experiment 5: one burnt order = **4 dishes cooked**. Two consequences:
- **Load:** if Gateway → Order → Catalog each retry 3 times, one user click can become 4 × 4 × 4 = 64 Catalog calls. Retry at **one** layer (usually the caller nearest the failure), and use **retry budgets** and timeouts that shrink at each hop.
- **Side effects:** the standard handler retries **all methods by default, including POST**. Retrying "charge the card" or "place the order" after a timeout can do it twice, because the first attempt may have succeeded and only the response got lost. Options: `options.Retry.DisableForUnsafeHttpMethods()`, or make the operation **idempotent** with an `Idempotency-Key` (Step 32).

---

## Trade-offs and alternatives
| Choice | Why here | Alternatives / when to switch |
|---|---|---|
| Hand-written health checks | The mechanism stays visible; two short classes. | **AspNetCore.HealthChecks.NpgSql / .Redis** (Xabaril, Apache-2.0): ready-made, plus a health UI. Use them in real services to avoid boilerplate. |
| One `/health` running all checks | Simple; the Aspire convention. | Separate `/startup`; or readiness that only checks *critical* dependencies. If Redis is just a cache, a Redis outage should be **Degraded**, not Unhealthy. |
| Standard pipeline for every HttpClient | Safe defaults everywhere, zero code per client. | Per-client tuning; **hedging** (`AddStandardHedgingHandler`: send a second request in parallel when the first is slow, which cuts tail latency for idempotent reads). |
| Demo values in the Waiter (`ConfigureAll`) | Makes the behaviour visible with a few curl calls. | Real services keep the defaults or bind options from configuration and tune with real latency data (e.g. attempt timeout ≈ p99.9). |
| Breaker per client | The default. | Per authority (`SelectPipelineByAuthority()`), so one bad instance or host doesn't open the breaker for all of them. |
| `Chaos` class in the Kitchen | Tiny and explicit. | Polly **chaos strategies** (`AddChaosFault`, `AddChaosLatency`, `AddChaosOutcome`) injected into the *client's* pipeline; Toxiproxy for network-level faults. |
| Resilience in code (Polly) | Free and per call site. | A **service mesh** (Istio, Linkerd) does retries and timeouts in a sidecar, language-neutral, but heavy for 12 GB of RAM. |

---

## Break-it experiments
All were run while building this step; the curl outputs are real. The dashboard observations (health state, retry spans, metric counts) are what you should see, but they weren't checked by script, so confirm them during review. Start the AppHost, then use [sandbox.http](https://github.com/pran890/FoodDelivery/blob/step-05-health-and-resilience/tools/http/sandbox.http) or curl from Git Bash with `K=https://localhost:7148; W=https://localhost:7240`.
**Wait about 10 s between experiments** so the breaker's sampling window from the previous one has passed.

### 1. Transient failures are hidden by retries
```bash
curl -sk -X POST "$K/chaos?failRate=0.3"
for i in $(seq 1 20); do curl -sk -o /dev/null -w "%{http_code} " $W/order; done; echo
curl -sk $K/chaos
```
→ measured: **20 × 200**, and `"requestsReceived":31,"failuresInjected":11`. The Kitchen failed 11 times and the user never noticed. In the dashboard, a retried `/order` trace has several `GET` client spans under the Waiter, with small, growing gaps between them (backoff).
With `failRate=0.5`: still 20 × 200 (`36` received, `16` failed). That's a failure ratio of 0.44, just under the breaker's 0.5. Run it a few times and occasionally the breaker trips: the tuning is a trade-off.

### 2. A dead dependency opens the circuit
```bash
curl -sk -X POST "$K/chaos?failRate=1"
for i in 1 2 3 4 5; do curl -sk -w " t=%{time_total}s\n" $W/order | grep -o '"reason":"[^"]*"\|t=.*' | tr '\n' ' '; curl -sk $K/chaos | grep -o '"requestsReceived":[0-9]*'; done
```
→ measured:
```
order 1: retries-exhausted  t=0.79s   kitchen received 4
order 2: retries-exhausted  t=1.15s   kitchen received 8
order 3: circuit-open       t=0.60s   kitchen received 10   ← opened on the 10th attempt (minimum throughput)
order 4: circuit-open       t=0.016s  kitchen received 10   ← not even called
order 5: circuit-open       t=0.017s  kitchen received 10
```
**Half-open recovery:** turn chaos off (`POST $K/chaos`) and call `/order` right away → still `circuit-open` (the breaker doesn't know the Kitchen is fixed). Wait 11 s and call again → **200**: the trial call succeeded and the circuit closed. `requestsReceived` is 1.

### 3. A slow dependency: attempt timeout vs total timeout
```bash
curl -sk -X POST "$K/chaos?delayMs=3000"
curl -sk -w " t=%{time_total}s\n" $W/order; curl -sk $K/chaos
```
→ measured: `"reason":"timeout"`, `TimeoutRejectedException: ... timeout of '00:00:08'`, **t=8.04 s**, `requestsReceived: 4`. Each 3 s attempt was cancelled at 2 s and retried: 0–2 s, about 2.2–4.2, 4.6–6.6, then the 4th was cut off by the 8 s total timeout.
With `delayMs=1500` (under the attempt timeout): `200 t=1.70s`, received 1. Slow but within budget, so no retry.
Lesson: without timeouts, a slow dependency is *worse* than a dead one, because callers wait and pile up.

### 4. The roadmap's main experiment: stop Postgres
```bash
curl -sk -X POST "$K/chaos"                  # chaos off
docker stop $(docker ps --filter name=postgres- --format "{{.Names}}")
curl -sk -w " [%{http_code}]\n" $K/health
curl -sk -w " [%{http_code}]\n" $K/alive
```
→ measured:
- **`/health` → 503 Unhealthy**: `postgres: Unhealthy`, `redis: Healthy`, `self: Healthy`. The first probe failed in 27 ms ("Exception while reading from stream": a pooled connection had died). Later probes take **3.0 s**, with "A timeout occurred while running check." The check timeout saved the probe.
- **`/alive` → 200 Healthy.** The process is fine, so an orchestrator would stop routing traffic but **not** restart it. That's the point of the split.
- **Dashboard:** kitchen shows as Unhealthy, and postgres as exited.

Sequential orders (`for i in 1 2 3 4; do curl -sk $W/order; done`) → measured: **all 4 `timeout` at 8.03 s**, 16 attempts, and **the breaker never opened**. Two lessons:
- **The Windows quirk:** a refused localhost connection takes about **2 s** on Windows, because it retries the connection attempt. Measured with a plain socket on the closed port: `127.0.0.1` refused in 2.07 s, `::1` in 2.02 s. `localhost` resolves to both and they are tried in turn, which matches the **4.1 s** a direct Kitchen call took. That is longer than the 2 s attempt timeout, so you get timeouts instead of fast 500s. On Linux a refused connection fails at once: Step 07 measured the dead-Postgres test at 97 ms on a CI runner, against about 2.5 s on Windows.
- **Slow failures can keep the breaker closed:** 4 attempts per 8 s order is fewer than the minimum of 10 per 10 s window (the likely explanation; the breaker state itself was not inspected).

Now send concurrent load:
```bash
for i in $(seq 1 10); do (curl -sk $W/order | grep -o '"reason":"[^"]*"') & done; wait
curl -sk -w " t=%{time_total}s\n" $W/order
```
→ measured: **10 × `circuit-open`**, then the next call is `circuit-open` in **0.026 s**, and the Kitchen received **15** attempts instead of the 40 that 10 orders × 4 attempts would make. Under real traffic the breaker does its job.

**Recovery:**
```bash
docker start $(docker ps -a --filter name=postgres- --format "{{.Names}}")
```
→ `/health` is 200 again after about 1 s, `/order` is 200 once the break duration has passed, and the dashboard shows kitchen Healthy.

### 5. Retry amplification
```bash
curl -sk -X POST "$K/chaos"
curl -sk $W/order?burn=true; curl -sk $K/chaos
```
→ `"reason":"retries-exhausted"` (`500 (Internal Server Error)`), and **`requestsReceived: 4`**. One order, four burnt dishes. In the dashboard, metric `kitchen.dishes.cooked` with `burnt=true` goes up by 4. Now imagine this were `POST /payments` (concept 5).

### 6. Liveness misconfigured
In [Extensions.cs](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/Extensions.cs), comment out the `Predicate` line of the `/alive` mapping, so liveness runs all checks. Restart, then stop Postgres → measured: `/alive` now returns **503 Unhealthy** (it was 200 before stopping Postgres, and is 200 again after starting it). In Kubernetes that means the kubelet kills the pod, it restarts, it's still 503, and you're in a CrashLoopBackOff for *every* pod that uses Postgres, during an outage that a restart can't fix. Undo the change.

---

## Interview questions

**1. Liveness vs readiness: what's the difference, and what goes in each?**
Liveness says "this process is broken, restart it". It should check only the process itself (it responds, no deadlock). Readiness says "don't send me traffic right now". It checks what's needed to serve requests: database, cache, warm-up done. Putting dependencies in liveness turns a database outage into mass restarts that fix nothing. A third probe, startup, protects slow-starting apps from being killed by liveness during boot.

**2. Which failures should you retry?**
Transient ones: timeouts, connection failures, 503, 429 (respecting `Retry-After`), 408, and sometimes 502/504. Not 4xx client errors like 400, 401, 403, 404 or 409, which fail the same way every time. Retry only idempotent operations, or ones protected by an idempotency key, with capped exponential backoff and jitter.

**3. Why add jitter to backoff?**
Clients that failed at the same moment (e.g. during a deploy) would otherwise retry at exactly the same moments, producing synchronized load spikes that can keep the dependency down. Jitter spreads retries randomly across the backoff window.

**4. Explain the circuit breaker states.**
Closed: calls flow and outcomes are sampled. When the failure ratio passes the threshold with enough throughput in the sampling window, it goes **open**: calls fail immediately without touching the dependency, for the break duration. Then **half-open**: a trial call goes through. Success closes the circuit, failure reopens it. It protects both caller (fast failure, no thread pile-up) and callee (room to recover).

**5. Why is the attempt timeout inside the retry and the total timeout outside it?**
The attempt timeout bounds each try, so a hung attempt is abandoned and retried. The total timeout bounds the whole operation, so the user gets an answer within a known budget no matter how many retries. If total were inside retry, each retry would get a fresh full budget and the call could take 4× longer.

**6. How can retries make an outage worse?**
They multiply load exactly when the dependency is struggling: 3 retries = 4× traffic, and retries at several layers multiply further (4 × 4 × 4). Mitigations: retry at one layer only, use backoff with jitter, retry budgets, circuit breakers so retries stop when the dependency is clearly down, and deadlines propagated down the call chain.

**7. A health check calls the database. What can go wrong?**
It can hang (use a check timeout). It can be expensive if run very often (use a trivial query and a short timeout). It can expose internals (restrict or trim the output). If the dependency is optional, failing readiness on it removes all instances from rotation for something that should only degrade a feature. Report Degraded instead, or don't check it in readiness.

**8. What is a cascading failure, and which patterns prevent it?**
One slow or failed service makes its callers slow or failed (threads and connections stuck waiting), which spreads up the call graph until the whole system is down. Prevention: timeouts everywhere, circuit breakers, **bulkheads** (isolate resources per dependency, e.g. the rate limiter or separate pools), load shedding, fallbacks, and asynchronous messaging where synchronous calls aren't required.

**9. What would you do differently for a POST that charges money?**
Don't blindly retry it: a timeout doesn't mean it failed. Send an **idempotency key** so the server deduplicates, making retries safe (Step 32). Or disable retries for unsafe methods (`DisableForUnsafeHttpMethods()`) and reconcile through the payment provider's status API or webhook (Steps 36–37).

---

## Review checklist
- [ ] Run the AppHost; the dashboard shows kitchen and waiter **Healthy**; `/health` and `/alive` return the JSON above.
- [ ] Experiments 1–5 reproduced; experiment 6 (liveness misconfigured) tried and undone.
- [ ] Postgres started again afterwards (`docker start ...`), and chaos turned off (`POST /chaos`).
- [ ] You can explain: why the DB check isn't in liveness, the order of the five strategies, why the breaker opened on the 10th attempt, why it *didn't* open with sequential slow failures, and why retrying a POST is dangerous.
- [ ] Approved, then commit + tag `step-05-health-and-resilience`.
