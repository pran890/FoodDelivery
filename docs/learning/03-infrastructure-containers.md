# Step 03 — Infrastructure containers

> **Topic:** running Postgres, Redis and Mailpit as containers from the AppHost. Covers container **lifetime**, **volumes**, **memory caps**, and how **connection strings** reach a service.
> **Tag when approved:** `step-03-infrastructure-containers`

> **Since Step 12** the Sandbox (Kitchen and Waiter) is gone. Its links below lead to this step's tag, and to run its experiments check out `step-03-infrastructure-containers`.

## Why this matters for this project
Every service from Step 08 on needs a database, a cache or an SMTP server. Installing them on Windows means version drift, services always running in the background, and no clean reset. Containers give us:
- The exact versions we choose.
- A hard RAM cap each.
- One-command reset.
- Connection strings handed to services automatically.

This step sets up that infrastructure once, and shows how it behaves when things go wrong.

## What was added
| File | Role |
|---|---|
| [AppHost.cs](../../src/AppHost/FoodDelivery.AppHost/AppHost.cs) | `AddPostgres` (PostGIS image, tuned), `AddRedis`, `AddMailPit`, each **Persistent** with a **named volume** and a **real memory cap**; Kitchen references the database and Redis. |
| [AppHost .csproj](../../src/AppHost/FoodDelivery.AppHost/FoodDelivery.AppHost.csproj) | + hosting integrations `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Redis`, `CommunityToolkit.Aspire.Hosting.MailPit` |
| [Kitchen Program.cs](https://github.com/pran890/FoodDelivery/blob/step-03-infrastructure-containers/src/Sandbox/FoodDelivery.Sandbox.Kitchen/Program.cs) | Reads `ConnectionStrings:sandboxdb` and `ConnectionStrings:redis` by hand. New endpoints `/infra/postgres`, `/infra/redis`, `/debug/connection-strings`. |
| [Kitchen .csproj](https://github.com/pran890/FoodDelivery/blob/step-03-infrastructure-containers/src/Sandbox/FoodDelivery.Sandbox.Kitchen/FoodDelivery.Sandbox.Kitchen.csproj) | + plain `Npgsql` and `StackExchange.Redis` clients |
| [tools/infra.ps1](../../tools/infra.ps1) | `status`, `stop`, `start`, `remove` (containers only), `reset` (containers + volumes) |
| [tools/http/sandbox.http](https://github.com/pran890/FoodDelivery/blob/step-03-infrastructure-containers/tools/http/sandbox.http) | + the three new endpoints |
| [CLAUDE.md](../../CLAUDE.md) | RAM rules updated with the gotchas below |

**No product data structures were created.** There are no tables, PostGIS isn't enabled in any database, and the only data is a throwaway database `sandboxdb` and a throwaway key `sandbox:kitchen:orders-served`. Real schemas start in Step 08, after your review.

## Measured footprint (idle)
| Container | Image | Memory used / cap |
|---|---|---|
| postgres | `postgis/postgis:17-3.5` (Postgres 17.5, PostGIS 3.5.2) | ~20–80 MB / 512 MB |
| redis | `redis:8.6` | ~5 MB / 128 MB |
| mailpit | `axllent/mailpit:v1.31.2` | ~8 MB / 64 MB |

First run: the images download (PostGIS is the big one). After that, with the containers already running, the AppHost was ready in **22 seconds**.

---

## Concepts

### 1. Hosting integrations: containers declared in C#
```csharp
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis").WithImageTag("17-3.5")   // swap the default postgres image for PostGIS
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("fooddelivery-postgres-data")
    .WithContainerRuntimeArgs("--memory=512m", "--memory-swap=512m")
    .WithArgs("-c", "shared_buffers=128MB", "-c", "max_connections=50");
var sandboxDb = postgres.AddDatabase("sandboxdb");
```
- `AddPostgres` / `AddRedis` / `AddMailPit` come from **hosting integration** packages, referenced only by the AppHost. They know each image's ports, environment variables and health checks, and how to build its connection string.
- `AddDatabase("sandboxdb")` is a **child resource**. Aspire runs `CREATE DATABASE` if it doesn't exist. It does nothing about tables.
- `WithArgs("-c", "shared_buffers=128MB", ...)`: the Postgres image's entrypoint treats arguments starting with `-` as server options. Checked through `/infra/postgres`, which returns `sharedBuffers: 128MB` and `maxConnections: 50`.
- **Generated secrets:** Aspire creates the Postgres and Redis passwords and stores them, **and the host ports**, in the AppHost's **user secrets** (`Parameters:postgres-password`, `Resources:postgres:tcp:port`, …). That's how persistent containers get identical values on every run. See them with `dotnet user-secrets list --project src\AppHost\FoodDelivery.AppHost`.
- **Aspire 13 runs Redis with TLS:** `--tls-port 6379 --port 6380`, using your trusted dev certificate. The connection string Kitchen receives therefore contains `ssl=true`. Inside the container, port 6380 is plain text, which is what the experiments use with `redis-cli`.

### 2. Connection strings: the same mechanism as Step 02
`.WithReference(sandboxDb)` injects an environment variable:
```
ConnectionStrings__sandboxdb = Host=localhost;Port=31901;Username=postgres;Password=…;Database=sandboxdb
ConnectionStrings__redis     = localhost:31902,password=…,ssl=true
```
`__` becomes `:`, so the service calls `builder.Configuration.GetConnectionString("sandboxdb")`, which reads the section `ConnectionStrings:sandboxdb`. `GET /debug/connection-strings` shows them with passwords masked.

We read them **by hand** with the plain `Npgsql` and `StackExchange.Redis` clients so the mechanism stays visible. Real services will use Aspire **client integrations** (`Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`, `Aspire.StackExchange.Redis`). They read the same connection string and also add health checks, tracing and retries. Those topics belong to Steps 04 and 05, so we'll appreciate them more after doing it the plain way.

`WaitFor(sandboxDb)` means Kitchen doesn't start until the database exists and is reachable. Experiment 5 shows Kitchen stuck waiting forever when that can't happen.

### 3. Container lifetime: Session vs Persistent
| | Session (default) | **Persistent** (ours) |
|---|---|---|
| AppHost stops | Containers are stopped and removed | **Containers keep running** |
| Next AppHost run | New containers, slow start | Reuses the running containers (22 s start) |
| RAM when you're not working | Freed | **Still used**, so run `tools\infra.ps1 stop` |

How Aspire recognises "the same container" next time: it labels each one with `com.microsoft.developer.usvc-dev.persistent=true` and a **`lifecycle-key`** hash of its configuration. What we saw:
- Changing an **environment variable** (the password, experiment 5) changes the key, so the container is **recreated**.
- Changing **`WithContainerRuntimeArgs`** (memory caps) did **not**: the old container was reused with its old limits. **Fix:** `tools\infra.ps1 remove` (the volumes stay), then run the AppHost.

### 4. Volumes: data outside the container
`WithDataVolume("fooddelivery-redis-data")` mounts a Docker **named volume** at the data directory (`/data` for Redis, `/var/lib/postgresql/data` for Postgres).
- Removing a container keeps the volume, so the data survives (the Postgres container was recreated twice in this step and kept its data).
- Removing the **volume** is the real reset: the Redis counter went back to **1** (experiment 2).
- For Redis, `WithDataVolume` also turns on snapshots: `--save 60 1` (write a snapshot to disk if at least 1 key changed in 60 seconds). Experiment 4 shows what that means after a crash.

### 5. Memory caps, and the swap trap
`--memory=128m` sets the RAM limit. **But on its own, Docker sets `--memory-swap` to double that**: 128 MB of RAM *plus* 128 MB of swap (`docker inspect` showed `MemorySwap=268435456`). Redis grew to **207 MB** inside a "128 MB" container without being killed. Only past 256 MB did the kernel kill it.

Setting `--memory-swap` equal to `--memory` means **no swap**, so the cap is real. The same 200,000 keys then got Redis killed at 128 MB.

When a container exceeds its cap, the kernel **kills** it: `OOMKilled=true`, **exit code 137** (128 + 9, i.e. SIGKILL). Nobody restarts it: the AppHost shows it as exited, and the services that depend on it return 500.

**The better design for Redis:** give Redis its own limit *below* the container cap (`maxmemory 100mb` + `maxmemory-policy noeviction`). Then Redis rejects writes with an `OOM command not allowed` **error** instead of the whole server being killed. Aspire's `AddRedis` builds the Redis command line itself, so there's no simple hook for this. We'll handle it when Redis starts holding real data (OTP in Step 09, carts in Step 29). `noeviction` matters there, because silently evicting someone's cart would be a bug.

### 6. Durability: what a snapshot does and doesn't protect
Redis keeps everything in RAM. With `--save 60 1`, it writes a snapshot (an **RDB** file) to the volume at most once a minute. After the OOM kill in experiment 4, the restarted Redis loaded the **last snapshot**. The counter went back from 2 to 1, because the latest increment happened in the last minute before the crash. The 200,000 filler keys were gone too.
- **RDB snapshots:** small and fast, but you can lose up to the snapshot interval.
- **AOF** (append-only file): logs every write, losing at most about 1 second with `appendfsync everysec`, but bigger files and more I/O.
- For our data: OTP codes and rate-limit counters can be lost safely; carts losing a minute is acceptable. **Anything that must not be lost belongs in Postgres**, which commits durably through its write-ahead log (WAL).

---

## Trade-offs and alternatives
| Choice | Alternative | Why we didn't |
|---|---|---|
| Persistent lifetime | Session (default) | Session recreates containers on every run: slow startup and empty databases every time. Persistent costs RAM when idle, solved by `tools\infra.ps1 stop`. |
| Named volumes | Bind mounts (`WithDataBindMount("C:\data\pg")`) | Bind mounts of Windows folders into a Linux container are slow (cross-filesystem) and have permission issues for Postgres. Named volumes live inside WSL2: fast and simple. |
| PostGIS image for all databases | Plain `postgres` image + separate PostGIS later | One image now avoids migrating data between images in Step 19. PostGIS is only *available*; it's enabled per database (`CREATE EXTENSION`) when a service needs it. |
| One Postgres container, many databases | One container per service | Strict isolation per service, but 10 × ~50–100 MB of RAM. One server with a database per service keeps the ownership rule (no service touches another's database) within budget. |
| Mailpit | Papercut, smtp4dev | All fine; Mailpit is tiny (8 MB), has an API we can use in tests, and has a maintained Aspire integration. |
| No pgAdmin / RedisInsight | `.WithPgAdmin()`, `.WithRedisInsight()` | Each is another container (pgAdmin ~150–250 MB). Add one temporarily when you need to browse data. |

---

## Break-it experiments
All were run while building this step; the outputs are real. Run `.\tools\infra.ps1 status` before and after to see what's going on.

### 1. Persistence across AppHost restarts
Call `GET https://localhost:7148/infra/redis` a few times, stop the AppHost (Ctrl+C), and run `docker ps`. **The 3 containers are still Up.** Start the AppHost again and call it again: the counter continues from where it was. Startup takes about 20 s instead of minutes.

### 2. Delete the volume
Stop the AppHost, then:
```powershell
docker rm -f (docker ps -a --filter name=redis- --format "{{.Names}}")
docker volume rm fooddelivery-redis-data
```
Run the AppHost and call `/infra/redis` → `{"ordersServed":1,...}`. The data lived in the volume, not the container.

### 3. Change a memory cap and watch it not apply
Change Mailpit's `"--memory=64m", "--memory-swap=64m"` to `"--memory=96m", "--memory-swap=96m"` and restart the AppHost. Then:
```powershell
docker inspect (docker ps --filter name=mailpit- --format "{{.Names}}") --format "Memory={{.HostConfig.Memory}}"
```
→ still `67108864` (64 MB): same container ID, old limit. Run `.\tools\infra.ps1 remove`, start the AppHost again, and it's now `100663296` (96 MB). Change it back afterwards and repeat the `remove`.
(This is exactly how the swap fix in this step first went unapplied to Postgres and Mailpit.)

### 4. Exceed the memory cap (OOM kill)
Use **Git Bash**. In PowerShell 5.1, piping text into `docker exec` adds a byte-order mark, which breaks the command (`ERR unknown command '﻿EVAL'`).
```bash
R=$(docker ps --filter name=redis- --format "{{.Names}}")
echo "EVAL \"for i=1,200000 do redis.call('SET','sandbox:filler:'..i,string.rep('x',1000)) end return 1\" 0" \
  | docker exec -i $R sh -c 'redis-cli -p 6380 -a "$REDIS_PASSWORD" --no-auth-warning'
docker inspect $R --format "Status={{.State.Status}} OOMKilled={{.State.OOMKilled}} ExitCode={{.State.ExitCode}}"
```
→ `Error: Server closed the connection`, then `Status=exited OOMKilled=true ExitCode=137`. `/infra/redis` now returns **500**, and the dashboard shows redis as exited.
Recover with `docker start $R`. Then `dbsize` = 1: the filler keys never reached a snapshot, and the counter lost its last increment (concept 6).
*Optional:* temporarily remove `"--memory-swap=128m"` (then `infra.ps1 remove` + run), repeat, and Redis survives at ~207 MB (`docker stats` shows 127 MiB / 128 MiB plus swap). That's the trap from concept 5.

### 5. Change the generated Postgres password
```powershell
dotnet user-secrets list --project src\AppHost\FoodDelivery.AppHost     # note the current Parameters:postgres-password value first!
dotnet user-secrets set "Parameters:postgres-password" "SomethingNew123" --project src\AppHost\FoodDelivery.AppHost
```
Run the AppHost:
- The Postgres container is **recreated** (new ID), because an environment variable changed.
- It reuses the **old volume**, and Postgres ignores the new `POSTGRES_PASSWORD`, which only applies when the data directory is first created. `docker logs` shows: `FATAL: password authentication failed for user "postgres"`.
- Kitchen **never starts**: it's stuck behind `WaitFor(sandboxDb)` and the dashboard shows it *Waiting*.

**Restore the original value** with `user-secrets set` and everything works again. Lesson: with persistent volumes, a credential change needs either the old password or a volume reset.

---

## Interview questions

**1. What's the difference between a container's lifetime and its data's lifetime?**
The container is a disposable process with a writable layer that disappears when it's removed. Data that must outlive it goes in a **volume** (managed by Docker) or a **bind mount** (a host folder). Recreating a container on the same volume keeps the data; deleting the volume loses it. That's why database containers are treated as replaceable while their volumes are treated as precious, and why "reset the database" means deleting the volume.

**2. A container with `--memory=128m` is using more than 128 MB and isn't killed. How?**
Without `--memory-swap`, Docker defaults it to twice `--memory`, so the container may use 128 MB of RAM **plus 128 MB of swap**. It's killed only at the combined limit. Set `--memory-swap` equal to `--memory` to disable swap. In Kubernetes, the memory limit is enforced on RAM and swap is usually off on nodes, so behaviour differs between environments, which is one more reason to set the flag explicitly.

**3. What does exit code 137 mean, and what do you do about it?**
137 = 128 + 9: the process got **SIGKILL**, most often from the kernel's OOM killer when the container hit its memory limit (`docker inspect` shows `OOMKilled=true`). Fixes: find what's growing (a leak, an unbounded cache, a huge query result), raise the limit if the workload genuinely needs it, and make the app limit itself below the container limit: Redis `maxmemory`, the .NET GC heap limit (`GCHeapHardLimit` / `DOTNET_GCHeapHardLimit`), Postgres `shared_buffers` and `work_mem`. A graceful error then happens before a hard kill.

**4. Redis `maxmemory-policy`: which would you choose for a cache vs a cart store?**
Cache: `allkeys-lru` or `allkeys-lfu`. Evicting old entries is the point, since a miss just reloads from the source. Cart or OTP store: `noeviction`. Losing a user's cart or a pending OTP silently is a bug, so it's better that writes fail with an error you can see, alert on and handle. You can also run separate Redis instances when one workload is a cache and another is a store.

**5. RDB vs AOF persistence in Redis?**
**RDB:** periodic point-in-time snapshots. Compact, fast restarts, but you lose everything since the last snapshot on a crash (we lost the last increment). **AOF:** logs every write. With `appendfsync everysec`, at most about 1 second is lost, at the cost of bigger files and more I/O; rewrites compact it. Many setups use both. In any case, data you can't lose belongs in a database with a write-ahead log, not in Redis.

**6. How does a .NET service receive its database connection string in Aspire, and in Kubernetes?**
In Aspire, `WithReference(db)` injects `ConnectionStrings__<name>` as an environment variable, and `GetConnectionString("<name>")` reads `ConnectionStrings:<name>`. In Kubernetes it's the same configuration contract: the value comes from a Secret mounted as an environment variable or file, or from a secret store such as Key Vault. The code doesn't change. That's the point of reading configuration by name instead of hard-coding it.

**7. Why does changing `POSTGRES_PASSWORD` not change the password of an existing Postgres container?**
The official image's entrypoint uses `POSTGRES_PASSWORD` (and `POSTGRES_USER`, `POSTGRES_DB`) **only when initialising an empty data directory**. With an existing volume it skips initialisation, and the stored password stays. To change it: `ALTER USER postgres PASSWORD '...'` with the old credentials, then update the secret; or wipe the volume in development.

**8. One Postgres server with many databases, or one per microservice?**
"Database per service" is about **ownership and coupling**: no service reads another's tables. A separate database on a shared server satisfies that. Separate servers add **runtime isolation**: one noisy service can't exhaust another's connections, CPU or I/O, and each can be versioned, scaled and backed up independently. In production, critical services often get their own server; locally, or with tight budgets, a shared server with strict per-service databases and logins is a reasonable compromise.

**9. Session vs persistent containers in Aspire: when do you want each?**
Session (the default): a clean, reproducible environment every run, which suits tests and CI; the cost is slow starts and no data between runs. Persistent: fast inner loop and data kept between runs, which suits daily development with databases. Costs: idle RAM use, configuration drift (changed runtime args not re-applied), and stale credentials. You manage them with `docker`/scripts or the dashboard.

---

## Review checklist
- [ ] Run the AppHost: `postgres`, `redis` and `mailpit` show Running in the dashboard; open Mailpit's web UI from its endpoint link.
- [ ] `/infra/postgres`, `/infra/redis` and `/debug/connection-strings` respond.
- [ ] Stop the AppHost and confirm the containers keep running; `.\tools\infra.ps1 stop` frees them.
- [ ] Experiments 1–5 reproduced (5: restore the password afterwards!).
- [ ] You can explain: lifetime vs volume, the swap trap, exit 137, why Aspire didn't re-apply the memory cap, and why the password change broke Postgres.
- [ ] Approved, then commit + tag `step-03-infrastructure-containers`.
