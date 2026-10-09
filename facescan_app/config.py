import configparser
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


@dataclass
class Config:
    db_url: str
    db_schema: Optional[str]
    people_table: str
    appointments_table: str
    store_path: str
    model: str
    detector: str
    anti_spoofing: bool
    threshold: Optional[float]
    min_minutes_before_checkout: float
    camera_index: int


def load_config(path: str = "config.ini") -> Config:
    if not Path(path).exists():
        raise FileNotFoundError(
            f"{path} not found. Copy facescan_app/config.example.ini to {path} and edit it."
        )
    parser = configparser.ConfigParser()
    parser.read(path)
    threshold = parser.get("faces", "threshold", fallback="").strip()
    return Config(
        db_url=parser.get("database", "url"),
        db_schema=parser.get("database", "schema", fallback="").strip() or None,
        people_table=parser.get("database", "people_table", fallback="people"),
        appointments_table=parser.get("database", "appointments_table", fallback="appointments"),
        store_path=parser.get("faces", "store_path", fallback="faces.db"),
        model=parser.get("faces", "model", fallback="Facenet512"),
        detector=parser.get("faces", "detector", fallback="opencv"),
        anti_spoofing=parser.getboolean("faces", "anti_spoofing", fallback=False),
        threshold=float(threshold) if threshold else None,
        min_minutes_before_checkout=parser.getfloat(
            "visits", "min_minutes_before_checkout", fallback=5
        ),
        camera_index=parser.getint("camera", "index", fallback=0),
    )
