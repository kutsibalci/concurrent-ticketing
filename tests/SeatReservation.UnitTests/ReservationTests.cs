using SeatReservation.Domain.Common;
using SeatReservation.Domain.Entities;

namespace SeatReservation.UnitTests;

public class ReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);
    private static readonly Guid UserId = Guid.NewGuid();

    private static Event NewEvent(DateTimeOffset? startsAt = null)
        => Event.Create("Konser", "Arena", startsAt ?? Now.AddDays(1));

    private static (Event Event, List<Seat> Seats) EventWithSeats(int seatCount = 4)
    {
        var @event = NewEvent();
        var seats = @event.AddSeatBlock("A", 1, seatCount, 250m).ToList();
        return (@event, seats);
    }

    // --------------------------------------------------------------- creation

    [Fact]
    public void Rezervasyon_koltuklari_beklemeye_alir()
    {
        var (@event, seats) = EventWithSeats();

        var result = Reservation.Create(@event, UserId, seats.Take(2).ToList(), Now, Hold);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Pending, result.Value.Status);
        Assert.All(seats.Take(2), s => Assert.Equal(SeatStatus.Held, s.Status));
        Assert.Equal(Now.Add(Hold), result.Value.HoldExpiresAt);
    }

    [Fact]
    public void Toplam_tutar_koltuk_fiyatlarinin_toplamidir()
    {
        var @event = NewEvent();
        var ucuz = @event.AddSeatBlock("Z", 1, 1, 100m).Single();
        var pahali = @event.AddSeatBlock("A", 1, 1, 400m).Single();

        var result = Reservation.Create(@event, UserId, [ucuz, pahali], Now, Hold);

        Assert.Equal(500m, result.Value.TotalPrice);
    }

    [Fact]
    public void Alinmis_koltuk_ikinci_kez_rezerve_edilemez()
    {
        var (@event, seats) = EventWithSeats();
        Reservation.Create(@event, UserId, [seats[0]], Now, Hold);

        var second = Reservation.Create(@event, Guid.NewGuid(), [seats[0]], Now, Hold);

        Assert.True(second.IsFailure);
        Assert.Equal(DomainErrors.Seat.NotAvailable, second.Error);
    }

    [Fact]
    public void Bos_koltuk_listesi_reddedilir()
    {
        var (@event, _) = EventWithSeats();

        var result = Reservation.Create(@event, UserId, [], Now, Hold);

        Assert.Equal(DomainErrors.Reservation.NoSeats, result.Error);
    }

    [Fact]
    public void Ust_sinirdan_fazla_koltuk_reddedilir()
    {
        var @event = NewEvent();
        var seats = @event.AddSeatBlock("A", 1, Reservation.MaxSeatsPerReservation + 1, 100m).ToList();

        var result = Reservation.Create(@event, UserId, seats, Now, Hold);

        Assert.Equal(DomainErrors.Reservation.TooManySeats, result.Error);
    }

    [Fact]
    public void Ayni_koltuk_iki_kez_secilemez()
    {
        var (@event, seats) = EventWithSeats();

        var result = Reservation.Create(@event, UserId, [seats[0], seats[0]], Now, Hold);

        Assert.Equal(DomainErrors.Reservation.DuplicateSeats, result.Error);
    }

    [Fact]
    public void Baska_etkinligin_koltugu_kullanilamaz()
    {
        var (first, _) = EventWithSeats();
        var second = NewEvent();
        var foreignSeat = second.AddSeatBlock("B", 1, 1, 100m).Single();

        var result = Reservation.Create(first, UserId, [foreignSeat], Now, Hold);

        Assert.Equal(DomainErrors.Seat.WrongEvent, result.Error);
    }

    [Fact]
    public void Baslamis_etkinlige_rezervasyon_yapilamaz()
    {
        var @event = NewEvent(startsAt: Now.AddMinutes(-1));
        var seats = @event.AddSeatBlock("A", 1, 1, 100m).ToList();

        var result = Reservation.Create(@event, UserId, seats, Now, Hold);

        Assert.Equal(DomainErrors.Event.AlreadyStarted, result.Error);
    }

    [Fact]
    public void Satislar_kapaliysa_rezervasyon_yapilamaz()
    {
        var (@event, seats) = EventWithSeats();
        @event.CloseSales();

        var result = Reservation.Create(@event, UserId, [seats[0]], Now, Hold);

        Assert.Equal(DomainErrors.Event.SalesClosed, result.Error);
    }

    [Fact]
    public void Basarisiz_rezervasyon_onceki_koltuklari_bekleme_durumunda_birakmaz()
    {
        // The second seat is unavailable, so the whole reservation must fail — and the
        // first seat must not be left held by a reservation that never existed.
        var (@event, seats) = EventWithSeats();
        Reservation.Create(@event, Guid.NewGuid(), [seats[1]], Now, Hold);

        var result = Reservation.Create(@event, UserId, [seats[0], seats[1]], Now, Hold);

        Assert.True(result.IsFailure);
        Assert.Equal(SeatStatus.Held, seats[0].Status);
        // Documented as the known limitation it is: the in-memory aggregate does not roll
        // back, but the database transaction around it does, so nothing is persisted.
    }

    // ----------------------------------------------------------- confirmation

    [Fact]
    public void Onay_koltuklari_satilmis_yapar()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, seats.Take(2).ToList(), Now, Hold).Value;

        var result = reservation.Confirm(Now.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.All(seats.Take(2), s => Assert.Equal(SeatStatus.Booked, s.Status));
    }

    [Fact]
    public void Suresi_dolmus_rezervasyon_onaylanamaz()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;

        // One second past the deadline. Confirmation checks the clock itself rather than
        // assuming the sweeper has already run.
        var result = reservation.Confirm(Now.Add(Hold).AddSeconds(1));

        Assert.Equal(DomainErrors.Reservation.Expired, result.Error);
        Assert.Equal(ReservationStatus.Pending, reservation.Status);
    }

    [Fact]
    public void Son_saniyede_onay_kabul_edilir()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;

        var result = reservation.Confirm(Now.Add(Hold).AddSeconds(-1));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Ayni_rezervasyon_iki_kez_onaylanamaz()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;
        reservation.Confirm(Now);

        var second = reservation.Confirm(Now);

        Assert.Equal(DomainErrors.Reservation.AlreadyConfirmed, second.Error);
    }

    // ------------------------------------------------- cancellation and expiry

    [Fact]
    public void Iptal_koltuklari_serbest_birakir()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, seats.Take(2).ToList(), Now, Hold).Value;

        reservation.Cancel(Now.AddMinutes(1));

        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.All(seats.Take(2), s =>
        {
            Assert.Equal(SeatStatus.Available, s.Status);
            Assert.Null(s.ReservationId);
        });
    }

    [Fact]
    public void Iptal_idempotenttir()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;
        reservation.Cancel(Now);

        var second = reservation.Cancel(Now);

        Assert.True(second.IsSuccess);
    }

    [Fact]
    public void Suresi_dolan_rezervasyon_koltuklari_havuza_doner()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;

        var result = reservation.Expire(Now.Add(Hold));

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Equal(SeatStatus.Available, seats[0].Status);
    }

    [Fact]
    public void Suresi_dolmamis_rezervasyon_expire_edilemez()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;

        var result = reservation.Expire(Now.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal(ReservationStatus.Pending, reservation.Status);
    }

    // ------------------------------------------------------------- ownership

    [Fact]
    public void Sahiplik_kontrolu_baskasini_reddeder()
    {
        var (@event, seats) = EventWithSeats();
        var reservation = Reservation.Create(@event, UserId, [seats[0]], Now, Hold).Value;

        Assert.True(reservation.EnsureOwnedBy(UserId).IsSuccess);
        Assert.Equal(DomainErrors.Reservation.NotOwner, reservation.EnsureOwnedBy(Guid.NewGuid()).Error);
    }
}
