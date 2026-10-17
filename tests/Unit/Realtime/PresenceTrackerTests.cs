using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Realtime
{
    // Conteo de espectadores: pestañas, anónimos, cambio de canal y desconexiones.
    [TestClass]
    public class PresenceTrackerTests
    {
        private PresenceTracker _tracker = null!;

        [TestInitialize]
        public void Setup() => _tracker = new PresenceTracker();

        private void JoinUser(int channelId, string connectionId, string userId, string name) =>
            _tracker.Join(channelId, connectionId, PresenceTracker.UserKey(userId), name);

        private void JoinAnonymous(int channelId, string connectionId) =>
            _tracker.Join(channelId, connectionId, PresenceTracker.AnonymousKey(connectionId), null);

        [TestMethod]
        public void CanalSinNadie_TieneCeroEspectadores()
        {
            Assert.AreEqual(0, _tracker.GetCount(1));
            var list = _tracker.GetViewerList(1);
            Assert.AreEqual(0, list.Count);
            Assert.AreEqual(0, list.Names.Count);
        }

        [TestMethod]
        public void UnUsuarioConTresPestanas_CuentaComoUno()
        {
            JoinUser(1, "pestaña-1", "u1", "ViewerUno");
            JoinUser(1, "pestaña-2", "u1", "ViewerUno");
            JoinUser(1, "pestaña-3", "u1", "ViewerUno");

            Assert.AreEqual(1, _tracker.GetCount(1));
            CollectionAssert.AreEqual(new[] { "ViewerUno" }, _tracker.GetViewerList(1).Names);
        }

        [TestMethod]
        public void CerrarUnaPestana_NoLoSacaHastaCerrarLaUltima()
        {
            JoinUser(1, "pestaña-1", "u1", "ViewerUno");
            JoinUser(1, "pestaña-2", "u1", "ViewerUno");

            _tracker.Leave("pestaña-1");
            Assert.AreEqual(1, _tracker.GetCount(1));

            _tracker.Leave("pestaña-2");
            Assert.AreEqual(0, _tracker.GetCount(1));
        }

        [TestMethod]
        public void LosAnonimos_CuentanPorConexion()
        {
            JoinAnonymous(1, "anon-a");
            JoinAnonymous(1, "anon-b");
            JoinUser(1, "c1", "u1", "ViewerUno");

            var list = _tracker.GetViewerList(1);
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual(2, list.AnonymousCount);
            CollectionAssert.AreEqual(new[] { "ViewerUno" }, list.Names);
        }

        [TestMethod]
        public void CambiarDeCanal_ConLaMismaConexion_LoSacaDelAnterior()
        {
            JoinUser(1, "c1", "u1", "ViewerUno");

            var previous = _tracker.Join(2, "c1", PresenceTracker.UserKey("u1"), "ViewerUno");

            Assert.AreEqual(1, previous);
            Assert.AreEqual(0, _tracker.GetCount(1));
            Assert.AreEqual(1, _tracker.GetCount(2));
        }

        [TestMethod]
        public void VolverAEntrarAlMismoCanal_NoDevuelveCanalAnterior()
        {
            JoinUser(1, "c1", "u1", "ViewerUno");

            Assert.IsNull(_tracker.Join(1, "c1", PresenceTracker.UserKey("u1"), "ViewerUno"));
            Assert.AreEqual(1, _tracker.GetCount(1));
        }

        [TestMethod]
        public void SalirSinHaberEntrado_NoRompeNada()
        {
            Assert.IsNull(_tracker.Leave("desconocida"));
        }

        [TestMethod]
        public void LaListaDeNombres_VaOrdenadaSinImportarMayusculasYSeRecorta()
        {
            JoinUser(1, "c1", "u1", "zeta");
            JoinUser(1, "c2", "u2", "Alfa");
            JoinUser(1, "c3", "u3", "beta");

            CollectionAssert.AreEqual(new[] { "Alfa", "beta", "zeta" }, _tracker.GetViewerList(1).Names);

            var trimmed = _tracker.GetViewerList(1, maxNames: 2);
            CollectionAssert.AreEqual(new[] { "Alfa", "beta" }, trimmed.Names);
            Assert.AreEqual(3, trimmed.Count, "El total sigue contando a todos.");
        }

        [TestMethod]
        public void LosCanalesCambiados_SeEntreganUnaSolaVez()
        {
            JoinUser(1, "c1", "u1", "ViewerUno");
            JoinUser(2, "c2", "u2", "ViewerDos");

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, _tracker.DrainDirty().ToList());
            Assert.AreEqual(0, _tracker.DrainDirty().Count, "Si nada cambió, no hay nada que difundir.");

            _tracker.Leave("c1");
            CollectionAssert.AreEqual(new[] { 1 }, _tracker.DrainDirty().ToList(), "Salir también marca el canal (para difundir el 0).");
        }

        [TestMethod]
        public void Snapshot_SoloIncluyeCanalesConEspectadores()
        {
            JoinUser(1, "c1", "u1", "ViewerUno");
            JoinUser(1, "c2", "u1", "ViewerUno");
            JoinAnonymous(2, "anon");
            JoinUser(3, "c3", "u3", "ViewerTres");
            _tracker.Leave("c3");

            var counts = _tracker.SnapshotCounts();

            Assert.AreEqual(2, counts.Count);
            Assert.AreEqual(1, counts[1]);
            Assert.AreEqual(1, counts[2]);
        }

        [TestMethod]
        public void MuchasConexionesEnParalelo_ElConteoQuedaExacto()
        {
            Parallel.For(0, 2000, i =>
            {
                _tracker.Join(1, $"c{i}", PresenceTracker.UserKey($"u{i % 500}"), $"Usuario{i % 500}");
                if (i % 4 == 0) _tracker.Leave($"c{i}");
            });

            // El usuario k tiene las conexiones k, k+500, k+1000 y k+1500; como 500 es múltiplo
            // de 4, si k%4==0 se cerraron las cuatro. Quedan 3 de cada 4 usuarios.
            Assert.AreEqual(375, _tracker.GetCount(1));
            Assert.AreEqual(375, _tracker.GetViewerList(1, maxNames: 1000).Names.Count);
        }
    }
}
