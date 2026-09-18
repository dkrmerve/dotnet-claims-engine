-- Handy queries for DBeaver / psql against the compose PostgreSQL
-- (Host localhost, Port 5432, Database claims, User claims, Password claims).
-- Identifiers are PascalCase (EF Core defaults) and therefore quoted.

-- Claims per status
SELECT "Status", COUNT(*) AS claims, SUM("ClaimedAmount") AS claimed_total
FROM claims
GROUP BY "Status"
ORDER BY claims DESC;

-- Claims under review for more than 14 days (rule 8, same logic as GET /claims/overdue)
SELECT c."Id", p."PolicyNumber", c."ReviewStartedAt", now() - c."ReviewStartedAt" AS in_review_for
FROM claims c
JOIN policies p ON p."Id" = c."PolicyId"
WHERE c."Status" = 'UnderReview'
  AND c."ReviewStartedAt" < now() - interval '14 days'
ORDER BY c."ReviewStartedAt";

-- Paid payouts per policy and policy year (policy years are anchored at EffectiveFrom, rule 7)
WITH years AS (
  SELECT p."Id" AS policy_id, p."PolicyNumber", p."CoverageLimit",
         (p."EffectiveFrom" + (gs.n * interval '1 year'))::date AS year_start,
         LEAST((p."EffectiveFrom" + ((gs.n + 1) * interval '1 year') - interval '1 day')::date, p."EffectiveTo") AS year_end
  FROM policies p
  CROSS JOIN LATERAL generate_series(0, GREATEST(0, EXTRACT(YEAR FROM age(p."EffectiveTo", p."EffectiveFrom"))::int)) AS gs(n)
)
SELECT y."PolicyNumber", y.year_start, y.year_end, y."CoverageLimit",
       COALESCE(SUM(c."ApprovedPayout") FILTER (WHERE c."Status" = 'Paid'), 0) AS paid,
       y."CoverageLimit" - COALESCE(SUM(c."ApprovedPayout") FILTER (WHERE c."Status" = 'Paid'), 0) AS remaining
FROM years y
LEFT JOIN claims c ON c."PolicyId" = y.policy_id AND c."IncidentDate" BETWEEN y.year_start AND y.year_end
GROUP BY y."PolicyNumber", y.year_start, y.year_end, y."CoverageLimit"
ORDER BY y."PolicyNumber", y.year_start;

-- Full history of one claim (replace the id)
SELECT h."At", h."ActorRole", h."FromStatus", h."ToStatus", h."Note"
FROM claim_history h
WHERE h."ClaimId" = '00000000-0000-0000-0000-000000000000'
ORDER BY h."At", h."Id";

-- Flagged claims waiting for a Manager
SELECT c."Id", p."PolicyNumber", c."ClaimedAmount", c."Status", c."FiledAt"
FROM claims c
JOIN policies p ON p."Id" = c."PolicyId"
WHERE c."Flag" = 'RequiresInvestigation' AND c."Status" IN ('Submitted', 'UnderReview')
ORDER BY c."FiledAt";

-- Holders with 3+ non-withdrawn claims in the last 365 days (rule 6 input)
SELECT p."HolderId", COUNT(*) AS recent_claims
FROM claims c
JOIN policies p ON p."Id" = c."PolicyId"
WHERE c."FiledAt" >= now() - interval '365 days' AND c."Status" <> 'Withdrawn'
GROUP BY p."HolderId"
HAVING COUNT(*) >= 3;

-- Idempotency keys and their age (24h TTL by default)
SELECT "Key", "ClaimId", "CreatedAt", now() - "CreatedAt" AS age
FROM idempotency_keys
ORDER BY "CreatedAt" DESC
LIMIT 50;

-- Row versions: application-managed Version next to PostgreSQL's xmin
SELECT "Id", "Status", "Version", xmin
FROM claims
ORDER BY "FiledAt" DESC
LIMIT 20;
