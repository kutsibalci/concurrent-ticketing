<h1 align="center">Seat Reservation API</h1>

<p align="center">
  A ticketing API built around one question:<br>
  <b>what stops the same seat from being sold twice?</b>
</p>

<p align="center">
  <a href="https://github.com/kutsibalci/seat-reservation-api/actions/workflows/ci.yml">
    <img src="https://github.com/kutsibalci/seat-reservation-api/actions/workflows/ci.yml/badge.svg" alt="CI" />
  </a>
  <img src="https://img.shields.io/badge/tests-65-brightgreen?style=flat-square" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/PostgreSQL-16-4169E1?style=flat-square&logo=postgresql&logoColor=white" />
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

## Architecture

```
SeatReservation.Domain          Entities and rules. No EF Core, no ASP.NET, no I/O.
        ↑
SeatReservation.Application     Use cases against interfaces (IApplicationDbContext,
        ↑                       ITokenService, ISeatAvailabilityCache)
SeatReservation.Infrastructure  EF Core + Npgsql, JWT, PBKDF2, Redis
        ↑
SeatReservation.Api             Minimal API endpoints, auth, OpenAPI, the sweeper
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

Swagger UI on <http://127.0.0.1:8080/swagger>, health on `/health`.

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
dotnet test          # 65 tests; integration tests need a running Docker daemon
```

| Suite | Count | Needs |
|---|---|---|
| `SeatReservation.UnitTests` | 42 | nothing — pure domain |
| `SeatReservation.IntegrationTests` | 23 | Docker (Testcontainers starts PostgreSQL 16) |

Beyond the concurrency cases, the integration suite covers refresh-token rotation
invalidating the old token, passwords and refresh tokens never appearing in clear text in
the database, a stranger being refused another user's reservation, role enforcement on
admin routes, and the seat map not going stale after a reservation.

## Notes

The parts worth reading are `ReservationService.CreateAsync` for the conflict path,
`SeatConfiguration` for the `xmin` mapping, and `ConcurrencyTests` for the proof.

Two things this deliberately does not do: it takes no payment (`Confirm` stands in for a
payment callback), and it has no seat-selection UI. Both would add surface without
adding anything to the problem the project is about.

---

<p align="center"><sub>Built by <a href="https://github.com/kutsibalci">Hüseyin Kutsi Balcı</a> · MIT licensed</sub></p>
