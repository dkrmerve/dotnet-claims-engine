#!/usr/bin/env bash
# Seeds the running stack (docker compose up) with two policies and claims in every state.
# Idempotent: ids are remembered in scripts/.seed-state.env and claims carry fixed Idempotency-Keys,
# so running it twice does not create duplicates. Requires curl only (no jq).
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
STATE_FILE="$(dirname "$0")/.seed-state.env"
[[ -f "$STATE_FILE" ]] && source "$STATE_FILE"

json_field() { # json_field '<json>' '<field>'  -> first string/number value of the field
  sed -nE "s/.*\"$2\":\"?([^\",}]*)\"?.*/\1/p" <<<"$1" | head -n 1
}

token() { # token <subject> <role>
  local body; body=$(curl -sS -X POST "$BASE_URL/auth/token" -H 'Content-Type: application/json' \
    -d "{\"subject\":\"$1\",\"role\":\"$2\"}")
  json_field "$body" accessToken
}

post() { # post <token> <path> <json-body> [idempotency-key]
  local key_header=()
  [[ -n "${4:-}" ]] && key_header=(-H "Idempotency-Key: $4")
  curl -sS -X POST "$BASE_URL$2" -H "Authorization: Bearer $1" -H 'Content-Type: application/json' "${key_header[@]}" -d "$3"
}

post_empty() { curl -sS -X POST "$BASE_URL$2" -H "Authorization: Bearer $1"; }

echo "== Waiting for $BASE_URL/health/ready"
for _ in $(seq 1 30); do
  curl -sf "$BASE_URL/health/ready" >/dev/null && break
  sleep 2
done

MANAGER=$(token manager-1 Manager)
ADJUSTER=$(token adjuster-1 Adjuster)

HOLDER_A="${HOLDER_A:-11111111-1111-4111-8111-111111111111}"
HOLDER_B="${HOLDER_B:-22222222-2222-4222-8222-222222222222}"

create_policy() { # create_policy <var> <holder> <number> <limit> <deductible>
  local var=$1 holder=$2 number=$3 limit=$4 deductible=$5
  if [[ -n "${!var:-}" ]]; then echo "== Policy $number already seeded (${!var})"; return; fi
  local body; body=$(post "$MANAGER" /policies "{\"policyNumber\":\"$number\",\"holderId\":\"$holder\",\"coverageType\":\"Auto\",\"coverageLimit\":$limit,\"deductible\":$deductible,\"effectiveFrom\":\"2026-01-01\",\"effectiveTo\":\"2026-12-31\"}")
  local id; id=$(json_field "$body" id)
  [[ -n "$id" ]] || { echo "Failed to create policy $number: $body"; exit 1; }
  printf '%s=%s\n' "$var" "$id" >> "$STATE_FILE"
  declare -g "$var=$id"
  echo "== Created policy $number -> $id"
}

create_policy POLICY_A "$HOLDER_A" "POL-SEED-A" 20000 500
create_policy POLICY_B "$HOLDER_B" "POL-SEED-B" 10000 0
printf 'HOLDER_A=%s\nHOLDER_B=%s\n' "$HOLDER_A" "$HOLDER_B" >> "$STATE_FILE"

CLAIMANT_A=$(token "$HOLDER_A" Claimant)
CLAIMANT_B=$(token "$HOLDER_B" Claimant)
INCIDENT=$(date -u -d '-5 days' +%F 2>/dev/null || date -u -v-5d +%F)

submit() { # submit <claimant-token> <policy> <amount> <description> <key> -> claim id
  local body; body=$(post "$1" /claims "{\"policyId\":\"$2\",\"incidentDate\":\"$INCIDENT\",\"claimedAmount\":$3,\"description\":\"$4\"}" "$5")
  json_field "$body" id
}

echo "== Claims on policy A (holder $HOLDER_A)"
C1=$(submit "$CLAIMANT_A" "$POLICY_A" 3000 "Rear-ended at a traffic light (stays Submitted)" seed-a-1)
C2=$(submit "$CLAIMANT_A" "$POLICY_A" 4500 "Hail damage on the roof (UnderReview)" seed-a-2)
post "$ADJUSTER" "/claims/$C2/review" '{"note":"Photos received."}' >/dev/null || true
C3=$(submit "$CLAIMANT_A" "$POLICY_A" 2500 "Windscreen replacement (Approved)" seed-a-3)
post_empty "$ADJUSTER" "/claims/$C3/review" >/dev/null || true
post_empty "$ADJUSTER" "/claims/$C3/approve" >/dev/null || true
C4=$(submit "$CLAIMANT_A" "$POLICY_A" 1800 "Parking lot scrape (Paid)" seed-a-4)
post_empty "$ADJUSTER" "/claims/$C4/review" >/dev/null || true
post_empty "$ADJUSTER" "/claims/$C4/approve" >/dev/null || true
post_empty "$MANAGER" "/claims/$C4/pay" >/dev/null || true
C5=$(submit "$CLAIMANT_A" "$POLICY_A" 900 "Duplicate of an earlier claim (Rejected)" seed-a-5)
post "$ADJUSTER" "/claims/$C5/reject" '{"reason":"Other","note":"Duplicate of seed-a-1."}' >/dev/null || true

echo "== Claims on policy B (holder $HOLDER_B)"
C6=$(submit "$CLAIMANT_B" "$POLICY_B" 8500 "Total loss after flooding (flagged: 85% of limit)" seed-b-1)
C7=$(submit "$CLAIMANT_B" "$POLICY_B" 600 "Withdrawn by the claimant" seed-b-2)
post_empty "$CLAIMANT_B" "/claims/$C7/withdraw" >/dev/null || true

echo
echo "Seeded. Policies: $POLICY_A (A), $POLICY_B (B). Claims: $C1 $C2 $C3 $C4 $C5 $C6 $C7"
echo "Try: curl -s $BASE_URL/claims?pageSize=50 -H \"Authorization: Bearer $MANAGER\""
