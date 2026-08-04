<h1 align="center">Seat Reservation API</h1>

<p align="center">
  A ticketing API built around one question:<br>
  <b>what stops the same seat from being sold twice?</b>
</p>

<p align="center">
  <a href="https://github.com/kutsibalci/seat-reservation-api/actions/workflows/ci.yml">
    <img src="https://github.com/kutsibalci/seat-reservation-api/actions/workflows/ci.yml/badge.svg" alt="CI" />
  </a>
  <img src="https://img.shields.io/badge/tests-84-brightgreen?style=flat-square" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/PostgreSQL-16-4169E1?style=flat-square&logo=postgresql&logoColor=white" />
  <img src="https://img.shields.io/badge/RabbitMQ-3.13-FF6600?style=flat-square&logo=rabbitmq&logoColor=white" />
  <img src="https://img.shields.io/badge/Redis-7-DC382D?style=flat-square&logo=redis&logoColor=white" />
  <img src="https://img.shields.io/badge/Docker-2496ED?style=flat-square&logo=docker&logoColor=white" />
  <img src="https://img.shields.io/badge/license-MIT-blue?style=flat-square" />
</p>

---

## The problem

Two customers open the same event page and click seat **A12** in the same millisecond.

The obvious implementation loses:

```csharp
var seat = await db.Seats.FindAsync(seatId);
if (seat.Status == SeatStatus.Available)   // ← both requests read Available
{
    seat.Status = SeatStatus.Held;         // ← both requests write Held
    await db.SaveChangesAsync();           // ← the seat is sold twice
}
```

Nothing is wrong with either line. The bug lives in the gap between them — a
time-of-check/time-of-use race, and it only appears under load, which is exactly when it
costs the most.

## The fix

`Seat` maps PostgreSQL's `xmin` system column as a concurrency token:

```csharp
builder.Property(s => s.Version)
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```

`xmin` holds the id of the transaction that last wrote the row, so it changes on every
update. EF Core then carries the value it read into the write:

```sql
UPDATE seats SET status = 1, reservation_id = @r
WHERE id = @id AND xmin = @version;
```

The first transaction to commit changes `xmin`. The second one's `WHERE` no longer
matches, **zero rows are affected**, EF raises `DbUpdateConcurrencyException`, and the
API answers **409 Conflict** instead of overwriting a sale.

No table locks. No `SELECT ... FOR UPDATE`. No lock ordering to get wrong. Readers are
never blocked, and two customers buying *different* seats never contend at all.

### Proven, not asserted

`ConcurrencyTests.Yirmi_es_zamanli_istek_ayni_koltugu_yalnizca_bir_kisiye_satar` registers
twenty users, releases them through a single `SemaphoreSlim` so the requests genuinely
overlap, and points all twenty at one seat:

```
20 concurrent requests → 1 × 201 Created, 19 × 409 Conflict
database: 1 held seat, 1 reservation
```

Against real PostgreSQL in a Testcontainers container — not an in-memory provider, which
has no `xmin` and therefore cannot lose a race at all. A test that cannot fail proves
nothing.

## Why holds expire

Taking payment is slow. Marking seats `Booked` only after payment lets a second customer
take them mid-checkout; marking them `Booked` before means an abandoned basket strands
them for good. So a reservation is a **hold with a deadline**:

```
POST /api/reservations          seats → Held,  hold expires in 10 minutes
POST /api/reservations/{id}/confirm   seats → Booked
DELETE /api/reservations/{id}         seats → Available
```

A background `ExpiredHoldSweeper` reclaims lapsed holds every minute. It is a cleanup,
not the rule: `Confirm` checks the deadline against the clock itself, so a sweeper that
is late — or dead — cannot let an expired hold through.

## The second race: telling anyone about it

Confirming a reservation writes to PostgreSQL and publishes to RabbitMQ, and there is no
transaction across both.

- Publish first, and the broker may hold an event for a database write that then fails.
- Publish after the commit, and the process can die in between — the event is gone with
  no trace that it ever should have existed.

