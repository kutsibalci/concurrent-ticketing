namespace SeatReservation.Domain.Common;

/// <summary>Every failure the domain can produce, in one place, so the API can map them to status codes exhaustively.</summary>
public static class DomainErrors
{
    public static class Event
    {
        public static readonly Error NotFound = new("event.not_found", "Etkinlik bulunamadı.");

        public static readonly Error AlreadyStarted = new(
            "event.already_started", "Etkinlik başladığı için yeni rezervasyon alınamaz.");

        public static readonly Error SalesClosed = new(
            "event.sales_closed", "Bu etkinlik için satışlar kapatıldı.");
    }

    public static class Seat
    {
        public static readonly Error NotFound = new("seat.not_found", "Koltuk bulunamadı.");

        public static readonly Error NotAvailable = new(
            "seat.not_available", "Koltuk şu anda müsait değil.");

        public static readonly Error NotHeld = new(
            "seat.not_held", "Koltuk beklemede değil.");

        public static readonly Error WrongEvent = new(
            "seat.wrong_event", "Koltuk bu etkinliğe ait değil.");
    }

    public static class Reservation
    {
        public static readonly Error NotFound = new("reservation.not_found", "Rezervasyon bulunamadı.");

        public static readonly Error NoSeats = new(
            "reservation.no_seats", "En az bir koltuk seçilmelidir.");

        public static readonly Error TooManySeats = new(
            "reservation.too_many_seats", "Tek seferde alınabilecek koltuk sayısı aşıldı.");

        public static readonly Error DuplicateSeats = new(
            "reservation.duplicate_seats", "Aynı koltuk birden fazla kez seçilemez.");

        public static readonly Error Expired = new(
            "reservation.expired", "Rezervasyon süresi doldu.");

        public static readonly Error NotPending = new(
            "reservation.not_pending", "Rezervasyon beklemede değil.");

        public static readonly Error AlreadyConfirmed = new(
            "reservation.already_confirmed", "Rezervasyon zaten onaylanmış.");

        public static readonly Error NotOwner = new(
            "reservation.not_owner", "Bu rezervasyon size ait değil.");

        /// <summary>Raised when another transaction won the race for the same seat.</summary>
        public static readonly Error ConcurrencyConflict = new(
            "reservation.concurrency_conflict",
            "Koltuk bu sırada başka bir kullanıcı tarafından alındı.");
    }

    public static class User
    {
        public static readonly Error EmailAlreadyUsed = new(
            "user.email_already_used", "Bu e-posta adresi zaten kayıtlı.");

        public static readonly Error InvalidCredentials = new(
            "user.invalid_credentials", "E-posta veya şifre hatalı.");

        public static readonly Error InvalidRefreshToken = new(
            "user.invalid_refresh_token", "Yenileme anahtarı geçersiz veya süresi dolmuş.");
    }
}
