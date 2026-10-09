import datetime as dt

import pytest
from sqlalchemy import create_engine, text

from facescan_app.clinic_db import ClinicDatabase, ScanOutcome

NOW = dt.datetime(2026, 10, 9, 9, 5, 0)


def make_db(appt_type: str, appointments, id_column: str = ""):
    engine = create_engine("sqlite://")
    with engine.begin() as conn:
        conn.execute(text("CREATE TABLE people (account INTEGER PRIMARY KEY)"))
        conn.execute(text(
            f"CREATE TABLE appointments ({id_column}account INTEGER, appt_date {appt_type}, "
            "arrival_datetime DATETIME, end_datetime DATETIME)"
        ))
        conn.execute(text("INSERT INTO people (account) VALUES (1001), (1002)"))
        for account, appt_date in appointments:
            conn.execute(
                text("INSERT INTO appointments (account, appt_date) VALUES (:a, :d)"),
                {"a": account, "d": appt_date},
            )
    return ClinicDatabase("sqlite://", engine=engine, min_minutes_before_checkout=5), engine


def visits(engine):
    with engine.connect() as conn:
        return conn.execute(text(
            "SELECT account, appt_date, arrival_datetime, end_datetime FROM appointments ORDER BY appt_date"
        )).all()


@pytest.mark.parametrize("appt_type,appt_value,id_column", [
    ("DATETIME", "2026-10-09 09:00:00.000000", ""),
    # stored in a different text format than SQLAlchemy writes it
    ("DATETIME", "2026-10-09 09:00:00", ""),
    ("DATETIME", "2026-10-09 09:00:00", "id INTEGER PRIMARY KEY, "),
    ("DATE", "2026-10-09", ""),
])
def test_arrival_then_departure(appt_type, appt_value, id_column):
    db, engine = make_db(appt_type, [(1001, appt_value)], id_column)

    first = db.record_scan("1001", NOW)
    assert first.outcome is ScanOutcome.CHECKED_IN
    assert visits(engine)[0].arrival_datetime is not None
    assert visits(engine)[0].end_datetime is None

    # immediate second scan does not check the patient out
    assert db.record_scan("1001", NOW + dt.timedelta(minutes=1)).outcome is ScanOutcome.ALREADY_CHECKED_IN

    leave = NOW + dt.timedelta(minutes=45)
    assert db.record_scan("1001", leave).outcome is ScanOutcome.CHECKED_OUT
    row = visits(engine)[0]
    assert row.end_datetime.startswith("2026-10-09 09:50:00")
    assert row.arrival_datetime.startswith("2026-10-09 09:05:00")

    assert db.record_scan("1001", leave + dt.timedelta(minutes=10)).outcome is ScanOutcome.VISIT_COMPLETE


def test_no_appointment_today():
    db, engine = make_db("DATETIME", [(1001, "2026-10-10 09:00:00.000000"), (1002, "2026-10-09 10:00:00.000000")])
    assert db.record_scan("1001", NOW).outcome is ScanOutcome.NO_APPOINTMENT
    assert all(r.arrival_datetime is None for r in visits(engine))


def test_picks_appointment_closest_to_now():
    db, engine = make_db("DATETIME", [
        (1001, "2026-10-09 08:00:00.000000"),
        (1001, "2026-10-09 15:00:00.000000"),
    ])
    db.record_scan("1001", dt.datetime(2026, 10, 9, 14, 40))
    morning, afternoon = visits(engine)
    assert morning.arrival_datetime is None
    assert afternoon.arrival_datetime is not None


def test_only_the_scanned_patients_visit_changes():
    db, engine = make_db("DATETIME", [(1001, "2026-10-09 09:00:00"), (1002, "2026-10-09 09:00:00")])
    db.record_scan("1001", NOW)
    rows = {r.account: r for r in visits(engine)}
    assert rows[1001].arrival_datetime is not None
    assert rows[1002].arrival_datetime is None


def test_account_exists():
    db, _ = make_db("DATETIME", [])
    assert db.account_exists("1001")
    assert db.account_exists(" 1002 ")
    assert not db.account_exists("9999")
    assert not db.account_exists("abc")
