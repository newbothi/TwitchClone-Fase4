"""Lectura de MongoDB (solo lectura): mensajes del chat y sesiones de stream.

Los nombres de campo son los de las clases de C# (PascalCase), porque la app no usa
convenciones de camelCase: ChatMessages.{ChannelId, UserId, SentAt, IsSimulated},
StreamSessions.{ChannelId, StartedAt, EndedAt, PeakViewers}.
"""

from __future__ import annotations

from datetime import datetime

from .transform import ChatEvent, Session


class MongoSource:
    def __init__(self, uri: str, database: str, timeout_ms: int = 3000):
        from pymongo import MongoClient  # importado aquí: las pruebas no necesitan Mongo
        self._client = MongoClient(uri, serverSelectionTimeoutMS=timeout_ms, tz_aware=True)
        self._db = self._client[database]

    def chat_between(self, start_utc: datetime, end_utc: datetime) -> list[ChatEvent]:
        cursor = self._db["ChatMessages"].find(
            {"SentAt": {"$gte": start_utc, "$lt": end_utc}},
            {"_id": 0, "ChannelId": 1, "UserId": 1, "SentAt": 1, "IsSimulated": 1},
        ).batch_size(5000)
        events = []
        for doc in cursor:
            channel_id = _int(doc.get("ChannelId"))
            if channel_id is None or not doc.get("UserId") or doc.get("SentAt") is None:
                continue
            events.append(ChatEvent(channel_id, str(doc["UserId"]), doc["SentAt"], bool(doc.get("IsSimulated", False))))
        return events

    def sessions_overlapping(self, start_utc: datetime, end_utc: datetime) -> list[Session]:
        cursor = self._db["StreamSessions"].find(
            {"StartedAt": {"$lt": end_utc}, "$or": [{"EndedAt": None}, {"EndedAt": {"$gt": start_utc}}]},
            {"_id": 0, "ChannelId": 1, "StartedAt": 1, "EndedAt": 1, "PeakViewers": 1},
        )
        sessions = []
        for doc in cursor:
            channel_id = _int(doc.get("ChannelId"))
            if channel_id is None or doc.get("StartedAt") is None:
                continue
            sessions.append(Session(channel_id, doc["StartedAt"], doc.get("EndedAt"), int(doc.get("PeakViewers") or 0)))
        return sessions

    def close(self) -> None:
        self._client.close()


def _int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
