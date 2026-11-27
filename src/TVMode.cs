// PC–TV Switcher - switch between two display profiles ("PC" and "TV"). Each profile says which
// screens are on and, optionally, which sound output to use.
// Uses the Windows CCD API (QueryDisplayConfig / SetDisplayConfig) and Core Audio.
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x csc.exe).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle(TVMode.Program.AppName)]
[assembly: AssemblyProduct(TVMode.Program.AppName)]
[assembly: AssemblyDescription("Switch Windows displays and sound between PC and TV profiles.")]

namespace TVMode
{
    // ------------------------------------------------------------------ Win32 display interop
    static class Native
    {
        public const uint QDC_ALL_PATHS = 0x1;
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

        public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;

        public const uint SDC_TOPOLOGY_SUPPLIED = 0x10;
        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        public const uint SDC_APPLY = 0x80;
        public const uint SDC_SAVE_TO_DATABASE = 0x200;
        public const uint SDC_ALLOW_CHANGES = 0x400;
        public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;

        public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
        public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

        public const int ERROR_SUCCESS = 0;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        public const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
            public override string ToString() { return HighPart.ToString("X8") + LowPart.ToString("X8"); }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable; // BOOL
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // We never interpret mode data, so the 48-byte union is kept as opaque fields.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public ulong u0, u1, u2, u3, u4, u5;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1;
        public const uint MOD_CONTROL = 0x2;
        public const uint MOD_SHIFT = 0x4;
        public const uint MOD_WIN = 0x8;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);

