# Step 10 — Rate limiting

> **Topic:** limiting how often a client or an account may do something: ASP.NET Core's in-memory limiters per IP, and an exact distributed limit per email in Redis.
> **Tag when approved:** `step-10-rate-limiting`

## Why this matters for this project
Step 09 left a gap: each new code brings 5 more guesses, and nothing limited how many codes could be requested. An attacker could guess forever, or flood someone's inbox. This step closes it. The same ideas return at the gateway (Step 55).

## What was added
| File | Role |
|---|---|
| [OtpRateLimiter.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Auth/OtpRateLimiter.cs) | Per **email**, in Redis, shared by all instances |
| [IpRateLimiting.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Auth/IpRateLimiting.cs) | Per **IP**, in memory: fixed window, sliding window, token bucket; the 429 answer |
| [RequestOtp.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Auth/RequestOtp.cs), [VerifyOtp.cs](../../src/Services/Identity/FoodDelivery.Identity/Features/Auth/VerifyOtp.cs) | Use them |
| [ServiceDefaults](../../src/ServiceDefaults/FoodDelivery.ServiceDefaults/Extensions.cs) | Health checks are never limited |
| [EmailRateLimitTests.cs](../../tests/FoodDelivery.Identity.Tests/EmailRateLimitTests.cs), [IpRateLimitTests.cs](../../tests/FoodDelivery.Identity.Tests/IpRateLimitTests.cs) | 22 tests |

No new packages.

## The limits
| Per | Where | Limit | Setting |
|---|---|---|---|
| IP | everything except health checks | 100 per minute | `IpRateLimit:Global` |
| IP | `/auth/otp/request` | 10 per minute | `IpRateLimit:OtpRequest` |
| IP | `/auth/otp/verify` | burst of 10, then 10 per minute | `IpRateLimit:OtpVerify` |
| Email | code requests | 60 s apart, 5 per hour, 20 per day | `OtpRateLimit:Gap`, `PerHour`, `PerDay` |

Redis layout, as approved:
```
rl:otp:{email}   sorted set   one entry per ACCEPTED request: score = Redis time in ms, member = random id
                              expires 24 h after the last accepted request
```

---

## Concepts

### 1. Who is being limited?
| Per | Stops | Doesn't stop |
|---|---|---|
| IP | One machine hammering the server | Many machines. It also hits everyone who shares an address. |
| Email | Attacks on one account, from anywhere | Attacks spread thinly over many accounts |

Neither is enough alone. The IP limit protects the **server**, the email limit protects the **account**.

### 2. Four algorithms
| Algorithm | Idea | Here | Weakness |
|---|---|---|---|
| Fixed window | N per minute, then reset | Global | **20 instead of 10** around the reset (experiment 2) |
| Sliding window | The minute moves in 6 steps of 10 s | Request | Approximate, and gives no `Retry-After` |
| Token bucket | A bucket of 10 refills by 1 every 6 s | Verify | Allows a burst (here on purpose, for typing errors) |
| Sliding log | Store every request time | Email | Memory grows with the limit |

### 3. The sliding log in one sentence
"At most N in a window" means "**the N-th newest request has left the window**".
```lua
local entry = redis.call('ZREVRANGE', KEYS[1], limit - 1, limit - 1, 'WITHSCORES')
return entry == nil and 0 or score + window - now      -- how long to wait
```
One sorted set answers all three limits. The script checks **all** of them before adding an entry, uses **Redis's clock** so instances agree, and doesn't store refused requests, so trying again doesn't extend the wait.

### 4. Guessing vs lockout
Any limit per email can be used up by an attacker to lock the owner out. A looser limit gives more guesses.

| Limit | Guesses per day | Break-in chance, a full year of non-stop trying |
|---|---|---|
| 5 per hour | 600 | about 20% |
| **5 per hour and 20 per day (chosen)** | 100 | about 3.6% |
| 10 per day | 50 | about 1.8% |

A refused request **doesn't replace the current code**, and every code goes to the owner's inbox, so the owner can use the latest one for 5 minutes, whoever asked for it. That softens the lockout but doesn't prevent it: an attacker who also sends **5 wrong guesses** deletes that code. People already signed in aren't affected (from Step 13).

### 5. 429, and why it's not a hint
A request over the email limit gets `429` with `Retry-After`. That's a deliberate exception to "always 202" (Step 09). Known and unknown emails are counted the same way before any lookup, so the answer says nothing about accounts.

`Retry-After` is rounded **up** to whole seconds. Rounded down, clients would come back too early.

### 6. Whose address is it?
Behind a proxy every connection comes from the proxy, and the real client is in `X-Forwarded-For`. But **any client can send that header**. Trusting it lets an attacker pick a new address for every request (experiment 4).

- For now the header is ignored. From Step 17, only the gateway's header will be trusted.
- ⚠️ `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` makes ASP.NET Core trust it **from anyone**. Aspire sets that variable when *publishing* (not locally, which was checked). Remember this in Step 57.
- **Locally, all clients share one limit:** Aspire's proxy (`dcp`) makes every connection. Using up the limit over `::1` also blocked `127.0.0.1`.

