namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common
{
    // Reloj que solo avanza cuando la prueba lo pide. Así se prueba el modo lento,
    // los timeouts y el límite de mensajes sin esperar segundos reales.
    // (Equivale a FakeTimeProvider de Microsoft.Extensions.TimeProvider.Testing,
    // sin agregar ese paquete.)
    public sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now;

        public ManualTimeProvider()
            : this(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero))
        {
        }

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        // Zona horaria "local" del reloj. Por defecto UTC, para que las pruebas den lo
        // mismo en tu PC (Chile) que en GitHub Actions (UTC).
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override TimeZoneInfo LocalTimeZone => Zone;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta), "El tiempo no puede retroceder.");
            lock (_gate) _now += delta;
        }

        public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
    }
}
