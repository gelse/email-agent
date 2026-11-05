# Email Agent

An agent that reads your email, decides what to do with each message, and
acts on it — with **user-owned, non-recoverable encryption**: the service
never holds the keys that decrypt your data.

- User-facing overview and operational docs: [`docs/`](docs/README.md)
- Design source of truth: `plans/01-architecture.md` (local checkout)
- Current milestone: **M1 — Identity, encryption, and the data model** —
  `plans/implementation/M1-identity-encryption-and-data-model.md` (local
  checkout), starting with `plans/implementation/M1T1-solution-scaffolding-and-dev-environment.md`

**Status:** M0 (foundation) and M1 (identity, encryption, and the data
model) are complete.

## Quickstart (development)

```bash
cp .env.example .env   # then fill in throwaway dev values (.env is gitignored)

bash scripts/gen-dev-cert.sh   # one-time: self-signed dev TLS certificate
                               # (docker/certs, git-ignored; nginx needs it)

make build             # dotnet build (8 projects, warnings are errors)
make test              # dotnet test (unit + integration suites)

make compose-up        # postgres + api + web (docker compose v2.24+)
make seed              # create the first admin; prints the password once
                       # (requires the M1T2 migration to be applied first:
                       #  dotnet ef database update)
make compose-down
```

Health and metrics: `GET :8081/health/live`, `GET :8081/health/ready`,
`GET :8081/metrics` (Prometheus), on the plain-HTTP listener. In dev the
`web` (nginx) container is the only host-published service: it serves the
SPA and reverse-proxies `/v1/*`, `/health/*` and `/metrics` to `api` on the
internal Docker network (same-origin topology, `plans/01-architecture.md`
§13.9).

Open `https://localhost:8443` for the SPA. That is the HTTPS edge with the
self-signed dev certificate — it simulates production, where TLS is
terminated externally (traefik in testing) in front of a plain-HTTP API, and
lets the `Secure` session cookie behave in dev exactly as it does in
production. The certificate is not trusted by default: browsers show a
warning you accept once for localhost, and command-line clients need
`curl -k`.

## Layout

| Path | What lives there |
|---|---|
| `src/` | EmailAgent.Api (ASP.NET Core host), EmailAgent.Spa (Blazor WASM), EmailAgent.Core, EmailAgent.Contracts, EmailAgent.Infrastructure |
| `tests/` | EmailAgent.UnitTests, EmailAgent.IntegrationTests |
| `tools/AuditVerify` | HMAC-chain verifier console tool |
| `docker/` | Dockerfile, dev compose, container entrypoint |
| `scripts/` | seed-dev, smoke, notices generation |

See `AGENTS.md` for the contributor/agent operating manual.

## License

This project is licensed under the [MIT License](LICENSE) — see that file
for the full text. You are free to use, modify, and fork it, including for
commercial purposes, provided the copyright and permission notices are
retained.
