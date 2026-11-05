# M1T1 — single entry point for the dev workflow (M1T1 task step 13;
# consumed by M1T9's smoke test). Requires: .NET SDK, Docker Compose v2.24+.

.PHONY: build test compose-up compose-down seed audit-verify

build:
	dotnet build

test:
	dotnet test

compose-up:
	# Q-A13: nginx only starts with its (git-ignored) TLS material present —
	# gen-dev-cert.sh is idempotent (no-op when the pair exists), so call it
	# unconditionally rather than test one file of the pair.
	@bash scripts/gen-dev-cert.sh
	docker compose up -d --wait

compose-down:
	docker compose down

# Creates the first admin in the running dev stack and prints the password
# once (scripts/seed-dev.sh).
seed:
	bash scripts/seed-dev.sh

# HMAC-chain verifier stub (real implementation ships in M1T9).
audit-verify:
	dotnet run --project tools/AuditVerify
