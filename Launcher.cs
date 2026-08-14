// DeepSeek Harness 一键启动器
// 功能:
//   - 自动检测 Node.js 与 @deepseek-ai/dsh 的位置 (配置 > 内置目录 > npm 全局 > npx 缓存 > exe 目录)
//   - 首次运行未安装 dsh 时, 一键通过 npm 安装到 %LOCALAPPDATA%\DeepSeekHarness (无需管理员权限)
//   - 隐藏启动 dsh web 服务 -> 自动打开浏览器 -> 托盘常驻 (可停止)
// 编译: csc /nologo /target:winexe /optimize+ /win32icon:launcher.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll Launcher.cs
// 注意: 使用 .NET Framework csc (C# 5), 不要使用字符串插值 $"" 等新语法。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    internal static class Program
    {
        // ---------------- 内建默认值 (可用 exe 旁的 launcher.config.txt 覆盖) ----------------
        internal static string _url = "http://127.0.0.1:3080";
        private static int _port = 3080;
        private static string _nodeExe = "";     // 空 -> 自动检测
        private static string _dshBin = "";      // 空 -> 自动检测
        private static string _dshHome = "";     // 空 -> %USERPROFILE%\.dsh
        private static string _workDir = "";     // 空 -> exe 所在目录
        private static string _installDir = "";  // 空 -> %LOCALAPPDATA%\DeepSeekHarness

        private const string PACKAGE_DIR = "DeepSeekHarness";
        internal const int START_TIMEOUT_SECONDS = 150;

        // ---------------- 运行期解析结果 ----------------
        internal static string ResolvedNodeExe;  // node.exe 完整路径 (null = 未找到)
        internal static string ResolvedDshBin;   // dsh bin.js 完整路径 (null = 未安装)
        internal static string NpmCli;           // npm-cli.js 路径 (可能为空)

        private static string _configPath;
        private static string _logPath;
        private static string _serverLogPath;
        private static readonly object LogLock = new object();

        private static Process _serverProc;
        private static Process _installProc;   // 正在运行的 npm 安装进程 (可被停止)
        private static bool _ownServer;
        private static bool _stopping;
        private static Mutex _mutex;

        [STAThread]
        private static int Main(string[] args)
        {
            bool selfTest = false;
            bool checkEnv = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-selftest", StringComparison.OrdinalIgnoreCase))
                    selfTest = true;
                else if (string.Equals(args[i], "-checkenv", StringComparison.OrdinalIgnoreCase))
                    checkEnv = true;
                else if (args[i].StartsWith("-config:", StringComparison.OrdinalIgnoreCase))
                    _configPath = args[i].Substring("-config:".Length);
            }

            _configPath = _configPath ?? Path.Combine(Application.StartupPath, "launcher.config.txt");
            _logPath = Path.Combine(Application.StartupPath, "launcher.log");
            _serverLogPath = Path.Combine(Application.StartupPath, "dsh-server.log");
            LoadConfig();

            Log("=== launcher start (pid=" + Process.GetCurrentProcess().Id + ") ===");
            Log("config=" + _configPath + " | url=" + _url);

            ResolveEnvironment();
            Log("node=" + (ResolvedNodeExe ?? "(not found)"));
            Log("npm-cli=" + (NpmCli.Length > 0 ? NpmCli : "(not found)"));
            Log("dsh=" + (ResolvedDshBin ?? "(not installed)"));
            Log("installDir=" + DefaultInstallDir());

            if (selfTest)
            {
                bool up = IsServerUp();
                Log("selftest: server " + (up ? "UP" : "DOWN"));
                return up ? 0 : 1;
            }
            if (checkEnv)
            {
                Log("checkenv: node=" + (ResolvedNodeExe != null) + " dsh=" + (ResolvedDshBin != null));
                if (ResolvedNodeExe == null) return 2;
                return ResolvedDshBin != null ? 0 : 1;
            }

            bool createdNew;
            _mutex = new Mutex(true, "DSH_Launcher_" + _port, out createdNew);
            if (!createdNew)
            {
                // 已有实例在运行: 若它已把服务拉起来就直接开浏览器; 否则尝试接管(处理被遗弃的互斥体)
                try
                {
                    if (!_mutex.WaitOne(0))
                    {
                        if (IsServerUp())
                        {
                            Log("another launcher instance is running; server up; opening browser.");
                            OpenBrowser();
                        }
                        else
                        {
                            Log("another launcher instance is running but server is down.");
                            MessageBox.Show("启动器已在后台运行, 但服务没有启动。\n\n请右键托盘图标选择「停止并退出」, 然后再双击启动器。",
                                "DeepSeek Harness 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return 0;
                    }
                }
                catch (AbandonedMutexException)
                {
                    Log("adopted abandoned mutex; continuing as owner.");
                }
            }

            if (IsServerUp())
            {
                Log("server already running; opening browser and exiting (not taking ownership).");
                ReleaseMutexIfOwned();
                OpenBrowser();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        // ---------------- 环境检测 ----------------

        private static void ResolveEnvironment()
        {
            ResolvedNodeExe = FindNode();
            NpmCli = "";
            if (ResolvedNodeExe != null)
            {
                string nodeDir = Path.GetDirectoryName(ResolvedNodeExe);
                if (nodeDir != null)
                {
                    string cand = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
                    if (File.Exists(cand)) NpmCli = cand;
                }
            }
            ResolvedDshBin = FindDsh();
        }

        private static string FindNode()
        {
            if (_nodeExe.Length > 0 && File.Exists(_nodeExe)) return _nodeExe;
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                string[] dirs = pathVar.Split(';');
                for (int i = 0; i < dirs.Length; i++)
                {
                    string dir = dirs[i].Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    try
                    {
                        string p = Path.Combine(dir, "node.exe");
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try { if (File.Exists(candidates[i])) return candidates[i]; } catch { }
            }
            return null;
        }

        // 内置安装的完整性检查: 中断的安装会留下残缺依赖树 (每次缺的包还不一样),
        // 只靠 bin.js 存在无法判断好坏, 必须抽查关键依赖 (直接 + 传递依赖里的高频缺口)。
        private static bool ManagedInstallVerified()
        {
            string nm = Path.Combine(DefaultInstallDir(), "node_modules");
            if (!File.Exists(Path.Combine(nm, "@deepseek-ai", "dsh", "lib", "bin.js"))) return false;
            string[] canaries = new string[]
            {
                "zod",
                "js-yaml",
                "commander",
                "chokidar",
                "@deepseek-ai/dsh-base",
                "@deepseek-ai/dsh-web-app",
                "@deepseek-ai/dsh-client-connection",
                "@deepseek-ai/dsh-host-apiproxy",
                "@deepseek-ai/dsh-client-web",
                "@deepseek-ai/dsh-typert-registry",
                "@deepseek-ai/dsh-session-title",
            };
            for (int i = 0; i < canaries.Length; i++)
            {
                if (!File.Exists(Path.Combine(nm, canaries[i].Replace("/", "\\"), "package.json")))
                {
                    Log("managed install incomplete (" + canaries[i] + " missing).");
                    return false;
                }
            }
            return true;
        }

        // 读取用户级 .npmrc 里的配置项 (如 cache=、prefix=), 兼容自定义盘符/路径的安装
        private static string ReadNpmrcValue(string key)
        {
            try
            {
                string npmrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
                if (!File.Exists(npmrc)) return null;
                string[] lines = File.ReadAllLines(npmrc);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(eq + 1).Trim();
                        if (v.Length > 0) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindDsh()
        {
            if (_dshBin.Length > 0 && File.Exists(_dshBin)) return _dshBin;

            // npx 缓存: 优先环境变量, 其次 .npmrc 的 cache= (用户可能自定义到其他盘), 最后默认位置
            string cache = Environment.GetEnvironmentVariable("npm_config_cache");
            if (string.IsNullOrEmpty(cache)) cache = ReadNpmrcValue("cache");
            if (string.IsNullOrEmpty(cache))
                cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache");
            try
            {
                string npxRoot = Path.Combine(cache, "_npx");
                if (Directory.Exists(npxRoot))
                {
                    string[] subs = Directory.GetDirectories(npxRoot);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string b = Path.Combine(subs[i], "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(b)) return b;
                    }
                }
            }
            catch { }

            // npm 全局安装: 优先 .npmrc 的 prefix= (可能自定义到其他盘), 其次默认位置
            string globalRoot = null;
            string prefix = ReadNpmrcValue("prefix");
            if (!string.IsNullOrEmpty(prefix)) globalRoot = Path.Combine(prefix, "node_modules");
            if (globalRoot == null)
                globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
            string g = Path.Combine(globalRoot, "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(g)) return g;

            // 内置安装目录 (必须通过完整性检查才使用, 否则视为未安装, 提示一键修复)
            if (ManagedInstallVerified()) return ManagedDshBin();

            // exe 目录便携安装
            string portable = Path.Combine(Application.StartupPath, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(portable)) return portable;

            return null;
        }

        internal static string DefaultInstallDir()
        {
            if (_installDir.Length > 0) return _installDir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PACKAGE_DIR);
        }

        internal static string ManagedDshBin()
        {
            return Path.Combine(DefaultInstallDir(), "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        }

        // ---------------- 安装 dsh (通过 npm) ----------------
        // progress 回调在后台线程触发, 不要在回调里阻塞 UI 线程 (应使用 BeginInvoke)。
        internal static bool InstallDsh(Action<string> progress, out string error)
        {
            error = null;
            if (ResolvedNodeExe == null)
            {
                error = "未检测到 Node.js, 无法安装。";
                return false;
            }
            string prefix = DefaultInstallDir();
            CleanInstallDir(prefix);
            try { Directory.CreateDirectory(prefix); }
            catch (Exception ex) { error = "无法创建安装目录: " + ex.Message; return false; }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);

            if (NpmCli.Length > 0)
            {
                psi.FileName = ResolvedNodeExe;
                psi.Arguments = "\"" + NpmCli + "\" install --prefix \"" + prefix + "\" @deepseek-ai/dsh@latest --no-audit --no-fund";
            }
            else
            {
                string comspec = Environment.GetEnvironmentVariable("ComSpec");
                psi.FileName = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec;
                psi.Arguments = "/c npm install --prefix \"" + prefix + "\" @deepseek-ai/dsh@latest --no-audit --no-fund";
            }

            Log("install cmd: " + psi.FileName + " " + psi.Arguments);
            Process proc = new Process();
            proc.StartInfo = psi;
            proc.EnableRaisingEvents = true;
            string lastLine = "";
            DataReceivedEventHandler onData = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    lastLine = e.Data;
                    AppendServerLog("[install] " + e.Data);
                    if (progress != null)
                    {
                        try { progress(e.Data); } catch { }
                    }
                }
            };
            proc.OutputDataReceived += onData;
            proc.ErrorDataReceived += onData;

            try
            {
                if (!proc.Start())
                {
                    error = "无法启动 npm 进程。";
                    return false;
                }
                _installProc = proc;   // 记住安装进程, 用户停止退出时一并杀掉, 避免留下残缺安装
            }
            catch (Exception ex)
            {
                error = "无法启动 npm 进程: " + ex.Message;
                return false;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            _installProc = null;

            if (proc.ExitCode != 0)
            {
                error = "npm install 失败 (exit " + proc.ExitCode + "): " + lastLine;
                Log("install failed: " + error);
                return false;
            }
            if (!ManagedInstallVerified())
            {
                error = "安装完成但依赖不完整, 请重试安装。";
                Log("install finished but managed install verification failed.");
                return false;
            }
            ResolvedDshBin = ManagedDshBin();
            Log("install OK, dsh bin=" + ResolvedDshBin);
            return true;
        }

        // 清空安装目录里可能残缺的旧安装, 保证全新安装
        private static void CleanInstallDir(string prefix)
        {
            try
            {
                string nm = Path.Combine(prefix, "node_modules");
                if (Directory.Exists(nm)) Directory.Delete(nm, true);
            }
            catch (Exception ex) { Log("clean node_modules error: " + ex.Message); }
            try { string pj = Path.Combine(prefix, "package.json"); if (File.Exists(pj)) File.Delete(pj); } catch { }
            try { string pl = Path.Combine(prefix, "package-lock.json"); if (File.Exists(pl)) File.Delete(pl); } catch { }
        }

        // ---------------- 配置 ----------------

        private static void LoadConfig()
        {
            if (!File.Exists(_configPath)) return;
            try
            {
                string[] lines = File.ReadAllLines(_configPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (val.Length == 0) continue;
                    switch (key.ToLowerInvariant())
                    {
                        case "nodeexe": _nodeExe = val; break;
                        case "dshbin": _dshBin = val; break;
                        case "url": _url = val; break;
                        case "port": { int p; if (int.TryParse(val, out p) && p > 0 && p < 65536) _port = p; } break;
                        case "dshhome": _dshHome = val; break;
                        case "workdir": _workDir = val; break;
                        case "installdir": _installDir = val; break;
                    }
                }
                Log("config loaded from " + _configPath);
            }
            catch (Exception ex)
            {
                Log("config load error: " + ex.Message);
            }
        }

        private static string ResolvedDshHome()
        {
            if (_dshHome.Length > 0) return _dshHome;
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrEmpty(home)) return home;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        private static string ResolvedWorkDir()
        {
            return _workDir.Length > 0 ? _workDir : Application.StartupPath;
        }

        // ---------------- 服务探测 / 浏览器 ----------------

        internal static bool IsServerUp()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_url);
                req.Method = "GET";
                req.Timeout = 2000;
                req.AllowAutoRedirect = true;
                req.UserAgent = "DSH-Launcher";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    resp.Close();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static void OpenBrowser()
        {
            try
            {
                Process.Start(_url);
            }
            catch (Exception ex)
            {
                Log("open browser error: " + ex.Message);
            }
        }

        // ---------------- 服务进程 ----------------

        internal static bool StartServer()
        {
            if (ResolvedDshBin == null)
            {
                Log("cannot start server: dsh not installed.");
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ResolvedNodeExe;
                // 显式传端口, 让配置里的 Port 与探测/打开地址保持一致
                psi.Arguments = "\"" + ResolvedDshBin + "\" web --port " + _port;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = ResolvedWorkDir();
                psi.EnvironmentVariables["DSH_HOME"] = ResolvedDshHome();
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);

                _serverProc = new Process();
                _serverProc.StartInfo = psi;
                _serverProc.EnableRaisingEvents = true;
                _serverProc.OutputDataReceived += OnServerOutput;
                _serverProc.ErrorDataReceived += OnServerOutput;
                _serverProc.Exited += OnServerExited;
                if (!_serverProc.Start())
                {
                    Log("failed to start node process (Start returned false).");
                    return false;
                }
                _ownServer = true;
                _serverProc.BeginOutputReadLine();
                _serverProc.BeginErrorReadLine();
                Log("server process started pid=" + _serverProc.Id);
                return true;
            }
            catch (Exception ex)
            {
                Log("start server error: " + ex.Message);
                return false;
            }
        }

        private static void OnServerOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null) AppendServerLog(e.Data);
        }

        private static void OnServerExited(object sender, EventArgs e)
        {
            Log("server process exited code=" + SafeExitCode());
            if (!_stopping && _ownServer)
            {
                MainForm form = MainForm.Instance;
                if (form != null && form.IsHandleCreated)
                {
                    try { form.BeginInvoke((Action)(() => form.OnServerExitedUnexpected())); }
                    catch { }
                }
            }
        }

        private static int SafeExitCode()
        {
            try { return _serverProc.ExitCode; }
            catch { return -1; }
        }

        internal static void StopServer()
        {
            _stopping = true;
            if (_serverProc != null && _ownServer)
            {
                try
                {
                    if (!_serverProc.HasExited) _serverProc.Kill();
                    if (!_serverProc.WaitForExit(5000))
                    {
                        Log("server did not exit within 5s after kill.");
                    }
                    else
                    {
                        Log("server stopped.");
                    }
                }
                catch (Exception ex)
                {
                    Log("stop server error: " + ex.Message);
                }
                finally
                {
                    _ownServer = false;
                }
            }
        }

        internal static bool OwnsServer { get { return _ownServer; } }

        internal static bool IsStopping { get { return _stopping; } }

        // 用户停止退出时, 杀掉仍在进行的 npm 安装进程, 避免留下残缺安装
        internal static void KillInstallProc()
        {
            try
            {
                if (_installProc != null)
                {
                    if (!_installProc.HasExited) _installProc.Kill();
                    _installProc = null;
                    Log("install process killed on exit.");
                }
            }
            catch { }
        }

        internal static void ReleaseMutexIfOwned()
        {
            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch { }
        }

        // ---------------- 日志 ----------------

        internal static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine;
                    File.AppendAllText(_logPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        internal static void AppendServerLog(string line)
        {
            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(_serverLogPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    internal class MainForm : Form
    {
        public static MainForm Instance;
        private Label _statusLabel;
        private LinkLabel _urlLink;
        private Button _btnOpen;
        private Button _btnInstall;
        private Button _btnStop;
        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private bool _serverUp;
        private bool _timeoutShown;
        private bool _balloonShown;
        private bool _starting;
        private int _restartCount;
        private bool _installing;
        private Thread _pollThread;

        public MainForm()
        {
            Instance = this;
            _serverUp = Program.IsServerUp();
            BuildUi();

            if (_serverUp)
            {
                _statusLabel.Text = "服务已在运行。";
                _urlLink.Text = Program._url;
                _btnOpen.Enabled = true;
            }
            else if (Program.ResolvedNodeExe == null)
            {
                ShowNoNodeState();
            }
            else if (Program.ResolvedDshBin == null)
            {
                ShowNeedInstallState();
            }
            else
            {
                StartServerAndPoll();
            }

            Shown += delegate
            {
                if (_serverUp)
                {
                    _tray.ShowBalloonTip(3000, "DeepSeek Harness", "服务已在运行。", ToolTipIcon.Info);
                }
            };
        }

        private void BuildUi()
        {
            Text = "DeepSeek Harness · 一键启动";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 160);
            Font = new Font("Microsoft YaHei UI", 9F);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(16, 14);
            _statusLabel.Size = new Size(368, 28);
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_statusLabel);

            Label hint = new Label();
            hint.Location = new Point(16, 46);
            hint.Size = new Size(120, 24);
            hint.Text = "页面地址:";
            hint.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(hint);

            _urlLink = new LinkLabel();
            _urlLink.Location = new Point(110, 46);
            _urlLink.Size = new Size(274, 24);
            _urlLink.Text = Program._url;
            _urlLink.TextAlign = ContentAlignment.MiddleLeft;
            _urlLink.LinkClicked += delegate { Program.OpenBrowser(); };
            Controls.Add(_urlLink);

            _btnOpen = new Button();
            _btnOpen.Text = "打开页面";
            _btnOpen.Location = new Point(16, 92);
            _btnOpen.Size = new Size(110, 36);
            _btnOpen.Click += delegate { Program.OpenBrowser(); };
            Controls.Add(_btnOpen);

            _btnInstall = new Button();
            _btnInstall.Text = "一键安装 dsh";
            _btnInstall.Location = new Point(136, 92);
            _btnInstall.Size = new Size(130, 36);
            _btnInstall.Visible = false;
            _btnInstall.Click += delegate { OnInstallButtonClick(); };
            Controls.Add(_btnInstall);

            _btnStop = new Button();
            _btnStop.Text = "停止并退出";
            _btnStop.Location = new Point(276, 92);
            _btnStop.Size = new Size(108, 36);
            _btnStop.Click += delegate
            {
                if (Program.OwnsServer)
                {
                    Program.StopServer();
                }
                Program.Log("user stopped & exited.");
                Shutdown();
            };
            Controls.Add(_btnStop);

            // 托盘
            _trayMenu = new ContextMenuStrip();
            ToolStripMenuItem openItem = new ToolStripMenuItem("打开页面");
            openItem.Click += delegate { Program.OpenBrowser(); };
            _trayMenu.Items.Add(openItem);
            ToolStripMenuItem installItem = new ToolStripMenuItem("安装或更新 dsh");
            installItem.Click += delegate { InstallAndStart(); };
            _trayMenu.Items.Add(installItem);
            ToolStripMenuItem stopItem = new ToolStripMenuItem("停止并退出");
            stopItem.Click += delegate { _btnStop.PerformClick(); };
            _trayMenu.Items.Add(stopItem);

            _tray = new NotifyIcon();
            Icon appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            _tray.Icon = appIcon ?? SystemIcons.Application;
            _tray.Text = "DeepSeek Harness";
            _tray.Visible = true;
            _tray.ContextMenuStrip = _trayMenu;
            _tray.DoubleClick += delegate { Program.OpenBrowser(); };

            FormClosing += OnFormClosing;
        }

        private void ShowNoNodeState()
        {
            _statusLabel.Text = "未检测到 Node.js, 请先安装。";
            _btnOpen.Enabled = false;
            _btnInstall.Text = "下载 Node.js";
            _btnInstall.Visible = true;
        }

        private void ShowNeedInstallState()
        {
            _statusLabel.Text = "未检测到可用的 DeepSeek Harness (或安装不完整), 点击右侧按钮一键安装/修复。";
            _btnOpen.Enabled = false;
            _btnInstall.Text = "一键安装 dsh";
            _btnInstall.Visible = true;
        }

        private void OnInstallButtonClick()
        {
            if (Program.ResolvedNodeExe == null)
            {
                try { Process.Start("https://nodejs.org/zh-cn/download"); }
                catch (Exception ex) { Program.Log("open nodejs site error: " + ex.Message); }
                return;
            }
            InstallAndStart();
        }

        private void StartServerAndPoll()
        {
            if (_starting) return;
            _starting = true;
            bool ok = Program.StartServer();
            if (!ok)
            {
                _statusLabel.Text = "启动失败, 请查看 dsh-server.log";
                _timeoutShown = true;
                return;
            }
            _statusLabel.Text = "正在启动服务, 请稍候…";
            _pollThread = new Thread(PollLoop);
            _pollThread.IsBackground = true;
            _pollThread.Start();
        }

        private void InstallAndStart()
        {
            if (_installing) return;
            _installing = true;
            _btnInstall.Enabled = false;
            _btnInstall.Text = "安装中…";
            _statusLabel.Text = "正在通过 npm 安装 @deepseek-ai/dsh, 可能需要几分钟…";
            _tray.ShowBalloonTip(2500, "DeepSeek Harness", "正在安装 dsh, 完成后会自动启动服务。", ToolTipIcon.Info);

            Thread worker = new Thread(delegate()
            {
                string error;
                bool ok = Program.InstallDsh(delegate(string line)
                {
                    if (!IsHandleCreated) return;
                    try
                    {
                        BeginInvoke((Action)(delegate()
                        {
                            string t = line == null ? "" : line.Trim();
                            if (t.Length > 48) t = t.Substring(0, 48) + "…";
                            _statusLabel.Text = "正在安装: " + t;
                        }));
                    }
                    catch { }
                }, out error);

                if (!IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(delegate()
                    {
                        _installing = false;
                        _btnInstall.Enabled = true;
                        if (ok)
                        {
                            _statusLabel.Text = "安装完成, 正在启动服务…";
                            _btnInstall.Visible = false;
                            _btnOpen.Enabled = true;
                            StartServerAndPoll();
                        }
                        else
                        {
                            _statusLabel.Text = "安装失败: " + (error ?? "未知错误");
                            _btnInstall.Text = "重试安装";
                            _tray.ShowBalloonTip(4000, "DeepSeek Harness", "安装失败, 请查看 dsh-server.log", ToolTipIcon.Error);
                        }
                    }));
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_balloonShown)
                {
                    _balloonShown = true;
                    _tray.ShowBalloonTip(2500, "DeepSeek Harness", "仍在后台运行, 双击托盘图标可打开页面。", ToolTipIcon.Info);
                }
            }
        }

        private void PollLoop()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Program.START_TIMEOUT_SECONDS);
            while (DateTime.UtcNow < deadline)
            {
                if (Program.IsServerUp())
                {
                    BeginInvoke((Action)OnServerReady);
                    return;
                }
                Thread.Sleep(500);
            }
            BeginInvoke((Action)OnServerTimeout);
        }

        private void OnServerReady()
        {
            if (_serverUp) return;
            _serverUp = true;
            _restartCount = 0;
            _statusLabel.Text = "服务已启动。";
            _btnOpen.Enabled = true;
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(3000, "DeepSeek Harness", "服务已启动, 正在为你打开页面…", ToolTipIcon.Info);
            }
            Program.OpenBrowser();
            Program.Log("server ready, browser opened.");
        }

        private void OnServerTimeout()
        {
            if (_timeoutShown) return;
            _timeoutShown = true;
            _statusLabel.Text = "启动超时, 请查看 dsh-server.log";
            _tray.ShowBalloonTip(4000, "DeepSeek Harness", "服务启动超时, 日志已写入 exe 同目录的 dsh-server.log。", ToolTipIcon.Error);
            Program.Log("start timeout reached.");
        }

        // 服务进程意外退出: 最多自动重启 3 次 (每次间隔 5 秒)
        public void OnServerExitedUnexpected()
        {
            if (Program.IsStopping) return;
            _serverUp = false;
            _starting = false;
            if (_restartCount >= 3)
            {
                _timeoutShown = true;
                _statusLabel.Text = "服务连续 3 次启动失败, 请查看 dsh-server.log";
                _btnOpen.Enabled = false;
                _tray.ShowBalloonTip(5000, "DeepSeek Harness", "服务连续 3 次启动失败, 已停止自动重试。", ToolTipIcon.Error);
                Program.Log("server restart limit reached.");
                return;
            }
            _restartCount++;
            _timeoutShown = false;
            _statusLabel.Text = "服务意外退出, 5 秒后自动重启 (第 " + _restartCount + "/3 次)…";
            Program.Log("server exited unexpectedly, auto-restart #" + _restartCount);
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(5000);
                if (!IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(delegate()
                    {
                        if (!Program.IsStopping) StartServerAndPoll();
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Shutdown()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
            Program.KillInstallProc();      // 若正在安装, 一并终止, 避免留下残缺安装
            Program.ReleaseMutexIfOwned();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
