using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace TranscodeServer
{
    // ---------------------------------------------------------------- load

    public static class Load
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        public static double Cpu = -1;
        public static double Gpu = -1;
        public static string GpuEngine = "";
        public static bool GpuAvailable = true;

        static long prevIdle, prevTotal;
        static DateTime lastGpu = DateTime.MinValue;

        public static void Start()
        {
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "load";
            t.Start();
        }

        static void Loop()
        {
            Thread.Sleep(500);
            while (true)
            {
                SampleCpu();
                if ((DateTime.Now - lastGpu).TotalMilliseconds >= 1200) SampleGpu();
                Thread.Sleep(800);
            }
        }

        static void SampleCpu()
        {
            try
            {
                long idle, kernel, user;
                if (!GetSystemTimes(out idle, out kernel, out user)) return;
                long total = kernel + user;
                if (prevTotal > 0 && total > prevTotal)
                {
                    long dIdle = idle - prevIdle;
                    long dTotal = total - prevTotal;
                    if (dTotal > 0)
                    {
                        double v = (dTotal - dIdle) * 100.0 / dTotal;
                        Cpu = Math.Max(0, Math.Min(100, v));
                    }
                }
                prevIdle = idle;
                prevTotal = total;
            }
            catch { }
        }

        static bool IsVideoEngine(string eng)
        {
            if (string.IsNullOrEmpty(eng)) return false;
            return eng.StartsWith("Video Codec", StringComparison.OrdinalIgnoreCase)
                || eng.StartsWith("VideoEncode", StringComparison.OrdinalIgnoreCase)
                || eng.StartsWith("VideoDecode", StringComparison.OrdinalIgnoreCase)
                || eng.StartsWith("Video Processing", StringComparison.OrdinalIgnoreCase);
        }

        static void SampleGpu()
        {
            lastGpu = DateTime.Now;
            try
            {
                Dictionary<string, double> per = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
                {
                    foreach (ManagementBaseObject mo in searcher.Get())
                    {
                        string name = mo["Name"] as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        int idx = name.IndexOf("engtype_", StringComparison.Ordinal);
                        if (idx < 0) continue;
                        string eng = name.Substring(idx + 8).Trim();
                        if (!IsVideoEngine(eng)) continue;
                        double v = 0;
                        try { v = Convert.ToDouble(mo["UtilizationPercentage"], CultureInfo.InvariantCulture); } catch { }
                        double cur;
                        per.TryGetValue(eng, out cur);
                        per[eng] = cur + v;
                    }
                }

                if (per.Count == 0)
                {
                    GpuAvailable = false;
                    Gpu = -1;
                    return;
                }

                double best = 0;
                string bestName = "";
                foreach (KeyValuePair<string, double> kv in per)
                {
                    if (bestName.Length == 0) bestName = kv.Key;
                    if (kv.Value > best) { best = kv.Value; bestName = kv.Key; }
                }
                GpuAvailable = true;
                Gpu = Math.Min(100, Math.Round(best, 1));
                GpuEngine = bestName;
            }
            catch
            {
                GpuAvailable = false;
                Gpu = -1;
            }
        }
    }

    // ---------------------------------------------------------------- config

    public class Config
    {
        public int port = 8099;
        public string handbrake = "bin\\HandBrakeCLI.exe";
        public int maxParallel = 1;
        public bool autoQueue = false;
        public string autoProfile = "h264-vce";
        public string autoSubtitle = "none";
        public string ffmpeg = "bin\\ffmpeg.exe";
    }

    public class Profile
    {
        public string id;
        public string label;
        public string desc;
        public string encoder;
        public string preset;
        public double quality;
        public bool copyVideo;
        public string family;
    }

    public class Job
    {
        public string id;
        public string name;
        public string source;
        public string output;
        public string profile;
        public string status = "queued";
        public string stage = "";
        public double progress;
        public double fps;
        public double speed;
        public double durationSec;
        public string eta = "";
        [ScriptIgnore] public DateTime startTime;
        public long sizeIn;
        public long sizeOut;
        public string error = "";
        public string createdAt = "";
        public string startedAt = "";
        public string finishedAt = "";
        public double elapsed;
        public string args = "";
        public double quality;
        public string resolution = "source";
        public string fpsMode = "source";
        public string audio = "copy:aac";
        public int audioBitrate = 128;
        public string custom = "";
        public string subtitleMode = "none";
        public string subtitleTrack = "auto";
        public string subtitleFile = "";
        public string subtitleLang = "chi";
        public string subTrack = "";
        public string subBurn = "";
        public string subSrtPath = "";
        public string subOriginal = "";
        public bool subNeedsMux;
        public int srtOffsetMs;
        public string subUsed = "";
        public string subNote = "";
        public bool subBurned;
        [ScriptIgnore] public Process proc;
    }

    public class SubInfo
    {
        public int track;
        public string langCode = "";
        public string name = "";
        public string format = "";
        public string sourceName = "";
        public bool forced;
        public bool isDefault;
    }

    public class MediaInfo
    {
        public double durationSec;
        public int width;
        public int height;
        public double fps;
        public List<SubInfo> subs = new List<SubInfo>();
    }

    // ---------------------------------------------------------------- program

    static class Program
    {
        public static string Root;
        public static int Port = 8099;
        public static string HandBrake = "";
        public static string HandBrakeVersion = "unknown";
        public static string Ffmpeg = "";
        public static bool HwEncode = false;
        public static string WorkDir, OutDir, DataDir, WebDir, InboxDir, LogDir;
        public static JobManager Jobs;
        public static Config Cfg = new Config();
        public static JavaScriptSerializer Json = new JavaScriptSerializer();
        public static DateTime Started = DateTime.Now;

        static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            WorkDir = Path.Combine(Root, "work");
            OutDir = Path.Combine(Root, "output");
            DataDir = Path.Combine(Root, "data");
            WebDir = Path.Combine(Root, "web");
            InboxDir = Path.Combine(Root, "inbox");
            LogDir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(WorkDir);
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(InboxDir);
            Directory.CreateDirectory(LogDir);

            bool portFromArgs = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[i + 1], out p) && p > 0 && p < 65536) { Port = p; portFromArgs = true; }
                    i++;
                }
            }

            LoadConfig();
            if (!portFromArgs && Cfg.port > 0) Port = Cfg.port;

            string hb = Cfg.handbrake;
            if (!Path.IsPathRooted(hb)) hb = Path.Combine(Root, hb);
            if (!File.Exists(hb))
            {
                string alt = Path.Combine(Root, "HandBrakeCLI.exe");
                if (File.Exists(alt)) hb = alt;
            }
            HandBrake = hb;
            ProbeHandBrake();
            DetectFfmpeg();
            Load.Start();

            Jobs = new JobManager();
            Jobs.Load();
            Jobs.Start();

            Console.Title = "Transcode LAN - port " + Port;
            Console.WriteLine("==========================================================");
            Console.WriteLine("  Transcode LAN  -  local video transcode service");
            Console.WriteLine("  HandBrake : " + HandBrakeVersion + (HwEncode ? "   [AMD VCE hardware encode: YES]" : "   [hardware encode: no]"));
            Console.WriteLine("  HandBrake : " + HandBrake);
            Console.WriteLine("  Output    : " + OutDir);
            foreach (string a in Util.LocalIPv4()) Console.WriteLine("  URL       : http://" + a + ":" + Port + "/");
            Console.WriteLine("  URL       : http://127.0.0.1:" + Port + "/");
            Console.WriteLine("==========================================================");
            Console.WriteLine("  Press Ctrl+C to stop.");
            Console.WriteLine();

            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { Jobs.Save(); };
            AppDomain.CurrentDomain.ProcessExit += delegate(object s, EventArgs e) { try { Jobs.Save(); } catch { } };

            HttpServer server = new HttpServer(Port);
            server.Run();
        }

        static void LoadConfig()
        {
            try
            {
                string f = Path.Combine(DataDir, "config.json");
                if (!File.Exists(f)) return;
                string json = File.ReadAllText(f, Encoding.UTF8);
                Config c = Json.Deserialize<Config>(json);
                if (c != null) Cfg = c;
            }
            catch (Exception ex) { Console.WriteLine("config.json ignored: " + ex.Message); }
        }

        static void ProbeHandBrake()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(HandBrake, "--version");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string er = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    string all = o + "\n" + er;
                    Match m = Regex.Match(all, @"HandBrake\s+([\d\.]+)");
                    if (m.Success) HandBrakeVersion = m.Groups[1].Value;
                    HwEncode = all.IndexOf("vcn: is available", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!HwEncode) HwEncode = all.IndexOf("AMD VCE", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception ex) { HandBrakeVersion = "not found (" + ex.Message + ")"; }
        }

        static void DetectFfmpeg()
        {
            Ffmpeg = "";
            try
            {
                if (!string.IsNullOrEmpty(Cfg.ffmpeg))
                {
                    string c = Cfg.ffmpeg;
                    if (!Path.IsPathRooted(c)) c = Path.Combine(Root, c);
                    if (File.Exists(c)) { Ffmpeg = c; return; }
                }
                string local = Path.Combine(Root, "bin\\ffmpeg.exe");
                if (File.Exists(local)) { Ffmpeg = local; return; }
                string path = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(path))
                {
                    foreach (string p in path.Split(';'))
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        string f = Path.Combine(p.Trim(), "ffmpeg.exe");
                        if (File.Exists(f)) { Ffmpeg = f; return; }
                    }
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- profiles

    static class Profiles
    {
        public static readonly List<Profile> All = new List<Profile>
        {
            new Profile { id = "h264-vce", label = "H.264 · AMD 硬编", desc = "VCE 硬件编码，最快，适合大批量快速出片", encoder = "vce_h264", preset = "balanced", quality = 28, family = "gpu" },
            new Profile { id = "h265-vce", label = "H.265 · AMD 硬编", desc = "VCE 硬编 HEVC，体积更小，兼容性略差", encoder = "vce_h265", preset = "balanced", quality = 28, family = "gpu" },
            new Profile { id = "h264-cpu", label = "H.264 · CPU 精压", desc = "x264 软件编码，压缩率高，速度慢", encoder = "x264", preset = "veryfast", quality = 23, family = "cpu" },
            new Profile { id = "h265-cpu", label = "H.265 · CPU 精压", desc = "x265 软件编码，体积最小，最慢", encoder = "x265", preset = "ultrafast", quality = 25, family = "cpu" },
            new Profile { id = "h264-fast", label = "H.264 · 极速", desc = "x264 ultrafast，比硬编更省体积，速度也快", encoder = "x264", preset = "ultrafast", quality = 26, family = "cpu" }
        };

        public static Profile Get(string id)
        {
            foreach (Profile p in All) if (p.id == id) return p;
            return All[0];
        }

        public static string Label(string id)
        {
            Profile p = null;
            foreach (Profile x in All) if (x.id == id) { p = x; break; }
            return p == null ? id : p.label;
        }
    }

    // ---------------------------------------------------------------- job manager

    public class JobManager
    {
        readonly List<Job> jobs = new List<Job>();
        readonly Queue<Job> queue = new Queue<Job>();
        readonly Dictionary<string, List<string>> logs = new Dictionary<string, List<string>>();
        readonly object gate = new object();
        readonly object logGate = new object();
        int running = 0;

        static readonly Regex ReProgress = new Regex(@"Encoding: task \d+ of \d+, ([\d\.]+) % \(([\d\.]+) fps, avg ([\d\.]+) fps, ETA ([^\)]+)\)", RegexOptions.Compiled);
        static readonly Regex ReProgressPlain = new Regex(@"Encoding: task \d+ of \d+, ([\d\.]+) %", RegexOptions.Compiled);
        static readonly Regex ReDuration = new Regex(@"^\s*\+\s*duration:\s*(\d+):(\d+):(\d+)", RegexOptions.Compiled);

        public void Start()
        {
            int n = Math.Max(1, Program.Cfg.maxParallel);
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(Loop);
                t.IsBackground = true;
                t.Name = "worker" + i;
                t.Start();
            }
            if (Program.Cfg.autoQueue) StartInboxWatcher();
        }

        void StartInboxWatcher()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(Program.InboxDir))
                        {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            if (ext != ".mp4" && ext != ".mkv" && ext != ".avi" && ext != ".mov" && ext != ".ts" && ext != ".m2ts" && ext != ".wmv" && ext != ".flv" && ext != ".webm") continue;
                            bool known = false;
                            lock (gate) { foreach (Job j in jobs) if (string.Equals(j.source, f, StringComparison.OrdinalIgnoreCase)) { known = true; break; } }
                            if (!known) Create(f, Program.Cfg.autoProfile, 0, "source", "source", "copy:aac", 128, "", Program.Cfg.autoSubtitle, "auto", "", true);
                        }
                    }
                    catch { }
                    Thread.Sleep(10000);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Loop()
        {
            while (true)
            {
                Job job = null;
                lock (gate)
                {
                    if (queue.Count > 0 && running < Math.Max(1, Program.Cfg.maxParallel))
                    {
                        job = queue.Dequeue();
                        running++;
                    }
                    else Monitor.Wait(gate, 500);
                }
                if (job == null) continue;
                if (job.status != "queued")
                {
                    lock (gate) { running--; Monitor.PulseAll(gate); }
                    continue;
                }
                try { RunJob(job); }
                catch (Exception ex)
                {
                    job.status = "error";
                    job.error = ex.Message;
                    AddLog(job, "EXCEPTION: " + ex.Message);
                }
                finally
                {
                    lock (gate) { running--; Monitor.PulseAll(gate); }
                    Save();
                }
            }
        }

        public Job Create(string source, string profile, double quality, string resolution, string fpsMode, string audio, int audioBitrate, string custom,
                          string subMode, string subTrack, string subFile, bool enqueue)
        {
            Job j = new Job();
            j.id = "j" + DateTime.Now.ToString("yyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            j.source = source;
            j.name = Path.GetFileName(source);
            j.profile = string.IsNullOrEmpty(profile) ? "h264-vce" : profile;
            j.quality = quality;
            j.resolution = string.IsNullOrEmpty(resolution) ? "source" : resolution;
            j.fpsMode = string.IsNullOrEmpty(fpsMode) ? "source" : fpsMode;
            j.audio = string.IsNullOrEmpty(audio) ? "copy:aac" : audio;
            j.audioBitrate = audioBitrate > 0 ? audioBitrate : 128;
            j.custom = custom == null ? "" : custom;
            j.subtitleMode = string.IsNullOrEmpty(subMode) ? "none" : subMode;
            j.subtitleTrack = string.IsNullOrEmpty(subTrack) ? "auto" : subTrack;
            j.subtitleFile = subFile == null ? "" : subFile;
            j.status = "queued";
            j.createdAt = Util.Iso(DateTime.Now);
            try { j.sizeIn = new FileInfo(source).Length; } catch { }
            j.output = Path.Combine(Program.OutDir, Util.UniqueName(Path.GetFileNameWithoutExtension(j.name) + "-" + j.profile, ".mp4"));

            lock (gate)
            {
                jobs.Insert(0, j);
                if (enqueue) queue.Enqueue(j);
                Monitor.PulseAll(gate);
            }
            AddLog(j, "job created: " + j.name + "  profile=" + j.profile);
            Save();
            return j;
        }

        public Job Get(string id)
        {
            lock (gate) { foreach (Job j in jobs) if (j.id == id) return j; }
            return null;
        }

        public List<Job> All()
        {
            lock (gate) { return new List<Job>(jobs); }
        }

        public int RunningCount() { lock (gate) { return running; } }
        public int QueuedCount() { lock (gate) { return queue.Count; } }

        public bool Cancel(string id)
        {
            Job j = Get(id);
            if (j == null) return false;
            lock (j)
            {
                if (j.status != "queued" && j.status != "running") return false;
                j.status = "canceled";
            }
            AddLog(j, "cancel requested");
            try { if (j.proc != null && !j.proc.HasExited) j.proc.Kill(); } catch { }
            return true;
        }

        public bool Remove(string id, bool purge)
        {
            Job j = Get(id);
            if (j == null) return false;
            if (j.status == "running") Cancel(id);
            bool sourceInUse = false;
            lock (gate)
            {
                foreach (Job o in jobs)
                {
                    if (o == j) continue;
                    if (string.Equals(o.source, j.source, StringComparison.OrdinalIgnoreCase)) { sourceInUse = true; break; }
                }
                jobs.Remove(j);
                Monitor.PulseAll(gate);
            }
            try { if (purge && !string.IsNullOrEmpty(j.output) && File.Exists(j.output)) File.Delete(j.output); } catch { }
            try { if (purge && !sourceInUse && !string.IsNullOrEmpty(j.source) && File.Exists(j.source) && j.source.StartsWith(Program.WorkDir, StringComparison.OrdinalIgnoreCase)) File.Delete(j.source); } catch { }
            try { if (purge) File.Delete(LogPath(j)); } catch { }
            Save();
            return true;
        }

        public List<string> Log(string id)
        {
            lock (logGate)
            {
                List<string> l;
                if (logs.TryGetValue(id, out l)) return new List<string>(l);
            }
            return new List<string>();
        }

        string LogPath(Job j) { return Path.Combine(Program.LogDir, j.id + ".log"); }

        public void AddLog(Job j, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (logGate)
            {
                List<string> l;
                if (!logs.TryGetValue(j.id, out l)) { l = new List<string>(); logs[j.id] = l; }
                l.Add(line);
                if (l.Count > 400) l.RemoveRange(0, l.Count - 400);
            }
            try { File.AppendAllText(LogPath(j), line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        string BuildArgs(Job j, string encodeSrc)
        {
            Profile p = Profiles.Get(j.profile);
            StringBuilder sb = new StringBuilder();
            sb.Append("-i \"").Append(encodeSrc).Append("\" ");
            sb.Append("-o \"").Append(j.output).Append("\" ");
            sb.Append("-f av_mp4 ");
            sb.Append("-e ").Append(p.encoder).Append(" ");
            if (!string.IsNullOrEmpty(p.preset)) sb.Append("--encoder-preset ").Append(p.preset).Append(" ");
            if (!p.copyVideo)
            {
                bool customBitrate = !string.IsNullOrEmpty(j.custom) &&
                    (j.custom.Contains("-b ") || j.custom.Contains("--vb") || j.custom.Contains("--bitrate"));
                if (!customBitrate)
                {
                    double q = j.quality > 0 ? j.quality : p.quality;
                    sb.Append("-q ").Append(q.ToString("0.##", CultureInfo.InvariantCulture)).Append(" ");
                }
            }
            if (j.audio == "none") sb.Append("-a none ");
            else if (j.audio == "av_aac")
            {
                sb.Append("-E av_aac ");
                sb.Append("-B ").Append(j.audioBitrate).Append(" ");
            }
            else
            {
                sb.Append("-E copy --audio-fallback av_aac ");
                sb.Append("-B ").Append(j.audioBitrate).Append(" ");
            }
            if (j.resolution != "source")
            {
                int w = 0, h = 0;
                if (j.resolution == "1080p") { w = 1920; h = 1080; }
                else if (j.resolution == "720p") { w = 1280; h = 720; }
                else if (j.resolution == "480p") { w = 854; h = 480; }
                if (w > 0) sb.Append("-X ").Append(w).Append(" -Y ").Append(h).Append(" ");
            }
            if (j.fpsMode != "source")
            {
                string fps = j.fpsMode == "30" ? "30" : (j.fpsMode == "25" ? "25" : "24");
                sb.Append("--rate ").Append(fps).Append(" --pfr ");
            }
            if (!string.IsNullOrEmpty(j.custom)) sb.Append(j.custom).Append(" ");
            if (!string.IsNullOrEmpty(j.subSrtPath))
            {
                sb.Append("--srt-file \"").Append(j.subSrtPath).Append("\" ");
                sb.Append("--srt-codeset UTF-8 ");
                sb.Append("--srt-lang ").Append(string.IsNullOrEmpty(j.subtitleLang) ? "chi" : j.subtitleLang).Append(" ");
                if (j.srtOffsetMs != 0) sb.Append("--srt-offset ").Append(j.srtOffsetMs).Append(" ");
                if (j.subBurn == "1") sb.Append("--srt-burn=1 ");
            }
            else if (!string.IsNullOrEmpty(j.subTrack))
            {
                sb.Append("-s ").Append(j.subTrack).Append(" ");
                if (j.subBurn == "1") sb.Append("--subtitle-burned=").Append(j.subTrack).Append(" ");
            }
            sb.Append("--optimize ");
            return sb.ToString().Trim();
        }

        void ResolveSubtitles(Job job, MediaInfo info)
        {
            job.subUsed = "";
            job.subNote = "";
            job.subTrack = "";
            job.subBurn = "";
            job.subSrtPath = "";
            job.subOriginal = "";
            job.subNeedsMux = false;
            job.subBurned = false;

            string mode = string.IsNullOrEmpty(job.subtitleMode) ? "none" : job.subtitleMode;
            if (mode != "burn" && mode != "soft") return;

            AddLog(job, "probe: dur=" + Math.Round(info.durationSec, 2) + "s " + info.width + "x" + info.height + " subs=" + info.subs.Count);

            Profile prof = Profiles.Get(job.profile);
            if (prof.copyVideo && mode == "burn")
            {
                mode = "soft";
                job.subNote = "当前方案不能烧录，已改为软字幕";
            }

            string srt = "";
            if (!string.IsNullOrEmpty(job.subtitleFile))
            {
                string cand = Path.IsPathRooted(job.subtitleFile) ? job.subtitleFile : Path.Combine(Program.Root, job.subtitleFile);
                if (File.Exists(cand)) srt = cand;
            }
            if (string.IsNullOrEmpty(srt)) srt = Util.FindExternalSub(job.source);

            if (!string.IsNullOrEmpty(srt))
            {
                job.subOriginal = srt;
                job.subBurn = mode == "burn" ? "1" : "";
                string ext = Path.GetExtension(srt).ToLowerInvariant();
                bool isAss = ext == ".ass" || ext == ".ssa";

                if (isAss && mode == "burn" && !string.IsNullOrEmpty(Program.Ffmpeg))
                {
                    job.subNeedsMux = true;
                    job.subSrtPath = "";
                    job.subTrack = "";
                    job.subUsed = "外挂 " + Path.GetFileName(srt) + " · 烧录（保留 ASS 样式）";
                    AddLog(job, "subtitle: ass -> mux into mkv, burn with libass");
                    return;
                }

                string srtFile = Util.EnsureSrt(srt);
                if (string.IsNullOrEmpty(srtFile))
                {
                    job.subNote = "字幕文件无法解析，已跳过字幕";
                    AddLog(job, "subtitle: cannot parse " + srt);
                    return;
                }
                job.subSrtPath = srtFile;
                job.subTrack = "";
                job.subUsed = "外挂 " + Path.GetFileName(srt) + (mode == "burn" ? " · 烧录" : " · 软字幕");
                if (mode == "burn")
                {
                    double cs = ProbeContainerStart(job.source);
                    if (Math.Abs(cs) > 0.001)
                    {
                        job.srtOffsetMs = (int)Math.Round(Math.Max(0.0, -cs) * 1000) + 100;
                        AddLog(job, "subtitle: container start " + cs.ToString("0.###", CultureInfo.InvariantCulture) + "s, srt offset +" + job.srtOffsetMs + "ms");
                    }
                }
                if (!string.Equals(srtFile, srt, StringComparison.OrdinalIgnoreCase))
                    job.subNote = mode == "burn"
                        ? "ASS/非 UTF-8 字幕已转成普通样式烧录"
                        : "ASS/非 UTF-8 字幕已转成普通样式（软字幕）";
                AddLog(job, "subtitle: external " + srt + " -> " + Path.GetFileName(srtFile) + (mode == "burn" ? " (srt-burn)" : " (soft)"));
                return;
            }

            if (info.subs.Count == 0)
            {
                job.subNote = "源文件里没有字幕轨";
                AddLog(job, "subtitle: none found in source");
                return;
            }

            SubInfo sel = null;
            int want = 0;
            int parsed;
            if (!string.IsNullOrEmpty(job.subtitleTrack) && job.subtitleTrack != "auto" && int.TryParse(job.subtitleTrack, out parsed)) want = parsed;
            if (want > 0)
            {
                foreach (SubInfo s in info.subs) if (s.track == want) { sel = s; break; }
            }
            if (sel == null)
            {
                foreach (SubInfo s in info.subs) if (Util.IsChinese(s)) { sel = s; break; }
            }
            if (sel == null) sel = info.subs[0];

            if (mode == "soft" && sel.format != "text")
            {
                mode = "burn";
                job.subNote = "图形字幕（" + sel.sourceName + "）无法软封装，已改为烧录";
            }

            job.subTrack = sel.track.ToString(CultureInfo.InvariantCulture);
            job.subBurn = mode == "burn" ? "1" : "";
            job.subUsed = sel.name + (mode == "burn" ? " · 烧录" : " · 软字幕");
            AddLog(job, "subtitle: track " + sel.track + " [" + sel.name + " / " + sel.sourceName + " / " + sel.format + "] -> " + (mode == "burn" ? "burn" : "soft"));
        }

        double ProbeStartTime(string video)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Program.Ffmpeg, "-hide_banner -i \"" + video + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    Match m = Regex.Match(err, @"start:\s*(-?[\d\.]+)");
                    if (m.Success)
                    {
                        double v;
                        if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
                    }
                }
            }
            catch { }
            return 0;
        }

        double ProbeContainerStart(string video)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Program.Ffmpeg, "-hide_banner -i \"" + video + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    Thread drain = new Thread(delegate() { try { p.StandardOutput.ReadToEnd(); } catch { } });
                    drain.IsBackground = true;
                    drain.Start();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    Match m = Regex.Match(err, @"Duration:.*?start:\s*(-?[\d\.]+)");
                    if (m.Success)
                    {
                        double v;
                        if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
                    }
                }
            }
            catch { }
            return 0;
        }

        bool RunFfmpegAssMux(Job job, string ass, out string tempPath)
        {
            tempPath = "";
            if (string.IsNullOrEmpty(Program.Ffmpeg) || !File.Exists(Program.Ffmpeg)) return false;
            string dir = Path.GetDirectoryName(job.source);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            string name = "_ass_" + job.id + ".mkv";
            string full = Path.Combine(dir, name);
            try { if (File.Exists(full)) File.Delete(full); } catch { }
            try
            {
                // HandBrake drops subtitle packets that start before the first video frame.
                // Sources with a negative/non-zero container start (vp9/opus mkv, ts, ...)
                // make the muxed subtitle start a few ms before that frame, so nudge it.
                double cstart = ProbeContainerStart(job.source);
                string offsetArg = "";
                if (Math.Abs(cstart) > 0.001)
                {
                    double shift = Math.Max(0.0, -cstart) + 0.1;
                    offsetArg = "-itsoffset " + shift.ToString("0.###", CultureInfo.InvariantCulture) + " ";
                    AddLog(job, "subtitle: container start " + cstart.ToString("0.###", CultureInfo.InvariantCulture) + "s, nudging subtitles +" + shift.ToString("0.###", CultureInfo.InvariantCulture) + "s");
                }

                string lang = string.IsNullOrEmpty(job.subtitleLang) ? "chi" : job.subtitleLang;
                string args = "-y -hide_banner -loglevel error -i \"" + job.source + "\" " + offsetArg + "-i \"" + ass + "\" " +
                              "-map 0:v:0 -map 0:a? -map 1:0 -c copy -c:s ass " +
                              "-metadata:s:s:0 language=" + lang + " \"" + full + "\"";
                ProcessStartInfo psi = new ProcessStartInfo(Program.Ffmpeg, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    job.proc = p;
                    Thread d = new Thread(delegate() { try { p.StandardError.ReadToEnd(); } catch { } });
                    d.IsBackground = true;
                    d.Start();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(1800000)) { try { p.Kill(); } catch { } }
                    job.proc = null;
                    if (p.ExitCode != 0) return false;
                }
                if (!File.Exists(full)) return false;
                if (new FileInfo(full).Length < 1024) { try { File.Delete(full); } catch { } return false; }
                LogMuxInfo(job, full, ass);
                tempPath = full;
                return true;
            }
            catch { return false; }
        }

        void LogMuxInfo(Job job, string muxed, string ass)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Program.Ffmpeg,
                    "-hide_banner -loglevel error -i \"" + muxed + "\" -map 0:s:0 -c:s srt -f srt -");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    Thread drain = new Thread(delegate() { try { p.StandardError.ReadToEnd(); } catch { } });
                    drain.IsBackground = true;
                    drain.Start();
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(60000);
                    StringBuilder sb = new StringBuilder();
                    int count = 0;
                    int srtEvents = 0;
                    foreach (string raw in outp.Replace("\r\n", "\n").Split('\n'))
                    {
                        string l = raw.Trim();
                        if (l.Length == 0) continue;
                        if (l.IndexOf("-->", StringComparison.Ordinal) >= 0) srtEvents++;
                        if (l.IndexOf("-->", StringComparison.Ordinal) < 0 && !l.StartsWith("<")) continue;
                        if (count < 3)
                        {
                            sb.Append(l.Length > 60 ? l.Substring(0, 60) : l).Append("  ");
                            count++;
                        }
                    }
                    int assEvents = 0;
                    try
                    {
                        string txt = Util.ReadTextSmart(ass);
                        foreach (string raw in txt.Replace("\r\n", "\n").Split('\n'))
                        {
                            string l = raw.TrimStart();
                            if (l.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) assEvents++;
                        }
                    }
                    catch { }
                    AddLog(job, "subtitle: ass dialogue lines=" + assEvents + ", events in muxed track=" + srtEvents);
                    AddLog(job, "subtitle: muxed track starts with: " + sb.ToString());
                }
            }
            catch { }
        }

        void RunJob(Job job)
        {
            job.status = "running";
            job.stage = "probe";
            job.startedAt = Util.Iso(DateTime.Now);
            job.startTime = DateTime.Now;
            job.durationSec = 0;
            job.speed = 0;
            job.fps = 0;
            job.progress = 0;
            job.eta = "";
            job.error = "";
            Save();

            if (!string.IsNullOrEmpty(job.subtitleMode) && job.subtitleMode != "none")
            {
                MediaInfo info = Util.Probe(job.source);
                if (info.durationSec > 0) job.durationSec = info.durationSec;
                ResolveSubtitles(job, info);
            }

            string encodeSrc = job.source;
            string tempSub = "";
            if (job.status == "canceled") return;
            if (job.subNeedsMux && !string.IsNullOrEmpty(job.subOriginal))
            {
                job.stage = "subtitle";
                Save();
                string tmp;
                if (RunFfmpegAssMux(job, job.subOriginal, out tmp))
                {
                    encodeSrc = tmp;
                    tempSub = tmp;
                    job.subTrack = "1";
                    job.subBurn = "1";
                    job.subSrtPath = "";
                    AddLog(job, "subtitle: ass muxed into mkv, burn track 1");
                }
                else
                {
                    string srtFile = Util.EnsureSrt(job.subOriginal);
                    if (!string.IsNullOrEmpty(srtFile))
                    {
                        job.subSrtPath = srtFile;
                        job.subBurn = "1";
                        job.subNote = "ASS 封装失败，已改用 SRT 烧录（样式会丢失）";
                        AddLog(job, "subtitle: ffmpeg mux failed, fallback to srt-burn");
                    }
                    else
                    {
                        job.subBurn = "";
                        job.subNote = "字幕处理失败，已跳过";
                    }
                }
            }

            job.stage = "encode";
            job.args = BuildArgs(job, encodeSrc);
            AddLog(job, "args: " + job.args);
            Save();

            ProcessStartInfo psi = new ProcessStartInfo(Program.HandBrake, job.args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Program.WorkDir;

            Process p = new Process();
            p.StartInfo = psi;
            p.Start();
            job.proc = p;

            Thread err = new Thread(delegate()
            {
                try
                {
                    string line;
                    while ((line = p.StandardError.ReadLine()) != null) ProcessLine(job, line);
                }
                catch { }
            });
            err.IsBackground = true;
            err.Start();

            try
            {
                StreamReader so = p.StandardOutput;
                char[] buf = new char[2048];
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    int n = so.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        char c = buf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0) { ProcessLine(job, sb.ToString()); sb.Length = 0; }
                        }
                        else
                        {
                            sb.Append(c);
                            if (sb.Length > 4000) sb.Length = 0;
                        }
                    }
                }
                if (sb.Length > 0) ProcessLine(job, sb.ToString());
                p.WaitForExit();
            }
            finally
            {
                try { err.Join(2000); } catch { }
                job.proc = null;
            }

            int code = -1;
            try { code = p.ExitCode; } catch { }
            job.elapsed = 0;
            try { job.elapsed = (DateTime.Now - DateTime.Parse(job.startedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime()).TotalSeconds; } catch { }
            job.finishedAt = Util.Iso(DateTime.Now);

            bool ok = code == 0 && File.Exists(job.output) && new FileInfo(job.output).Length > 0;
            if (job.status == "canceled")
            {
                try { if (File.Exists(job.output)) File.Delete(job.output); } catch { }
                AddLog(job, "canceled");
            }
            else if (ok)
            {
                job.status = "done";
                job.progress = 100;
                job.sizeOut = new FileInfo(job.output).Length;
                if (job.subBurn == "1" && !job.subBurned)
                    job.subNote = (job.subNote + " 字幕可能没有烧录成功，请点「日志」确认").Trim();
                AddLog(job, "done: " + job.sizeOut + " bytes in " + Math.Round(job.elapsed) + "s");
            }
            else
            {
                job.status = "error";
                job.error = "HandBrake 退出码 " + code + "，详情见日志";
                try { if (File.Exists(job.output) && new FileInfo(job.output).Length == 0) File.Delete(job.output); } catch { }
                AddLog(job, "failed, exit code " + code);
            }
            try { if (!string.IsNullOrEmpty(tempSub) && File.Exists(tempSub)) File.Delete(tempSub); } catch { }
            Save();
        }

        void ProcessLine(Job job, string line)
        {
            lock (job) { ProcessLineCore(job, line); }
        }

        void ProcessLineCore(Job job, string line)
        {
            if (line.IndexOf("Render/Burn-in", StringComparison.Ordinal) >= 0) job.subBurned = true;
            Match md = ReDuration.Match(line);
            if (md.Success)
            {
                try
                {
                    job.durationSec = int.Parse(md.Groups[1].Value) * 3600 + int.Parse(md.Groups[2].Value) * 60 + int.Parse(md.Groups[3].Value);
                }
                catch { }
                return;
            }

            Match m = ReProgress.Match(line);
            if (!m.Success) m = ReProgressPlain.Match(line);
            if (m.Success)
            {
                try
                {
                    job.progress = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (m.Groups.Count > 2)
                    {
                        job.fps = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        job.eta = m.Groups[4].Value.Trim();
                    }
                    RecalcSpeed(job);
                }
                catch { }
                return;
            }

            if (line.Length == 0) return;
            if (line.StartsWith("Encoding:", StringComparison.OrdinalIgnoreCase)) return;
            AddLog(job, line);
        }

        void RecalcSpeed(Job job)
        {
            if (job.startTime == default(DateTime) || job.durationSec <= 0) return;
            if (job.progress < 2) return;
            double el = (DateTime.Now - job.startTime).TotalSeconds;
            if (el < 1.5) return;
            double done = job.durationSec * job.progress / 100.0;
            if (done <= 0) return;
            job.speed = done / el;
            if (job.speed > 0.01)
            {
                double remain = Math.Max(0.0, (job.durationSec - done) / job.speed);
                job.eta = Util.FmtDuration(remain);
            }
        }

        // ------------------------------------------------------------ persist

        public void Save()
        {
            try
            {
                List<Dictionary<string, object>> dto = new List<Dictionary<string, object>>();
                lock (gate)
                {
                    foreach (Job j in jobs)
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["id"] = j.id; d["name"] = j.name; d["source"] = j.source; d["output"] = j.output;
                        d["profile"] = j.profile; d["status"] = j.status; d["progress"] = j.progress;
                        d["fps"] = j.fps; d["speed"] = j.speed; d["durationSec"] = j.durationSec; d["eta"] = j.eta; d["sizeIn"] = j.sizeIn; d["sizeOut"] = j.sizeOut;
                        d["error"] = j.error; d["createdAt"] = j.createdAt; d["startedAt"] = j.startedAt;
                        d["finishedAt"] = j.finishedAt; d["elapsed"] = j.elapsed; d["args"] = j.args;
                        d["quality"] = j.quality; d["resolution"] = j.resolution; d["fpsMode"] = j.fpsMode;
                        d["audio"] = j.audio; d["audioBitrate"] = j.audioBitrate; d["custom"] = j.custom;
                        d["subtitleMode"] = j.subtitleMode; d["subtitleTrack"] = j.subtitleTrack; d["subtitleFile"] = j.subtitleFile;
                        d["subUsed"] = j.subUsed; d["subNote"] = j.subNote;
                        dto.Add(d);
                    }
                }
                string json = Program.Json.Serialize(dto);
                string tmp = Path.Combine(Program.DataDir, "jobs.json.tmp");
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                string dst = Path.Combine(Program.DataDir, "jobs.json");
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
            }
            catch { }
        }

        public void Load()
        {
            try
            {
                string f = Path.Combine(Program.DataDir, "jobs.json");
                if (!File.Exists(f)) return;
                string json = File.ReadAllText(f, Encoding.UTF8);
                List<Dictionary<string, object>> dto = Program.Json.Deserialize<List<Dictionary<string, object>>>(json);
                if (dto == null) return;
                foreach (Dictionary<string, object> d in dto)
                {
                    Job j = new Job();
                    j.id = Util.Str(d, "id"); j.name = Util.Str(d, "name"); j.source = Util.Str(d, "source");
                    j.output = Util.Str(d, "output"); j.profile = Util.Str(d, "profile"); j.status = Util.Str(d, "status");
                    j.progress = Util.Num(d, "progress"); j.fps = Util.Num(d, "fps"); j.eta = Util.Str(d, "eta");
                    j.speed = Util.Num(d, "speed"); j.durationSec = Util.Num(d, "durationSec");
                    j.sizeIn = (long)Util.Num(d, "sizeIn"); j.sizeOut = (long)Util.Num(d, "sizeOut");
                    j.error = Util.Str(d, "error"); j.createdAt = Util.Str(d, "createdAt"); j.startedAt = Util.Str(d, "startedAt");
                    j.finishedAt = Util.Str(d, "finishedAt"); j.elapsed = Util.Num(d, "elapsed"); j.args = Util.Str(d, "args");
                    j.quality = Util.Num(d, "quality"); j.resolution = Util.Str(d, "resolution"); j.fpsMode = Util.Str(d, "fpsMode");
                    j.audio = Util.Str(d, "audio"); j.audioBitrate = (int)Util.Num(d, "audioBitrate"); j.custom = Util.Str(d, "custom");
                    j.subtitleMode = Util.Str(d, "subtitleMode"); j.subtitleTrack = Util.Str(d, "subtitleTrack"); j.subtitleFile = Util.Str(d, "subtitleFile");
                    j.subUsed = Util.Str(d, "subUsed"); j.subNote = Util.Str(d, "subNote");
                    if (j.status == "running" || j.status == "queued")
                    {
                        j.status = "error";
                        j.error = "服务重启，任务中断";
                    }
                    jobs.Add(j);
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- http

    public class HttpRequest
    {
        public string Method = "GET";
        public string Path = "/";
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long ContentLength;
        public Stream Body;

        public string Get(string k)
        {
            string v;
            return Query.TryGetValue(k, out v) ? v : null;
        }

        public string Header(string k)
        {
            string v;
            return Headers.TryGetValue(k, out v) ? v : null;
        }
    }

    public class BodyStream : Stream
    {
        readonly byte[] prefix;
        int pos, end;
        readonly Stream inner;
        long remaining;

        public BodyStream(byte[] p, int off, int count, Stream s, long total)
        {
            prefix = p; pos = off; end = off + count; inner = s; remaining = total < 0 ? 0 : total;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0) return 0;
            int want = (int)Math.Min(count, remaining);
            int got;
            if (pos < end)
            {
                got = Math.Min(want, end - pos);
                Array.Copy(prefix, pos, buffer, offset, got);
                pos += got;
            }
            else
            {
                got = inner.Read(buffer, offset, want);
                if (got <= 0) return 0;
            }
            remaining -= got;
            return got;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
    }

    public class HttpServer
    {
        readonly int port;
        TcpListener listener;

        public HttpServer(int p) { port = p; }

        public void Run()
        {
            listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.ExclusiveAddressUse = true;
                listener.Start(128);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: cannot listen on port " + port + " (port already in use).");
                Console.WriteLine("       Another instance may be running, or another program uses this port.");
                Console.WriteLine("       Try:  TranscodeServer.exe --port 8098");
                Console.WriteLine("       Detail: " + ex.GetType().Name + " " + ex.HResult);
                return;
            }
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 600000;
                client.SendTimeout = 600000;
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    HandleConnection(ns);
                }
            }
            catch { }
        }

        static int FindHeadEnd(byte[] b, int len)
        {
            for (int i = 3; i < len; i++)
                if (b[i - 3] == 13 && b[i - 2] == 10 && b[i - 1] == 13 && b[i] == 10) return i + 1;
            return -1;
        }

        void HandleConnection(NetworkStream ns)
        {
            byte[] buf = new byte[131072];
            int total = 0;
            int end = -1;
            while (total < buf.Length)
            {
                int n = ns.Read(buf, total, buf.Length - total);
                if (n <= 0) return;
                total += n;
                end = FindHeadEnd(buf, total);
                if (end >= 0) break;
            }
            if (end < 0) { Util.SendText(ns, 431, "Request Header Fields Too Large"); return; }

            string head = Encoding.UTF8.GetString(buf, 0, end);
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) { Util.SendText(ns, 400, "Bad Request"); return; }

            HttpRequest req = new HttpRequest();
            req.Method = first[0].ToUpperInvariant();
            string target = first[1];
            int qm = target.IndexOf('?');
            string rawPath = qm >= 0 ? target.Substring(0, qm) : target;
            string qs = qm >= 0 ? target.Substring(qm + 1) : "";
            try { req.Path = Uri.UnescapeDataString(rawPath); } catch { req.Path = rawPath; }
            req.Query = Util.ParseQuery(qs);

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                string k = lines[i].Substring(0, c).Trim();
                string v = lines[i].Substring(c + 1).Trim();
                if (!req.Headers.ContainsKey(k)) req.Headers[k] = v;
            }

            string cl;
            if (req.Headers.TryGetValue("Content-Length", out cl))
            {
                long v;
                if (long.TryParse(cl, out v)) req.ContentLength = v;
            }
            req.Body = new BodyStream(buf, end, total - end, ns, req.ContentLength);

            Router.Handle(req, ns);
        }
    }

    // ---------------------------------------------------------------- router

    static class Router
    {
        public static void Handle(HttpRequest req, Stream ns)
        {
            try
            {
                string p = req.Path;

                if (req.Method == "OPTIONS") { Util.SendEmpty(ns, 204); return; }

                if (p == "/" || p == "/index.html")
                {
                    string idx = Path.Combine(Program.WebDir, "index.html");
                    if (File.Exists(idx)) Util.SendFile(ns, idx, "text/html; charset=utf-8", false, null, "index.html");
                    else Util.SendText(ns, 500, "web/index.html missing");
                    return;
                }
                if (p == "/favicon.ico") { Util.SendEmpty(ns, 204); return; }
                if (p == "/health") { Util.SendText(ns, 200, "ok"); return; }

                if (p == "/api/status" && req.Method == "GET") { Status(ns); return; }
                if (p == "/api/profiles" && req.Method == "GET") { ProfilesJson(ns); return; }
                if (p == "/api/jobs")
                {
                    if (req.Method == "GET") { JobsJson(ns); return; }
                    if (req.Method == "POST") { CreateJob(ns, req); return; }
                }
                if (p == "/api/upload" && req.Method == "PUT") { Upload(ns, req); return; }
                if (p == "/api/inbox" && req.Method == "GET") { Inbox(ns); return; }
                if (p == "/api/probe" && req.Method == "GET") { Probe(ns, req); return; }
                if (p == "/api/staged" && req.Method == "DELETE") { Discard(ns, req); return; }

                if (p.StartsWith("/api/jobs/"))
                {
                    string rest = p.Substring("/api/jobs/".Length);
                    string[] seg = rest.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length == 0) { Util.SendText(ns, 404, "not found"); return; }
                    string id = seg[0];
                    string sub = seg.Length > 1 ? seg[1] : "";

                    if (sub == "" && req.Method == "GET") { JobJson(ns, id); return; }
                    if (sub == "" && req.Method == "DELETE")
                    {
                        bool purge = req.Get("purge") == "1";
                        Util.SendJson(ns, 200, new Dictionary<string, object> { { "ok", Program.Jobs.Remove(id, purge) } });
                        return;
                    }
                    if (sub == "cancel" && req.Method == "POST") { Util.SendJson(ns, 200, new Dictionary<string, object> { { "ok", Program.Jobs.Cancel(id) } }); return; }
                    if (sub == "log" && req.Method == "GET")
                    {
                        List<string> log = Program.Jobs.Log(id);
                        Util.SendText(ns, 200, string.Join("\n", log.ToArray()));
                        return;
                    }
                    if (sub == "download" && req.Method == "GET") { Download(ns, req, id); return; }
                    if (sub == "source" && req.Method == "GET") { Source(ns, req, id); return; }
                }

                Util.SendText(ns, 404, "not found");
            }
            catch (Exception ex)
            {
                try { Util.SendText(ns, 500, "error: " + ex.Message); } catch { }
            }
        }

        static void Status(Stream ns)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["version"] = "1.0";
            d["port"] = Program.Port;
            d["addresses"] = Util.LocalIPv4List();
            d["handbrake"] = Program.HandBrakeVersion;
            d["handbrakePath"] = Program.HandBrake;
            d["hw"] = Program.HwEncode;
            d["cpu"] = Util.CpuName();
            d["cores"] = Environment.ProcessorCount;
            d["cpuLoad"] = Load.Cpu < 0 ? -1 : Math.Round(Load.Cpu, 1);
            d["gpuLoad"] = Load.Gpu < 0 ? -1 : Math.Round(Load.Gpu, 1);
            d["gpuEngine"] = Load.GpuEngine;
            d["os"] = Environment.OSVersion.VersionString;
            d["uptime"] = (long)(DateTime.Now - Program.Started).TotalSeconds;
            d["running"] = Program.Jobs.RunningCount();
            d["queued"] = Program.Jobs.QueuedCount();
            d["outputDir"] = Program.OutDir;
            d["workDir"] = Program.WorkDir;
            d["diskFreeGB"] = Util.DiskFreeGb(Program.OutDir);
            d["autoQueue"] = Program.Cfg.autoQueue;
            long used = 0, free = 0;
            try { DriveInfo di = new DriveInfo(Path.GetPathRoot(Program.OutDir)); used = di.TotalSize - di.AvailableFreeSpace; free = di.AvailableFreeSpace; } catch { }
            d["diskTotalGB"] = (double)used / 1073741824.0 + (double)free / 1073741824.0;
            Util.SendJson(ns, 200, d);
        }

        static void ProfilesJson(Stream ns)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (Profile p in Profiles.All)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = p.id; d["label"] = p.label; d["desc"] = p.desc;
                d["quality"] = p.quality; d["family"] = p.family; d["copy"] = p.copyVideo;
                list.Add(d);
            }
            Util.SendJson(ns, 200, list);
        }

        static void JobsJson(Stream ns)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (Job j in Program.Jobs.All()) list.Add(Dto(j));
            Util.SendJson(ns, 200, list);
        }

        static void JobJson(Stream ns, string id)
        {
            Job j = Program.Jobs.Get(id);
            if (j == null) { Util.SendText(ns, 404, "no such job"); return; }
            Util.SendJson(ns, 200, Dto(j));
        }

        public static Dictionary<string, object> Dto(Job j)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = j.id;
            d["name"] = j.name;
            d["status"] = j.status;
            d["progress"] = Math.Round(j.progress, 2);
            d["fps"] = Math.Round(j.fps, 1);
            d["speed"] = Math.Round(j.speed, 2);
            d["durationSec"] = j.durationSec;
            d["eta"] = j.eta;
            d["sizeIn"] = j.sizeIn;
            d["sizeOut"] = j.sizeOut;
            d["profile"] = j.profile;
            d["profileLabel"] = Profiles.Label(j.profile);
            d["error"] = j.error;
            d["createdAt"] = j.createdAt;
            d["startedAt"] = j.startedAt;
            d["finishedAt"] = j.finishedAt;
            d["elapsed"] = Math.Round(j.elapsed, 1);
            d["quality"] = j.quality;
            d["resolution"] = j.resolution;
            d["fpsMode"] = j.fpsMode;
            d["hasOutput"] = j.status == "done" && !string.IsNullOrEmpty(j.output) && File.Exists(j.output);
            d["sourceExists"] = !string.IsNullOrEmpty(j.source) && File.Exists(j.source);
            d["args"] = j.args;
            d["stage"] = j.stage;
            d["subtitleMode"] = j.subtitleMode;
            d["subtitleTrack"] = j.subtitleTrack;
            d["subtitleUsed"] = j.subUsed;
            d["subtitleNote"] = j.subNote;
            d["subtitleBurned"] = j.subBurned;
            string rel = j.source;
            try
            {
                if (!string.IsNullOrEmpty(rel) && rel.StartsWith(Program.Root, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(Program.Root.Length).TrimStart('\\').Replace('\\', '/');
            }
            catch { }
            d["source"] = rel;
            return d;
        }

        static void CreateJob(Stream ns, HttpRequest req)
        {
            string body = Util.ReadBodyText(req);
            Dictionary<string, object> d = Program.Json.Deserialize<Dictionary<string, object>>(body);
            if (d == null) { Util.SendText(ns, 400, "invalid json"); return; }

            string source = Util.Str(d, "source");
            if (string.IsNullOrEmpty(source)) { Util.SendText(ns, 400, "source required"); return; }
            if (!Path.IsPathRooted(source)) source = Path.Combine(Program.Root, source);
            source = Path.GetFullPath(source);
            if (!File.Exists(source)) { Util.SendText(ns, 400, "source not found: " + source); return; }

            string profile = Util.Str(d, "profile");
            double quality = d.ContainsKey("quality") ? Util.Num(d, "quality") : 0;
            string res = Util.Str(d, "resolution");
            string fps = Util.Str(d, "fps");
            string audio = Util.Str(d, "audio");
            int ab = d.ContainsKey("audioBitrate") ? (int)Util.Num(d, "audioBitrate") : 128;
            string custom = Util.Str(d, "custom");
            string subMode = Util.Str(d, "subtitleMode");
            string subTrack = Util.Str(d, "subtitleTrack");
            string subFile = Util.Str(d, "subtitleFile");

            Job j = Program.Jobs.Create(source, profile, quality, res, fps, audio, ab, custom, subMode, subTrack, subFile, true);
            Util.SendJson(ns, 200, Dto(j));
        }

        static void Upload(Stream ns, HttpRequest req)
        {
            string name = req.Get("name");
            if (string.IsNullOrEmpty(name)) name = "upload.mp4";
            name = Util.SafeName(name);
            string id = req.Get("group");
            if (string.IsNullOrEmpty(id) || !IsSafeId(id))
                id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string dir = Path.Combine(Program.WorkDir, id);
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, name);

            long written = 0;
            using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.Read, 262144))
            {
                byte[] buf = new byte[262144];
                int n;
                while ((n = req.Body.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    written += n;
                }
            }

            string rel = "work/" + id + "/" + name;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["source"] = rel;
            r["name"] = name;
            r["size"] = written;
            r["group"] = id;
            Util.SendJson(ns, 200, r);
        }

        static bool IsSafeId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        static void Probe(Stream ns, HttpRequest req)
        {
            string src = req.Get("source");
            if (string.IsNullOrEmpty(src)) { Util.SendText(ns, 400, "source required"); return; }
            if (!Path.IsPathRooted(src)) src = Path.Combine(Program.Root, src);
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) { Util.SendText(ns, 404, "source not found"); return; }

            MediaInfo mi = Util.Probe(src);
            List<Dictionary<string, object>> subs = new List<Dictionary<string, object>>();
            foreach (SubInfo s in mi.subs)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["track"] = s.track;
                d["name"] = s.name;
                d["lang"] = s.langCode;
                d["format"] = s.format;
                d["codec"] = s.sourceName;
                d["forced"] = s.forced;
                d["default"] = s.isDefault;
                d["chinese"] = Util.IsChinese(s);
                subs.Add(d);
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["duration"] = Math.Round(mi.durationSec, 2);
            r["width"] = mi.width;
            r["height"] = mi.height;
            r["fps"] = Math.Round(mi.fps, 3);
            r["subs"] = subs;
            r["externalSub"] = Util.FindExternalSub(src);
            r["debug"] = Util.ProbeDebug;
            Util.SendJson(ns, 200, r);
        }

        static void Discard(Stream ns, HttpRequest req)
        {
            string src = req.Get("source");
            if (string.IsNullOrEmpty(src)) { Util.SendText(ns, 400, "source required"); return; }
            if (!Path.IsPathRooted(src)) src = Path.Combine(Program.Root, src);
            src = Path.GetFullPath(src);
            bool ok = false;
            try
            {
                if (src.StartsWith(Program.WorkDir, StringComparison.OrdinalIgnoreCase) && File.Exists(src))
                {
                    string dir = Path.GetDirectoryName(src);
                    if (!string.IsNullOrEmpty(dir) && dir.Length > Program.WorkDir.Length)
                    {
                        Directory.Delete(dir, true);
                        ok = true;
                    }
                }
            }
            catch { }
            Util.SendJson(ns, 200, new Dictionary<string, object> { { "ok", ok } });
        }

        static void Inbox(Stream ns)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                foreach (string f in Directory.GetFiles(Program.InboxDir))
                {
                    FileInfo fi = new FileInfo(f);
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["name"] = fi.Name;
                    d["size"] = fi.Length;
                    d["mtime"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    d["source"] = "inbox/" + fi.Name;
                    list.Add(d);
                }
            }
            catch { }
            Util.SendJson(ns, 200, list);
        }

        static void Download(Stream ns, HttpRequest req, string id)
        {
            Job j = Program.Jobs.Get(id);
            if (j == null || string.IsNullOrEmpty(j.output) || !File.Exists(j.output))
            {
                Util.SendText(ns, 404, "no output file");
                return;
            }
            string fn = Path.GetFileName(j.output);
            Util.SendFile(ns, j.output, "video/mp4", true, req.Header("Range"), fn);
        }

        static void Source(Stream ns, HttpRequest req, string id)
        {
            Job j = Program.Jobs.Get(id);
            if (j == null || !File.Exists(j.source)) { Util.SendText(ns, 404, "no source"); return; }
            Util.SendFile(ns, j.source, Util.MimeFor(j.source), false, req.Header("Range"), Path.GetFileName(j.source));
        }
    }

    // ---------------------------------------------------------------- util

    static class Util
    {
        public static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null) return o.ToString();
            return "";
        }

        public static double Num(Dictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                double v;
                if (double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            }
            return 0;
        }

        public static string Iso(DateTime t)
        {
            return t.ToString("o", CultureInfo.InvariantCulture);
        }

        public static string FmtDuration(double sec)
        {
            int s = (int)Math.Max(0, Math.Round(sec));
            int h = s / 3600;
            int m = (s % 3600) / 60;
            int ss = s % 60;
            if (h > 0) return h + ":" + m.ToString("00") + ":" + ss.ToString("00");
            return m + ":" + ss.ToString("00");
        }

        public static string SafeName(string n)
        {
            string name = Path.GetFileName(n);
            if (string.IsNullOrEmpty(name)) name = "file";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                bool ok = true;
                foreach (char b in bad) if (c == b) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "file";
            if (s.Length > 120) s = s.Substring(s.Length - 120);
            return s;
        }

        public static string UniqueName(string baseName, string ext)
        {
            string dir = Program.OutDir;
            string candidate = baseName + ext;
            int i = 1;
            while (File.Exists(Path.Combine(dir, candidate)))
            {
                candidate = baseName + " (" + i + ")" + ext;
                i++;
                if (i > 999) break;
            }
            return candidate;
        }

        public static Dictionary<string, string> ParseQuery(string qs)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(qs)) return d;
            foreach (string part in qs.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq >= 0 ? part.Substring(0, eq) : part;
                string v = eq >= 0 ? part.Substring(eq + 1) : "";
                try
                {
                    k = Uri.UnescapeDataString(k.Replace('+', ' '));
                    v = Uri.UnescapeDataString(v.Replace('+', ' '));
                }
                catch { }
                if (k.Length > 0) d[k] = v;
            }
            return d;
        }

        public static string ReadBodyText(HttpRequest req)
        {
            if (req.ContentLength <= 0) return "";
            if (req.ContentLength > 4 * 1024 * 1024) throw new Exception("body too large");
            byte[] all = new byte[req.ContentLength];
            int total = 0;
            while (total < all.Length)
            {
                int n = req.Body.Read(all, total, all.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return Encoding.UTF8.GetString(all, 0, total);
        }

        public static string CpuName()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("ProcessorNameString");
                        if (v != null) return v.ToString().Trim();
                    }
                }
            }
            catch { }
            return "CPU";
        }

        public static double DiskFreeGb(string path)
        {
            try
            {
                DriveInfo di = new DriveInfo(Path.GetPathRoot(path));
                return Math.Round((double)di.AvailableFreeSpace / 1073741824.0, 1);
            }
            catch { return 0; }
        }

        public static List<string> LocalIPv4()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ip.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;
                        list.Add(s);
                    }
                }
            }
            catch { }
            return list;
        }

        public static string LocalIPv4List()
        {
            List<string> l = LocalIPv4();
            if (l.Count == 0) return "127.0.0.1";
            return string.Join(", ", l.ToArray());
        }

        public static string MimeFor(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            if (e == ".mp4" || e == ".m4v") return "video/mp4";
            if (e == ".mkv") return "video/x-matroska";
            if (e == ".webm") return "video/webm";
            if (e == ".avi") return "video/x-msvideo";
            if (e == ".mov") return "video/quicktime";
            if (e == ".ts" || e == ".m2ts") return "video/mp2t";
            return "application/octet-stream";
        }

        // ------------------------------------------------------------ probe

        public static MediaInfo Probe(string path)
        {
            MediaInfo mi = new MediaInfo();
            ProbeDebug = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Program.HandBrake, "-i \"" + path + "\" --scan --json");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    Thread drain = new Thread(delegate() { try { p.StandardError.ReadToEnd(); } catch { } });
                    drain.IsBackground = true;
                    drain.Start();
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } }
                    ProbeDebug = "exit=" + p.ExitCode + " stdout=" + o.Length;
                    int idx = o.IndexOf("JSON Title Set:", StringComparison.Ordinal);
                    ProbeDebug += " idx=" + idx;
                    if (idx < 0) return mi;
                    string json = o.Substring(idx + "JSON Title Set:".Length).Trim();
                    int last = json.LastIndexOf('}');
                    if (last > 0) json = json.Substring(0, last + 1);
                    ProbeDebug += " json=" + json.Length;
                    Dictionary<string, object> root = Program.Json.Deserialize<Dictionary<string, object>>(json);
                    ParseMedia(root, mi);
                    ProbeDebug += " subs=" + mi.subs.Count;
                }
            }
            catch (Exception ex)
            {
                ProbeDebug += " EX " + ex.GetType().Name + ": " + ex.Message;
            }
            return mi;
        }

        public static string ProbeDebug = "";

        static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o)) return o as Dictionary<string, object>;
            return null;
        }

        static bool Bool(Dictionary<string, object> d, string k)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return false;
            if (o is bool) return (bool)o;
            return string.Equals(o.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        static object[] AsArray(object o)
        {
            object[] arr = o as object[];
            if (arr != null) return arr;
            System.Collections.ArrayList al = o as System.Collections.ArrayList;
            if (al != null) return al.ToArray();
            return null;
        }

        static void ParseMedia(Dictionary<string, object> root, MediaInfo mi)
        {
            if (root == null) return;
            object o;
            if (!root.TryGetValue("TitleList", out o)) return;
            object[] list = AsArray(o);
            if (list == null || list.Length == 0) return;
            Dictionary<string, object> t = list[0] as Dictionary<string, object>;
            if (t == null) return;

            Dictionary<string, object> dur = GetDict(t, "Duration");
            if (dur != null) mi.durationSec = Num(dur, "Hours") * 3600 + Num(dur, "Minutes") * 60 + Num(dur, "Seconds");

            Dictionary<string, object> geo = GetDict(t, "Geometry");
            if (geo != null) { mi.width = (int)Num(geo, "Width"); mi.height = (int)Num(geo, "Height"); }

            Dictionary<string, object> fr = GetDict(t, "FrameRate");
            if (fr != null) { double den = Num(fr, "Den"); if (den > 0) mi.fps = Num(fr, "Num") / den; }

            object so;
            if (!t.TryGetValue("SubtitleList", out so)) return;
            object[] arr = AsArray(so);
            if (arr == null) return;
            foreach (object item in arr)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null) continue;
                SubInfo s = new SubInfo();
                s.track = (int)Num(d, "TrackNumber");
                s.langCode = Str(d, "LanguageCode");
                s.name = Str(d, "Language");
                s.format = Str(d, "Format");
                s.sourceName = Str(d, "SourceName");
                Dictionary<string, object> at = GetDict(d, "Attributes");
                if (at != null) { s.forced = Bool(at, "Forced"); s.isDefault = Bool(at, "Default"); }
                mi.subs.Add(s);
            }
        }

        public static bool IsChinese(SubInfo s)
        {
            string c = (s.langCode ?? "").ToLowerInvariant();
            if (c == "chi" || c == "zho" || c == "zh" || c == "cmn" || c == "yue" || c == "chs" || c == "cht") return true;
            string n = s.name ?? "";
            return n.IndexOf("中文", StringComparison.Ordinal) >= 0
                || n.IndexOf("简体", StringComparison.Ordinal) >= 0
                || n.IndexOf("繁体", StringComparison.Ordinal) >= 0
                || n.IndexOf("中字", StringComparison.Ordinal) >= 0
                || n.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Mandarin", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Cantonese", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------ subtitle conversion
        // HandBrake can only read external subtitles as SRT (--srt-file), so ASS/SSA and
        // non-UTF8 SRT files are converted to plain UTF-8 SRT before the job starts.

        public static string ReadTextSmart(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(b);
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(b); }
                catch { return Encoding.UTF8.GetString(b); }
            }
        }

        static string CleanAssText(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '{')
                {
                    int close = text.IndexOf('}', i);
                    if (close < 0) break;
                    i = close + 1;
                    continue;
                }
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    if (n == 'N' || n == 'n') { sb.Append('\n'); i += 2; continue; }
                    if (n == 'h') { sb.Append(' '); i += 2; continue; }
                    sb.Append(c);
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString().Replace("\r", "").Trim();
        }

        static double ParseAssTime(string s)
        {
            // H:MM:SS.cc
            try
            {
                string[] parts = s.Trim().Split(':');
                if (parts.Length != 3) return -1;
                double h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                double sec = double.Parse(parts[2], CultureInfo.InvariantCulture);
                return h * 3600 + m * 60 + sec;
            }
            catch { return -1; }
        }

        static string SrtTime(double t)
        {
            int ms = (int)Math.Round(t * 1000);
            int h = ms / 3600000;
            int m = (ms % 3600000) / 60000;
            int s = (ms % 60000) / 1000;
            int r = ms % 1000;
            return string.Format("{0:00}:{1:00}:{2:00},{3:000}", h, m, s, r);
        }

        public static string EnsureSrt(string subPath)
        {
            try
            {
                string ext = Path.GetExtension(subPath).ToLowerInvariant();
                string text = ReadTextSmart(subPath);
                bool isAss = (ext == ".ass" || ext == ".ssa");
                string outPath = Path.Combine(Path.GetDirectoryName(subPath),
                    Path.GetFileNameWithoutExtension(subPath) + (isAss ? ".converted.srt" : ".utf8.srt"));

                if (!isAss)
                {
                    if (!text.Contains("\uFFFD") && !IsAnsiEncoded(subPath)) return subPath;
                    File.WriteAllText(outPath, text, new UTF8Encoding(false));
                    return outPath;
                }

                List<double> starts = new List<double>();
                List<double> ends = new List<double>();
                List<string> bodies = new List<string>();
                string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                bool inEvents = false;
                int iStart = -1, iEnd = -1, iText = -1;

                foreach (string raw in lines)
                {
                    string line = raw.TrimEnd();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                        iStart = iEnd = iText = -1;
                        continue;
                    }
                    if (!inEvents) continue;
                    if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] f = line.Substring(7).Split(',');
                        for (int i = 0; i < f.Length; i++)
                        {
                            string k = f[i].Trim().ToLowerInvariant();
                            if (k == "start") iStart = i;
                            else if (k == "end") iEnd = i;
                            else if (k == "text") iText = i;
                        }
                        continue;
                    }
                    if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (iStart < 0 || iEnd < 0 || iText < 0) continue;

                    string rest = line.Substring(9);
                    string[] parts = rest.Split(new char[] { ',' }, iText + 1);
                    if (parts.Length <= iText) continue;

                    double st = ParseAssTime(parts[iStart]);
                    double en = ParseAssTime(parts[iEnd]);
                    if (st < 0 || en <= st) continue;
                    string body = CleanAssText(parts[iText]);
                    if (body.Length == 0) continue;

                    int found = -1;
                    for (int k = 0; k < starts.Count; k++)
                        if (Math.Abs(starts[k] - st) < 0.02 && Math.Abs(ends[k] - en) < 0.02) { found = k; break; }
                    if (found >= 0) bodies[found] = bodies[found] + "\n" + body;
                    else { starts.Add(st); ends.Add(en); bodies.Add(body); }
                }

                if (starts.Count == 0) return "";

                List<int> order = new List<int>();
                for (int k = 0; k < starts.Count; k++) order.Add(k);
                order.Sort(delegate(int a, int b) { return starts[a].CompareTo(starts[b]); });

                StringBuilder sb = new StringBuilder();
                int idx = 1;
                foreach (int k in order)
                {
                    sb.Append(idx++).Append("\r\n");
                    sb.Append(SrtTime(starts[k])).Append(" --> ").Append(SrtTime(ends[k])).Append("\r\n");
                    sb.Append(bodies[k]).Append("\r\n\r\n");
                }
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                return outPath;
            }
            catch { return ""; }
        }

        static bool IsAnsiEncoded(string path)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                UTF8Encoding strict = new UTF8Encoding(false, true);
                strict.GetString(b);
                return false;
            }
            catch { return true; }
        }

        public static string FindExternalSub(string videoPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(videoPath);
                string baseName = Path.GetFileNameWithoutExtension(videoPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
                string[] exts = new string[] { ".srt", ".ass", ".ssa" };
                foreach (string f in Directory.GetFiles(dir))
                {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    if (!string.Equals(fn, baseName, StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    foreach (string e in exts) if (ext == e) return f;
                }
            }
            catch { }
            return "";
        }

        static void Head(Stream s, int code, string reason, string ctype, long len, bool download, string filename, string extra)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: transcode-lan\r\n");
            if (ctype != null) sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
            sb.Append("Content-Length: ").Append(len).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Range\r\n");
            if (download && filename != null)
            {
                string ascii = filename;
                StringBuilder a = new StringBuilder();
                foreach (char c in ascii) a.Append(c < 128 ? c : '_');
                sb.Append("Content-Disposition: attachment; filename=\"").Append(a).Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(filename)).Append("\r\n");
            }
            if (extra != null) sb.Append(extra);
            sb.Append("Connection: close\r\n\r\n");
            byte[] b = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(b, 0, b.Length);
        }

        public static void SendText(Stream s, int code, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text ?? "");
            Head(s, code, CodeText(code), "text/plain; charset=utf-8", b.Length, false, null, null);
            s.Write(b, 0, b.Length);
        }

        public static void SendEmpty(Stream s, int code)
        {
            Head(s, code, CodeText(code), null, 0, false, null, null);
        }

        public static void SendJson(Stream s, int code, object o)
        {
            string json = Program.Json.Serialize(o);
            byte[] b = Encoding.UTF8.GetBytes(json);
            Head(s, code, CodeText(code), "application/json; charset=utf-8", b.Length, false, null, "Cache-Control: no-store\r\n");
            s.Write(b, 0, b.Length);
        }

        public static void SendFile(Stream s, string path, string ctype, bool download, string range, string filename)
        {
            FileInfo fi = new FileInfo(path);
            long total = fi.Length;
            long start = 0, stop = total - 1;
            bool partial = false;

            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string spec = range.Substring(6).Split(',')[0].Trim();
                int dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    string a = spec.Substring(0, dash);
                    string b = spec.Substring(dash + 1);
                    long v;
                    if (a.Length > 0 && long.TryParse(a, out v)) { start = v; if (b.Length > 0 && long.TryParse(b, out v)) stop = Math.Min(v, total - 1); }
                    else if (b.Length > 0 && long.TryParse(b, out v)) { start = Math.Max(0, total - v); }
                    partial = true;
                }
            }
            if (start < 0) start = 0;
            if (stop >= total) stop = total - 1;
            if (start > stop || start >= total)
            {
                Head(s, 416, "Range Not Satisfiable", "text/plain", 0, false, null, "Content-Range: bytes */" + total + "\r\n");
                return;
            }

            long len = stop - start + 1;
            string extra = null;
            if (partial)
            {
                extra = "Content-Range: bytes " + start + "-" + stop + "/" + total + "\r\n";
                StringBuilder sb = new StringBuilder();
                sb.Append("HTTP/1.1 206 Partial Content\r\n");
                sb.Append("Server: transcode-lan\r\n");
                sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
                sb.Append("Content-Length: ").Append(len).Append("\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type, Range\r\n");
                sb.Append("Access-Control-Expose-Headers: Content-Range, Content-Length\r\n");
                sb.Append(extra);
                if (download) sb.Append("Content-Disposition: attachment; filename=\"").Append(Path.GetFileName(filename)).Append("\"\r\n");
                sb.Append("Connection: close\r\n\r\n");
                byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
                s.Write(hb, 0, hb.Length);
            }
            else
            {
                string ascii = "";
                try { ascii = Path.GetFileName(filename); } catch { }
                StringBuilder sb = new StringBuilder();
                sb.Append("HTTP/1.1 200 OK\r\n");
                sb.Append("Server: transcode-lan\r\n");
                sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
                sb.Append("Content-Length: ").Append(total).Append("\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Expose-Headers: Content-Range, Content-Length\r\n");
                if (download && ascii != null)
                {
                    StringBuilder a = new StringBuilder();
                    foreach (char c in ascii) a.Append(c < 128 ? c : '_');
                    sb.Append("Content-Disposition: attachment; filename=\"").Append(a).Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(ascii)).Append("\r\n");
                }
                sb.Append("Cache-Control: no-store\r\n");
                sb.Append("Connection: close\r\n\r\n");
                byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
                s.Write(hb, 0, hb.Length);
            }

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 262144, FileOptions.SequentialScan))
            {
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[262144];
                long remain = len;
                while (remain > 0)
                {
                    int want = (int)Math.Min(buf.Length, remain);
                    int n = fs.Read(buf, 0, want);
                    if (n <= 0) break;
                    s.Write(buf, 0, n);
                    remain -= n;
                }
            }
        }

        static string CodeText(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 416: return "Range Not Satisfiable";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }
    }
}
