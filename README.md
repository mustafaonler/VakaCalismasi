# ATM Cash Management Backend

Backend for the cash management of a bank's ATM network: cassette configuration, cash loading,
two-step withdrawals with optimal note selection, deposits, manual reconciliation with four-eyes
approval, an immutable cassette movement ledger, reporting and low-cash alerts.

**Stack:** .NET 10 · ASP.NET Core minimal APIs · EF Core 10 (code-first, migrations) · PostgreSQL 17 ·
xUnit · Testcontainers · Docker Compose

All of Part A and every Part B item (B1–B10) are implemented and covered by automated tests.

---

## Contents

1. [How to run](#how-to-run)
2. [API overview](#api-overview)
3. [Architecture](#architecture)
4. [Key design decisions](#key-design-decisions)
5. [Assumptions](#assumptions)
6. [Unfinished items and known limitations](#unfinished-items-and-known-limitations)
7. [AI usage](#ai-usage)

---

## How to run

### With Docker (API + database)

```bash
docker compose up --build
```

- API: <http://localhost:8080>
- Swagger UI: <http://localhost:8080/swagger>
- OpenAPI document: <http://localhost:8080/openapi/v1.json>
- Health check: <http://localhost:8080/health> (includes a database check)

Migrations are applied automatically when the API starts. PostgreSQL is exposed on `localhost:5432`
(database, user and password: `atmcash`).

### Tests

Requirements: .NET 10 SDK and a running Docker engine (integration tests start their own
PostgreSQL container through Testcontainers).

```bash
dotnet test
```

| Project | Tests | What it covers |
|---|---|---|
| `AtmCash.UnitTests` | 71 | The eight B3 acceptance cases (verbatim) plus edge cases, ATM lifecycle, quotes, deposits, reconciliation, low-cash alerts |
| `AtmCash.IntegrationTests` | 49 | The real API (`WebApplicationFactory`) against a real PostgreSQL 17 container: every endpoint, error responses, the append-only ledger trigger, idempotency, quote expiry, the outbox worker, and parallel-request tests for B6 |

### Postman

`postman/AtmCash.postman_collection.json` runs the end-to-end scenario in order:
create ATM → configure cassettes → load cash → put in service → request quote → confirm →
deposit → switch to maintenance → reconcile → report. It also shows an idempotent confirm
replay, the four-eyes approval (blocked service, same-user rejection, approval by a second user)
and the consistency check. Each request asserts its result and passes ids on through collection
variables, so the whole collection can be run with the Collection Runner or
`newman run postman/AtmCash.postman_collection.json`. No environment file is needed;
`baseUrl` is a collection variable (default `http://localhost:8080`).

---

## API overview

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/atms` | Create an ATM (starts `UNCONFIGURED`) |
| GET | `/api/atms`, `/api/atms/{id}` | List ATMs / notes per cassette and denomination, balance per currency |
| PUT | `/api/atms/{id}/cassettes` | Configure the four recycling cassettes (slot 5 is always the deposit cassette) |
| POST | `/api/atms/{id}/cash-loads` | Record cash loaded by hand |
| POST | `/api/atms/{id}/cash-unloads` | Empty cassettes (e.g. collect the deposit cassette) |
| POST | `/api/atms/{id}/status` | Move to `IN_SERVICE` or `MAINTENANCE` |
| POST | `/api/atms/{id}/withdrawal-quotes` | Quote a withdrawal; reserves the offered notes for 60 seconds |
| GET | `/api/withdrawal-quotes/{id}` | Get a quote |
| POST | `/api/withdrawal-quotes/{id}/confirm` | Dispense the cash; requires an `Idempotency-Key` header |
| POST | `/api/withdrawal-quotes/{id}/cancel` | Cancel and release the reservation |
| POST | `/api/atms/{id}/deposits` | Deposit; shows the cassette of every note and the returned notes |
| POST | `/api/atms/{id}/reconciliations` | Record a manual count (`X-User` header) |
| POST | `/api/reconciliations/{id}/approve` | Four-eyes approval by a different `X-User` |
| GET | `/api/reconciliations/{id}`, `/api/exchange-rates` | Reconciliation details / rates used for TRY values |
| GET | `/api/consistency`, `/api/atms/{id}/consistency` | Compare stored balances with balances replayed from the ledger |
| GET | `/api/reports/operations[?atmId=]` | Note, reserved and available counts per cassette; totals per currency and in TRY |
| GET | `/health` | Liveness and database check |

Enums use the spellings of the assignment: `IN_SERVICE`, `RESERVATION_RELEASE`, `TRY` and so on.

**Error model.** Every error is an RFC 9457 `application/problem+json` response with a stable,
machine-readable `code` next to the human-readable `detail`:

```json
{
  "type": "https://httpstatuses.io/409",
  "title": "Request conflicts with current state",
  "status": 409,
  "detail": "ATM IST-1 cannot move from CONFIGURED to IN_SERVICE; allowed from: LOADED, MAINTENANCE.",
  "code": "INVALID_STATE_TRANSITION",
  "traceId": "00-..."
}
```

Status codes: 400 validation (`INVALID_DENOMINATION`, `IDEMPOTENCY_KEY_REQUIRED`, `USER_REQUIRED`,
malformed JSON as `INVALID_REQUEST` …), 403 (`SAME_USER_APPROVAL`), 404 (`ATM_NOT_FOUND`,
`QUOTE_NOT_FOUND` …), 409 business conflicts (`OPERATION_NOT_ALLOWED_IN_STATE`,
`DAILY_LIMIT_EXCEEDED`, `QUOTE_EXPIRED`, `NO_PAYABLE_AMOUNT`, `RECONCILIATION_AWAITING_APPROVAL` …).
All codes are listed in `src/AtmCash.Domain/Common/ErrorCodes.cs`.

---

## Architecture

```
src/
  AtmCash.Domain          Business rules only, no framework dependencies
    Atms/                 Atm aggregate (5 cassettes, lifecycle), Cassette, holdings, movements, low-cash alert
    Cash/                 Currency, denominations, NoteSelector (B3), exchange rate
    Withdrawals/          WithdrawalQuote (B4), daily limits (B5)
    Deposits/             Deposit routing results (B7)
    Reconciliations/      Reconciliation with four-eyes approval (B9)
  AtmCash.Application     Use cases: transactions, locking, DTOs; depends on an IAtmDbContext abstraction
  AtmCash.Infrastructure  EF Core DbContext, mappings, migrations, card hashing
  AtmCash.Api             Minimal API endpoints, ProblemDetails, hosted services, composition root
tests/
  AtmCash.UnitTests        Domain tests, no database
  AtmCash.IntegrationTests API + PostgreSQL (Testcontainers)
postman/                   End-to-end collection
```

`Atm` is the aggregate root: every change to a cassette goes through one of its methods, which
enforce the lifecycle and capacity rules and return the matching ledger movements. The
Application layer wraps each use case in a transaction; the Infrastructure layer saves the
aggregate together with the movements and alerts it raised. Dependencies point inwards only
(Api → Application/Infrastructure → Domain).

---

## Key design decisions

### Note selection (B3): bounded dynamic programming

Taking the largest note first fails with limited stock. For example, 110 from 100×3, 50×1,
20×1 should give lower 100 and upper 120, and 200 from 100×1, 50×4, 20×5 should be 100 + 50×2.
`NoteSelector` instead solves bounded change-making with a dynamic program over amounts in units
of 10 (all denominations are multiples of 10): `minNotes[a]` is the fewest notes that pay `a`
with the available stock. One table answers all three questions:

- **Exact:** is the requested amount reachable, and with how few notes?
- **Lower alternative:** the largest reachable amount below the request.
- **Upper alternative:** the smallest reachable amount above it, but not above the customer's
  remaining daily limit.

The worst case (four full cassettes of 500s, i.e. 200,000) is 20,000 cells times a few hundred
note counts: a few million operations, about 4 ms. The eight acceptance cases are unit tests
written in the notation of the assignment table.

### Concurrency (B6): one row lock per ATM

Every operation that changes an ATM's cash (quote, confirm, cancel, expiry, deposit, load,
unload, reconcile, status change) runs in a transaction that first takes
`SELECT ... FROM atms WHERE id = @id FOR UPDATE`, then loads the aggregate, changes it and
commits. Operations on the same ATM are therefore serialized, while different ATMs proceed in
parallel. This is the simplest model that is obviously correct: an ATM serves one customer at a
time anyway, and every decision is made against fresh balances. Database check constraints
(`reserved BETWEEN 0 AND count`, `count BETWEEN 0 AND 100`) are a second line of defence.

The daily limit spans ATMs, so quotes also take a transaction-scoped advisory lock on the card
(`pg_advisory_xact_lock`, keyed by the card hash). Lock order is always card, then ATM. Confirm,
cancel and expiry take only the ATM lock, so no lock cycle is possible.

Both are proven by integration tests that fire 30 simultaneous requests:

- **Same ATM:** with 10 notes available, exactly 10 quotes succeed and no note is reserved twice.
- **Same card across two ATMs:** exactly the TRY 20,000 limit is granted.

Removing the `FOR UPDATE` makes the first test fail.

### Two-step withdrawal and idempotency (B4)

**Quote.** A quote stores its offered breakdowns and reserves notes (`RESERVATION` movements).
With two alternatives it reserves, per denomination, the maximum either one needs. Each
alternative fits the stock and the constraint is per denomination, so the maximum fits too, and
the customer can confirm either.

**Confirm.** Confirming dispenses the chosen breakdown (`RESERVATION_RELEASE` + `WITHDRAWAL`) and
releases the rest. Quotes are valid for 60 seconds. An overdue quote is expired when it is
confirmed, when the next quote on the same ATM is requested, or by a background sweep every
5 seconds, whichever comes first. An expired or
cancelled quote cannot be confirmed.

**Idempotency.** The confirm result is stored in `idempotency_records` under its
`Idempotency-Key`, in the same transaction as the cash movements. The key is looked up while
holding the ATM lock, so a retry, or a concurrent duplicate, either waits for the first request
to commit and then returns the stored response byte for byte, or does the work itself. Cash can
never be debited twice. Reusing a key for a different quote is rejected.

### Movement ledger (B8)

Every change to a cassette is an immutable `cassette_movements` row (`LOAD`, `UNLOAD`,
`WITHDRAWAL`, `DEPOSIT`, `RESERVATION`, `RESERVATION_RELEASE`, `RECONCILIATION_ADJUSTMENT`) with
a signed quantity and a correlation id: the quote, deposit or reconciliation that caused it.

- **Same transaction:** movements are produced by the domain methods themselves and saved by the
  same `SaveChanges` as the balance, so the two cannot diverge.
- **Append-only at the database level:** a PostgreSQL trigger rejects every `UPDATE` and `DELETE`
  on the table.
- **Reproducible balances:** a holding's note count is the sum of its non-reservation movements,
  and its reserved count is the sum of its reservation movements.

The consistency check endpoints replay the ledger and compare it with the stored balances for
every cassette holding, reporting matches and differences. Both reads run in one
`REPEATABLE READ` snapshot, so a transaction committing in between cannot produce a false
difference. An integration test changes a stored balance behind the ledger's back and checks
that it is reported.

Balances are stored as well as derived, so that reads and checks stay cheap and the ledger
remains an independent audit trail.

### Reconciliation and four-eyes approval (B9)

A reconciliation must count all five cassettes: a single `count` for a recycling cassette, and
notes per currency and denomination for the mixed deposit cassette. The differences are booked
at once as `RECONCILIATION_ADJUSTMENT` movements, so the system matches the physical cash.
Shortages are booked before surpluses so a cassette never exceeds its capacity mid-way.

The differences are valued in TRY with the `exchange_rates` table (seed data). The threshold
uses the **sum of absolute values**: a TRY 2,000 shortage in one cassette and a TRY 2,000
surplus in another add up to TRY 4,000, not zero, so they cannot hide each other. Above
TRY 1,000, the reconciliation becomes `AWAITING_APPROVAL`.

The ATM keeps the five lifecycle states of B2. A pending reconciliation is recorded on the ATM
(`pendingReconciliationId`, visible in the ATM details and the report), and while it is set,
`IN_SERVICE` is refused with `RECONCILIATION_AWAITING_APPROVAL`. Only a user whose `X-User`
differs from the counting user (compared case-insensitively) can approve; otherwise the API
returns 403 `SAME_USER_APPROVAL`. This blocks the ATM exactly as an extra state would, without
changing the lifecycle the assignment defines.

### Transactional outbox for low-cash alerts (B10)

When an operation takes a recycling cassette's available count below 10% of its capacity (fewer
than 10 notes), the aggregate raises a `LowCashAlert`. The check compares the state before and
after the whole operation. Comparing per movement would be wrong: confirming a withdrawal
releases and removes the same notes, which would look like a fresh drop. An alert fires on
crossing the threshold, not repeatedly while below it.

Alerts are written to `outbox_messages` by the same `SaveChanges`, and therefore the same
transaction, as the change that caused them: an alert exists if and only if its operation
committed. `OutboxPublisherService` (a hosted service) runs every 2 seconds:

1. Locks pending messages with `FOR UPDATE SKIP LOCKED`, so several API instances never take the
   same message.
2. Logs each message as a warning, which stands in for a broker or notification call.
3. Marks the messages processed in the same transaction.

Delivery is at-least-once.

### Card numbers (B5)

Card numbers are never stored. Quotes keep an HMAC-SHA256 of the number with a server-side secret
(`Security:CardHashSecret`). A plain SHA-256 would not be enough: card numbers have little
entropy, so every possible number could be hashed and looked up.

### Operations report (B10)

`GET /api/reports/operations` returns, per ATM and cassette:

- `noteCount`, `reservedCount` and `availableCount`, plus a low-cash flag;
- the deposit cassette's notes per currency and denomination;
- totals per currency (count, reserved, available, amount, available amount), valued in TRY with
  the rate used;
- the TRY total per ATM and for the whole network.

Monetary values in responses are rounded to two decimals; calculations keep full precision.

---

## Assumptions

Points the assignment leaves open, and how they were decided:

**Configuration and lifecycle**
- "At least one and at most two recycling cassettes may be configured for a foreign currency" is
  read literally: a configuration must contain one or two foreign-currency cassettes.
- Configuration lists the four recycling cassettes in slot order 1–4; slot 5 is always the
  deposit cassette. A cassette may only change currency or denomination when it is empty
  (unload it first).
- `CONFIGURED` and `LOADED` are reached by configuring and loading. Only `IN_SERVICE` and
  `MAINTENANCE` can be requested directly; the first load moves `CONFIGURED` to `LOADED`.
  Cash can also be loaded again in `LOADED` and in `MAINTENANCE`.
- Reconciliation is allowed in `MAINTENANCE` only. During initial setup the hand-loaded cash is
  recorded through loading.
- Entering `MAINTENANCE` cancels open quotes, so no notes stay reserved while cassettes are
  counted or changed.

**Withdrawals**
- When several breakdowns use the same fewest number of notes, the tie is broken
  deterministically (fewer notes of the smallest denomination).
- Amounts that are not a multiple of 10 are never paid exactly; the alternatives are offered.
- The customer chooses the alternative at confirm (`EXACT`, `LOWER`, `UPPER`). `EXACT` is the
  default when an exact match exists.
- With two cassettes of the same denomination, withdrawals draw from the fuller one first
  (by available notes, as reserved notes are spoken for) and deposits fill the emptier one first.
- The daily limit applies per card, per currency and per UTC calendar day. Confirmed
  withdrawals count, and so does the largest amount an open quote could still turn into.
  A request above the remaining limit is rejected; the upper alternative is capped at the
  remaining limit.
- Card numbers must be 12–19 digits; spaces and dashes are ignored.

**Deposits**
- Deposits do not need a card number; the assignment does not ask for one.
- Deposit lines are routed in request order. This only decides which notes are returned when the
  deposit cassette runs out of room, not how many. Repeated lines for the same note are merged.
- Unrecognised and returned notes never enter a cassette, so they produce no ledger movement;
  they appear only in the response.

**Reconciliation and reporting**
- The approval threshold is exclusive: exactly TRY 1,000 does not need approval.
- Adjustments are booked when the count is recorded; approval gates the return to service.
- A new count is refused while one is awaiting approval. There is no reject flow (see
  limitations).
- Exchange rates are illustrative seed data (TRY 1, USD 41.50, EUR 48.25) and are read-only
  through the API.
- A low-cash alert is raised when a recycling cassette crosses below 10 available notes through
  a reservation (the first step of a withdrawal), a reconciliation shortage or an unload. Loading
  a cassette with fewer than 10 notes does not raise one, because the count did not fall.
  Confirming a withdrawal does not lower the available count again: the notes were already
  reserved.

---

## Unfinished items and known limitations

- **No authentication or authorization.** As the assignment allows, `X-User` is trusted as given.
- **No reject flow for reconciliations.** A count awaiting approval can only be approved. A wrong
  count would be corrected by a new reconciliation after approval.
- **Idempotency records are never purged.** A production system would expire them after a
  retention period. Two concurrent confirms that reuse one key for quotes on *different* ATMs
  would hit the primary key and return 500 instead of 409.
- **Outbox delivery.** "Publishing" means logging. There is no retry counter, back-off or
  dead-letter handling, and consumers must tolerate at-least-once delivery.
- **Daily limit window.** The day is the UTC calendar day, not Istanbul local time.
- **ATM code uniqueness** is checked before insert and enforced by a unique index; two
  simultaneous creations with the same code would make the second fail with 500 rather than 409.
- **Exchange rates** cannot be updated through the API, and there is no pagination on the list
  and report endpoints.
- **Per-ATM serialization.** All cash operations of one ATM are serialized; this matches how an
  ATM works but would need revisiting for a very high request rate on a single ATM.

---

## AI usage

## AI usage
This project was developed with the assistance of AI tools for generating boilerplate code, writing automated tests, and refining the documentation. All architectural decisions, business logic implementations, and final code reviews were carefully examined and confirmed by the developer.
