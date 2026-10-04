// Integration tests for PluginUpdateHelper (DIY FFB Pedal plugin update without PowerShell).
// Serves the files over a local HTTP server, runs FakeSimHub (= the real helper) in folders whose names have
// spaces, parentheses, "&" and accents, and checks what update.cmd left on disk.
// Usage: UpdateTests.exe BUILD_DIR WORK_DIR [scenario ...]
//   BUILD_DIR has SimHubWPF.exe (FakeSimHub), old\DiyFfbPedal.dll and new\DiyFfbPedal.dll
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class UpdateTests
{
    static string buildDir, workDir;
    static byte[] oldDll, newDll, hostExe;
    static readonly byte[] oldResx = Encoding.UTF8.GetBytes("<root>old</root>");
    static readonly byte[] newResx = Encoding.UTF8.GetBytes("<root>new</root>");
    static readonly Dictionary<string, byte[]> served = new Dictionary<string, byte[]>();
    static string baseUrl;

    // one scenario's folders and helpers
    class Case
    {
        public string Name, SimHub, Temp, UpdDir;
        public List<string> Failures = new List<string>();
        public string P(string f) { return Path.Combine(SimHub, f); }
        public byte[] Read(string f) { return File.Exists(P(f)) ? File.ReadAllBytes(P(f)) : null; }
        public string Result { get { return File.Exists(P("result.txt")) ? File.ReadAllText(P("result.txt")).Trim() : ""; } }
        public void Check(bool ok, string what) { if (!ok) Failures.Add(what); }
    }

    static int Main(string[] args)
    {
        buildDir = Path.GetFullPath(args[0]);
        workDir = Path.GetFullPath(args[1]);
        var only = new HashSet<string>(args.Skip(2));
        oldDll = File.ReadAllBytes(Path.Combine(buildDir, "old", "DiyFfbPedal.dll"));
        newDll = File.ReadAllBytes(Path.Combine(buildDir, "new", "DiyFfbPedal.dll"));
        hostExe = File.ReadAllBytes(Path.Combine(buildDir, "SimHubWPF.exe"));
        served["/new.dll"] = newDll;
        served["/new.resx"] = newResx;
        served["/junk.dll"] = Encoding.UTF8.GetBytes("<html><body>Rate limit exceeded</body></html>");
        served["/truncated.dll"] = newDll.Take(newDll.Length / 2).ToArray();
        served["/other.dll"] = hostExe; // a valid .NET assembly, but not DiyFfbPedal
        StartServer();
        if (Directory.Exists(workDir))
        {
            foreach (string f in Directory.GetFiles(workDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(workDir, true);
        }

        var scenarios = new List<KeyValuePair<string, Action<Case>>>
        {
            S("full", c => Success(c, Run(c, "new.dll", "new.resx", "3000"), newResx, 30)),
            S("rollback-no-resx", c => Success(c, Run(c, "new.dll", "-", "0"), oldResx, 30)),
            S("resx-404", c => Success(c, Run(c, "new.dll", "missing.resx", "0"), oldResx, 30)),
            S("dll-404", c => { Stale(c); DownloadFails(c, Run(c, "missing.dll", "new.resx", "0"), "HttpRequestException"); }),
            S("dll-junk", c => DownloadFails(c, Run(c, "junk.dll", "new.resx", "0"), "InvalidDataException")),
            S("dll-truncated", c => DownloadFails(c, Run(c, "truncated.dll", "new.resx", "0"), "InvalidDataException")),
            S("dll-other-assembly", c => DownloadFails(c, Run(c, "other.dll", "new.resx", "0"), "InvalidDataException")),
            S("plugin-readonly", c => { File.SetAttributes(c.P("DiyFfbPedal.dll"), FileAttributes.ReadOnly); CopyFails(c, Run(c, "new.dll", "new.resx", "0")); }),
            S("backup-readonly", c => { File.WriteAllBytes(c.P("DiyFfbPedal.dll.bak"), oldResx); File.SetAttributes(c.P("DiyFfbPedal.dll.bak"), FileAttributes.ReadOnly); CopyFails(c, Run(c, "new.dll", "new.resx", "0")); }),
            S("simhub-hangs", c => Success(c, Run(c, "new.dll", "new.resx", "hang"), newResx, 120)),
            S("double-click", c => Double(c)),
            S("no-simhub-exe", c => NoSimHubExe(c)),
        };

        int failed = 0;
        foreach (var s in scenarios.Where(x => only.Count == 0 || only.Contains(x.Key)))
        {
            var c = NewCase(s.Key);
            var sw = Stopwatch.StartNew();
            try { s.Value(c); }
            catch (Exception ex) { c.Failures.Add("exception: " + ex); }
            KillLeftovers(c);
            Console.WriteLine("{0} {1} ({2:0.0} s){3}", c.Failures.Count == 0 ? "PASS" : "FAIL", c.Name, sw.Elapsed.TotalSeconds,
                              c.Failures.Count == 0 ? "" : Environment.NewLine + "     - " + string.Join(Environment.NewLine + "     - ", c.Failures)
                                                         + Environment.NewLine + "     result.txt: " + c.Result.Replace(Environment.NewLine, " | "));
            if (c.Failures.Count > 0) failed++;
        }
        Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        return failed;
    }

    static KeyValuePair<string, Action<Case>> S(string n, Action<Case> a) { return new KeyValuePair<string, Action<Case>>(n, a); }

    static Case NewCase(string name)
    {
        var c = new Case { Name = name };
        c.SimHub = Path.Combine(workDir, name, "Sim Hub (x86) Ünï & Co");
        c.Temp = Path.Combine(workDir, name, "Temp dír & (x)");
        c.UpdDir = Path.Combine(c.Temp, "DiyFfbPedal_update");
        Directory.CreateDirectory(c.SimHub);
        Directory.CreateDirectory(c.Temp);
        File.WriteAllBytes(c.P("SimHubWPF.exe"), hostExe);
        File.WriteAllBytes(c.P("DiyFfbPedal.dll"), oldDll);
        Directory.CreateDirectory(c.P("Languages")); // SimHub's folder is "Languages", the script writes "languages"
        File.WriteAllBytes(c.P(@"Languages\DiyFfbPedal.resx"), oldResx);
        return c;
    }

    static Process Run(Case c, string dll, string resx, string exit, string exe = "SimHubWPF.exe")
    {
        var psi = new ProcessStartInfo(c.P(exe)) { UseShellExecute = false, WorkingDirectory = c.SimHub };
        psi.Arguments = string.Format("update \"{0}/{1}\" \"{2}\" {3}", baseUrl, dll, resx == "-" ? "-" : baseUrl + "/" + resx, exit);
        psi.EnvironmentVariables["TEMP"] = c.Temp;
        psi.EnvironmentVariables["TMP"] = c.Temp;
        return Process.Start(psi);
    }

    static bool WaitFor(Func<bool> cond, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { if (cond()) return true; Thread.Sleep(250); }
        return cond();
    }

    static bool Same(byte[] a, byte[] b) { return a != null && b != null && a.SequenceEqual(b); }

    static void Success(Case c, Process host, byte[] expectedResx, int timeout)
    {
        c.Check(WaitFor(() => File.Exists(c.P("restarted.txt")), timeout), "SimHub was not started again within " + timeout + " s");
        Thread.Sleep(1000);
        c.Check(host.HasExited, "the original SimHub process is still running");
        c.Check(Same(c.Read("DiyFfbPedal.dll"), newDll), "plugin was not replaced by the new DLL");
        c.Check(Same(c.Read("DiyFfbPedal.dll.bak"), oldDll), "backup is not the old DLL");
        c.Check(Same(c.Read(@"Languages\DiyFfbPedal.resx"), expectedResx), "resx is not the expected one");
        c.Check(!File.Exists(Path.Combine(c.UpdDir, "DiyFfbPedal.dll")), "downloaded DLL was not cleaned up");
        if (File.Exists(c.P("restarted.txt")))
        {
            string[] r = File.ReadAllLines(c.P("restarted.txt"));
            c.Check(string.Equals(r[0].TrimEnd('\\'), c.SimHub, StringComparison.OrdinalIgnoreCase), "restarted with working directory " + r[0]);
            Console.WriteLine("     restarted as " + r[1] + ", working directory ok: " + string.Equals(r[0].TrimEnd('\\'), c.SimHub, StringComparison.OrdinalIgnoreCase));
        }
        c.Check(c.Result.Contains("install started"), "host did not report the install start");
    }

    // a download that fails must leave everything as it was and must not close SimHub through the script
    static void DownloadFails(Case c, Process host, string error)
    {
        c.Check(host.WaitForExit(30000), "host did not exit after the error");
        Thread.Sleep(3000);
        c.Check(c.Result.Contains("error: " + error), "expected " + error);
        c.Check(!c.Result.Contains("install started"), "install was started");
        c.Check(Same(c.Read("DiyFfbPedal.dll"), oldDll), "plugin was changed");
        c.Check(!File.Exists(c.P("DiyFfbPedal.dll.bak")), "a backup was made");
        c.Check(Same(c.Read(@"Languages\DiyFfbPedal.resx"), oldResx), "resx was changed");
        c.Check(!File.Exists(Path.Combine(c.UpdDir, "DiyFfbPedal.dll")), "a DLL was left ready to install");
        c.Check(!File.Exists(c.P("restarted.txt")), "SimHub was restarted");
    }

    // files left by an older attempt must never be installed
    static void Stale(Case c)
    {
        Directory.CreateDirectory(c.UpdDir);
        File.WriteAllBytes(Path.Combine(c.UpdDir, "DiyFfbPedal.dll"), newDll);
        File.WriteAllBytes(Path.Combine(c.UpdDir, "DiyFfbPedal.resx"), newResx);
    }

    // the copy fails: the old plugin stays, the error stays on screen for a while, then SimHub starts again
    static void CopyFails(Case c, Process host)
    {
        c.Check(host.WaitForExit(30000), "host did not exit");
        var sw = Stopwatch.StartNew();
        c.Check(WaitFor(() => File.Exists(c.P("restarted.txt")), 60), "SimHub was not started again after the error");
        Console.WriteLine("     restarted {0:0.0} s after SimHub closed", sw.Elapsed.TotalSeconds);
        Thread.Sleep(1000);
        c.Check(Same(c.Read("DiyFfbPedal.dll"), oldDll), "plugin is not the old DLL");
        c.Check(!c.Result.Contains("error"), "host reported an error");
    }

    static void Double(Case c)
    {
        var psi = new ProcessStartInfo(c.P("SimHubWPF.exe"), "double \"" + baseUrl + "/new.dll\"") { UseShellExecute = false };
        psi.EnvironmentVariables["TEMP"] = c.Temp;
        psi.EnvironmentVariables["TMP"] = c.Temp;
        var host = Process.Start(psi);
        c.Check(host.WaitForExit(30000), "host did not exit");
        c.Check(c.Result.Contains("second download refused"), "second download was not refused");
        c.Check(c.Result.Contains("first download ok"), "first download did not finish");
        c.Check(Same(c.Read("DiyFfbPedal.dll"), oldDll), "plugin was changed");
    }

    // the plugin is not next to SimHubWPF.exe: refuse before closing SimHub
    static void NoSimHubExe(Case c)
    {
        File.Move(c.P("SimHubWPF.exe"), c.P("OtherHost.exe"));
        var host = Run(c, "new.dll", "new.resx", "0", "OtherHost.exe");
        c.Check(host.WaitForExit(30000), "host did not exit");
        Thread.Sleep(3000);
        c.Check(c.Result.Contains("error: FileNotFoundException"), "expected FileNotFoundException");
        c.Check(Same(c.Read("DiyFfbPedal.dll"), oldDll), "plugin was changed");
        c.Check(!File.Exists(Path.Combine(c.UpdDir, "update.cmd")), "update.cmd was written");
        c.Check(!File.Exists(c.P("restarted.txt")), "SimHub was restarted");
    }

    static void KillLeftovers(Case c)
    {
        foreach (var p in Process.GetProcessesByName("SimHubWPF")) try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
        foreach (var p in Process.GetProcessesByName("OtherHost")) try { p.Kill(); } catch (Exception) { }
    }

    // minimal HTTP/1.1 server: GET of the files in "served", 404 for anything else
    static void StartServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        baseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(() =>
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                Task.Run(() =>
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var head = new StringBuilder();
                        while (!head.ToString().EndsWith("\r\n\r\n")) { int b = stream.ReadByte(); if (b < 0) return; head.Append((char)b); }
                        string path = head.ToString().Split(' ')[1];
                        byte[] body;
                        bool found = served.TryGetValue(path, out body);
                        if (!found) body = Encoding.ASCII.GetBytes("not found");
                        byte[] h = Encoding.ASCII.GetBytes(string.Format("HTTP/1.1 {0}\r\nContent-Length: {1}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n",
                                                                         found ? "200 OK" : "404 Not Found", body.Length));
                        stream.Write(h, 0, h.Length);
                        stream.Write(body, 0, body.Length);
                    }
                });
            }
        }) { IsBackground = true }.Start();
    }
}
