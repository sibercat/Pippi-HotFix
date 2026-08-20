// Pippi Hot Fix installer.
//
// Finds the Steam Workshop copy of Pippi.pak, compares it against the latest
// GitHub release, and swaps it in. The release API is the single source of
// truth: a new hotfix needs a new release, not a new build of this program.
//
// Build with build.bat. It prefers the Roslyn compiler from any installed .NET
// SDK with -deterministic, so the build is reproducible and the published exe
// can be checked against its SHA-256. It falls back to the compiler that ships
// with Windows, which works but is not hash-reproducible.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Shown in the file's Properties -> Details tab. An unsigned executable with
// blank metadata is one of the things a careful user checks, so fill it in.
[assembly: AssemblyTitle("Pippi Hot Fix Installer")]
[assembly: AssemblyDescription("Installs the community hot fix for the Pippi mod for Conan Exiles")]
[assembly: AssemblyProduct("Pippi Hot Fix")]
[assembly: AssemblyCompany("sibercat")]
[assembly: AssemblyCopyright("Unofficial community stopgap. Pippi is by Joshtech.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PippiHotFix
{
    static class Cfg
    {
        public const string AppId = "440900";
        public const string WorkshopId = "3725018456";
        public const string PakName = "Pippi.pak";
        public const string ReleaseApi =
            "https://api.github.com/repos/sibercat/Pippi-HotFix/releases?per_page=100";
        public const string ReleasePage =
            "https://github.com/sibercat/Pippi-HotFix/releases";

        // The untouched Workshop file, for telling "not fixed yet" apart from
        // "fixed with an older hotfix".
        public const string StockSha =
            "1b96a4ca0b45a98e2b0f36b7c8918846d6fde139d7c221d42d779bb667d372aa";
        public const long StockSize = 45970706L;
    }

    enum State { NoFile, Stock, UpToDate, Outdated, Foreign, Retired, Offline }

    class Release
    {
        public string Tag = "";
        public string Name = "";
        public string Url = "";
        public long Size;
        public string Sha256 = "";
        public bool Ok { get { return Url.Length > 0; } }
    }

    // Every hotfix this project has ever published, not just the newest.
    //
    // Without the history we cannot tell an older hotfix apart from a file we
    // have never seen - and "never seen" is most likely an official Pippi
    // update. Overwriting that with this stopgap would be a silent downgrade,
    // so an unrecognised file is never replaced without the user agreeing to it.
    class Catalog
    {
        public Release Latest = new Release();
        public HashSet<string> Known =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Retired;
        public bool Ok { get { return Latest.Ok; } }
    }

    static class Steam
    {
        static string RegPath()
        {
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64,
                                         Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using (var b = Microsoft.Win32.RegistryKey.OpenBaseKey(
                               Microsoft.Win32.RegistryHive.CurrentUser, view))
                    using (var k = b.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        if (k == null) continue;
                        var v = k.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(v))
                            return v.Replace('/', '\\');
                    }
                }
                catch { }
            }
            return null;
        }

        // Every Steam library on the machine, not just the default one.
        public static List<string> Libraries()
        {
            var libs = new List<string>();
            var root = RegPath();
            if (string.IsNullOrEmpty(root)) return libs;
            libs.Add(root);

            foreach (var vdf in new[] { Path.Combine(root, @"steamapps\libraryfolders.vdf"),
                                        Path.Combine(root, @"config\libraryfolders.vdf") })
            {
                try
                {
                    if (!File.Exists(vdf)) continue;
                    var text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"",
                                                      RegexOptions.IgnoreCase))
                    {
                        var p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (!libs.Contains(p)) libs.Add(p);
                    }
                }
                catch { }
            }
            return libs;
        }

        public static string FindPak()
        {
            foreach (var lib in Libraries())
            {
                var p = Path.Combine(lib, @"steamapps\workshop\content\" + Cfg.AppId
                                          + @"\" + Cfg.WorkshopId + @"\" + Cfg.PakName);
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }
    }

    static class Util
    {
        public static string Sha256(string path, Action<int> progress)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite, 1 << 20))
            {
                var buf = new byte[1 << 20];
                long done = 0, total = Math.Max(fs.Length, 1);
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (progress != null) progress((int)(done * 100 / total));
                }
                sha.TransformFinalBlock(buf, 0, 0);
                var sb = new StringBuilder(64);
                foreach (var b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static bool GameRunning()
        {
            string[] names = { "ConanSandbox", "ConanSandbox-Win64-Shipping",
                               "ConanSandboxServer-Win64-Shipping" };
            foreach (var n in names)
            {
                try { if (Process.GetProcessesByName(n).Length > 0) return true; }
                catch { }
            }
            return false;
        }

        public static string Bytes(long n)
        {
            return n.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        }

        // A dedicated server caches unpacked copies that must be dropped so the
        // new pak is re-extracted. Harmless no-op for a normal game install.
        public static int ClearExtractedMods(string pakPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(pakPath);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var ex = Path.Combine(dir, @"ConanSandbox\Saved\ExtractedMods");
                    if (Directory.Exists(ex))
                    {
                        int k = 0;
                        foreach (var f in Directory.GetFiles(ex, "Pippi-*"))
                        {
                            try { File.Delete(f); k++; } catch { }
                        }
                        return k;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return 0;
        }
    }

    static class Github
    {
        public static Catalog Fetch(out string error)
        {
            error = null;
            var cat = new Catalog();
            try
            {
                try
                {
                    ServicePointManager.SecurityProtocol =
                        (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
                }
                catch
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
                    catch { }
                }

                var req = (HttpWebRequest)WebRequest.Create(Cfg.ReleaseApi);
                req.UserAgent = "PippiHotFix";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 20000;

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    json = sr.ReadToEnd();

                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                var releases = ser.DeserializeObject(json) as object[];
                if (releases == null) { error = "Unexpected response from GitHub."; return cat; }

                bool sawNewest = false;
                foreach (var relObj in releases)          // newest first
                {
                    var rel = relObj as Dictionary<string, object>;
                    if (rel == null) continue;
                    if (Str(rel, "draft").Equals("True", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pak = new Release
                    {
                        Tag = Str(rel, "tag_name"),
                        Name = Str(rel, "name")
                    };
                    var assets = rel.ContainsKey("assets") ? rel["assets"] as object[] : null;
                    if (assets != null)
                    {
                        foreach (var a in assets)
                        {
                            var d = a as Dictionary<string, object>;
                            if (d == null) continue;
                            if (!string.Equals(Str(d, "name"), Cfg.PakName,
                                               StringComparison.OrdinalIgnoreCase)) continue;
                            pak.Url = Str(d, "browser_download_url");
                            long sz;
                            if (long.TryParse(Str(d, "size"), out sz)) pak.Size = sz;
                            var dg = Str(d, "digest");
                            if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            {
                                pak.Sha256 = dg.Substring(7).ToLowerInvariant();
                                cat.Known.Add(pak.Sha256);
                            }
                            break;
                        }
                    }

                    bool prerelease = Str(rel, "prerelease")
                        .Equals("True", StringComparison.OrdinalIgnoreCase);
                    if (prerelease) continue;

                    // Read the retirement marker from the newest real release
                    // whether or not it carries a pak. Standing the project down
                    // by publishing a release with no download is the obvious
                    // thing to do, and it must not go unnoticed.
                    if (!sawNewest)
                    {
                        sawNewest = true;
                        var marker = (pak.Name + " " + Str(rel, "body")).ToUpperInvariant();
                        cat.Retired = marker.Contains("[RETIRED]");
                    }

                    if (!cat.Ok && pak.Ok) cat.Latest = pak;
                }
                if (!cat.Ok)
                    error = "No release has a " + Cfg.PakName + " attached.";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return cat;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return "";
        }
    }

    public class MainForm : Form
    {
        Label _headline, _detail, _pathLabel;
        Panel _statusPanel;
        Button _primary, _restore, _browse;
        ProgressBar _bar;
        Label _note;

        string _pak;
        readonly string _handedPak;
        string _installedSha = "";
        Catalog _cat = new Catalog();
        State _state = State.Offline;
        string _netError;

        static readonly Color Ink = Color.FromArgb(24, 28, 31);
        static readonly Color Muted = Color.FromArgb(104, 116, 120);
        static readonly Color Good = Color.FromArgb(28, 116, 94);
        static readonly Color Bad = Color.FromArgb(166, 48, 38);
        static readonly Color Warn = Color.FromArgb(158, 96, 30);
        static readonly Color Face = Color.FromArgb(246, 247, 246);

        public MainForm() : this(null) { }

        public MainForm(string handedPak)
        {
            _handedPak = handedPak;
            Text = "Pippi Hot Fix";
            ClientSize = new Size(600, 430);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Face;
            Font = new Font("Segoe UI", 9.75f);

            var title = new Label
            {
                Text = "Pippi Hot Fix",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(28, 22)
            };
            var sub = new Label
            {
                Text = "Repairs the Pippi crash on Conan Exiles Enhanced.",
                Font = new Font("Segoe UI", 10f),
                ForeColor = Muted,
                AutoSize = true,
                Location = new Point(30, 62)
            };

            _statusPanel = new Panel
            {
                Location = new Point(28, 98),
                Size = new Size(544, 132),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _headline = new Label
            {
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(516, 30),
                Location = new Point(14, 12),
                ForeColor = Ink
            };
            _detail = new Label
            {
                Font = new Font("Segoe UI", 9.75f),
                AutoSize = false,
                Size = new Size(516, 44),
                Location = new Point(14, 44),
                ForeColor = Ink
            };
            _pathLabel = new Label
            {
                Font = new Font("Consolas", 8.5f),
                AutoSize = false,
                Size = new Size(516, 32),
                Location = new Point(14, 92),
                ForeColor = Muted
            };
            _statusPanel.Controls.Add(_headline);
            _statusPanel.Controls.Add(_detail);
            _statusPanel.Controls.Add(_pathLabel);

            _bar = new ProgressBar
            {
                Location = new Point(28, 244),
                Size = new Size(544, 10),
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };

            _primary = new Button
            {
                Text = "Fix My Game",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Location = new Point(28, 268),
                Size = new Size(544, 52),
                BackColor = Color.FromArgb(166, 92, 37),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
            _primary.FlatAppearance.BorderSize = 0;
            _primary.Click += OnPrimary;

            _browse = new Button
            {
                Text = "Choose Pippi.pak myself...",
                Location = new Point(28, 332),
                Size = new Size(266, 34),
                FlatStyle = FlatStyle.System
            };
            _browse.Click += OnBrowse;

            _restore = new Button
            {
                Text = "Restore original file",
                Location = new Point(306, 332),
                Size = new Size(266, 34),
                FlatStyle = FlatStyle.System
            };
            _restore.Click += OnRestore;

            _note = new Label
            {
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Muted,
                AutoSize = false,
                Size = new Size(544, 34),
                Location = new Point(28, 376),
                Text = "Close Conan Exiles before using this. Steam restores the broken file "
                     + "whenever it updates Workshop items or verifies files \u2014 if crashes "
                     + "come back, run this again."
            };

            Controls.Add(title);
            Controls.Add(sub);
            Controls.Add(_statusPanel);
            Controls.Add(_bar);
            Controls.Add(_primary);
            Controls.Add(_browse);
            Controls.Add(_restore);
            Controls.Add(_note);

            Shown += (s, e) => Refresh_();
            FormClosing += (s, e) => { _closing = true; };
        }

        string BackupPath { get { return _pak == null ? null : _pak + ".original"; } }

        void SetStatus(Color c, string headline, string detail)
        {
            _headline.ForeColor = c;
            _headline.Text = headline;
            _detail.Text = detail;
            _pathLabel.Text = _pak ?? "";
        }

        bool _closing;

        // Hashing and downloading pump messages, so the window can be closed
        // mid-operation. Touching disposed controls after that throws from a
        // callback and takes the process down.
        void Progress(int pct)
        {
            if (_closing || IsDisposed || _bar == null || _bar.IsDisposed) return;
            _bar.Value = Math.Min(Math.Max(pct, 0), 100);
            Application.DoEvents();
        }

        bool Alive { get { return !_closing && !IsDisposed; } }

        void Busy(bool on, string label)
        {
            if (!Alive) return;
            _primary.Enabled = _browse.Enabled = _restore.Enabled = !on;
            _bar.Visible = on;
            if (on) { _headline.ForeColor = Ink; _headline.Text = label; }
            Application.DoEvents();
        }

        void Refresh_()
        {
            Busy(true, "Looking for Pippi...");
            _pak = (_handedPak != null && File.Exists(_handedPak)) ? _handedPak : Steam.FindPak();
            if (_pak == null)
            {
                var remembered = LoadRemembered();
                if (remembered != null && File.Exists(remembered)) _pak = remembered;
            }

            if (_pak == null)
            {
                _state = State.NoFile;
                Busy(false, null);
                SetStatus(Bad, "Pippi was not found",
                    "Subscribe to Pippi on the Steam Workshop and let it download, then run "
                  + "this again. If you keep Pippi somewhere unusual, use the button below to "
                  + "point at the file yourself.");
                _primary.Text = "Search again";
                _restore.Enabled = false;
                return;
            }

            _bar.Style = ProgressBarStyle.Continuous;
            try
            {
                _installedSha = Util.Sha256(_pak, Progress);
            }
            catch (Exception ex)
            {
                // Locked by Steam, denied by an ACL, on a disconnected drive.
                // Running from Shown, an escape here would kill the program
                // before the window ever appeared.
                _state = State.NoFile;
                Busy(false, null);
                SetStatus(Bad, "Could not read your Pippi file",
                    "Found it at the path below, but could not open it: " + ex.Message
                  + "  Close Steam and Conan Exiles and try again.");
                _primary.Text = "Try again";
                _restore.Enabled = false;
                return;
            }
            _cat = Github.Fetch(out _netError);
            Busy(false, null);
            Evaluate();
        }

        void Evaluate()
        {
            long size;
            try
            {
                size = new FileInfo(_pak).Length;
            }
            catch (Exception)
            {
                // Evaluate() also runs from the failure path of an install, so
                // it has to cope with the file having gone missing.
                _state = State.NoFile;
                SetStatus(Bad, "Pippi is no longer where it was",
                    "The file has moved or been removed since this window opened. "
                  + "Search again, or point at it yourself.");
                _primary.Text = "Search again";
                _restore.Enabled = _pak != null && File.Exists(BackupPath);
                return;
            }
            _restore.Enabled = File.Exists(BackupPath);

            if (!_cat.Ok)
            {
                _state = State.Offline;
                bool stock = _installedSha == Cfg.StockSha;
                SetStatus(stock ? Bad : Warn,
                    stock ? "Not fixed yet \u2014 and I cannot reach the download"
                          : "Cannot check for updates",
                    "Could not reach GitHub" + (_netError == null ? "" : " (" + _netError + ")")
                  + ". Check your internet connection, or download Pippi.pak manually from the "
                  + "releases page and use the button below.");
                _primary.Text = "Try again";
                return;
            }

            if (_cat.Retired)
            {
                // Pippi has been fixed properly; this project has stood down.
                _state = State.Retired;
                SetStatus(Good, "This hot fix is no longer needed",
                    "Pippi has been updated officially, so the community fix has been retired. "
                  + "Use Restore original file below, or turn Workshop auto-update back on, and "
                  + "let Steam install the official version.");
                _primary.Text = "Open the releases page";
                return;
            }

            if (_installedSha == _cat.Latest.Sha256)
            {
                _state = State.UpToDate;
                SetStatus(Good, "You are up to date",
                    "Hot fix " + _cat.Latest.Tag + " is installed. Nothing to do \u2014 you can close "
                  + "this and play. " + Util.Bytes(size) + ".");
                _primary.Text = "Re-apply anyway";
            }
            else if (_cat.Known.Contains(_installedSha))
            {
                // One of ours, just an older one - safe to move forward.
                _state = State.Outdated;
                SetStatus(Warn, "A newer hot fix is available",
                    "You have an earlier hot fix. Hot fix " + _cat.Latest.Tag + " is available ("
                  + Util.Bytes(_cat.Latest.Size) + ").");
                _primary.Text = "Update to hot fix " + _cat.Latest.Tag;
            }
            else if (_installedSha == Cfg.StockSha)
            {
                _state = State.Stock;
                SetStatus(Bad, "Your Pippi is broken and will crash",
                    "This is the unpatched Workshop file. Press the button below to replace it "
                  + "with hot fix " + _cat.Latest.Tag + ". Your original is kept so you can undo this.");
                _primary.Text = "Fix My Game";
            }
            else
            {
                // Not the stock file and not anything we published. Most likely
                // an official Pippi update, which must not be quietly replaced.
                _state = State.Foreign;
                SetStatus(Warn, "This looks like a different version of Pippi",
                    "Your Pippi is neither the broken file nor any hot fix from this project. "
                  + "It is probably a newer official release \u2014 if so, you do not need this and "
                  + "installing it would put you back on an older mod.");
                _primary.Text = "Install hot fix anyway";
            }
        }

        void OnPrimary(object sender, EventArgs e)
        {
            if (_state == State.NoFile || _state == State.Offline) { Refresh_(); return; }

            if (_state == State.Retired)
            {
                try { Process.Start(Cfg.ReleasePage); } catch { }
                return;
            }

            if (_state == State.Foreign)
            {
                var warn = MessageBox.Show(this,
                    "The Pippi you have is not the broken file, and it is not any hot fix from "
                  + "this project.\n\nIf Pippi has been updated officially, installing this would "
                  + "replace a newer mod with an older one, and you would be the only person on "
                  + "your server running it.\n\nOnly continue if you are sure.\n\nInstall hot fix "
                  + _cat.Latest.Tag + " over it?",
                    "This may not be what you want",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (warn != DialogResult.Yes) return;
            }

            if (Util.GameRunning())
            {
                MessageBox.Show(this,
                    "Conan Exiles is still running. Close the game completely, then try again.",
                    "Close the game first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string tmp = Path.Combine(Path.GetTempPath(), "PippiHotFix_" + Guid.NewGuid().ToString("N") + ".pak");
            try
            {
                Busy(true, "Downloading hot fix " + _cat.Latest.Tag + "...");
                _bar.Value = 0;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "PippiHotFix");
                    bool done = false;
                    Exception err = null;
                    wc.DownloadProgressChanged += (s2, e2) => Progress(e2.ProgressPercentage);
                    wc.DownloadFileCompleted += (s2, e2) => { err = e2.Error; done = true; };
                    wc.DownloadFileAsync(new Uri(_cat.Latest.Url), tmp);
                    while (!done && Alive) { Application.DoEvents(); Thread.Sleep(30); }
                    if (!Alive) { wc.CancelAsync(); return; }
                    if (err != null) throw err;
                }

                Busy(true, "Checking the download...");
                _bar.Value = 0;
                var got = Util.Sha256(tmp, Progress);
                if (_cat.Latest.Sha256.Length == 64)
                {
                    if (got != _cat.Latest.Sha256)
                        throw new Exception("The downloaded file did not match its published "
                                          + "checksum, so it was not installed. Try again.");
                }
                else
                {
                    // No digest published: fall back to the declared size so a
                    // truncated download still cannot be installed silently.
                    var downloaded = new FileInfo(tmp).Length;
                    if (_cat.Latest.Size > 0 && downloaded != _cat.Latest.Size)
                        throw new Exception("The download was incomplete, so it was not "
                                          + "installed. Try again.");
                    // Adopt it as the expected hash, or the file we just wrote
                    // would read back as an unrecognised version of Pippi.
                    _cat.Latest.Sha256 = got;
                    _cat.Known.Add(got);
                }

                Busy(true, "Installing...");
                // Keep the most recent file that is not one of ours - that is the
                // real original, whether it is the broken Workshop file or an
                // official update. Refreshing it matters: if someone installs over
                // an official Pippi, a stale backup would "restore" them to the
                // broken file instead.
                if (!_cat.Known.Contains(_installedSha))
                    File.Copy(_pak, BackupPath, true);

                Replace(tmp, _pak);
                int cleared = Util.ClearExtractedMods(_pak);

                Busy(false, null);
                _installedSha = got;
                Evaluate();
                MessageBox.Show(this,
                    "Pippi is fixed. You can start Conan Exiles and connect as normal."
                  + (cleared > 0 ? "\n\nAlso cleared " + cleared + " cached server file(s)." : ""),
                    "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                Busy(false, null);
                OfferElevation();
            }
            catch (Exception ex)
            {
                Busy(false, null);
                Evaluate();
                MessageBox.Show(this, ex.Message + "\n\nYou can also download Pippi.pak by hand "
                              + "from:\n" + Cfg.ReleasePage,
                    "That did not work", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // Never leave the Workshop folder without a Pippi.pak.
        //
        // Deleting the destination and then moving is the obvious way to do
        // this and the wrong one: if the move fails - antivirus, a lock, a full
        // disk - the mod is simply gone and the player has no idea why.
        // File.Replace swaps in one step and leaves the destination untouched
        // if it fails. The fallback for filesystems without it keeps a rescue
        // copy so a failed move can still be undone.
        static void Replace(string src, string dst)
        {
            var staged = dst + ".new";
            var rescue = dst + ".rescue";
            try
            {
                File.Copy(src, staged, true);

                if (!File.Exists(dst))
                {
                    File.Move(staged, dst);
                    return;
                }

                try
                {
                    File.Replace(staged, dst, null);
                }
                catch (Exception)
                {
                    File.Copy(dst, rescue, true);
                    try
                    {
                        File.Delete(dst);
                        File.Move(staged, dst);
                    }
                    catch
                    {
                        if (!File.Exists(dst)) File.Copy(rescue, dst, false);
                        throw;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                try { if (File.Exists(rescue)) File.Delete(rescue); } catch { }
            }
        }

        void OfferElevation()
        {
            var r = MessageBox.Show(this,
                "Windows would not let this program change the file. Run it again as "
              + "administrator?", "Permission needed",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            try
            {
                // Hand over the path we already resolved. An elevated process
                // may run as a different account, where neither the Steam
                // registry key nor our remembered path exists - it would come
                // up saying Pippi was not found.
                var psi = new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = _pak == null ? "" : "--pak \"" + _pak + "\""
                };
                Process.Start(psi);
                Close();
            }
            catch { }
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Find Pippi.pak";
                d.Filter = "Pippi.pak|Pippi.pak|Mod files (*.pak)|*.pak";
                if (_pak != null)
                {
                    try { d.InitialDirectory = Path.GetDirectoryName(_pak); } catch { }
                }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _pak = d.FileName;
                SaveRemembered(_pak);
                Busy(true, "Checking that file...");
                _bar.Value = 0;
                _installedSha = Util.Sha256(_pak, Progress);
                if (!_cat.Ok) _cat = Github.Fetch(out _netError);
                Busy(false, null);
                Evaluate();
            }
        }

        void OnRestore(object sender, EventArgs e)
        {
            if (BackupPath == null || !File.Exists(BackupPath)) return;
            if (Util.GameRunning())
            {
                MessageBox.Show(this, "Close Conan Exiles first.", "Close the game first",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var r = MessageBox.Show(this,
                "Put the original Workshop file back?\n\nDo this once Pippi has been updated "
              + "officially. Until then, the original file will crash.",
                "Restore original", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            try
            {
                Replace(BackupPath, _pak);
                Util.ClearExtractedMods(_pak);
                Busy(true, "Checking...");
                _bar.Value = 0;
                _installedSha = Util.Sha256(_pak, Progress);
                Busy(false, null);
                Evaluate();
            }
            catch (UnauthorizedAccessException) { OfferElevation(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "That did not work",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static string RememberFile
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PippiHotFix");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "path.txt");
            }
        }
        static void SaveRemembered(string p)
        {
            try { File.WriteAllText(RememberFile, p); } catch { }
        }
        static string LoadRemembered()
        {
            try { if (File.Exists(RememberFile)) return File.ReadAllText(RememberFile).Trim(); }
            catch { }
            return null;
        }

        // Headless check, for testing and for server owners: PippiHotFix.exe /check
        public static int SelfTest()
        {
            Console.WriteLine("Steam libraries:");
            foreach (var l in Steam.Libraries()) Console.WriteLine("   " + l);

            var pak = Steam.FindPak() ?? LoadRemembered();
            Console.WriteLine("Pippi.pak: " + (pak ?? "<not found>"));
            string sha = null;
            if (pak != null && File.Exists(pak))
            {
                var fi = new FileInfo(pak);
                sha = Util.Sha256(pak, null);
                Console.WriteLine("   size   " + Util.Bytes(fi.Length));
                Console.WriteLine("   sha256 " + sha);
                Console.WriteLine("   stock? " + (sha == Cfg.StockSha));
            }

            string err;
            var cat = Github.Fetch(out err);
            var rel = cat.Latest;
            Console.WriteLine("Latest release: " + (cat.Ok ? rel.Tag + " (" + rel.Name + ")"
                                                           : "UNAVAILABLE " + err));
            if (cat.Ok)
            {
                Console.WriteLine("   url    " + rel.Url);
                Console.WriteLine("   size   " + Util.Bytes(rel.Size));
                Console.WriteLine("   sha256 " + rel.Sha256);
                Console.WriteLine("   known hotfix builds: " + cat.Known.Count);
                Console.WriteLine("   retired: " + cat.Retired);
                if (sha != null)
                    Console.WriteLine("   verdict " + (cat.Retired ? "RETIRED - do not install"
                                                     : sha == rel.Sha256 ? "UP TO DATE"
                                                     : sha == Cfg.StockSha ? "NEEDS FIX"
                                                     : cat.Known.Contains(sha) ? "OLDER HOTFIX"
                                                     : "FOREIGN - will ask before replacing"));
            }
            Console.WriteLine("Conan running: " + Util.GameRunning());
            // 0 nothing to do, 1 needs fixing, 2 cannot tell, 3 no Pippi found
            if (pak == null || sha == null) return 3;
            if (!cat.Ok) return 2;
            if (cat.Retired || sha == rel.Sha256) return 0;
            return 1;
        }

        [STAThread]
        static int Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a.Equals("/check", StringComparison.OrdinalIgnoreCase)
                 || a.Equals("--check", StringComparison.OrdinalIgnoreCase))
                {
                    // A winexe has no stdout until a console is attached and
                    // Console.Out is repointed at it.
                    if (!AttachConsole(-1)) AllocConsole();
                    try
                    {
                        var w = new StreamWriter(Console.OpenStandardOutput());
                        w.AutoFlush = true;
                        Console.SetOut(w);
                    }
                    catch { }
                    return SelfTest();
                }
            }

            string handedPak = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--pak", StringComparison.OrdinalIgnoreCase)
                 || args[i].Equals("/pak", StringComparison.OrdinalIgnoreCase))
                {
                    handedPak = args[i + 1];
                    break;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(handedPak));
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AllocConsole();
    }
}