        public const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int DM_DISPLAYFREQUENCY = 0x400000;
        public const uint CDS_UPDATEREGISTRY = 0x1;
        public const int DISP_CHANGE_SUCCESSFUL = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Grey hint text shown in an empty text box.</summary>
        public static void SetPlaceholder(TextBox box, string text)
        {
            EventHandler apply = delegate { SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, text); };
            if (box.IsHandleCreated) apply(box, EventArgs.Empty);
            else box.HandleCreated += apply;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        /// <summary>Dark title bar (Windows 10 20H1+ / 11); harmless where unsupported.</summary>
        public static void UseDarkTitleBar(Form form)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4) != 0)
                    DwmSetWindowAttribute(form.Handle, 19 /* pre-20H1 value */, ref on, 4);
            }
            catch { }
        }

        /// <summary>Dark scrollbars / dropdown parts for a standard control (theme names Windows itself uses).</summary>
        public static void UseDarkControlTheme(Control c, bool combo)
        {
            EventHandler apply = delegate { try { SetWindowTheme(c.Handle, combo ? "DarkMode_CFD" : "DarkMode_Explorer", null); } catch { } };
            if (c.IsHandleCreated) apply(c, EventArgs.Empty);
            else c.HandleCreated += apply;
        }

        /// <summary>DPI of the main screen (96 = 100%).</summary>
        public static int PrimaryDpi()
        {
            try
            {
                uint x, y;
                IntPtr mon = MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
                if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out x, out y) == 0 && x > 0) return (int)x;
            }
            catch { }
            return 96;
        }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        /// <summary>Work area of the main screen, read fresh (WinForms' Screen class can be stale right after a switch).</summary>
        public static Rectangle PrimaryWorkArea()
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            IntPtr mon = MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return Screen.PrimaryScreen.WorkingArea;
            return Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
        }

        public static string ToBase64<T>(T value) where T : struct
        {
            int n = Marshal.SizeOf(typeof(T));
            IntPtr p = Marshal.AllocHGlobal(n);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                var bytes = new byte[n];
                Marshal.Copy(p, bytes, 0, n);
                return Convert.ToBase64String(bytes);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static T FromBase64<T>(string text) where T : struct
        {
            var bytes = Convert.FromBase64String(text);
            int n = Marshal.SizeOf(typeof(T));
            if (bytes.Length != n) throw new FormatException("Bad saved layout data.");
            IntPtr p = Marshal.AllocHGlobal(n);
            try
            {
                Marshal.Copy(bytes, 0, p, n);
                return (T)Marshal.PtrToStructure(p, typeof(T));
            }
            finally { Marshal.FreeHGlobal(p); }
        }
    }

    // ------------------------------------------------------------------ Core Audio interop
    static class AudioNative
    {
        public const int eRender = 0;
        public const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
        public const uint DEVICE_STATE_ACTIVE = 0x1;
        public const uint DEVICE_STATE_UNPLUGGED = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // PROPVARIANT: 16 bytes on x86, 24 on x64 - this layout matches both.
        [StructLayout(LayoutKind.Sequential)]
        public struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr p;
            public IntPtr p2;
        }

        public static PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        // Undocumented but long-standing (Windows 7 - 11) interface used by every
        // "switch default audio device" tool; it is what the Windows sound panel calls.
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        public class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
            [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            [PreserveSig] int ResetDeviceFormat(IntPtr a);
            [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
            [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
        }
    }

    class AudioDevice
    {
        public string Id;
        public string Name;
        public bool Active;
    }

    static class AudioManager
    {
        static string ReadName(AudioNative.IMMDevice dev)
        {
            AudioNative.IPropertyStore store;
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
            try
            {
                var key = AudioNative.PKEY_Device_FriendlyName;
                AudioNative.PROPVARIANT pv;
                if (store.GetValue(ref key, out pv) != 0) return null;
                try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null; }
                finally { AudioNative.PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        /// <summary>Speaker/headphone/HDMI outputs that are working now or just unplugged.</summary>
        public static List<AudioDevice> GetOutputs()
        {
            var list = new List<AudioDevice>();
            var en = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumeratorCom();
            try
            {
                AudioNative.IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(AudioNative.eRender,
                        AudioNative.DEVICE_STATE_ACTIVE | AudioNative.DEVICE_STATE_UNPLUGGED, out col) != 0) return list;
                try
                {
                    uint n;
                    col.GetCount(out n);
                    for (uint i = 0; i < n; i++)
                    {
                        AudioNative.IMMDevice dev;
                        if (col.Item(i, out dev) != 0 || dev == null) continue;
                        try
                        {
                            var d = new AudioDevice();
                            dev.GetId(out d.Id);
                            uint st;
                            dev.GetState(out st);
                            d.Active = st == AudioNative.DEVICE_STATE_ACTIVE;
                            d.Name = ReadName(dev) ?? "Unknown output";
                            list.Add(d);
                        }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                finally { Marshal.ReleaseComObject(col); }
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        public static string GetDefaultId()
        {
            var en = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumeratorCom();
            try
            {
                AudioNative.IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(AudioNative.eRender, AudioNative.eMultimedia, out dev) != 0 || dev == null) return null;
                try { string id; dev.GetId(out id); return id; }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        static bool IsActive(string id)
        {
            var en = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumeratorCom();
            try
            {
                AudioNative.IMMDevice dev;
                if (en.GetDevice(id, out dev) != 0 || dev == null) return false;
                try { uint st; dev.GetState(out st); return st == AudioNative.DEVICE_STATE_ACTIVE; }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        /// <summary>
        /// Makes the device the default output. A TV's HDMI sound only appears a few seconds after
        /// its picture is turned on, so this waits up to <paramref name="timeoutMs"/> for it.
        /// Blocks - call from a background thread in the UI.
        /// </summary>
        public static bool SetDefault(string id, int timeoutMs, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!IsActive(id))
            {
                if (DateTime.UtcNow > deadline)
                {
                    error = "The sound output for this mode isn't available.";
                    return false;
                }
                Thread.Sleep(500);
            }

            var policy = (AudioNative.IPolicyConfig)new AudioNative.PolicyConfigClient();
            try
            {
                int hr = policy.SetDefaultEndpoint(id, AudioNative.eConsole);
                if (hr == 0) hr = policy.SetDefaultEndpoint(id, AudioNative.eMultimedia);
                if (hr == 0) hr = policy.SetDefaultEndpoint(id, AudioNative.eCommunications);
                if (hr != 0)
                {
                    error = "Windows refused to change the sound output (0x" + hr.ToString("X8") + ").";
                    return false;
                }
                return true;
            }
            finally { Marshal.ReleaseComObject(policy); }
        }
    }

    // ------------------------------------------------------------------ Display model
    class DisplayTarget
    {
        public Native.LUID AdapterId;
        public uint TargetId;
        public string FriendlyName;
        public string DevicePath;
        public uint OutputTechnology;
        public bool Active;

        public string HwKey { get { return AdapterId + ":" + TargetId; } }

        // Stable identity across reboots: the monitor device path (contains EDID id + port instance).
        public string Key
        {
            get { return string.IsNullOrEmpty(DevicePath) ? (FriendlyName + "#" + TargetId) : DevicePath; }
        }

        public bool IsInternal
        {
            get
            {
                return OutputTechnology == 0x80000000 // INTERNAL
                    || OutputTechnology == 11         // DISPLAYPORT_EMBEDDED
                    || OutputTechnology == 13;        // UDI_EMBEDDED
            }
        }

        public string Connection
        {
            get
            {
                if (IsInternal) return "Built-in";
                switch (OutputTechnology)
                {
                    case 0: return "VGA";
                    case 4: return "DVI";
                    case 5: return "HDMI";
                    case 6: return "LVDS";
                    case 10: return "DisplayPort";
                    case 12: return "UDI";
                    case 15: return "Miracast";
                    case 16: return "Indirect (wired)";
                    case 17: return "Indirect (virtual)";
                    case 18: return "DisplayPort (USB tunnel)";
                    default: return "Other";
                }
            }
        }

        public string DisplayName
        {
            get
            {
                string n = string.IsNullOrEmpty(FriendlyName) ? (IsInternal ? "Laptop screen" : "Unknown display") : FriendlyName;
                return n + "  (" + Connection + ")";
            }
        }
    }

    static class DisplayManager
    {
        static void Query(uint flags, out Native.DISPLAYCONFIG_PATH_INFO[] paths, out Native.DISPLAYCONFIG_MODE_INFO[] modes)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint np, nm;
                int err = Native.GetDisplayConfigBufferSizes(flags, out np, out nm);
                if (err != Native.ERROR_SUCCESS) throw new Exception("GetDisplayConfigBufferSizes failed (error " + err + ")");
                paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
                modes = new Native.DISPLAYCONFIG_MODE_INFO[nm];
                err = Native.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (err == Native.ERROR_INSUFFICIENT_BUFFER) continue; // config changed between calls, retry
                if (err != Native.ERROR_SUCCESS) throw new Exception("QueryDisplayConfig failed (error " + err + ")");
                Array.Resize(ref paths, (int)np);
                Array.Resize(ref modes, (int)nm);
                return;
            }
            throw new Exception("Display configuration kept changing; please try again.");
        }

        static string TargetKey(Native.DISPLAYCONFIG_PATH_INFO p) { return p.targetInfo.adapterId + ":" + p.targetInfo.id; }
        static string SourceKey(Native.DISPLAYCONFIG_PATH_INFO p) { return p.sourceInfo.adapterId + ":" + p.sourceInfo.id; }
        static bool IsActive(Native.DISPLAYCONFIG_PATH_INFO p) { return (p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0; }

        static List<DisplayTarget> BuildTargets(Native.DISPLAYCONFIG_PATH_INFO[] paths)
        {
            var map = new Dictionary<string, DisplayTarget>();
            var order = new List<DisplayTarget>();
            foreach (var p in paths)
            {
                if (p.targetInfo.targetAvailable == 0) continue; // nothing plugged in on this port
                string k = TargetKey(p);
                DisplayTarget t;
                if (!map.TryGetValue(k, out t))
                {
                    t = new DisplayTarget();
                    t.AdapterId = p.targetInfo.adapterId;
                    t.TargetId = p.targetInfo.id;
                    t.OutputTechnology = p.targetInfo.outputTechnology;

                    var name = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    name.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    name.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME));
                    name.header.adapterId = p.targetInfo.adapterId;
                    name.header.id = p.targetInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref name) == Native.ERROR_SUCCESS)
                    {
                        t.FriendlyName = name.monitorFriendlyDeviceName;
                        t.DevicePath = name.monitorDevicePath;
                        t.OutputTechnology = name.outputTechnology;
                    }
                    map[k] = t;
                    order.Add(t);
                }
                if (IsActive(p)) t.Active = true;
            }
            return order;
        }

        public static List<DisplayTarget> GetDisplays()
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ALL_PATHS, out paths, out modes);
            return BuildTargets(paths);
        }

        public static bool IsPrimaryInProfile(HashSet<string> keys)
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
            var primaryKeys = new HashSet<string>(paths.Where(p =>
            {
                uint index = p.sourceInfo.modeInfoIdx;
                return index < modes.Length && IsPrimarySource(modes[index]);
            }).Select(TargetKey));
            return BuildTargets(paths).Any(t => primaryKeys.Contains(t.HwKey) && keys.Contains(t.Key));
        }

        static bool IsPrimarySource(Native.DISPLAYCONFIG_MODE_INFO mode)
        {
            // The desktop origin is stored in the source mode's position fields.
            return mode.infoType == Native.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                && (uint)(mode.u1 >> 32) == 0 && (uint)mode.u2 == 0;
        }

        /// <summary>Turns on exactly the connected displays matching <paramref name="wanted"/> (extended desktop) and turns off the rest.</summary>
        // Win32 error codes SetDisplayConfig returns.
        const int ERROR_ACCESS_DENIED = 5, ERROR_GEN_FAILURE = 31, ERROR_NOT_SUPPORTED = 50,
                  ERROR_INVALID_PARAMETER = 87, ERROR_BAD_CONFIGURATION = 1610;

        // Valid values for paths we turn on (inactive paths from QueryDisplayConfig carry 0, which Windows rejects).
        const uint ROTATION_IDENTITY = 1, SCALING_PREFERRED = 128;

        /// <summary>Thrown when Windows won't make the change; the message explains it in plain words.</summary>
        public class SwitchRefusedException : Exception
        {
            public readonly int ErrorCode;
            public SwitchRefusedException(int code) : base(Explain(code)) { ErrorCode = code; }
        }

        public static string Explain(int code)
        {
            string why;
            switch (code)
            {
                case ERROR_ACCESS_DENIED:
                    why = "Windows blocked the display change. This happens while the screen is locked, a UAC (admin) prompt is open, or in a Remote Desktop session.";
                    break;
                case ERROR_GEN_FAILURE:
                    why = "The graphics driver couldn't switch screens right now - often because the TV is still waking up. Try again in a few seconds.";
                    break;
                case ERROR_INVALID_PARAMETER:
                case ERROR_BAD_CONFIGURATION:
                    why = "Windows couldn't find a working setup for these screens. Check the TV is on the right input, then switch again or save the profile's layout again.";
                    break;
                case ERROR_NOT_SUPPORTED:
                    why = "The graphics driver doesn't support this screen combination.";
                    break;
                default:
                    why = "Windows refused the display change.";
                    break;
            }
            return why + " (error " + code + ")";
        }

        /// <summary>
        /// Turns on exactly the connected displays matching <paramref name="wanted"/> (extended desktop) and turns off the rest.
        /// Tries a few ways and, if Windows is busy (TV waking up, driver reconfiguring), tries again shortly after.
        /// </summary>
        public static void Apply(Func<DisplayTarget, bool> wanted, bool save = true)
        {
            int[] delaysMs = { 0, 500, 1200, 2500 };
            int err = 0;
            for (int attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                if (delaysMs[attempt] > 0) Thread.Sleep(delaysMs[attempt]);
                err = TryApply(wanted, save, attempt + 1); // re-reads the hardware every time
                if (err == Native.ERROR_SUCCESS)
                {
                    if (attempt > 0) SwitchLog.Write("Switch succeeded on attempt " + (attempt + 1) + ".");
                    return;
                }
            }
            SwitchLog.Write("Switch FAILED: " + Explain(err));
            throw new SwitchRefusedException(err);
        }

        /// <summary>One attempt: picks a path per wanted screen and tries each way of applying it. Returns the last error.</summary>
        static int TryApply(Func<DisplayTarget, bool> wanted, bool save, int attempt)
        {
            Native.DISPLAYCONFIG_PATH_INFO[] all;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ALL_PATHS, out all, out modes);

            var targets = BuildTargets(all);
            var wantedTargets = targets.Where(wanted).ToList();
            var wantedKeys = new HashSet<string>(wantedTargets.Select(t => t.HwKey));
            if (wantedKeys.Count == 0) throw new Exception("None of the requested displays are connected.");

            var chosen = new List<Native.DISPLAYCONFIG_PATH_INFO>();
            var keptModes = new List<Native.DISPLAYCONFIG_MODE_INFO>();
            var chosenModes = new List<Native.DISPLAYCONFIG_PATH_INFO>(); // same paths, pointing at keptModes
            var usedSources = new HashSet<string>();
            var doneTargets = new HashSet<string>();

            // Pass 1: keep the source already driving a target that stays on (less flicker, keeps its settings).
            // Pass 2: give each remaining target its own free source on the SAME graphics chip as its port
            //         (cross-chip paths on two-GPU laptops are often refused), so the desktop is extended, not cloned.
            // Pass 3: any free source, as a last resort.
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var p in all)
                {
                    if (pass == 0 && !IsActive(p)) continue;
                    if (pass == 1 && p.sourceInfo.adapterId.ToString() != p.targetInfo.adapterId.ToString()) continue;
                    if (p.targetInfo.targetAvailable == 0) continue;
                    string tk = TargetKey(p), sk = SourceKey(p);
                    if (!wantedKeys.Contains(tk) || doneTargets.Contains(tk) || usedSources.Contains(sk)) continue;

                    var np = p;
                    np.flags = Native.DISPLAYCONFIG_PATH_ACTIVE;
                    np.sourceInfo.statusFlags = 0;   // output-only fields
                    np.targetInfo.statusFlags = 0;
                    if (np.targetInfo.rotation == 0) np.targetInfo.rotation = ROTATION_IDENTITY;
                    if (np.targetInfo.scaling == 0) np.targetInfo.scaling = SCALING_PREFERRED;

                    // A copy that keeps the screen's current resolution if it is already on (third method below).
                    var withModes = np;
                    withModes.sourceInfo.modeInfoIdx = KeepMode(p.sourceInfo.modeInfoIdx, modes, keptModes);
                    withModes.targetInfo.modeInfoIdx = KeepMode(p.targetInfo.modeInfoIdx, modes, keptModes);
                    chosenModes.Add(withModes);

                    np.sourceInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                    np.targetInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                    chosen.Add(np);
                    usedSources.Add(sk);
                    doneTargets.Add(tk);
                }
            }

            if (doneTargets.Count != wantedKeys.Count)
                throw new Exception("Could not find a free graphics output for every display.");

            var arr = chosen.ToArray();
            uint saveFlag = save ? Native.SDC_SAVE_TO_DATABASE : 0;
            string screens = string.Join(", ", wantedTargets.Select(t => t.DisplayName));
            var tries = new List<string>();
            int err;

            // 1) Let Windows look up this screen combination in its own database, so resolution, scaling, arrangement
            //    and main screen come back the way you last set them. (Skipped for a temporary switch: it would be saved.)
            if (save)
            {
                err = Native.SetDisplayConfig((uint)arr.Length, arr, 0, null,
                    Native.SDC_APPLY | Native.SDC_TOPOLOGY_SUPPLIED | Native.SDC_ALLOW_PATH_ORDER_CHANGES);
                tries.Add("remembered=" + err);
                if (err == Native.ERROR_SUCCESS) return Log(attempt, screens, tries, err);
            }

            // 2) A combination Windows has never seen: let it pick resolutions itself.
            err = Native.SetDisplayConfig((uint)arr.Length, arr, 0, null,
                Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES | saveFlag);
            tries.Add("auto=" + err);
            if (err == Native.ERROR_SUCCESS) return Log(attempt, screens, tries, err);

            // 3) Keep the current resolution of screens that are already on, let Windows fill in the rest.
            var withCurrent = chosenModes.ToArray();
            err = Native.SetDisplayConfig((uint)withCurrent.Length, withCurrent, (uint)keptModes.Count, keptModes.ToArray(),
                Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES | saveFlag);
            tries.Add("keep-current=" + err);
            return Log(attempt, screens, tries, err);
        }

        /// <summary>Copies a mode the screen uses now into <paramref name="kept"/>; returns its new index, or "invalid".</summary>
        static uint KeepMode(uint index, Native.DISPLAYCONFIG_MODE_INFO[] modes, List<Native.DISPLAYCONFIG_MODE_INFO> kept)
        {
            if (index == Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || index >= modes.Length) return Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            kept.Add(modes[index]);
            return (uint)(kept.Count - 1);
        }

        static int Log(int attempt, string screens, List<string> tries, int err)
        {
            // Only failures (and the attempt that fixed them) are worth keeping.
            if (err != Native.ERROR_SUCCESS || attempt > 1)
                SwitchLog.Write("Attempt " + attempt + " for [" + screens + "]: " + string.Join(", ", tries));
            return err;
        }

        public static Dictionary<string, string> ActiveScreenNames()
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
            var targets = BuildTargets(paths).ToDictionary(t => t.HwKey);
            var names = new Dictionary<string, string>();
            foreach (var path in paths)
            {
                DisplayTarget target;
                if (!targets.TryGetValue(TargetKey(path), out target)) continue;
                var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref source) == Native.ERROR_SUCCESS)
                    names[target.Key] = source.viewGdiDeviceName;
            }
            return names;
        }

        public const int BatteryRefreshHz = 60;

        /// <summary>
        /// Same rule as G-Helper's auto screen mode: on the charger the laptop screen runs at its highest refresh rate,
        /// on battery at 60Hz. Only touches the built-in screen, and only if it is on.
        /// Returns a short note like "Laptop 165Hz (charger)", or null if the laptop screen is off.
        /// </summary>
        public static string ApplyLaptopRefreshForPower()
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
            var internalTargets = new HashSet<string>(BuildTargets(paths).Where(t => t.IsInternal).Select(t => t.HwKey));

            foreach (var p in paths)
            {
                if (!internalTargets.Contains(TargetKey(p))) continue;

                var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                src.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                src.header.adapterId = p.sourceInfo.adapterId;
                src.header.id = p.sourceInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref src) != Native.ERROR_SUCCESS) continue;
                string device = src.viewGdiDeviceName;

                var cur = NewDevMode();
                if (!Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref cur)) continue;

                // Refresh rates offered at the current resolution and colour depth.
                var rates = new SortedSet<int>();
                var m = NewDevMode();
                for (int i = 0; Native.EnumDisplaySettings(device, i, ref m); i++)
                {
                    if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight
                        && m.dmBitsPerPel == cur.dmBitsPerPel && m.dmDisplayFrequency > 1)
                        rates.Add(m.dmDisplayFrequency);
                    m = NewDevMode();
                }
                if (rates.Count == 0) return null;

                bool onCharger = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
                int want = onCharger ? rates.Max : (rates.Contains(BatteryRefreshHz) ? BatteryRefreshHz : rates.Min);
                string note = "Laptop " + want + "Hz (" + (onCharger ? "charger" : "battery") + ")";
                if (cur.dmDisplayFrequency == want) return note;

                var set = cur;
                set.dmFields = Native.DM_DISPLAYFREQUENCY;
                set.dmDisplayFrequency = want;
                int r = Native.ChangeDisplaySettingsEx(device, ref set, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (r != Native.DISP_CHANGE_SUCCESSFUL)
                {
                    // The screen may still be settling right after a switch - try once more.
                    Thread.Sleep(700);
                    r = Native.ChangeDisplaySettingsEx(device, ref set, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
                }
                return r == Native.DISP_CHANGE_SUCCESSFUL ? note : "Laptop refresh rate not changed (error " + r + ")";
            }
            return null;
        }

        static Native.DEVMODE NewDevMode()
        {
            var d = new Native.DEVMODE();
            d.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            return d;
        }

        /// <summary>Snapshots the exact current setup: which screens, resolution, refresh rate, rotation, position and main screen.</summary>
        public static SavedLayout CaptureLayout()
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths;
            Native.DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);

            var byHw = BuildTargets(paths).ToDictionary(t => t.HwKey);
            var layout = new SavedLayout();
            var summary = new List<string>();
            foreach (var p in paths)
            {
                DisplayTarget t;
                if (!byHw.TryGetValue(TargetKey(p), out t)) continue;
                layout.Paths.Add(p);
                layout.PathKeys.Add(t.Key);
                summary.Add(Describe(t, p, modes));
            }
            layout.Modes.AddRange(modes);
            layout.Summary = string.Join(", ", summary);
            if (layout.Paths.Count == 0) throw new Exception("No screens are on.");
            return layout;
        }

        static string Describe(DisplayTarget t, Native.DISPLAYCONFIG_PATH_INFO p, Native.DISPLAYCONFIG_MODE_INFO[] modes)
        {
            string name = string.IsNullOrEmpty(t.FriendlyName) ? (t.IsInternal ? "Laptop" : "Screen") : t.FriendlyName;
            string res = "", hz = "", main = "";
            uint si = p.sourceInfo.modeInfoIdx, ti = p.targetInfo.modeInfoIdx;
            if (si < modes.Length && modes[si].infoType == Native.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                var m = modes[si];
                // source mode: width, height | pixelFormat, position.x | position.y
                res = " " + (uint)(m.u0 & 0xFFFFFFFF) + "x" + (uint)(m.u0 >> 32);
                if (IsPrimarySource(m)) main = " (main)";
            }
            if (ti < modes.Length && modes[ti].infoType == Native.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
            {
                // target mode: pixelRate | hSync | vSync(numerator, denominator) | ...
                ulong v = modes[ti].u2;
                uint num = (uint)(v & 0xFFFFFFFF), den = (uint)(v >> 32);
                if (den != 0) hz = " " + Math.Round((double)num / den) + "Hz";
            }
            return name + res + hz + main;
        }

        /// <summary>
        /// Restores a snapshot from <see cref="CaptureLayout"/>. Graphics adapter IDs change on every reboot,
        /// so each saved screen is matched to its current hardware by its monitor ID first.
        /// Returns false if the snapshot no longer fits the hardware (caller then falls back to <see cref="Apply"/>).
        /// </summary>
        public static bool ApplyLayout(SavedLayout layout, bool save = true)
        {
            Native.DISPLAYCONFIG_PATH_INFO[] all;
            Native.DISPLAYCONFIG_MODE_INFO[] current;
            Query(Native.QDC_ALL_PATHS, out all, out current);
            var byKey = new Dictionary<string, DisplayTarget>();
            foreach (var t in BuildTargets(all)) byKey[t.Key] = t;

            var adapterMap = new Dictionary<string, Native.LUID>();
            var targetIdMap = new Dictionary<string, uint>();
            var paths = layout.Paths.ToArray();
            for (int i = 0; i < paths.Length; i++)
            {
                DisplayTarget t;
                if (!byKey.TryGetValue(layout.PathKeys[i], out t)) return false;
                string oldAdapter = paths[i].targetInfo.adapterId.ToString();
                Native.LUID mapped;
                if (adapterMap.TryGetValue(oldAdapter, out mapped) && mapped.ToString() != t.AdapterId.ToString()) return false;
                adapterMap[oldAdapter] = t.AdapterId;
                targetIdMap[oldAdapter + ":" + paths[i].targetInfo.id] = t.TargetId;
                paths[i].targetInfo.adapterId = t.AdapterId;
                paths[i].targetInfo.id = t.TargetId;
            }
            for (int i = 0; i < paths.Length; i++)
            {
                Native.LUID mapped;
                if (!adapterMap.TryGetValue(paths[i].sourceInfo.adapterId.ToString(), out mapped)) return false;
                paths[i].sourceInfo.adapterId = mapped;
            }
            var modes = layout.Modes.ToArray();
            for (int i = 0; i < modes.Length; i++)
            {
                string oldAdapter = modes[i].adapterId.ToString();
                Native.LUID mapped;
                if (!adapterMap.TryGetValue(oldAdapter, out mapped)) return false;
                if (modes[i].infoType == Native.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                {
                    uint newId;
                    if (targetIdMap.TryGetValue(oldAdapter + ":" + modes[i].id, out newId)) modes[i].id = newId;
                }
                modes[i].adapterId = mapped;
            }

            int err = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES
                | (save ? Native.SDC_SAVE_TO_DATABASE : 0));
            if (err != Native.ERROR_SUCCESS)
                SwitchLog.Write("Saved layout [" + layout.Summary + "] refused with error " + err + "; falling back to a normal switch.");
            return err == Native.ERROR_SUCCESS;
        }
    }

    // ------------------------------------------------------------------ Profiles & settings
    enum Mode { PC, TV, Unknown }

    class ScreenRef
    {
        public string Key;   // DisplayTarget.Key
        public string Name;  // remembered so it can be shown while unplugged
    }

    class SavedLayout
    {
        public List<Native.DISPLAYCONFIG_PATH_INFO> Paths = new List<Native.DISPLAYCONFIG_PATH_INFO>();
        public List<string> PathKeys = new List<string>(); // DisplayTarget.Key of each path's screen
        public List<Native.DISPLAYCONFIG_MODE_INFO> Modes = new List<Native.DISPLAYCONFIG_MODE_INFO>();
        public string Summary = "";

        public HashSet<string> Keys { get { return new HashSet<string>(PathKeys); } }
    }

    class Profile
    {
        public List<ScreenRef> Screens = new List<ScreenRef>();
        public string AudioId = "";   // "" = don't change the sound output
        public string AudioName = "";
        public SavedLayout Layout;    // null = let Windows remember the layout

        public HashSet<string> Keys { get { return new HashSet<string>(Screens.Select(s => s.Key)); } }
    }

    static class Settings
    {
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TVMode"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Profile Pc = new Profile();
        public static Profile Tv = new Profile();
        public static uint HotkeyMods = Native.MOD_CONTROL | Native.MOD_ALT;
        public static uint HotkeyKey = (uint)Keys.T;
        public static bool LaptopRefreshFollowsPower = true;
        public static List<TvApp> TvApps = new List<TvApp>(); // apps to open in TV mode, in order
        public static Mode LastMode = Mode.Unknown; // last mode switched to (tie-break when both profiles match)
        public static bool SetupPending;

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        public static Profile For(Mode m) { return m == Mode.TV ? Tv : Pc; }

        static ScreenRef ParseScreen(string v)
        {
            int t = v.IndexOf('\t');
            return t < 0 ? new ScreenRef { Key = v, Name = v } : new ScreenRef { Key = v.Substring(0, t), Name = v.Substring(t + 1) };
        }

        /// <summary>Loads settings; on first run (or from the old single-TV format) fills sensible profiles.</summary>
        static SavedLayout LayoutOf(Profile p)
        {
            if (p.Layout == null) p.Layout = new SavedLayout();
            return p.Layout;
        }

        static void ReadLayoutLine(Profile p, string kind, string v)
        {
            try
            {
                if (kind == "Path")
                {
                    int t = v.IndexOf('\t');
                    if (t < 0) return;
                    var path = Native.FromBase64<Native.DISPLAYCONFIG_PATH_INFO>(v.Substring(0, t));
                    LayoutOf(p).Paths.Add(path);
                    LayoutOf(p).PathKeys.Add(v.Substring(t + 1));
                }
                else if (kind == "Mode") LayoutOf(p).Modes.Add(Native.FromBase64<Native.DISPLAYCONFIG_MODE_INFO>(v));
                else if (kind == "Summary") LayoutOf(p).Summary = v;
            }
            catch { p.Layout = null; }
        }

        static void WriteLayout(List<string> lines, string prefix, Profile p)
        {
            if (p.Layout == null) return;
            for (int i = 0; i < p.Layout.Paths.Count; i++)
                lines.Add(prefix + "LayoutPath=" + Native.ToBase64(p.Layout.Paths[i]) + "\t" + p.Layout.PathKeys[i]);
            foreach (var m in p.Layout.Modes) lines.Add(prefix + "LayoutMode=" + Native.ToBase64(m));
            lines.Add(prefix + "LayoutSummary=" + p.Layout.Summary);
        }

        public static void Load()
        {
            bool hasProfiles = false, hasAppList = false;
            bool firstRun = !File.Exists(FilePath);
            SetupPending = firstRun;
            string TvAppPath = "", TvAppArgs = "", TvAppName = "";
            bool TvAppClose = true;
            string legacyTvKey = null, legacyTvName = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1);
                        uint u;
                        if (k.StartsWith("PcLayout")) { ReadLayoutLine(Pc, k.Substring(8), v); continue; }
                        if (k.StartsWith("TvLayout")) { ReadLayoutLine(Tv, k.Substring(8), v); continue; }
                        switch (k)
                        {
                            case "PcScreen": Pc.Screens.Add(ParseScreen(v)); hasProfiles = true; break;
                            case "TvScreen": Tv.Screens.Add(ParseScreen(v)); hasProfiles = true; break;
                            case "Profiles": hasProfiles = true; break;
                            case "SetupPending": SetupPending = v.Trim() == "1"; break;
                            case "PcAudioId": Pc.AudioId = v.Trim(); break;
                            case "PcAudioName": Pc.AudioName = v.Trim(); break;
                            case "TvAudioId": Tv.AudioId = v.Trim(); break;
                            case "TvAudioName": Tv.AudioName = v.Trim(); break;
                            case "HotkeyMods": if (uint.TryParse(v, out u)) HotkeyMods = u; break;
                            case "HotkeyKey": if (uint.TryParse(v, out u) && u != 0) HotkeyKey = u; break;
                            case "LaptopRefreshFollowsPower": LaptopRefreshFollowsPower = v.Trim() != "0"; break;
                            case "TvAppPath": TvAppPath = v.Trim(); break;
                            case "TvAppArgs": TvAppArgs = v.Trim(); break;
                            case "TvAppName": TvAppName = v.Trim(); break;
                            case "LastMode": LastMode = v.Trim() == "TV" ? Mode.TV : v.Trim() == "PC" ? Mode.PC : Mode.Unknown; break;
                            case "TvAppClose": TvAppClose = v.Trim() != "0"; break;
                            case "TvApps": hasAppList = true; break;
                            case "TvApp":
                                {
                                    var p = v.Split('\t');
                                    if (p.Length >= 2 && p[1].Trim() != "")
                                        TvApps.Add(new TvApp
                                        {
                                            Name = p[0].Trim(), Path = p[1].Trim(), Args = p.Length > 2 ? p[2].Trim() : "",
                                            CloseOnPc = p.Length <= 3 || p[3].Trim() != "0",
                                            WaitForPrimary = p.Length > 4 && p[4].Trim() == "1",
                                            WaitForAudio = p.Length > 5 && p[5].Trim() == "1",
                                            OpenInTv = p.Length <= 6 || p[6].Trim() != "0"
                                        });
                                    hasAppList = true;
                                    break;
                                }
                            case "TvKey": legacyTvKey = v.Trim(); break;     // old format
                            case "TvName": legacyTvName = v.Trim(); break;   // old format
                        }
                    }
                }
            }
            catch { }

            // Older versions had a single TV app.
            if (!hasAppList && !string.IsNullOrEmpty(TvAppPath))
                TvApps.Add(new TvApp { Name = TvAppName, Path = TvAppPath, Args = TvAppArgs, CloseOnPc = TvAppClose });

            if (hasProfiles) return;

            // First run or old format: PC = screens that are on now (minus the old TV), TV = the old TV.
            try
            {
                foreach (var d in DisplayManager.GetDisplays())
                {
                    if (d.Key == legacyTvKey) continue;
                    if (d.Active || legacyTvKey != null) Pc.Screens.Add(new ScreenRef { Key = d.Key, Name = d.DisplayName });
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(legacyTvKey))
                Tv.Screens.Add(new ScreenRef { Key = legacyTvKey, Name = legacyTvName ?? "TV" });
            if (!firstRun) { try { Save(); } catch { } }
        }

        public static void Save()
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string> { "Profiles=1" };
            lines.Add("SetupPending=" + (SetupPending ? "1" : "0"));
            foreach (var s in Pc.Screens) lines.Add("PcScreen=" + s.Key + "\t" + s.Name);
            foreach (var s in Tv.Screens) lines.Add("TvScreen=" + s.Key + "\t" + s.Name);
            lines.Add("PcAudioId=" + Pc.AudioId);
            lines.Add("PcAudioName=" + Pc.AudioName);
            lines.Add("TvAudioId=" + Tv.AudioId);
            lines.Add("TvAudioName=" + Tv.AudioName);
            lines.Add("HotkeyMods=" + HotkeyMods);
            lines.Add("HotkeyKey=" + HotkeyKey);
            lines.Add("LaptopRefreshFollowsPower=" + (LaptopRefreshFollowsPower ? "1" : "0"));
            lines.Add("LastMode=" + (LastMode == Mode.TV ? "TV" : LastMode == Mode.PC ? "PC" : ""));
            lines.Add("TvApps=1"); // marks the list format, so an empty list stays empty
            foreach (var a in TvApps)
                lines.Add("TvApp=" + Clean(a.Name) + "\t" + Clean(a.Path) + "\t" + Clean(a.Args) + "\t" + (a.CloseOnPc ? "1" : "0")
                    + "\t" + (a.WaitForPrimary ? "1" : "0") + "\t" + (a.WaitForAudio ? "1" : "0")
                    + "\t" + (a.OpenInTv ? "1" : "0"));
            WriteLayout(lines, "Pc", Pc);
            WriteLayout(lines, "Tv", Tv);
            File.WriteAllLines(FilePath, lines);
        }

        public static string HotkeyText
        {
            get
            {
                var parts = new List<string>();
                if ((HotkeyMods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
                if ((HotkeyMods & Native.MOD_ALT) != 0) parts.Add("Alt");
                if ((HotkeyMods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
                if ((HotkeyMods & Native.MOD_WIN) != 0) parts.Add("Win");
                parts.Add(new KeysConverter().ConvertToString((Keys)HotkeyKey));
                return string.Join(" + ", parts);
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("TVMode") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("TVMode", "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue("TVMode", false);
                }
            }
        }
    }

    static class Modes
    {
        public static string Name(Mode m) { return m == Mode.TV ? "TV" : "PC"; }

        /// <summary>
        /// Turns on the profile's screens that are connected and turns everything else off.
        /// Uses the profile's saved layout when all of its screens are connected. Returns true if it did.
        /// </summary>
        public static bool Switch(Mode mode, out string refreshNote)
        {
            bool usedLayout = SwitchScreens(mode);
            Settings.LastMode = mode;
            try { Settings.Save(); } catch { }
            refreshNote = null;
            // A saved layout (or Windows) may have put back a refresh rate that doesn't match the power source.
            if (Settings.LaptopRefreshFollowsPower)
            {
                try { refreshNote = DisplayManager.ApplyLaptopRefreshForPower(); }
                catch (Exception ex) { refreshNote = "Laptop refresh rate not changed: " + ex.Message; }
            }
            return usedLayout;
        }

        static bool SwitchScreens(Mode mode)
        {
            var profile = Settings.For(mode);
            var keys = profile.Keys;
            if (keys.Count == 0)
                throw new Exception("The " + Name(mode) + " profile has no screens yet. Tick at least one screen in it.");

            var displays = DisplayManager.GetDisplays();
            var connected = new HashSet<string>(displays.Where(d => keys.Contains(d.Key)).Select(d => d.Key));
            if (connected.Count == 0)
                throw new Exception("None of the screens in the " + Name(mode) + " profile are connected"
                    + (mode == Mode.TV ? " - is the TV plugged in and turned on?" : "."));

            if (profile.Layout != null && profile.Layout.Keys.SetEquals(connected))
            {
                try { if (DisplayManager.ApplyLayout(profile.Layout)) return true; }
                catch { }
            }
            DisplayManager.Apply(d => keys.Contains(d.Key));
            return false;
        }

        /// <summary>Which profile matches the screens that are on right now.</summary>
        public static Mode Current()
        {
            return Current(DisplayManager.GetDisplays());
        }

        /// <summary>Same as <see cref="Current()"/>, reusing a display list the caller already has.</summary>
        public static Mode Current(List<DisplayTarget> displays)
        {
            var on = new HashSet<string>(displays.Where(d => d.Active).Select(d => d.Key));
            var matches = new List<Mode>();
            foreach (var m in new[] { Mode.TV, Mode.PC })
            {
                var keys = Settings.For(m).Keys;
                var expected = new HashSet<string>(displays.Where(d => keys.Contains(d.Key)).Select(d => d.Key));
                if (expected.Count > 0 && expected.SetEquals(on)) matches.Add(m);
            }
            if (matches.Count == 0) return Mode.Unknown;
            // Both profiles use the same screens right now (e.g. the TV is unplugged): trust the last switch.
            if (matches.Count == 2 && (Settings.LastMode == Mode.PC || Settings.LastMode == Mode.TV)) return Settings.LastMode;
            return matches[0];
        }

        /// <summary>TV mode goes to PC mode; anything else goes to TV mode.</summary>
        public static Mode Opposite()
        {
            return Current() == Mode.TV ? Mode.PC : Mode.TV;
        }
    }

    // ------------------------------------------------------------------ Switch log
    /// <summary>
    /// Short record of display switches that Windows refused (and retries that fixed them), in
    /// %APPDATA%\TVMode\switch.log, so a "sometimes it doesn't switch" problem can be traced afterwards.
    /// </summary>
    static class SwitchLog
    {
        static readonly object gate = new object();
        const long MaxBytes = 128 * 1024;

        public static string FilePath { get { return Path.Combine(Settings.Dir, "switch.log"); } }

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        // Keep the newer half.
                        var lines = File.ReadAllLines(FilePath);
                        File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
            }
            catch { } // logging must never break a switch
        }
    }

    // ------------------------------------------------------------------ TV apps
    /// <summary>One app to open in TV mode.</summary>
    class TvApp
    {
        public string Name = "";
        public string Path = "";       // .exe, shortcut, steam:// link or shell:AppsFolder\... Store app
        public string Args = "";
        public bool CloseOnPc = true;  // close it again when going back to PC mode
        public bool OpenInTv = true;   // off = stays in the list but doesn't open automatically in TV mode
        public bool WaitForPrimary;
        public bool WaitForAudio;

        /// <summary>Identifies the app in the launch-state file.</summary>
        public string Key { get { return (Path ?? "").Trim().ToLowerInvariant(); } }
    }

    /// <summary>
    /// Opens the TV-mode apps and remembers, per app, exactly which processes it started (PID + start time, in a file
    /// so the command line and a restarted app agree). Only those are closed later - never a copy you opened yourself.
    /// </summary>
    static class AppLauncher
    {
        static string StateFile { get { return Path.Combine(Settings.Dir, "launched.txt"); } }
        static readonly object stateLock = new object();

        public const string SteamBigPicture = "steam://open/bigpicture";

        public static string DisplayName(string path)
        {
            if (path.StartsWith(SteamBigPicture, StringComparison.OrdinalIgnoreCase)) return "Steam Big Picture";
            if (IsSteamLink(path)) return "Steam";
            if (path.Contains("://") || IsShellPath(path)) return path;
            return Path.GetFileNameWithoutExtension(path);
        }

        public static string NameOf(TvApp app)
        {
            return !string.IsNullOrEmpty(app.Name) ? app.Name : DisplayName(app.Path);
        }

        static bool IsShellPath(string path) { return path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase); }

        /// <summary>True if TV Mode can close this app again later (apps, shortcuts to apps, Steam).</summary>
        public static bool CanClose(string path)
        {
            return !string.IsNullOrEmpty(path) && (IsSteamLink(path) || ProcessNameFor(path) != null);
        }

        public static bool IsSteam(string path) { return IsSteamLink(path ?? ""); }

        public static bool IsSteamLink(string path) { return path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase); }

        static bool SteamRunning() { return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0; }

        public static string SteamExe()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var v = k == null ? null : k.GetValue("SteamExe") as string;
                    return string.IsNullOrEmpty(v) ? null : v.Replace('/', '\\');
                }
            }
            catch { return null; }
        }

        static void ShellOpen(string target, string args)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target, args ?? "") { UseShellExecute = true });
        }

        static string ResolveShortcut(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                return (string)sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
            }
            catch { return null; }
        }

        /// <summary>Process name to watch for, or null for links (steam://..., .url, Store apps) that can't be tracked.</summary>
        public static string ProcessNameFor(string path)
        {
            string exe = ExecutableFor(path);
            return exe == null ? null : Path.GetFileNameWithoutExtension(exe);
        }

        static string ExecutableFor(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains("://") || IsShellPath(path)) return null;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".exe") return path;
            if (ext == ".lnk")
            {
                string target = ResolveShortcut(path);
                if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return target;
            }
            return null;
        }

        static long StartTicks(System.Diagnostics.Process p)
        {
            try { return p.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
        }

        // ---- launch-state file: one line per tracked thing, "key<TAB>pid<TAB>startTicks" or "key<TAB>steam<TAB>1|0"

        class Entry
        {
            public string Key, Kind, A, B;
            public string Line { get { return Key + "\t" + Kind + "\t" + A + "\t" + B; } }
        }

        static List<Entry> ReadState()
        {
            var list = new List<Entry>();
            try
            {
                if (!File.Exists(StateFile)) return list;
                foreach (var line in File.ReadAllLines(StateFile))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 4) list.Add(new Entry { Key = p[0], Kind = p[1], A = p[2], B = p[3] });
                }
            }
            catch { }
            return list;
        }

        static void WriteState(List<Entry> entries)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                if (entries.Count == 0) { if (File.Exists(StateFile)) File.Delete(StateFile); }
                else File.WriteAllLines(StateFile, entries.Select(e => e.Line));
            }
            catch { }
        }

        /// <summary>Replaces this app's entries in the state file.</summary>
        static void SetEntries(string key, IEnumerable<Entry> entries)
        {
            lock (stateLock)
            {
                var all = ReadState().Where(e => e.Key != key).ToList();
                all.AddRange(entries);
                WriteState(all);
            }
        }

        static List<Entry> EntriesFor(string key)
        {
            lock (stateLock) return ReadState().Where(e => e.Key == key).ToList();
        }

        /// <summary>Processes this app started that are still running (PID reuse is ruled out by the start time).</summary>
        static List<System.Diagnostics.Process> Tracked(string key)
        {
            var list = new List<System.Diagnostics.Process>();
            foreach (var e in EntriesFor(key).Where(x => x.Kind == "pid"))
            {
                int pid;
                long ticks;
                if (!int.TryParse(e.A, out pid) || !long.TryParse(e.B, out ticks)) continue;
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById(pid);
                    if (!p.HasExited && ticks > 0 && StartTicks(p) == ticks) list.Add(p);
                }
                catch { }
            }
            return list;
        }

        static IEnumerable<Entry> PidEntries(string key, IEnumerable<System.Diagnostics.Process> procs)
        {
            return procs.Select(p => new Entry { Key = key, Kind = "pid", A = p.Id.ToString(), B = StartTicks(p).ToString() }).ToList();
        }

        // ---- open

        /// <summary>Opens one app. Blocks for a few seconds to see which processes it started.</summary>
        public static string Launch(TvApp app, out bool error)
        {
            error = false;
            string name = NameOf(app), key = app.Key;
            if (IsSteamLink(app.Path)) return LaunchSteam(app, out error);
            if (Tracked(key).Count > 0) return name + " is already open";

            string exe = ExecutableFor(app.Path);
            string procName = exe == null ? null : Path.GetFileNameWithoutExtension(exe);
            var before = new HashSet<int>();
            if (procName != null)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName)) before.Add(p.Id);

            Entry started = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(app.Path, app.Args ?? "") { UseShellExecute = true };
                if (File.Exists(app.Path)) psi.WorkingDirectory = Path.GetDirectoryName(app.Path);
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p != null && procName != null && !before.Contains(p.Id))
                        started = new Entry { Key = key, Kind = "pid", A = p.Id.ToString(), B = StartTicks(p).ToString() };
                }
            }
            catch (Exception ex)
            {
                error = true;
                return "Couldn't open " + name + ": " + ex.Message;
            }

            if (procName == null)
            {
                SetEntries(key, new Entry[0]);
                return "Opened " + name;
            }

            // Apps often start through a launcher process, so watch for new processes of that name for a moment.
            Thread.Sleep(3000);
            var fresh = new List<Entry>();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
            {
                using (p)
                {
                    if (before.Contains(p.Id)) continue;
                    try
                    {
                        if (string.Equals(p.MainModule.FileName, Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                            fresh.AddRange(PidEntries(key, new[] { p }));
                    }
                    catch { } // Process exited or Windows denied access to its executable path.
                }
            }
            if (started != null && !fresh.Any(e => e.A == started.A)) fresh.Add(started);
            SetEntries(key, fresh);
            if (fresh.Count == 0 && before.Count > 0)
                return name + " was already running, so it won't be closed later";
            return "Opened " + name;
        }

        // Steam links are handed to Steam itself, so there is no process of ours to watch. Instead remember whether
        // Steam was already running, and on the way back: leave Big Picture, and quit Steam only if TV Mode started it.
        static string LaunchSteam(TvApp app, out bool error)
        {
            error = false;
            bool wasRunning = SteamRunning();
            var earlier = EntriesFor(app.Key).FirstOrDefault(e => e.Kind == "steam");
            if (earlier != null) wasRunning = earlier.A == "1"; // TV mode again before going back: keep the first answer
            try { ShellOpen(app.Path, app.Args); }
            catch (Exception ex)
            {
                error = true;
                return "Couldn't open Steam: " + ex.Message;
            }
            SetEntries(app.Key, new[] { new Entry { Key = app.Key, Kind = "steam", A = wasRunning ? "1" : "0", B = "" } });
            return "Opened " + NameOf(app) + (wasRunning ? "" : " (starting Steam)");
        }

        // ---- close

        /// <summary>Politely closes what <see cref="Launch"/> started for this app. Returns null if there was nothing to close.</summary>
        public static string Close(TvApp app, out bool error)
        {
            error = false;
            string key = app.Key, name = NameOf(app);
            var steam = EntriesFor(key).FirstOrDefault(e => e.Kind == "steam");
            if (steam != null) return CloseSteam(app, steam.A, out error);

            var procs = Tracked(key);
            if (procs.Count == 0) { SetEntries(key, new Entry[0]); return null; }

            foreach (var p in procs)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            foreach (var p in procs)
            {
                int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try { p.WaitForExit(left); } catch { }
            }
            var stillOpen = procs.Where(p => { try { return !p.HasExited; } catch { return false; } }).ToList();
            SetEntries(key, PidEntries(key, stillOpen)); // keep tracking whatever is left, so the next switch can try again
            if (stillOpen.Count > 0)
            {
                error = true;
                // Not killed on purpose: it may be asking to save something.
                return name + " didn't close by itself - please close it";
            }
            return "Closed " + name;
        }

        static string CloseSteam(TvApp app, string wasRunning, out bool error)
        {
            error = false;
            if (!SteamRunning()) { SetEntries(app.Key, new Entry[0]); return null; }

            if (wasRunning == "1")
            {
                // You had Steam open already: just leave Big Picture and keep Steam running.
                try { ShellOpen("steam://close/bigpicture", null); }
                catch (Exception ex) { error = true; return "Couldn't close Steam Big Picture: " + ex.Message; }
                SetEntries(app.Key, new Entry[0]);
                return "Closed Steam Big Picture";
            }

            string exe = SteamExe();
            if (exe == null || !File.Exists(exe)) { error = true; return "Couldn't find steam.exe to close Steam"; }
            try { System.Diagnostics.Process.Start(exe, "-shutdown"); }
            catch (Exception ex) { error = true; return "Couldn't close Steam: " + ex.Message; }
            SetEntries(app.Key, new Entry[0]);
            return "Closed Steam";
        }

        // ---- all apps at once

        public class Result
        {
            public TvApp App;
            public string Message;
            public bool Error;
        }

        /// <summary>
        /// Opens (TV) or closes (PC, only apps with CloseOnPc) every TV app, all at the same time - each one may wait a
        /// few seconds. <paramref name="report"/> is called (from a worker thread) as each app finishes.
        /// </summary>
        public static void RunAll(bool open, IEnumerable<TvApp> apps, HashSet<string> tvKeys, string audioId,
            Action<Result> report, Func<bool> cancelled = null)
        {
            using (var mutex = new Mutex(false, "TVMode_AppBatch_7b3f"))
            {
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
                try { RunBatch(open, apps, report, tvKeys, audioId, cancelled ?? (() => false)); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static void RunBatch(bool open, IEnumerable<TvApp> apps, Action<Result> report, HashSet<string> tvKeys,
            string audioId, Func<bool> cancelled)
        {
            // Opening skips apps switched off for TV mode; closing still covers them, in case one was opened before it was switched off.
            var groups = apps.Where(a => !string.IsNullOrEmpty(a.Path) && (open ? a.OpenInTv : a.CloseOnPc))
                .GroupBy(a => ProcessNameFor(a.Path) ?? a.Key, StringComparer.OrdinalIgnoreCase);
            var threads = new List<Thread>();
            foreach (var group in groups)
            {
                var batch = group;
                var t = new Thread(delegate ()
                {
                    foreach (var app in batch)
                    {
                        var r = new Result { App = app };
                        try
                        {
                            if (open)
                            {
                                r.Message = WaitForLaunch(app, tvKeys, audioId, cancelled);
                                if (cancelled()) return;
                            }
                            r.Error = r.Message != null;
                            if (!r.Error) r.Message = open ? Launch(app, out r.Error) : Close(app, out r.Error);
                        }
                        catch (Exception ex) { r.Message = NameOf(app) + ": " + ex.Message; r.Error = true; }
                        if (r.Message != null && report != null) report(r);
                    }
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA); // ShellExecute (links, shortcuts, Store apps) wants STA
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join();
        }

        static string WaitForLaunch(TvApp app, HashSet<string> tvKeys, string audioId, Func<bool> cancelled)
        {
            bool checkAudio = app.WaitForAudio && !string.IsNullOrEmpty(audioId);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!cancelled())
            {
                bool primaryReady = !app.WaitForPrimary || DisplayManager.IsPrimaryInProfile(tvKeys);
                bool audioReady = !checkAudio || string.Equals(AudioManager.GetDefaultId(), audioId, StringComparison.OrdinalIgnoreCase);
                if (primaryReady && audioReady) return null;
                if (timer.ElapsedMilliseconds >= 12000)
                {
                    string reason = !primaryReady && !audioReady ? "the TV display and sound weren't ready"
                        : !primaryReady ? "no TV profile screen became the main display"
                        : "the TV sound output didn't become the Windows default";
                    return "Skipped " + NameOf(app) + " because " + reason;
                }
                Thread.Sleep(250);
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ Entry point
    static class Program
    {
        public const string AppName = "PC–TV Switcher";

        /// <summary>Sent by a second launch so the running copy brings its window to the front.</summary>
        public static readonly int ShowWindowMessage = Native.RegisterWindowMessage("TVMode_ShowWindow_7b3f");

        [STAThread]
        static int Main(string[] args)
        {
            Settings.Load();
            string cmd = args.Length > 0 ? args[0].Trim().TrimStart('-', '/').ToLowerInvariant() : "";

            // Command-line use: TVMode.exe pc | tv | tv-no-apps | toggle | list
            if (cmd == "pc" || cmd == "tv" || cmd == "tv-no-apps" || cmd == "toggle" || cmd == "list")
            {
                bool hasConsole = Native.AttachConsole(-1);
                try
                {
                    if (cmd == "list")
                    {
                        Console.WriteLine("Displays:");
                        foreach (var d in DisplayManager.GetDisplays())
                            Console.WriteLine("  " + (d.Active ? "[on]  " : "[off] ") + d.DisplayName);
                        string def = AudioManager.GetDefaultId();
                        Console.WriteLine("Sound outputs:");
                        foreach (var a in AudioManager.GetOutputs())
                            Console.WriteLine("  " + (a.Id == def ? "[default] " : a.Active ? "[ready]   " : "[unplugged] ") + a.Name);
                        foreach (var m in new[] { Mode.PC, Mode.TV })
                        {
                            var p = Settings.For(m);
                            Console.WriteLine(Modes.Name(m) + " profile:");
                            Console.WriteLine("  Screens: " + (p.Screens.Count == 0 ? "(none)" : string.Join(", ", p.Screens.Select(s => s.Name))));
                            Console.WriteLine("  Sound:   " + (p.AudioId == "" ? "don't change" : p.AudioName));
                            Console.WriteLine("  Layout:  " + (p.Layout == null ? "Windows remembers it" : p.Layout.Summary));
                        }
                        Console.WriteLine("TV apps:" + (Settings.TvApps.Count == 0 ? " (none)" : ""));
                        foreach (var a in Settings.TvApps)
                            Console.WriteLine("  " + AppLauncher.NameOf(a) + (a.OpenInTv ? "" : "  (off - doesn't open in TV mode)")
                                + (a.CloseOnPc && AppLauncher.CanClose(a.Path) ? "  (auto-close in PC mode)" : "  (kept open in PC mode)"));
                        return 0;
                    }
                    if (!SetupSession.TryEnter())
                        throw new InvalidOperationException("Finish or close the setup wizard before switching modes.");
                    try { SwitchFromCommandLine(cmd); }
                    finally { SetupSession.Exit(); }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    if (!hasConsole && cmd != "list")
                        MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "TVMode_SingleInstance_7b3f", out createdNew))
            {
                if (!createdNew)
                {
                    // Already running (maybe hidden in the tray): ask that copy to show its window.
                    if (cmd != "tray") Native.PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(cmd == "tray"));
            }
            return 0;
        }

        static void SwitchFromCommandLine(string cmd)
        {
            Mode mode = cmd == "pc" ? Mode.PC : cmd == "toggle" ? Modes.Opposite() : Mode.TV;
            string refreshNote;
            Modes.Switch(mode, out refreshNote);
            Console.WriteLine("Switched to " + Modes.Name(mode) + " mode.");
            if (refreshNote != null) Console.WriteLine(refreshNote);
            var prof = Settings.For(mode);
            System.Threading.Tasks.Task sound = null;
            if (!string.IsNullOrEmpty(prof.AudioId))
            {
                sound = System.Threading.Tasks.Task.Factory.StartNew(delegate
                {
                    string err;
                    if (AudioManager.SetDefault(prof.AudioId, 10000, out err))
                        Console.WriteLine("Sound: " + prof.AudioName);
                    else
                        Console.Error.WriteLine("Sound: " + err);
                });
            }
            if (cmd != "tv-no-apps" && Settings.TvApps.Count > 0)
            {
                if (mode == Mode.TV) Thread.Sleep(1200); // let the TV finish switching so the apps open there
                var printLock = new object();
                AppLauncher.RunAll(mode == Mode.TV, Settings.TvApps, prof.Keys, prof.AudioId,
                    r => { lock (printLock) (r.Error ? Console.Error : Console.Out).WriteLine(r.Message); });
            }
            if (sound != null) sound.GetAwaiter().GetResult();
        }
    }
}
