"""Normalización del texto antes de pasarlo al modelo.

La gente esquiva los filtros escribiendo "1d10t4", "iiiidiotaaa" o "i d i o t a".
Antes de vectorizar se lleva todo a una forma común, para que el modelo aprenda
"idiota" una sola vez y lo reconozca en todas sus variantes.
"""

from __future__ import annotations

import re
import unicodedata

_URL = re.compile(r"(https?://\S+|www\.\S+)", re.IGNORECASE)
_MENTION = re.compile(r"(?<!\w)@\w+")               # "@usuario", pero no "t@rado"
_REPEAT = re.compile(r"(\D)\1{2,}")                       # "aaaa" -> "aa" (los números no: "1000")
_SPACED = re.compile(r"\b(?:\w {1,2}){3,}\w\b")           # "i d i o t a" -> "idiota"
_SPACES = re.compile(r"\s+")
_LEET = str.maketrans({"0": "o", "1": "i", "3": "e", "4": "a", "5": "s", "7": "t", "@": "a", "$": "s"})


def _strip_accents(text: str) -> str:
    decomposed = unicodedata.normalize("NFKD", text)
    return "".join(c for c in decomposed if not unicodedata.combining(c))


def _deleet_token(token: str) -> str:
    # Solo se traduce si la palabra mezcla letras con números/símbolos ("1d10t4").
    # Un número solo ("1000") se deja tal cual.
    has_letter = any(c.isalpha() for c in token)
    has_leet = any(c in "013457@$" for c in token)
    return token.translate(_LEET) if has_letter and has_leet else token


def normalize(text: str | None) -> str:
    if not text:
        return ""
    text = _URL.sub(" enlace ", text)
    text = _MENTION.sub(" usuario ", text)
    text = _strip_accents(text.lower())
    text = " ".join(_deleet_token(t) for t in text.split())
    text = _SPACED.sub(lambda m: m.group(0).replace(" ", ""), text)
    text = _REPEAT.sub(r"\1\1", text)
    return _SPACES.sub(" ", text).strip()
