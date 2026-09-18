# Contributing

Thanks for taking a look. This is a portfolio project, but it is run like a real one.

## Branching

```
main      <- always releasable; only fast-forward merges from develop
develop   <- integration branch; pull requests land here
feature/* <- new behaviour, branched from develop
fix/*     <- bug fixes, branched from develop
```

`main` is created with the initial commit; `develop` is branched from it afterwards.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`, `ci:`.
One logical change per commit, imperative mood, body explains *why* when it is not obvious.

## Before you open a pull request

- [ ] `dotnet format ClaimsEngine.sln --verify-no-changes` passes (CI enforces it)
- [ ] `dotnet build -c Release` has zero warnings (`TreatWarningsAsErrors` is on)
- [ ] `dotnet test -c Release` is green, including the PostgreSQL suite (needs Docker)
- [ ] Coverage gates still pass (Domain >= 95%, Application >= 90%, Api + Infrastructure >= 85%, line and branch)
- [ ] Every new rule or error path has a test named after the case, and `docs/TEST-CATALOG.md` is regenerated (`scripts/generate-test-catalog.sh`)
- [ ] New error codes are added to the error catalog in `README.md`
- [ ] Schema changes come with an EF migration (`dotnet ef migrations add <Name> --project src/ClaimsEngine.Infrastructure --startup-project src/ClaimsEngine.Infrastructure`)
- [ ] `docker compose up --build` still comes up healthy

## Pull request checklist (for the reviewer)

- Does the change keep business rules in the Domain project and HTTP concerns in the API project?
- Are edge cases (boundaries, concurrency, idempotency) covered by tests, not just the happy path?
- Are new configuration values validated at startup and documented in the README table?
- Is anything logged that should not be (secrets, tokens, personal data)?
