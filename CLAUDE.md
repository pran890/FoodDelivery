# Food Delivery App (Zomato/Swiggy-style) — Learning Project

## Purpose and how we work
- **Learning project, not a business and not a showcase.** There's no deadline; efficient learning matters more than development speed.
- It's designed as if it must scale, so it uses microservices.
- Free tools and services only. No paid APIs or paid library versions.
- Single developer, running locally on a Windows laptop with **12 GB RAM**.
- The developer cannot run Python locally. Write scripts in **PowerShell, SQL or .NET**.

### Step workflow (mandatory)
The project is built in small steps, **one topic per step**. The roadmap with all steps is in [docs/roadmap.md](docs/roadmap.md).
1. **Brief:** explain the concept and what the step will build, before any code.
2. **Build:** Claude writes the minimum code that demonstrates the topic, with comments only where they explain the concept.
   - **Schema review gate:** before creating or changing any data structure, Claude presents the proposal in chat and **waits for the developer's approval**. This covers EF entities, tables, columns, types, keys, indexes, constraints, migrations, Redis key layouts, and integration event/message shapes. Nothing is created until it's approved. Steps that need this are marked 🗄️ in [TASKS.md](TASKS.md).
3. **Learning doc:** Claude writes `docs/learning/NN-topic.md` with:
   - The concept, and how it's done here, with file links.
   - Trade-offs and alternatives.
   - Break-it experiments with the expected results, verified by actually running them.
   - 5–10 interview questions with model answers.
4. **Review:** the developer runs it, does the experiments and reviews. Claude updates the code or doc until it's clear.
5. **Close:** only after the developer approves, commit and tag `step-NN-topic`.

Rules:
- **[TASKS.md](TASKS.md) is the status board.** Update it whenever a step or sub-task changes status, and keep the "Current step" checklist and the progress count accurate.
- **Never pull a later step's topic into an earlier step.** Check the latest `step-*` tag to see where the project is.
- Each step's AppHost starts only the resources that step needs.

## Apps
| App | Tech | Users | Arrives |
|---|---|---|---|
| Customer app | React + Vite, PWA | Customers | Step 22 |
| Restaurant panel | React + Vite | Restaurant owners | Step 39 |
| Admin panel | React + Vite | Admins | Step 40 |
| Rider app | React + Vite, PWA | Delivery partners | Step 46 |

