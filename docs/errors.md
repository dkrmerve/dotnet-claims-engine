# Error catalog

Every error response is an RFC 7807 problem (`application/problem+json`) with these fields:

```json
{
  "type": "https://github.com/dkrmerve/dotnet-claims-engine/docs/errors#invalid_transition",
  "title": "Conflict",
  "status": 409,
  "detail": "Cannot move claim from Submitted to Approved.",
  "instance": "/claims/7847c3cb-.../approve",
  "code": "invalid_transition",
  "correlationId": "35d5d455e21b45fca2a7befcc7fb5213",
  "traceId": "00-d9d52e71...-00"
}
```

`code` is stable and machine-readable; `detail` is for humans and may change. Request-validation
problems (`request_validation_failed`) additionally carry an `errors` dictionary keyed by field.
The `type` anchor of each code is the heading below.

The mapping from exception to status lives in exactly one place, `src/ClaimsEngine.Api/Errors/ErrorMapper.cs`,
and is unit-tested by `ErrorMapperTests` (see `docs/TEST-CATALOG.md`).

| Code | HTTP | Raised by | When |
|---|---|---|---|
| `request_validation_failed` | 400 | API edge validators | The request body or query string has missing, malformed or out-of-range fields. All errors are reported at once in `errors`. |
| `invalid_request` | 400 | ASP.NET Core body binding | The JSON cannot be parsed, has the wrong type for a field, the body is missing on an endpoint that needs one, or a route/query value has the wrong type (`page=abc`). |
| `validation_error` | 400 | Domain `ValidationException` | A domain invariant refused the input after the edge validators let it through (for example an incident date in the future relative to "now", or a negative amount reaching `Money`). |
| `rejection_note_required` | 400 | Domain `ValidationException` | Rejecting with reason `Other` and no note. |
| `note_required` | 400 | Domain `ValidationException` | Clearing an investigation flag without a note. |
| `unauthenticated` | 401 | JWT bearer handler / `Authentication.Resolve` | No bearer token, an expired or badly signed token, or a token without a usable `sub` / `role` claim. |
| `forbidden_role` | 403 | Authorization policies | The token is valid but its role may not call this endpoint (for example an Adjuster calling `pay`). |
| `insufficient_authority` | 403 | Domain `InsufficientAuthorityException` | The role may call the endpoint but not for this claim: approval above the role's amount limit (rule 5), a non-Claimant withdrawing, a non-Manager clearing a flag. |
| `not_owner` | 403 | Application `Ownership` | A Claimant touched a policy or claim whose `holderId` is not the token's `sub`. |
| `not_found` | 404 | Domain `NotFoundException` / routing | Unknown policy or claim id, a non-GUID id in the route, or an unknown path. |
| `method_not_allowed` | 405 | Routing | HTTP method not supported on the path. |
| `invalid_transition` | 409 | Domain `InvalidTransitionException` | The claim state machine (rule 4) does not allow the move: approve before review, pay before approve, anything after Paid/Rejected/Withdrawn, withdraw after approval. |
| `investigation_pending` | 409 | Domain `InvalidTransitionException` | Approving a claim that carries the `RequiresInvestigation` flag (rule 6). |
| `flag_not_set` | 409 | Domain `InvalidTransitionException` | Clearing the flag on a claim that is not flagged. |
| `concurrency_conflict` | 409 | Infrastructure unit of work (`ConcurrencyException`) | Another request changed the claim or policy between load and save (application `Version` and PostgreSQL `xmin` both act as tokens). Reload and retry. |
| `duplicate_key` | 409 | Infrastructure unit of work (`DuplicateKeyException`) | A unique index rejected the write, for example a duplicate policy number. (The submit path resolves idempotency-key races internally and never exposes this code for them.) |
| `payload_too_large` | 413 | `RequestBodyLimitMiddleware` / Kestrel | Request body over 64 KiB. |
| `unsupported_media_type` | 415 | ASP.NET Core | Body is not `application/json`. |
| `policy_not_eligible` | 422 | Domain `RuleViolationException` | The policy is Lapsed/Cancelled, or the incident date is outside `[effectiveFrom, effectiveTo]` (rule 1). |
| `idempotency_key_reused` | 422 | Application submit handler | The `Idempotency-Key` was already used within its TTL with a different payload (rule 10). |
| `limit_exhausted` | 422 | Domain `RuleViolationException` | Paying this claim would push the policy year's paid total over the coverage limit (rule 7, re-checked inside the payment transaction). |
| `rate_limited` | 429 | Rate limiter | More write requests from one client address than `RateLimiting:PermitLimit` per window. `Retry-After` is set. |
| `internal_error` | 500 | Exception handler | Anything unexpected, including a transient failure during `COMMIT` (`CommitOutcomeUnknownException`: the outcome is unknown, so the work is not re-run; retry with your `Idempotency-Key`). The message is not exposed; the exception is logged at error level with the `correlationId` from the response. |

## Business outcomes that are not errors

Some rule violations produce a **201 Created** with an already-closed claim, because the audit
trail must exist: a late filing (rule 2) yields `status: Rejected, rejectionReason: LateFiling`;
a claim at or below the deductible yields `BelowDeductible`; a claim against an exhausted policy
year yields `LimitExhausted`. See the business rules table in the README.
