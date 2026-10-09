"""Reads patients and appointments from the clinic database and records arrival/departure."""

import datetime as dt
from dataclasses import dataclass
from enum import Enum
from typing import Any, Optional

from sqlalchemy import MetaData, Table, and_, create_engine, literal_column, select, update
from sqlalchemy.engine import Engine


class ScanOutcome(Enum):
    CHECKED_IN = "checked_in"
    CHECKED_OUT = "checked_out"
    ALREADY_CHECKED_IN = "already_checked_in"
    VISIT_COMPLETE = "visit_complete"
    NO_APPOINTMENT = "no_appointment"


@dataclass
class ScanResult:
    outcome: ScanOutcome
    account: Any
    appt_date: Any = None
    timestamp: Optional[dt.datetime] = None


class ClinicDatabase:
    def __init__(
        self,
        url: str,
        schema: Optional[str] = None,
        people_table: str = "people",
        appointments_table: str = "appointments",
        min_minutes_before_checkout: float = 5,
        engine: Optional[Engine] = None,
    ):
        self.engine = engine or create_engine(url)
        metadata = MetaData(schema=schema)
        # reflect the real tables so values are bound with the database's own column types
        self.people = Table(people_table, metadata, autoload_with=self.engine)
        self.appointments = Table(appointments_table, metadata, autoload_with=self.engine)
        self.min_checkout_gap = dt.timedelta(minutes=min_minutes_before_checkout)
        self.row_key = self._row_key_columns()

    def _row_key_columns(self) -> list:
        """Columns that pin down one appointment row, so an update touches only that visit."""
        if len(self.appointments.primary_key.columns) > 0:
            return list(self.appointments.primary_key.columns)
        if self.engine.dialect.name == "sqlite":
            return [literal_column("rowid")]
        # no primary key: fall back to the values that identify a visit
        return [self.appointments.c.account, self.appointments.c.appt_date]

    def _key_labels(self) -> list:
        return [column.label(f"_key{i}") for i, column in enumerate(self.row_key)]

    def _where_row(self, row: Any) -> Any:
        return and_(*[column == row._mapping[f"_key{i}"] for i, column in enumerate(self.row_key)])

    def _coerce_account(self, account: Any) -> Any:
        """Convert a typed-in account number to the people.account column's python type."""
        try:
            python_type = self.people.c.account.type.python_type
        except NotImplementedError:
            return account
        if python_type is int:
            return int(str(account).strip())
        if python_type is str:
            return str(account).strip()
        return account

    def account_exists(self, account: Any) -> bool:
        try:
            account = self._coerce_account(account)
        except ValueError:
            return False
        with self.engine.connect() as conn:
            row = conn.execute(
                select(self.people.c.account).where(self.people.c.account == account).limit(1)
            ).first()
        return row is not None

    def _day_bounds(self, now: dt.datetime) -> tuple:
        """Start of today and start of tomorrow, typed to match appt_date (DATE or DATETIME)."""
        today = now.date()
        tomorrow = today + dt.timedelta(days=1)
        try:
            python_type = self.appointments.c.appt_date.type.python_type
        except NotImplementedError:
            python_type = dt.datetime
        if python_type is dt.date:
            return today, tomorrow
        return (
            dt.datetime.combine(today, dt.time.min),
            dt.datetime.combine(tomorrow, dt.time.min),
        )

    def record_scan(self, account: Any, now: Optional[dt.datetime] = None) -> ScanResult:
        """
        Apply a face scan to the patient's appointment today:
          - a visit that is in progress (arrived, not ended) gets its end_datetime set
          - otherwise the next visit not yet arrived gets its arrival_datetime set
        """
        now = (now or dt.datetime.now()).replace(microsecond=0)
        account = self._coerce_account(account)
        appts = self.appointments.c
        start, end = self._day_bounds(now)

        with self.engine.begin() as conn:
            rows = conn.execute(
                select(appts.appt_date, appts.arrival_datetime, appts.end_datetime, *self._key_labels()).where(
                    and_(
                        appts.account == account,
                        appts.appt_date >= start,
                        appts.appt_date < end,
                    )
                )
            ).all()

            if not rows:
                return ScanResult(ScanOutcome.NO_APPOINTMENT, account)

            in_progress = [r for r in rows if r.arrival_datetime is not None and r.end_datetime is None]
            if in_progress:
                visit = max(in_progress, key=lambda r: _as_datetime(r.arrival_datetime))
                if now - _as_datetime(visit.arrival_datetime) < self.min_checkout_gap:
                    return ScanResult(
                        ScanOutcome.ALREADY_CHECKED_IN, account, visit.appt_date,
                        _as_datetime(visit.arrival_datetime),
                    )
                conn.execute(
                    update(self.appointments)
                    .where(and_(self._where_row(visit), appts.end_datetime.is_(None)))
                    .values(end_datetime=now)
                )
                return ScanResult(ScanOutcome.CHECKED_OUT, account, visit.appt_date, now)

            waiting = [r for r in rows if r.arrival_datetime is None]
            if waiting:
                # several appointments today: take the one scheduled closest to now
                visit = min(waiting, key=lambda r: abs(_as_datetime(r.appt_date) - now))
                conn.execute(
                    update(self.appointments)
                    .where(and_(self._where_row(visit), appts.arrival_datetime.is_(None)))
                    .values(arrival_datetime=now)
                )
                return ScanResult(ScanOutcome.CHECKED_IN, account, visit.appt_date, now)

            return ScanResult(ScanOutcome.VISIT_COMPLETE, account)


def _as_datetime(value: Any) -> dt.datetime:
    if isinstance(value, dt.datetime):
        return value
    if isinstance(value, dt.date):
        return dt.datetime.combine(value, dt.time.min)
    return dt.datetime.fromisoformat(str(value))
