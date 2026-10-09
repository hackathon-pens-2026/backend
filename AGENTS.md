# SignIt Backend — Agent Instructions

## Read before changing code

- Read this file, any scoped `AGENTS.md`, and `SignIt-Backend-Requirements.md` v2.6 or its newer approved replacement. Use the UI/UX specification to understand what the frontend needs, not to move business rules into it.
- Inspect the solution, project files, `global.json`, package versions, configuration, migrations and existing implementations before editing. The PRD targets .NET 10, ASP.NET Core and PostgreSQL; follow the actual compatible versions in the repository.
- Read official, version-matched documentation for unfamiliar APIs. Check PDF/OCR/storage library capabilities and licenses before adding them. Do not assume package compatibility or claim verification without evidence.
- Keep changes within the requested feature. Do not rewrite the architecture, upgrade packages or introduce another framework without a concrete need.

## Setup and verification

- Use the solution and startup project that actually exist. Do not invent solution paths or npm-style backend commands.
- Restore with `dotnet restore`, build with `dotnet build`, and run the relevant tests with `dotnet test`, using the actual solution/project paths. Inspect existing scripts before choosing additional commands.
- Start the API through the configured launch profile or existing container workflow. Check required database, worker and storage services before testing an integration.
- Keep secrets in environment variables, development user-secrets or the configured production secret store. Provide names and safe examples in configuration; never commit passwords, tokens or connection strings with real credentials.
- Generate migrations only when the schema changes. Review the SQL and effect on existing data. Do not reset databases or run destructive migrations to make checks pass.
- Report commands actually run and their results. Missing dependencies, unavailable services and skipped checks remain explicit blockers.

## Backend owns business rules

- Implement all business decisions here: letter schema validation, participant eligibility, routing, task transitions, authorization, resource conflicts, LLM draft processing, template/PDF generation, QR evidence and email delivery.
- Treat every client input as untrusted. Validate request shape and domain rules server-side even when the frontend already validates it.
- Return schema requirements, eligible candidates, workflow state and allowed actions to the frontend. The frontend may display them; it must not be required to calculate business decisions.
- Never authorize a request because a button was hidden, the frontend supplied a role, or a client claimed the task was active.

## Architecture and OOP

- Use the modular monolith in the PRD, implemented as one ASP.NET Core project (`backend.csproj` / `Program.cs`, assembly `SignIt.Api`). Each feature lives in `Modules/<Feature>/` (`Controllers`, `DTOs`, `Models`, `Services`, `Data`); shared adapters live in `Infrastructure/` (`Persistence` with `AppDbContext` and `Migrations`, `Email`, `Storage`, `Llm`, `Errors`). Reuse existing conventions; if the repository differs, reconcile the affected boundary before moving code.
- `Models` owns entities, value objects and invariants. `Services`/`DTOs` own use cases, orchestration and HTTP-facing contracts. `Data` and `Infrastructure` own EF Core, storage and provider adapters. `Controllers` own HTTP contracts, binding and response mapping. These are namespace/folder boundaries (not separate projects): keep them one-directional.
- `Models` must not depend on ASP.NET Core, EF Core, provider SDKs or `Infrastructure`. `Services` use abstractions for external dependencies; implementations are wired at the composition root (`Program.cs` / `Infrastructure/DependencyInjection.cs`).
- Encapsulate state transitions in named operations. Do not let controllers, provider adapters or arbitrary property setters bypass invariants.
- Use constructor injection and the existing DI container. Prefer composition over inheritance; use interfaces at actual external or substitution boundaries.
- Keep Controllers or Minimal API handlers thin and use one consistent style. Do not mix both for the same feature without a reason.
- Do not add generic repositories over EF Core, base service hierarchies, CQRS frameworks, event buses or microservices merely to satisfy OOP or clean architecture.
- Keep a use case's transaction boundary clear. Avoid abstractions that hide when data is committed or external effects occur.

