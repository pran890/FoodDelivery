# Step 11 — JWT issuing

> **Topic:** access tokens: what a JWT contains, how RS256 signs it, and why the classic attacks on it fail.
> **Tag when approved:** `step-11-jwt-issuing`

## Why this matters for this project
Ten services need to know who is calling. If each asked Identity on every request, Identity would be a bottleneck and a single point of failure. A **signed token** carries the answer with it: Identity signs once, and everyone can check the signature on their own.

## What was added
| File | Role |
|---|---|
| [TokenIssuer.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Tokens/TokenIssuer.cs) | Writes the token |
| [SigningKey.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Tokens/SigningKey.cs) | Loads the RSA key, and creates one in Development |
| [JwtOptions.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Tokens/JwtOptions.cs) | Issuer, audience, lifetime, key path |
| [VerifyOtp.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Auth/VerifyOtp.cs) | Returns the token |
| [TokenTests.cs](../../tests/FoodDelivery.Identity.Tests/TokenTests.cs), [SigningKeyTests.cs](../../tests/FoodDelivery.Identity.Tests/SigningKeyTests.cs) | The contract, the attacks, and the key |
| [.gitignore](../../.gitignore) | `*.pem`: the repository is public |

Nothing accepts the token yet. That's Step 12.

## The contract
Every service and frontend depends on this. **Changing it is a schema change.**
```
header   { "alg": "RS256", "kid": "<fingerprint of the public key>", "typ": "at+jwt" }
payload  { "iss": "https://identity.fooddelivery.local", "aud": "fooddelivery-api",
           "sub": "<user id>", "email": "...", "role": ["Customer"],
           "jti": "<random>", "iat": …, "nbf": …, "exp": iat + 900 }
response { "accessToken": "...", "tokenType": "Bearer", "expiresIn": 900,
           "user": { "id": …, "email": …, "roles": […] } }        Cache-Control: no-store
```
`sid` is added in Step 13 (sessions) and `restaurant_id` in Step 26.

---

## Concepts

### 1. Anatomy
`base64url(header) . base64url(payload) . signature`

Base64url is an *encoding*, not encryption. A real token from this step, decoded without any key:
```
{"alg":"RS256","kid":"WkswyJ3c…","typ":"at+jwt"}
{"aud":"fooddelivery-api",…,"email":"asha@example.com","role":["Customer"],…}
```
**Signed, not encrypted.** Never put anything in it that its holder may not read. The email is a deliberate exception.

### 2. Signing with RS256
| | RS256 (RSA) | HS256 (HMAC) |
|---|---|---|
| Create | Private key, only Identity | Shared secret |
| Check | Public key, anyone | **The same secret** |
| So | Services can check but not forge | Every service that can check can also forge |

Signing is slow and checking is fast, which suits "sign once, check on every request".

### 3. The claims, and what each prevents
| Claim | Without it |
|---|---|
| `iss` | A token from another system could be accepted |
| `aud` | A token meant for another API could be accepted |
| `exp` | A stolen token works forever |
| `typ: at+jwt` | Another kind of JWT signed with the same key could pass as an access token |
| `kid` | During key rotation (Step 16) services wouldn't know which key to use |
| `jti` | Tokens couldn't be told apart in logs or block lists |

### 4. A token can't be taken back
Nobody asks Identity, so nobody hears that a user was blocked or lost a role. Until `exp` the token works. **That's why it lives for 15 minutes.** The refresh token (Step 13) gets a new one and *can* be revoked.

### 5. The key
- **Development:** created once in `%LOCALAPPDATA%\FoodDelivery\identity-signing-key.pem`, outside the repository. It's a plain file, protected only by Windows permissions: you, administrators and the system.
- **Production:** `Jwt:SigningKeyPath` must point to an existing key, or Identity **refuses to start**. Silently creating one would invalidate every token and give each instance a different key.
- **`kid`** is a fingerprint of the public key (RFC 7638), so the same key always has the same id. Checked: the same `kid` after a restart.
- Identity signs something at startup, so a file with only a *public* key fails then and not at the first sign-in.
- **To get a new key:** stop Identity and delete the file. All tokens become invalid.

---

## Trade-offs and alternatives
Measured on this laptop (average of 500, two runs, nearly the same):

| Algorithm | Token | Sign | Check |
|---|---|---|---|
| **RSA 2048 (chosen)** | 723 characters | 0.9 ms | 0.06 ms |
| RSA 3072 | 893 | 2.5 ms | 0.07 ms |
| ES256 (P-256) | 467 | 0.27 ms | 0.26 ms |

