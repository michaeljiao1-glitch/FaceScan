"""Desktop kiosk: live camera, a Scan button for patients, and an Enroll button for staff."""

import argparse
import threading
import tkinter as tk
from tkinter import messagebox, simpledialog
from typing import Callable, Optional

import cv2
from PIL import Image, ImageTk

from facescan_app.clinic_db import ClinicDatabase, ScanOutcome, ScanResult
from facescan_app.config import Config, load_config
from facescan_app.faces import FaceRecognizer, FaceStore, NoFaceError, SpoofError

STATUS_RESET_MS = 7000
FRAME_INTERVAL_MS = 30

COLORS = {"ok": "#1b7f3b", "warn": "#b36b00", "error": "#b3261e", "info": "#333333"}


def describe(result: ScanResult) -> tuple:
    when = result.timestamp.strftime("%I:%M %p").lstrip("0") if result.timestamp else ""
    if result.outcome is ScanOutcome.CHECKED_IN:
        return f"Welcome! You are checked in at {when}.", "ok"
    if result.outcome is ScanOutcome.CHECKED_OUT:
        return f"Thank you! You are checked out at {when}.", "ok"
    if result.outcome is ScanOutcome.ALREADY_CHECKED_IN:
        return f"You are already checked in ({when}). Scan again when you leave.", "info"
    if result.outcome is ScanOutcome.VISIT_COMPLETE:
        return "Your visit today is already complete.", "info"
    return "No appointment found for you today. Please see the front desk.", "warn"


