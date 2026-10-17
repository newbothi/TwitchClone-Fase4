"""AutoMod con machine learning para el chat de TwitchClone (Fase 4).

Clasifica mensajes en: ninguna, insulto, acoso, odio, sexual, amenaza, spam.
Modelo: TF-IDF (n-gramas de caracteres y de palabras) + regresión logística (scikit-learn).
Aprende de los moderadores: cada vez que aprueban o rechazan un mensaje retenido,
la app de C# lo envía a /feedback y el modelo se reentrena.
"""

__version__ = "1.0.0"