| Choice | Why here | Alternatives |
|---|---|---|
| RS256 | Checked far more often than signed, supported everywhere | ES256: smaller tokens, slower to check here |
| JWT | Checked without asking Identity | Opaque tokens: can be revoked at once, but every request needs a lookup |
| 15 minutes | Limits the damage of a stolen token | Shorter: more refreshes. Longer: more damage. |
| `role` always a list | One case for clients | A string for one role (the library's usual behaviour for a single value) |
| Email in the token | Services can show it without asking | Leave it out: less personal data in logs and browsers |

---

## Break-it experiments
All were run while building this step. Most are tests, so run them with `dotnet test --project tests\FoodDelivery.Identity.Tests --filter-class "*TokenTests"`.

### 1. Change the payload, and `alg: none` (the roadmap's experiments)
Change `role` to `["Admin"]` and keep the signature → rejected: invalid signature.
Set `alg` to `none`, `None` or `nOnE`, and remove the signature → rejected.
Now set `RequireSignedTokens = false` in the test's validator → **all three `none` tests fail**. One careless setting accepts forged tokens.

### 2. The public key as an HS256 secret
Change `alg` to `HS256` and sign with the public key as the secret. A library that trusts the header's `alg` would accept it.
→ rejected. Removing `ValidAlgorithms` from the validator **didn't change that**: the library refuses HS256 with an RSA key on its own. `ValidAlgorithms` is a second layer.

### 3. Read a token without any key
Decode the two parts with any base64 tool → everything is readable, as shown in concept 1.

### 4. A key in memory
Use `RSA.Create(2048)` at every start instead of the file.
→ `Development_creates_a_key_once_and_keeps_using_it` and `Instances_starting_together_agree_on_one_key` fail: a restart or a second instance means another key, and tokens stop working.

### 5. The wrong type of token
A token signed with the **real** key but `typ: JWT` → rejected. Without `ValidTypes` in the validator → **accepted**.

### 6. Size and speed
See the table above.

### 7. Do the tests catch real bugs?
12 bugs in our code, one at a time, all caught: role as a string, wrong type, an extra claim, a 1-hour lifetime, a repeated `jti`, a cacheable response, RS512, a random `kid`, production creating a key, a short key, no test signature, and instances overwriting each other's key.

### 8. The bug CI found after this step was pushed
All tests passed locally, then CI failed with `ObjectDisposedException: RSAOpenSsl`.
The JWT library caches signing objects in `CryptoProviderFactory.Default`: **one cache for the whole process, looked up by key**. The tests run several Identities in one process with the same key. When one shut down and disposed its RSA object, another got the dead object from the cache. Whether that happened depended on the **test order**.

The fix: each `SigningKey` has its own `CryptoProviderFactory`. Remove that line → `Disposing_one_instance_does_not_break_another_with_the_same_key` fails every time.

One Identity per process wouldn't have hit it today, but **key rotation (Step 16) loads and disposes keys in a running process** and would have. Afterwards the suite passed in five different random orders (`dotnet test ... --seed 22`). Worth doing whenever tests share something.

### Not verified
The permissions of the key file on Linux (`600`): the code sets them, but no test checks it.

---

## Interview questions

**1. What are the parts of a JWT, and which are secret?**
Header, payload, signature. None is secret: the first two are only encoded. The signature makes changes detectable.

**2. RS256 or HS256?**
RS256 when others must check tokens without being able to create them. HS256 only when one party does both.

**3. What's the `alg: none` attack?**
The attacker sets the algorithm to none and drops the signature. A verifier that trusts the header accepts it. Require signatures and allow only the expected algorithms.

**4. What's the algorithm confusion attack?**
The token is switched to HS256 and signed with the *public* key as the secret. A verifier that picks the algorithm from the header and uses "the key" for both accepts it. Bind keys to algorithms.

**5. How do you revoke a JWT?**
You can't, without giving up stateless checks. Keep it short-lived, revoke the refresh token, and for emergencies keep a block list of `jti` or `sid`, or rotate the key.

**6. Why check `aud` and `iss`?**
So that a valid token from another system, or for another API, isn't accepted here.

**7. What belongs in an access token?**
What services need on every request and the user may see: id, roles, scope. No secrets, little personal data, and nothing that changes often.

**8. Where should a browser keep it?**
In memory. `localStorage` is readable by any script on the page (XSS). The refresh token goes in an HttpOnly cookie (Step 14).

**9. What is `kid` for?**
It tells the verifier which public key to use, so that keys can be rotated without breaking tokens that are still valid.

**10. JWT or opaque tokens?**
JWT for many services checking on their own. Opaque when instant revocation matters more than a lookup per request. A common mix: opaque outside, JWT inside, exchanged at the gateway.

---

## Review checklist
- [ ] Run the AppHost and sign in with [identity.http](../../tools/http/identity.http): the answer contains a token.
- [ ] Decode its first two parts with a base64 tool. (Don't paste real tokens into websites.)
- [ ] Experiments 1 and 5: weaken the validator and watch the tests fail.
- [ ] `.\tools\ci.ps1` passes (AppHost off).
- [ ] You can explain: signed vs encrypted, RS256 vs HS256, `alg: none`, why a token can't be revoked, and why production must not create a key.
- [ ] Approved, then commit + tag `step-11-jwt-issuing`.