## Clean code

- Use names that describe the operation. Separate validation, business transitions, persistence and provider calls when they represent different responsibilities.
- Prefer explicit typed DTOs and mappings. Do not expose tracked entities, passwords, provider payloads or internal exception details through API responses.
- Use nullable annotations and handle absent data deliberately. Do not suppress warnings or catch exceptions just to return success.
- Pass `CancellationToken` through asynchronous request and provider calls where supported. Do not use `.Result`, `.Wait()` or `async void` in request processing.
- Avoid duplicate business rules and unexplained constants. Keep workflow/template policies versioned; keep runtime limits in validated configuration.
- Remove dead code and unused imports. Comments explain constraints or decisions, not obvious syntax. Do not leave placeholder success paths in completed features.
- Follow existing formatting/analyzers. Fix issues introduced by the change without reformatting unrelated files.

## Accounts and authorization

- Four account categories, two frontend surfaces: mahasiswa umum and Dagri BEM use mahasiswa; BAAK and manajemen/kemahasiswaan use manajemen. Categories alone do not grant every task or unit permission.
- Accounts, assignments, organizations, templates, routing and resources are provisioned by the team through explicit seed/scripts/migrations. No admin role, account/configuration CRUD endpoints, registration or SSO in the MVP.
- Seeds are idempotent using stable identifiers; rerunning them must not duplicate accounts, reset existing passwords or rewrite historical snapshots. Secrets are supplied outside the seed source.
- Use established password hashing, login protection and single-use reset tokens. Apply the session/cookie/JWT pattern agreed with the frontend; enforce the corresponding CSRF/CORS and expiry behavior.
- Check ownership, task assignment, active scope and delegation on each operation. Filter queries by authorized scope before returning data, counts or pagination.

## Letters, routing and workflow

- Separate the submitting account from the people named as Mengajukan, Hormat Kami and Mengetahui. Resolve participants to eligible provisioned accounts; do not create users from typed names.
- Route approvers by letter purpose, organization, field and valid assignments. Freeze the resolved policy/template/participants in the submitted revision.
- Proposal/LPJ chain: Ketupel, Ketua Organisasi, Pembina, Minat Bakat/Kemahasiswaan, Wadir 3.
- Pasca/SAW room chain: Ketupel, Ketua Organisasi, Pembina Organisasi, Minat Bakat/Kemahasiswaan, Dagri BEM, BAAK, Wadir 3.
- Item borrowing chain: Ketupel, Ketua Organisasi, Pembina Organisasi, Minat Bakat/Kemahasiswaan, Wadir 3, Wadir 2. Other templates follow seeded routing; do not invent a campus policy.
- Only the authorized active task can act. Required deferred tasks do not count as completed; delegation records both the actual actor and mandate.
- Implement the PRD state machine explicitly. A changed revision cannot reuse old approvals as evidence for new content; preserve historical evidence and restart required tasks according to policy.
- Use optimistic concurrency and database transactions for mutations. Honor expected revision and idempotency keys; bind keys to actor/operation and detect conflicting payload reuse.
- Queue order is scoped to active tasks by `ActivatedAt` with a stable tie-breaker. Queue position is not a promised completion time.
- Check resource conflicts again at the final reservation transaction. Prevent concurrent approvals from reserving overlapping resources through database-enforced coordination, not a prior availability lookup alone.

## QR and documents

