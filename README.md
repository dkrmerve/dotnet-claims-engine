# Claims Engine

[![CI](https://github.com/dkrmerve/dotnet-claims-engine/actions/workflows/ci.yml/badge.svg)](https://github.com/dkrmerve/dotnet-claims-engine/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An insurance **claim processing service**: policies, claims, a strict claim lifecycle, payout
rules, fraud flags, annual limits, an audit trail, idempotent submission and optimistic
concurrency. ASP.NET Core minimal API on .NET 10, EF Core 10 on PostgreSQL, tested with xUnit
against a real PostgreSQL through Testcontainers, shipped as a Docker image whose build runs the
test suite (a red test means no image).

```
POST /policies  ->  POST /claims  ->  /review  ->  /approve  ->  /pay
                        |                 |                        |
                     Rejected          Withdrawn              audit trail
                (late, below deductible,
                 limit exhausted)
```

## What this project demonstrates

**If you are not an engineer:** an insurer receives a claim ("my car was hit, it costs 3 000"),
checks that the policy covers it, deducts the policyholder's own share, makes sure the yearly
limit is not exceeded, flags suspicious patterns for a manager, and pays out only after the right
person has approved. This service does exactly that, refuses anything that breaks the rules, and
keeps a full history of who did what and when. Every decision it takes is written down in the
[business rules table](#business-rules) and is tested.

**If you are an engineer:** the interesting parts are

- a pure domain model (`Policy`, `Claim`, `Money`, typed ids) with the rules as methods, no framework references, 99% covered;
- one class per use case, ports for persistence and time, no MediatR;
- two clearly separated validation layers: request validators at the edge that report *all* field errors at once, and domain invariants that throw a typed `DomainException` hierarchy mapped to HTTP in one table;
- optimistic concurrency with two tokens (application `Version` and PostgreSQL `xmin`), a unique index for idempotency races, and every command in one explicit transaction;
- JWT bearer authentication with role policies and ownership checks, a dev issuer for local use, and an OIDC mode for production;
- integration tests through `WebApplicationFactory` against a real PostgreSQL (Testcontainers), including genuinely parallel requests;
- production plumbing: ProblemDetails everywhere, correlation ids, JSON logs, health probes, Prometheus metrics, rate limiting, security headers, validated configuration, migrations with retry, non-root container, Trivy scan in CI.

## Business flow

1. A **Manager** creates a policy: holder, coverage type, coverage limit, deductible, effective period.
2. The **Claimant** (or an adjuster on their behalf) files a claim with an incident date, amount and description, optionally with an `Idempotency-Key` so retries are safe.
3. The engine decides immediately: not covered -> `422`; filed too late, below the deductible or limit exhausted -> the claim is created **already Rejected** (audit trail); suspicious -> flagged `RequiresInvestigation`.
4. An **Adjuster** starts the review. The claim enters the 14-day review SLA; `GET /claims/overdue` lists breaches.
5. Someone with enough **authority for the amount** approves (Adjuster <= 5 000, SeniorAdjuster <= 50 000, Manager any). Flagged claims need a Manager to clear the flag first. Alternatively the claim is rejected with a mandatory reason, or the Claimant withdraws it.
6. A **Manager** pays. The annual limit is re-checked inside the payment transaction; races on the same policy end in `409`, not in an overpaid policy.
7. Every step is in `GET /claims/{id}/history`.

## Business rules

| # | Rule | Where | Outcome |
|---|---|---|---|
| 1 | Policy must be `Active` and the incident date inside `[effectiveFrom, effectiveTo]` (inclusive) | `Policy.EnsureEligibleFor` | `422 policy_not_eligible` |
| 2 | Filing window: `filedAt - incidentDate(00:00 UTC)` must be <= 30 days; exactly 30 days is fine, 30 days + 1 s is late | `Claim.Submit` | `201`, claim `Rejected` with `LateFiling` |
| 3 | `eligiblePayout = min(claimed - deductible, coverageLimit - paidInPolicyYear)`; `claimed` must be > 0 | `Claim.Submit` | `<= 0` -> `201` with `BelowDeductible` / `LimitExhausted`; `claimed <= 0` -> `400` |
| 4 | State machine `Submitted -> UnderReview -> Approved -> Paid`; `Submitted\|UnderReview -> Rejected\|Withdrawn`; Paid/Rejected/Withdrawn are terminal | `Claim.EnsureTransition` | `409 invalid_transition` |
| 5 | Approval authority by payout: <= 5 000 Adjuster+, <= 50 000 SeniorAdjuster+, above Manager (limits inclusive) | `ApprovalAuthority` | `403 insufficient_authority` |
| 6 | Flag `RequiresInvestigation` when the holder has >= 3 other non-withdrawn claims in the last 365 days or `claimed >= 80 %` of the limit; flagged claims cannot be approved until a Manager clears the flag with a note | `Claim.Submit`, `Claim.Approve`, `Claim.ClearFlag` | `409 investigation_pending`, `400 note_required`, `409 flag_not_set` |
| 7 | Paid payouts per policy year (anchored at `effectiveFrom`, not the calendar year) never exceed the limit; re-checked at pay time with versioned claim *and* policy rows | `Claim.Pay`, `Policy.RecordPayout` | `422 limit_exhausted`, `409 concurrency_conflict` |
| 8 | Review SLA: `UnderReview` for more than 14 days (strictly) is overdue, measured from the moment the claim entered review | `ListOverdueClaimsHandler` | `GET /claims/overdue` |
| 9 | Rejection needs a reason from `LateFiling, BelowDeductible, LimitExhausted, NotCovered, Fraud, InsufficientEvidence, Other`; `Other` needs a note | `Claim.Reject` + validator | `400 request_validation_failed` / `rejection_note_required` |
| 10 | Idempotent submit: same key + same payload -> the original `201` (header `Idempotent-Replayed: true`); same key + different payload -> `422`; keys are scoped to the caller (`sub`), expire after 24 h and are swept afterwards; concurrent submits with one key (new or expired) create exactly one claim | `SubmitClaimHandler` + composite unique key + `Version` token | `422 idempotency_key_reused` |
| 11 | Every transition appends a `ClaimHistoryEntry` (at, actor role, from, to, note) | `Claim.Transition` | `GET /claims/{id}/history` |
| - | Ownership: Claimants only see and file on their own policies (`sub` = holder id) | `Ownership` | `403 not_owner` |

## Architecture

```
                         HTTP (JSON, RFC 7807 problems, JWT bearer)
                                        |
 +--------------------------------------v-----------------------------------------+
 | ClaimsEngine.Api            minimal API, endpoints, request validators,        |
 |                             JWT auth + role policies, rate limiter, health,    |
 |                             correlation id, JSON logs, metrics, ErrorMapper    |
 +--------------------------------------+-----------------------------------------+
                                        | commands / queries (one class each)
 +--------------------------------------v-----------------------------------------+
 | ClaimsEngine.Application    use cases, ownership checks, idempotency,          |
 |                             ports: IPolicyRepository, IClaimRepository,        |
 |                             IIdempotencyStore, IUnitOfWork, IClock             |
 +--------------------------------------+-----------------------------------------+
                                        | aggregates
 +--------------------------------------v-----------------------------------------+
 | ClaimsEngine.Domain         Policy, Claim, Money, ClaimRules, ApprovalAuthority |
 |                             DomainException hierarchy                          |
 |                             (zero package references)                           |
 +----------------------------------------------------------------------------------+
                                        ^
                                        | implements the ports
 +--------------------------------------+-----------------------------------------+
 | ClaimsEngine.Infrastructure EF Core 10 + Npgsql, migrations, xmin + Version    |
 |                             tokens, transactional unit of work, retrying       |
 |                             initializer                                        |
 +--------------------------------------+-----------------------------------------+
                                        |
                                   PostgreSQL 16

 Auth model:  token (sub, role) --> policy (AnyRole / AdjusterOrAbove / Manager / Claimant)
              --> handler: ownership (Claimant.sub == policy.holderId) --> domain: authority by amount
```

The dependency direction is inward: Api -> Application -> Domain, Infrastructure -> Application.
The domain never sees HTTP, EF or configuration; it receives `ClaimRules` and "now" as values.

## Quick start (Docker)

```bash
docker compose up --build        # builds the image (runs the test suite), starts PostgreSQL + API
curl -s localhost:8080/health/ready
open http://localhost:8080/docs  # interactive API reference (Scalar)
```

Everything has a local default (`docker-compose.yml`); copy `.env.example` to `.env` to change
ports, credentials, rule thresholds or the dev signing key. The dev token issuer is **on** in the
compose stack so you can mint tokens without an identity provider:

```bash
curl -s -X POST localhost:8080/auth/token -H 'Content-Type: application/json' \
  -d '{"subject":"manager-1","role":"Manager"}'
```

### Demo & inspecting the database

- `scripts/seed.sh` creates two policies and seven claims in every state (idempotent, curl only).
- `postman/ClaimsEngine.postman_collection.json` + `postman/local.postman_environment.json`: the full walkthrough with test scripts chaining tokens and ids.
- DBeaver / psql connection to the compose PostgreSQL: host `localhost`, port `5432`, database `claims`, user `claims`, password `claims`. Useful queries (claims per status, overdue, payouts per policy year, history of a claim, xmin vs Version) are in `docs/sql/queries.sql`.
- Logs: `docker compose logs -f api` (JSON lines, one per request, plus errors with `CorrelationId`).

## Local build

Prerequisites: .NET SDK 10.0, Docker (for the PostgreSQL test suite and the image).

```bash
dotnet tool restore                          # dotnet-ef, reportgenerator
dotnet build -c Release                      # zero warnings; TreatWarningsAsErrors is on
dotnet test  -c Release                      # 441 tests, coverage gates enforced, PostgreSQL via Testcontainers
CLAIMS_TESTS_DB=sqlite dotnet test           # fast profile (in-memory SQLite, PostgreSQL-only tests skipped)
dotnet format --verify-no-changes            # what CI checks
dotnet run --project src/ClaimsEngine.Api    # needs a PostgreSQL at ConnectionStrings:Default
```

To run the API outside Docker point `ConnectionStrings__Default` at any PostgreSQL 14+ and set
`Auth__DevIssuer__SigningKey` (32+ characters); `appsettings.Development.json` enables the dev
issuer.

## Configuration

All settings are bound from `appsettings.json` and environment variables (`Section__Key`) and
**validated at startup**; an invalid value stops the process with a clear message.

| Variable | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | *(required)* | Npgsql connection string incl. pool settings: `Minimum Pool Size=2;Maximum Pool Size=50;Timeout=15;Command Timeout=30` |
| `Auth__Authority`, `Auth__Audience` | unset | Production mode: validate tokens against this OIDC issuer (Entra ID, Keycloak, Auth0, ...) |
| `Auth__DevIssuer__Enabled` | `false` | Expose `POST /auth/token` (local only) |
| `Auth__DevIssuer__SigningKey` | *(required unless Authority is set)* | HS256 key, >= 32 chars, no default in code |
| `Auth__DevIssuer__Issuer` / `Audience` / `TokenLifetimeMinutes` | `claims-engine-dev` / `claims-engine` / `60` | Dev token claims |
| `Idempotency__TtlHours` | `24` | How long an `Idempotency-Key` answers replays |
| `Idempotency__SweepIntervalMinutes` | `60` | How often expired keys are deleted (`0` disables the sweeper) |
| `RateLimiting__PermitLimit` / `WindowSeconds` | `60` / `10` | Fixed window on write endpoints, per authenticated subject (per client address for anonymous calls) |
| `Proxy__TrustForwardedHeaders` | `false` | Honour `X-Forwarded-For` / `X-Forwarded-Proto` from a trusted ingress so limits and logs see the real client |
| `Rules__FilingWindowDays` | `30` | Rule 2 |
| `Rules__FrequentClaimantThreshold` / `FrequentClaimantWindowDays` | `3` / `365` | Rule 6 |
| `Rules__HighValueShareOfLimit` | `0.80` | Rule 6 |
| `Rules__ReviewSlaDays` | `14` | Rule 8 |
| `Rules__AdjusterApprovalLimit` / `SeniorAdjusterApprovalLimit` | `5000` / `50000` | Rule 5 |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` switches to readable console logs |

## API

All amounts are EUR: requests send plain decimals, responses return `{ "amount": 2500, "currency": "EUR" }`.
Enums are strings. `incidentDate` accepts `yyyy-MM-dd` or an ISO-8601 date-time with offset,
normalised to the UTC calendar day. Timestamps are UTC `DateTimeOffset`s.

| Method | Path | Who | Purpose |
|---|---|---|---|
| `GET` | `/health/live`, `/health/ready`, `/health` | anonymous | Liveness; readiness runs `SELECT 1` through the pool; all checks |
| `GET` | `/openapi/v1.json`, `/docs`, `/metrics` | anonymous | OpenAPI document, Scalar UI, Prometheus metrics |
| `POST` | `/auth/token` | anonymous, dev issuer only | Mint a token `{ "subject", "role" }` |
| `POST` | `/policies` | Manager | Create a policy, `201` + `Location` |
| `GET` | `/policies/{id}` | any role (Claimant: own only) | Read a policy |
| `POST` | `/claims` | any role (Claimant: own policy) | File a claim, optional `Idempotency-Key`, `201` + `Location` |
| `GET` | `/claims?policyId=&status=&page=&pageSize=` | any role (Claimant: own only) | Newest first, `page >= 1`, `pageSize 1..100` (default 20) |
| `GET` | `/claims/overdue` | Adjuster+ | Review SLA breaches |
| `GET` | `/claims/{id}`, `/claims/{id}/history` | any role (Claimant: own only) | Claim and audit trail |
| `POST` | `/claims/{id}/review` | Adjuster+ | `Submitted -> UnderReview`, optional `{ "note" }` |
| `POST` | `/claims/{id}/approve` | Adjuster+ (amount authority applies) | `UnderReview -> Approved` |
| `POST` | `/claims/{id}/reject` | Adjuster+ | `{ "reason", "note"? }` |
| `POST` | `/claims/{id}/pay` | Manager | `Approved -> Paid`, limit re-check |
| `POST` | `/claims/{id}/withdraw` | Claimant (owner) | `Submitted\|UnderReview -> Withdrawn` |
| `POST` | `/claims/{id}/clear-flag` | Manager | `{ "note" }` required |

### Authentication and authorization

Bearer JWTs with two claims: `sub` (the caller; **Claimants use their holder id**) and `role`
(`Claimant`, `Adjuster`, `SeniorAdjuster`, `Manager`). Two modes, chosen by configuration:

- **External OIDC issuer (production):** set `Auth__Authority` and `Auth__Audience`. The JWT
  bearer handler downloads the discovery document and signing keys and validates issuer,
  audience, signature and lifetime. Map your provider's role claim to `role` (Entra ID: app roles;
  Keycloak: a `role` mapper on the client; Auth0: an action adding the claim). The dev issuer
  cannot be enabled in this mode (startup validation fails).
- **Dev issuer (local):** no authority set, `Auth__DevIssuer__SigningKey` (32+ chars) required,
  `Auth__DevIssuer__Enabled=true` exposes `POST /auth/token` which mints one-hour HS256 tokens.
  The compose stack uses this mode; the key in `docker-compose.yml`/`.env.example` is a documented
  local default, never a production secret.

Authorization is layered: an endpoint **policy** on the role (`403 forbidden_role`), an
**ownership** check for Claimants in the use case (`403 not_owner`), and the domain's
**authority-by-amount** rule (`403 insufficient_authority`). A test
(`EndpointSecurityTests`) fails if any endpoint is mapped without a policy or `AllowAnonymous`.

## Walkthrough (curl)

```bash
BASE=http://localhost:8080
tok() { curl -s -X POST $BASE/auth/token -H 'Content-Type: application/json' \
        -d "{\"subject\":\"$1\",\"role\":\"$2\"}" | sed -E 's/.*"accessToken":"([^"]+)".*/\1/'; }
MANAGER=$(tok manager-1 Manager); ADJUSTER=$(tok adjuster-1 Adjuster)
HOLDER=33333333-3333-4333-8333-333333333333

# 1. policy (Manager)
POLICY=$(curl -s -X POST $BASE/policies -H "Authorization: Bearer $MANAGER" -H 'Content-Type: application/json' -d "{
  \"policyNumber\":\"POL-2026-001\",\"holderId\":\"$HOLDER\",\"coverageType\":\"Auto\",
  \"coverageLimit\":20000,\"deductible\":500,\"effectiveFrom\":\"2026-01-01\",\"effectiveTo\":\"2026-12-31\"}" \
  | sed -E 's/.*"id":"([^"]+)".*/\1/')

# 2. claim (Claimant = the holder), idempotent
CLAIMANT=$(tok $HOLDER Claimant)
CLAIM=$(curl -s -X POST $BASE/claims -H "Authorization: Bearer $CLAIMANT" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 6f9c1c1e-2b4a-4b8e-9b0c-1a2b3c4d5e6f' -d "{
  \"policyId\":\"$POLICY\",\"incidentDate\":\"$(date -u -d '-5 days' +%F)\",
  \"claimedAmount\":3000,\"description\":\"Rear-ended at a traffic light.\"}" | sed -E 's/.*"id":"([^"]+)".*/\1/')
#    -> 201, status Submitted, eligiblePayout 2500 (3000 - 500 deductible)

# 3. review, approve (Adjuster: 2500 <= 5000), pay (Manager)
curl -s -X POST $BASE/claims/$CLAIM/review  -H "Authorization: Bearer $ADJUSTER" -H 'Content-Type: application/json' -d '{"note":"Photos received."}'
curl -s -X POST $BASE/claims/$CLAIM/approve -H "Authorization: Bearer $ADJUSTER"
curl -s -X POST $BASE/claims/$CLAIM/pay     -H "Authorization: Bearer $MANAGER"

# 4. audit trail and a rule violation
curl -s $BASE/claims/$CLAIM/history -H "Authorization: Bearer $CLAIMANT"
curl -s -X POST $BASE/claims/$CLAIM/pay -H "Authorization: Bearer $MANAGER"   # 409 invalid_transition
```

Every error is an RFC 7807 problem with a stable `code`:

```json
{ "type": "https://github.com/dkrmerve/dotnet-claims-engine/docs/errors#invalid_transition",
  "title": "Conflict", "status": 409, "detail": "Cannot move claim from Paid to Paid.",
  "instance": "/claims/7847.../pay", "code": "invalid_transition",
  "correlationId": "35d5d455e21b45fca2a7befcc7fb5213", "traceId": "00-..." }
```

## Error catalog

Full descriptions in [`docs/errors.md`](docs/errors.md); the mapping lives in one place
(`src/ClaimsEngine.Api/Errors/ErrorMapper.cs`) and is unit-tested.

| Code | HTTP | When |
|---|---|---|
| `request_validation_failed` | 400 | Edge validation; `errors` dictionary lists every bad field |
| `invalid_request` | 400 | Malformed JSON, wrong field type, missing body, `page=abc` |
| `validation_error`, `rejection_note_required`, `note_required` | 400 | Domain `ValidationException` |
| `unauthenticated` | 401 | No/expired/badly signed token, or no `sub`/`role` |
| `forbidden_role` | 403 | Role may not call the endpoint |
| `insufficient_authority` | 403 | Approval above the role's limit; wrong role for withdraw/clear-flag |
| `not_owner` | 403 | Claimant reaching someone else's policy or claim |
| `not_found` | 404 | Unknown id, non-GUID id, unknown route |
| `method_not_allowed` | 405 | Wrong HTTP method |
| `invalid_transition`, `investigation_pending`, `flag_not_set` | 409 | State machine and flag rules |
| `concurrency_conflict` | 409 | Row changed between load and save (`Version` / `xmin`) |
| `duplicate_key` | 409 | Unique index violation (for example a duplicate policy number) |
| `payload_too_large` | 413 | Body over 64 KiB |
| `unsupported_media_type` | 415 | Not `application/json` |
| `policy_not_eligible`, `idempotency_key_reused`, `limit_exhausted` | 422 | Business rules 1, 10, 7 |
| `rate_limited` | 429 | Too many writes per client address; `Retry-After` set |
| `internal_error` | 500 | Unexpected; logged with the correlation id, no internals exposed |

## Edge cases we handle

Each bullet names the test that proves it (full list: [`docs/TEST-CATALOG.md`](docs/TEST-CATALOG.md), 268 test methods).

- **Filing window boundary** - exactly 30 days accepted, 30 days + 1 s late, future incident dates rejected, incident exactly on `effectiveFrom`/`effectiveTo` inside: `FilingWindow_Exactly30Days_IsAccepted`, `FilingWindow_30DaysPlusOneSecond_IsLateFiling`, `FilingWindow_IncidentDateInTheFuture_IsValidationError`, `Eligibility_IncidentExactlyOnEffectiveFrom_IsInsidePeriod`, `Eligibility_IncidentExactlyOnEffectiveTo_IsInsidePeriod`, `Submit_ExactlyThirtyDays_IsAccepted`.
- **Payout maths** - claimed == deductible, remainder exactly equal to the remaining limit, remaining limit zero, zero deductible, deductible >= limit refused at creation: `Payout_ClaimedEqualsDeductible_IsRejectedBelowDeductible`, `Payout_ClaimedMinusDeductibleExactlyEqualsRemainingLimit_PaysFullRemainder`, `Payout_RemainingLimitZero_IsRejectedLimitExhausted`, `Payout_ZeroDeductible_PaysWholeClaimedAmount`, `Policy_Create_DeductibleAtOrAboveCoverageLimit_IsRejected`, `CreatePolicy_DeductibleNotBelowLimit_Is400`.
- **Money** - negative, more than two decimals, mixed currencies rejected; half-even rounding; `NaN` in JSON is a 400: `Money_NegativeAmount_IsRejected`, `Money_MoreThanTwoDecimals_IsRejected`, `Money_MixedCurrencies_CannotBeCombinedOrCompared`, `Money_Round_RoundsHalfToEvenToTwoDecimals`, `Submit_AmountNaN_Is400InvalidRequest`.
- **Policy year, not calendar year** - windows anchored at `effectiveFrom` (leap day included); a paid claim from the previous policy year does not consume the current one: `PolicyYear_IsAnchoredAtEffectiveFrom_NotCalendarYear`, `AnnualLimit_SubmitUsesPolicyYearOfIncident_NotCalendarYear`, `Submit_PaidClaimFromPreviousPolicyYear_DoesNotConsumeCurrentYear`, `Submit_LimitConsumedByPreviousPolicyYear_IsNotCounted`.
- **Fraud flag** - exactly 3 prior claims flag, 2 do not; withdrawn excluded; a claim exactly 365 days old counts; exactly 80 % of the limit flags: `Flag_ExactlyThreeOtherClaims_Flags_TwoDoNot`, `Submit_ThreeOtherClaimsInWindow_FlagsClaim_WithdrawnExcluded`, `Submit_ClaimFiledExactly365DaysAgo_IsInsideTheWindow`, `Flag_ClaimedExactly80PercentOfLimit_Flags`, `Submit_FourthClaimInAYear_IsFlagged_WithdrawnExcluded`.
- **Authority thresholds** - 5 000.00 ok / 5 000.01 forbidden for Adjuster, same at 50 000 for SeniorAdjuster: `Authority_ThresholdBoundaries`, `Authority_AdjusterOn5000Point01_Is403_SeniorAdjusterOk`, `Authority_SeniorAdjusterOn50000Point01_Is403_ManagerOk`.
- **State machine, every pair** - 6 states x 6 actions table-driven, plus double approve, pay before approve, withdraw after approval, clear-flag on an unflagged claim: `StateMachine_EveryStateActionPair_BehavesPerTable`, `StateMachine_DoubleApprove_IsInvalidTransition`, `Transition_PayBeforeApprove_Is409`, `Transition_WithdrawAfterApproval_Is409_WithdrawFromReview_IsAllowed`, `Flag_ClearOnUnflaggedClaim_Is409FlagNotSet`.
- **Idempotency** - identical replay, different body, key reused across policies, 24 h TTL edge, parallel submits with one key create exactly one claim (unique index + replay of the stored response), the same key from two callers is two independent claims, parallel renewals of an *expired* key create exactly one new claim (`Version` token + replay), expired keys are swept: `Idempotency_SameKeySameBody_ReturnsIdentical201AndSameClaimId`, `Idempotency_SameKeyDifferentBody_Is422`, `Idempotency_KeyReusedAcrossPolicies_Is422`, `Idempotency_KeyExpiresAfter24Hours_ThenCreatesNewClaim`, `Idempotency_KeyAtExactlyTtl_StillReplays`, `Concurrency_ParallelSubmitsWithSameIdempotencyKey_CreateExactlyOneClaim`, `Idempotency_DuplicateKeyOnCommit_ReplaysWhatTheWinnerStored`, `Idempotency_SameKeyFromTwoSubjects_CreatesTwoIndependentClaims`, `Idempotency_ParallelSubmitsWithSameExpiredKey_RenewExactlyOnce`, `Idempotency_ExpiredKeyRenewedConcurrently_LoserReplaysTheWinner`, `Sweeper_RunsAtStartup_AndDeletesOnlyExpiredKeys`.
- **Transactions** - the unit of work runs the use case once when the commit succeeds, and a transient failure *during* COMMIT is surfaced (500, nothing persisted) instead of re-running the work: `UnitOfWork_WorkRunsOnce_WhenCommitSucceeds`, `UnitOfWork_TransientFailureDuringCommit_IsNotRetried_AndSurfacesAsUnknownOutcome`, `Pay_WhenCommitOutcomeIsUnknown_Is500_AndNothingIsPersisted`.
- **Concurrency** - two real parallel pays on one claim, two parallel pays on one policy that together exceed the limit, two contexts loading the same row, `xmin` changing on every write: `Concurrency_ParallelPaysOnSameClaim_ExactlyOneSucceeds`, `Concurrency_ParallelPaysOnOnePolicyExceedingLimit_NeverOvershoot`, `Concurrency_TwoContextsLoadSameClaim_SecondSaveIsConcurrencyConflict`, `Concurrency_XminChangesOnEveryWrite`.
- **Atomicity** - a failure after the first write inside pay or submit persists nothing: `Atomicity_PayFailingAfterFirstWrite_PersistsNothing`, `Atomicity_SubmitFailingAfterFirstWrite_PersistsNeitherClaimNorIdempotencyKey`.
- **Database constraints, bypassing the domain** - unique idempotency key, check constraints, FK with `ON DELETE RESTRICT`: `Constraint_DuplicateIdempotencyKey_IsRejectedByUniqueIndex`, `Constraint_NegativeClaimedAmount_IsRejectedByCheckConstraint`, `Constraint_DeductibleAboveLimit_IsRejectedByCheckConstraint`, `Constraint_PolicyWithClaims_CannotBeDeleted_OnDeleteRestrict`, `Constraint_ClaimForUnknownPolicy_IsRejectedByForeignKey`.
- **Input hardening** - unknown enum lists allowed values, description > 2 000, empty body / malformed JSON are 400 not 500, non-GUID route id is 404, body > 64 KiB is 413, wrong media type 415, missing token 401, a legacy role header is ignored: `CreatePolicy_UnknownEnum_ListsAllowedValues`, `Submit_DescriptionOver2000Chars_Is400`, `Request_MalformedJson_Is400InvalidRequest_Not500`, `Request_EmptyBody_Is400`, `Route_NonGuidId_Is404WithProblem`, `Request_BodyOver64KiB_Is413PayloadTooLarge`, `Request_UnsupportedMediaType_Is415WithProblem`, `Auth_NoToken_Is401Unauthenticated`, `Auth_StrayRoleHeaderOnGet_IsIgnored`.
- **Overdue** - exactly 14 days not overdue, 14 days + 1 s overdue, measured from review start not filing: `Overdue_Exactly14Days_IsNotOverdue_14DaysPlusOneSecond_Is`, `Overdue_UsesReviewStart_NotFiledAt`, `Overdue_Exactly14Days_NotListed_14DaysPlusOneSecond_Listed_FromReviewStart`.
- **Time** - a `+03:00` incident timestamp is normalised to the UTC day; non-ISO date strings rejected; all stored instants are UTC: `Submit_IncidentDateTimeWithPlus3Offset_IsNormalisedToUtcDay`, `IncidentDates_ParsesIsoFormsToUtcDay`, `IncidentDates_RejectsNonIsoForms`, `Submit_HappyPath_Returns201WithLocation_AndIsReadable` (asserts a zero offset).
- **Pagination** - `pageSize` 0 and 101 are 400, defaults documented, Claimant scope: `List_InvalidQuery_Is400WithFieldError`, `List_FiltersPagesAndScopesToClaimant`, `ListClaims_InvalidPaging_IsValidationError`.
- **Auth** - expired token, wrong signing key, wrong role, ownership, dev issuer off -> 404, token without `sub`: `Auth_ExpiredToken_Is401`, `Auth_WrongSigningKey_Is401`, `Auth_ValidTokenWrongRole_Is403ForbiddenRole`, `Auth_ClaimantReadingSomeoneElsesClaim_Is403NotOwner`, `DevIssuer_WhenDisabled_TokenEndpointIs404`, `Auth_TokenWithoutSubClaim_Is401`.
- **Operations** - invalid configuration fails at startup, missing connection string fails fast, migrations retry with back-off, readiness turns 503 when `SELECT 1` fails, unexpected exceptions are 500 without internals and logged with the correlation id, rate limit 429 per subject (never one shared bucket behind a proxy; `X-Forwarded-For` only honoured when `Proxy:TrustForwardedHeaders` is on), health/metrics probes are not logged at Information: `InvalidConfiguration_FailsAtStartup`, `MissingConnectionString_FailsFastWithClearMessage`, `DatabaseInitializer_RetriesWithBackoff_ThenGivesUp`, `HealthReady_WhenDatabaseProbeFails_Is503_LiveStays200`, `UnexpectedException_Is500InternalError_WithoutStackTrace_AndLoggedWithCorrelationId`, `RateLimit_ThirdWriteInWindow_Is429RateLimited_ReadsUnaffected`, `RateLimit_IsPartitionedBySubject_NotSharedAcrossCallersOnOneAddress`, `RateLimit_ForwardedForIsIgnored_WhenProxyIsNotTrusted`, `RateLimit_UsesForwardedFor_WhenProxyIsTrusted`, `RequestLogging_HealthAndMetricsProbes_AreDebugNotInformation`.

## Test strategy

| Project | What | How | Tests | Line | Branch | Gate |
|---|---|---|---|---|---|---|
| `ClaimsEngine.Domain.Tests` | every rule, every exception type, every enum branch, `Money` and state-machine property tables | plain xUnit `Assert`, fixed `FakeClock`, `[Theory]`/`[MemberData]` | 218 | 99.43 % | 98.46 % | >= 95 % |
| `ClaimsEngine.Application.Tests` | every handler, idempotency (TTL, hash, caller scope, duplicate-key and renewal races, sweep), ownership, pagination, overdue threshold | in-memory fakes of the ports | 54 | 96.17 % | 97.14 % | >= 90 % |
| `ClaimsEngine.Api.Tests` | the real pipeline via `WebApplicationFactory` against **PostgreSQL 16 in Testcontainers**: every endpoint, every error code, auth, real parallelism, constraints, atomicity, commit-phase failures, operations; plus direct tests of validators, middleware pieces and the `ErrorMapper` table | one container per run, one **fresh database per test**, JWTs minted with the test signing key | 182 | Api 98.60 % / Infra 98.05 % | Api 91.08 % / Infra 79.41 % | >= 85 % on Api+Infrastructure combined (98.48 % / 89.72 %) |

Total: **454 test cases** (281 test methods; theories expand). Coverage is measured by
`coverlet.msbuild` on every `dotnet test` and the run **fails below the gate**, locally and in CI.
CI additionally renders an HTML report (ReportGenerator) and uploads it with the TRX results.
Excluded from coverage, and nothing else: the EF migration files and the OpenAPI source-generator
output compiled into the API assembly (both generated code).

Every API test gets its own database (`CREATE DATABASE t_<guid>` on the shared container), so
there is no shared mutable state, ids are unique per test and order does not matter; the suite
was run twice in a row and filtered to single classes with identical results. Inside
`docker build` there is no Docker daemon, so the image build runs the API tests in the SQLite
in-memory profile (`CLAIMS_TESTS_DB=sqlite`); the 15 PostgreSQL-only tests are skipped there and
the PostgreSQL suite in CI is the source of truth.

### What is not tested

- Token validation against a real external OIDC issuer (Entra/Keycloak/Auth0): the external mode is exercised only as "rejects tokens it cannot validate"; no live identity provider is involved.
- Kestrel's own request-body limit on the wire (the in-memory test server bypasses it); the middleware that enforces the same limit is tested.
- Behaviour under sustained load, connection-pool exhaustion, or PostgreSQL failover; the retrying execution strategy is configured but not fault-injected at the network level.
- The Trivy scan and the ReportGenerator step run only in CI, not in the test projects.
- Migration *upgrades* between schema versions: there is one initial migration.
- The Scalar UI itself (only that `/docs` responds 200 without a CSP header).

## Concurrency and the database

| Situation | Mechanism | Proof |
|---|---|---|
| Two operations on the same claim at once | `claims.Version` (application-managed, incremented in every transition) and PostgreSQL `xmin` are both EF concurrency tokens; the loser's `UPDATE ... WHERE Version = @old AND xmin = @old` affects 0 rows -> `409 concurrency_conflict` | `Concurrency_ParallelPaysOnSameClaim_ExactlyOneSucceeds`, `Concurrency_TwoContextsLoadSameClaim_SecondSaveIsConcurrencyConflict`, `Concurrency_XminChangesOnEveryWrite` |
| Two payments on different claims of one policy that together exceed the limit | the paid total is re-read inside the payment transaction; `Policy.RecordPayout` bumps `policies.Version`, so the two transactions conflict on the policy row; the loser retried sees `422 limit_exhausted` | `Concurrency_ParallelPaysOnOnePolicyExceedingLimit_NeverOvershoot`, `Pay_SecondClaimBeyondLimit_Is422LimitExhausted` |
| Two submits with the same new `Idempotency-Key` | composite primary key on `idempotency_keys(Subject, Key)`; the second insert fails with `23505`, the unit of work maps it to `DuplicateKeyException`, the handler re-reads the winner's record and replays it | `Concurrency_ParallelSubmitsWithSameIdempotencyKey_CreateExactlyOneClaim`, `Idempotency_DuplicateKeyOnCommit_ReplaysWhatTheWinnerStored` |
| Two submits renewing the same *expired* key | `idempotency_keys.Version` (and `xmin`) is a concurrency token bumped by `Renew`; the loser's update affects 0 rows -> `ConcurrencyException`, which the submit handler resolves exactly like the duplicate-key race by replaying the winner | `Idempotency_ParallelSubmitsWithSameExpiredKey_RenewExactlyOnce`, `Idempotency_ExpiredKeyRenewedConcurrently_LoserReplaysTheWinner` |
| The same key value from two different callers | keys are scoped by `sub`; two rows, two claims, no information leak between callers | `Idempotency_SameKeyFromTwoSubjects_CreatesTwoIndependentClaims` |
| A transient failure during `COMMIT` | the outcome is unknown, so the unit of work does **not** re-run the work: it raises `CommitOutcomeUnknownException` (500) and the client retries with its `Idempotency-Key` | `UnitOfWork_TransientFailureDuringCommit_IsNotRetried_AndSurfacesAsUnknownOutcome`, `Pay_WhenCommitOutcomeIsUnknown_Is500_AndNothingIsPersisted` |
| Two policies with the same number | unique index -> `409 duplicate_key` | `CreatePolicy_DuplicatePolicyNumber_Is409DuplicateKey` |
| Partial failure inside a use case | every command runs in `IUnitOfWork.RunInTransactionAsync`: explicit `BEGIN`, one `SaveChanges`, `COMMIT`; any exception rolls back claim, history and policy changes together | `Atomicity_PayFailingAfterFirstWrite_PersistsNothing`, `Atomicity_SubmitFailingAfterFirstWrite_PersistsNeitherClaimNorIdempotencyKey` |

Isolation level is PostgreSQL's default `READ COMMITTED`; correctness comes from the version
tokens and constraints, not from serializable transactions.

**Transactions and retries.** Every command runs through `IUnitOfWork.RunInTransactionAsync`:
`BEGIN`, the use case, one `SaveChanges`, `COMMIT`. The work runs inside Npgsql's retrying
execution strategy, so a transient failure *before* the commit (connection drop while loading or
saving) re-runs the whole use case on a cleared change tracker. A failure *during* `COMMIT` is
deliberately **not** retried: the database may or may not have applied the transaction, and
re-running the work could file a second claim or answer `409` to a payment that succeeded.
Instead the unit of work raises `CommitOutcomeUnknownException` (HTTP 500 `internal_error`,
logged with the correlation id) and the client retries with its `Idempotency-Key`, which turns
the retry into a safe replay.

Schema (`src/ClaimsEngine.Infrastructure/Persistence/Migrations`): `policies`, `claims`
(FK to policies `ON DELETE RESTRICT`), `claim_history` (owned, FK to claims), `idempotency_keys`.
Money columns are `numeric(18,2)`, enums are stored as names, timestamps as `timestamptz`.
Indexes: `claims(PolicyId, Status)`, `claims(FiledAt)`, `claims(Status, ReviewStartedAt)`,
`claim_history(ClaimId, At)`, `idempotency_keys(CreatedAt)`, unique `policies(PolicyNumber)`.
Check constraints: coverage limit > 0, `0 <= deductible < limit`, `effectiveTo > effectiveFrom`,
claimed amount > 0, eligible payout >= 0.

Migrations are applied at startup with exponential back-off (1, 2, 4, 8, 16 s, up to 60 s) so
`docker compose up` works before PostgreSQL accepts connections; each attempt is logged.
Npgsql connection pooling is explicit in the connection string; the `DbContext` is scoped, uses
`EnableRetryOnFailure` for transient faults and `NoTracking` for queries (repositories opt in to
tracking for aggregates they modify). `/health/ready` executes a real `SELECT 1` through the pool.

## Operations

- **Logs:** JSON lines on stdout (`ASPNETCORE_ENVIRONMENT=Development` switches to readable text). One line per request (`POST /claims responded 201 in 11.8 ms`; `/health/*` and `/metrics` probes are logged at Debug so they do not drown the request log), errors with the exception and `CorrelationId`. `X-Correlation-Id` is echoed (or generated as 32 hex chars) and lives in the logging scope; it is also in every problem response.
- **Health:** `/health/live` (process up), `/health/ready` (`SELECT 1`), `/health` (all). The Docker `HEALTHCHECK` and the compose healthcheck probe `/health/ready` with bash's `/dev/tcp`, so the runtime image needs no curl.
- **Metrics:** `/metrics` in Prometheus format (`prometheus-net`): `http_requests_received_total`, `http_request_duration_seconds` by method/endpoint/status, .NET runtime counters. Scrape it; alert on 5xx rate, p95 latency, `409 concurrency_conflict` spikes (a sign of contention), `429` volume, readiness flapping.
- **Limits:** request body 64 KiB, request headers timeout 30 s, keep-alive 2 min, fixed-window rate limit on writes per authenticated subject (per client address for the anonymous token endpoint; set `Proxy__TrustForwardedHeaders=true` behind a trusted ingress so that address is the real client), `ShutdownTimeout` 15 s for graceful drain.
- **Housekeeping:** `IdempotencySweeper` deletes idempotency keys older than the TTL every `Idempotency__SweepIntervalMinutes` (60 by default, runs once at startup, logs the count); `claim_history` is append-only by design.
- **Security headers:** `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, `Cache-Control: no-store`, `Content-Security-Policy: default-src 'none'` (not on `/docs`, which runs its own scripts). The `Server` header is off. HTTPS redirection is off inside the container: TLS terminates at the edge.
- **Image:** `mcr.microsoft.com/dotnet/aspnet:10.0`, non-root `app` user, `DOTNET_gcServer=0`, **356 MB** as built here; compose sets `mem_limit: 512m`, `cpus: 1.0`.

## Production security

- The **dev issuer is a demo convenience**. In production set `Auth__Authority`/`Auth__Audience` (Entra ID, Keycloak, Auth0 or any OIDC provider), leave `Auth__DevIssuer__Enabled=false` (the default) and do not set a signing key at all. The validation code path is the same; only the key source changes.
- Secrets (connection string, any key) come from environment variables; inject them from a secrets manager (Kubernetes secrets, AWS Secrets Manager, Azure Key Vault) rather than files in the image. Nothing secret is logged; tokens are never logged.
- Terminate TLS at the ingress / load balancer and forward `X-Forwarded-*` headers; enable `UseForwardedHeaders` there so rate limiting and logs see real client addresses.
- Run as the non-root user the image already uses, read-only filesystem, resource limits, and the CI Trivy scan gate (HIGH/CRITICAL) before promoting an image.

## Before going to production

- [ ] Replace the dev issuer with your OIDC provider (`Auth__Authority`, `Auth__Audience`), map roles, disable `Auth__DevIssuer__Enabled`
- [ ] Lock down CORS (no CORS policy is registered: same-origin only today; add an explicit allow-list if a browser client appears)
- [ ] TLS termination at the edge, forwarded headers configured, HSTS at the edge
- [ ] Review request limits (64 KiB body, rate-limit window) for your traffic; put the limiter behind the real client address
- [ ] Graceful shutdown: `ShutdownTimeout` 15 s is set; align the orchestrator's termination grace period
- [ ] Observability: scrape `/metrics`, ship JSON logs, alert on the signals above; add OpenTelemetry tracing export if you run a collector
- [ ] Resource limits and replicas in the orchestrator; the API is stateless, PostgreSQL is the single stateful part
- [ ] Pin base images by digest and keep the Trivy gate; Dependabot PRs for NuGet, actions and Docker are configured
- [ ] Secrets from a secrets manager; rotate the database password; least-privilege database role (no `CREATE DATABASE`)
- [ ] Backups and point-in-time recovery for PostgreSQL; test a restore
- [ ] Retention: the idempotency sweeper is on by default (tune `Idempotency__SweepIntervalMinutes`); decide how long `claim_history` (append-only) and closed claims must be kept for compliance

## Roadmap / TODO

- No notifications or reporting projection: the audit trail is the `claim_history` table only. If downstream consumers appear, add an outbox table written in the same transaction (the domain intentionally raises no in-memory events today).
- `GET /claims` has no free-text search or date filters.
- Policy status changes (`Lapsed`, `Cancelled`) exist in the domain but have no endpoint.
- OpenTelemetry traces (only metrics and correlation ids today).
- The rate limiter is in-process; a multi-replica deployment needs a distributed store or an edge limiter.
- Claim amounts are EUR only; `Money` carries a currency, the persistence layer stores only the amount.

## Assumptions and design decisions

- **"30 days"** is measured from 00:00 UTC on the incident date to the filing instant (the brief asked for "30 days + 1 second" to be late).
- **Policy year** = 12 months from each anniversary of `effectiveFrom`; the last one is cut at `effectiveTo`. Anniversaries of 29 February fall on 28 February.
- **"Already paid"** counts `Paid` claims only; approved-but-unpaid claims do not reserve budget, which is why `pay` re-checks the limit.
- **Flag evaluation happens before auto-rejection**, so a late, high-value claim is both `Rejected` and flagged in the audit trail.
- **State before rights:** a transition that is impossible in the current state answers `409` even to an unauthorised actor; authority is checked next, then the flag.
- **`pay` requires Manager** (the brief allowed a `Finance` role; one role fewer keeps the demo readable). Adjusters may file claims on behalf of holders; Claimants only on their own.
- **Claimant identity** is the holder id in `sub`. A Claimant whose `sub` is not a GUID owns nothing and sees an empty list rather than an error.
- **Dates in requests** accept `yyyy-MM-dd` or ISO-8601 date-times with offset and nothing else (`10/03/2026` is rejected as ambiguous).
- **Check constraints** compare through `CAST(... AS REAL)` on SQLite (decimals are TEXT there) and natively on PostgreSQL; the migration only ever targets PostgreSQL.
- **Coverage gates** are enforced per test project on the assemblies that project covers; Infrastructure and Api share one gate because they are exercised by the same integration suite.
- **`Version` and `xmin` together:** `Version` is portable (it works on SQLite too and is visible to API clients), `xmin` is the database-level guard that also catches writes that bypass the application. Idempotency records carry both as well, which is what makes renewing an expired key race-safe.
- **Idempotency keys are per caller:** the stored key is `(sub, key)`. Two callers using the same key value never collide and cannot learn about each other's keys; the same caller reusing a key with a different body still gets `422`.
- **Commit failures are not retried** (see Transactions and retries); a client that wants exactly-once submission sends an `Idempotency-Key` and retries on 5xx.
- **No domain events:** the aggregate does not collect events because nothing consumes them; `claim_history` is the audit trail. An outbox is the documented path if consumers appear.

## Trade-offs

- **No MediatR / no FluentValidation:** one handler class per use case and hand-written validators are easier to read in a project this size and keep the Application project free of packages. The cost is a little repetition in the validators.
- **Client-side sum of paid payouts:** `SumPaidPayoutsAsync` loads the (few) paid payouts of one policy year and sums in memory so that the same query runs on SQLite; on a very large book this would become a SQL `SUM`.
- **A fresh database per test** costs about 0.3 s per test but removes every ordering and cleanup problem; the whole API suite still runs in about a minute.
- **SQLite profile inside `docker build`:** the image build cannot start containers, so it proves the build and the SQLite-compatible tests, while CI proves PostgreSQL. Two profiles mean the provider-specific code (xmin, `CAST`) has to be conditional.
- **Rate limiting in-process** is simple and testable; it is per replica.
- **`DateTimeOffset` everywhere** with a UTC-ticks converter for SQLite: exactness and comparability over a smaller schema.

## Project layout

```
dotnet-claims-engine/
|-- ClaimsEngine.sln, Directory.Build.props, Directory.Packages.props, .editorconfig
|-- Dockerfile, docker-compose.yml, .env.example, .dockerignore
|-- src/
|   |-- ClaimsEngine.Domain/          Policies/, Claims/, Shared/ (Money, ids, Actor), Exceptions/
|   |-- ClaimsEngine.Application/     Policies/, Claims/ (one handler per use case), Ports/, Dtos/, Ownership.cs
|   |-- ClaimsEngine.Infrastructure/  Persistence/ (DbContext, configurations, repositories, unit of work,
|   |                                 initializer, Migrations/), DependencyInjection.cs
|   `-- ClaimsEngine.Api/             Program.cs, Endpoints/, Auth/, Validation/, Errors/, Middleware/,
|                                     Health/, Options/, Composition/, Contracts/
|-- tests/
|   |-- Directory.Build.props         coverage settings shared by the test projects
|   |-- ClaimsEngine.Domain.Tests/    one class per rule
|   |-- ClaimsEngine.Application.Tests/  handlers with in-memory fakes
|   `-- ClaimsEngine.Api.Tests/       WebApplicationFactory + Testcontainers PostgreSQL
|-- docs/                             errors.md (error catalog), TEST-CATALOG.md (generated), sql/queries.sql
|-- postman/                          collection + local environment
|-- scripts/                          seed.sh, generate-test-catalog.sh
|-- .github/workflows/ci.yml, .github/dependabot.yml
`-- LICENSE (MIT), CONTRIBUTING.md
```

## License

MIT, see [LICENSE](LICENSE).
