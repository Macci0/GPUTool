// GPUTool - small Windows front end for nvidia-smi
// Power limit, live values, history graph (power, temp, load, clock, VRAM) with auto scaling,
// compact graph-only mode, selectable curve colours, smart window transparency,
// data logging, autostart task
//
// Copyright (C) 2026 WickiM - https://github.com/Macci0/GPUTool
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program (file LICENSE).  If not, see <https://www.gnu.org/licenses/>.
//
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Build on Windows (no install needed, C# 5 compatible) - or just run build.bat:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:GPUTool.exe src\GPUTool.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("GPUTool")]
[assembly: AssemblyDescription("Power limit, monitoring and logging for NVIDIA GPUs via nvidia-smi")]
[assembly: AssemblyCompany("WickiM")]
[assembly: AssemblyProduct("GPUTool")]
[assembly: AssemblyCopyright("Copyright (C) 2026 WickiM, GPL-3.0-or-later")]
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

namespace GPUTool
{
    static class AppInfo
    {
        public const string Name = "GPUTool";
        public const string Version = "1.5.0";
        public const string Author = "WickiM";
        public const string Copyright = "\u00A9 2026 WickiM";
        public const string Url = "https://github.com/Macci0/GPUTool";
        public const string License = "GNU GPL v3 or later";
    }

    class Sample
    {
        public DateTime T;
        public double Power, Temp, Clock, Util, Limit, MemUsed, MemTotal;
    }

    class GpuInfo
    {
        public string Index;
        public string BusId;
        public string Name;
        public double Min;
        public double Max;
        public double Default;
        public double Limit;
        public double MaxClock = double.NaN;
        public List<Sample> History = new List<Sample>();

        public override string ToString()
        {
            return Index + ": " + Name + "  (" + BusId + ")";
        }
    }

    class SmiResult
    {
        public int Code;
        public string Output;
    }

    class CheckItem
    {
        public string Name;
        public bool Ok;
        public bool Fatal;     // app cannot work without this
        public string Detail;
    }

    // ================= Helpers =================

    static class Util
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly char[] Comma = new[] { ',' };
        public static readonly char[] NewLines = new[] { '\r', '\n' };
        // logs go next to the exe if that folder is writable (same place for every user account),
        // otherwise into the TEMP folder of the current account
        public static string LogPath = ResolveLogPath();
        public static string DefaultDataLogPath = Path.Combine(Path.GetDirectoryName(LogPath), "GPUTool_data.txt");

        static string ResolveLogPath()
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPUTool.log");
                using (FileStream fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return p;
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "GPUTool.log");
            }
        }

        public static void Log(string s)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "  " + s + Environment.NewLine);
            }
            catch { }
        }

        public static bool IsAdmin()
        {
            try
            {
                WindowsPrincipal p = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string FindSmi()
        {
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (File.Exists(sys)) return sys;
            string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                     @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
            if (File.Exists(pf)) return pf;
            return "nvidia-smi.exe";
        }

        public static SmiResult Run(string exe, string args)
        {
            SmiResult r = new SmiResult();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    Task<string> err = p.StandardError.ReadToEndAsync();
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    r.Code = p.HasExited ? p.ExitCode : -1;
                    r.Output = (outp + err.Result).Trim();
                }
            }
            catch (Exception ex)
            {
                r.Code = -1;
                r.Output = ex.Message;
            }
            return r;
        }

        public static double ParseD(string s)
        {
            double d;
            if (double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d)) return d;
            return double.NaN;
        }

        public static string Fmt(double d, string unit)
        {
            if (double.IsNaN(d)) return "N/A";
            return d.ToString("0.#", Inv) + " " + unit;
        }

        public static string Mem(double usedMiB, double totalMiB)
        {
            if (double.IsNaN(usedMiB)) return "N/A";
            if (!double.IsNaN(totalMiB) && totalMiB >= 1024)
                return (usedMiB / 1024.0).ToString("0.0", Inv) + " / " + (totalMiB / 1024.0).ToString("0.#", Inv) + " GiB";
            return usedMiB.ToString("0", Inv) + (double.IsNaN(totalMiB) ? "" : " / " + totalMiB.ToString("0", Inv)) + " MiB";
        }

        public static string Num(double d)
        {
            if (double.IsNaN(d)) return "N/A";
            return d.ToString("0.##", Inv);
        }

        static string NetName(int release)
        {
            if (release >= 533320) return "4.8.1";
            if (release >= 528040) return "4.8";
            if (release >= 461808) return "4.7.2";
            if (release >= 461308) return "4.7.1";
            if (release >= 460798) return "4.7";
            if (release >= 394802) return "4.6.2";
            if (release >= 394254) return "4.6.1";
            if (release >= 393295) return "4.6";
            if (release >= 379893) return "4.5.2";
            if (release >= 378675) return "4.5.1";
            if (release >= 378389) return "4.5";
            return "4.0 (too old)";
        }

        // ---------- Self-check ----------

        public static List<CheckItem> RunChecks(string smi)
        {
            List<CheckItem> list = new List<CheckItem>();

            // 1. .NET Framework
            CheckItem net = new CheckItem { Name = ".NET Framework", Fatal = true };
            try
            {
                int release = 0;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("Release");
                        if (v != null) release = Convert.ToInt32(v, Inv);
                    }
                }
                net.Ok = release >= 378389;
                net.Detail = (release > 0 ? NetName(release) + " (release " + release + ")" : "not found")
                             + ", CLR " + Environment.Version + (Environment.Is64BitProcess ? ", 64-bit" : ", 32-bit");
                if (!net.Ok) net.Detail += " - requires 4.5 or newer";
            }
            catch (Exception ex)
            {
                net.Ok = false;
                net.Detail = "Check failed: " + ex.Message;
            }
            list.Add(net);

            // 2. Administrator rights
            bool admin = IsAdmin();
            list.Add(new CheckItem
            {
                Name = "Administrator rights",
                Ok = admin,
                Fatal = false,
                Detail = admin ? "available" : "missing - monitoring only, limit/autostart disabled"
            });

            // 3. nvidia-smi present
            bool smiFile = File.Exists(smi);
            list.Add(new CheckItem
            {
                Name = "nvidia-smi found",
                Ok = smiFile,
                Fatal = false,
                Detail = smiFile ? smi : "not in System32 or NVSMI folder, trying search path (PATH)"
            });

            // 4. nvidia-smi runs + driver
            CheckItem run = new CheckItem { Name = "nvidia-smi / driver", Fatal = true };
            SmiResult r = Run(smi, "--query-gpu=driver_version,name --format=csv,noheader");
            if (r.Code == 0 && r.Output.Length > 0)
            {
                string[] lines = r.Output.Split(NewLines, StringSplitOptions.RemoveEmptyEntries);
                string drv = lines[0].Split(Comma)[0].Trim();
                run.Ok = true;
                run.Detail = "driver " + drv + ", " + lines.Length + " GPU(s)";
            }
            else
            {
                run.Ok = false;
                run.Detail = "Error (code " + r.Code + "): " + r.Output;
            }
            list.Add(run);

            // 5. Task Scheduler
            string sch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            bool schOk = File.Exists(sch);
            list.Add(new CheckItem
            {
                Name = "Task Scheduler",
                Ok = schOk,
                Fatal = false,
                Detail = schOk ? "schtasks.exe present" : "schtasks.exe missing - autostart not possible"
            });

            foreach (CheckItem c in list)
                Log("Check " + (c.Ok ? "OK      " : (c.Fatal ? "ERROR   " : "WARNING ")) + c.Name + ": " + c.Detail);

            return list;
        }

        public static string ChecksText(List<CheckItem> checks, string dataLog)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppInfo.Name + " v" + AppInfo.Version + "  -  " + AppInfo.Copyright);
            sb.AppendLine(AppInfo.Url);
            sb.AppendLine("Free software under the " + AppInfo.License + ", comes with ABSOLUTELY NO WARRANTY.");
            sb.AppendLine("Not affiliated with NVIDIA.");
            sb.AppendLine();
            foreach (CheckItem c in checks)
            {
                string tag = c.Ok ? "[OK]      " : (c.Fatal ? "[ERROR]   " : "[WARNING] ");
                sb.AppendLine(tag + c.Name);
                sb.AppendLine("          " + c.Detail);
            }
            sb.AppendLine();
            sb.AppendLine("App/crash log: " + LogPath);
            sb.AppendLine("Settings:      " + Settings.FilePath);
            if (dataLog != null) sb.AppendLine("Data log:      " + dataLog);
            return sb.ToString();
        }
    }

    // ================= Settings file =================

    // Settings are kept in GPUTool.ini next to the exe (portable, same file for every user account,
    // e.g. when the UAC prompt is confirmed with another admin account). If that folder is not
    // writable (e.g. C:\Program Files) the file goes to %APPDATA%\GPUTool\GPUTool.ini instead.
    static class Settings
    {
        public static readonly string FilePath = ResolvePath();
        static readonly string OldFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"GPUTool\settings.txt");

        static string ResolvePath()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPUTool.ini");
            try
            {
                using (FileStream fs = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return local;
            }
            catch
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"GPUTool\GPUTool.ini");
            }
        }

        public static Dictionary<string, string> Load()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            string file = FilePath;
            try
            {
                // first start with the ini: take over settings of older versions
                if ((!File.Exists(file) || new FileInfo(file).Length == 0) && File.Exists(OldFile))
                {
                    file = OldFile;
                    Util.Log("Settings: importing " + OldFile);
                }
                if (!File.Exists(file)) return d;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (Exception ex) { Util.Log("Settings load failed: " + ex.Message); }
            return d;
        }

        public static void Save(Dictionary<string, string> d)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                List<string> keys = new List<string>(d.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                List<string> lines = new List<string>();
                lines.Add("; " + AppInfo.Name + " settings - written by the app, edit only while it is closed");
                lines.Add("[" + AppInfo.Name + "]");
                foreach (string k in keys) lines.Add(k + "=" + d[k]);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex) { Util.Log("Settings save failed: " + ex.Message); }
        }

        public static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        public static int GetInt(Dictionary<string, string> d, string key, int def)
        {
            int v;
            return int.TryParse(Get(d, key, ""), NumberStyles.Integer, Util.Inv, out v) ? v : def;
        }
    }

    // ================= Self-check window =================

    class CheckForm : Form
    {
        CheckForm(List<CheckItem> checks, bool startup, bool allowContinue, string dataLog)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = AppInfo.Name + " - System check";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 330);

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            TextBox tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = SystemColors.Window,
                Text = Util.ChecksText(checks, dataLog)
            };
            root.Controls.Add(tb, 0, 0);

            // bottom row: project link left, buttons right
            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            LinkLabel link = new LinkLabel { Text = AppInfo.Url.Replace("https://", ""), AutoSize = true, Anchor = AnchorStyles.Left };
            link.LinkClicked += delegate
            {
                try { Process.Start(AppInfo.Url); }
                catch (Exception ex) { Util.Log("Open URL failed: " + ex.Message); }
            };
            bottom.Controls.Add(link, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            Button ok = new Button
            {
                Text = startup ? "Continue" : "Close",
                AutoSize = true,
                MinimumSize = new Size(90, 28),
                DialogResult = DialogResult.OK,
                Enabled = allowContinue
            };
            buttons.Controls.Add(ok);
            AcceptButton = ok;

            if (startup)
            {
                Button quit = new Button
                {
                    Text = "Exit",
                    AutoSize = true,
                    MinimumSize = new Size(90, 28),
                    DialogResult = DialogResult.Cancel
                };
                buttons.Controls.Add(quit);
                CancelButton = quit;
            }
            else CancelButton = ok;
            bottom.Controls.Add(buttons, 1, 0);
            root.Controls.Add(bottom, 0, 1);

            ResumeLayout(false);
            PerformLayout();

            Load += delegate { MinimumSize = new Size(Width * 3 / 4, Height * 3 / 4); };
            Shown += delegate { tb.SelectionStart = 0; tb.SelectionLength = 0; (allowContinue ? (Control)ok : tb).Focus(); };
        }

        public static bool ShowChecks(IWin32Window owner, List<CheckItem> checks, bool startup, bool allowContinue, string dataLog)
        {
            using (CheckForm f = new CheckForm(checks, startup, allowContinue, dataLog))
            {
                DialogResult r = owner == null ? f.ShowDialog() : f.ShowDialog(owner);
                return r == DialogResult.OK;
            }
        }
    }

    // ================= Series check box =================
    // Click on the box: show/hide the curve. Click on the coloured name: LabelClick (pick a colour).

    class SeriesCheckBox : CheckBox
    {
        public string Key;
        public event EventHandler LabelClick;
        int downX = -1;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            downX = e.Button == MouseButtons.Left ? e.X : -1;
            base.OnMouseDown(e);
        }

        protected override void OnClick(EventArgs e)
        {
            int glyph = 16;
            try
            {
                using (Graphics g = CreateGraphics())
                    glyph = CheckBoxRenderer.GetGlyphSize(g, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width + 3;
            }
            catch { }
            bool onLabel = downX > glyph;
            downX = -1;
            if (onLabel && LabelClick != null)
            {
                LabelClick(this, e);
                return;   // no toggle
            }
            base.OnClick(e);
        }
    }

    // ================= Graph =================

    class GraphPanel : Panel
    {
        // default curve colours (user colours are stored in settings.txt)
        public static readonly Dictionary<string, Color> DefaultColors = new Dictionary<string, Color>
        {
            { "Power", Color.FromArgb(230, 120, 0) },
            { "Temp", Color.FromArgb(210, 40, 40) },
            { "Load", Color.FromArgb(40, 150, 60) },
            { "Clock", Color.FromArgb(50, 100, 210) },
            { "VRAM", Color.FromArgb(140, 60, 180) }
        };

        public Color CPower = DefaultColors["Power"];
        public Color CTemp = DefaultColors["Temp"];
        public Color CUtil = DefaultColors["Load"];
        public Color CClock = DefaultColors["Clock"];
        public Color CMem = DefaultColors["VRAM"];

        public void SetColor(string key, Color c)
        {
            switch (key)
            {
                case "Power": CPower = c; break;
                case "Temp": CTemp = c; break;
                case "Load": CUtil = c; break;
                case "Clock": CClock = c; break;
                case "VRAM": CMem = c; break;
            }
            Invalidate();
        }

        public GpuInfo Gpu;
        public int RangeSeconds = 300;
        public bool ShowPower = true, ShowTemp = true, ShowUtil = true, ShowClock = false, ShowMem = true;
        public bool AutoScale = true;
        public bool ShowValues = false;   // current values in the header line (compact mode)

        // auto scaling: grows immediately when a value would leave the plot,
        // shrinks only after the narrower range has been wanted for ShrinkDelay seconds
        const double ShrinkDelay = 20;

        class Axis
        {
            public double Lo, Hi;
            public bool Valid;
            public DateTime PendingSince = DateTime.MinValue;
        }

        Axis axLeft = new Axis(), axRight = new Axis();

        public void ResetScale()
        {
            axLeft.Valid = false;
            axRight.Valid = false;
            Invalidate();
        }

        public GraphPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Window;
            BorderStyle = BorderStyle.FixedSingle;
        }

        static string RangeText(int s)
        {
            if (s >= 60) return "-" + (s / 60) + " min";
            return "-" + s + " s";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Font f = new Font(Font.FontFamily, Math.Max(6.5f, Font.SizeInPoints - 1.5f)))
            using (Brush txt = new SolidBrush(SystemColors.GrayText))
            using (Pen grid = new Pen(Color.FromArgb(225, 225, 225)))
            {
                DateTime now = DateTime.Now;
                double maxClock = Gpu != null ? Gpu.MaxClock : double.NaN;
                bool clockOk = !double.IsNaN(maxClock) && maxClock > 0;

                // ---- axis ranges ----
                List<Func<Sample, double>> rightSeries = new List<Func<Sample, double>>();
                if (ShowTemp) rightSeries.Add(delegate(Sample s) { return s.Temp; });
                if (ShowUtil) rightSeries.Add(delegate(Sample s) { return s.Util; });
                if (ShowMem) rightSeries.Add(MemPct);
                if (ShowClock && clockOk) rightSeries.Add(delegate(Sample s) { return s.Clock / maxClock * 100.0; });

                // fixed scales: 0..max power limit (rounded up) and 0..100
                double maxW = 100;
                if (Gpu != null && !double.IsNaN(Gpu.Max) && Gpu.Max > 0) maxW = Gpu.Max;
                if (Gpu != null)
                    foreach (Sample s in Gpu.History)
                        if (!double.IsNaN(s.Power) && s.Power > maxW) maxW = s.Power;
                maxW = Math.Ceiling(maxW / 25.0) * 25.0;

                double lLo = 0, lHi = maxW, rLo = 0, rHi = 100;
                if (AutoScale && Gpu != null && Gpu.History.Count >= 2)
                {
                    double dMin, dMax;
                    List<Func<Sample, double>> leftSeries = new List<Func<Sample, double>>();
                    if (ShowPower) leftSeries.Add(delegate(Sample s) { return s.Power; });

                    if (DataRange(leftSeries, now, out dMin, out dMax))
                    {
                        UpdateAxis(axLeft, dMin, dMax, 20, double.MaxValue, now);
                        lLo = axLeft.Lo; lHi = axLeft.Hi;
                    }
                    else axLeft.Valid = false;

                    if (DataRange(rightSeries, now, out dMin, out dMax))
                    {
                        UpdateAxis(axRight, dMin, dMax, 10, 100, now);
                        rLo = axRight.Lo; rHi = axRight.Hi;
                    }
                    else axRight.Valid = false;
                }

                // ---- axis labels, margins sized to the widest label (saves room in narrow windows) ----
                string[] lLab = new string[5], rLab = new string[5];
                float lW = 0, rW = 0;
                for (int i = 0; i <= 4; i++)
                {
                    // left (watt) axis only carries labels while the power curve is shown
                    lLab[i] = ShowPower ? AxisLabel(lLo + (lHi - lLo) * i / 4) : "";
                    rLab[i] = AxisLabel(rLo + (rHi - rLo) * i / 4);
                    lW = Math.Max(lW, g.MeasureString(lLab[i], f).Width);
                    rW = Math.Max(rW, g.MeasureString(rLab[i], f).Width);
                }

                float fh = f.GetHeight(g);
                int left = (int)Math.Ceiling(lW) + 4;
                int rightM = (int)Math.Ceiling(rW) + 4;
                int top = (int)Math.Ceiling(fh * 1.5f) + 4;
                int bottom = (int)Math.Ceiling(fh) + 4;
                Rectangle r = new Rectangle(left, top,
                                            Math.Max(10, ClientSize.Width - left - rightM),
                                            Math.Max(10, ClientSize.Height - top - bottom));

                StringFormat right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                StringFormat leftF = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

                // vertical time grid (quarters)
                for (int i = 1; i < 4; i++)
                {
                    float x = r.Left + r.Width * i / 4f;
                    g.DrawLine(grid, x, r.Top, x, r.Bottom);
                }

                // horizontal grid + axis labels
                for (int i = 0; i <= 4; i++)
                {
                    float y = r.Bottom - r.Height * i / 4f;
                    g.DrawLine(grid, r.Left, y, r.Right, y);
                    g.DrawString(lLab[i], f, txt, new RectangleF(0, y - fh, r.Left - 2, fh * 2), right);
                    g.DrawString(rLab[i], f, txt, new RectangleF(r.Right + 2, y - fh, rightM, fh * 2), leftF);
                }

                // header line: units (and current values in compact mode)
                DrawHeader(g, f, txt, fh);

                // time labels below the plot
                string rt = RangeText(RangeSeconds);
                SizeF nowSize = g.MeasureString("now", f);
                if (g.MeasureString(rt, f).Width + nowSize.Width + 6 <= r.Width)
                    g.DrawString(rt, f, txt, r.Left, r.Bottom + 2);
                g.DrawString("now", f, txt, r.Right - nowSize.Width, r.Bottom + 2);

                if (Gpu == null || Gpu.History.Count < 2)
                {
                    g.DrawString("Waiting...", Font, txt, r.Left + 4, r.Top + 4);
                    return;
                }

                if (ShowClock && clockOk)
                    DrawSeries(g, r, CClock, now, delegate(Sample s) { return s.Clock / maxClock * 100.0; }, rLo, rHi);
                if (ShowMem) DrawSeries(g, r, CMem, now, MemPct, rLo, rHi);
                if (ShowUtil) DrawSeries(g, r, CUtil, now, delegate(Sample s) { return s.Util; }, rLo, rHi);
                if (ShowTemp) DrawSeries(g, r, CTemp, now, delegate(Sample s) { return s.Temp; }, rLo, rHi);
                if (ShowPower) DrawSeries(g, r, CPower, now, delegate(Sample s) { return s.Power; }, lLo, lHi);
            }
        }

        // Header line above the plot. Normal mode: "W auto" left, "°C / %" right.
        // Compact mode: current values (coloured like their curves, clock left out) have priority;
        // values that don't fit are dropped from the end, units are only drawn if there is room.
        void DrawHeader(Graphics g, Font f, Brush txt, float fh)
        {
            int w = ClientSize.Width;
            string unitL = ShowPower ? (AutoScale ? "W  auto" : "W") : (AutoScale ? "auto" : "");
            const string unitR = "°C / %";
            float uL = g.MeasureString(unitL, f).Width, uLs = g.MeasureString("W", f).Width;
            float uR = g.MeasureString(unitR, f).Width;

            List<KeyValuePair<string, Color>> parts = new List<KeyValuePair<string, Color>>();
            if (ShowValues && Gpu != null && Gpu.History.Count > 0)
            {
                Sample s = Gpu.History[Gpu.History.Count - 1];
                if (ShowPower) parts.Add(new KeyValuePair<string, Color>(Util.Fmt(s.Power, "W"), CPower));
                if (ShowTemp) parts.Add(new KeyValuePair<string, Color>(Util.Fmt(s.Temp, "°C"), CTemp));
                if (ShowUtil) parts.Add(new KeyValuePair<string, Color>(Util.Fmt(s.Util, "%"), CUtil));
                if (ShowMem) parts.Add(new KeyValuePair<string, Color>(Util.Mem(s.MemUsed, s.MemTotal), CMem));
            }

            if (parts.Count == 0)
            {
                g.DrawString(unitL, f, txt, 2, 1);
                g.DrawString(unitR, f, txt, w - 2 - uR, 1);
                return;
            }

            using (Font bf = new Font(f, FontStyle.Bold))
            {
                float gap = g.MeasureString(" ", bf).Width * 1.5f;
                List<float> widths = new List<float>();
                foreach (KeyValuePair<string, Color> kv in parts) widths.Add(g.MeasureString(kv.Key, bf).Width);

                float total = 0;
                int count = parts.Count;
                while (count > 0)
                {
                    total = 0;
                    for (int i = 0; i < count; i++) total += widths[i] + (i > 0 ? gap : 0);
                    if (total <= w - 4) break;
                    count--;
                }
                if (count == 0) return;

                float x0 = (w - total) / 2f;
                // units only where they don't collide with the values
                if (x0 >= 2 + uL + 6 && x0 + total <= w - 2 - uR - 6)
                {
                    g.DrawString(unitL, f, txt, 2, 1);
                    g.DrawString(unitR, f, txt, w - 2 - uR, 1);
                }
                else if (x0 >= 2 + uLs + 4 && x0 + total <= w - 2 - uR - 4)
                {
                    if (ShowPower) g.DrawString("W", f, txt, 2, 1);
                    g.DrawString(unitR, f, txt, w - 2 - uR, 1);
                }

                float x = x0;
                float y = Math.Max(1, fh * 0.5f - 1);
                for (int i = 0; i < count; i++)
                {
                    using (Brush b = new SolidBrush(parts[i].Value)) g.DrawString(parts[i].Key, bf, b, x, y);
                    x += widths[i] + gap;
                }
            }
        }

        static double MemPct(Sample s)
        {
            return s.MemTotal > 0 ? s.MemUsed / s.MemTotal * 100.0 : double.NaN;
        }

        static string AxisLabel(double v)
        {
            return v.ToString(Math.Abs(v - Math.Round(v)) < 0.001 ? "0" : "0.#", Util.Inv);
        }

        // min/max of all given series inside the visible time range
        bool DataRange(List<Func<Sample, double>> series, DateTime now, out double dMin, out double dMax)
        {
            dMin = double.MaxValue;
            dMax = double.MinValue;
            if (Gpu == null || series.Count == 0) return false;
            foreach (Sample s in Gpu.History)
            {
                if ((now - s.T).TotalSeconds > RangeSeconds) continue;
                foreach (Func<Sample, double> fn in series)
                {
                    double v = fn(s);
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (v < dMin) dMin = v;
                    if (v > dMax) dMax = v;
                }
            }
            return dMin <= dMax;
        }

        // nice axis range around the data: padding of 25 % of the span (at least half of minSpan)
        // on both sides, 4 grid steps of a "round" size
        static void NiceRange(double dMin, double dMax, double minSpan, double cap, out double lo, out double hi)
        {
            double span = dMax - dMin;
            double pad = Math.Max(span * 0.25, minSpan * 0.5);
            double a = dMin - pad, b = dMax + pad;
            if (dMin >= 0 && a < 0) a = 0;
            // keep e.g. percent axes from running past 100 when the data doesn't
            if (dMax <= cap && b > cap) b = cap;

            double[] steps = { 0.5, 1, 2, 2.5, 5, 10, 15, 20, 25, 30, 40, 50, 60, 75, 100, 150, 200, 250, 500, 1000, 2500, 5000 };
            foreach (double step in steps)
            {
                double l = Math.Floor(a / step) * step;
                double h = l + 4 * step;
                if (h < b) continue;
                // range would run past the cap (e.g. 100 %): align it to the cap instead if that still fits
                if (h > cap && cap - 4 * step >= 0 && cap - 4 * step <= a)
                {
                    l = cap - 4 * step;
                    h = cap;
                }
                lo = l;
                hi = h;
                return;
            }
            lo = a;
            hi = b;
        }

        static void UpdateAxis(Axis ax, double dMin, double dMax, double minSpan, double cap, DateTime now)
        {
            double tLo, tHi;
            NiceRange(dMin, dMax, minSpan, cap, out tLo, out tHi);

            // first time, or data would leave the plot: rescale at once
            if (!ax.Valid || dMin < ax.Lo || dMax > ax.Hi)
            {
                ax.Lo = tLo;
                ax.Hi = tHi;
                ax.Valid = true;
                ax.PendingSince = DateTime.MinValue;
                return;
            }

            // data fits: only zoom in after the narrower range was wanted for a while
            if (Math.Abs(tLo - ax.Lo) < 1e-9 && Math.Abs(tHi - ax.Hi) < 1e-9)
            {
                ax.PendingSince = DateTime.MinValue;
                return;
            }
            if (ax.PendingSince == DateTime.MinValue)
            {
                ax.PendingSince = now;
                return;
            }
            if ((now - ax.PendingSince).TotalSeconds >= ShrinkDelay)
            {
                ax.Lo = tLo;
                ax.Hi = tHi;
                ax.PendingSince = DateTime.MinValue;
            }
        }

        // Draws one curve. Samples falling into the same pixel column are combined
        // (first, min, max, last), so long time ranges stay fast and short spikes stay visible.
        void DrawSeries(Graphics g, Rectangle r, Color c, DateTime now, Func<Sample, double> val, double lo, double hi)
        {
            double span = hi - lo;
            if (span <= 0) return;
            using (Pen p = new Pen(c, 1.6f))
            {
                List<PointF> seg = new List<PointF>();
                int col = int.MinValue, n = 0;
                float firstX = 0, firstY = 0, minY = 0, maxY = 0, lastY = 0;

                foreach (Sample s in Gpu.History)
                {
                    double age = (now - s.T).TotalSeconds;
                    if (age > RangeSeconds) continue;
                    double v = val(s);
                    if (double.IsNaN(v) || double.IsInfinity(v))
                    {
                        EmitColumn(seg, col, n, firstX, firstY, minY, maxY, lastY);
                        n = 0;
                        col = int.MinValue;
                        Flush(g, p, seg);
                        continue;
                    }
                    v = Math.Max(lo, Math.Min(hi, v));
                    float x = (float)(r.Right - age / RangeSeconds * r.Width);
                    float y = (float)(r.Bottom - (v - lo) / span * r.Height);
                    int cx = (int)Math.Floor(x);
                    if (cx != col)
                    {
                        EmitColumn(seg, col, n, firstX, firstY, minY, maxY, lastY);
                        col = cx;
                        n = 0;
                    }
                    if (n == 0) { firstX = x; firstY = y; minY = y; maxY = y; }
                    else { if (y < minY) minY = y; if (y > maxY) maxY = y; }
                    lastY = y;
                    n++;
                }
                EmitColumn(seg, col, n, firstX, firstY, minY, maxY, lastY);
                Flush(g, p, seg);
            }
        }

        static void EmitColumn(List<PointF> seg, int col, int n, float firstX, float firstY, float minY, float maxY, float lastY)
        {
            if (n == 0) return;
            if (n == 1)
            {
                seg.Add(new PointF(firstX, firstY));
                return;
            }
            float x = col + 0.5f;
            seg.Add(new PointF(x, firstY));
            if (minY != firstY && minY != lastY) seg.Add(new PointF(x, minY));
            if (maxY != firstY && maxY != lastY) seg.Add(new PointF(x, maxY));
            seg.Add(new PointF(x, lastY));
        }

        static void Flush(Graphics g, Pen p, List<PointF> seg)
        {
            if (seg.Count >= 2) g.DrawLines(p, seg.ToArray());
            seg.Clear();
        }
    }

    // ================= Main window =================

    class MainForm : Form
    {
        const string TaskName = "GPUTool Power-Limit";
        const int HistorySeconds = 3600;  // keep 60 min (approx. 100 bytes per sample and GPU)

        ComboBox cboGpu, cboRange;
        Label lblDraw, lblTemp, lblClock, lblUtil, lblLimit, lblMem, lblStatus, lblLogInfo;
        TrackBar trk;
        NumericUpDown num, numInterval;
        Button btnApply, btnDefault, btnTask, btnTaskDel, btnInfo, btnBrowse, btnOpenFolder;
        SeriesCheckBox chkPower, chkTemp, chkUtil, chkClock, chkMem;
        CheckBox chkAuto, chkLog, chkTransp, chkTranspCompact;
        NumericUpDown numTransp;
        Timer fadeTimer;
        uint ownPid;
        TextBox txtPath;
        TabControl tabs;
        GraphPanel graph;
        Timer timer;

        List<GpuInfo> gpus = new List<GpuInfo>();
        Dictionary<string, string> settings;
        StreamWriter logWriter;
        DateTime lastLog = DateTime.MinValue;
        int logLines;
        bool busy, syncing, togglingLog, admin;
        int pollCount;
        ToolTip infoTip;
        static readonly string[] RangeSecs = { "60", "300", "900", "3600" };

        // compact graph-only mode
        TableLayoutPanel rootPanel, graphTable;
        Panel compactPanel;
        TabPage tGraphPage;
        bool compact;
        Size normalMin;
        float dpiK = 1f;
        string baseTitle;
        bool switchingMode;
        ContextMenuStrip graphMenu;
        ToolStripMenuItem miTop, miNormal;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        string smi;
        List<CheckItem> checks;

        public MainForm(string smiPath, List<CheckItem> startupChecks)
        {
            smi = smiPath;
            checks = startupChecks;
            admin = Util.IsAdmin();
            settings = Settings.Load();

            SuspendLayout();
            // sizes below are given for 96 DPI and scaled automatically to the screen DPI
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            baseTitle = AppInfo.Name + (admin ? "" : " (no admin rights)");
            Text = baseTitle;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(490, 380);

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6) };
            rootPanel = root;
            compactPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(2) };
            compactPanel.MouseDown += delegate(object sender, MouseEventArgs e) { StartDrag(e); };
            Controls.Add(compactPanel);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // --- row 0: GPU selection ---
            TableLayoutPanel top = Table(3);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = "GPU:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            cboGpu = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            cboGpu.SelectedIndexChanged += delegate { OnGpuChanged(); };
            top.Controls.Add(cboGpu, 1, 0);
            btnInfo = new Button { Text = "?", Width = 28, Anchor = AnchorStyles.Left };
            btnInfo.Click += delegate { OnInfo(); };
            infoTip = new ToolTip();
            UpdateInfoButton();
            top.Controls.Add(btnInfo, 2, 0);
            root.Controls.Add(top, 0, 0);

            // --- row 1: live values (2 columns) ---
            TableLayoutPanel vals = Table(2);
            vals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            vals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            lblDraw = ValueLabel(); vals.Controls.Add(lblDraw, 0, 0);
            lblLimit = ValueLabel(); vals.Controls.Add(lblLimit, 1, 0);
            lblTemp = ValueLabel(); vals.Controls.Add(lblTemp, 0, 1);
            lblUtil = ValueLabel(); vals.Controls.Add(lblUtil, 1, 1);
            lblClock = ValueLabel(); vals.Controls.Add(lblClock, 0, 2);
            lblMem = ValueLabel(); vals.Controls.Add(lblMem, 1, 2);
            vals.Margin = new Padding(0, 4, 0, 2);
            root.Controls.Add(vals, 0, 1);

            // --- row 2: tabs (grow with the window) ---
            tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
            TabPage tSet = new TabPage("Settings") { Padding = new Padding(4) };
            TabPage tGraph = new TabPage("Graph") { Padding = new Padding(4) };
            TabPage tLog = new TabPage("Logging") { Padding = new Padding(4) };
            tabs.TabPages.Add(tSet);
            tabs.TabPages.Add(tGraph);
            tabs.TabPages.Add(tLog);
            tabs.SelectedIndexChanged += delegate
            {
                if (tabs.SelectedIndex == 1) graph.Invalidate();
                UpdateMinSize();
            };
            root.Controls.Add(tabs, 0, 2);

            BuildSettingsTab(tSet);
            BuildGraphTab(tGraph);
            compactPanel.ContextMenuStrip = graphMenu;
            BuildLogTab(tLog);

            // --- row 3: status line ---
            lblStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 0) };
            root.Controls.Add(lblStatus, 0, 3);

            ResumeLayout(false);
            PerformLayout();

            LoadGpus();

            timer = new Timer { Interval = 1000 };
            timer.Tick += delegate { Poll(); };
            timer.Start();

            Load += delegate
            {
                // normal layout needs at least the start size; only the graph tab may go smaller
                normalMin = Size;
                using (Graphics g = CreateGraphics()) dpiK = g.DpiX / 96f;
                RestoreWindow();
                UpdateMinSize();
            };
            ResizeEnd += delegate { SaveSettings(); };   // after moving / resizing
            Resize += delegate { CheckCompact(); };
            Shown += delegate
            {
                Poll();
                // resume logging if it was on when the app was last closed (off on first start)
                if (Settings.Get(settings, "logEnabled", "0") == "1") chkLog.Checked = true;
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                Util.Log("Window closing, reason: " + e.CloseReason);
                OnClosing();
            };
            Util.Log("Main window created, admin: " + admin);
        }

        // ---------- window position ----------

        void RestoreWindow()
        {
            int tab = Settings.GetInt(settings, "tab", 0);
            if (tab >= 0 && tab < tabs.TabCount) tabs.SelectedIndex = tab;

            int x = Settings.GetInt(settings, "winX", int.MinValue), y = Settings.GetInt(settings, "winY", int.MinValue);
            int w = Settings.GetInt(settings, "winW", 0), h = Settings.GetInt(settings, "winH", 0);
            if (x == int.MinValue || y == int.MinValue || w <= 0 || h <= 0) return;

            Rectangle rc = new Rectangle(x, y, w, h);
            // only if the window would be reachable (screen setup may have changed)
            bool visible = false;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(sc.WorkingArea, new Rectangle(rc.X, rc.Y, rc.Width, Math.Min(rc.Height, 40)));
                if (inter.Width >= 60 && inter.Height >= 20) { visible = true; break; }
            }
            if (!visible) { Util.Log("Saved window position is off-screen, using default."); return; }

            bool wasCompact = Settings.Get(settings, "compact", "0") == "1";
            if (wasCompact) tabs.SelectedIndex = 1;
            UpdateMinSize();
            StartPosition = FormStartPosition.Manual;
            Bounds = rc;              // may switch to compact mode (Resize -> CheckCompact)
            if (compact) Bounds = rc; // borderless window: apply again, the frame size changed
            if (Settings.Get(settings, "maximized", "0") == "1" && !compact) WindowState = FormWindowState.Maximized;
        }

        void StoreWindow()
        {
            Rectangle rc = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (rc.Width <= 0 || rc.Height <= 0) return;
            settings["winX"] = rc.X.ToString(Util.Inv);
            settings["winY"] = rc.Y.ToString(Util.Inv);
            settings["winW"] = rc.Width.ToString(Util.Inv);
            settings["winH"] = rc.Height.ToString(Util.Inv);
            settings["maximized"] = WindowState == FormWindowState.Maximized ? "1" : "0";
            settings["compact"] = compact ? "1" : "0";
            settings["tab"] = tabs.SelectedIndex.ToString(Util.Inv);
            GpuInfo g = Current;
            if (g != null) settings["gpu"] = g.BusId;
        }

        // ---------- compact graph-only mode ----------
        // On the Graph tab the window may be made smaller than the normal layout;
        // below the normal size only the graph and the time range box are shown.

        void UpdateMinSize()
        {
            if (normalMin.IsEmpty) return;
            MinimumSize = (tabs.SelectedIndex == 1 || compact)
                ? new Size((int)(150 * dpiK), (int)(160 * dpiK))
                : normalMin;
        }

        void CheckCompact()
        {
            if (normalMin.IsEmpty || switchingMode || WindowState == FormWindowState.Minimized) return;
            bool want = tabs.SelectedIndex == 1 && (Width < normalMin.Width || Height < normalMin.Height);
            if (want != compact) SetCompact(want);
        }

        void SetCompact(bool on)
        {
            if (switchingMode) return;
            switchingMode = true;
            compact = on;
            SuspendLayout();

            // compact: no title bar; a thin frame (form padding) stays for resizing,
            // the window is moved by dragging the graph
            FormBorderStyle = on ? FormBorderStyle.None : FormBorderStyle.Sizable;
            MaximizeBox = !on;
            Padding = on ? new Padding(Math.Max(3, (int)(4 * dpiK))) : Padding.Empty;
            BackColor = on ? SystemColors.ControlDark : SystemColors.Control;
            compactPanel.BackColor = SystemColors.Control;
            miNormal.Visible = on;

            graphTable.Parent = on ? (Control)compactPanel : tGraphPage;
            rootPanel.Visible = !on;
            compactPanel.Visible = on;
            foreach (CheckBox cb in new[] { chkPower, chkTemp, chkUtil, chkClock, chkMem, chkAuto }) cb.Visible = !on;
            graph.ShowValues = on;
            UpdateTitle();
            ResumeLayout(true);
            graph.Invalidate();
            switchingMode = false;
            Util.Log("Compact mode " + (on ? "on" : "off"));
        }

        // back to the normal layout (context menu)
        void LeaveCompact()
        {
            if (!compact) return;
            Size target = normalMin;
            SetCompact(false);
            Size = new Size(Math.Max(Width, target.Width), Math.Max(Height, target.Height));
        }

        // drag the borderless compact window by its graph / background
        void StartDrag(MouseEventArgs e)
        {
            if (!compact || e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }

        // borderless compact window: the thin frame acts as resize border
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84 /* WM_NCHITTEST */ || !compact || (int)m.Result != 1 /* HTCLIENT */) return;

            int lp = unchecked((int)(long)m.LParam);
            Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int grip = Math.Max(4, (int)(6 * dpiK));
            bool l = p.X < grip, r = p.X >= ClientSize.Width - grip;
            bool t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;

            if (t && l) m.Result = (IntPtr)13;       // HTTOPLEFT
            else if (t && r) m.Result = (IntPtr)14;  // HTTOPRIGHT
            else if (b && l) m.Result = (IntPtr)16;  // HTBOTTOMLEFT
            else if (b && r) m.Result = (IntPtr)17;  // HTBOTTOMRIGHT
            else if (l) m.Result = (IntPtr)10;       // HTLEFT
            else if (r) m.Result = (IntPtr)11;       // HTRIGHT
            else if (t) m.Result = (IntPtr)12;       // HTTOP
            else if (b) m.Result = (IntPtr)15;       // HTBOTTOM
            else m.Result = (IntPtr)2;               // HTCAPTION (drag)
        }

        void UpdateTitle()
        {
            GpuInfo g = Current;
            Text = compact && g != null ? g.Name + " - " + AppInfo.Name : baseTitle;
        }

        // ---------- layout helpers ----------

        static TableLayoutPanel Table(int cols)
        {
            return new TableLayoutPanel
            {
                ColumnCount = cols,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        }

        static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0)
            };
        }

        static Label ValueLabel()
        {
            return new Label { AutoSize = true, Text = "-", Margin = new Padding(3, 2, 3, 2) };
        }

        static Label RowLabel(string text)
        {
            return new Label { Text = text, AutoSize = true };
        }

        // One row of controls side by side, all centred vertically on the same middle line
        // (TableLayoutPanel + Anchor.Left centres each control in its cell).
        static TableLayoutPanel CenteredRow(params Control[] controls)
        {
            TableLayoutPanel row = Table(controls.Length + 1);
            row.RowCount = 1;
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < controls.Length; i++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                controls[i].Anchor = AnchorStyles.Left;
                Padding m = controls[i].Margin;
                controls[i].Margin = new Padding(m.Left, 3, m.Right, 3);
                row.Controls.Add(controls[i], i, 0);
            }
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // filler
            return row;
        }

        void BuildSettingsTab(TabPage t)
        {
            TableLayoutPanel tl = Table(1);
            tl.Dock = DockStyle.Top;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            num = new NumericUpDown { Width = 70, DecimalPlaces = 0, Increment = 5 };
            num.ValueChanged += delegate { SyncFromNum(); };
            btnDefault = new Button { Text = "Default", AutoSize = true, Margin = new Padding(8, 3, 3, 3) };
            btnDefault.Click += delegate { OnDefault(); };
            tl.Controls.Add(CenteredRow(RowLabel("New limit (W):"), num, btnDefault), 0, 0);

            trk = new TrackBar { Dock = DockStyle.Fill, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Margin = new Padding(0, 6, 0, 0) };
            trk.ValueChanged += delegate { SyncFromTrack(); };
            tl.Controls.Add(trk, 0, 1);

            FlowLayoutPanel row3 = Flow();
            btnApply = new Button { Text = "Apply", AutoSize = true, MinimumSize = new Size(80, 0) };
            btnApply.Click += delegate { OnApply(); };
            row3.Controls.Add(btnApply);
            btnTask = new Button { Text = "Set autostart", AutoSize = true };
            btnTask.Click += delegate { OnTaskCreate(); };
            row3.Controls.Add(btnTask);
            btnTaskDel = new Button { Text = "Remove autostart", AutoSize = true };
            btnTaskDel.Click += delegate { OnTaskDelete(); };
            row3.Controls.Add(btnTaskDel);
            tl.Controls.Add(row3, 0, 2);

            BuildTransparencyRow(tl, 3);

            t.Controls.Add(tl);
        }

        void BuildGraphTab(TabPage t)
        {
            TableLayoutPanel tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            graphTable = tl;
            tGraphPage = t;

            graph = new GraphPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 3) };
            graph.MouseDown += delegate(object sender, MouseEventArgs e) { StartDrag(e); };

            graphMenu = new ContextMenuStrip();
            miTop = new ToolStripMenuItem("Always on top") { CheckOnClick = true, Checked = Settings.Get(settings, "topMost", "0") == "1" };
            miTop.CheckedChanged += delegate { TopMost = miTop.Checked; };
            TopMost = miTop.Checked;
            miNormal = new ToolStripMenuItem("Normal view") { Visible = false };
            miNormal.Click += delegate { LeaveCompact(); };
            ToolStripMenuItem miClose = new ToolStripMenuItem("Close");
            miClose.Click += delegate { Close(); };
            ToolStripMenuItem miColors = new ToolStripMenuItem("Reset colours");
            miColors.Click += delegate { ResetColors(); };
            graphMenu.Items.Add(miNormal);
            graphMenu.Items.Add(miTop);
            graphMenu.Items.Add(miColors);
            graphMenu.Items.Add(new ToolStripSeparator());
            graphMenu.Items.Add(miClose);
            graph.ContextMenuStrip = graphMenu;
            tl.Controls.Add(graph, 0, 0);

            FlowLayoutPanel bar = Flow();
            bar.MouseDown += delegate(object sender, MouseEventArgs e) { StartDrag(e); };
            bar.ContextMenuStrip = graphMenu;
            cboRange = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
            cboRange.Items.AddRange(new object[] { "1 min", "5 min", "15 min", "60 min" });
            int rngIdx = Array.IndexOf(RangeSecs, Settings.Get(settings, "graphRange", "300"));
            cboRange.SelectedIndex = rngIdx >= 0 ? rngIdx : 1;
            graph.RangeSeconds = int.Parse(RangeSecs[cboRange.SelectedIndex], Util.Inv);
            cboRange.SelectedIndexChanged += delegate
            {
                graph.RangeSeconds = int.Parse(RangeSecs[cboRange.SelectedIndex], Util.Inv);
                graph.ResetScale();
            };
            bar.Controls.Add(cboRange);

            ToolTip seriesTip = new ToolTip();
            chkPower = AddSeriesBox(bar, "Power", true, seriesTip);
            chkTemp = AddSeriesBox(bar, "Temp", true, seriesTip);
            chkUtil = AddSeriesBox(bar, "Load", true, seriesTip);
            chkClock = AddSeriesBox(bar, "Clock", false, seriesTip);
            chkMem = AddSeriesBox(bar, "VRAM", true, seriesTip);
            UpdateSeriesFlags();

            chkAuto = new CheckBox
            {
                Text = "Auto",
                AutoSize = true,
                Checked = Settings.Get(settings, "graphAuto", "1") == "1",
                Margin = new Padding(12, 5, 0, 3)
            };
            graph.AutoScale = chkAuto.Checked;
            chkAuto.CheckedChanged += delegate
            {
                graph.AutoScale = chkAuto.Checked;
                graph.ResetScale();
            };
            new ToolTip().SetToolTip(chkAuto, "Fit the axes to the visible data (zooms in slowly, zooms out at once)");
            bar.Controls.Add(chkAuto);
            tl.Controls.Add(bar, 0, 1);

            t.Controls.Add(tl);
        }

        SeriesCheckBox AddSeriesBox(Control parent, string key, bool defaultOn, ToolTip tip)
        {
            Color c = LoadColor(key);
            graph.SetColor(key, c);
            SeriesCheckBox cb = new SeriesCheckBox
            {
                Key = key,
                Text = key,
                ForeColor = c,
                AutoSize = true,
                Checked = Settings.Get(settings, "show" + key, defaultOn ? "1" : "0") == "1",
                Margin = new Padding(5, 5, 0, 3),
                Cursor = Cursors.Hand
            };
            cb.CheckedChanged += delegate { UpdateSeriesFlags(); graph.ResetScale(); };
            cb.LabelClick += delegate { PickColor(cb); };
            tip.SetToolTip(cb, "Box: show/hide the curve - name: choose its colour");
            parent.Controls.Add(cb);
            return cb;
        }

        void UpdateSeriesFlags()
        {
            if (chkPower == null || chkTemp == null || chkUtil == null || chkClock == null || chkMem == null) return;
            graph.ShowPower = chkPower.Checked;
            graph.ShowTemp = chkTemp.Checked;
            graph.ShowUtil = chkUtil.Checked;
            graph.ShowClock = chkClock.Checked;
            graph.ShowMem = chkMem.Checked;
        }

        Color LoadColor(string key)
        {
            string v = Settings.Get(settings, "color" + key, "");
            int argb;
            if (v.Length > 0 && int.TryParse(v, NumberStyles.HexNumber, Util.Inv, out argb))
                return Color.FromArgb(255, Color.FromArgb(argb));
            return GraphPanel.DefaultColors[key];
        }

        void PickColor(SeriesCheckBox cb)
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.Color = cb.ForeColor;
                d.FullOpen = true;
                d.AnyColor = true;
                List<int> custom = new List<int>();
                foreach (Color dc in GraphPanel.DefaultColors.Values) custom.Add(dc.R | (dc.G << 8) | (dc.B << 16));
                d.CustomColors = custom.ToArray();
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ApplyColor(cb, d.Color);
            }
        }

        void ApplyColor(SeriesCheckBox cb, Color c)
        {
            cb.ForeColor = c;
            graph.SetColor(cb.Key, c);
            settings["color" + cb.Key] = (c.ToArgb() & 0xFFFFFF).ToString("X6", Util.Inv);
            Settings.Save(settings);
        }

        void ResetColors()
        {
            foreach (SeriesCheckBox cb in new[] { chkPower, chkTemp, chkUtil, chkClock, chkMem })
            {
                ApplyColor(cb, GraphPanel.DefaultColors[cb.Key]);
                settings.Remove("color" + cb.Key);
            }
            Settings.Save(settings);
        }

        // ---------- smart transparency ----------
        // The window fades to the chosen opacity while the app is in the background and the
        // mouse is not over it; it becomes fully opaque again on hover or when activated.

        void BuildTransparencyRow(TableLayoutPanel tl, int row)
        {
            // grid: [Opacity (%):] [number] [x Transparency when inactive]
            //                               [x only in compact graph view]
            TableLayoutPanel g = Table(3);
            g.RowCount = 2;
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g.Margin = new Padding(0, 8, 0, 0);

            // Anchor = Left centres label and boxes vertically in their row
            g.Controls.Add(new Label { Text = "Opacity (%):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 3) }, 0, 0);

            int lvl;
            if (!int.TryParse(Settings.Get(settings, "transpLevel", "60"), NumberStyles.Integer, Util.Inv, out lvl)) lvl = 60;
            numTransp = new NumericUpDown
            {
                Width = 55,
                Minimum = 20,
                Maximum = 95,
                Increment = 5,
                Value = Math.Max(20, Math.Min(95, lvl)),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 3, 12, 3)
            };
            g.Controls.Add(numTransp, 1, 0);

            chkTransp = new CheckBox
            {
                Text = "Transparency when inactive",
                AutoSize = true,
                Checked = Settings.Get(settings, "transp", "0") == "1",
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 3, 3, 0)
            };
            g.Controls.Add(chkTransp, 2, 0);

            chkTranspCompact = new CheckBox
            {
                Text = "only in compact graph view",
                AutoSize = true,
                Checked = Settings.Get(settings, "transpCompact", "1") == "1",
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 0, 3, 3)
            };
            g.Controls.Add(chkTranspCompact, 2, 1);

            tl.Controls.Add(g, 0, row);

            new ToolTip().SetToolTip(chkTransp,
                "Fades the window while you work in other programs. Fully visible again when the mouse is over it.");

            EventHandler changed = delegate
            {
                numTransp.Enabled = chkTransp.Checked;
                chkTranspCompact.Enabled = chkTransp.Checked;
                SaveSettings();
                FadeTick();
            };
            chkTransp.CheckedChanged += changed;
            numTransp.ValueChanged += changed;
            chkTranspCompact.CheckedChanged += changed;
            numTransp.Enabled = chkTransp.Checked;
            chkTranspCompact.Enabled = chkTransp.Checked;

            fadeTimer = new Timer { Interval = 50 };
            fadeTimer.Tick += delegate { FadeTick(); };
            fadeTimer.Start();
        }

        bool AppInForeground()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (ownPid == 0) ownPid = (uint)Process.GetCurrentProcess().Id;
                return pid == ownPid;
            }
            catch { return true; }
        }

        void FadeTick()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            double target = 1.0;
            bool active = chkTransp != null && chkTransp.Checked && (compact || !chkTranspCompact.Checked);
            if (active && !Bounds.Contains(Cursor.Position) && !AppInForeground())
                target = (double)numTransp.Value / 100.0;

            double cur = Opacity;
            if (Math.Abs(cur - target) < 0.01)
            {
                if (cur != target) Opacity = target;
                return;
            }
            // quick to appear, slower to fade out
            double k = target > cur ? 0.45 : 0.15;
            Opacity = cur + (target - cur) * k;
        }

        void BuildLogTab(TabPage t)
        {
            TableLayoutPanel tl = Table(1);
            tl.Dock = DockStyle.Top;
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            chkLog = new CheckBox { Text = "Write data log (all GPUs)", AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
            chkLog.CheckedChanged += delegate { OnLogToggle(); };
            tl.Controls.Add(chkLog, 0, 0);

            TableLayoutPanel pathRow = Table(3);
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(new Label { Text = "File:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            txtPath = new TextBox { Dock = DockStyle.Fill, Text = Settings.Get(settings, "logPath", Util.DefaultDataLogPath) };
            pathRow.Controls.Add(txtPath, 1, 0);
            btnBrowse = new Button { Text = "...", Width = 30, Anchor = AnchorStyles.Left };
            btnBrowse.Click += delegate { OnBrowse(); };
            pathRow.Controls.Add(btnBrowse, 2, 0);
            tl.Controls.Add(pathRow, 0, 1);

            numInterval = new NumericUpDown { Width = 60, Minimum = 1, Maximum = 3600 };
            int iv;
            if (!int.TryParse(Settings.Get(settings, "logInterval", "5"), NumberStyles.Integer, Util.Inv, out iv)) iv = 5;
            numInterval.Value = Math.Max(1, Math.Min(3600, iv));
            tl.Controls.Add(CenteredRow(RowLabel("Interval (s):"), numInterval), 0, 2);

            FlowLayoutPanel btnRow = Flow();
            btnOpenFolder = new Button { Text = "Open folder", AutoSize = true };
            btnOpenFolder.Click += delegate { OnOpenFolder(); };
            btnRow.Controls.Add(btnOpenFolder);
            Button btnDefPath = new Button { Text = "Default path", AutoSize = true };
            btnDefPath.Click += delegate { if (logWriter == null) txtPath.Text = Util.DefaultDataLogPath; };
            btnRow.Controls.Add(btnDefPath);
            tl.Controls.Add(btnRow, 0, 3);

            lblLogInfo = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3), Text = "Tab-separated text, readable in Excel." };
            tl.Controls.Add(lblLogInfo, 0, 4);

            t.Controls.Add(tl);
        }

        SmiResult Smi(string args)
        {
            return Util.Run(smi, args);
        }

        // ---------- load GPUs ----------

        void LoadGpus()
        {
            SetControlsEnabled(false);
            SmiResult r = Smi("--query-gpu=index,pci.bus_id,name,power.min_limit,power.max_limit,power.default_limit,power.limit,clocks.max.sm --format=csv,noheader,nounits");
            Util.Log("GPU query code " + r.Code + ": " + r.Output.Replace("\r", "").Replace("\n", " | "));
            if (r.Code != 0)
            {
                lblStatus.Text = "nvidia-smi error - see \"?\"";
                return;
            }

            foreach (string line in r.Output.Split(NewLinesArr(), StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = line.Split(Util.Comma);
                if (f.Length < 8) continue;
                GpuInfo g = new GpuInfo();
                g.Index = f[0].Trim();
                g.BusId = f[1].Trim();
                g.Name = f[2].Trim();
                g.Min = Util.ParseD(f[3]);
                g.Max = Util.ParseD(f[4]);
                g.Default = Util.ParseD(f[5]);
                g.Limit = Util.ParseD(f[6]);
                g.MaxClock = Util.ParseD(f[7]);
                gpus.Add(g);
            }

            int pick = 0;
            double best = -1;
            for (int i = 0; i < gpus.Count; i++)
            {
                cboGpu.Items.Add(gpus[i]);
                string n = gpus[i].Name.ToUpperInvariant();
                double score = double.IsNaN(gpus[i].Max) ? 0 : gpus[i].Max;
                if (n.Contains("V100") || n.Contains("PG500")) score += 100000;
                if (score > best) { best = score; pick = i; }
            }
            // last selected GPU wins, if it is still there
            string savedBus = Settings.Get(settings, "gpu", "");
            for (int i = 0; i < gpus.Count; i++) if (gpus[i].BusId == savedBus) pick = i;
            if (cboGpu.Items.Count > 0) cboGpu.SelectedIndex = pick;
            else lblStatus.Text = "No GPU found.";
        }

        static char[] NewLinesArr()
        {
            return Util.NewLines;
        }

        GpuInfo Current
        {
            get { return cboGpu.SelectedItem as GpuInfo; }
        }

        void OnGpuChanged()
        {
            GpuInfo g = Current;
            if (g == null) return;
            graph.Gpu = g;
            graph.ResetScale();
            if (compact) UpdateTitle();
            UpdateLiveLabels(g);

            bool ok = !double.IsNaN(g.Min) && !double.IsNaN(g.Max) && g.Max > g.Min;
            SetControlsEnabled(ok);
            if (!ok)
            {
                lblStatus.Text = "Power limit not adjustable on this card.";
                return;
            }

            syncing = true;
            int min = (int)Math.Ceiling(g.Min);
            int max = (int)Math.Floor(g.Max);
            num.Minimum = min;
            num.Maximum = max;
            trk.Minimum = min;
            trk.Maximum = max;
            int cur = (int)Math.Round(double.IsNaN(g.Limit) ? g.Max : g.Limit);
            cur = Math.Max(min, Math.Min(max, cur));
            num.Value = cur;
            trk.Value = cur;
            syncing = false;

            lblStatus.Text = admin
                ? "Range " + min + "–" + max + " W, default " + Util.Fmt(g.Default, "W")
                : "Monitoring only - run as admin to change settings.";
        }

        void SetControlsEnabled(bool on)
        {
            num.Enabled = on && admin;
            trk.Enabled = on && admin;
            btnApply.Enabled = on && admin;
            btnDefault.Enabled = on && admin;
            btnTask.Enabled = on && admin;
            btnTaskDel.Enabled = admin;
        }

        void SyncFromNum()
        {
            if (syncing) return;
            syncing = true;
            trk.Value = (int)num.Value;
            syncing = false;
        }

        void SyncFromTrack()
        {
            if (syncing) return;
            syncing = true;
            num.Value = trk.Value;
            syncing = false;
        }

        // ---------- live values (all GPUs, one call) ----------

        void Poll()
        {
            if (gpus.Count == 0 || busy || !IsHandleCreated) return;
            busy = true;
            Task.Run(() => Smi("--query-gpu=pci.bus_id,power.draw,temperature.gpu,clocks.sm,utilization.gpu,power.limit,memory.used,memory.total --format=csv,noheader,nounits"))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) Util.Log("Poll task faulted: " + t.Exception);
                    if (IsDisposed || !IsHandleCreated) { busy = false; return; }
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            busy = false;
                            try { OnPollResult(t.Result); }
                            catch (Exception ex) { Util.Log("Poll result error: " + ex); }
                        });
                    }
                    catch (Exception ex)
                    {
                        busy = false;
                        Util.Log("Poll error: " + ex.Message);
                    }
                });
        }

        void OnPollResult(SmiResult r)
        {
            pollCount++;
            if (pollCount == 1 || pollCount % 600 == 0)
                Util.Log("Heartbeat: poll #" + pollCount + ", code " + r.Code + ", " +
                         r.Output.Replace("\r", "").Replace("\n", " | "));
            if (r.Code != 0)
            {
                lblDraw.Text = "Power: error";
                return;
            }

            DateTime now = DateTime.Now;
            List<GpuInfo> updated = new List<GpuInfo>();
            foreach (string line in r.Output.Split(Util.NewLines, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = line.Split(Util.Comma);
                if (f.Length < 8) continue;
                string bus = f[0].Trim();
                GpuInfo g = null;
                foreach (GpuInfo x in gpus) if (x.BusId == bus) { g = x; break; }
                if (g == null) continue;

                Sample s = new Sample
                {
                    T = now,
                    Power = Util.ParseD(f[1]),
                    Temp = Util.ParseD(f[2]),
                    Clock = Util.ParseD(f[3]),
                    Util = Util.ParseD(f[4]),
                    Limit = Util.ParseD(f[5]),
                    MemUsed = Util.ParseD(f[6]),
                    MemTotal = Util.ParseD(f[7])
                };
                g.History.Add(s);
                if (!double.IsNaN(s.Limit)) g.Limit = s.Limit;
                while (g.History.Count > 0 && (now - g.History[0].T).TotalSeconds > HistorySeconds)
                    g.History.RemoveAt(0);
                updated.Add(g);
            }

            GpuInfo cur = Current;
            if (cur != null) UpdateLiveLabels(cur);
            if (tabs.SelectedIndex == 1) graph.Invalidate();

            WriteLog(now, updated);
        }

        void UpdateLiveLabels(GpuInfo g)
        {
            if (g.History.Count == 0)
            {
                lblDraw.Text = "Power: -";
                lblLimit.Text = "Limit: " + Util.Fmt(g.Limit, "W");
                lblTemp.Text = "Temperature: -";
                lblUtil.Text = "Load: -";
                lblClock.Text = "Clock: -";
                lblMem.Text = "VRAM: -";
                return;
            }
            Sample s = g.History[g.History.Count - 1];
            lblDraw.Text = "Power: " + Util.Fmt(s.Power, "W");
            lblLimit.Text = "Limit: " + Util.Fmt(s.Limit, "W");
            lblTemp.Text = "Temperature: " + Util.Fmt(s.Temp, "°C");
            lblUtil.Text = "Load: " + Util.Fmt(s.Util, "%");
            lblClock.Text = "Clock: " + Util.Fmt(s.Clock, "MHz");
            lblMem.Text = "VRAM: " + Util.Mem(s.MemUsed, s.MemTotal);
        }

        // ---------- data log ----------

        void OnLogToggle()
        {
            if (togglingLog) return;
            if (chkLog.Checked)
            {
                if (!StartLog())
                {
                    togglingLog = true;
                    chkLog.Checked = false;
                    togglingLog = false;
                }
            }
            else StopLog();
            txtPath.Enabled = logWriter == null;
            btnBrowse.Enabled = logWriter == null;
        }

        bool StartLog()
        {
            string p = txtPath.Text.Trim();
            if (p.Length == 0) p = Util.DefaultDataLogPath;
            try
            {
                p = Path.GetFullPath(p);
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                bool isNew = !File.Exists(p) || new FileInfo(p).Length == 0;
                FileStream fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                logWriter = new StreamWriter(fs, new UTF8Encoding(false));
                logWriter.AutoFlush = true;
                if (isNew) logWriter.WriteLine("Time\tBusId\tName\tPower_W\tLimit_W\tTemp_C\tClock_MHz\tLoad_pct\tVRAM_used_MiB\tVRAM_total_MiB");
                logWriter.WriteLine("# logging started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) +
                                    ", interval " + numInterval.Value + " s");
                txtPath.Text = p;
                lastLog = DateTime.MinValue;
                logLines = 0;
                lblLogInfo.Text = "Logging...";
                Util.Log("Data log started: " + p);
                SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                logWriter = null;
                Util.Log("Data log start failed: " + ex.Message);
                MessageBox.Show(this, "Could not open log file:\n\n" + p + "\n\n" + ex.Message, Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        void StopLog()
        {
            if (logWriter == null) return;
            try
            {
                logWriter.WriteLine("# logging stopped " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv));
                logWriter.Dispose();
            }
            catch { }
            logWriter = null;
            lblLogInfo.Text = "Stopped, " + logLines + " lines written.";
            Util.Log("Data log stopped.");
        }

        void WriteLog(DateTime now, List<GpuInfo> updated)
        {
            if (logWriter == null || updated.Count == 0) return;
            if ((now - lastLog).TotalSeconds < (double)numInterval.Value - 0.3) return;
            lastLog = now;
            try
            {
                string ts = now.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv);
                foreach (GpuInfo g in updated)
                {
                    Sample s = g.History[g.History.Count - 1];
                    logWriter.WriteLine(ts + "\t" + g.BusId + "\t" + g.Name + "\t" + Util.Num(s.Power) + "\t" +
                                        Util.Num(s.Limit) + "\t" + Util.Num(s.Temp) + "\t" + Util.Num(s.Clock) + "\t" +
                                        Util.Num(s.Util) + "\t" + Util.Num(s.MemUsed) + "\t" + Util.Num(s.MemTotal));
                    logLines++;
                }
                lblLogInfo.Text = "Logging... " + logLines + " lines";
            }
            catch (Exception ex)
            {
                Util.Log("Data log write failed: " + ex.Message);
                StopLog();
                togglingLog = true;
                chkLog.Checked = false;
                togglingLog = false;
                txtPath.Enabled = true;
                btnBrowse.Enabled = true;
                lblLogInfo.Text = "Write error: " + ex.Message;
            }
        }

        void OnBrowse()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Data log file";
                d.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                d.OverwritePrompt = false;
                try
                {
                    string p = Path.GetFullPath(txtPath.Text.Trim().Length > 0 ? txtPath.Text.Trim() : Util.DefaultDataLogPath);
                    d.InitialDirectory = Path.GetDirectoryName(p);
                    d.FileName = Path.GetFileName(p);
                }
                catch { }
                if (d.ShowDialog(this) == DialogResult.OK) txtPath.Text = d.FileName;
            }
        }

        void OnOpenFolder()
        {
            try
            {
                string p = Path.GetFullPath(txtPath.Text.Trim().Length > 0 ? txtPath.Text.Trim() : Util.DefaultDataLogPath);
                if (File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(p) + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open folder:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void SaveSettings()
        {
            settings["logPath"] = txtPath.Text.Trim();
            settings["logInterval"] = ((int)numInterval.Value).ToString(Util.Inv);
            settings["logEnabled"] = chkLog.Checked ? "1" : "0";
            settings["graphAuto"] = chkAuto.Checked ? "1" : "0";
            settings["topMost"] = miTop.Checked ? "1" : "0";
            settings["transp"] = chkTransp.Checked ? "1" : "0";
            settings["transpLevel"] = ((int)numTransp.Value).ToString(Util.Inv);
            settings["transpCompact"] = chkTranspCompact.Checked ? "1" : "0";
            foreach (SeriesCheckBox cb in new[] { chkPower, chkTemp, chkUtil, chkClock, chkMem })
                settings["show" + cb.Key] = cb.Checked ? "1" : "0";
            int rs = graph.RangeSeconds;
            settings["graphRange"] = rs.ToString(Util.Inv);
            if (normalMin.IsEmpty == false && WindowState != FormWindowState.Minimized) StoreWindow();
            Settings.Save(settings);
        }

        void OnClosing()
        {
            timer.Stop();
            StopLog();
            SaveSettings();
        }

        // ---------- actions ----------

        bool Confirm(string msg)
        {
            return MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                   MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        void OnInfo()
        {
            checks = Util.RunChecks(smi);
            UpdateInfoButton();
            CheckForm.ShowChecks(this, checks, false, true, txtPath.Text.Trim());
        }

        void UpdateInfoButton()
        {
            int warn = 0;
            if (checks != null)
                foreach (CheckItem c in checks) if (!c.Ok) warn++;
            btnInfo.Text = warn > 0 ? "!" : "?";
            btnInfo.ForeColor = warn > 0 ? Color.DarkOrange : SystemColors.ControlText;
            infoTip.SetToolTip(btnInfo, warn > 0
                ? "System check: " + warn + " warning(s) - click for details"
                : "System check and info");
        }

        void OnDefault()
        {
            GpuInfo g = Current;
            if (g == null || double.IsNaN(g.Default)) return;
            num.Value = Math.Max(num.Minimum, Math.Min(num.Maximum, (decimal)Math.Round(g.Default)));
        }

        void OnApply()
        {
            GpuInfo g = Current;
            if (g == null) return;
            int val = (int)num.Value;
            if (!Confirm("Set power limit?\n\n" + g.Name + " (" + g.BusId + ")\n" +
                         "from " + Util.Fmt(g.Limit, "W") + " to " + val + " W")) return;

            SmiResult r = Smi("-i " + g.BusId + " -pl " + val);
            Util.Log("Set -pl " + val + " on " + g.BusId + ", code " + r.Code + ": " + r.Output);
            if (r.Code == 0)
            {
                g.Limit = val;
                lblStatus.Text = "Limit set: " + val + " W (valid until reboot)";
                Poll();
            }
            else
            {
                MessageBox.Show(this, "Failed to set limit:\n\n" + r.Output, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnTaskCreate()
        {
            GpuInfo g = Current;
            if (g == null) return;
            int val = (int)num.Value;
            if (!Confirm("Create autostart task \"" + TaskName + "\"?\n\n" +
                         "On every Windows start the power limit of\n" + g.Name + " (" + g.BusId + ")\n" +
                         "will be set to " + val + " W (30 s after boot).\n\nAn existing task will be replaced.")) return;

            string tr = "\\\"" + smi + "\\\" -i " + g.BusId + " -pl " + val;
            string args = "/Create /F /TN \"" + TaskName + "\" /SC ONSTART /DELAY 0000:30 /RU SYSTEM /RL HIGHEST /TR \"" + tr + "\"";
            SmiResult r = Util.Run("schtasks.exe", args);
            Util.Log("schtasks create code " + r.Code + ": " + r.Output);
            if (r.Code == 0) lblStatus.Text = "Autostart set: " + val + " W";
            else MessageBox.Show(this, "Could not create task:\n\n" + r.Output, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void OnTaskDelete()
        {
            if (!Confirm("Remove autostart task \"" + TaskName + "\"?\n\nThe current limit stays active until reboot.")) return;
            SmiResult r = Util.Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"");
            Util.Log("schtasks delete code " + r.Code + ": " + r.Output);
            if (r.Code == 0) lblStatus.Text = "Autostart removed.";
            else MessageBox.Show(this, "Could not remove task (does it exist?):\n\n" + r.Output, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================= Entry point =================

    static class Program
    {
        static bool fatalShown;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static void ShowFatal(Exception ex)
        {
            string msg = ex == null ? "Unknown error" : ex.ToString();
            Util.Log("CRASH: " + msg);
            if (fatalShown) return;
            fatalShown = true;
            try
            {
                MessageBox.Show("Unexpected error:\n\n" + (ex == null ? "unknown" : ex.GetType().Name + ": " + ex.Message) +
                                "\n\nDetails in log:\n" + Util.LogPath,
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Util.Log("---- Start, args: [" + string.Join(" ", args) + "], admin: " + Util.IsAdmin() +
                     ", user: " + Environment.UserDomainName + "\\" + Environment.UserName +
                     ", exe: " + AppDomain.CurrentDomain.BaseDirectory +
                     ", OS: " + Environment.OSVersion + ", CLR: " + Environment.Version);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { ShowFatal(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { ShowFatal(e.ExceptionObject as Exception); };
            TaskScheduler.UnobservedTaskException += delegate(object s, UnobservedTaskExceptionEventArgs e) { Util.Log("Unobserved task exception: " + e.Exception); e.SetObserved(); };
            Application.ApplicationExit += delegate { Util.Log("ApplicationExit event."); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Util.Log("ProcessExit event, exit code " + Environment.ExitCode + "."); };

            try
            {
                try { SetProcessDPIAware(); } catch { }   // sharp text on scaled displays
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool alreadyTried = Array.IndexOf(args, "--elevated") >= 0;
                if (!Util.IsAdmin() && !alreadyTried)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--elevated");
                        psi.UseShellExecute = true;
                        psi.Verb = "runas";
                        Process.Start(psi);
                        Util.Log("Restart with admin rights requested.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        // UAC declined -> continue without admin rights (monitoring only)
                        Util.Log("Admin rights not granted: " + ex.Message);
                    }
                }

                string smi = Util.FindSmi();
                List<CheckItem> checks = Util.RunChecks(smi);

                bool fatal = false;
                foreach (CheckItem c in checks)
                {
                    if (!c.Ok && c.Fatal) fatal = true;
                }

                // the check window only pops up when something prevents the app from working;
                // warnings (e.g. no admin rights) are shown via the "!" button in the main window
                if (fatal)
                {
                    if (!CheckForm.ShowChecks(null, checks, true, false, null))
                    {
                        Util.Log("Exited after system check.");
                        return;
                    }
                }

                Application.Run(new MainForm(smi, checks));
                Util.Log("Exited normally.");
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
            }
        }
    }
}
