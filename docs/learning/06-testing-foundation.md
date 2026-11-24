# Step 06 — Testing foundation

> **Topic:** unit, integration and architecture tests with xUnit v3, `WebApplicationFactory`, Testcontainers (PostGIS + Redis) and NetArchTest, and how to stop tests leaking state into each other.
> **Tag when approved:** `step-06-testing-foundation`

> **Since Step 12** the Sandbox (Kitchen and Waiter) is gone. Its links below lead to this step's tag, and to run its experiments check out `step-06-testing-foundation`.

## Why this matters for this project
Until now every check was a curl command. That doesn't scale to 10 services, and the CI pipeline (Step 07) needs something to run. Fakes aren't enough either: an in-memory stand-in for Postgres can't tell you that a SQL query is wrong or that PostGIS is missing. This step sets up tests that run the **real app** against a **real Postgres and Redis**, and turns the project's rules ("services never reference each other") into tests that fail.

## What was added
| File | Role |
|---|---|
| [tests/Directory.Build.props](../../tests/Directory.Build.props) | Settings for test projects only. Imports the root file explicitly, because MSBuild stops at the first one it finds. |
| [tests/.editorconfig](../../tests/.editorconfig) | Allows underscores in test names |
| [ChaosTests.cs](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/tests/FoodDelivery.Sandbox.Kitchen.Tests/ChaosTests.cs) | **Unit** tests: one class, no I/O |
| [KitchenFixture.cs](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/tests/FoodDelivery.Sandbox.Kitchen.Tests/KitchenFixture.cs) | Containers + in-memory Kitchen, shared once; the state reset; the test base class |
| [DishTests.cs](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/tests/FoodDelivery.Sandbox.Kitchen.Tests/DishTests.cs), [InfrastructureTests.cs](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/tests/FoodDelivery.Sandbox.Kitchen.Tests/InfrastructureTests.cs), [HealthTests.cs](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/tests/FoodDelivery.Sandbox.Kitchen.Tests/HealthTests.cs) | **Integration** tests over HTTP |
| [ProjectReferenceTests.cs](../../tests/FoodDelivery.ArchitectureTests/ProjectReferenceTests.cs) | **Architecture** rules read from the `.csproj` files |
| [TypeRuleTests.cs](../../tests/FoodDelivery.ArchitectureTests/TypeRuleTests.cs) | **Architecture** rules on compiled code |
| [global.json](../../global.json) | `dotnet test` uses Microsoft.Testing.Platform, which xUnit v3 runs on |
| [Kitchen .csproj](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/src/Sandbox/FoodDelivery.Sandbox.Kitchen/FoodDelivery.Sandbox.Kitchen.csproj) | `InternalsVisibleTo`, so tests can use the Kitchen's internal types |
| [Directory.Packages.props](../../Directory.Packages.props) | + `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `Testcontainers.Redis`, `NetArchTest.Rules` |

> Change from the brief: `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` turned out not to be needed, because xUnit v3 4.x runs on Microsoft.Testing.Platform.

## Running the tests
Docker Desktop must be running and the **AppHost must be off** (12 GB RAM rule).
```powershell
dotnet test                                                                                  # everything
dotnet test --project tests\FoodDelivery.ArchitectureTests                                   # no Docker needed
dotnet test --project tests\FoodDelivery.Sandbox.Kitchen.Tests --filter-method "*Three_calls*"
```
Measured on this laptop:

| | Tests | Time |
|---|---|---|
| Architecture | 7 | 5 s |
| Kitchen (4 unit + 10 integration) | 14 | 23–26 s, nearly all of it starting containers |
| First run ever (also downloads the Ryuk image) | 21 | 71 s |

Containers during a run: Postgres 35 MB of 512 MB, Redis 6 MB of 128 MB, Ryuk 10 MB (no cap).

---

## Concepts

### 1. Three kinds of test
| Kind | What runs | Catches | Cost |
|---|---|---|---|
| **Unit** (`ChaosTests`) | One class | Logic mistakes | Milliseconds |
| **Integration** (`DishTests`…) | Real app + real Postgres and Redis | Wiring, SQL, JSON, config, health checks | Seconds to start, then 3 ms to about 3 s each |
| **Architecture** | Nothing; it inspects code | Broken project rules | Seconds |

`ChaosTests` tests random code at the ends of its range: at 0% nothing may fail, at 100% everything must. Testing "about 30% fail" would be flaky by design.

### 2. xUnit's lifecycle
- **A new instance of the test class for every test.** Fields are never shared, so `_chaos` in `ChaosTests` can't leak.
- **Fixtures** are the exception, for expensive things:

| Sharing | Created | Classes run |
|---|---|---|
| `IClassFixture<T>` | once per class | in parallel with each other |
| `ICollectionFixture<T>` (**used here**) | once per collection | **one after another** inside the collection |
| `AssemblyFixture` | once per assembly | in parallel with each other |

- **`IAsyncLifetime`** gives async setup and cleanup. On a *fixture* it runs once. On a *test class* it runs around **every test**, which is where the reset lives.

### 3. WebApplicationFactory
It runs the Kitchen's real `Program.cs` inside the test process: real routing, DI, middleware and OpenTelemetry. `CreateClient()` returns an `HttpClient` wired straight to it, with no network port.

**The config timing trap.** The Kitchen reads its connection strings *before* `builder.Build()`:
```csharp
host.UseSetting("ConnectionStrings:sandboxdb", postgres);   // visible from the first line: works
host.ConfigureAppConfiguration(...)                          // applied too late here: experiment 2
```

### 4. Testcontainers
- Starts containers on **random free ports**, waits until they're ready, and gives you the connection string.
- It uses none of our named volumes, so **development data is never touched**. Checked: the volume count was 3 before and 3 after a run.
- **Ryuk**, a small helper container, removes leftovers even if the test process crashes. Checked: 0 test containers left after each run.
- **Same image tags and memory caps as the AppHost.** Tests on a different Postgres version prove less. `docker inspect` confirmed 512 MB and 128 MB, with swap equal to memory.

### 5. Isolation: shared containers mean shared state
Starting containers per test would be clean and unbearably slow, so they're shared, and **every test resets state first**. Two kinds of state leak, and the second is easy to forget:
1. **Data:** Redis keys → `FLUSHDB`.
2. **Singletons inside the running app:** the `Chaos` settings → set back to "off".

**Reset before, not after.** If a test crashes, cleanup afterwards may not run. Resetting first means a test never depends on how the previous one ended.

Postgres holds no tables yet. Resetting real tables comes in Step 08.

### 6. Architecture tests, and two ways they lie
**They can check nothing.** A rule such as "types in namespace X must not…" passes when X has no types. `Verify(...)` therefore first asserts that the rule selects something. It paid off while building this step: the ServiceDefaults rule selected **zero** types. NetArchTest skips every type whose full name starts with `System` or `Microsoft` ([exclusion list in its source](https://github.com/BenMorris/NetArchTest/blob/master/src/NetArchTest.Rules/Types.cs)), and our `Extensions` class lives in `Microsoft.Extensions.Hosting`. That rule now reads the assembly's references instead.

**The DLL doesn't show everything.** The compiler drops a project reference that no code uses. Measured by making the Kitchen reference Contracts:

| | References in `Kitchen.dll` |
|---|---|
| Referenced, unused | `ServiceDefaults` |
| Referenced and used | `Contracts`, `ServiceDefaults` |

So "services never reference each other" is checked on the `.csproj` files. Service projects are discovered automatically, so a new service is covered without touching the test.

**See every test fail once.** A test that has never failed proves nothing. Every architecture rule was broken on purpose while building this step, and two integration tests were checked the same way (experiment 7).

---

## Trade-offs and alternatives
| Choice | Why here | Alternatives |
|---|---|---|
| Testcontainers | Real Postgres and PostGIS; throwaway | EF in-memory provider or SQLite: fast, but different SQL and no PostGIS, so bugs pass. The AppHost's containers: no startup cost, but tests would alter your dev data. |
| `WebApplicationFactory`, one service | Fast, debuggable, can replace services in DI | `Aspire.Hosting.Testing` starts the whole AppHost: true end-to-end, but heavy for 12 GB. |
| One shared fixture | 2 containers, about 24 s | Per class: 6 containers, about 40 s (experiment 5). |
| `FLUSHDB` per test | Simple and complete | Unique keys per test: allows parallel tests, but every test must remember to do it. |
| xUnit v3 | Async lifecycle, native test platform | NUnit, MSTest, TUnit. |
| NetArchTest.Rules 1.3.2 (MIT) | Readable rules | `NetArchTest.eNhancedEdition`, ArchUnitNET, or plain reflection, as used for ServiceDefaults. |
| `InternalsVisibleTo` | Tests reuse the Kitchen's response records | Separate test DTOs: they also catch accidental JSON renames, which shared records can't. |

That last row is a real weakness: because the tests share the Kitchen's records, renaming a JSON field changes both sides and no test fails. Step 21 (OpenAPI as a contract) deals with it.

---

## Break-it experiments
All were run while building this step and the results are real. **Undo each change afterwards** (`git diff` should be empty).

### 1. Make a test leak state between runs (the roadmap's experiment)
In [KitchenFixture.cs](https://github.com/pran890/FoodDelivery/blob/step-06-testing-foundation/tests/FoodDelivery.Sandbox.Kitchen.Tests/KitchenFixture.cs), in `KitchenTest`, replace the reset:
```csharp
public ValueTask InitializeAsync() => ValueTask.CompletedTask;
```
→ **3 of 14 fail**, the same three in both runs:
- `Three_calls_count_three_orders`: `Expected: 3, Actual: 4`. The other test's increment was still in Redis.
- Two `DishTests` get **503**, because an earlier test left chaos at 100%.

Now run the first one **alone**: `--filter-method "*Three_calls*"` → **it passes.** "Fails in the suite, passes alone" is the signature of leaked state. The failing test is the victim, not the culprit.

**1b.** Restore the reset, but delete only the "Shared state 2" lines → **2 fail**, both with 503. A clean database isn't enough.

### 2. Override the config too late
Replace the two `UseSetting` calls with:
```csharp
host.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
    { ["ConnectionStrings:sandboxdb"] = postgres, ["ConnectionStrings:redis"] = redis }));
