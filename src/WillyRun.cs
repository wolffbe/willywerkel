using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Launcher for Autos bauen mit Willy Werkel.
// Sets 640x480 (fullscreen), runs the game, and ALWAYS restores the
// original screen resolution when the game closes (even if it is killed).
// Pass /fenster (or /windowed) to skip the resolution change.
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
    const int ENUM_CURRENT_SETTINGS = -1, CDS_FULLSCREEN = 4, DISP_CHANGE_SUCCESSFUL = 0;
    const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;
    const int SW_SHOW = 5, SW_RESTORE = 9;

    static void ForceForeground(IntPtr h)
    {
        ShowWindow(h, SW_RESTORE); ShowWindow(h, SW_SHOW);
        // tapping ALT lifts Windows' foreground lock so SetForegroundWindow works for another process
        keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0);
        SetForegroundWindow(h); BringWindowToTop(h); SetActiveWindow(h);
    }

    static void Main(string[] args)
    {
        bool windowed = false;
        foreach (string a in args) { string s = a.ToLower(); if (s.Contains("window") || s.Contains("fenster") || s == "/w") windowed = true; }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        string gameDir = Path.Combine(baseDir, "Game");
        string exe = Path.Combine(gameDir, "WILLY32.EXE");
        string cd  = Path.Combine(baseDir, "CD");

        // 256-colour compatibility for the game (per-app, restores itself)
        try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                  k.SetValue(exe, "~ 256COLOR HIGHDPIAWARE", RegistryValueKind.String); } catch {}

        // map B: to the game data if not already mapped
        if (!Directory.Exists("B:\\Movies")) Shell("subst.exe", "B: \"" + cd + "\"");

        // remember current resolution, switch to 640x480
        DEVMODE original = new DEVMODE(); original.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        bool haveOrig = EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref original);
        bool changed = false;
        if (!windowed && haveOrig && !(original.dmPelsWidth == 640 && original.dmPelsHeight == 480))
        {
            DEVMODE dm = original;
            dm.dmPelsWidth = 640; dm.dmPelsHeight = 480;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            changed = (ChangeDisplaySettings(ref dm, CDS_FULLSCREEN) == DISP_CHANGE_SUCCESSFUL);
        }

        IntPtr tray = IntPtr.Zero;
        try
        {
            if (!File.Exists(exe)) return;
            Process p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = true });
            if (p != null)
            {
                // Alt+F4 anywhere instantly terminates the game
                var watcher = new Thread(delegate()
                {
                    while (!p.HasExited)
                    {
                        if ((GetAsyncKeyState(0x12) & 0x8000) != 0 && (GetAsyncKeyState(0x73) & 0x8000) != 0) // VK_MENU + VK_F4
                        { try { p.Kill(); } catch {} return; }
                        Thread.Sleep(40);
                    }
                });
                watcher.IsBackground = true; watcher.Start();

                // find the game window
                IntPtr h = IntPtr.Zero;
                for (int i = 0; i < 80 && !p.HasExited; i++)
                {
                    p.Refresh(); h = p.MainWindowHandle;
                    if (h != IntPtr.Zero) break;
                    System.Threading.Thread.Sleep(250);
                }
                if (h != IntPtr.Zero && !windowed)
                {
                    // hide the taskbar and make the game cover the whole screen on top of everything
                    tray = FindWindow("Shell_TrayWnd", null);
                    if (tray != IntPtr.Zero) ShowWindow(tray, 0); // SW_HIDE
                    int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); // SM_CXSCREEN / SM_CYSCREEN
                    for (int k = 0; k < 8 && !p.HasExited; k++)
                    {
                        ForceForeground(h);
                        SetWindowPos(h, (IntPtr)(-1), 0, 0, sw, sh, 0x0040); // HWND_TOPMOST, SWP_SHOWWINDOW
                        System.Threading.Thread.Sleep(400);
                    }
                }
                else if (h != IntPtr.Zero)
                {
                    for (int k = 0; k < 8 && !p.HasExited; k++) { ForceForeground(h); System.Threading.Thread.Sleep(400); }
                }
                p.WaitForExit();
            }
        }
        finally
        {
            if (tray != IntPtr.Zero) ShowWindow(tray, 5); // SW_SHOW - bring the taskbar back
            if (changed) { DEVMODE o = original; o.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL; ChangeDisplaySettings(ref o, 0); }
        }
    }

    static void Shell(string f, string a)
    {
        try { var p = Process.Start(new ProcessStartInfo(f, a) { UseShellExecute = false, CreateNoWindow = true }); p.WaitForExit(); } catch {}
    }
}
