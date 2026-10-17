// Cada prueba levanta su propio servidor en un puerto libre, pero los tiempos de
// SignalR (difusión cada 2 s) son más predecibles si no compiten entre sí.
[assembly: DoNotParallelize]
