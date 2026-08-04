using SeatReservation.Domain.Common;
using SeatReservation.Domain.Entities;
using SeatReservation.Infrastructure.Security;

namespace SeatReservation.UnitTests;

public class SeatTests
{
    private static Seat NewSeat() => Seat.Create(Guid.NewGuid(), "a", 12, 150m);

    [Fact]
    public void Koltuk_musait_baslar()
    {
        Assert.Equal(SeatStatus.Available, NewSeat().Status);
    }

    [Fact]
    public void Sira_adi_normalize_edilir()
    {
        // "a" and "A" must not become two different rows.
        Assert.Equal("A", Seat.Create(Guid.NewGuid(), " a ", 1, 10m).Row);
    }

    [Fact]
    public void Etiket_sira_ve_numaradan_olusur()
    {
        Assert.Equal("A12", NewSeat().Label);
    }

    [Fact]
    public void Musait_koltuk_beklemeye_alinabilir()
    {
        var seat = NewSeat();
        var reservationId = Guid.NewGuid();

        Assert.True(seat.Hold(reservationId).IsSuccess);
        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(reservationId, seat.ReservationId);
    }

    [Fact]
    public void Beklemedeki_koltuk_tekrar_beklemeye_alinamaz()
    {
        var seat = NewSeat();
        seat.Hold(Guid.NewGuid());

        Assert.Equal(DomainErrors.Seat.NotAvailable, seat.Hold(Guid.NewGuid()).Error);
    }

    [Fact]
    public void Beklemede_olmayan_koltuk_satilamaz()
    {
        Assert.Equal(DomainErrors.Seat.NotHeld, NewSeat().Book().Error);
    }

    [Fact]
    public void Satilmis_koltuk_beklemeye_alinamaz()
    {
        var seat = NewSeat();
        seat.Hold(Guid.NewGuid());
        seat.Book();

        Assert.Equal(DomainErrors.Seat.NotAvailable, seat.Hold(Guid.NewGuid()).Error);
    }

    [Fact]
    public void Serbest_birakma_idempotenttir()
    {
        var seat = NewSeat();

        seat.Release();
        seat.Release();

        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Null(seat.ReservationId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Gecersiz_koltuk_numarasi_reddedilir(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Seat.Create(Guid.NewGuid(), "A", number, 10m));
    }

    [Fact]
    public void Negatif_fiyat_reddedilir()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Seat.Create(Guid.NewGuid(), "A", 1, -1m));
    }
}

public class EventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Koltuk_blogu_araligi_olusturur()
    {
        var @event = Event.Create("Maç", "Stadyum", Now.AddDays(2));

        var seats = @event.AddSeatBlock("B", 5, 9, 300m);

        Assert.Equal(5, seats.Count);
        Assert.Equal([5, 6, 7, 8, 9], seats.Select(s => s.Number));
        Assert.All(seats, s => Assert.Equal(300m, s.Price));
    }

    [Fact]
    public void Ters_aralik_reddedilir()
    {
        var @event = Event.Create("Maç", "Stadyum", Now.AddDays(2));

        Assert.Throws<ArgumentOutOfRangeException>(() => @event.AddSeatBlock("B", 9, 5, 300m));
    }

    [Fact]
    public void Baslamamis_ve_satisi_acik_etkinlik_rezerve_edilebilir()
    {
        var @event = Event.Create("Maç", "Stadyum", Now.AddHours(1));

        Assert.True(@event.EnsureBookable(Now).IsSuccess);
    }
}

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new(iterations: 1_000);

    [Fact]
    public void Dogru_sifre_dogrulanir()
    {
        var hash = _hasher.Hash("CokGizliSifre123!");

        Assert.True(_hasher.Verify("CokGizliSifre123!", hash));
    }

    [Fact]
    public void Yanlis_sifre_reddedilir()
    {
        var hash = _hasher.Hash("CokGizliSifre123!");

        Assert.False(_hasher.Verify("cokgizlisifre123!", hash));
        Assert.False(_hasher.Verify("", hash));
    }

    [Fact]
    public void Ayni_sifre_farkli_hash_uretir()
    {
        // Different salts per password: identical passwords must not be visible as
        // identical rows in the users table.
        Assert.NotEqual(_hasher.Hash("ayni"), _hasher.Hash("ayni"));
    }

    [Fact]
    public void Iterasyon_sayisi_hashe_gomulur_ve_ileri_uyumludur()
    {
        var hash = new Pbkdf2PasswordHasher(iterations: 4_321).Hash("x");

        Assert.Contains("$4321$", hash);
        Assert.True(new Pbkdf2PasswordHasher(iterations: 99_999).Verify("x", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bozuk")]
    [InlineData("pbkdf2-sha256$abc$def$ghi")]
    [InlineData("pbkdf2-sha256$1000$!!!$xxx")]
    public void Bozuk_hash_istisna_firlatmaz(string stored)
    {
        Assert.False(_hasher.Verify("herhangi", stored));
    }
}
