namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Support
{
    // Para lo que ocurre "en algún momento" (eventos de SignalR, difusión cada 2 s):
    // reintenta cada 20 ms hasta que se cumpla o se acabe el tiempo. Nunca un Sleep fijo.
    public static class Eventually
    {
        public static async Task<T?> FindAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var value = probe();
                if (value != null) return value;
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(20);
            }
        }

        public static async Task TrueAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
        {
            var limit = timeout ?? HubClient.DefaultTimeout;
            var ok = await FindAsync(() => condition() ? "ok" : null, limit);
            if (ok == null) Assert.Fail($"No se cumplió en {limit.TotalSeconds:0.#} s: {because}");
        }
    }
}
