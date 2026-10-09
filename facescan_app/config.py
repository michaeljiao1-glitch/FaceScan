import configparser
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from facescan_app import sqlserver


@dataclass
class Config:
    db_url: str
    database: Optional[str]
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


def load_config(path: str = "config.ini", database: Optional[str] = None) -> Config:
    """Read config.ini. `database` overrides the clinic database (Initial Catalog)."""
    if not Path(path).exists():
        raise FileNotFoundError(
            f"{path} not found. Copy facescan_app/config.example.ini to {path} and edit it."
        )
    # interpolation off so passwords may contain '%'
    parser = configparser.ConfigParser(interpolation=None)
    parser.read(path)

    database = database or parser.get("database", "database", fallback="").strip() or None
    connection_string = parser.get("database", "connection_string", fallback="").strip()
    if connection_string:
        driver = parser.get("database", "odbc_driver", fallback="").strip() or sqlserver.DEFAULT_DRIVER
        db_url = sqlserver.to_sqlalchemy_url(connection_string, database, driver)
        database = sqlserver.database_name(connection_string, database)
    else:
        db_url = parser.get("database", "url")

    threshold = parser.get("faces", "threshold", fallback="").strip()
    store_path = parser.get("faces", "store_path", fallback="faces_{database}.db")
    return Config(
        db_url=db_url,
        database=database,
        db_schema=parser.get("database", "schema", fallback="").strip() or None,
        people_table=parser.get("database", "people_table", fallback="people"),
        appointments_table=parser.get("database", "appointments_table", fallback="appointments"),
        # each clinic gets its own face file, so account numbers from different clinics never mix
        store_path=store_path.replace("{database}", database or "default"),
        model=parser.get("faces", "model", fallback="Facenet512"),
        detector=parser.get("faces", "detector", fallback="opencv"),
        anti_spoofing=parser.getboolean("faces", "anti_spoofing", fallback=False),
        threshold=float(threshold) if threshold else None,
        min_minutes_before_checkout=parser.getfloat(
            "visits", "min_minutes_before_checkout", fallback=5
        ),
        camera_index=parser.getint("camera", "index", fallback=0),
    )
