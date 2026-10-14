using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace FaceScan
{
    /// <summary>Kiosk window: live camera, a Scan button for patients and an Enroll button for staff.</summary>
    public sealed class MainForm : Form
    {
        private const int StatusResetMilliseconds = 7000;

        private static readonly Color Ok = Color.FromArgb(27, 127, 59);
        private static readonly Color Warn = Color.FromArgb(179, 107, 0);
        private static readonly Color Error = Color.FromArgb(179, 38, 30);
        private static readonly Color Info = Color.FromArgb(51, 51, 51);

        private readonly AppSettings settings;
        private readonly PictureBox preview;
        private readonly Label status;
        private readonly Button scanButton;
        private readonly Button enrollButton;
        private readonly System.Windows.Forms.Timer statusTimer;
        private readonly object frameGate = new object();

        private ClinicDatabase database;
        private FaceEngine engine;
        private FaceStore store;
        private VideoCapture camera;
        private Thread cameraThread;
        private volatile bool closing;
        private Mat latestFrame;
        private bool busy;

        public MainForm(AppSettings settings)
        {
            this.settings = settings;

            Text = "FaceScan Check-In - " + settings.DatabaseName;
            BackColor = Color.White;
            ClientSize = new System.Drawing.Size(720, 680);
            MinimumSize = new System.Drawing.Size(480, 480);
            StartPosition = FormStartPosition.CenterScreen;

            preview = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
            };
            status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 90,
                Font = new Font("Segoe UI", 18f),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Starting up...",
                ForeColor = Info,
            };
            scanButton = new Button
            {
                Text = "Scan my face",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                Size = new System.Drawing.Size(320, 70),
                Enabled = false,
            };
            enrollButton = new Button
            {
                Text = "Staff: enroll patient",
                Font = new Font("Segoe UI", 10f),
                Size = new System.Drawing.Size(170, 40),
                Enabled = false,
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 90,
                Padding = new Padding(16, 10, 16, 10),
                FlowDirection = FlowDirection.LeftToRight,
            };
            enrollButton.Margin = new Padding(24, 25, 0, 0);
            buttons.Controls.Add(scanButton);
            buttons.Controls.Add(enrollButton);

            Controls.Add(preview);
            Controls.Add(status);
            Controls.Add(buttons);

            statusTimer = new System.Windows.Forms.Timer { Interval = StatusResetMilliseconds };
            statusTimer.Tick += (s, e) => ShowIdle();

            scanButton.Click += OnScan;
            enrollButton.Click += OnEnroll;
            Shown += OnShown;
            FormClosing += OnClosing;
        }

        private async void OnShown(object sender, EventArgs e)
        {
            camera = new VideoCapture(settings.CameraIndex);
            if (!camera.IsOpened())
            {
                ShowError("Could not open camera " + settings.CameraIndex + ".", sticky: true);
                return;
            }
            cameraThread = new Thread(CameraLoop) { IsBackground = true, Name = "Camera" };
            cameraThread.Start();

            try
            {
                // connecting to the database and loading the models takes a few seconds
                await Task.Run(() =>
                {
                    var db = new ClinicDatabase(settings.ConnectionString, settings.Schema,
                        settings.PeopleTable, settings.AppointmentsTable, settings.MinTimeBeforeCheckout);
                    db.Initialize();
                    store = new FaceStore(settings.FaceStorePath);
                    engine = new FaceEngine(settings.ModelsFolder);
                    database = db;
                });
            }
            catch (Exception ex)
            {
                ShowError("Startup failed: " + ex.Message, sticky: true);
                return;
            }
            scanButton.Enabled = true;
            enrollButton.Enabled = true;
            ShowIdle();
        }

        private void CameraLoop()
        {
            using (var frame = new Mat())
            {
                while (!closing)
                {
                    if (!camera.Read(frame) || frame.Empty())
                    {
                        Thread.Sleep(30);
                        continue;
                    }
                    lock (frameGate)
                    {
                        if (latestFrame != null)
                        {
                            latestFrame.Dispose();
                        }
                        latestFrame = frame.Clone();
                    }
                    Bitmap image;
                    using (var mirrored = new Mat())
                    {
                        Cv2.Flip(frame, mirrored, FlipMode.Y); // mirror, so the preview feels natural
                        image = mirrored.ToBitmap();
                    }
                    try
                    {
                        BeginInvoke((Action)(() => ShowFrame(image)));
                    }
                    catch (InvalidOperationException)
                    {
                        image.Dispose(); // window is closing
                        return;
                    }
                }
            }
        }

        private void ShowFrame(Bitmap image)
        {
            var old = preview.Image;
            preview.Image = image;
            if (old != null)
            {
                old.Dispose();
            }
        }

        private Mat CurrentFrame()
        {
            lock (frameGate)
            {
                return latestFrame == null ? null : latestFrame.Clone();
            }
        }

        private async void OnScan(object sender, EventArgs e)
        {
            var frame = CurrentFrame();
            if (busy || frame == null)
            {
                return;
            }
            SetBusy(true);
            ShowMessage("Scanning... please hold still.", Info, sticky: true);
            try
            {
                var result = await Task.Run(() =>
                {
                    using (frame)
                    {
                        var embedding = engine.Embed(frame);
                        var match = FaceMatcher.BestMatch(embedding, store.ForModel(FaceEngine.ModelName),
                            settings.MatchThreshold, settings.MatchMargin);
                        return match == null ? null : database.RecordScan(match.Account, DateTime.Now);
                    }
                });
                if (result == null)
                {
                    ShowMessage("Face not recognized. Please see the front desk.", Warn);
                }
                else
                {
                    ShowResult(result);
                }
            }
            catch (NoFaceException)
            {
                ShowMessage("No face detected. Please look at the camera.", Warn);
            }
            catch (Exception ex)
            {
                ShowError("Error: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void OnEnroll(object sender, EventArgs e)
        {
            if (busy)
            {
                return;
            }
            var account = PromptDialog.Ask(this, "Enroll patient",
                "Patient account number.\r\nHave the patient look at the camera, then press OK.");
            if (string.IsNullOrWhiteSpace(account))
            {
                return;
            }
            account = account.Trim();
            var frame = CurrentFrame();
            if (frame == null)
            {
                return;
            }
            SetBusy(true);
            ShowMessage("Enrolling...", Info, sticky: true);
            try
            {
                var error = await Task.Run(() =>
                {
                    using (frame)
                    {
                        if (!database.AccountExists(account))
                        {
                            return "Account " + account + " was not found in the " + settings.PeopleTable + " table.";
                        }
                        store.Add(account, FaceEngine.ModelName, engine.Embed(frame));
                        return null;
                    }
                });
                if (error != null)
                {
                    ShowError(error);
                }
                else
                {
                    ShowMessage("Enrolled account " + account +
                        ". Enroll again from another angle to improve recognition.", Ok);
                }
            }
            catch (NoFaceException)
            {
                ShowMessage("No face detected. Have the patient look at the camera and try again.", Warn);
            }
            catch (Exception ex)
            {
                ShowError("Error: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ShowResult(ScanResult result)
        {
            var when = result.Time.HasValue ? result.Time.Value.ToString("h:mm tt") : "";
            switch (result.Outcome)
            {
                case ScanOutcome.CheckedIn:
                    ShowMessage("Welcome! You are checked in at " + when + ".", Ok);
                    break;
                case ScanOutcome.CheckedOut:
                    ShowMessage("Thank you! You are checked out at " + when + ".", Ok);
                    break;
                case ScanOutcome.AlreadyCheckedIn:
                    ShowMessage("You are already checked in (" + when + "). Scan again when you leave.", Info);
                    break;
                case ScanOutcome.VisitComplete:
                    ShowMessage("Your visit today is already complete.", Info);
                    break;
                default:
                    ShowMessage("No appointment found for you today. Please see the front desk.", Warn);
                    break;
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            scanButton.Enabled = !value;
            enrollButton.Enabled = !value;
        }

        private void ShowError(string text, bool sticky = false)
        {
            ShowMessage(text, Error, sticky);
        }

        /// <summary>Shows a message; it returns to the idle prompt after a few seconds unless sticky.</summary>
        private void ShowMessage(string text, Color color, bool sticky = false)
        {
            statusTimer.Stop();
            status.Text = text;
            status.ForeColor = color;
            if (!sticky)
            {
                statusTimer.Start();
            }
        }

        private void ShowIdle()
        {
            statusTimer.Stop();
            status.Text = "Look at the camera and press Scan to check in or out.";
            status.ForeColor = Info;
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            closing = true;
            if (cameraThread != null)
            {
                cameraThread.Join(1000);
            }
            if (camera != null)
            {
                camera.Dispose();
            }
            if (engine != null)
            {
                engine.Dispose();
            }
        }
    }
}
