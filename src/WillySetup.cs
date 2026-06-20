using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Autos bauen mit Willy Werkel - automatic Setup + Patch for 64-bit Windows.
// Asks for the ISO and the install folder, performs the install the dead
// 16-bit SETUP.EXE cannot do, applies all compatibility fixes, and installs
// a one-click launcher (WillyRun.exe) + desktop shortcut.
class WillySetup
{
    static string isoPath;
    // base64 of the compiled WillyRun.exe launcher (injected at build time)
    const string WILLYRUN_B64 = "__WILLYRUN_B64__";

    [STAThread]
    static void Main(string[] argv)
    {
        Console.Title = "Autos bauen mit Willy Werkel - Setup";
        try { Run(argv); }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: " + ex.Message);
            Console.ResetColor();
            if (isoPath != null) try { Dismount(isoPath); } catch {}
        }
        Console.WriteLine("\nPress any key or click here to close...");
        WaitForKeyOrClick();
    }

    static void Run(string[] argv)
    {
        Console.WriteLine("===================================================");
        Console.WriteLine("  Autos bauen mit Willy Werkel - Setup & Patch");
        Console.WriteLine("===================================================\n");

        // --- 1) choose the ISO ---
        isoPath = PickIso(argv);
        if (isoPath == null) { Console.WriteLine("No ISO selected. Cancelled."); return; }
        Console.WriteLine("ISO        : " + isoPath);

        // --- 2) choose the install location ---
        string parent = (argv.Length > 1 && Directory.Exists(argv[1])) ? argv[1] : PickFolder();
        if (parent == null) { Console.WriteLine("No install folder selected. Cancelled."); return; }
        string install = Path.Combine(parent, "WillyWerkel");
        Console.WriteLine("Install to : " + install + "\n");

        // --- mount ISO ---
        Console.WriteLine("Mounting ISO...");
        string drv = Mount(isoPath);
        if (drv == null) throw new Exception("Could not mount the ISO.");
        string src = drv + ":\\";
        if (!File.Exists(src + "AUTOBAU.HLP"))
            throw new Exception("This ISO is not 'Autos bauen mit Willy Werkel' (AUTOBAU.HLP not found).");
        Console.WriteLine("Mounted as : " + drv + ":\n");

        // free B: in case a previous install still has it mapped into the target
        try { var sp = Process.Start(new ProcessStartInfo("subst.exe", "B: /D") { UseShellExecute = false, CreateNoWindow = true }); sp.WaitForExit(); } catch {}
        if (Directory.Exists(install))
        {
            Console.WriteLine("Removing previous installation...");
            ClearReadOnly(install);
            try { Directory.Delete(install, true); } catch {}
        }
        string game = Path.Combine(install, "Game");
        string cd   = Path.Combine(install, "CD");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(cd);

        // [1] program on hard disk
        Console.WriteLine("[1/6] Installing program files...");
        CopyTree(src + "DATA", game);
        string gx = Path.Combine(game, "Xtras");
        CopyTree(src + "XTRAS", gx);
        CopyFile(src + "DATA\\FILEIO.X32",   Path.Combine(gx, "FILEIO.X32"));
        CopyFile(src + "DATA\\FILEIO16.X16", Path.Combine(gx, "FILEIO16.X16"));

        // [2] movie data
        Console.WriteLine("[2/6] Copying movie data...");
        string mov = Path.Combine(cd, "Movies");
        CopyTree(src + "MOVIES", mov);

        // [3] PATCH: DATA.CST into the movie folder -> removes the "Where is data.cst?" popup
        Console.WriteLine("[3/6] Patch: removing the data.cst prompt...");
        CopyFile(src + "DATA\\DATA.CST", Path.Combine(mov, "DATA.CST"));
        CopyTree(src + "DATA",  Path.Combine(cd, "DATA"));
        CopyTree(src + "XTRAS", Path.Combine(cd, "Xtras"));
        CopyFile(src + "DATA\\FILEIO.X32",   Path.Combine(cd, "Xtras", "FILEIO.X32"));
        CopyFile(src + "DATA\\FILEIO16.X16", Path.Combine(cd, "Xtras", "FILEIO16.X16"));
        CopyFile(src + "AUTOBAU.HLP", Path.Combine(cd, "AutoBau.hlp"));
        CopyFile(src + "AUTOBAU.HLP", Path.Combine(cd, "MulleBil.hlp"));
        CopyFile(src + "AUTOBAU.CNT", Path.Combine(cd, "AutoBau.cnt"));
        CopyFile(src + "DATA\\DATA.CST", Path.Combine(cd, "DATA.CST"));

        // [4] ZX_DATEN driving data - tolerate bad/missing sectors (partial rips)
        Console.WriteLine("[4/6] Copying driving data (tolerating bad sectors)...");
        string zx = Path.Combine(cd, "ZX_DATEN");
        Directory.CreateDirectory(zx);
        foreach (string f in Directory.GetFiles(drv + ":\\ZX_DATEN"))
            CopyFile(drv + ":\\ZX_DATEN\\" + Path.GetFileName(f), Path.Combine(zx, Path.GetFileName(f)));
        ClearReadOnly(install);

        // [5] PATCH: 256-colour compatibility + install the launcher
        Console.WriteLine("[5/6] Patch: 256-colour mode + installing launcher...");
        SetCompat(Path.Combine(game, "WILLY32.EXE"));
        string runner = Path.Combine(install, "WillyRun.exe");
        File.WriteAllBytes(runner, Convert.FromBase64String(WILLYRUN_B64));

        // [6] shortcuts + readme
        Console.WriteLine("[6/6] Creating shortcuts...");
        File.WriteAllText(Path.Combine(install, "README.txt"), Readme, Encoding.Default);
        string icon = Path.Combine(game, "MULLE.ICO");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        MakeShortcut(UniqueLnk(Path.Combine(desktop, "Autos bauen mit Willy Werkel.lnk")), runner, "", install, icon);

        // the game saves progress next to itself; make the folder writable for standard users
        // (needed when installing under Program Files, which is read-only for normal users)
        Console.WriteLine("Setting write permissions (for saved games)...");
        GrantWrite(install);

        Dismount(isoPath);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n===================================================");
        Console.WriteLine("  DONE!");
        Console.WriteLine("  Start the game from the desktop shortcut");
        Console.WriteLine("  \"Autos bauen mit Willy Werkel\".");
        Console.WriteLine("===================================================");
        Console.ResetColor();
    }

    // ---------- dialogs ----------
    static string PickIso(string[] argv)
    {
        if (argv.Length > 0 && File.Exists(argv[0]) && argv[0].ToLower().EndsWith(".iso")) return argv[0];
        Console.WriteLine("Please select the game ISO file...");
        using (var d = new OpenFileDialog())
        {
            d.Title = "Select the 'Autos bauen mit Willy Werkel' ISO file";
            d.Filter = "Disc image (*.iso)|*.iso|All files (*.*)|*.*";
            string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(dl)) d.InitialDirectory = dl;
            return ShowFront(d) == DialogResult.OK ? d.FileName : null;
        }
    }

    static string PickFolder()
    {
        Console.WriteLine("Please choose where to install the game...");
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        IntPtr owner = GetConsoleWindow(); SetForegroundWindow(owner);
        try { return PickModern(pf, owner); } // modern dialog opens directly inside Program Files (x86)
        catch
        {
            using (var d = new FolderBrowserDialog()) { d.Description = "Choose where to install Willy Werkel"; d.SelectedPath = pf; return ShowFront(d) == DialogResult.OK ? d.SelectedPath : null; }
        }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    delegate bool EnumWinProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWinProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    class OwnerWindow : IWin32Window { public IntPtr Handle { get; set; } }

    // ---- modern (Vista+) folder picker: opens directly inside the target folder ----
    [ComImport, ClassInterface(ClassInterfaceType.None), Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialog { }
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint c, IntPtr rg); void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
        void Advise(IntPtr p, out uint c); void Unadvise(uint c);
        void SetOptions(uint fos); void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem psi); void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi); void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t); void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int alignment); void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string e);
        void Close(int hr); void SetClientGuid(ref Guid g); void ClearClientData(); void SetFilter(IntPtr f);
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem ppv);

    static string PickModern(string initial, IntPtr owner)
    {
        var dlg = (IFileDialog)new FileOpenDialog();
        uint o; dlg.GetOptions(out o); dlg.SetOptions(o | 0x20 | 0x8); // FOS_PICKFOLDERS | FOS_NOCHANGEDIR
        dlg.SetTitle("Choose where to install Willy Werkel");
        try { Guid iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); IShellItem si; if (SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out si) == 0 && si != null) dlg.SetFolder(si); } catch {}
        if (dlg.Show(owner) != 0) return null; // cancelled
        IShellItem res; dlg.GetResult(out res); string p; res.GetDisplayName(0x80058000, out p); return p; // SIGDN_FILESYSPATH
    }

    static IntPtr FindTreeView(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(64); GetClassName(h, sb, 64);
            if (sb.ToString() == "SysTreeView32") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // wait for the folder dialog to open, then scroll its selected folder toward the centre
    static void CenterFolderSelection()
    {
        const int TVF = 0x1100, TVM_GETNEXTITEM = TVF + 10, TVM_ENSUREVISIBLE = TVF + 20, TVM_GETVISIBLECOUNT = TVF + 16;
        const int TVGN_CARET = 9, TVGN_NEXTVISIBLE = 6;
        try
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(150);
                IntPtr tv = FindTreeView(GetForegroundWindow());
                if (tv == IntPtr.Zero) continue;
                IntPtr hSel = SendMessage(tv, TVM_GETNEXTITEM, (IntPtr)TVGN_CARET, IntPtr.Zero);
                if (hSel == IntPtr.Zero) continue;
                int vc = SendMessage(tv, TVM_GETVISIBLECOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (vc < 2) vc = 10;
                SendMessage(tv, TVM_ENSUREVISIBLE, IntPtr.Zero, hSel);
                IntPtr h = hSel;
                for (int i = 0; i < vc / 2; i++) { IntPtr nx = SendMessage(tv, TVM_GETNEXTITEM, (IntPtr)TVGN_NEXTVISIBLE, h); if (nx == IntPtr.Zero) break; h = nx; }
                SendMessage(tv, TVM_ENSUREVISIBLE, IntPtr.Zero, h);   // bring the item half a page below into view -> selected lands mid-screen
                SendMessage(tv, TVM_ENSUREVISIBLE, IntPtr.Zero, hSel); // keep the selected folder on screen
                return;
            }
        }
        catch {}
    }

    // ---- wait for a key press OR a left mouse click in the console window ----
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool ReadConsoleInput(IntPtr h, out INPUT_RECORD rec, uint len, out uint read);
    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct KEY_EVENT_RECORD { public int bKeyDown; public ushort wRepeatCount, wVirtualKeyCode, wVirtualScanCode; public char UnicodeChar; public uint dwControlKeyState; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSE_EVENT_RECORD { public COORD dwMousePosition; public uint dwButtonState, dwControlKeyState, dwEventFlags; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD Key; [FieldOffset(4)] public MOUSE_EVENT_RECORD Mouse; }

    static void WaitForKeyOrClick()
    {
        IntPtr hIn = GetStdHandle(-10); // STD_INPUT_HANDLE
        uint old = 0; bool restore = GetConsoleMode(hIn, out old);
        try
        {
            // enable mouse input, turn off QuickEdit so clicks are delivered as events
            SetConsoleMode(hIn, (old | 0x0010u | 0x0080u) & ~0x0040u);
            INPUT_RECORD rec; uint read;
            while (ReadConsoleInput(hIn, out rec, 1, out read))
            {
                if (read == 0) continue;
                if (rec.EventType == 0x0001 && rec.Key.bKeyDown != 0) break;                                  // key down
                if (rec.EventType == 0x0002 && rec.Mouse.dwEventFlags == 0 && (rec.Mouse.dwButtonState & 1) != 0) break; // left click
            }
        }
        catch { try { Console.ReadKey(); } catch {} }
        finally { if (restore) try { SetConsoleMode(hIn, old); } catch {} }
    }

    // show a dialog in the foreground, owned by the (already visible) console window
    static DialogResult ShowFront(CommonDialog d)
    {
        IntPtr h = GetConsoleWindow();
        if (h == IntPtr.Zero) return d.ShowDialog();
        SetForegroundWindow(h);
        return d.ShowDialog(new OwnerWindow { Handle = h });
    }

    // ---------- helpers ----------
    static string RunPS(string cmd)
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd + "\"");
        psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
        var p = Process.Start(psi);
        string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        return o.Trim();
    }

    static string Mount(string iso)
    {
        string e = iso.Replace("'", "''");
        RunPS("Mount-DiskImage -ImagePath '" + e + "' -ErrorAction SilentlyContinue | Out-Null");
        for (int i = 0; i < 12; i++)
        {
            string d = RunPS("(Get-DiskImage -ImagePath '" + e + "' | Get-Volume).DriveLetter");
            if (d.Length >= 1 && char.IsLetter(d[0])) return d.Substring(0, 1);
            Thread.Sleep(1000);
        }
        return null;
    }

    static void Dismount(string iso) { RunPS("Dismount-DiskImage -ImagePath '" + iso.Replace("'", "''") + "' | Out-Null"); }

    static void CopyTree(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (string f in Directory.GetFiles(srcDir)) CopyFile(f, Path.Combine(dstDir, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(srcDir)) CopyTree(d, Path.Combine(dstDir, Path.GetFileName(d)));
    }

    static void CopyFile(string src, string dst)
    {
        long logical = 0; try { logical = new FileInfo(src).Length; } catch {}
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        long read = 0;
        using (var os = new FileStream(dst, FileMode.Create, FileAccess.Write))
        {
            try
            {
                using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read))
                {
                    byte[] buf = new byte[1048576]; int n;
                    while (true)
                    {
                        try { n = fs.Read(buf, 0, buf.Length); } catch { break; }
                        if (n <= 0) break;
                        os.Write(buf, 0, n); read += n;
                    }
                }
            }
            catch {}
            if (read < logical)
            {
                byte[] z = new byte[1048576]; long rem = logical - read;
                while (rem > 0) { int w = (int)Math.Min(rem, z.Length); os.Write(z, 0, w); rem -= w; }
            }
        }
        try { File.SetAttributes(dst, FileAttributes.Normal); } catch {}
    }

    // grant the BUILTIN\Users group (SID S-1-5-32-545, language-independent) modify rights, with inheritance
    static void GrantWrite(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls.exe", "\"" + dir + "\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var p = Process.Start(psi); p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }
        catch {}
    }

    static void ClearReadOnly(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch {}
    }

    static void SetCompat(string exe)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
            k.SetValue(exe, "~ 256COLOR HIGHDPIAWARE", RegistryValueKind.String);
    }

    // if a shortcut with this name already exists, return "name (2).lnk", "name (3).lnk", ...
    static string UniqueLnk(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path);
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string p = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(p)) return p;
        }
        return path;
    }

    static void MakeShortcut(string lnk, string target, string args, string workdir, string icon)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "willy_mksc.vbs");
        var b = new StringBuilder();
        b.Append("Set s=CreateObject(\"WScript.Shell\")\r\n");
        b.Append("Set l=s.CreateShortcut(\"" + lnk + "\")\r\n");
        b.Append("l.TargetPath=\"" + target + "\"\r\n");
        b.Append("l.Arguments=\"" + args + "\"\r\n");
        b.Append("l.WorkingDirectory=\"" + workdir + "\"\r\n");
        b.Append("l.IconLocation=\"" + icon + "\"\r\n");
        b.Append("l.Save\r\n");
        File.WriteAllText(tmp, b.ToString(), Encoding.Default);
        var psi = new ProcessStartInfo("wscript.exe", "\"" + tmp + "\"") { UseShellExecute = false, CreateNoWindow = true };
        var p = Process.Start(psi); p.WaitForExit();
        try { File.Delete(tmp); } catch {}
    }

    const string Readme =