Either way the two systems disagree and nothing in the data says so. The **outbox pattern**
removes the gap: the event is written as a row in the same transaction as the reservation,
so both land or neither does.

```
POST /confirm ──┐
                ├── one transaction ──► reservations UPDATE
                └────────────────────► outbox_messages INSERT
                                              │
              OutboxDispatcher (every 5s) ────┘
                        │
                        └──► RabbitMQ ──► worker ──► notification
```

A separate dispatcher moves rows to the broker. That turns *exactly once* — which is not
available — into *at least once*, which is, and the consumer absorbs the difference by
being idempotent: a `processed_messages` row keyed on the message id, inserted in the same
transaction as the work, so a duplicate delivery violates the primary key instead of
sending a second e-mail.

Two details worth pointing at:

**`FOR UPDATE SKIP LOCKED`** is what lets a second dispatcher be started. `FOR UPDATE`
alone would make it wait behind the first, turning horizontal scale into a queue;
`SKIP LOCKED` tells PostgreSQL to pass over rows another transaction holds, so each
dispatcher claims a disjoint batch. `Es_zamanli_gondericiler_ayni_mesaji_iki_kez_yayinlamaz`
runs four of them over forty messages and asserts forty distinct publishes.

**A failed publish is not a lost event.** The row stays unprocessed, the attempt count goes
up, and the retry is scheduled with exponential backoff. After the limit it is marked dead
and left in the table — deleting it would destroy the only record that something never
reached anyone.

## Architecture

```
SeatReservation.Domain          Entities, rules, event contracts. No EF Core, no ASP.NET, no I/O.
        ↑
SeatReservation.Application     Use cases against interfaces (IApplicationDbContext,
        ↑                       ITokenService, ISeatAvailabilityCache, IEventPublisher)
SeatReservation.Infrastructure  EF Core + Npgsql, JWT, PBKDF2, Redis, RabbitMQ
        ↑
SeatReservation.Api             Minimal API, auth, OpenAPI, hold sweeper, outbox dispatcher
SeatReservation.Worker          Consumes events and sends notifications
```

Dependencies point inward. `Seat.Hold()` and `Reservation.Confirm()` are pure methods
over in-memory objects, which is why 42 of the 65 tests need no database at all and run
in under a second.

`TimeProvider` is injected rather than `DateTimeOffset.UtcNow` being called directly, so a
test can place itself one second either side of a deadline instead of sleeping.

## Security

| Concern | Approach |
|---|---|
| Passwords | PBKDF2-HMAC-SHA256, 210 000 iterations, per-password salt, fixed-time comparison. The iteration count is embedded in the hash so it can be raised without invalidating existing rows. |
| Refresh tokens | 256 bits of entropy, stored **hashed**. A read of the token table is not enough to impersonate anyone. Plain SHA-256 rather than PBKDF2 here: the value is already random, so there is nothing for a slow KDF to protect. |
| Token rotation | Refreshing revokes the token presented and links it to its replacement, so a stolen refresh token works at most once and leaves a trail. |
| Signing key | No default. Startup validation refuses to run without one of at least 32 characters — a signing key with a fallback is a key anyone who read the source can forge tokens with. |
| Clock skew | 30 seconds, not the 5-minute default, which would extend a 15-minute access token by a third. |
| Ownership | Every reservation endpoint compares the record's owner against the `NameIdentifier` claim. An id is not authorization. |
| User enumeration | Login answers identically for an unknown address and a wrong password, and hashes anyway when the user does not exist so the two paths take comparable time. |
| Exposure | Neither PostgreSQL nor Redis publishes a port. The API listens on `127.0.0.1` only. |
| Containers | The API image runs as a non-root user and the SDK never reaches the runtime layer. |
| Dependencies | CI fails on any package with a known advisory, including transitive ones, and gitleaks scans the full history. |

## Running it

```bash
git clone https://github.com/kutsibalci/seat-reservation-api.git
cd seat-reservation-api

cp .env.example .env
# set POSTGRES_PASSWORD and JWT_SIGNING_KEY  (openssl rand -base64 48)

docker compose up -d --build
```