- Generate a QR for each provisioned account in the backend. No QR upload, signing PIN/OTP or certificate-based signing in the MVP.
- QR is an identifier, not an authorization credential. Select the signing actor from the authenticated authorized task, not a QR/user ID supplied as proof by the client.
- Persist actor, task, revision/document reference, timestamp and audit evidence. Render QR overlays only for actions actually completed; draft slots remain placeholders.
- Use a deterministic template renderer with validated data. Keep template versions and signature layout associated with the document revision.
- Inspect uploaded PDFs and validate page/rotation/coordinate mappings before overlays. Enforce file limits and private authorized download access; do not trust filename extensions alone.
- Run OCR and expensive PDF work as jobs. Persist job state and finalization results; retries must not duplicate evidence or require approving the same completed task again.
- `Completed` requires all mandatory evidence and a successfully produced final document. Failed rendering becomes `ProcessingFailed`, not a fake final PDF.
- Public QR status and file-hash verification are optional P2. Do not describe internal QR signatures as certified or claim that scanning a QR proves a file's content is unchanged.

## LLM letter assistant

- The first assistant question is “Ingin membuat tipe surat apa?”. Use the selected seeded template schema as the source of required fields.
- Accept natural-language or multi-field input, extract a typed candidate patch, then validate it before saving. Treat user text and document text as data, never as instructions that grant tool permissions.
- Resolve names/resources against authorized candidates. Missing or ambiguous values require clarification; never invent users, IDs, dates, budgets or signatures.
- Persist the user's draft and apply corrections to the intended fields only. Use revision/concurrency controls so late LLM responses cannot overwrite newer edits.
- Generate only after validated review and an explicit user request. Generation creates a preview; submission remains a separate authorized operation. The LLM cannot sign, approve, reject or bypass workflow.
- Tools execute within the authenticated session's scope. Search results, counts and sources must not leak another user's data.
- Provider failure preserves the draft and exposes enough schema/validation information for a frontend form fallback. Keep provider credentials server-side and avoid logging sensitive prompts/documents by default.

## Email, jobs and integrations

- Email is the only notification channel. Do not add an in-app notification API, read/unread state or SignalR hub. Transaction lists and timelines remain available.
- Write workflow changes and email outbox records atomically. Workers deliver after commit, with deduplication, bounded retries, quota handling and persisted outcomes.
- Use the configured Resend adapter; do not send production email in tests, seed runs or preview deployments by default. Keep local/test delivery isolated.
- Verify Resend webhook signatures against the raw body, deduplicate provider event IDs and enforce replay protections using the provider's current documentation.
- Delivery status is separate from approval status; provider acceptance or delivery is not proof of reading. Reminder recipients follow the current active assignment and stop when no longer applicable.
- Use typed provider/storage adapters, timeouts and configuration validation. API requests must not wait for email delivery or long OCR jobs.
- Keep audit records for important mutations and provisioning changes. Structured logs carry correlation IDs without secrets or unnecessary document content. No audit or email-health admin UI/API in the MVP.

## API contracts and tests

- Follow the PRD `/api/v1` contract and the repository's OpenAPI conventions. Update DTO/schema documentation when a contract changes; do not silently break the frontend.
- Return consistent validation, unauthorized/forbidden, not-found, conflict and processing errors. Use appropriate HTTP responses, safe messages and stable error codes.
- Paginate and order lists deterministically. Bound upload size, search ranges, LLM input and job processing according to configured limits.
- Add focused tests for new or changed domain behavior. Prioritize authorization, revision invalidation, chain 5/7/6, idempotency, concurrent reservation, outbox deduplication and LLM field validation.
- Verify persistence-sensitive behavior against PostgreSQL using existing integration tooling; in-memory substitutes do not establish real transaction/constraint behavior. Mock external delivery/LLM services, not the invariant being tested.
- Test relevant failure paths: stale tasks, renderer/OCR/provider failure, ambiguous participants and retries. Do not write tests that merely repeat the implementation.

## Before finishing

- Review the diff for duplicated rules, unnecessary layers, secrets, unsafe schema changes and accidental contract changes.
- Run relevant tests and build. State what changed, what was verified and what remains blocked; do not label an endpoint integrated when it only returns fixture data.
- Do not claim release readiness from a passing build alone. Deployment checks include persistent database/storage, worker execution, private files and the agreed Vercel-to-Azure authentication flow.
