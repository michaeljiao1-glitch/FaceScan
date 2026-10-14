# FaceScan check-in kiosk (.NET Framework 4.5.2)

A Windows desktop app that recognizes a patient's face, finds their appointment today in the
clinic's SQL Server database, and records when they arrive and when they leave.

## How it works

1. **Enroll (staff, once per patient).** Click **Staff: enroll patient**, type the patient's
   account number and have them look at the camera. The account must exist in `people.account`.
   Enrolling 2-3 times from slightly different angles improves recognition.
2. **Arrival.** The patient clicks **Scan my face**. FaceScan recognizes them, finds their row in
   `appointments` where `account` matches and `appt_date` is today, and sets `arrival_datetime`.
3. **Departure.** When leaving, the patient scans again; the visit with an arrival but no end
   gets `end_datetime` set.

| Situation | What happens |
|---|---|
| Face not recognized, or two patients look too alike to call | "Please see the front desk", nothing written |
| No appointment today | "No appointment found", nothing written |
| Scans again within 5 minutes of arriving | Treated as a double scan, not a checkout |
| Visit already has an end time | "Visit already complete", nothing written |
| Several appointments today | Checks in to the one scheduled closest to now |

Face recognition runs entirely on the PC (no internet, no cloud service): OpenCV's SSD face
detector finds the face, the eyes are used to align it, and the SFace model turns it into 128
numbers that are compared with the enrolled patients.

## Solution layout

| Path | What it is |
|---|---|
| `FaceScan.sln` | Visual Studio solution |
| `FaceScan/` | The app (WinForms, `FaceScan.exe`) |
| `FaceScan/App.config` | Connection string and settings (becomes `FaceScan.exe.config`) |
| `FaceScan/models/` | Face models, copied next to the exe on build |
| `FaceScan.Tests/` | NUnit tests |

NuGet packages: `OpenCvSharp3-AnyCPU` 4.0.0.20181129 (the last OpenCV wrapper that supports
.NET 4.5.2), `NUnit`, `NUnit3TestAdapter`.

## Building

1. Open `FaceScan.sln` in Visual Studio 2015 or later.
2. Build > Build Solution. Visual Studio restores the NuGet packages on the first build.
3. The app is in `FaceScan\bin\Release\` (or `bin\Debug\`). Deploy that whole folder; it
   contains the exe, the OpenCvSharp DLLs, `dll\x64` + `dll\x86` (native OpenCV) and `models\`.

## Installing on a kiosk PC

1. **Windows 10 or 11** (they include .NET Framework 4.8, which runs 4.5.2 apps). On a Windows
   "N" edition, also install the Media Feature Pack (OpenCV needs it for the camera). No Visual C++
   runtime or Python is needed.
2. Copy the built folder (e.g. `bin\Release`) to `C:\FaceScan`.
3. Edit `C:\FaceScan\FaceScan.exe.config`: put the clinic's connection string in
   `connectionStrings` > `Clinic` (Initial Catalog = the clinic's database).
4. Allow camera access: Settings > Privacy & security > Camera > "Let desktop apps access your camera".
5. Run `FaceScan.exe`. The window title shows which clinic database it is connected to.

### Multiple clinics

Only the database (Initial Catalog) changes per clinic. Either edit it in each PC's
`FaceScan.exe.config` (connection string, or the `Database` setting), or keep one config and
start the app with `FaceScan.exe /database:cm_clinicname` (e.g. in a desktop shortcut).
Enrolled faces are stored per clinic (`faces_<database>.dat`), so the same account number at
two clinics never gets mixed up.

## Settings (`FaceScan.exe.config`)

| Setting | Default | Meaning |
|---|---|---|
| `Database` | (empty) | Overrides Initial Catalog |
| `Schema`, `PeopleTable`, `AppointmentsTable` | `dbo`, `people`, `appointments` | Table names |
| `FaceDataFolder` | `%ProgramData%\FaceScan` | Where enrolled faces are stored |
| `MatchThreshold` | `0.40` | Similarity needed to accept a match (higher = stricter) |
| `MatchMargin` | `0.05` | How far ahead of the next patient the best match must be |
| `MinMinutesBeforeCheckout` | `5` | Minimum minutes between arrival and checkout scans |
| `CameraIndex` | `0` | Which camera to use |

## Database requirements

SQL Server. FaceScan needs `people.account` and `appointments.account`, `appt_date` (DATE or
DATETIME), `arrival_datetime`, `end_datetime`; it checks they exist at startup. The login only
needs `SELECT` on both tables and `UPDATE` on `appointments` - a dedicated kiosk login with just
those rights is recommended.

## Tests

Test Explorer runs them. `ClinicDatabaseTests` run against a real SQL Server when the environment
variable `FACESCAN_TEST_SQL` holds a connection string for a login that can create databases
(each test creates and drops a scratch database); otherwise they are skipped. `FaceEngineTests`
use DeepFace's sample photos in `..\tests\unit\dataset`.

## Privacy

`faces_*.dat` files hold biometric data linked to patient account numbers. Keep them on an
encrypted disk (BitLocker), restrict access to `%ProgramData%\FaceScan`, back them up securely,
and get patient consent before enrolling. FaceScan cannot tell a real face from a photo of one.