```
(and add `using Microsoft.Extensions.Configuration;`)
→ integration tests fail with `InvalidOperationException: Missing ConnectionStrings:sandboxdb`.

### 3. Break an architecture rule
| Change | Failing test |
|---|---|
| Kitchen `.csproj` references Waiter | `Service_references_only_shared_projects("FoodDelivery.Sandbox.Kitchen")` |
| Contracts takes a package | `Contracts_references_no_project_and_no_package` |
| ServiceDefaults references Waiter | `Shared_projects_never_reference_services` |
| ServiceDefaults uses Npgsql | `Service_defaults_knows_no_database_drivers` |
| `Chaos` uses Npgsql | `Chaos_does_no_io` ("Rule broken by: Chaos") |
| `RedisHealthCheck` not sealed | `Health_checks_are_sealed_and_named_consistently` |

For the two rows that add a Waiter reference, the rule reads files, so run the already-built tests (`dotnet test --no-build`). Building the Kitchen row fails first with `CS0436`, because both apps define `Program`.

### 4. A rule that checks nothing
Add a test without the guard:
```csharp
var result = Types.InAssembly(Assembly.Load("FoodDelivery.Sandbox.Kitchen"))
    .That().ResideInNamespace("No.Such.Namespace").ShouldNot().HaveDependencyOn("System").GetResult();
