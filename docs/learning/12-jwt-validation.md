# Step 12 — JWT validation in another service

> **Topic:** how a service checks a token it didn't issue: discovery, public keys (JWKS), and the settings that make the check safe.
> **Tag when approved:** `step-12-jwt-validation`

## Why this matters for this project
Step 11 made tokens, but nothing accepted them. From here on every service can answer "who is calling?" without asking Identity, without a database and without any shared secret. The setup is one line per service, so all of them check the same way.

## What was added
| File | Role |
|---|---|
| [WellKnown.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Tokens/WellKnown.cs) | Identity's two public addresses |
| [JwtExtensions.cs](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/JwtExtensions.cs) | `AddFoodDeliveryJwt()`: the one line |
| [Catalog](../../src/Services/Catalog/FoodDelivery.Catalog/) | A skeleton with one protected endpoint, `GET /whoami` |
| [Catalog tests](../../tests/FoodDelivery.Catalog.Tests/) | 20 tests with a fake Identity, no containers |
| [WellKnownTests.cs](../../tests/FoodDelivery.Identity.Tests/WellKnownTests.cs) | What Identity publishes |
| [catalog.http](../../tools/http/catalog.http) | Call Catalog with and without a token |

**Removed:** the Sandbox (Kitchen, Waiter and their tests). The docs of Steps 02–07 now link to their own tags. The one check worth keeping, "a dead database fails readiness but never liveness", moved to Identity's tests.

---

## Concepts

### 1. How a service gets the key
```
Catalog ── GET /.well-known/openid-configuration ──▶ Identity    { issuer, jwks_uri }
Catalog ── GET jwks_uri ───────────────────────────▶ Identity    { keys: [ { kid, n, e, … } ] }
then, for every request:  token's kid → matching key → check signature and claims    (no network)
```
The keys are fetched when the first token arrives and then kept. Measured: 20 requests, **1 download**. Through the AppHost the first call took 4.5 s, and later ones 0.05 s.

The addresses and formats are standards (OpenID Connect Discovery, RFC 7517), so any library in any language can read them. Publishing the key is safe: it can only *check*. The published fields are written out one by one, and a test fails if any part of the private key (`d`, `p`, `q`, …) appears.

### 2. What is checked, and what happens without it
| Setting | Removing it |
|---|---|
| `ValidIssuer` | Tokens from anyone are accepted |
| `ValidAudience` | Tokens made for another API are accepted |
| `ValidTypes = at+jwt` | Other kinds of JWT are accepted |
| `ClockSkew = 30 s` | Default 5 minutes: a 15-minute token lasts 20 |
| `ValidAlgorithms = RS256` | Nothing changed in the tests: the library refuses HS256 with an RSA key by itself. A second layer. |
| `MapInboundClaims = false` | `sub` and `role` arrive under long schema URLs, and `/whoami` finds no id |

### 3. Protected by default
The **fallback policy** applies to every endpoint that says nothing about authorization. `/whoami` has no `RequireAuthorization()` and is still protected. Forgetting it fails safely: a 401 you notice, and not a leak you don't.

Even unknown addresses answer 401 without a token, so a stranger can't explore which ones exist. Health checks opt out with `AllowAnonymous()`.

### 4. 401 or 503?
If Identity is down *before* Catalog ever got the keys, the library's answer was **401**, after 7–9 s of retries. But the token may be perfectly good, and a frontend would sign its user out. Catalog now answers **503** with `Retry-After`: "I can't tell right now". No token at all is still 401.

### 5. What caching costs
| Situation | Measured |
|---|---|
| Identity dies after the keys are known | Catalog keeps working: 200 in 0.05 s |
| Identity starts using a **new key** | One 401, then accepted: an unknown `kid` triggers a download |
| **Another** new key soon afterwards | Not picked up: at most one forced download per **5 minutes** |
| A **withdrawn** key | Still accepted: the library keeps the last good keys for **1 hour** |

The 5 minutes also stop an attacker from using invented `kid`s to make every service hammer Identity (tested: 20 unknown keys, at most one extra download).

Both limits matter for **Step 16**: publish a new key *before* using it, and decide whether a stolen key may stay valid for an hour (`UseLastKnownGoodConfiguration = false` switches that off).

### 6. Keys must come over HTTPS
Whoever can replace the keys on the way can impersonate everyone. In Development `https+http://identity` is allowed (service discovery). In Production `Jwt:Authority` must be set and be `https`, or **the service doesn't start**.

