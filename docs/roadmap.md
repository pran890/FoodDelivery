# Food Delivery App — Learning Roadmap (one topic per step)

## Context
A food delivery app in the style of Zomato/Swiggy, built **to learn, not to show off and not for speed**. There's no deadline, so the project is split into **small steps, each teaching one topic**. Each step is fully understood, experimented with and documented before the next one starts. It's a .NET microservices backend with React frontends, designed as if it had to scale. It runs locally on a **12 GB** Windows laptop, uses free tools only, and has no Python (PowerShell, SQL or .NET only). Nothing exists yet. The project goes in **`D:\FoodDelivery`**.

## How each step works
1. **Brief:** before any code, I explain the concept, what we'll build, and what to watch for.
2. **Build:** **I write the code**, the minimum needed to show the topic, with comments only where they explain the concept.
3. **Learning doc:** I write `docs/learning/NN-topic.md` with:
   - The concept in plain terms, and how it's done here, with links to the files.
   - Trade-offs and alternatives not chosen, and why.
   - **Break-it experiments**: steps to cause a failure and what you should see (logs, traces, HTTP responses).
   - **Interview questions** (5–10) with model answers.
4. **You review:** run it, do the experiments, read the doc, and ask questions. I update the code or doc until it's clear.
5. **Close:** commit, then tag `step-NN-topic` (e.g. `step-12-refresh-rotation`), so `git diff step-11..step-12` shows exactly what the topic added.
6. **No pulling forward:** later topics stay out of earlier steps. Each step's AppHost starts only the resources that step needs (RAM).

Steps are planned in detail one at a time, when we reach them. The tables below set the order and scope.

## Settled decisions
- **Authentication:** fully custom from day one. No OpenIddict and no ASP.NET Core Identity.
  - Our own Users, Roles and Sessions tables.
  - Email OTP: Redis, HMAC'd code, constant-time compare, attempt limit and TTL.
  - RS256 JWTs (15 minutes) with claims `sub`, `email`, `role`, `sid` and, from step 26, `restaurant_id`.
  - JWKS and discovery endpoints, and signing key rotation.
  - Refresh tokens (30 days) with rotation and reuse detection, one session per device.
  - Refresh token in an **HttpOnly cookie** (SameSite=Strict, path-scoped, CSRF header). The access token stays in memory in the browser.
  - Other services use only the standard JwtBearer setup in `AddFoodDeliveryJwt()`.
- **Messaging:** **Wolverine** (MIT). It acts as the in-process mediator, RabbitMQ transport, Postgres outbox and inbox, saga engine, and scheduled timeouts. No MediatR, MassTransit or Hangfire.
- **Code structure:**
  - Clean Architecture (Api/Application/Domain/Infrastructure) for **Order, Payment and Delivery**.
  - Vertical slices for **Identity, Catalog, Cart, Search, Review and Notification**.
- **Local run:** Aspire AppHost. Infrastructure runs in containers with memory caps and persistent volumes; services run as processes. Docker Compose only for deployment.
- **Data:** Postgres 17 + PostGIS in one container, one database per service, EF Core migrations per service; Redis.
- **Frontend:** pnpm workspace; Vite + React + TS, React Router 7, TanStack Query, Tailwind + shadcn/ui, orval-generated API client. The Vite dev proxy puts the SPA and API on one origin.
- **Seed data:** Jaipur (about 26.9124, 75.7873). Cities come from config.
- **Order statuses:** PaymentPending → Paid → Accepted → Preparing → ReadyForPickup → **RiderAssigned** → PickedUp → Delivered, plus Rejected, Cancelled and Refunded.

---

## Roadmap

### Stage A — Foundation
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 01 | Repo and build hygiene | `.slnx`, `global.json`, `Directory.Build.props`, central package management, analyzers, `.editorconfig`, `.gitignore`, git init. Updated CLAUDE.md. **Machine setup** (commands for you to run): `.wslconfig` with `memory=6GB` and pnpm via corepack. | Add a conflicting package version; see central package management reject it. |
| 02 | Aspire basics | AppHost + ServiceDefaults + one "hello" API; dashboard, resource model, service discovery | Change a port and watch discovery still resolve it. |
| 03 | Infrastructure containers | Postgres + PostGIS, Redis and Mailpit in the AppHost: persistent lifetime, volumes, memory caps, connection string injection | Exceed a memory cap; delete a volume. |
| 04 | OpenTelemetry | Traces, metrics, logs; a custom `ActivitySource` and `Meter`; W3C `traceparent` propagation between 2 APIs | Drop propagation and see the trace split in two. |
| 05 | Health and resilience | Liveness vs readiness checks; HTTP resilience pipeline (timeout, retry, circuit breaker) | Stop Postgres; watch readiness fail and the circuit open. |
| 06 | Testing foundation | xUnit v3, WebApplicationFactory, Testcontainers (PostGIS, Redis), a NetArchTest architecture test project | Make a test leak state between runs. |
| 07 | CI | GitHub Actions: build, test, cache. Pushing happens only when you ask. | Break a test and see the pipeline go red. |