Assert.True(result.IsSuccessful);
```
→ **passes**, having inspected zero types.

### 5. One fixture per class
Make `KitchenTest` implement `IClassFixture<KitchenFixture>`, and delete the `[Collection]` attribute and the `KitchenSuite` class (an analyzer rejects an unused collection).
→ all pass, but **39.6 s and 40.0 s** instead of 23–25 s, with **6** database containers at once instead of 2.

### 6. Shared fixture, parallel classes
Delete `[Collection]` and `KitchenSuite`, and add `[assembly: AssemblyFixture(typeof(FoodDelivery.Sandbox.Kitchen.Tests.KitchenFixture))]` above the namespace. Run it three times:
→ **0, then 1, then 3 failures** with identical code. The classes run in parallel and reset each other's state mid-test. This is what a flaky suite looks like, and the first run would have let it through.

### 7. Do the tests catch real bugs?
Re-introduce two bugs from earlier steps:
- `SourceName = "Kitchen.Cooking"` (Step 04, experiment 2, where nothing complained) → `Request_joins_the_callers_trace_and_records_the_custom_span` fails.
- Remove the `/alive` `Predicate` (Step 05, experiment 6) → `Dead_postgres_fails_readiness_but_not_liveness` fails: `Expected: OK, Actual: ServiceUnavailable`.

---

## Interview questions

**1. Unit vs integration tests: how do you decide?**
Unit-test logic with branches and rules (pricing, state machines, policies), where many cases run in milliseconds. Integration-test where the risk is in the *connections*: SQL, serialization, DI, config, auth. A service that's mostly CRUD gets more value from integration tests; one with rich domain logic needs both.

**2. Why not use the EF Core in-memory provider?**
It isn't a relational database: no SQL translation, no constraints, no real transactions, no provider features such as PostGIS or `xmin`. Tests pass while production fails. Testcontainers runs the real engine at the cost of a few seconds.

**3. A test fails in the suite but passes alone. What do you do?**
Suspect shared state. Look for what survives between tests: database rows, cache keys, static fields, singletons in a shared host, environment variables. Reset state *before* each test, then confirm by running in a different order. Retrying the test only hides it.

**4. What's the difference between class, collection and assembly fixtures?**
Class: one instance per test class. Collection: one shared by several classes, which then run sequentially. Assembly: one for the whole assembly, with classes still running in parallel, so only safe for state that tests don't change or that is partitioned per test.

**5. How does WebApplicationFactory differ from calling a running server?**
It hosts the real app in the test process on an in-memory server. No ports, easy debugging, and services or settings can be replaced. It doesn't exercise Kestrel, TLS or the real network, so a few end-to-end tests are still useful.

**6. Why might overriding configuration in a test have no effect?**
Timing. With minimal hosting, code before `builder.Build()` reads configuration immediately, but sources added through `ConfigureAppConfiguration` on the factory arrive later. Use `UseSetting`, or have the app read settings lazily through options.

**7. What are architecture tests for, and how can they mislead you?**
They keep rules that code review forgets: layering, forbidden dependencies, naming. They mislead when a rule selects nothing (renamed namespace, excluded types) or inspects the wrong artifact (the compiler removes unused references). Assert that the selection isn't empty, and see each rule fail once.

**8. How do you keep integration tests fast?**
Share containers, reset data instead of recreating it, keep logic in unit tests, run independent groups in parallel only when their state is separate, and pre-pull images in CI.

**9. How do you know your tests are any good?**
Break the code on purpose and see whether a test fails. Mutation testing (Stryker.NET) automates that. Coverage only shows that a line *ran*, not that anything checked its result.

---

## Review checklist
- [ ] Docker Desktop running, AppHost off: `dotnet test` → 21 passed.
- [ ] Experiments 1, 2, 4 and 6 reproduced (and undone).
- [ ] At least two rows of experiment 3 tried.
- [ ] You can explain: why the collection fixture runs classes one after another, why state is reset *before* each test, why `UseSetting` works and `ConfigureAppConfiguration` doesn't, and why project references are read from the `.csproj` files.
- [ ] Approved, then commit + tag `step-06-testing-foundation`.