---

## Trade-offs and alternatives
| Choice | Why here | Alternatives |
|---|---|---|
| Every service checks tokens | No single point of failure, no trust between services needed | Only the gateway checks: less code, but anyone who gets past it is trusted |
| Public keys over HTTP | Rotation without redeploying | A key in each service's config: no dependency, painful to rotate |
| Discovery document | Standard, libraries expect it | Configure the JWKS address directly |
| Protected by default | Mistakes fail safely | Opt in per endpoint |
| Fake Identity in tests | Fast, no containers, any key or failure on demand | Run the real Identity: more realistic, slower |

---

## Break-it experiments
All were run while building this step. **Undo each change afterwards.**

### 1. Wrong audience, expired, clock difference (the roadmap's experiments)
See `TokenValidationTests`. A token that expired 20 s ago is accepted and one that expired 40 s ago isn't; the same holds for tokens from the future.
Remove `ClockSkew` → both 40-second tests fail.

### 2. Stop Identity
**Warm:** call `/whoami`, kill `FoodDelivery.Identity.exe`, call again → 200, three times in a row.
**Cold:** `Identity_down_before_the_keys_are_known_is_503_and_recovers`. Remove the `OnChallenge` logic → it answers 401.

### 3. Change the key
→ see the table in concept 5.

### 4. Forget to protect an endpoint
`/whoami` is that endpoint. Make the fallback policy allow everyone → 12 of 19 tests fail.

### 5. Default claim names
Remove `MapInboundClaims = false` → `A_valid_token_tells_who_the_caller_is` fails.

### 6. Do the tests catch real bugs?
15 bugs, one at a time. **14 caught:** any token type, default clock difference, default claim names, open fallback policy, audience not checked, issuer not checked, issuer setting removed, 401 on a cold start, probes needing a token, no check at startup, and for Identity: limited addresses, not cacheable, a leaked private part, a wrong modulus. **Not caught:** removing `ValidAlgorithms` (see concept 2).

Two bugs didn't compile at first, so an *older* program was tested and reported failures. A failing test proves nothing until you know what it ran.

### Not verified
Two machines whose clocks really differ. The tests make tokens with shifted times instead.

---

## Interview questions

**1. How does a service verify a JWT from another service?**
It reads the issuer's discovery document, downloads the public keys, picks the one named by `kid`, and checks the signature, issuer, audience, lifetime, type and algorithm.

**2. What is JWKS, and why is it safe to publish?**
A list of public keys in JSON. Public keys verify signatures but can't create them.

**3. What happens if the identity provider is down?**
Services with cached keys keep working. New tokens can't be issued. A service that never got the keys can't judge tokens and should say 503, not 401.

**4. How do you rotate keys without an outage?**
Publish the new key first, wait until caches have it, sign with it, and remove the old one after the last token it signed has expired.

**5. What is clock skew?**
The allowed difference between clocks. Too little rejects good tokens, too much extends every token's life.

**6. Validate at the gateway or in every service?**
Both is common: the gateway rejects early, and services don't trust the network. Checking only at the gateway means that anything inside can impersonate anyone.

**7. Authentication vs authorization, 401 vs 403?**
Who you are, and what you may do. 401: no valid identity. 403: identity known, not allowed.

**8. Why protect by default?**
Because forgetting is the usual mistake. With a fallback policy it causes a visible 401 and not a silent leak.

**9. Why must metadata be fetched over HTTPS?**
Whoever can change the keys in transit can sign tokens as anyone.

**10. How do you test authentication without the real provider?**
Replace only the network: serve your own discovery and keys, sign your own tokens, and leave the real validation in place. Don't replace the authentication handler, or the tests skip the code that matters.

---

## Review checklist
- [ ] Run the AppHost: Postgres, Redis, Mailpit, Identity and Catalog start.
- [ ] Sign in with [identity.http](../../tools/http/identity.http), copy the token, and call [catalog.http](../../tools/http/catalog.http).
- [ ] Experiments 2 (warm) and 5 reproduced.
- [ ] `.\tools\ci.ps1` passes (AppHost off).
- [ ] You can explain: how Catalog got the key, why it works while Identity is down, 401 vs 503, protected by default, and what caching means for a stolen key.
- [ ] Approved, then commit + tag `step-12-jwt-validation`.