### Stage B — Identity (custom authentication)
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 08 | Vertical slices + EF Core basics | Identity service skeleton; Users, Roles and UserRoles tables; migrations per service; Admin seeding | Add a column without a migration. |
| 09 | Email OTP | Redis OTP (HMAC, TTL, attempts, single-use, constant-time compare), `IOtpSender` → MailKit → Mailpit, response that never reveals whether an email exists | Brute-force attempts; expire a code. |
| 10 | Rate limiting | ASP.NET Core rate limiter algorithms (fixed, sliding, token bucket) per IP, plus a distributed counter per email in Redis | Two instances, and the in-memory limiter is bypassed. |
| 11 | JWT issuing | RSA key, `JsonWebTokenHandler`, RS256, claims, `kid`; JWT anatomy (header/payload/signature) | Tamper with the payload; switch to `alg: none`. |
| 12 | JWT validation in another service | Discovery + JWKS endpoints, `AddFoodDeliveryJwt()`, Catalog skeleton with one protected endpoint | Wrong audience; expired token; clock skew. |
| 13 | Sessions + refresh rotation | Sessions and RefreshTokens tables, hashed tokens, rotation, **reuse detection revokes the session**, "my devices" endpoints | Replay an old refresh token. |
| 14 | Cookies and CSRF | HttpOnly/Secure/SameSite cookie, path scope, CSRF header, logout | Read the cookie from JS (you can't); try a cross-site POST. |
| 15 | Role and policy authorization | Roles, policies, admin endpoints (grant role, block user → revoke sessions) | A blocked user keeps an access token for ≤ 15 minutes (accepted limitation). |
| 16 | Signing key rotation | SigningKeys table, Data Protection for private keys, multiple keys in JWKS, retire after the token lifetime | Retire the key too early; watch valid tokens fail. |

### Stage C — Gateway
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 17 | YARP gateway | Routes, service discovery, transforms, JWT check at the edge plus again in each service, forwarded headers | Bypass the gateway; the service still rejects bad tokens. |

### Stage D — Catalog
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 18 | EF Core modelling + optimistic concurrency | Restaurant, MenuCategory, MenuItem; owned types (opening hours), Money value object, `xmin` concurrency → 409 | Two concurrent edits. |
| 19 | PostGIS geospatial | Geography point, GiST index, `ST_DWithin` nearby query, `EXPLAIN ANALYZE`; Jaipur seeder (console app, idempotent) | Drop the index and compare the query plan. |
| 20 | Resource-based authorization + lifecycle | Owner-only edits (`OwnerUserId == sub`), Pending → Approved → Suspended state machine, admin approval | One owner edits another owner's restaurant. |
| 21 | OpenAPI as a contract | Built-in OpenAPI, document transformers (security scheme), Scalar UI, `.http` files | A breaking change that's visible in a diff of the generated spec. |

### Stage E — Customer frontend
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 22 | pnpm monorepo + Vite | Workspace, shared tsconfig/ui packages, Tailwind + shadcn, Vite proxy | — |
| 23 | Type-safe API client | orval → types + TanStack Query hooks; query keys, caching, invalidation | Change the API and watch TypeScript catch it. |
| 24 | SPA authentication | In-memory access token, single-flight refresh on 401, session restore on load, protected routes | Fire 5 requests that all get a 401 at once; only one refresh call is made. |
| 25 | Customer screens | OTP login, nearby restaurants (geolocation), menu page, my devices | Deny geolocation and check the fallback. |

### Stage F — Messaging
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 26 | RabbitMQ + Wolverine basics | Exchanges and queues, `RestaurantApproved` event → Identity grants RestaurantOwner and stores `restaurant_id` (replacing the step 15 shortcut) | Stop the consumer and see messages queue up. |
| 27 | Transactional outbox + inbox | Wolverine Postgres outbox; the dual-write problem | Crash between the database commit and the publish. |
| 28 | Retries, dead letters, idempotent consumers | Retry policies, error queue, poison messages, deduplication | Throw in a handler; process a message twice. |

### Stage G — Cart
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 29 | Redis modelling + atomic operations | Cart service: `cart:{userId}` hash, TTL, one-restaurant rule → 409, atomic updates (Lua or transactions) | Two concurrent add-to-cart calls. |

### Stage H — Order (Clean Architecture)
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 30 | Clean Architecture + DDD aggregate | Order service layers, Order aggregate with invariants and a status state machine, domain events | Make an illegal status change; the domain rejects it. |
| 31 | Synchronous calls between services | Place order: read the cart, re-validate prices with Catalog over HTTP with resilience | Catalog down or slow. |
| 32 | Idempotency keys | `Idempotency-Key` table, response replay | Double-click "place order". |
| 33 | Saga orchestration | Wolverine order saga; restaurant accept/reject; **scheduled 3-minute timeout** | Restart the service mid-saga. |
| 34 | Race conditions | Accept vs timeout both hitting at once, resolved by row-version concurrency | Fire both together. |
| 35 | Time-based business rules | `CancellationPolicy` with `TimeProvider`, cancel preview and cancel endpoints | Fake the clock just past the 60-second window. |

### Stage I — Payment (Clean Architecture)
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 36 | Payment gateway integration | Razorpay test mode order creation, payment state model | Razorpay API down. |
| 37 | Webhooks | HMAC signature check, idempotency, out-of-order events, Cloudflare Tunnel | Replay or forge a webhook. |
| 38 | Compensation | Reject/timeout → refund through the saga; refund webhooks | Refund call fails, then retries. |

### Stage J — Restaurant and admin panels
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 39 | Restaurant panel | Menu management, incoming orders (polling for now), accept with prep time / reject | Two tabs accept at once. |
| 40 | Admin panel | Approvals, order search, manual refund/cancel, block accounts | — |

### Stage K — Real-time
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 41 | SignalR basics | Notification hub, groups (`restaurant-{id}`, `order-{id}`), JWT in the query string, reconnect + catch-up via GET; live orders with sound | Drop the network; catch up on missed events. |
| 42 | SignalR scale-out | Redis backplane, 2 hub instances, publishing from another service | Remove the backplane and lose messages. |

### Stage L — Delivery, geo and the rider app
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 43 | Delivery service + Redis GEO | Rider model, online/offline, nearby riders with `GEOSEARCH` | — |
| 44 | Rider assignment | On `OrderAccepted`, pick the nearest free rider; `RiderAssigned`; no double-assignment; rider accept timeout | Two orders compete for one rider. |
| 45 | External geo APIs | Photon/Nominatim proxy (1 request per second outbound limit, User-Agent, Redis cache), OSRM route/ETA with straight-line fallback | OSRM down. |
| 46 | PWA basics | Rider app: manifest, service worker, geolocation, status updates | Go offline. |
| 47 | Live tracking | Location → Redis (latest only) → SignalR `order-{id}`; ETA every 30 seconds; Leaflet map in the customer app | A burst of updates shows why it isn't on RabbitMQ. |
| 48 | Rider simulator | .NET console app moving fake riders along OSRM routes | — |
| 49 | Addresses + map pin | Customer address book, Photon type-ahead through the backend, map pin | — |

### Stage M — Remaining services
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 50 | Search + CQRS read model | Postgres full-text search (`tsvector`, GIN, ranking) + `pg_trgm` for typos; a projection kept up to date by Catalog events | Delayed events show eventual consistency. |
| 51 | Email notifications | Templated emails triggered by events | — |
| 52 | Web push | VAPID, push subscription, service worker push; iOS caveats | — |
| 53 | Reviews | Only delivered orders can be reviewed (local state built from events); rating aggregates sent to Catalog and Search | — |

### Stage N — Hardening and operations
| # | Topic | What we build | Break-it |
|---|---|---|---|
| 54 | Caching | HybridCache/output cache for menus, invalidated by events | Serve stale data after an edit. |
| 55 | Edge protection | Gateway rate limiting, security headers | — |
| 56 | Load testing | k6 against our own services (never the public OSM servers) | Find the first bottleneck. |
| 57 | Containers for deployment | Multi-stage Dockerfiles, small images, `linux/arm64`, Compose | — |
| 58 | Kubernetes | kind: deployments, services, probes, config | Kill a pod. |
| 59 | Optional deployment | Oracle ARM VM + Cloudflare Tunnel | — |

**Later, optional:** Meilisearch, coupons, scheduled orders, Kafka/Redpanda experiment, React Native apps.

---

## Changes to CLAUDE.md (step 01)
- 12 GB RAM rules: WSL2 capped at 6 GB, 5–6 .NET services at a time, no Testcontainers alongside the full stack.
- Rewrite the auth section to describe custom auth as above. Remove OpenIddict, ASP.NET Core Identity and "replace OpenIddict later".
- Wolverine in place of MassTransit/Hangfire/MediatR. Mixed code structure rule. Aspire as the primary way to run locally.
- Replace "Build phases" with this step roadmap and its workflow rules (one topic per step, no pulling forward, a doc and a tag per step).
- `RiderAssigned` status. SignalR moves before Delivery. Jaipur seed data.

## Verification (every step)
- What the step demonstrates runs through the AppHost and is visible in the Aspire dashboard (traces and logs).
- The step's tests pass (`dotnet test`; `pnpm -r build` for frontend steps). CI is green from step 07 onwards.
- Every break-it experiment in the doc reproduces the described behaviour.
- `docs/learning/NN-topic.md` is reviewed and approved by you, then the commit is tagged `step-NN-topic`.
