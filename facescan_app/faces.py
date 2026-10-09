"""Enrolled face embeddings (stored locally) and matching a live face against them."""

import datetime as dt
import json
import sqlite3
from dataclasses import dataclass
from typing import Any, List, Optional, Tuple

import numpy as np


class FaceStore:
    """SQLite file mapping patient account numbers to face embeddings."""

    def __init__(self, path: str):
        self.conn = sqlite3.connect(path, check_same_thread=False)
        self.conn.execute(
            """CREATE TABLE IF NOT EXISTS face_embeddings (
                   id INTEGER PRIMARY KEY AUTOINCREMENT,
                   account TEXT NOT NULL,
                   model TEXT NOT NULL,
                   embedding TEXT NOT NULL,
                   created_at TEXT NOT NULL
               )"""
        )
        self.conn.commit()

    def add(self, account: str, model: str, embedding: List[float]) -> None:
        self.conn.execute(
            "INSERT INTO face_embeddings (account, model, embedding, created_at) VALUES (?, ?, ?, ?)",
            (str(account), model, json.dumps(list(map(float, embedding))),
             dt.datetime.now().isoformat(timespec="seconds")),
        )
        self.conn.commit()

    def remove(self, account: str) -> int:
        cur = self.conn.execute("DELETE FROM face_embeddings WHERE account = ?", (str(account),))
        self.conn.commit()
        return cur.rowcount

    def all_for_model(self, model: str) -> List[Tuple[str, np.ndarray]]:
        rows = self.conn.execute(
            "SELECT account, embedding FROM face_embeddings WHERE model = ?", (model,)
        ).fetchall()
        return [(account, np.array(json.loads(emb))) for account, emb in rows]


@dataclass
class Match:
    account: str
    distance: float


def cosine_distance(a: np.ndarray, b: np.ndarray) -> float:
    return float(1 - np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))


def best_match(
    embedding: np.ndarray, enrolled: List[Tuple[str, np.ndarray]], threshold: float
) -> Optional[Match]:
    """Closest enrolled face, or None if nobody is within the threshold."""
    best: Optional[Match] = None
    for account, stored in enrolled:
        distance = cosine_distance(embedding, stored)
        if best is None or distance < best.distance:
            best = Match(account, distance)
    if best is None or best.distance > threshold:
        return None
    return best


class NoFaceError(Exception):
    pass


class SpoofError(Exception):
    pass


class FaceRecognizer:
    def __init__(self, store: FaceStore, model: str, detector: str,
                 anti_spoofing: bool = False, threshold: Optional[float] = None):
        # imported here so the rest of the app (and its tests) load without tensorflow
        from deepface import DeepFace
        from deepface.modules import exceptions, verification

        self._deepface = DeepFace
        self._exceptions = exceptions
        self.store = store
        self.model = model
        self.detector = detector
        self.anti_spoofing = anti_spoofing
        self.threshold = threshold if threshold is not None else verification.find_threshold(model, "cosine")
        DeepFace.build_model(model)  # load weights up front so the first scan is not slow

    def embed(self, frame_bgr: np.ndarray) -> Tuple[np.ndarray, dict]:
        """Embedding of the largest (closest) face in the frame."""
        try:
            faces: List[Any] = self._deepface.represent(
                img_path=frame_bgr,
                model_name=self.model,
                detector_backend=self.detector,
                enforce_detection=True,
                anti_spoofing=self.anti_spoofing,
            )
        except self._exceptions.SpoofDetected as err:
            raise SpoofError(str(err)) from err
        except self._exceptions.FaceNotDetected as err:
            raise NoFaceError(str(err)) from err
        face = max(faces, key=lambda f: f["facial_area"]["w"] * f["facial_area"]["h"])
        return np.array(face["embedding"]), face["facial_area"]

    def enroll(self, account: str, frame_bgr: np.ndarray) -> None:
        embedding, _ = self.embed(frame_bgr)
        self.store.add(account, self.model, embedding.tolist())

    def identify(self, frame_bgr: np.ndarray) -> Optional[Match]:
        embedding, _ = self.embed(frame_bgr)
        return best_match(embedding, self.store.all_for_model(self.model), self.threshold)
