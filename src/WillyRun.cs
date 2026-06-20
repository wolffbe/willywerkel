using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Launcher for Autos bauen mit Willy Werkel.
// Runs the game at 640x480 fullscreen. Restores the original resolution + taskbar
// whenever the game loses focus (Alt-Tab, Windows key) and re-applies fullscreen
// when it returns. Always restores on exit. Alt+F4 kills the game instantly.
// Pass /fenster (or /windowed) to run in a plain 640x480 window instead.
class WillyRun
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    const int ENUM_CURRENT_SETTINGS = -1, CDS_FULLSCREEN = 4, DISP_CHANGE_SUCCESSFUL = 0;
    const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;

    static DEVMODE original;
    static bool changed;            // we changed the resolution and owe a restore
    static IntPtr tray = IntPtr.Zero;

    static void ForceForeground(IntPtr h)
    {
        ShowWindow(h, 9); ShowWindow(h, 5); // SW_RESTORE, SW_SHOW
        keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); // tap ALT to lift the foreground lock
        SetForegroundWindow(h); BringWindowToTop(h); SetActiveWindow(h);
    }

    static void ApplyFullscreen(IntPtr h)
    {
        DEVMODE cur = new DEVMODE(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref cur);
        if (cur.dmPelsWidth != 640 || cur.dmPelsHeight != 480)
        {
            DEVMODE dm = original; dm.dmPelsWidth = 640; dm.dmPelsHeight = 480; dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            if (ChangeDisplaySettings(ref dm, CDS_FULLSCREEN) == DISP_CHANGE_SUCCESSFUL) changed = true;
        }
        tray = FindWindow("Shell_TrayWnd", null); if (tray != IntPtr.Zero) ShowWindow(tray, 0); // hide taskbar
        int w = GetSystemMetrics(0), ht = GetSystemMetrics(1);
        SetWindowPos(h, (IntPtr)(-1), 0, 0, w, ht, 0x40); // HWND_TOPMOST, SWP_SHOWWINDOW
        ForceForeground(h);
    }

    static void RestoreDesktop(IntPtr h)
    {
        if (tray != IntPtr.Zero) ShowWindow(tray, 5);                       // show taskbar
        if (changed) { DEVMODE o = original; o.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL; ChangeDisplaySettings(ref o, 0); changed = false; }
        if (h != IntPtr.Zero) SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x13); // HWND_NOTOPMOST, NOSIZE|NOMOVE|NOACTIVATE
    }

    static void Main(string[] args)
    {
        bool windowed = false;
        foreach (string a in args) { string s = a.ToLower(); if (s.Contains("window") || s.Contains("fenster") || s == "/w") windowed = true; }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        string gameDir = Path.Combine(baseDir, "Game");
        string exe = Path.Combine(gameDir, "WILLY32.EXE");
        string cd = Path.Combine(baseDir, "CD");

        try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                  k.SetValue(exe, "~ 256COLOR HIGHDPIAWARE", RegistryValueKind.String); } catch {}
        // the 1997 game scans drive letters for its data; map B: just while it runs
        bool mappedB = false;
        if (!Directory.Exists("B:\\Movies")) { Shell("subst.exe", "B: \"" + cd + "\""); mappedB = true; }

        original = new DEVMODE(); original.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref original);
        if (!windowed && !(original.dmPelsWidth == 640 && original.dmPelsHeight == 480))
        {
            DEVMODE dm = original; dm.dmPelsWidth = 640; dm.dmPelsHeight = 480; dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            if (ChangeDisplaySettings(ref dm, CDS_FULLSCREEN) == DISP_CHANGE_SUCCESSFUL) changed = true;
        }

        IntPtr h = IntPtr.Zero;
        try
        {
            if (!File.Exists(exe)) return;
            Process p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = true });
            if (p == null) return;
            int pid = p.Id;

            for (int i = 0; i < 80 && !p.HasExited; i++) { p.Refresh(); h = p.MainWindowHandle; if (h != IntPtr.Zero) break; Thread.Sleep(250); }

            if (!windowed && h != IntPtr.Zero) ApplyFullscreen(h);
            else if (h != IntPtr.Zero) ForceForeground(h);

            string state = "fs"; // fullscreen currently applied
            while (!p.HasExited)
            {
                if ((GetAsyncKeyState(0x12) & 0x8000) != 0 && (GetAsyncKeyState(0x73) & 0x8000) != 0) { try { p.Kill(); } catch {} break; } // Alt+F4
                if (!windowed && h != IntPtr.Zero)
                {
                    IntPtr fg = GetForegroundWindow(); int fgpid; GetWindowThreadProcessId(fg, out fgpid);
                    bool gameFg = (fgpid == pid);
                    if (gameFg && state != "fs") { ApplyFullscreen(h); state = "fs"; }            // returned to game -> re-apply
                    else if (!gameFg && state == "fs") { RestoreDesktop(h); state = "out"; }       // Alt-Tab / Win key -> restore
                }
                Thread.Sleep(120);
            }
        }
        finally { RestoreDesktop(h); if (mappedB) Shell("subst.exe", "B: /D"); } // remove the temporary B: drive
    }

    static void Shell(string f, string a)
    { try { var p = Process.Start(new ProcessStartInfo(f, a) { UseShellExecute = false, CreateNoWindow = true }); p.WaitForExit(); } catch {} }
}
