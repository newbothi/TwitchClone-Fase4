// Cada prueba arma sus propios objetos (no hay estado compartido), así que se
// pueden correr en paralelo por clase.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
