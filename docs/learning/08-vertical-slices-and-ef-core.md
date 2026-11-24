# Step 08 — Vertical slices + EF Core basics

> **Topic:** the first real service (Identity) as vertical slices, EF Core with Postgres, migrations, and seeding.
> **Tag when approved:** `step-08-vertical-slices-and-ef-core`

> **Since Step 09** `POST /users` (the `CreateUser` slice) is gone: verifying an email code creates the user. To repeat the experiments that use it, check out this step's tag.

## Why this matters for this project
Every later service needs the same three things: a way to organise code, a way to map objects to tables, and a safe way to change those tables while data is in them. This step sets the pattern for all of them, and creates the tables that login (Steps 09–16) builds on.

## What was added
| File | Role |
|---|---|
| [Entities.cs](../../src/Services/Identity/FoodDelivery.Identity/Data/Entities.cs) | `User`, `Role`, `UserRole`, and the fixed `RoleId` values |
| [IdentityDbContext.cs](../../src/Services/Identity/FoodDelivery.Identity/Data/IdentityDbContext.cs) | The mapping: keys, lengths, indexes, check constraint, role rows |
| [Migrations/](../../src/Services/Identity/FoodDelivery.Identity/Data/Migrations/) | `InitialCreate`, generated |
| [DatabaseSetup.cs](../../src/Services/Identity/FoodDelivery.Identity/Data/DatabaseSetup.cs) | Migrate and seed the admin at startup, Development only |
| [IdentityDbHealthCheck.cs](../../src/Services/Identity/FoodDelivery.Identity/Data/IdentityDbHealthCheck.cs) | Readiness |
| [CreateUser.cs](https://github.com/pran890/FoodDelivery/blob/step-08-vertical-slices-and-ef-core/src/Services/Identity/FoodDelivery.Identity/Features/Users/CreateUser.cs), [GetUser.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Users/GetUser.cs) | The two slices, Development only |
| [EmailAddress.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Users/EmailAddress.cs) | Decides when two emails are the same |
| [Identity tests](../../tests/FoodDelivery.Identity.Tests/) | 25 tests |
| [AppHost.cs](../../src/AppHost/FoodDelivery.AppHost/AppHost.cs) | + `identitydb` and `identity`. Redis, Mailpit and the Sandbox now start by hand (`WithExplicitStart`). |
| [ServiceDefaults](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/Extensions.cs) | + Npgsql traces: one span per SQL command |
| [dotnet-tools.json](../../dotnet-tools.json) | The `dotnet-ef` tool, pinned for this repo (`dotnet tool restore`) |

## The schema
Exactly as approved, and checked against the SQL the migration generates:
```
users       id uuid PK · email varchar(254), unique, CHECK (email = lower(email)) · created_at timestamptz
roles       id smallint PK · name varchar(32), unique        rows: 1 Customer, 2 RestaurantOwner, 3 Rider, 4 Admin
user_roles  user_id → users (CASCADE) · role_id → roles (RESTRICT) · granted_at · PK (user_id, role_id) · index (role_id)
```

Checking the proposal again before building found three problems *around* the tables. All were fixed in code:

| Problem | Fix |
|---|---|
| .NET keeps `İ` (U+0130) when lowercasing, Postgres makes it `i` (measured). Such an email would break the check constraint and return a 500. | ASCII emails only, checked before lowercasing |
| `GET /users/{id}` would have returned emails with no login | Both endpoints exist in Development only |
| .NET times have 100 ns precision, Postgres 1 µs, so `POST` and `GET` returned different values | Round before saving |

---

## Concepts

### 1. Vertical slices
```
Layers:  Controllers/  Services/  Repositories/  Dtos/    → one change touches four folders
Slices:  Features/Users/CreateUser.cs                     → one change, one file
```
A slice holds the endpoint, request, response and logic of **one use case**. `CreateUser.Response` and `GetUser.Response` look the same today and are still separate: slices change for different reasons, and sharing a type would tie them together. What *is* shared is a real domain rule, `EmailAddress`.

There's no repository layer. `DbContext` already is a unit of work, and `DbSet` already is a repository.

### 2. DbContext and change tracking
`SaveChanges` compares tracked objects with how they were loaded, and sends the differences in **one transaction** (here two INSERTs: the user and the role). It lives for one request and isn't thread-safe.

Measured (experiment 6):

| Read | SQL | Tracked objects | Same object when read twice |
|---|---|---|---|
| `db.Users` | all columns | 1 | yes |
| `.AsNoTracking()` | **the same SQL** | 0 | no |
| `.Select(...)` | **only those columns** | — | — |

So `AsNoTracking` saves memory and CPU in the app, not database work. A projection saves both, which is why `GetUser` uses one.

### 3. Let the database decide
`CreateUser` doesn't ask "does this email exist?" first. Two requests could both get "no" and both insert. It inserts, and translates the unique violation into 409:
```csharp
catch (DbUpdateException ex) when (ex.InnerException is PostgresException
    { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdentityDbContext.UniqueEmailIndex })
```
The constraint name matters: another unique violation would be a bug, not a duplicate email.

### 4. Migrations
1. Change the model.
2. `dotnet ef migrations add <Name> --project src\Services\Identity\FoodDelivery.Identity --output-dir Data/Migrations`
3. **Read the generated code and the SQL** (`dotnet ef migrations script`).
4. Apply it.

- The **snapshot** file is EF's record of the model after the last migration. It's what "pending changes" is compared with.
- `__EFMigrationsHistory` records which migrations a database has **by name only**. Experiment 3 shows what that means.
- **Applied at startup in Development only.** In production a deployment step should run a reviewed script, so that the app needs no right to alter tables, and a bad migration doesn't stop every instance from starting.

### 5. Two kinds of seed data
| | Roles | Admin |
|---|---|---|
| Same everywhere? | Yes | No, it comes from config |
| Where | `HasData`, part of the migration | Startup code |
| Safe to repeat? | A migration runs once | `ON CONFLICT DO NOTHING` |

The repository is public, so the committed admin is `admin@fooddelivery.local`. Use your own with:
```powershell
dotnet user-secrets set "Identity:AdminEmail" "you@example.com" --project src\Services\Identity\FoodDelivery.Identity
```
Two things to know: if that email already belongs to a customer, they **also** become an admin, and changing the setting does **not** remove the previous admin.

### 6. UUIDv7
It starts with a timestamp, so new keys go to the end of the index instead of random pages. It can't be guessed like 1, 2, 3. The trade-off is that the id reveals when the user was created.

### 7. Database retries
`EnableRetryOnFailure()` retries transient failures such as a dropped connection, never errors such as a unique violation. The catch: a transaction you open yourself must run inside `CreateExecutionStrategy().ExecuteAsync(...)`, or EF refuses it.

---

## Trade-offs and alternatives
| Choice | Why here | Alternatives |
|---|---|---|
| Vertical slices | Identity is mostly separate use cases | Clean Architecture, used for Order, Payment and Delivery (Step 30) |
| EF Core | Migrations, change tracking, LINQ | Dapper: full SQL control, no migrations. Often combined for heavy reads. |
| snake_case names | No quotes in SQL | EF's default `"UserRoles"` |
| Lowercased email + check | Simple and visible | `citext`; a second normalised column |
| Fixed `smallint` role ids | Small and stable | Role name as the key |
| Migrate at startup (Development) | Nothing to remember | SQL scripts or a migration bundle in the pipeline |
| ASCII emails only | Avoids look-alike and lowercasing problems | Supporting international addresses needs Unicode normalisation rules |

---

## Break-it experiments
All were run while building this step, on throwaway databases. **Undo each change afterwards.**

### 1. Add a column without a migration (the roadmap's experiment)
Add `public string? DisplayName { get; init; }` to `User` and start Identity.
→ it **refuses to start**: `PendingModelChangesWarning ... has pending changes. Add a new migration`. The test `Every_model_change_has_a_migration` fails too.
With that check switched off (`ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))`) it starts, and then fails when used: `42703: column "display_name" of relation "users" does not exist`.

### 2. Simultaneous sign-ups
In `CreateUser`, check with `AnyAsync` first and disable the `catch`.
→ `Simultaneous_sign_ups_create_exactly_one_user` fails, 3 of 3 runs.

**The first version of this test was itself broken.** It sent 20 parallel requests and, inside the full suite, *passed* with the bug present. Now a table lock lets reads through and holds back every INSERT until all requests are waiting, so the race happens every time. A race test that depends on luck proves nothing.

### 3. Edit a migration that's already applied
In `InitialCreate`, change `roles.name` from 32 to 64.
→ the existing database stays at **32**, a new one gets **64**, and `has-pending-model-changes` says **"No changes"**. Two databases with the same history and different schemas, and nothing notices. **Never edit an applied migration; add a new one.**

### 4. Roll a migration back
Add a migration for `DisplayName`, store a name, run `dotnet ef database update InitialCreate`, then update again.
→ the column is dropped, then comes back **empty**. `Down` restores the structure, not the data.

### 5. Two instances starting at once
| Database at the start | Both started | Result |
|---|---|---|
| Exists, empty | 3 of 3 | 1 migration, 4 roles, 1 admin |
| **Doesn't exist** | **2 of 3** | once, one instance crashed with `42P04: database already exists` |

EF locks while *migrating*, but creating the database isn't covered. Aspire creates ours beforehand.
The `fail: ... __EFMigrationsHistory` lines in the log on a first start are normal: EF looks for the table before it exists.

### 6. Tracked, untracked, projected
Compare `ToQueryString()` and `ChangeTracker.Entries().Count()` for the three reads. See the table in concept 2.

### 7. Do the tests catch real bugs?
| Break | Failing test |
|---|---|
| Remove the ASCII check | `Invalid_emails_are_rejected("asİha@...")`. The Kelvin-sign case still passes, because `MailAddress` happens to reject it as well. |
| Remove the rounding | `Created_user_can_be_read_back_unchanged` (`...3117204` vs `...3117200`) |
| Map the endpoints everywhere | `User_endpoints_do_not_exist_in_production` |

### Not verified
SQL spans in the dashboard. A test run confirmed that the app subscribes to Npgsql's traces, but I haven't looked at the dashboard itself.

---

## Interview questions

**1. Vertical slices vs layered architecture?**
Layers group by technical role, slices by use case. Slices keep a change in one place and let each use case be as simple or complex as it needs. The risk is duplication and drifting conventions, so share real domain rules and leave the rest separate.

**2. How does EF Core know what to save?**
The change tracker keeps each loaded entity and a snapshot of its original values. `SaveChanges` finds the differences and sends INSERT, UPDATE and DELETE statements in one transaction.

**3. When would you use AsNoTracking?**
For reads you won't modify. It skips tracking, saving memory and CPU, but the SQL is the same. Projecting into a DTO is usually better, since it also reads fewer columns.

**4. How do you guarantee unique emails under concurrency?**
With a unique index, and by handling the violation. "Check then insert" has a race between the two steps. Normalise before storing, so that equality means what you intend.

**5. Should an app migrate its database at startup?**
Fine in development. In production: instances can race, a failed migration stops the app from starting, and the app needs schema-changing rights. Prefer a pipeline step with a reviewed script, and changes that work with both the old and the new app version (expand, then contract).

**6. What if you edit an applied migration?**
Databases that already have it never run the change; new ones do. History is tracked by name, so nothing detects the difference. Add a new migration instead.

**7. Why UUIDv7 over UUIDv4 or bigint?**
v4 is random, which scatters index inserts. `bigint` is compact but guessable, and needs the database to assign it. v7 is time-ordered and can be created in the app. It does reveal the creation time.

**8. Does Postgres index foreign keys?**
No. It indexes primary keys and unique constraints. Without an index on the referencing column, deleting a parent row and joining from the parent side can need a full scan.

**9. Where should reference data and environment data be seeded?**
Reference data that's identical everywhere goes in migrations. Data that differs per environment goes in idempotent startup code or a deployment script, with values from configuration.

**10. How do you test a race condition?**
Force the interleaving instead of hoping for it: hold a lock or a barrier until every participant reaches the critical point, then release. Then prove that the test fails with the bug present.

---

## Review checklist
- [ ] Run the AppHost: only Postgres and Identity start. Try [identity.http](../../tools/http/identity.http).
- [ ] Look at the tables with a SQL client, or in Git Bash:
      `docker exec -it $(docker ps --filter name=postgres- --format "{{.Names}}") sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -U postgres -d identitydb -c "\d users"'`
- [ ] In the dashboard, a `POST /users` trace contains SQL spans.
- [ ] Experiments 1, 3 and 4 reproduced (and undone).
- [ ] `.\tools\ci.ps1` passes (AppHost off).
- [ ] You can explain: why there's no "does it exist?" query, why an applied migration must not be edited, why the roles are in a migration and the admin isn't, and why the first race test was worthless.
- [ ] Approved, then commit + tag `step-08-vertical-slices-and-ef-core`.