"Autos bauen mit Willy Werkel - made to run on Windows 11\r\n" +
"========================================================\r\n\r\n" +
"HOW TO PLAY\r\n" +
"  Double-click the desktop icon \"Autos bauen mit Willy Werkel\".\r\n" +
"  The launcher does everything automatically: sets 256-colour mode,\r\n" +
"  mounts the game data as drive B:, switches to fullscreen and starts\r\n" +
"  the game. No CD and no installation prompts.\r\n\r\n" +
"  When you quit the game, your normal screen resolution and the\r\n" +
"  taskbar are restored automatically.\r\n\r\n" +
"  A desktop icon \"Autos bauen mit Willy Werkel\" is created (fullscreen).\r\n" +
"  Press Alt+F4 at any time to close the game immediately.\r\n\r\n" +
"WHY IT DID NOT WORK BEFORE\r\n" +
"  - The CD's SETUP.EXE is a 16-bit program; 64-bit Windows cannot run it.\r\n" +
"  - The game looks for its data on a CD drive (drive letters B..Z).\r\n" +
"  - The game needs 256-colour mode.\r\n" +
"  - It expects the program on the hard disk and the data on a different\r\n" +
"    drive. This setup arranges all of that.\r\n\r\n" +
"NOTE ON RESOLUTION\r\n" +
"  The game is from 1997 and drawn at 640x480; true 1920x1080 is not\r\n" +
"  possible. Fullscreen mode upscales the 640x480 picture to fill the\r\n" +
"  screen.\r\n\r\n" +
"NOTE ON DRIVING ROUTES\r\n" +
"  If the ISO is an incomplete rip, the route/background data (ZX_DATEN)\r\n" +
"  may be missing. The workshop / car-building part works fully.\r\n";
}