**IPv6:** one home connection usually has a whole /64, so addresses in the same /64 share a limit.

### 7. In memory vs shared
| | In memory | Redis |
|---|---|---|
| Speed | No network call | One round trip |
| Two instances | **Twice the limit** | One limit |
| Restart | Forgotten | Kept |
| Redis down | Still works | Doesn't (here sign-in needs Redis anyway: 503) |

---

## Trade-offs and alternatives
| Choice | Why here | Alternatives |
|---|---|---|
| Sliding log | Exact, one key, at most 20 entries | Counters: 330 bytes per email for three limits, inexact at the reset. The sorted set takes 240 bytes (1 entry) to 998 (20). |
| Limits in the service | Protects it however it's reached | At the gateway (Step 55): stops traffic earlier |
| 429 for an email over its limit | The user knows to wait | Silent 202: hides the limit, but the user waits for nothing |
| No queueing | Refuse at once | `QueueLimit`: smooths bursts, holds connections |
| No captcha | Simple | A captcha or proof of work after a few requests |

## Known limits
- An attacker with many addresses can still request codes for **many different** emails. That sends unwanted mail and fills Redis: each email costs at least 240 bytes for 24 h. A captcha or a global cap on sent mail would help.
- Offices and mobile networks share addresses, so a strict IP limit hits innocent people.

---

## Break-it experiments
All were run while building this step. **Undo each change afterwards.**

### 1. Two instances (the roadmap's experiment)
Run `FoodDelivery.Identity.exe` twice on different ports and send 30 requests to `/auth/otp/verify`.
→ one instance allows **10**, two instances allow **20**. The email limit holds across instances: test `The_limit_is_shared_by_all_instances_and_by_all_spellings`.

### 2. Around the reset
A limiter of 10 per 6 s: send 10 at 5.5 s and 10 at 6.4 s.
→ fixed window: **10 + 10 = 20**. Sliding window: **10 + 0 = 10**. The same in 3 runs.

### 3. Count non-atomically
Replace the script with: read the length, return if it's at the limit, then add.
→ of 50 simultaneous requests with a limit of 3, **all 50** get through. 3 of 3 runs.

### 4. Trust the header
Send 30 requests, each with a different `X-Forwarded-For`.
→ normally **10** allowed. With `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`: **30 of 30**. One environment variable removes the IP limit.

### 5. Lock someone out
Request codes for `asha@example.com` until you get 429.
→ the owner can't request another, but the **last code still works**, unless the attacker also spends its 5 guesses: test `Second_request_within_the_gap_is_refused_and_the_first_code_still_works`.

### 6. Memory
1,000 emails on a throwaway Redis: the numbers are in the trade-offs table.

### 7. Do the tests catch real bugs?
12 bugs, put in one at a time, all caught: refused requests stored, no expiry, hour limit ignored, day limit ignored, code replaced before the check, email limit removed, health checks limited, no /64, verify not limited, header trusted, bucket never refilling, no middleware.

### Not verified
The rate limiting metrics in the dashboard.

---

## Interview questions

**1. Compare fixed window, sliding window, token bucket and sliding log.**
Fixed: cheap, double at the boundary. Sliding window: approximate and smooth. Token bucket: allows bursts with a steady average. Log: exact, memory grows with the limit.

**2. How do you rate limit across many instances?**
Keep the state in a shared store and make check-and-count one atomic step (a Lua script). Use the store's clock. Decide what happens when the store is down: fail open or closed.

**3. Why does the in-memory limiter fail behind a load balancer?**
Each instance counts only its own requests, so the real limit is N times the number of instances, and changes as you scale.

**4. Which key would you limit by?**
Whatever identifies the abuser *and* what's being protected: usually both. IP or API key for the server, account or email for the account.

**5. What's wrong with trusting X-Forwarded-For?**
Clients can set it. Trust it only from known proxies, and take the address the last trusted proxy added.

**6. How can a rate limit become an attack?**
By using up someone else's quota (lockout). Choose limits that heal, don't invalidate what the user already has, and combine several keys.

**7. What should a 429 contain?**
A `Retry-After`, a machine-readable body, and nothing that reveals whether the account exists.

**8. Fail open or fail closed when the limiter's store is down?**
Open for availability where abuse is merely annoying, closed where it's dangerous. For sign-in codes: closed.

**9. Why not store refused requests?**
A client that keeps trying would never get out, and an attacker could extend somebody's lockout for free. (Sometimes that's wanted, as a penalty.)

**10. How do you test time-based limits without waiting?**
Inject the clock, or change the stored times. Here the tests age the entries in Redis and check just before and just after the limit.

---

## Review checklist
- [ ] Run the AppHost and request two codes in a row: the second is 429 with `Retry-After`.
- [ ] Experiments 4 and 5 reproduced.
- [ ] `.\tools\ci.ps1` passes (AppHost off).
- [ ] You can explain: IP vs email limits, why two instances double one and not the other, the guessing/lockout trade-off, and why `X-Forwarded-For` is ignored.
- [ ] Approved, then commit + tag `step-10-rate-limiting`.
