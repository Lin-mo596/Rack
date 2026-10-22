// Rack (WPF 版) —— 核心层：配置 / 收纳目录 / 图标 / 托盘 / 全局热键 / 入口
//
// 和 WinForms 版的根本差别：
//   WPF 的渲染走 DirectX，能输出 alpha。配合 DwmExtendFrameIntoClientArea，
//   窗口的"没画东西的地方"会真的透出背后的桌面，而且是实时的（不是截图）。
//   代价是 Win11 已经没有 Aero 的模糊合成，所以透出来的是清晰画面，不是毛玻璃。
//
// 编译（系统自带 csc，不需要 .NET SDK）：
//   csc /target:winexe /codepage:65001 /out:RackWpf.exe ^
//       /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll ^
//       /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
//       Core.cs DockWindow.cs SettingsWindow.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace RackWpf
{
    // ══════════════════════════════════════════════════════════════
    //  Win32 互操作
    // ══════════════════════════════════════════════════════════════
    internal static class Nat
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        internal static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr hObject);

        [DllImport("psapi.dll")]
        internal static extern int EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE { public int cx; public int cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [Flags]
        internal enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4 }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr pbc,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object shellItem);

        // ── HBITMAP → 带 alpha 的位图 ──
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hObj, int size, ref BITMAP bmp);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
                                            IntPtr bits, ref BITMAPINFO bmi, uint usage);

        internal static Bitmap HBitmapToArgb(IntPtr hbmp)
        {
            BITMAP bm = new BITMAP();
            if (GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), ref bm) == 0) return null;
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            try
            {
                using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero))
                {
                    IntPtr hdc = screen.GetHdc();
                    try { GetDIBits(hdc, hbmp, 0, (uint)h, data.Scan0, ref bmi, 0); }
                    finally { screen.ReleaseHdc(hdc); }
                }
            }
            catch { }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        /// <summary>向 Shell 要指定像素尺寸的图标（带透明通道）。</summary>
        internal static Bitmap LoadShellIcon(string path, int px)
        {
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                object obj;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj);
                IShellItemImageFactory factory = (IShellItemImageFactory)obj;
                IntPtr hbmp;
                factory.GetImage(new SIZE(px, px), SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out hbmp);
                if (hbmp == IntPtr.Zero) return null;
                try { return HBitmapToArgb(hbmp); }
                finally { DeleteObject(hbmp); }
            }
            catch
            {
                try
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(path))
                        if (ico != null) return ico.ToBitmap();
                }
                catch { }
                return null;
            }
        }

        internal static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch { }
        }

        // ── 无边框窗口 + 玻璃 ──
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int l, r, t, b; }

        /// <summary>把整个客户区变成"玻璃区域"，没画东西的地方就透出背后。</summary>
        internal static void ExtendGlass(IntPtr hwnd)
        {
            try
            {
                MARGINS m = new MARGINS();
                m.l = -1; m.r = -1; m.t = -1; m.b = -1;
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { }
        }

        // ── 任务栏几何 ──
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);

        internal static int TaskbarHeight()
        {
            try
            {
                IntPtr h = FindWindow("Shell_TrayWnd", null);
                if (h != IntPtr.Zero)
                {
                    RECT r;
                    if (GetWindowRect(h, out r))
                    {
                        int hgt = r.Bottom - r.Top;
                        if (hgt > 0 && hgt < 200) return hgt;
                    }
                }
            }
            catch { }
            return 0;
        }

        // ── 资源管理器重启广播 ──
        // explorer 崩溃重建后会广播 TaskbarCreated，托盘图标得自己重新注册一遍
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        internal static int WM_TASKBARCREATED
        {
            get { try { return (int)RegisterWindowMessage("TaskbarCreated"); } catch { return 0; } }
        }
        // ── 全局热键 ──
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal const uint MOD_ALT = 1;
        internal const uint MOD_CTRL = 2;
        internal const uint MOD_SHIFT = 4;
        internal const uint MOD_WIN = 8;

        internal static bool HotkeyRegister(IntPtr hwnd, int id, uint mods, uint vk)
        {
            try { return RegisterHotKey(hwnd, id, mods, vk); } catch { return false; }
        }

        internal static void HotkeyUnregister(IntPtr hwnd, int id)
        {
            try { UnregisterHotKey(hwnd, id); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  配置
    // ══════════════════════════════════════════════════════════════
    internal static class Cfg
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath { get { return Path.Combine(Store.Root, "config.ini"); } }

        internal static void Load()
        {
            Map.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s[0] == ';' || s[0] == '#') continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1).Trim();
                    int semi = v.IndexOf(';');
                    if (semi >= 0) v = v.Substring(0, semi).Trim();
                    Map[k] = v;
                }
            }
            catch { }
        }

        internal static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; Rack 配置文件（WPF 版）");
                sb.AppendLine("; 可以直接用记事本改，改完要重启程序才生效");
                sb.AppendLine();
                sb.AppendLine("; ── 外观 ──");
                sb.AppendLine("; ui.theme       light = 白色主题（默认），dark = 深色主题");
                sb.AppendLine("; ui.textColor   抽屉里文字的颜色，十六进制不带 #，默认 FFB6C1 浅粉");
                sb.AppendLine("; ui.textShadow  1 = 文字带淡投影（默认），0 = 不要");
                sb.AppendLine("; ui.alpha       那层纱的浓淡 0-255。越小越透：");
                sb.AppendLine(";                0 = 完全透明（默认，只剩图标和文字），150 = 淡白底，230 ≈ 实心");
                sb.AppendLine();
                sb.AppendLine("; ── 行为 ──");
                sb.AppendLine("; beh.keepDesktop 1 = 拖进抽屉时保留桌面原件（等于复制一份）");
                sb.AppendLine(";                 0 = 移走（桌面才会变干净）");
                sb.AppendLine("; beh.singleClick 1 = 左键单击就启动；0 = 双击启动");
                sb.AppendLine("; beh.pinByDefault 1 = 打开抽屉默认钉住；0 = 默认不钉住（点外面自动关）");
                sb.AppendLine("; beh.hotkey      全局快捷键，留空 = 不要热键");
                sb.AppendLine();
                sb.AppendLine("; ── 窗口位置 ── -999999 表示没记录过，回右下角默认位置");
                sb.AppendLine();

                sb.AppendLine("ui.theme=" + (LightTheme ? "light" : "dark"));
                sb.AppendLine("ui.alpha=" + Alpha);
                sb.AppendLine("ui.textColor=" + TextColor);
                sb.AppendLine("ui.textShadow=" + (TextShadow ? "1" : "0"));
                sb.AppendLine("ui.posX=" + PosX);
                sb.AppendLine("ui.posY=" + PosY);
                sb.AppendLine("beh.keepDesktop=" + (KeepDesktop ? "1" : "0"));
                sb.AppendLine("beh.singleClick=" + (SingleClickLaunch ? "1" : "0"));
                sb.AppendLine("beh.pinByDefault=" + (PinByDefault ? "1" : "0"));
                sb.AppendLine("beh.hotkey=" + HotkeyText);

                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        internal static void EnsureFile()
        {
            try { if (!File.Exists(FilePath)) Save(); } catch { }
        }

        internal static string Get(string key, string def)
        {
            string v;
            return Map.TryGetValue(key, out v) ? v : def;
        }

        internal static int GetInt(string key, int def)
        {
            int r;
            return int.TryParse(Get(key, ""), out r) ? r : def;
        }

        internal static void Set(string key, string val) { Map[key] = val; }
        internal static void SetInt(string key, int val) { Map[key] = val.ToString(); }
        internal static void SetBool(string key, bool val) { Map[key] = val ? "1" : "0"; }

        internal static bool LightTheme { get { return Get("ui.theme", "light") == "light"; } }
        /// <summary>抽屉面板上的文字颜色（十六进制，不带 #）。默认浅粉。</summary>
        internal static string TextColor { get { return Get("ui.textColor", "FFB6C1"); } }
        /// <summary>文字是否带一层淡投影（纯透明背景上用来提对比度）。</summary>
        internal static bool TextShadow { get { return Get("ui.textShadow", "1") == "1"; } }
        /// <summary>那层纱的 alpha。越小越透。</summary>
        internal static int Alpha { get { return GetInt("ui.alpha", 0); } }
        internal static bool KeepDesktop { get { return Get("beh.keepDesktop", "0") == "1"; } }
        internal static bool SingleClickLaunch { get { return Get("beh.singleClick", "0") == "1"; } }
        /// <summary>打开抽屉时是否默认钉住（钉住 = 保持常开、点外面不自动关）</summary>
        internal static bool PinByDefault { get { return Get("beh.pinByDefault", "1") == "1"; } }
        internal static string HotkeyText { get { return Get("beh.hotkey", "Ctrl+Alt+Space"); } }
        internal static int PosX { get { return GetInt("ui.posX", -999999); } }
        internal static int PosY { get { return GetInt("ui.posY", -999999); } }
    }

    // ══════════════════════════════════════════════════════════════
    //  收纳目录（和 WinForms 版完全一致，是同一批文件夹）
    // ══════════════════════════════════════════════════════════════
    internal static class Store
    {
        internal static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rack");
        internal static readonly string DockRoot = Path.Combine(Root, "Dock");
        internal static readonly string[] DefaultGroups = { "游戏", "工作", "工具" };

        internal static string DirFor(string group) { return Path.Combine(DockRoot, group); }

        internal static string[] Groups()
        {
            List<string> g = new List<string>();
            try
            {
                if (Directory.Exists(DockRoot))
                    foreach (string d in Directory.GetDirectories(DockRoot))
                        g.Add(Path.GetFileName(d));
            }
            catch { }
            // 先按 order.ini 里记录的分组顺序排；没记录过的按创建时间追加在后
            List<string> ord = Order.Get(Order.GroupKey);
            g.Sort(delegate(string a, string b)
            {
                int ia = ord.IndexOf(a);
                int ib = ord.IndexOf(b);
                if (ia < 0 && ib < 0)
                {
                    DateTime ta = DateTime.MinValue, tb = DateTime.MinValue;
                    try { ta = Directory.GetCreationTime(DirFor(a)); } catch { }
                    try { tb = Directory.GetCreationTime(DirFor(b)); } catch { }
                    int c = ta.CompareTo(tb);
                    if (c != 0) return c;
                    return string.Compare(a, b, StringComparison.CurrentCulture);
                }
                if (ia < 0) return 1;
                if (ib < 0) return -1;
                return ia - ib;
            });
            return g.ToArray();
        }

        internal static void EnsureInit()
        {
            Directory.CreateDirectory(DockRoot);
            if (Groups().Length == 0)
                foreach (string d in DefaultGroups)
                    Directory.CreateDirectory(Path.Combine(DockRoot, d));
        }

        internal static bool CreateGroup(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (name.IndexOf(c) >= 0) return false;
            name = name.Trim();
            if (name.Length == 0 || Directory.Exists(DirFor(name))) return false;
            try { Directory.CreateDirectory(DirFor(name)); return true; }
            catch { return false; }
        }

        internal static bool RenameGroup(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(newName)) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (newName.IndexOf(c) >= 0) return false;
            newName = newName.Trim();
            if (newName.Length == 0 || newName == oldName || Directory.Exists(DirFor(newName))) return false;
            try { Directory.Move(DirFor(oldName), DirFor(newName)); return true; }
            catch { return false; }
        }

        internal static bool DeleteGroup(string name)
        {
            try
            {
                string d = DirFor(name);
                if (!Directory.Exists(d)) return false;
                if (Directory.GetFileSystemEntries(d).Length > 0) return false;
                Directory.Delete(d, false);
                return true;
            }
            catch { return false; }
        }

        private static string UniquePath(string dir, string name)
        {
            string dest = Path.Combine(dir, name);
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int i = 2; i < 9999; i++)
            {
                dest = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
            }
            return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        }

        internal static bool Import(string source, string group, bool keepSource)
        {
            if (string.IsNullOrEmpty(source)) return false;
            string src = source.TrimEnd(Path.DirectorySeparatorChar);
            if (!File.Exists(src) && !Directory.Exists(src)) return false;

            string target = DirFor(group);
            if (!Directory.Exists(target)) return false;
            string name = Path.GetFileName(src);

            try
            {
                string parent = Path.GetDirectoryName(src);
                if (parent != null && string.Equals(parent, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            bool isDir = Directory.Exists(src);

            if (keepSource)
            {
                try
                {
                    string dest = UniquePath(target, name);
                    if (isDir) CopyDir(src, dest);
                    else File.Copy(src, dest, false);
                    return true;
                }
                catch { return false; }
            }

            try
            {
                string dest = UniquePath(target, name);
                if (isDir) Directory.Move(src, dest);
                else File.Move(src, dest);
                return true;
            }
            catch
            {
                try
                {
                    string dest = UniquePath(target, name);
                    if (isDir) CopyDir(src, dest);
                    else File.Copy(src, dest, false);
                    try
                    {
                        if (isDir) Directory.Delete(src, true);
                        else File.Delete(src);
                    }
                    catch { }
                    return true;
                }
                catch { return false; }
            }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        internal static List<string> CollectDesktopItems()
        {
            List<string> result = new List<string>();
            List<string> roots = new List<string>();
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(root))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".lnk" || ext == ".url" || ext == ".exe")
                            result.Add(f);
                    }
                    foreach (string d in Directory.GetDirectories(root)) result.Add(d);
                }
                catch { }
            }
            return result;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  图标顺序：手动拖出来的先后存在 order.ini 里
    //  文件系统本身给不出稳定顺序，所以顺序必须自己记账
    // ══════════════════════════════════════════════════════════════
    internal static class Order
    {
        /// <summary>分组顺序也记在 order.ini 里，用这个保留键。</summary>
        internal const string GroupKey = "__groups__";

        private static readonly Dictionary<string, List<string>> Map =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath { get { return Path.Combine(Store.Root, "order.ini"); } }

        internal static void Load()
        {
            Map.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                string cur = null;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s[0] == ';') continue;
                    if (s[0] == '[' && s[s.Length - 1] == ']')
                    {
                        cur = s.Substring(1, s.Length - 2);
                        if (!Map.ContainsKey(cur)) Map[cur] = new List<string>();
                        continue;
                    }
                    if (cur == null) continue;
                    Map[cur].Add(s);
                }
            }
            catch { }
        }

        internal static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; Rack 图标顺序 —— 在抽屉里拖动图标会自动更新这里");
                sb.AppendLine("; 想让某一组恢复按名称排序，右键该组里任意图标 →「这一组恢复按名称排序」");
                foreach (KeyValuePair<string, List<string>> kv in Map)
                {
                    sb.AppendLine("[" + kv.Key + "]");
                    foreach (string n in kv.Value) sb.AppendLine(n);
                }
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        internal static List<string> Get(string group)
        {
            List<string> list;
            if (group != null && Map.TryGetValue(group, out list)) return new List<string>(list);
            return new List<string>();
        }

        internal static void Clear(string group)
        {
            if (group == null) return;
            Map.Remove(group);
            Save();
        }

        /// <summary>把磁盘上实际存在的文件同步进顺序表：已有的保留，新加的追加到末尾，没有的剔除。</summary>
        internal static void Sync(string group, List<string> existing)
        {
            if (group == null) return;
            List<string> cur = Get(group);
            List<string> result = new List<string>();
            foreach (string n in cur) if (existing.Contains(n)) result.Add(n);
            foreach (string n in existing) if (!result.Contains(n)) result.Add(n);

            bool changed = (result.Count != cur.Count);
            if (!changed)
                for (int i = 0; i < result.Count; i++)
                    if (result[i] != cur[i]) { changed = true; break; }

            Map[group] = result;
            if (changed) Save();
        }

        /// <summary>把 srcName 挪到 targetName 前面（before=true）或后面。</summary>
        internal static void MoveRelative(string group, string srcName, string targetName, bool before)
        {
            if (group == null || string.IsNullOrEmpty(srcName) || string.IsNullOrEmpty(targetName)) return;
            if (srcName == targetName) return;

            List<string> list = Get(group);
            if (!list.Contains(srcName)) list.Add(srcName);
            if (!list.Contains(targetName)) list.Add(targetName);

            list.Remove(srcName);
            int idx = list.IndexOf(targetName);
            if (idx < 0) idx = list.Count;
            else if (!before) idx++;

            list.Insert(idx, srcName);
            Map[group] = list;
            Save();
        }
    }
    // ══════════════════════════════════════════════════════════════
    //  图标缓存（转成 WPF 的 BitmapSource）
    // ══════════════════════════════════════════════════════════════
    internal static class Icons
    {
        private static readonly Dictionary<string, BitmapSource> Cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        internal static void Clear() { Cache.Clear(); }

        internal static BitmapSource Get(string path, int px)
        {
            string key = px.ToString() + "|" + path;
            BitmapSource hit;
            if (Cache.TryGetValue(key, out hit)) return hit;

            BitmapSource src = null;

            // .url 是"网址快捷方式"，Shell 一律给默认的地球图标，不会去读文件里写的 IconFile。
            // Steam 生成的 .url 里正好带着 IconFile=真正的 .ico，直接按它加载。
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                src = LoadUrlIcon(path, px);

            if (src == null)
            {
                try
                {
                    using (Bitmap bmp = Nat.LoadShellIcon(path, px))
                    {
                        if (bmp != null) src = ToSource(bmp);
                    }
                }
                catch { }
            }

            Cache[key] = src;
            return src;
        }

        /// <summary>从 .url 文件里读 IconFile= 那一行，按它拿图标。</summary>
        private static BitmapSource LoadUrlIcon(string urlPath, int px)
        {
            try
            {
                string ico = null;
                foreach (string line in File.ReadAllLines(urlPath))
                {
                    string s = line.Trim();
                    if (s.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        ico = s.Substring(9).Trim();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(ico) || !File.Exists(ico)) return null;
                return LoadIcoFile(ico, px);
            }
            catch { return null; }
        }

        /// <summary>
        /// 自己解析 .ico 结构来取图标。
        /// 起因：.NET 的 Icon.ToBitmap() 对"PNG 压缩的 ICO 子图"有 bug，解出来是彩色噪点。
        /// 现代图标（尤其 Steam 生成的那种）大量用 PNG 压缩，所以这里绕开它：
        ///   PNG 子图 → 直接把字节交给 WPF 解码
        ///   BMP 子图 → 才用 Icon 类
        /// </summary>
        private static BitmapSource LoadIcoFile(string icoPath, int px)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(icoPath);
                if (bytes.Length < 22) return null;

                int count = BitConverter.ToUInt16(bytes, 4);
                int bestScore = int.MaxValue;
                int bestW = 0;
                int bestOff = 0;
                int bestSize = 0;

                for (int i = 0; i < count; i++)
                {
                    int off = 6 + i * 16;
                    if (off + 16 > bytes.Length) break;
                    int w = bytes[off] == 0 ? 256 : bytes[off];
                    uint size = BitConverter.ToUInt32(bytes, off + 8);
                    uint dataOff = BitConverter.ToUInt32(bytes, off + 12);
                    if (dataOff + size > bytes.Length || size == 0) continue;

                    // 打分：优先挑不小于目标尺寸的最小那个；比目标小的排后面
                    int score = (w >= px) ? (w - px) : (px - w) + 1000;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestW = w;
                        bestOff = (int)dataOff;
                        bestSize = (int)size;
                    }
                }
                if (bestW == 0) return null;

                bool isPng = (bytes[bestOff] == 0x89 && bytes[bestOff + 1] == 0x50
                              && bytes[bestOff + 2] == 0x4E && bytes[bestOff + 3] == 0x47);

                if (isPng)
                {
                    using (MemoryStream ms = new MemoryStream(bytes, bestOff, bestSize))
                    {
                        BitmapImage img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = ms;
                        img.EndInit();
                        img.Freeze();
                        return img;
                    }
                }

                using (Icon ic = new Icon(icoPath, bestW, bestW))
                {
                    using (Bitmap bmp = ic.ToBitmap()) return ToSource(bmp);
                }
            }
            catch { return null; }
        }

        /// <summary>GDI+ Bitmap → WPF BitmapSource，保住 alpha 通道。</summary>
        internal static BitmapSource ToSource(Bitmap bmp)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    ms.Position = 0;
                    BitmapImage img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch { return null; }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  配色（和 WinForms 版一致，WPF 侧再包一层）
    // ══════════════════════════════════════════════════════════════
    internal static class Theme
    {
        internal static bool Light { get { return Cfg.LightTheme; } }

        internal static System.Windows.Media.Color Text
        {
            get { return Light ? System.Windows.Media.Color.FromRgb(26, 28, 34)
                              : System.Windows.Media.Color.FromRgb(240, 242, 246); }
        }
        internal static System.Windows.Media.Color TextDim
        {
            get { return Light ? System.Windows.Media.Color.FromRgb(96, 100, 110)
                              : System.Windows.Media.Color.FromRgb(168, 172, 182); }
        }
        internal static System.Windows.Media.Color Accent
        {
            get { return Light ? System.Windows.Media.Color.FromRgb(37, 99, 235)
                              : System.Windows.Media.Color.FromRgb(110, 175, 255); }
        }
        internal static System.Windows.Media.Color AccentSoft
        {
            get { return Light ? System.Windows.Media.Color.FromRgb(198, 216, 248)
                              : System.Windows.Media.Color.FromRgb(50, 98, 154); }
        }
        internal static System.Windows.Media.Color Hover
        {
            get { return Light ? System.Windows.Media.Color.FromRgb(228, 232, 240)
                              : System.Windows.Media.Color.FromRgb(58, 58, 68); }
        }
        internal static System.Windows.Media.Color Line
        {
            get { return Light ? System.Windows.Media.Color.FromRgb(198, 204, 216)
                              : System.Windows.Media.Color.FromRgb(72, 78, 90); }
        }

        /// <summary>抽屉面板上文字的颜色。可配置，默认浅粉。</summary>
        internal static System.Windows.Media.Color PanelText
        {
            get
            {
                try
                {
                    string s = Cfg.TextColor.Trim();
                    if (s.StartsWith("#")) s = s.Substring(1);
                    uint v = Convert.ToUInt32(s, 16);
                    return System.Windows.Media.Color.FromRgb(
                        (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
                }
                catch { return System.Windows.Media.Color.FromRgb(255, 182, 193); }
            }
        }

        /// <summary>同一颜色的淡化版，给未选中的标签用。</summary>
        internal static System.Windows.Media.Color PanelTextDim
        {
            get
            {
                System.Windows.Media.Color c = PanelText;
                return System.Windows.Media.Color.FromArgb(150, c.R, c.G, c.B);
            }
        }

        /// <summary>叠在透明玻璃上的那层纱。alpha 越小越透。</summary>
        internal static System.Windows.Media.Brush Veil(int alpha)
        {
            byte a = (byte)(alpha < 0 ? 0 : (alpha > 255 ? 255 : alpha));
            System.Windows.Media.Color c = Light
                ? System.Windows.Media.Color.FromArgb(a, 252, 252, 255)
                : System.Windows.Media.Color.FromArgb(a, 12, 12, 16);
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        internal static System.Windows.Media.Brush Solid(System.Windows.Media.Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        internal static System.Windows.Media.FontFamily Font
        {
            get { return new System.Windows.Media.FontFamily("Microsoft YaHei UI"); }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  热键字符串 <-> (mods, vk)
    // ══════════════════════════════════════════════════════════════
    internal static class HotkeyText
    {
        internal static bool Parse(string s, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string raw in s.Split('+'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                string lp = p.ToLowerInvariant();
                if (lp == "ctrl" || lp == "control") mods |= Nat.MOD_CTRL;
                else if (lp == "alt") mods |= Nat.MOD_ALT;
                else if (lp == "shift") mods |= Nat.MOD_SHIFT;
                else if (lp == "win") mods |= Nat.MOD_WIN;
                else
                {
                    try
                    {
                        object o = Enum.Parse(typeof(WinForms.Keys), p, true);
                        WinForms.Keys k = (WinForms.Keys)o;
                        if (k == WinForms.Keys.None) return false;
                        vk = (uint)k;
                    }
                    catch { return false; }
                }
            }
            return mods != 0 && vk != 0;
        }

        internal static string Format(uint mods, uint vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((mods & Nat.MOD_CTRL) != 0) sb.Append("Ctrl+");
            if ((mods & Nat.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Nat.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Nat.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(((WinForms.Keys)vk).ToString());
            return sb.ToString();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  托盘 + 热键 + 窗口管理
    // ══════════════════════════════════════════════════════════════
    internal sealed class TrayApp
    {
        private const int HOTKEY_ID = 0x5241;

        private readonly WinForms.NotifyIcon _tray;
        private readonly Icon _icon;
        private DockWindow _dock;
        private WinForms.ToolStripMenuItem _autoStartItem;
        private HotkeyHost _hotkeyHost;
        private readonly bool _preview;

        internal TrayApp(bool preview)
        {
            _preview = preview;
            Store.EnsureInit();
            Cfg.Load();
            Cfg.EnsureFile();
            Order.Load();
            

            _icon = BuildTrayIcon();

            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
            menu.Items.Add("打开抽屉", null, delegate { ToggleDock(); });
            menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("打开收纳文件夹", null, delegate { OpenFolder(Store.DockRoot); });
            menu.Items.Add("打开程序文件夹", null, delegate { OpenFolder(Store.Root); });
            menu.Items.Add(new WinForms.ToolStripSeparator());

            _autoStartItem = new WinForms.ToolStripMenuItem("开机自动启动");
            _autoStartItem.Checked = ReadAutoStart();
            _autoStartItem.Click += delegate
            {
                WriteAutoStart(!ReadAutoStart());
                _autoStartItem.Checked = ReadAutoStart();
            };
            menu.Items.Add(_autoStartItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitAll(); });

            _tray = new WinForms.NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "Rack 抽屉 — 左键打开";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.MouseUp += delegate(object s, WinForms.MouseEventArgs e)
            {
                if (e.Button == WinForms.MouseButtons.Left) ToggleDock();
            };

            SetupHotkey();

            if (preview) ToggleDock();
            else
            {
                EnsureAutoStartOnce();
                ShowFirstRunHint();
            }
        }

        private void SetupHotkey()
        {
            if (_hotkeyHost == null)
            {
                _hotkeyHost = new HotkeyHost();
                _hotkeyHost.Hotkey += delegate { ToggleDock(); };
                // explorer 崩了重启、任务栏重建之后，托盘图标会全部消失，
                // 收到这条广播就把图标重新挂上去，用户不用手动重开程序
                _hotkeyHost.TaskbarCreated += delegate
                {
                    try
                    {
                        _tray.Visible = false;
                        _tray.Visible = true;
                        try
                        {
                            File.AppendAllText(Path.Combine(Store.Root, "crash.log"),
                                DateTime.Now.ToString("s") + "  explorer 重启，已重新挂上托盘图标" + Environment.NewLine);
                        }
                        catch { }
                    }
                    catch { }
                };
            }
            Nat.HotkeyUnregister(_hotkeyHost.Handle, HOTKEY_ID);
            uint mods, vk;
            if (!HotkeyText.Parse(Cfg.HotkeyText, out mods, out vk)) return;
            bool ok = Nat.HotkeyRegister(_hotkeyHost.Handle, HOTKEY_ID, mods, vk);
            if (!ok)
            {
                try
                {
                    _tray.ShowBalloonTip(6000, "快捷键没注册上",
                        "「" + Cfg.HotkeyText + "」可能被别的软件占用了，去设置里换一个组合。",
                        WinForms.ToolTipIcon.Warning);
                }
                catch { }
            }
        }

        internal void CloseDock()
        {
            if (_dock != null) { _dock.Close(); _dock = null; }
        }

        internal void ReloadHotkey() { SetupHotkey(); }

        private void ToggleDock()
        {
            if (_dock != null && _dock.IsVisible)
            {
                _dock.Close();
                _dock = null;
                return;
            }
            _dock = new DockWindow(_preview);
            _dock.Closed += delegate { _dock = null; };
            // 从抽屉里打开设置并保存后，也要重新注册热键
            _dock.SettingsSaved += delegate
            {
                SetupHotkey();
                _autoStartItem.Checked = ReadAutoStart();
            };
            _dock.ShowAtTray();
        }

        private void OpenSettings()
        {
            SettingsWindow sw = new SettingsWindow();
            sw.Owner = (_dock != null && _dock.IsVisible) ? _dock : null;
            bool ok = sw.ShowDialog() == true;
            if (ok)
            {
                _autoStartItem.Checked = ReadAutoStart();
                SetupHotkey();
                if (_dock != null) _dock.RefreshAll();
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private void ExitAll()
        {
            try
            {
                if (_dock != null) { _dock.Close(); _dock = null; }
                if (_hotkeyHost != null)
                {
                    Nat.HotkeyUnregister(_hotkeyHost.Handle, HOTKEY_ID);
                    _hotkeyHost.DestroyHandle();
                    _hotkeyHost = null;
                }
                _tray.Visible = false;
                _tray.Dispose();
                _icon.Dispose();
            }
            catch { }
            System.Windows.Application.Current.Shutdown();
        }

        private void EnsureAutoStartOnce()
        {
            string flag = Path.Combine(Store.Root, ".autostart.wpf");
            if (File.Exists(flag)) return;
            try
            {
                if (!ReadAutoStart()) WriteAutoStart(true);
                _autoStartItem.Checked = ReadAutoStart();
                File.WriteAllText(flag, DateTime.Now.ToString("s"));
            }
            catch { }
        }

        private void ShowFirstRunHint()
        {
            string flag = Path.Combine(Store.Root, ".hinted.wpf");
            if (File.Exists(flag)) return;
            try
            {
                _tray.ShowBalloonTip(9000, "Rack 已在后台运行",
                    "图标可能藏在任务栏的「^」里。把它从「^」拖到任务栏上，以后就能直接点开了。",
                    WinForms.ToolTipIcon.Info);
                File.WriteAllText(flag, DateTime.Now.ToString("s"));
            }
            catch { }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "RackDock";

        internal static bool ReadAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }

        internal static void WriteAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        private static Icon BuildTrayIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                RectangleF body = new RectangleF(3f, 6f, 26f, 22f);
                using (System.Drawing.Drawing2D.GraphicsPath p = RoundRect(body, 5f))
                using (System.Drawing.Drawing2D.LinearGradientBrush b =
                    new System.Drawing.Drawing2D.LinearGradientBrush(body,
                        System.Drawing.Color.FromArgb(140, 195, 255),
                        System.Drawing.Color.FromArgb(58, 130, 246), 90f))
                    g.FillPath(b, p);

                using (SolidBrush w = new SolidBrush(System.Drawing.Color.FromArgb(235, 255, 255, 255)))
                {
                    g.FillRectangle(w, 9f, 12f, 14f, 3f);
                    g.FillRectangle(w, 9f, 18f, 14f, 3f);
                }
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
            }
            finally { Nat.DestroyIcon(h); bmp.Dispose(); }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float radius)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  全局热键的消息窗口
    // ══════════════════════════════════════════════════════════════
    internal sealed class HotkeyHost : WinForms.NativeWindow
    {
        internal event Action<int> Hotkey;
        internal event Action TaskbarCreated;
        private readonly int _wmTaskbar;

        internal HotkeyHost()
        {
            CreateHandle(new WinForms.CreateParams());
            _wmTaskbar = Nat.WM_TASKBARCREATED;
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (Hotkey != null) Hotkey(id);
                return;
            }
            if (_wmTaskbar != 0 && m.Msg == _wmTaskbar)
            {
                if (TaskbarCreated != null) TaskbarCreated();
                return;
            }
            base.WndProc(ref m);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════
    internal static class Program
    {
        [STAThread]
        internal static void Main()
        {
            // 必须在任何窗口之前声明 DPI 感知
            try { Nat.SetProcessDpiAwarenessContext(Nat.PerMonitorV2); } catch { }

            string[] argv = Environment.GetCommandLineArgs();
            string mode = argv.Length > 1 ? argv[1].ToLowerInvariant() : "";

            Store.EnsureInit();
            Cfg.Load();
            Cfg.EnsureFile();
            Order.Load();
            

            // 崩了要知道为什么：把没接住的异常写到 crash.log
            AppDomain.CurrentDomain.UnhandledException += delegate(object s2, UnhandledExceptionEventArgs e2)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Store.Root, "crash.log"),
                        DateTime.Now.ToString("s") + "  [AppDomain]  " + e2.ExceptionObject + Environment.NewLine);
                }
                catch { }
            };

            System.Windows.Application app = new System.Windows.Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object s2, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e2)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Store.Root, "crash.log"),
                        DateTime.Now.ToString("s") + "  [UI]  " + e2.Exception + Environment.NewLine);
                }
                catch { }
                e2.Handled = true;   // 别因为一个界面异常就把整个抽屉服务带下线
            };

            if (mode == "--settings")
            {
                SettingsWindow sw = new SettingsWindow();
                app.Run(sw);
                return;
            }

            TrayApp tray = null;
            app.Startup += delegate { tray = new TrayApp(mode == "--preview"); };

            bool created;
            using (Mutex mtx = new Mutex(true, "Local\\RackDock_single", out created))
            {
                if (!created) return;
                app.Run();
                if (tray != null) { }
            }
            Nat.TrimMemory();
        }
    }
}
