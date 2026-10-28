// LGBrightnessHotkeys : des raccourcis clavier (Ctrl + Volume +/- par défaut) règlent la luminosité
// d'un LG UltraFine sur 32 crans, en partant toujours de la valeur réelle lue sur l'écran (HID, rapport feature 0).
// En option, la luminosité suit le capteur de lumière ambiante de l'écran.
// Compilation : voir build.ps1 (csc de .NET Framework, C# 5, métadonnées WinRT fournies avec Windows).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using DeviceInformation = Windows.Devices.Enumeration.DeviceInformation;
using DeviceInformationCollection = Windows.Devices.Enumeration.DeviceInformationCollection;
using LightSensor = Windows.Devices.Sensors.LightSensor;
using LightSensorReadingChangedEventArgs = Windows.Devices.Sensors.LightSensorReadingChangedEventArgs;

// Le runtime ciblé ; l'exécution native ARM64 vient, elle, de <supportedArchitectures> dans app.manifest.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8.1")]

namespace LGBrightnessHotkeys
{
    static class Program
    {
        [STAThread]
        static int Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\LGBrightnessHotkeys.SingleInstance", out created))
            {
                if (!created)
                {
                    // Déjà lancé : relancer l'exe ouvre la fenêtre des raccourcis de l'instance en cours.
                    IntPtr hwnd = Native.FindWindow(null, TrayApp.WindowCaption);
                    if (hwnd != IntPtr.Zero)
                    {
                        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                        Native.PostMessage(hwnd, TrayApp.OpenSettingsMessage, IntPtr.Zero, IntPtr.Zero);
                    }
                    return 1;
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    // Accès HID à l'écran : énumération SetupAPI, interface UsagePage 0x80 / Usage 0x01.
    sealed class LgMonitor : IDisposable
    {
        public const int RawMin = 400, RawMax = 54000;

        SafeFileHandle handle;
        int featureLength;

        public bool TryRead(out int raw)
        {
            raw = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!EnsureOpen()) return false;
                var buf = new byte[featureLength];
                if (Native.HidD_GetFeature(handle, buf, buf.Length))
                {
                    raw = buf[1] | (buf[2] << 8);
                    return true;
                }
                Invalidate();
            }
            return false;
        }

        public bool TryWrite(int raw)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!EnsureOpen()) return false;
                var buf = new byte[featureLength];
                buf[1] = (byte)(raw & 0xFF);
                buf[2] = (byte)((raw >> 8) & 0xFF);
                if (Native.HidD_SetFeature(handle, buf, buf.Length)) return true;
                Invalidate();
            }
            return false;
        }

        public void Invalidate()
        {
            if (handle != null) { handle.Dispose(); handle = null; }
        }

        public void Dispose() { Invalidate(); }

        bool EnsureOpen()
        {
            if (handle != null) return true;
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return false;
            try
            {
                for (int i = 0; ; i++)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(did);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did)) return false;
                    string path = GetDevicePath(set, ref did);
                    if (path == null || path.IndexOf("vid_043e", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int len;
                    SafeFileHandle h = OpenBrightnessInterface(path, out len);
                    if (h != null) { handle = h; featureLength = len; return true; }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        static string GetDevicePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA did)
        {
            int required;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(required);
            try
            {
                // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits, 6 en 32 bits.
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, required, out required, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static SafeFileHandle OpenBrightnessInterface(string path, out int featureLen)
        {
            featureLen = 0;
            SafeFileHandle h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                h.Dispose();
                h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); return null; }
            }
            IntPtr preparsed;
            if (!Native.HidD_GetPreparsedData(h, out preparsed)) { h.Dispose(); return null; }
            var caps = new byte[64];
            int status = Native.HidP_GetCaps(preparsed, caps);
            Native.HidD_FreePreparsedData(preparsed);
            ushort usage = BitConverter.ToUInt16(caps, 0);
            ushort usagePage = BitConverter.ToUInt16(caps, 2);
            ushort len = BitConverter.ToUInt16(caps, 8);
            if (status != Native.HIDP_STATUS_SUCCESS || usagePage != 0x80 || usage != 0x01 || len < 3) { h.Dispose(); return null; }
            featureLen = len;
            return h;
        }

        public static int RawToPercent(int raw)
        {
            if (raw <= RawMin) return 0;
            return Math.Min(100, (int)Math.Round(raw * 100.0 / RawMax));
        }

        public static int PercentToRaw(int pct)
        {
            return Math.Max(RawMin, (int)Math.Round(pct / 100.0 * RawMax));
        }

        // 32 crans de RawMin à RawMax, chacun multipliant la luminosité par (54000 / 400)^(1/32) ≈ 1,166 :
        // l'œil perçoit les écarts en relatif, donc des pas fins en bas et larges en haut (comme les 16 crans de macOS).
        public const int Steps = 32;

        public static int LevelRaw(int level)
        {
            return (int)Math.Round(RawMin * Math.Pow((double)RawMax / RawMin, (double)level / Steps));
        }

        // Valeur brute du cran suivant (dir > 0) ou précédent (dir < 0) ; à un quart de cran près, on est sur le cran.
        public static int NextStep(int raw, int dir)
        {
            double level = Math.Log(Math.Max(raw, 1) / (double)RawMin) / Math.Log((double)RawMax / RawMin) * Steps;
            const double tol = 0.25;
            int target = dir > 0 ? (int)Math.Floor(level + tol) + 1 : (int)Math.Ceiling(level - tol) - 1;
            return LevelRaw(Math.Max(0, Math.Min(Steps, target)));
        }
    }

    // Capteur de lumière ambiante de l'écran (« Capteur de lumière ambiante HID », pilote de Windows),
    // lu par Windows.Devices.Sensors. Il n'envoie une mesure que quand la lumière change ; plage 0 à 4096 lux.
    sealed class AmbientLight : IDisposable
    {
        readonly object gate = new object();
        LightSensor sensor;
        Windows.Foundation.TypedEventHandler<LightSensor, LightSensorReadingChangedEventArgs> handler;
        double lux = double.NaN;

        // Dernière mesure, NaN sans capteur. Les mesures arrivent sur un thread du système.
        public double Lux { get { lock (gate) return lux; } }

        public bool Found { get { return sensor != null; } }

        public void Open()
        {
            Close();
            try
            {
                LightSensor s = FindLgSensor();
                if (s == null) return;
                s.ReportInterval = Math.Max(s.MinimumReportInterval, 250u);
                handler = (sender, e) => { double v = e.Reading.IlluminanceInLux; lock (gate) lux = v; };
                s.ReadingChanged += handler;
                sensor = s;
                var reading = s.GetCurrentReading();
                if (reading != null) lock (gate) lux = reading.IlluminanceInLux;
            }
            catch (Exception) { Close(); } // écran débranché pendant la recherche
        }

        public void Close()
        {
            if (sensor != null)
            {
                try { sensor.ReadingChanged -= handler; sensor.ReportInterval = 0; }
                catch (Exception) { }
                sensor = null;
            }
            lock (gate) lux = double.NaN;
        }

        public void Dispose() { Close(); }

        static LightSensor FindLgSensor()
        {
            DeviceInformationCollection devices = Wait(DeviceInformation.FindAllAsync(LightSensor.GetDeviceSelector()));
            if (devices == null) return null;
            foreach (DeviceInformation d in devices)
                if (d.Id.IndexOf("vid_043e", StringComparison.OrdinalIgnoreCase) >= 0) return Wait(LightSensor.FromIdAsync(d.Id));
            return null;
        }

        // Attente bloquante (quelques dizaines de ms) : les opérations WinRT se terminent sur un autre thread.
        static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) where T : class
        {
            DateTime until = DateTime.UtcNow.AddSeconds(3);
            while (op.Status == Windows.Foundation.AsyncStatus.Started)
            {
                if (DateTime.UtcNow > until) { op.Cancel(); return null; }
                Thread.Sleep(10);
            }
            return op.Status == Windows.Foundation.AsyncStatus.Completed ? op.GetResults() : null;
        }
    }

    // Luminosité automatique : courbe lux → %, lissage de la mesure, et décalage appris des réglages manuels
    // (« à cette lumière, je préfère 5 points de plus »).
    sealed class AutoBrightness
    {
        // Courbe par défaut, interpolée sur log10(lux + 1) : l'œil perçoit la lumière de façon logarithmique.
        static readonly double[] CurveLux = { 0, 10, 50, 100, 300, 1000, 3000 };
        static readonly double[] CurvePct = { 5, 10, 20, 30, 50, 75, 100 };
        public const int Threshold = 5;     // écart minimal, en points de %, avant de changer la luminosité
        const int Window = 11;              // médiane des 11 dernières mesures, une par seconde

        readonly double[] samples = new double[Window];
        int count, next;
        public double Offset;               // points de % ajoutés à la courbe

        public bool HasLux { get { return count > 0; } }

        // Médiane : une main devant le capteur ou un flash de moins de 5 s ne la déplace pas.
        public double SmoothedLux
        {
            get
            {
                var sorted = new double[count];
                Array.Copy(samples, sorted, count);
                Array.Sort(sorted);
                return sorted[count / 2];
            }
        }

        public void Reset() { count = next = 0; }

        public void AddSample(double lux)
        {
            samples[next] = Math.Max(0, lux);
            next = (next + 1) % Window;
            if (count < Window) count++;
        }

        public static double Curve(double lux)
        {
            double x = Math.Log10(Math.Max(0, lux) + 1);
            for (int i = 1; i < CurveLux.Length; i++)
            {
                double x1 = Math.Log10(CurveLux[i] + 1);
                if (x <= x1)
                {
                    double x0 = Math.Log10(CurveLux[i - 1] + 1);
                    return CurvePct[i - 1] + (CurvePct[i] - CurvePct[i - 1]) * (x - x0) / (x1 - x0);
                }
            }
            return CurvePct[CurvePct.Length - 1];
        }

        public int Target()
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(Curve(SmoothedLux) + Offset)));
        }

        // La luminosité choisie à la main devient la référence pour la lumière actuelle.
        public void Learn(int pct)
        {
            if (HasLux) Offset = Math.Max(-100, Math.Min(100, pct - Curve(SmoothedLux)));
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        public const string WindowCaption = "LGBrightnessHotkeys";
        public static readonly int OpenSettingsMessage = Native.RegisterWindowMessage("LGBrightnessHotkeys.OpenSettings");
        const int HotkeyUp = 1, HotkeyDown = 2;
        const int WriteSettleMs = 1000;
        const int FadeSteps = 30;           // fondu de la luminosité auto : 30 pas de 50 ms

        readonly LgMonitor monitor = new LgMonitor();
        readonly NotifyIcon tray;
        readonly Icon trayIcon;
        readonly MessageWindow window;
        readonly Osd osd = new Osd();
        readonly ToolStripMenuItem retryItem, autoItem;
        readonly AmbientLight light = new AmbientLight();
        readonly AutoBrightness auto = new AutoBrightness();
        readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 50 };
        bool autoEnabled, learnOnNextTick, sensorStale;
        DateTime lastDeviceChange;
        int expectedRaw = -1;               // dernière valeur connue de l'écran, pour repérer un réglage fait ailleurs
        int fadeFromRaw, fadeToRaw, fadeStep;
        Hotkey upKey = Settings.Load(Settings.UpName, Hotkey.DefaultUp);
        Hotkey downKey = Settings.Load(Settings.DownName, Hotkey.DefaultDown);
        bool upRegistered, downRegistered, exiting;
        SettingsForm settingsForm;
        DateTime lastTooltipRefresh = DateTime.MinValue;
        int lastWrittenRaw = -1;
        DateTime lastWriteTime;

        public TrayApp()
        {
            trayIcon = Sun.MakeIcon(SystemInformation.SmallIconSize.Width);
            var menu = new ContextMenuStrip();
            retryItem = new ToolStripMenuItem("Réactiver les raccourcis", null, delegate { RegisterHotkeys(true); });
            retryItem.Visible = false;
            menu.Items.Add(retryItem);
            autoItem = new ToolStripMenuItem("Luminosité automatique", null, delegate { SetAuto(!autoEnabled, true); });
            menu.Items.Add(autoItem);
            var settingsItem = new ToolStripMenuItem("Raccourcis…", null, delegate { OpenSettings(); });
            settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold); // action du double-clic
            menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripMenuItem("Quitter", null, delegate { ExitThread(); }));
            tray = new NotifyIcon { Icon = trayIcon, Text = "Luminosité LG", ContextMenuStrip = menu, Visible = true };
            tray.MouseMove += delegate
            {
                if ((DateTime.Now - lastTooltipRefresh).TotalSeconds >= 2) RefreshTooltip();
            };
            tray.DoubleClick += delegate { OpenSettings(); };

            window = new MessageWindow(this);
            RegisterHotkeys(false);
            autoTimer.Tick += delegate { AutoTick(); };
            fadeTimer.Tick += delegate { FadeTick(); };
            auto.Offset = Settings.LoadInt(Settings.AutoOffsetName, 0) / 10.0;
            if (Settings.LoadInt(Settings.AutoName, 0) != 0) SetAuto(true, false);
            RefreshTooltip();
        }

        // Activée depuis le menu, la luminosité auto part de la luminosité actuelle ; au démarrage de Windows,
        // elle reprend le décalage enregistré.
        void SetAuto(bool on, bool fromMenu)
        {
            autoEnabled = on;
            autoItem.Checked = on;
            StopFade();
            auto.Reset();
            expectedRaw = -1;
            if (on)
            {
                learnOnNextTick = fromMenu;
                light.Open();
                if (fromMenu && !light.Found)
                    tray.ShowBalloonTip(6000, "Luminosité LG", "Capteur de lumière de l'écran introuvable. La luminosité automatique démarrera quand l'écran sera branché.", ToolTipIcon.Warning);
                autoTimer.Start();
                AutoTick();
            }
            else
            {
                autoTimer.Stop();
                light.Close();
            }
            if (fromMenu) Settings.SaveInt(Settings.AutoName, on ? 1 : 0);
            RefreshTooltip();
        }

        void AutoTick()
        {
            DateTime now = DateTime.UtcNow;
            // Branchement ou débranchement : on rouvre le capteur une fois la rafale de notifications passée.
            if (sensorStale && (now - lastDeviceChange).TotalSeconds >= 2) { sensorStale = false; light.Open(); }
            double lux = light.Lux;
            if (double.IsNaN(lux)) return;
            auto.AddSample(lux);
            if (fadeTimer.Enabled || (now - lastWriteTime).TotalMilliseconds < WriteSettleMs) return;

            int raw;
            if (!monitor.TryRead(out raw)) { expectedRaw = -1; return; }
            int pct = LgMonitor.RawToPercent(raw);
            // Activation depuis le menu, ou réglage fait par un autre logiciel : cette luminosité devient la référence.
            if (learnOnNextTick || (expectedRaw >= 0 && Math.Abs(pct - LgMonitor.RawToPercent(expectedRaw)) >= 2))
            {
                learnOnNextTick = false;
                LearnAuto(pct);
            }
            expectedRaw = raw;
            int target = auto.Target();
            if (Math.Abs(target - pct) >= AutoBrightness.Threshold) StartFade(raw, LgMonitor.PercentToRaw(target));
        }

        void LearnAuto(int pct)
        {
            auto.Learn(pct);
            Settings.SaveInt(Settings.AutoOffsetName, (int)Math.Round(auto.Offset * 10));
        }

        void StartFade(int fromRaw, int toRaw)
        {
            fadeFromRaw = fromRaw;
            fadeToRaw = toRaw;
            fadeStep = 0;
            fadeTimer.Start();
        }

        void StopFade() { fadeTimer.Stop(); }

        void FadeTick()
        {
            fadeStep++;
            int raw = fadeFromRaw + (int)Math.Round((fadeToRaw - fadeFromRaw) * (double)fadeStep / FadeSteps);
            if (!monitor.TryWrite(raw)) { StopFade(); expectedRaw = -1; return; }
            lastWrittenRaw = expectedRaw = raw;
            lastWriteTime = DateTime.UtcNow;
            if (fadeStep >= FadeSteps) { StopFade(); SetTooltip(LgMonitor.RawToPercent(raw)); }
        }

        void RegisterHotkeys(bool fromMenu)
        {
            if (!upRegistered) upRegistered = Native.RegisterHotKey(window.Handle, HotkeyUp, upKey.Modifiers, upKey.Vk);
            if (!downRegistered) downRegistered = Native.RegisterHotKey(window.Handle, HotkeyDown, downKey.Modifiers, downKey.Vk);
            if (upRegistered && downRegistered)
            {
                retryItem.Visible = false;
                if (fromMenu) tray.ShowBalloonTip(4000, "Luminosité LG", "Raccourcis " + upKey + " et " + downKey + " actifs.", ToolTipIcon.Info);
                return;
            }
            retryItem.Visible = true;
            tray.ShowBalloonTip(10000, "Luminosité LG : raccourci déjà pris",
                TakenMessage(upRegistered, downRegistered, upKey, downKey) + " Fermez l'autre application (ex. « LG Ultrafine Brightness ») puis « Réactiver les raccourcis », ou choisissez-en d'autres via « Raccourcis… ».",
                ToolTipIcon.Warning);
        }

        void UnregisterHotkeys()
        {
            if (upRegistered) Native.UnregisterHotKey(window.Handle, HotkeyUp);
            if (downRegistered) Native.UnregisterHotKey(window.Handle, HotkeyDown);
            upRegistered = downRegistered = false;
        }

        static string TakenMessage(bool upOk, bool downOk, Hotkey up, Hotkey down)
        {
            if (!upOk && !downOk) return up + " et " + down + " sont déjà utilisés par une autre application ou réservés par Windows.";
            return (!upOk ? up : down) + " est déjà utilisé par une autre application ou réservé par Windows.";
        }

        // Les raccourcis sont libérés pendant que la fenêtre est ouverte, pour pouvoir les saisir.
        internal void OpenSettings()
        {
            if (settingsForm != null) { settingsForm.Activate(); return; }
            UnregisterHotkeys();
            settingsForm = new SettingsForm(upKey, downKey, trayIcon, TryApplyHotkeys);
            try { settingsForm.ShowDialog(); }
            finally { settingsForm.Dispose(); settingsForm = null; }
            if (!exiting && (!upRegistered || !downRegistered)) RegisterHotkeys(false); // annulé : on remet les anciens
        }

        // Appelé par le bouton OK : renvoie null si les nouveaux raccourcis sont actifs, sinon le message d'erreur.
        string TryApplyHotkeys(Hotkey up, Hotkey down)
        {
            if (up.Equals(down)) return "Les deux raccourcis doivent être différents.";
            UnregisterHotkeys();
            bool upOk = Native.RegisterHotKey(window.Handle, HotkeyUp, up.Modifiers, up.Vk);
            bool downOk = Native.RegisterHotKey(window.Handle, HotkeyDown, down.Modifiers, down.Vk);
            if (upOk && downOk)
            {
                upKey = up;
                downKey = down;
                upRegistered = downRegistered = true;
                retryItem.Visible = false;
                Settings.Save(Settings.UpName, up);
                Settings.Save(Settings.DownName, down);
                return null;
            }
            if (upOk) Native.UnregisterHotKey(window.Handle, HotkeyUp);
            if (downOk) Native.UnregisterHotKey(window.Handle, HotkeyDown);
            return TakenMessage(upOk, downOk, up, down);
        }

        internal void OnHotkey(int id)
        {
            int dir = id == HotkeyUp ? 1 : id == HotkeyDown ? -1 : 0;
            if (dir == 0) return;
            StopFade();
            int raw;
            if (!monitor.TryRead(out raw)) { ShowNotFound(); return; }
            // L'écran ne renvoie la nouvelle valeur que 180 à 360 ms après une écriture : juste après
            // nos propres écritures (touche maintenue, appuis rapprochés), partir de la dernière valeur écrite.
            if (lastWrittenRaw >= 0 && (DateTime.UtcNow - lastWriteTime).TotalMilliseconds < WriteSettleMs) raw = lastWrittenRaw;
            int newRaw = LgMonitor.NextStep(raw, dir);
            int pct = LgMonitor.RawToPercent(newRaw);
            if (newRaw != raw)
            {
                if (!monitor.TryWrite(newRaw)) { ShowNotFound(); return; }
                lastWrittenRaw = newRaw;
                lastWriteTime = DateTime.UtcNow;
            }
            if (autoEnabled)
            {
                // En mode auto, l'appui ne coupe rien : il règle la préférence pour la lumière actuelle.
                learnOnNextTick = false;
                LearnAuto(pct);
                expectedRaw = newRaw;
            }
            SetTooltip(pct);
            osd.ShowLevel(pct);
        }

        internal void OnDeviceChange()
        {
            monitor.Invalidate();
            lastWrittenRaw = -1;
            if (autoEnabled)
            {
                sensorStale = true;
                lastDeviceChange = DateTime.UtcNow;
                expectedRaw = -1;
            }
        }

        void ShowNotFound()
        {
            lastWrittenRaw = -1;
            tray.Text = "Luminosité LG : écran introuvable";
            osd.ShowMessage("Écran LG introuvable");
        }

        void RefreshTooltip()
        {
            lastTooltipRefresh = DateTime.Now;
            int raw;
            if (monitor.TryRead(out raw)) SetTooltip(LgMonitor.RawToPercent(raw));
            else tray.Text = "Luminosité LG : écran introuvable";
        }

        void SetTooltip(int pct)
        {
            string text = "Luminosité LG : " + pct + " %";
            if (autoEnabled)
            {
                double lux = light.Lux;
                text += double.IsNaN(lux) ? " (auto)" : " (auto, " + Math.Round(lux) + " lux)";
            }
            tray.Text = text;
            lastTooltipRefresh = DateTime.Now;
        }

        protected override void ExitThreadCore()
        {
            exiting = true;
            if (settingsForm != null) settingsForm.Close();
            UnregisterHotkeys();
            autoTimer.Dispose();
            fadeTimer.Dispose();
            light.Dispose();
            tray.Visible = false;
            tray.Dispose();
            trayIcon.Dispose();
            osd.Dispose();
            window.Destroy();
            monitor.Dispose();
            base.ExitThreadCore();
        }

        // Fenêtre invisible de premier niveau : reçoit WM_HOTKEY, les WM_DEVICECHANGE diffusés
        // et le message d'une deuxième instance qui demande d'ouvrir les réglages.
        sealed class MessageWindow : NativeWindow
        {
            readonly TrayApp owner;
            IntPtr notification;

            public MessageWindow(TrayApp owner)
            {
                this.owner = owner;
                CreateHandle(new CreateParams { Caption = WindowCaption });
                notification = RegisterHidNotification(Handle);
            }

            static IntPtr RegisterHidNotification(IntPtr hwnd)
            {
                // DEV_BROADCAST_DEVICEINTERFACE_W : size, devicetype, reserved, classguid, name[1] (32 octets avec alignement).
                Guid hidGuid;
                Native.HidD_GetHidGuid(out hidGuid);
                IntPtr buf = Marshal.AllocHGlobal(32);
                try
                {
                    for (int i = 0; i < 32; i++) Marshal.WriteByte(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, 32);
                    Marshal.WriteInt32(buf, 4, Native.DBT_DEVTYP_DEVICEINTERFACE);
                    Marshal.Copy(hidGuid.ToByteArray(), 0, new IntPtr(buf.ToInt64() + 12), 16);
                    return Native.RegisterDeviceNotification(hwnd, buf, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            public void Destroy()
            {
                if (notification != IntPtr.Zero) { Native.UnregisterDeviceNotification(notification); notification = IntPtr.Zero; }
                DestroyHandle();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_HOTKEY) owner.OnHotkey(m.WParam.ToInt32());
                else if (m.Msg == Native.WM_DEVICECHANGE) owner.OnDeviceChange();
                else if (m.Msg == OpenSettingsMessage) owner.OpenSettings();
                base.WndProc(ref m);
            }
        }
    }

    // Affichage à l'écran : fenêtre sans bordure, au premier plan, qui ne prend jamais le focus.
    sealed class Osd : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        readonly float scale;
        readonly Font symbolFont, textFont, messageFont;
        int level;
        string message;

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32);
            DoubleBuffered = true;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size(S(220), S(72));
            symbolFont = new Font("Segoe UI Symbol", 16f);
            textFont = new Font("Segoe UI Semibold", 15f);
            messageFont = new Font("Segoe UI", 12f);
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            }
            catch (Exception) { } // coins carrés avant Windows 11
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public void ShowLevel(int pct) { level = pct; message = null; Popup(); }

        public void ShowMessage(string text) { message = text; Popup(); }

        void Popup()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - S(56));
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Invalidate();
            Update();
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Size client = ClientSize;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (message != null)
            {
                TextRenderer.DrawText(g, message, messageFont, new Rectangle(Point.Empty, client), Color.White,
                    flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            const string sun = "☀";
            string text = level + " %";
            Size sunSize = TextRenderer.MeasureText(g, sun, symbolFont, Size.Empty, flags);
            Size textSize = TextRenderer.MeasureText(g, text, textFont, Size.Empty, flags);
            int gap = S(8);
            int rowHeight = Math.Max(sunSize.Height, textSize.Height);
            int rowTop = S(12);
            int x = (client.Width - (sunSize.Width + gap + textSize.Width)) / 2;
            TextRenderer.DrawText(g, sun, symbolFont, new Point(x, rowTop + (rowHeight - sunSize.Height) / 2), Sun.Color, flags);
            TextRenderer.DrawText(g, text, textFont, new Point(x + sunSize.Width + gap, rowTop + (rowHeight - textSize.Height) / 2), Color.White, flags);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = S(20), barHeight = S(6);
            var track = new RectangleF(pad, client.Height - S(16) - barHeight, client.Width - 2 * pad, barHeight);
            using (var b = new SolidBrush(Color.FromArgb(70, 70, 70))) FillPill(g, b, track);
            if (level > 0)
            {
                var fill = new RectangleF(track.X, track.Y, Math.Max(barHeight, track.Width * level / 100f), track.Height);
                using (var b = new SolidBrush(Color.White)) FillPill(g, b, fill);
            }
        }

        static void FillPill(Graphics g, Brush brush, RectangleF r)
        {
            float d = r.Height;
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hideTimer.Dispose();
                symbolFont.Dispose();
                textFont.Dispose();
                messageFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Un raccourci : modificateurs MOD_* + touche virtuelle, tel que le prend RegisterHotKey.
    struct Hotkey
    {
        public static readonly Hotkey DefaultUp = new Hotkey(Native.MOD_CONTROL, Native.VK_VOLUME_UP);
        public static readonly Hotkey DefaultDown = new Hotkey(Native.MOD_CONTROL, Native.VK_VOLUME_DOWN);

        public readonly uint Modifiers, Vk;

        public Hotkey(uint modifiers, uint vk)
        {
            Modifiers = modifiers & (Native.MOD_ALT | Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_WIN);
            Vk = vk & 0xFF;
        }

        public int Encode() { return (int)((Modifiers << 16) | Vk); }

        public static Hotkey Decode(int value) { return new Hotkey((uint)value >> 16, (uint)value & 0xFFFF); }

        // Sans modificateur, seules les touches qui ne servent pas à taper du texte sont acceptées.
        public bool IsValid
        {
            get { return Vk != 0 && (Modifiers != 0 || (Vk >= 0x70 && Vk <= 0x87) || (Vk >= 0xA6 && Vk <= 0xB7) || Vk == 0x13 || Vk == 0x91); }
        }

        public static string ModifiersText(uint mods)
        {
            var sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Maj + ");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win + ");
            return sb.Length == 0 ? "" : sb.ToString(0, sb.Length - 3);
        }

        public override string ToString()
        {
            string mods = ModifiersText(Modifiers);
            return mods.Length == 0 ? KeyName(Vk) : mods + " + " + KeyName(Vk);
        }

        static string KeyName(uint vk)
        {
            switch (vk)
            {
                case 0x08: return "Retour arrière";
                case 0x09: return "Tab";
                case 0x0D: return "Entrée";
                case 0x13: return "Pause";
                case 0x1B: return "Échap";
                case 0x20: return "Espace";
                case 0x21: return "Page préc.";
                case 0x22: return "Page suiv.";
                case 0x23: return "Fin";
                case 0x24: return "Origine";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2D: return "Inser";
                case 0x2E: return "Suppr";
                case 0x91: return "Arrêt défil";
                case 0xA6: return "Navigateur : précédent";
                case 0xA7: return "Navigateur : suivant";
                case 0xA8: return "Navigateur : actualiser";
                case 0xA9: return "Navigateur : arrêter";
                case 0xAA: return "Rechercher";
                case 0xAB: return "Favoris";
                case 0xAC: return "Accueil";
                case 0xAD: return "Muet";
                case 0xAE: return "Volume −";
                case 0xAF: return "Volume +";
                case 0xB0: return "Piste suivante";
                case 0xB1: return "Piste précédente";
                case 0xB2: return "Stop";
                case 0xB3: return "Lecture/Pause";
                case 0xB4: return "Courrier";
                case 0xB5: return "Média";
                case 0xB6: return "Application 1";
                case 0xB7: return "Application 2";
            }
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Pavé num. " + (vk - 0x60);
            // Autres touches : le caractère de la disposition clavier en cours, sinon le nom donné par Windows.
            uint ch = Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_CHAR) & 0x7FFFFFFF;
            if (ch > 0x20) return char.ToUpper((char)ch).ToString();
            uint scan = Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
            var name = new StringBuilder(64);
            if (scan != 0 && Native.GetKeyNameText((int)(scan << 16), name, name.Capacity) > 0) return name.ToString();
            return "Touche 0x" + vk.ToString("X2");
        }
    }

    // Réglages dans HKCU\Software\LGBrightnessHotkeys : raccourcis (DWORD : modificateurs << 16 | touche),
    // luminosité auto activée (0/1) et décalage appris (dixièmes de point de %).
    static class Settings
    {
        public const string UpName = "HotkeyUp", DownName = "HotkeyDown";
        public const string AutoName = "AutoBrightness", AutoOffsetName = "AutoOffset";
        const string KeyPath = @"Software\LGBrightnessHotkeys";

        public static Hotkey Load(string name, Hotkey fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key == null ? null : key.GetValue(name);
                    if (value is int)
                    {
                        Hotkey h = Hotkey.Decode((int)value);
                        if (h.IsValid) return h;
                    }
                }
            }
            catch (Exception) { } // réglage illisible : valeur par défaut
            return fallback;
        }

        public static void Save(string name, Hotkey h)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, h.Encode(), RegistryValueKind.DWord);
        }

        public static int LoadInt(string name, int fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key == null ? null : key.GetValue(name);
                    if (value is int) return (int)value;
                }
            }
            catch (Exception) { }
            return fallback;
        }

        public static void SaveInt(string name, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch (Exception) { } // préférence non enregistrée : sans gravité
        }
    }

    // Fenêtre « Raccourcis » : on clique dans une case puis on appuie sur la combinaison voulue.
    sealed class SettingsForm : Form
    {
        readonly HotkeyBox upBox = new HotkeyBox(), downBox = new HotkeyBox();
        readonly Label status = new Label();
        readonly Func<Hotkey, Hotkey, string> apply;

        public SettingsForm(Hotkey up, Hotkey down, Icon icon, Func<Hotkey, Hotkey, string> apply)
        {
            this.apply = apply;
            Text = "Raccourcis – Luminosité LG";
            Icon = icon;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Func<int, int> s = v => (int)Math.Round(v * scale);
            Padding = new Padding(s(12));

            var upLabel = MakeLabel("Augmenter la luminosité :");
            var downLabel = MakeLabel("Diminuer la luminosité :");
            int boxWidth = TextRenderer.MeasureText("Ctrl + Alt + Maj + Win + Navigateur : actualiser", Font).Width + s(16);
            int labelWidth = Math.Max(TextRenderer.MeasureText(upLabel.Text, Font).Width, TextRenderer.MeasureText(downLabel.Text, Font).Width);
            int textWidth = labelWidth + boxWidth + s(12);
            upBox.Width = downBox.Width = boxWidth;
            upBox.Value = up;
            downBox.Value = down;
            upBox.Hint += ShowStatus;
            downBox.Hint += ShowStatus;

            var intro = new Label
            {
                Text = "Cliquez dans une case, puis appuyez sur la combinaison de touches voulue.",
                AutoSize = true,
                MaximumSize = new Size(textWidth, 0),
                Margin = new Padding(0, 0, 0, s(10)),
            };
            status.AutoSize = true;
            status.MaximumSize = new Size(textWidth, 0);
            status.ForeColor = Color.Firebrick;
            status.Margin = new Padding(0, s(6), 0, 0);

            var defaults = new Button { Text = "Par défaut", AutoSize = true, TabIndex = 0 };
            var ok = new Button { Text = "OK", AutoSize = true, TabIndex = 1 };
            var cancel = new Button { Text = "Annuler", AutoSize = true, TabIndex = 2, DialogResult = DialogResult.Cancel };
            ok.MinimumSize = cancel.MinimumSize = defaults.MinimumSize = new Size(s(80), 0);
            defaults.Click += delegate { upBox.Value = Hotkey.DefaultUp; downBox.Value = Hotkey.DefaultDown; ShowStatus(""); };
            ok.Click += delegate
            {
                string error = apply(upBox.Value, downBox.Value);
                if (error == null) DialogResult = DialogResult.OK;
                else ShowStatus(error);
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, s(12), 0, 0),
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(defaults);

            // Le Padding du formulaire ne décale que les contrôles ancrés : on place la grille à la main.
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(Padding.Left, Padding.Top) };
            grid.Controls.Add(intro, 0, 0);
            grid.SetColumnSpan(intro, 2);
            grid.Controls.Add(upLabel, 0, 1);
            grid.Controls.Add(upBox, 1, 1);
            grid.Controls.Add(downLabel, 0, 2);
            grid.Controls.Add(downBox, 1, 2);
            grid.Controls.Add(status, 0, 3);
            grid.SetColumnSpan(status, 2);
            grid.Controls.Add(buttons, 0, 4);
            grid.SetColumnSpan(buttons, 2);
            Controls.Add(grid);

            AcceptButton = ok;
            CancelButton = cancel;
            ActiveControl = upBox;
            Shown += delegate { Activate(); };
        }

        static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        }

        void ShowStatus(string text) { status.Text = text; }
    }

    // Case de saisie d'un raccourci : la combinaison pressée remplace le contenu.
    sealed class HotkeyBox : TextBox
    {
        Hotkey value;
        bool captured; // combinaison saisie, modificateurs encore enfoncés
        public event Action<string> Hint;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
        }

        public Hotkey Value
        {
            get { return value; }
            set { this.value = value; Text = value.ToString(); Select(0, 0); }
        }

        // La case n'est pas éditable : pas de texte surligné quand elle prend le focus.
        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            BeginInvoke((MethodInvoker)delegate { Select(0, 0); });
        }

        // Toutes les touches vont à la case, sauf Tab, Entrée et Échap seuls (navigation, OK, Annuler).
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == 0 && (key == Keys.Tab || key == Keys.Enter || key == Keys.Escape)) return false;
            return true;
        }

        // Aucun raccourci de la fenêtre (mnémoniques, Ctrl+A...) ne doit intercepter la saisie.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { return false; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            uint mods = CurrentModifiers();
            if (IsModifierKey(e.KeyCode))
            {
                captured = false;
                Text = Hotkey.ModifiersText(mods) + " + …";
                return;
            }
            var candidate = new Hotkey(mods, (uint)e.KeyCode);
            if (!candidate.IsValid)
            {
                Text = value.ToString();
                RaiseHint("« " + candidate + " » seule n'est pas possible : ajoutez Ctrl, Alt, Maj ou Win (sauf pour F1–F24 et les touches multimédia).");
                return;
            }
            Value = candidate;
            captured = true;
            RaiseHint("");
        }

        // Traitée aussi : sinon relâcher Alt ouvrirait le menu système de la fenêtre.
        protected override void OnKeyUp(KeyEventArgs e)
        {
            e.Handled = true;
            uint mods = CurrentModifiers();
            if (mods == 0) captured = false;
            Text = mods != 0 && !captured ? Hotkey.ModifiersText(mods) + " + …" : value.ToString();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            captured = false;
            Text = value.ToString();
        }

        void RaiseHint(string text)
        {
            if (Hint != null) Hint(text);
        }

        static bool IsModifierKey(Keys key)
        {
            switch (key)
            {
                case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
                case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
                case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
                case Keys.LWin: case Keys.RWin:
                    return true;
            }
            return false;
        }

        static uint CurrentModifiers()
        {
            uint mods = 0;
            if (IsDown(0x11)) mods |= Native.MOD_CONTROL;
            if (IsDown(0x12)) mods |= Native.MOD_ALT;
            if (IsDown(0x10)) mods |= Native.MOD_SHIFT;
            if (IsDown(0x5B) || IsDown(0x5C)) mods |= Native.MOD_WIN;
            return mods;
        }

        static bool IsDown(int vk) { return (Native.GetKeyState(vk) & 0x8000) != 0; }
    }

    static class Sun
    {
        public static readonly Color Color = Color.FromArgb(255, 196, 40);

        public static Icon MakeIcon(int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var brush = new SolidBrush(Color))
                using (var pen = new Pen(Color, Math.Max(1.5f, size / 10f)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float c = size / 2f, r = size * 0.2f, inner = size * 0.32f, outer = size * 0.45f;
                    g.FillEllipse(brush, c - r, c - r, 2 * r, 2 * r);
                    for (int k = 0; k < 8; k++)
                    {
                        double a = k * Math.PI / 4;
                        float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                        g.DrawLine(pen, c + dx * inner, c + dy * inner, c + dx * outer, c + dy * outer);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }
    }

    static class Native
    {
        public const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        public const int WM_HOTKEY = 0x0312, WM_DEVICECHANGE = 0x0219, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
        public const uint MAPVK_VK_TO_VSC = 0, MAPVK_VK_TO_CHAR = 2;
        public const int ASFW_ANY = -1;
        public const uint VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
        public const int DBT_DEVTYP_DEVICEINTERFACE = 5, DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        public const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        // Les fonctions HidD_* renvoient un BOOLEAN (1 octet).
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameText(int lParam, StringBuilder name, int size);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);
        [DllImport("user32.dll")] public static extern bool UnregisterDeviceNotification(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
