# FaceScan check-in app

A desktop kiosk that recognizes a patient's face, finds their appointment today,
and records when they arrive and when they leave.

## How it works

1. **Enroll (staff, once per patient).** Click **Staff: enroll patient**, type the
   patient's account number, and have them look at the camera. The account must
   exist in the `people` table (`account` column). The face embedding is saved in a
   local file (`faces_<clinic database>.db`), not in the clinic database. Enrolling a patient 2-3 times
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

## Setup (Windows PC with a webcam)

1. Install Python 3.10+ from python.org (tick "Add Python to PATH").
2. Install **Microsoft ODBC Driver 18 for SQL Server** from Microsoft's website.
3. In the repository folder:
   ```bash
   pip install -r facescan_app/requirements.txt
   copy facescan_app\config.example.ini config.ini
   ```
4. Edit `config.ini`: paste the SQL Server connection string and set `database`
   to the clinic's database name.
5. Run `python -m facescan_app`.

The first start downloads the face recognition model (~95 MB) into `~/.deepface`.

## Multiple clinics

The connection string is shared; only the database (Initial Catalog) changes.
Either set `database = cm_clinicname` in each clinic's `config.ini`, or keep one
`config.ini` and start the app with:

```bash
python -m facescan_app --database cm_clinicname
```

Enrolled faces are stored per clinic (`faces_cm_clinicname.db`), so the same account
number at two clinics never gets mixed up. The window title shows which clinic
database the kiosk is connected to.

## Database requirements

FaceScan reads the table structure from the database (tested on SQL Server 2022;
other databases work via a SQLAlchemy `url`). It needs:

- `people.account`
- `appointments.account`, `appointments.appt_date` (DATE or DATETIME),
  `appointments.arrival_datetime`, `appointments.end_datetime`

The database login needs only `SELECT` on both tables and `UPDATE` on `appointments`;
a dedicated login with just those rights is recommended for the kiosk.
If `appointments` has a primary key, updates target that exact row.

## Privacy

The `faces_*.db` files hold biometric data linked to patient account numbers. Keep them on an
encrypted disk, restrict access to them, and get patient consent before enrolling.