- Web only. React Native may come later as a separate track.
- The frontends live in a pnpm workspace (`web/`) with shared packages: `api-client` (orval-generated types + TanStack Query hooks from each service's OpenAPI), `ui` (Tailwind + shadcn/ui) and `tsconfig`.
- Stack: React Router 7 (library mode), TanStack Query.
- The **Vite dev proxy** sends `/api` to the gateway, so the SPA and the API share one origin in development.

## Backend microservices (.NET 10, ASP.NET Core)
Each service owns its own database. No service reads another service's database.

| Service | Responsibility | Storage | Code structure |
|---|---|---|---|
| API Gateway | Single entry point, JWT check, routing | YARP | — |
| Identity | Custom auth: email OTP, users, roles, sessions, JWT signing, JWKS | Postgres + Redis | Vertical slices |
| Catalog | Restaurants, locations, menus, dishes, stock toggle | Postgres + PostGIS | Vertical slices |
| Search | Restaurant and dish search (read model) | Postgres full-text search first, Meilisearch later | Vertical slices |
| Cart | One cart per user, one restaurant per cart | Redis only | Vertical slices |
| Order | Order lifecycle, saga, accept/reject, cancellation policy | Postgres (Wolverine outbox) | **Clean Architecture** |
| Payment | Razorpay test mode, webhooks, refunds | Postgres | **Clean Architecture** |
| Delivery | Rider assignment, live location, geo calls | Postgres + PostGIS, Redis | **Clean Architecture** |
| Notification | SignalR hub, web push, email | Postgres (log) | Vertical slices |
| Review | Ratings and reviews | Postgres | Vertical slices |

- **Clean Architecture** means Api / Application / Domain / Infrastructure projects. It's used where the domain logic is rich: sagas, money and assignment.
- **Vertical slices** means one project organised by feature folder (`Features/<Feature>/<UseCase>`). It's used where the logic is mostly CRUD or infrastructure.
- **Shared code** is allowed only in `src/BuildingBlocks/FoodDelivery.Contracts` (integration event records, no logic), `src/BuildingBlocks/FoodDelivery.Web` (ProblemDetails, current user) and `src/ServiceDefaults`. Services never reference each other.

## Tech stack (all free)
- **Build:**
  - `global.json` pins the SDK.
  - `Directory.Build.props` holds the shared settings: analyzers in Recommended mode, warnings as errors, code style enforced in build.
  - **Central Package Management** (`Directory.Packages.props`): never put a `Version` on a `PackageReference`.
- **Local orchestration:** **.NET Aspire AppHost** is the primary way to run locally. Infrastructure runs in containers with memory caps, persistent lifetime and volumes; the .NET services run as processes. Docker Compose is only for deployment (step 57).
- **Database:**
  - PostgreSQL 17 + PostGIS (`postgis/postgis` image) in one container, a separate database per service.
  - EF Core + Npgsql, migrations per service.
  - Optimistic concurrency with `xmin`.
- **Cache / fast state:** Redis. Used for carts, OTP codes, rate-limit counters, rider latest location, Redis GEO for nearby riders, caches and the SignalR backplane.
- **Messaging:** **Wolverine** (MIT). It covers the in-process mediator, the RabbitMQ transport, the Postgres transactional outbox and inbox, sagas, and **scheduled messages for timeouts**.
  - **Do not use** MassTransit (v9 is paid), MediatR (commercial) or Hangfire.
- **Real-time:** SignalR in the Notification service, with groups like `restaurant-{id}` and `order-{id}`. JWT goes in the query string. On reconnect, the client also fetches current state with a `GET`.
- **Auth: fully custom** (no OpenIddict, no ASP.NET Core Identity). The developer chose this to learn it end to end.
  - **Tables:** `Users`, `Roles`, `UserRoles`, `Sessions` (one per device login), `RefreshTokens`, `SigningKeys`.
  - **Email OTP:**
    - Kept in Redis as `otp:{email}`, holding an **HMAC-SHA256** of the 6-digit code, with a 5-minute TTL, at most 5 attempts, single use, and constant-time comparison.
    - Rate limited per email (Redis) and per IP.
    - The response is always `202`, so it never reveals whether an email exists.
    - Sent through `IOtpSender` (`EmailOtpSender` via MailKit to Mailpit; a `WhatsAppOtpSender` can be added later).
  - **Access token:** RS256 JWT, 15 minutes, `kid` in the header.
    - Claims: `iss`, `aud=fooddelivery-api`, `sub`, `email`, `role` (one per role), `sid`, `jti`, `iat`, `nbf`, `exp`. `restaurant_id` is added from step 26.
  - **Refresh token:** a random value stored only as a SHA-256 hash, lasting 30 days.
    - **Rotated on every use.** If a consumed token is used again, **the whole session is revoked**.
    - Sent as an **HttpOnly, Secure, SameSite=Strict cookie**, scoped to the Identity auth path, and refresh requires a CSRF header.
    - The access token is kept only in browser memory.
  - **Keys:** private keys are encrypted with Data Protection. JWKS publishes the current and previous keys. Rotation retires the old key only after the token lifetime has passed.
  - **Endpoints:**
    - `POST /auth/otp/request`, `POST /auth/otp/verify`, `POST /auth/refresh`, `POST /auth/logout`.
    - `GET /users/me`, plus `/users/me/sessions` (list and revoke).
    - Admin: grant roles, block user, rotate keys.
    - `/.well-known/openid-configuration` and `/.well-known/jwks.json`.
  - **Roles:** Customer, RestaurantOwner, Rider, Admin. The admin account is seeded from config.
  - **Other services** validate only through the standard JwtBearer setup in `AddFoodDeliveryJwt()` (ServiceDefaults), reading JWKS. They never reference Identity code.
- **Email:** Mailpit locally. It catches OTP and notification emails.
- **Payments:** Razorpay test mode, with webhook signature verification and idempotency. Webhooks reach localhost through a free Cloudflare Tunnel.
- **Maps:** OpenStreetMap only, no Google Maps.
  - Display: Leaflet + react-leaflet.
  - Tiles: OSM standard tiles, for development only.
  - Type-ahead address search: Photon.
  - Geocoding and reverse geocoding: Nominatim. Max ~1 request per second, proper User-Agent, debounce ~500 ms, results cached in Redis.
  - Routes and ETA: OSRM public demo server; self-host later with one state's OSM data.
  - All geo calls go through the backend (Delivery service Geo module). Fall back to straight-line distance if OSRM is down.
  - Never load-test against public OSM servers.
- **Observability:** OpenTelemetry + the Aspire dashboard.
- **Testing:** xUnit v3, WebApplicationFactory, Testcontainers, NetArchTest, k6 (step 56).
- **CI/CD:** GitHub Actions; images in GitHub Container Registry. Push only when the developer asks.

## 12 GB RAM rules
- **WSL2:** capped at **6 GB** in `%USERPROFILE%\.wslconfig`.
- **Containers:** each one gets a memory cap. Postgres is capped at 512 MB and Redis at 128 MB. RabbitMQ only from step 26.
- **Services:** run at most 5–6 .NET services at once; the AppHost starts only what the current step needs.
- **Frontends:** at most two Vite dev servers at once.
- **Tests:** don't run Testcontainers suites while the full AppHost is running.
- **Prefer light options:** RabbitMQ over Kafka, Postgres full-text search or Meilisearch over OpenSearch, the Aspire dashboard over a full observability stack.

## Order flow (happy path)
1. Customer places an order: `POST /orders` with the cart, an address and an `Idempotency-Key`.
2. The gateway validates the JWT and routes to the Order service.
3. Order reads the cart, re-checks prices and stock with Catalog over HTTP (with resilience), saves the order as `PaymentPending`, and publishes `OrderCreated` through the Wolverine outbox in the same transaction.
4. Payment creates a Razorpay test order. The Razorpay webhook leads to `PaymentSucceeded` or `PaymentFailed`. The order becomes `Paid` and the cart clears.
5. The restaurant gets the order (polling until step 41, then SignalR).
   - A **Wolverine scheduled message** auto-rejects after 3 minutes with no response.
   - Accept (with prep time) → `OrderAccepted`.
   - Reject or timeout → `OrderRejected` + `RefundRequested`.
   - Row-version concurrency makes sure accept and timeout can't both win.
   - Ownership check: the JWT's `restaurant_id` must match the order.
6. Delivery hears `OrderAccepted`, finds the nearest free rider (Redis GEO / PostGIS) → `RiderAssigned`. The rider then updates status to `PickedUp` and `Delivered`.
7. Rider location arrives every 3–5 s → Delivery → Redis (latest point only) → SignalR group `order-{id}`. ETA is recalculated via OSRM about every 30 s. Tracking stops after delivery.

Order statuses: `PaymentPending → Paid → Accepted → Preparing → ReadyForPickup → RiderAssigned → PickedUp → Delivered`, plus `Rejected`, `Cancelled` and `Refunded`.

## Communication rules
- **RabbitMQ (via Wolverine + outbox):** important business events between services that must not be lost.
- **Redis + SignalR:** fast-changing state where only the latest value matters, such as rider location. Location updates never go through RabbitMQ.
- **Webhooks:** external server → our backend only (Razorpay → Payment).
- **SignalR:** our backend → browsers.
- **HTTP through the gateway:** simple reads (menus, order history) and synchronous checks (price re-validation).

## Cart rules
- One cart per user, with items from one restaurant only. Stored as `cart:{userId}` in Redis with a TTL of a few days.
- Adding an item from a different restaurant returns `409 Conflict`; the app asks whether to clear the cart and replace it.
- Order re-validates prices and availability at checkout.

## Cancellation policy (Order: `CancellationPolicy.Evaluate(order, now)`, using `TimeProvider`)
| Status | Customer can cancel? | Refund |
|---|---|---|
| `PaymentPending` | Yes, free | — |
| `Paid` (not yet accepted) | Yes | Full |
| `Accepted` / `Preparing` | Yes | Full within ~60 s; after that none or partial. The restaurant is still paid. |
| `PickedUp` | No (`409`). Admin only. | None by default. The restaurant and rider are still paid. |
| `Delivered` | No; issues go to support | — |

- Endpoints: `GET /orders/{id}/cancel-preview` and `POST /orders/{id}/cancel`, with row-version concurrency.

## PWA limitations (accepted)
- Rider location only updates while the rider app is open. Use the **rider simulator** (.NET console app) to move fake riders along OSRM routes.
- Web push on iPhone needs the PWA added to the home screen (iOS 16.4+).
- Location, service workers and push need HTTPS on a phone; use a Cloudflare Tunnel when testing on one.

## Data
- Seed city: **Jaipur** (centre about 26.9124, 75.7873). The seeder takes a list of cities from config, so adding a city is a config change.

## Out of scope
Real SMS, real payments, background rider GPS, native mobile apps (for now), ads, subscriptions.

## Later / optional
Meilisearch, coupons, favourites, scheduled orders, recommendations, earnings/payout reports, distance-based delivery fee, WhatsApp OTP sender, BFF for the SPA, Kafka/Redpanda experiment, React Native apps.

## Hosting (optional, step 59)
- Frontends: Netlify free plan or Cloudflare Pages.
- Backend: Oracle Cloud Always Free ARM VM running Docker Compose; build images for `linux/arm64`. Cloudflare Tunnel for HTTPS and webhooks.
- Free tiers change often, so keep everything runnable locally.