Swagger UI on <http://127.0.0.1:8080/swagger>, health on `/health`, RabbitMQ management on
<http://127.0.0.1:15672>. Change `API_PORT` in `.env` if something already holds 8080.

Migrations are applied at startup, and in Development one event with a 70-seat map is
seeded — so `GET /api/events` has something in it on the first run.

Confirm a reservation and the worker's log shows the notification a few seconds later — the
delay is the dispatcher's poll interval, which is the visible cost of not publishing inline:

```
worker-1  | BILDIRIM -> ben@ornek.test: 'Final Maçı' icin 2 koltuk onaylandi (A1, A2), toplam 1.000,00 TL.
```

<details>
<summary>End-to-end with curl</summary>

```bash
API=http://127.0.0.1:8080

TOKEN=$(curl -s -X POST $API/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"ben@ornek.test","password":"GucluSifre123!","displayName":"Ben"}' \
  | jq -r .accessToken)

EVENT=$(curl -s $API/api/events | jq -r '.[0].id')
SEAT=$(curl -s $API/api/events/$EVENT/seats | jq -r '.seats[] | select(.status=="Available") | .id' | head -1)

curl -s -X POST $API/api/reservations \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"eventId\":\"$EVENT\",\"seatIds\":[\"$SEAT\"]}" | jq
```
</details>

## Endpoints

| Method | Route | Auth | |
|---|---|---|---|
| `POST` | `/api/auth/register` | — | Account + token pair |
| `POST` | `/api/auth/login` | — | Token pair |
| `POST` | `/api/auth/refresh` | — | Rotates the refresh token |
| `GET` | `/api/events` | — | Catalogue with live availability |
| `GET` | `/api/events/{id}/seats` | — | Seat map (cached, evicted on every write) |
| `POST` | `/api/events` | Admin | Create an event and its seat blocks |
| `POST` | `/api/reservations` | Customer | **Hold seats — 409 on a lost race** |
| `POST` | `/api/reservations/{id}/confirm` | Customer | Hold → booking |
| `DELETE` | `/api/reservations/{id}` | Customer | Release seats |
| `GET` | `/api/reservations` | Customer | The caller's own reservations |

## Tests

```bash
dotnet test          # 84 tests; integration tests need a running Docker daemon
```

| Suite | Count | Needs |
|---|---|---|
| `SeatReservation.UnitTests` | 42 | nothing — pure domain |
| `SeatReservation.IntegrationTests` | 42 | Docker (Testcontainers starts PostgreSQL 16 and RabbitMQ 3.13) |

Beyond the concurrency cases, the integration suite covers refresh-token rotation
invalidating the old token, passwords and refresh tokens never appearing in clear text in
the database, a stranger being refused another user's reservation, role enforcement on
admin routes, and the seat map not going stale after a reservation.

The outbox is tested against a publisher the test controls — that is the right tool for
driving retry, backoff and dead-lettering deliberately — plus one round trip through a
real broker, because a fake proves nothing about whether the exchange, the binding and the
message properties are actually right.

## Notes

The parts worth reading are `ReservationService.CreateAsync` for the conflict path,
`SeatConfiguration` for the `xmin` mapping, `ApplicationDbContext.ClaimDueOutboxMessagesAsync`
for `SKIP LOCKED`, and `ConcurrencyTests` and `OutboxTests` for the proof.

RabbitMQ is driven through `RabbitMQ.Client` rather than a framework on top of it. The
mechanics — publisher confirms, manual acknowledgement, prefetch, a dead-letter exchange —
are the interesting part here, and hiding them behind conventions would defeat the point of
building it.

Three things this deliberately does not do: it takes no payment (`Confirm` stands in for a
payment callback), it has no seat-selection UI, and the worker logs notifications rather
than sending mail. Each would add an integration without adding anything to the two
problems the project is actually about — selling a seat once, and telling someone about it
exactly once.

---

<p align="center"><sub>Built by <a href="https://github.com/kutsibalci">Hüseyin Kutsi Balcı</a> · MIT licensed</sub></p>
