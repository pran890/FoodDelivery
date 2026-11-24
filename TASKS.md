# Tasks and Status

The one place to see where the project stands. Updated whenever a step changes status.
Full step details: [docs/roadmap.md](docs/roadmap.md). Workflow rules: [CLAUDE.md](CLAUDE.md).

**Status key:** ⬜ Not started · 🔄 In progress · 👀 In review (waiting for you) · ✅ Done (approved, committed, tagged)

**Schema review** column: 🗄️ marks steps that create or change data structures (tables, columns, indexes, Redis keys, message shapes). In those steps the proposed schema is **reviewed and approved before any of it is created**.

---

## Current step: 10 — Rate limiting (🔄 In progress)
- [x] Brief
- [ ] 🗄️ Schema review: rate limit Redis keys (waiting for your approval)
- [ ] Build
- [ ] Learning doc + verified experiments
- [ ] **You:** review the doc and run the experiments
- [ ] Commit + tag `step-10-rate-limiting`

## Open questions / waiting on
- Nothing open.

---

## All steps

### Stage A — Foundation
| # | Step | Schema review | Status |
|---|---|---|---|
| 01 | Repo and build hygiene | | ✅ |
| 02 | Aspire basics | | ✅ |
| 03 | Infrastructure containers | | ✅ |
| 04 | OpenTelemetry | | ✅ |
| 05 | Health and resilience | | ✅ |
| 06 | Testing foundation | | ✅ |
| 07 | CI | | ✅ |

### Stage B — Identity (custom authentication)
| # | Step | Schema review | Status |
|---|---|---|---|
| 08 | Vertical slices + EF Core basics (Users, Roles) | 🗄️ | ✅ |
| 09 | Email OTP (Redis keys) | 🗄️ | ✅ |
| 10 | Rate limiting (Redis keys) | 🗄️ | 🔄 In progress |
| 11 | JWT issuing (claims contract) | 🗄️ | ⬜ |
| 12 | JWT validation in another service | | ⬜ |
| 13 | Sessions + refresh rotation | 🗄️ | ⬜ |
| 14 | Cookies and CSRF | | ⬜ |
| 15 | Role and policy authorization (blocking a user) | 🗄️ | ⬜ |
| 16 | Signing key rotation | 🗄️ | ⬜ |

### Stage C — Gateway
| # | Step | Schema review | Status |
|---|---|---|---|
| 17 | YARP gateway | | ⬜ |

### Stage D — Catalog
| # | Step | Schema review | Status |
|---|---|---|---|
| 18 | EF Core modelling + optimistic concurrency | 🗄️ | ⬜ |
| 19 | PostGIS geospatial + Jaipur seeder | 🗄️ | ⬜ |
| 20 | Resource-based authorization + lifecycle | 🗄️ | ⬜ |
| 21 | OpenAPI as a contract | | ⬜ |

### Stage E — Customer frontend
| # | Step | Schema review | Status |
|---|---|---|---|
| 22 | pnpm monorepo + Vite | | ⬜ |
| 23 | Type-safe API client | | ⬜ |
| 24 | SPA authentication | | ⬜ |
| 25 | Customer screens | | ⬜ |

### Stage F — Messaging
| # | Step | Schema review | Status |
|---|---|---|---|
| 26 | RabbitMQ + Wolverine basics (event contracts) | 🗄️ | ⬜ |
| 27 | Transactional outbox + inbox | 🗄️ | ⬜ |
| 28 | Retries, dead letters, idempotent consumers | | ⬜ |

### Stage G — Cart
| # | Step | Schema review | Status |
|---|---|---|---|
| 29 | Redis modelling + atomic operations | 🗄️ | ⬜ |

### Stage H — Order (Clean Architecture)
| # | Step | Schema review | Status |
|---|---|---|---|
| 30 | Clean Architecture + DDD aggregate | 🗄️ | ⬜ |
| 31 | Synchronous calls between services | | ⬜ |
| 32 | Idempotency keys | 🗄️ | ⬜ |
| 33 | Saga orchestration | 🗄️ | ⬜ |
| 34 | Race conditions | | ⬜ |
| 35 | Time-based business rules (cancellation) | | ⬜ |

### Stage I — Payment (Clean Architecture)
| # | Step | Schema review | Status |
|---|---|---|---|
| 36 | Payment gateway integration | 🗄️ | ⬜ |
| 37 | Webhooks | 🗄️ | ⬜ |
| 38 | Compensation (refunds) | 🗄️ | ⬜ |

### Stage J — Restaurant and admin panels
| # | Step | Schema review | Status |
|---|---|---|---|
| 39 | Restaurant panel | | ⬜ |
| 40 | Admin panel | | ⬜ |

### Stage K — Real-time
| # | Step | Schema review | Status |
|---|---|---|---|
| 41 | SignalR basics | | ⬜ |
| 42 | SignalR scale-out | | ⬜ |

### Stage L — Delivery, geo and the rider app
| # | Step | Schema review | Status |
|---|---|---|---|
| 43 | Delivery service + Redis GEO | 🗄️ | ⬜ |
| 44 | Rider assignment | 🗄️ | ⬜ |
| 45 | External geo APIs (cache keys) | 🗄️ | ⬜ |
| 46 | PWA basics | | ⬜ |
| 47 | Live tracking | | ⬜ |
| 48 | Rider simulator | | ⬜ |
| 49 | Addresses + map pin | 🗄️ | ⬜ |

### Stage M — Remaining services
| # | Step | Schema review | Status |
|---|---|---|---|
| 50 | Search + CQRS read model | 🗄️ | ⬜ |
| 51 | Email notifications | 🗄️ | ⬜ |
| 52 | Web push | 🗄️ | ⬜ |
| 53 | Reviews | 🗄️ | ⬜ |

### Stage N — Hardening and operations
| # | Step | Schema review | Status |
|---|---|---|---|
| 54 | Caching | | ⬜ |
| 55 | Edge protection | | ⬜ |
| 56 | Load testing | | ⬜ |
| 57 | Containers for deployment | | ⬜ |
| 58 | Kubernetes | | ⬜ |
| 59 | Optional deployment | | ⬜ |

---

## Progress
**9 / 59 done** · Current: Step 10 (in progress)

## Done log
| # | Step | Tag | Date |
|---|---|---|---|
| 01 | Repo and build hygiene | `step-01-repo-and-build-hygiene` | 2026-11-17 |
| 02 | Aspire basics | `step-02-aspire-basics` | 2026-11-17 |
| 03 | Infrastructure containers | `step-03-infrastructure-containers` | 2026-11-23 |
| 04 | OpenTelemetry | `step-04-opentelemetry` | 2026-11-23 |
| 05 | Health and resilience | `step-05-health-and-resilience` | 2026-11-24 |
| 06 | Testing foundation | `step-06-testing-foundation` | 2026-11-24 |
| 07 | CI | `step-07-ci` | 2026-11-24 |
| 08 | Vertical slices + EF Core basics | `step-08-vertical-slices-and-ef-core` | 2026-11-24 |
| 09 | Email OTP | `step-09-email-otp` | 2026-11-24 |
