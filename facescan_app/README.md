# FaceScan check-in app

A desktop kiosk that recognizes a patient's face, finds their appointment today,
and records when they arrive and when they leave.

## How it works

1. **Enroll (staff, once per patient).** Click **Staff: enroll patient**, type the
   patient's account number, and have them look at the camera. The account must
   exist in the `people` table (`account` column). The face embedding is saved in a
   local file (`faces.db`), not in the clinic database. Enrolling a patient 2-3 times
   from slightly different angles improves recognition.
2. **Arrival.** The patient clicks **Scan my face**. FaceScan recognizes them, finds
   their row in `appointments` where `account` matches and `appt_date` is today, and
   sets `arrival_datetime` to the current time.
3. **Departure.** When leaving, the patient scans again. The visit that has an
   `arrival_datetime` but no `end_datetime` gets `end_datetime` set.

Other cases:

| Situation | What happens |
|---|---|
| Face not recognized | "Please see the front desk", nothing written |
| No appointment today | "No appointment found", nothing written |
| Scans again within 5 minutes of arriving | Treated as a double scan, not a checkout (configurable) |
| Visit already has an end time | "Visit already complete", nothing written |
| Several appointments today | Checks in to the one scheduled closest to now |

## Setup

```bash
pip install -r facescan_app/requirements.txt
pip install pyodbc          # or the driver for your database
cp facescan_app/config.example.ini config.ini
# edit config.ini: set the database URL
python -m facescan_app      # run from the repository root
```

The first start downloads the face recognition model (~95 MB) into `~/.deepface`.
Python's `tkinter` is required (included with the python.org installers for
Windows and macOS; on Linux install `python3-tk`).

## Database requirements

FaceScan reads the table structure from the database, so it works with SQL Server,
MySQL, PostgreSQL, SQLite or anything else SQLAlchemy supports. It needs:

- `people.account`
- `appointments.account`, `appointments.appt_date` (DATE or DATETIME),
  `appointments.arrival_datetime`, `appointments.end_datetime`

The database login needs `SELECT` on both tables and `UPDATE` on `appointments`.
If `appointments` has a primary key, updates target that exact row.

## Privacy

`faces.db` holds biometric data linked to patient account numbers. Keep it on an
encrypted disk, restrict access to it, and get patient consent before enrolling.