class FaceScanApp:
    def __init__(self, root: tk.Tk, config: Config):
        self.root = root
        self.config = config
        self.busy = False
        self.frame = None
        self._reset_job: Optional[str] = None

        root.title(f"FaceScan Check-In - {config.database}" if config.database else "FaceScan Check-In")
        root.configure(bg="white")

        self.video = tk.Label(root, bg="black")
        self.video.pack(padx=16, pady=(16, 8))

        self.status = tk.Label(root, text="Starting up...", font=("Helvetica", 20),
                               bg="white", wraplength=640, height=2)
        self.status.pack(padx=16, pady=8)

        buttons = tk.Frame(root, bg="white")
        buttons.pack(pady=(0, 16))
        self.scan_button = tk.Button(buttons, text="Scan my face", font=("Helvetica", 22, "bold"),
                                     width=16, command=self.on_scan, state=tk.DISABLED)
        self.scan_button.pack(side=tk.LEFT, padx=8)
        self.enroll_button = tk.Button(buttons, text="Staff: enroll patient", font=("Helvetica", 12),
                                       command=self.on_enroll, state=tk.DISABLED)
        self.enroll_button.pack(side=tk.LEFT, padx=8)

        self.camera = cv2.VideoCapture(config.camera_index)
        if not self.camera.isOpened():
            messagebox.showerror("FaceScan", f"Could not open camera {config.camera_index}.")
            root.destroy()
            return
        root.protocol("WM_DELETE_WINDOW", self.close)
        self.update_video()

        # connecting to the database and loading the model can take a while
        self.run_in_background(self.load, self.loaded)

    # -- startup ---------------------------------------------------------------------------

    def load(self):
        cfg = self.config
        db = ClinicDatabase(cfg.db_url, cfg.db_schema, cfg.people_table,
                            cfg.appointments_table, cfg.min_minutes_before_checkout)
        recognizer = FaceRecognizer(FaceStore(cfg.store_path), cfg.model, cfg.detector,
                                    cfg.anti_spoofing, cfg.threshold)
        return db, recognizer

    def loaded(self, result):
        self.db, self.recognizer = result
        self.scan_button.config(state=tk.NORMAL)
        self.enroll_button.config(state=tk.NORMAL)
        self.show_idle()

    # -- camera ----------------------------------------------------------------------------

    def update_video(self):
        ok, frame = self.camera.read()
        if ok:
            self.frame = frame
            rgb = cv2.cvtColor(cv2.flip(frame, 1), cv2.COLOR_BGR2RGB)  # mirror for the user
            image = Image.fromarray(rgb)
            image.thumbnail((640, 480))
            photo = ImageTk.PhotoImage(image)
            self.video.config(image=photo)
            self.video.image = photo
        self.root.after(FRAME_INTERVAL_MS, self.update_video)

    def current_frame(self):
        return None if self.frame is None else self.frame.copy()

    # -- actions ---------------------------------------------------------------------------

    def on_scan(self):
        frame = self.current_frame()
        if self.busy or frame is None:
            return
        self.show("Scanning... please hold still.", "info", sticky=True)

        def work():
            match = self.recognizer.identify(frame)
            if match is None:
                return None
            return self.db.record_scan(match.account)

        def done(result: Optional[ScanResult]):
            if result is None:
                self.show("Face not recognized. Please see the front desk.", "warn")
            else:
                self.show(*describe(result))

        self.run_in_background(work, done)

    def on_enroll(self):
        if self.busy:
            return
        account = simpledialog.askstring(
            "Enroll patient",
            "Patient account number.\nHave the patient look at the camera, then press OK.",
            parent=self.root,
        )
        if not account or not account.strip():
            return
        account = account.strip()
        frame = self.current_frame()
        if frame is None:
            return
        self.show("Enrolling...", "info", sticky=True)

        def work():
            if not self.db.account_exists(account):
                return f"Account {account} was not found in the {self.config.people_table} table."
            self.recognizer.enroll(account, frame)
            return None

        def done(error: Optional[str]):
            if error:
                self.show(error, "error")
            else:
                self.show(f"Enrolled account {account}. Enroll again from another angle "
                          "to improve recognition.", "ok")

        self.run_in_background(work, done)

    # -- helpers ---------------------------------------------------------------------------

    def run_in_background(self, work: Callable, done: Callable):
        """Run slow work (model, database) off the UI thread, then call done() on the UI thread."""
        self.busy = True
        self.scan_button.config(state=tk.DISABLED)

        def target():
            try:
                result = work()
                callback = lambda: done(result)
            except NoFaceError:
                callback = lambda: self.show("No face detected. Please look at the camera.", "warn")
            except SpoofError:
                callback = lambda: self.show("Please scan your real face, not a photo.", "error")
            except Exception as err:  # pylint: disable=broad-except
                message = f"Error: {err}"
                callback = lambda: self.show(message, "error")
            self.root.after(0, lambda: self.finish(callback))

        threading.Thread(target=target, daemon=True).start()

    def finish(self, callback: Callable):
        self.busy = False
        if hasattr(self, "recognizer"):
            self.scan_button.config(state=tk.NORMAL)
        callback()

    def show(self, text: str, level: str, sticky: bool = False):
        """Show a status message; it returns to the idle prompt after a few seconds unless sticky."""
        self.status.config(text=text, fg=COLORS[level])
        if self._reset_job:
            self.root.after_cancel(self._reset_job)
            self._reset_job = None
        # before startup finishes there is no idle prompt to return to
        if not sticky and hasattr(self, "recognizer"):
            self._reset_job = self.root.after(STATUS_RESET_MS, self.show_idle)

    def show_idle(self):
        self._reset_job = None
        self.status.config(text="Look at the camera and press Scan to check in or out.",
                           fg=COLORS["info"])

    def close(self):
        self.camera.release()
        self.root.destroy()


def main():
    parser = argparse.ArgumentParser(description="FaceScan patient check-in kiosk")
    parser.add_argument("--config", default="config.ini", help="path to config.ini")
    parser.add_argument("--database", help="clinic database (Initial Catalog), overrides config.ini")
    args = parser.parse_args()

    root = tk.Tk()
    try:
        config = load_config(args.config, args.database)
    except (FileNotFoundError, ValueError) as err:
        root.withdraw()
        messagebox.showerror("FaceScan", str(err))
        return
    FaceScanApp(root, config)
    root.mainloop()


if __name__ == "__main__":
    main()
