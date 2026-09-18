// Surface Keyboard Backlight Keeper - keeps the Surface keyboard backlight from timing out.
// Made by Claude (Anthropic's AI model), prompted, tested and directed by Oracooll. MIT License.
//
// How it works: Windows 11 25H2 drives Surface keyboard backlights through a standard
// HID "Keyboard Backlight" collection (usage page 0x0C, usage 0x07). The keyboard firmware
// turns the light off after ~30 s without physical key presses. This tray app re-sends the
// HID "Set Level" output report (usage 0x7B) every few seconds, which re-arms the firmware
// timer, so the light stays on. It follows whatever brightness Windows last set, so the
// keyboard's backlight key keeps working (and choosing "off" with that key is respected).
//
// Known limit (verified on Surface Laptop Studio 2): once the firmware has switched the light
// off, no HID write of any kind turns it back on. Only a physical key press, a trackpad touch
// or the display turning back on does. So: touch the trackpad once, and this app keeps it on.
//
// Build (no SDK needed, uses the .NET Framework compiler that ships with Windows):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+
//     /out:SurfaceKeyboardBacklightKeeper.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll
//     /win32manifest:app.manifest SurfaceBacklightKeeper.cs   (or just run build.ps1)
//
// Written in C# 5 syntax on purpose so the in-box compiler can build it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("Surface Keyboard Backlight Keeper")]
[assembly: System.Reflection.AssemblyProduct("Surface Keyboard Backlight Keeper")]
[assembly: System.Reflection.AssemblyDescription("Keeps the Surface keyboard backlight from timing out. Made by Claude, prompted by Oracooll.")]
[assembly: System.Reflection.AssemblyCompany("Made by Claude, prompted by Oracooll")]
[assembly: System.Reflection.AssemblyCopyright("MIT License. Copyright (c) 2026 Oracooll. Made by Claude.")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace SurfaceBacklightKeeper
{
    // ------------------------------------------------------------------ Win32 / HID interop
    static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const int HidP_Input = 0, HidP_Output = 1, HidP_Feature = 2;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        public const uint CR_SUCCESS = 0;
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
        public static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_POWERSETTINGCHANGE = 0x8013;
        public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr overlapped);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_Interface_List_Size(out uint size, ref Guid classGuid, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_Interface_List(ref Guid classGuid, string deviceId, char[] buffer, uint bufferLen, uint flags);

        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage; public ushort UsagePage;
            public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
        }

        // Explicit layout of HIDP_VALUE_CAPS (72 bytes); only the fields we use are declared.
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        public struct HIDP_VALUE_CAPS
        {
            [FieldOffset(0)] public ushort UsagePage;
            [FieldOffset(2)] public byte ReportID;
            [FieldOffset(12)] public byte IsRange;
            [FieldOffset(15)] public byte IsAbsolute;
            [FieldOffset(18)] public ushort BitSize;
            [FieldOffset(20)] public ushort ReportCount;
            [FieldOffset(40)] public int LogicalMin;
            [FieldOffset(44)] public int LogicalMax;
            [FieldOffset(56)] public ushort UsageMin;
            [FieldOffset(58)] public ushort UsageMax;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public byte Data; }

        public static List<string> EnumerateHidInterfaces()
        {
            var result = new List<string>();
            Guid g = GUID_DEVINTERFACE_HID;
            uint size;
            if (CM_Get_Device_Interface_List_Size(out size, ref g, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || size == 0) return result;
            var buf = new char[size];
            if (CM_Get_Device_Interface_List(ref g, null, buf, size, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS) return result;
            var sb = new StringBuilder();
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == '\0') { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Length = 0; } else break; }
                else sb.Append(buf[i]);
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ Backlight HID device
    sealed class BacklightDevice : IDisposable
    {
        public const ushort UsagePageConsumer = 0x0C;
        public const ushort UsageKeyboardBacklight = 0x07;
        public const ushort UsageSetLevel = 0x7B;
        public const ushort UsageLevelSuggestion = 0x517;

        public string Path;
        public string Name = "keyboard backlight";
        public ushort Vid, Pid;
        public byte SetLevelReportId;
        public int OutputReportLength, FeatureReportLength;
        public int LogicalMin, LogicalMax;
        public byte SuggestionReportId; public int SuggestionCount;
        public byte InitialLevelReportId;
        public int[] Suggestions = new int[0];
        SafeFileHandle _h;

        public bool IsOpen { get { return _h != null && !_h.IsInvalid && !_h.IsClosed; } }

        public static List<BacklightDevice> FindAll(Action<string> log)
        {
            var found = new List<BacklightDevice>();
            foreach (var path in Native.EnumerateHidInterfaces())
            {
                BacklightDevice d = null;
                try { d = TryOpen(path, log); } catch (Exception ex) { log("Error probing " + path + ": " + ex.Message); }
                if (d != null) found.Add(d);
            }
            return found;
        }

        static BacklightDevice TryOpen(string path, Action<string> log)
        {
            // Cheap pre-filter: the interface path contains no usage info, so we must open each collection.
            // Keyboard/mouse collections refuse user-mode opens; open with no access rights first to read caps.
            var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            IntPtr pp = IntPtr.Zero;
            try
            {
                if (!Native.HidD_GetPreparsedData(h, out pp)) return null;
                Native.HIDP_CAPS caps;
                if (Native.HidP_GetCaps(pp, out caps) != Native.HIDP_STATUS_SUCCESS) return null;
                if (caps.UsagePage != UsagePageConsumer || caps.Usage != UsageKeyboardBacklight) return null;

                var d = new BacklightDevice();
                d.Path = path;
                d.OutputReportLength = caps.OutputReportByteLength;
                d.FeatureReportLength = caps.FeatureReportByteLength;

                if (caps.NumberOutputValueCaps > 0)
                {
                    var vc = new Native.HIDP_VALUE_CAPS[caps.NumberOutputValueCaps]; ushort n = caps.NumberOutputValueCaps;
                    Native.HidP_GetValueCaps(Native.HidP_Output, vc, ref n, pp);
                    for (int i = 0; i < n; i++)
                        if (vc[i].UsagePage == UsagePageConsumer && vc[i].IsRange == 0 && vc[i].UsageMin == UsageSetLevel && vc[i].BitSize == 8)
                        { d.SetLevelReportId = vc[i].ReportID; d.LogicalMin = vc[i].LogicalMin; d.LogicalMax = vc[i].LogicalMax; }
                }
                if (d.LogicalMax <= d.LogicalMin || d.OutputReportLength < 2)
                {
                    log("Backlight collection at " + path + " has no usable Set Level output report; skipping.");
                    return null;
                }
                if (caps.NumberFeatureValueCaps > 0)
                {
                    var vc = new Native.HIDP_VALUE_CAPS[caps.NumberFeatureValueCaps]; ushort n = caps.NumberFeatureValueCaps;
                    Native.HidP_GetValueCaps(Native.HidP_Feature, vc, ref n, pp);
                    for (int i = 0; i < n; i++)
                    {
                        if (vc[i].UsagePage != UsagePageConsumer || vc[i].IsRange != 0 || vc[i].BitSize != 8) continue;
                        if (vc[i].UsageMin == UsageLevelSuggestion) { d.SuggestionReportId = vc[i].ReportID; d.SuggestionCount = vc[i].ReportCount; }
                        else if (vc[i].UsageMin == UsageSetLevel) { d.InitialLevelReportId = vc[i].ReportID; }
                    }
                }
                h.Close();
                d._h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (d._h.IsInvalid)
                    d._h = Native.CreateFile(path, Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (d._h.IsInvalid)
                {
                    log("Found backlight collection but could not open it for writing (error " + Marshal.GetLastWin32Error() + "): " + path);
                    return null;
                }
                var attr = new Native.HIDD_ATTRIBUTES(); attr.Size = 12;
                if (Native.HidD_GetAttributes(d._h, ref attr)) { d.Vid = attr.VendorID; d.Pid = attr.ProductID; }
                var pbuf = new byte[256];
                if (Native.HidD_GetProductString(d._h, pbuf, pbuf.Length))
                {
                    var s = Encoding.Unicode.GetString(pbuf); int z = s.IndexOf('\0'); if (z >= 0) s = s.Substring(0, z);
                    // The Surface mini-driver returns a raw USB string descriptor (bLength, bDescriptorType=3) - strip that header.
                    if (s.Length > 0 && (s[0] >> 8) == 3) s = s.Substring(1);
                    s = s.Trim(); if (s.Length >= 8) d.Name = s; else if (attr.VendorID == 0x045E) d.Name = "Surface keyboard";
                }
                d.ReadSuggestions();
                log(string.Format("Backlight device: {0} VID={1:X4} PID={2:X4} setLevelReport={3} range={4}..{5} suggestions=[{6}] initialLevelReport={7} path={8}",
                    d.Name, d.Vid, d.Pid, d.SetLevelReportId, d.LogicalMin, d.LogicalMax, string.Join(",", Array.ConvertAll(d.Suggestions, x => x.ToString())), d.InitialLevelReportId, path));
                return d;
            }
            finally
            {
                if (pp != IntPtr.Zero) Native.HidD_FreePreparsedData(pp);
                if (!h.IsClosed) h.Close();
            }
        }

        void ReadSuggestions()
        {
            if (SuggestionReportId == 0 || FeatureReportLength < 2) return;
            var buf = new byte[FeatureReportLength]; buf[0] = SuggestionReportId;
            if (!Native.HidD_GetFeature(_h, buf, buf.Length)) return;
            var list = new List<int>();
            for (int i = 0; i < SuggestionCount && 1 + i < buf.Length; i++)
            {
                int v = buf[1 + i];
                if (v >= LogicalMin && v <= LogicalMax && !list.Contains(v)) list.Add(v);
            }
            list.Sort();
            Suggestions = list.ToArray();
        }

        /// <summary>Level the device itself reports (what the host last set). Null if unsupported/failed.</summary>
        public int? ReadDeviceLevel()
        {
            if (InitialLevelReportId == 0 || FeatureReportLength < 2 || !IsOpen) return null;
            var buf = new byte[FeatureReportLength]; buf[0] = InitialLevelReportId;
            if (!Native.HidD_GetFeature(_h, buf, buf.Length)) return null;
            return buf[1];
        }

        /// <summary>
        /// Sends the Set Level output report. The Surface HID mini-driver only implements the
        /// IOCTL_HID_WRITE_REPORT path (WriteFile); HidD_SetOutputReport returns ERROR_NOT_SUPPORTED (50)
        /// on it, so WriteFile is tried first and HidD_SetOutputReport is kept as a fallback for other keyboards.
        /// </summary>
        public bool SetLevel(int level, out int error)
        {
            error = 0;
            if (!IsOpen) { error = -1; return false; }
            if (level < LogicalMin) level = LogicalMin; if (level > LogicalMax) level = LogicalMax;
            var buf = new byte[OutputReportLength]; buf[0] = SetLevelReportId; buf[1] = (byte)level;
            uint written;
            if (Native.WriteFile(_h, buf, (uint)buf.Length, out written, IntPtr.Zero)) return true;
            int e1 = Marshal.GetLastWin32Error();
            if (Native.HidD_SetOutputReport(_h, buf, buf.Length)) return true;
            error = e1 != 0 ? e1 : Marshal.GetLastWin32Error();
            return false;
        }

        /// <summary>Registry key name Windows uses to persist this device's manual brightness.</summary>
        public string StateKeyName
        {
            get
            {
                var p = Path;
                if (p.StartsWith(@"\\?\")) p = p.Substring(4);
                return p.Replace('\\', '#');
            }
        }

        public void Dispose() { if (_h != null && !_h.IsClosed) _h.Close(); }
    }

    // ------------------------------------------------------------------ Settings
    sealed class Settings
    {
        const string KeyPath = @"Software\SurfaceBacklightKeeper";
        public bool Enabled = true;
        public int IntervalSeconds = 10;
        public int FixedLevel = -1;            // -1 = follow Windows setting
        public bool DipAndRestore = false;     // fallback keep-alive method
        public bool PauseWhenDisplayOff = true;
        public bool PauseWhenLocked = true;
        public bool PauseOnBattery = false;
        public bool Logging = false;
        public bool FirstRun;                  // no settings key existed when the app started

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) { s.FirstRun = true; return s; }
                    s.Enabled = ReadInt(k, "Enabled", 1) != 0;
                    // The firmware timeout was observed to be under 25 s, so anything above 15 s risks a gap.
                    s.IntervalSeconds = Math.Max(3, Math.Min(15, ReadInt(k, "IntervalSeconds", 10)));
                    s.FixedLevel = ReadInt(k, "FixedLevel", -1);
                    s.DipAndRestore = ReadInt(k, "DipAndRestore", 0) != 0;
                    s.PauseWhenDisplayOff = ReadInt(k, "PauseWhenDisplayOff", 1) != 0;
                    s.PauseWhenLocked = ReadInt(k, "PauseWhenLocked", 1) != 0;
                    s.PauseOnBattery = ReadInt(k, "PauseOnBattery", 0) != 0;
                    s.Logging = ReadInt(k, "Logging", 0) != 0;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Enabled", Enabled ? 1 : 0);
                    k.SetValue("IntervalSeconds", IntervalSeconds);
                    k.SetValue("FixedLevel", FixedLevel);
                    k.SetValue("DipAndRestore", DipAndRestore ? 1 : 0);
                    k.SetValue("PauseWhenDisplayOff", PauseWhenDisplayOff ? 1 : 0);
                    k.SetValue("PauseWhenLocked", PauseWhenLocked ? 1 : 0);
                    k.SetValue("PauseOnBattery", PauseOnBattery ? 1 : 0);
                    k.SetValue("Logging", Logging ? 1 : 0);
                }
            }
            catch { }
        }

        static int ReadInt(RegistryKey k, string name, int def)
        {
            object v = k.GetValue(name); if (v is int) return (int)v; return def;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "SurfaceBacklightKeeper";
        public static bool IsStartWithWindows()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) { return k != null && k.GetValue(RunName) != null; } } catch { return false; }
        }
        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Tray application
    sealed class KeeperForm : Form
    {
        readonly Settings _s;
        readonly NotifyIcon _tray;
        readonly System.Windows.Forms.Timer _timer;
        System.Windows.Forms.Timer _clickTimer;
        readonly List<BacklightDevice> _devices = new List<BacklightDevice>();
        readonly Dictionary<string, int> _lastKnownLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IntPtr _powerNotify = IntPtr.Zero;
        bool _displayOff, _locked, _suspended;
        int _lastWindowsLevel = -1;   // last ManualBrightnessNits we saw (to detect the user pressing the key to "off")
        bool _respectKeyOff;          // user turned the light off with the keyboard key while we were running
        DateTime _lastSend = DateTime.MinValue; int _lastSentLevel = -1; string _lastError = "";
        int _consecutiveFailures;
        string _lastLogged = ""; DateTime _lastLogTime = DateTime.MinValue;   // per-write log lines only on change or every 10 min
        Icon _iconOn, _iconOff;
        ToolStripMenuItem _miEnabled, _miLevel, _miInterval, _miMethodResend, _miMethodDip, _miPauseDisplay, _miPauseLock, _miPauseBattery, _miStartup, _miLogging, _miStatus;

        public KeeperForm()
        {
            _s = Settings.Load();
            Text = "Surface Keyboard Backlight Keeper"; ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            CreateHandle();

            _iconOn = MakeIcon(Color.FromArgb(255, 214, 92), true);
            _iconOff = MakeIcon(Color.FromArgb(140, 140, 140), false);
            _tray = new NotifyIcon(); _tray.Icon = _iconOn; _tray.Visible = true; _tray.Text = "Surface Keyboard Backlight Keeper";
            _tray.ContextMenuStrip = BuildMenu();
            // Left click opens the menu, double-click toggles. The menu is shown after the double-click interval
            // so a double-click can still be told apart from a single click.
            _clickTimer = new System.Windows.Forms.Timer(); _clickTimer.Interval = Math.Max(150, SystemInformation.DoubleClickTime);
            _clickTimer.Tick += delegate { _clickTimer.Stop(); ShowTrayMenu(); };
            _tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _clickTimer.Start(); };
            _tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); ToggleEnabled(); } };

            _timer = new System.Windows.Forms.Timer(); _timer.Interval = _s.IntervalSeconds * 1000; _timer.Tick += delegate { Tick(); };

            Guid g = Native.GUID_CONSOLE_DISPLAY_STATE;
            _powerNotify = Native.RegisterPowerSettingNotification(Handle, ref g, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Log("---- Surface Keyboard Backlight Keeper " + Application.ProductVersion + " starting (interval " + _s.IntervalSeconds + " s, level " + (_s.FixedLevel < 0 ? "follow Windows" : _s.FixedLevel.ToString()) + ", method " + (_s.DipAndRestore ? "dip-and-restore" : "resend") + ")");
            Rescan();
            RefreshMenu();
            _timer.Start();
            Tick();

            if (_devices.Count == 0)
                _tray.ShowBalloonTip(8000, "Surface Keyboard Backlight Keeper", "No keyboard backlight device was found. This needs a Surface with a backlit keyboard on Windows 11 25H2 or later.", ToolTipIcon.Warning);
            else if (_s.FirstRun)
            {
                _s.Save();
                _tray.ShowBalloonTip(6000, "Surface Keyboard Backlight Keeper is running", "It keeps the keyboard backlight on once it is lit. Touch the trackpad or a key to light it. Click the tray icon for options.", ToolTipIcon.Info);
            }
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        // ---------------- device management
        void Rescan()
        {
            foreach (var d in _devices) d.Dispose();
            _devices.Clear();
            _devices.AddRange(BacklightDevice.FindAll(Log));
            _lastLogged = "";
            if (_devices.Count == 0) Log("No HID keyboard-backlight collection found. Is this a Surface with a backlit keyboard on Windows 11 25H2 (build 26200.7922+)?");
            foreach (var d in _devices)
            {
                int? lvl = d.ReadDeviceLevel();
                if (lvl.HasValue && lvl.Value > 0) _lastKnownLevel[d.Path] = lvl.Value;
            }
            _consecutiveFailures = 0;
        }

        /// <summary>Brightness Windows last applied to this device, read from the Lighting registry state.</summary>
        internal static int? ReadWindowsLevel(BacklightDevice d)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Lighting\Backlight\State\" + d.StateKeyName))
                {
                    if (k == null) return null;
                    object v = k.GetValue("ManualBrightnessNits");
                    if (v is int) return (int)v;
                }
            }
            catch { }
            return null;
        }

        int ResolveTargetLevel(BacklightDevice d, out string source)
        {
            if (_s.FixedLevel >= 0) { source = "fixed"; return Math.Min(_s.FixedLevel, d.LogicalMax); }
            int? w = ReadWindowsLevel(d);
            if (w.HasValue)
            {
                if (w.Value > 0)
                {
                    _lastKnownLevel[d.Path] = w.Value; _lastWindowsLevel = w.Value; _respectKeyOff = false;
                    source = "Windows setting"; return w.Value;
                }
                // Windows has the backlight at "off". If that changed while we were running, the user pressed the
                // backlight key to turn it off - respect that. Otherwise (app start / re-enable) "enabled" means "on".
                if (_lastWindowsLevel > 0) _respectKeyOff = true;
                _lastWindowsLevel = 0;
                if (_respectKeyOff) { source = "off (backlight key)"; return 0; }
                source = "last used (Windows has it off)";
                return LastKnownOrDefault(d);
            }
            int? dl = d.ReadDeviceLevel();
            if (dl.HasValue && dl.Value > 0) { source = "device"; _lastKnownLevel[d.Path] = dl.Value; return dl.Value; }
            source = "last used";
            return LastKnownOrDefault(d);
        }

        int LastKnownOrDefault(BacklightDevice d)
        {
            int last;
            if (_lastKnownLevel.TryGetValue(d.Path, out last) && last > 0) return last;
            int? dl = d.ReadDeviceLevel();
            if (dl.HasValue && dl.Value > 0) return dl.Value;
            var nonZero = new List<int>(); foreach (int s in d.Suggestions) if (s > 0) nonZero.Add(s);
            if (nonZero.Count > 0) return nonZero[nonZero.Count / 2];   // middle preset (6 nits on the Surface Laptop Studio 2)
            return d.LogicalMax;
        }

        /// <summary>True while "SurfaceBacklightKeeper.exe --test" is cycling the levels, so the tray app does not interfere.</summary>
        static bool SelfTestRunning()
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(Program.SelfTestEventName, out ev)) return false;
            using (ev) return ev.WaitOne(0);
        }

        bool Paused(out string why)
        {
            why = null;
            if (!_s.Enabled) { why = "disabled"; return true; }
            if (SelfTestRunning()) { why = "self test running"; return true; }
            if (_suspended) { why = "system suspended"; return true; }
            if (_s.PauseWhenDisplayOff && _displayOff) { why = "display off"; return true; }
            if (_s.PauseWhenLocked && _locked) { why = "session locked"; return true; }
            if (_s.PauseOnBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) { why = "on battery"; return true; }
            return false;
        }

        void Tick()
        {
            string why;
            if (Paused(out why)) { UpdateTray(); return; }
            if (_devices.Count == 0 || _consecutiveFailures >= 3)
            {
                Rescan();
                if (_devices.Count == 0) { UpdateTray(); return; }
            }
            foreach (var d in _devices)
            {
                string src;
                int level = ResolveTargetLevel(d, out src);
                if (level <= 0) { _lastSentLevel = 0; _lastError = ""; continue; } // user turned the backlight off with the key - respect it
                int err; bool ok;
                string how;
                if (_s.DipAndRestore)
                {
                    int dip = level > d.LogicalMin + 1 ? level - 1 : Math.Min(level + 1, d.LogicalMax);
                    ok = d.SetLevel(dip, out err);
                    if (ok) { Thread.Sleep(20); ok = d.SetLevel(level, out err); }
                    how = "dip via " + dip;
                }
                else { ok = d.SetLevel(level, out err); how = "resend"; }

                if (ok)
                {
                    _lastSend = DateTime.Now; _lastSentLevel = level; _lastError = ""; _consecutiveFailures = 0;
                    if (_s.Logging)
                    {
                        string line = "Sending level " + level + " (" + src + ", " + how + ") to " + d.Name;
                        if (line != _lastLogged || (DateTime.Now - _lastLogTime).TotalMinutes >= 10) { Log(line + " every " + _s.IntervalSeconds + " s"); _lastLogged = line; _lastLogTime = DateTime.Now; }
                    }
                }
                else { _consecutiveFailures++; _lastError = "write failed, error " + err; _lastLogged = ""; Log("Set Level failed (error " + err + ") on " + d.Name + "; failures=" + _consecutiveFailures); }
            }
            UpdateTray();
        }

        // ---------------- system events
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_POWERBROADCAST)
            {
                int evt = (int)m.WParam.ToInt64();
                if (evt == Native.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    var ps = (Native.POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(Native.POWERBROADCAST_SETTING));
                    if (ps.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        bool wasOff = _displayOff;
                        _displayOff = ps.Data == 0;
                        if (_s.Logging) Log("Display state -> " + (ps.Data == 0 ? "off" : ps.Data == 2 ? "dimmed" : "on"));
                        // The embedded controller relights the keyboard when the display comes back; re-arm right away.
                        if (wasOff && !_displayOff) Tick(); else UpdateTray();
                    }
                }
            }
            base.WndProc(ref m);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect) _locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect) { _locked = false; Tick(); return; }
            UpdateTray();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                _suspended = false;
                // Give the SAM keyboard a moment to come back, then re-open it.
                var t = new System.Windows.Forms.Timer(); t.Interval = 4000;
                t.Tick += delegate { t.Stop(); t.Dispose(); Rescan(); Tick(); };
                t.Start();
            }
            else if (e.Mode == PowerModes.Suspend) { _suspended = true; UpdateTray(); }
            else if (e.Mode == PowerModes.StatusChange) Tick();   // AC/battery change may start or end the "on battery" pause
        }

        // ---------------- tray UI
        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            _miStatus = new ToolStripMenuItem("Status"); _miStatus.Enabled = false; m.Items.Add(_miStatus);
            m.Items.Add(new ToolStripSeparator());
            _miEnabled = new ToolStripMenuItem("Keep keyboard backlight on", null, delegate { ToggleEnabled(); }); _miEnabled.CheckOnClick = false; m.Items.Add(_miEnabled);
            _miLevel = new ToolStripMenuItem("Brightness"); m.Items.Add(_miLevel);
            _miInterval = new ToolStripMenuItem("Refresh every"); m.Items.Add(_miInterval);
            foreach (int sec in new[] { 5, 10, 15 })
            {
                int s = sec; var mi = new ToolStripMenuItem(s + " seconds", null, delegate { _s.IntervalSeconds = s; _timer.Interval = s * 1000; _s.Save(); RefreshMenu(); Tick(); });
                _miInterval.DropDownItems.Add(mi);
            }
            var method = new ToolStripMenuItem("Keep-alive method"); m.Items.Add(method);
            _miMethodResend = new ToolStripMenuItem("Re-send the current level (default, no flicker)", null, delegate { _s.DipAndRestore = false; _s.Save(); RefreshMenu(); });
            _miMethodDip = new ToolStripMenuItem("Tiny dip and restore on every refresh (only if the light still times out)", null, delegate { _s.DipAndRestore = true; _s.Save(); RefreshMenu(); });
            method.DropDownItems.Add(_miMethodResend); method.DropDownItems.Add(_miMethodDip);
            var pause = new ToolStripMenuItem("Pause when"); m.Items.Add(pause);
            _miPauseDisplay = new ToolStripMenuItem("Display is off", null, delegate { _s.PauseWhenDisplayOff = !_s.PauseWhenDisplayOff; _s.Save(); RefreshMenu(); Tick(); });
            _miPauseLock = new ToolStripMenuItem("Screen is locked", null, delegate { _s.PauseWhenLocked = !_s.PauseWhenLocked; _s.Save(); RefreshMenu(); Tick(); });
            _miPauseBattery = new ToolStripMenuItem("Running on battery", null, delegate { _s.PauseOnBattery = !_s.PauseOnBattery; _s.Save(); RefreshMenu(); Tick(); });
            pause.DropDownItems.Add(_miPauseDisplay); pause.DropDownItems.Add(_miPauseLock); pause.DropDownItems.Add(_miPauseBattery);
            m.Items.Add(new ToolStripSeparator());
            _miStartup = new ToolStripMenuItem("Start with Windows", null, delegate { Settings.SetStartWithWindows(!Settings.IsStartWithWindows()); RefreshMenu(); }); m.Items.Add(_miStartup);
            _miLogging = new ToolStripMenuItem("Write log file", null, delegate { _s.Logging = !_s.Logging; _s.Save(); RefreshMenu(); if (_s.Logging) Log("Logging enabled"); }); m.Items.Add(_miLogging);
            m.Items.Add(new ToolStripMenuItem("Open log folder", null, delegate { try { Directory.CreateDirectory(LogDir); Process.Start("explorer.exe", LogDir); } catch { } }));
            m.Items.Add(new ToolStripMenuItem("Re-detect keyboard", null, delegate { Rescan(); RefreshMenu(); Tick(); }));
            m.Items.Add(new ToolStripSeparator());
            var about = new ToolStripMenuItem("About"); m.Items.Add(about);
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            foreach (string line in new[] {
                "Surface Keyboard Backlight Keeper",
                "Version " + ver.Major + "." + ver.Minor + "." + ver.Build,
                "Made by Claude, prompted by Oracooll",
                "Open source (MIT): github.com/Oracooll/SurfaceKeyboardBacklightKeeper",
                "-",
                "Keeps the Surface keyboard backlight from switching off after",
                "~30 s without typing. Every few seconds it re-sends the brightness",
                "level through the same HID Keyboard Backlight interface Windows 11",
                "uses, which re-arms the keyboard firmware's idle timer.",
                "-",
                "The firmware only lights the keyboard on a key press or trackpad",
                "touch, so touch the trackpad once; the app keeps it on from there.",
                "The keyboard's backlight key still changes the level or turns it off." })
            {
                if (line == "-") { about.DropDownItems.Add(new ToolStripSeparator()); continue; }
                var li = new ToolStripMenuItem(line); li.Enabled = false; about.DropDownItems.Add(li);
            }
            m.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Close(); }));
            m.Opening += delegate { RefreshMenu(); };
            return m;
        }

        /// <summary>Opens the tray menu exactly as a right-click would (same position, closes on focus loss).</summary>
        void ShowTrayMenu()
        {
            RefreshMenu();
            var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) { try { mi.Invoke(_tray, null); return; } catch { } }
            _tray.ContextMenuStrip.Show(Cursor.Position);
        }

        void ToggleEnabled()
        {
            _s.Enabled = !_s.Enabled; _s.Save();
            if (_s.Enabled) _respectKeyOff = false;   // enabling means "on", even if the key had turned it off earlier
            RefreshMenu(); Tick();
        }

        void RefreshMenu()
        {
            _miEnabled.Checked = _s.Enabled;
            foreach (ToolStripMenuItem mi in _miInterval.DropDownItems) mi.Checked = mi.Text.StartsWith(_s.IntervalSeconds + " ");
            _miMethodResend.Checked = !_s.DipAndRestore; _miMethodDip.Checked = _s.DipAndRestore;
            _miPauseDisplay.Checked = _s.PauseWhenDisplayOff; _miPauseLock.Checked = _s.PauseWhenLocked; _miPauseBattery.Checked = _s.PauseOnBattery;
            _miStartup.Checked = Settings.IsStartWithWindows(); _miLogging.Checked = _s.Logging;

            _miLevel.DropDownItems.Clear();
            var follow = new ToolStripMenuItem("Follow Windows setting (use the keyboard's backlight key)", null, delegate { _s.FixedLevel = -1; _s.Save(); RefreshMenu(); Tick(); });
            follow.Checked = _s.FixedLevel < 0; _miLevel.DropDownItems.Add(follow);
            if (_devices.Count > 0)
            {
                var d = _devices[0];
                var levels = new List<int>(d.Suggestions);
                if (levels.Count == 0) { for (int l = d.LogicalMin; l <= d.LogicalMax; l++) levels.Add(l); }
                if (!levels.Contains(d.LogicalMax)) levels.Add(d.LogicalMax);
                int idx = 0;
                foreach (int lv in levels)
                {
                    if (lv <= 0) continue;
                    int l = lv; idx++;
                    var mi = new ToolStripMenuItem("Always level " + idx + "  (" + l + " nits)", null, delegate { _s.FixedLevel = l; _s.Save(); RefreshMenu(); Tick(); });
                    mi.Checked = _s.FixedLevel == l; _miLevel.DropDownItems.Add(mi);
                }
            }
            UpdateTray();
        }

        void UpdateTray()
        {
            string why; bool paused = Paused(out why);
            string status;
            if (_devices.Count == 0) status = "No keyboard backlight device found";
            else if (paused) status = "Paused: " + why;
            else if (_lastError.Length > 0) status = "Error: " + _lastError;
            else if (_lastSentLevel == 0) status = "Off via the backlight key - press it again to turn it back on";
            else if (_lastSentLevel > 0) status = "Keeping backlight on at " + _lastSentLevel + " nits (last sent " + _lastSend.ToString("HH:mm:ss") + ")";
            else status = "Starting...";
            _miStatus.Text = status;
            string tip = "Backlight Keeper: " + status; if (tip.Length > 63) tip = tip.Substring(0, 60) + "..."; // NotifyIcon.Text max is 63 chars on .NET Framework
            _tray.Text = tip;
            _tray.Icon = (paused || _devices.Count == 0 || _lastError.Length > 0) ? _iconOff : _iconOn;
        }

        static Icon MakeIcon(Color glow, bool lit)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    if (lit) using (var b = new SolidBrush(Color.FromArgb(90, glow))) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var b = new SolidBrush(Color.FromArgb(50, 50, 50))) g.FillRectangle(b, 3, 10, 26, 14);
                    using (var p = new Pen(glow, 1.5f)) g.DrawRectangle(p, 3, 10, 26, 14);
                    using (var b = new SolidBrush(glow))
                    {
                        for (int r = 0; r < 2; r++) for (int c = 0; c < 6; c++) g.FillRectangle(b, 6 + c * 4, 13 + r * 4, 2, 2);
                        g.FillRectangle(b, 8, 21, 16, 2);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); } finally { Native.DestroyIcon(h); }
            }
        }

        // ---------------- logging
        static string LogDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceBacklightKeeper"); } }
        static readonly object _logLock = new object();
        void Log(string msg)
        {
            // Startup/device/error lines are always written; per-tick lines only when logging is enabled (callers check).
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(LogDir);
                    var f = System.IO.Path.Combine(LogDir, "keeper.log");
                    if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024)
                    {
                        var old = System.IO.Path.Combine(LogDir, "keeper.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(f, old);   // keep one previous log instead of discarding history
                    }
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (_powerNotify != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_powerNotify);
            foreach (var d in _devices) d.Dispose();
            _tray.Visible = false; _tray.Dispose();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        public const string SelfTestEventName = "Local\\SurfaceBacklightKeeper.SelfTest";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { LogCrash("UI thread exception", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { LogCrash("Unhandled exception", e.ExceptionObject as Exception); };
            if (args.Length > 0 && (args[0] == "--test" || args[0] == "/test")) { RunSelfTest(); return; }

            bool created;
            using (var mutex = new Mutex(true, "Local\\SurfaceBacklightKeeper.SingleInstance", out created))
            {
                if (!created) return;
                try { Application.Run(new KeeperForm()); }
                catch (Exception ex) { LogCrash("Fatal", ex); throw; }
            }
        }

        static void LogCrash(string what, Exception ex)
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceBacklightKeeper");
                Directory.CreateDirectory(dir);
                File.AppendAllText(System.IO.Path.Combine(dir, "keeper.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what + ": " + (ex == null ? "(null)" : ex.ToString()) + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Visibly steps the backlight through every level so the user can confirm the app controls it.</summary>
        static void RunSelfTest()
        {
            var sb = new StringBuilder();
            var devices = BacklightDevice.FindAll(delegate(string s) { sb.AppendLine(s); });
            if (devices.Count == 0)
            {
                MessageBox.Show("No HID keyboard-backlight collection was found on this PC.\r\n\r\n" + sb, "Surface Keyboard Backlight Keeper - self test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var d = devices[0];
            int? restore = KeeperForm.ReadWindowsLevel(d);
            if (!restore.HasValue) restore = d.ReadDeviceLevel();
            // Never write level 0: the firmware would switch the light off and only a key press or trackpad touch brings it back.
            var levels = new List<int>(); foreach (int s in d.Suggestions) if (s > 0) levels.Add(s);
            if (levels.Count < 2) { levels.Clear(); levels.Add(Math.Max(1, d.LogicalMin)); levels.Add((d.LogicalMin + d.LogicalMax) / 2); levels.Add(d.LogicalMax); }
            var seq = new List<int>(levels); levels.Reverse(); seq.AddRange(levels); // up then down
            var log = new StringBuilder();
            int failures = 0, err;
            using (var pauseTray = new EventWaitHandle(true, EventResetMode.ManualReset, SelfTestEventName))
            {
                foreach (int l in seq)
                {
                    bool ok = d.SetLevel(l, out err);
                    log.AppendLine("Set level " + l + " nits -> " + (ok ? "OK" : "FAILED (error " + err + ")"));
                    if (!ok) failures++;
                    Thread.Sleep(900);
                }
                if (restore.HasValue) d.SetLevel(restore.Value, out err);
                pauseTray.Reset();
            }
            d.Dispose();
            MessageBox.Show("Device: " + d.Name + " (VID " + d.Vid.ToString("X4") + " PID " + d.Pid.ToString("X4") + ")\r\n" +
                "Levels reported by the keyboard: " + string.Join(", ", Array.ConvertAll(d.Suggestions, delegate(int x) { return x.ToString(); })) + " nits\r\n\r\n" +
                log + "\r\n" + (failures == 0 ? "If you saw the keyboard light step up and back down, the app can control the backlight.\r\n(The light must already be on: touch the trackpad, then run the test.)" : failures + " write(s) failed - see the log folder for details.") +
                (restore.HasValue ? "\r\nRestored Windows' level: " + restore.Value + " nits." : ""),
                "Surface Keyboard Backlight Keeper - self test", MessageBoxButtons.OK, failures == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
