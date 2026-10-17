import pytest

from automod_service.text import normalize


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("Hola A Todos", "hola a todos"),
        ("Qué PARTIDAZO, ñandú", "que partidazo, nandu"),          # sin tildes ni eñes
        ("1d10t4", "idiota"),                                       # leetspeak
        ("3stup1do", "estupido"),
        ("t@rado", "tarado"),                                       # @ dentro de palabra = "a"
        ("iiiidiotaaaa", "iidiotaa"),                               # letras estiradas
        ("e r e s  u n  i d i o t a", "eresunidiota"),              # letras separadas
        ("@ViewerUno hola", "usuario hola"),                        # menciones
        ("mira https://ejemplo.com/x ahora", "mira enlace ahora"),  # enlaces
        ("entra a www.algo.cl", "entra a enlace"),
        ("gané 1000000 puntos", "gane 1000000 puntos"),             # los números no se tocan
        ("   muchos     espacios   ", "muchos espacios"),
        ("", ""),
        (None, ""),
    ],
)
def test_normalize(raw, expected):
    assert normalize(raw) == expected


def test_un_numero_solo_no_se_trata_como_leetspeak():
    assert normalize("tengo 1337 puntos y 3 vidas") == "tengo 1337 puntos y 3 vidas"
