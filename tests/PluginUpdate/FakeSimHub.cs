// Stand-in for SimHubWPF.exe, compiled together with the real PluginUpdateHelper.cs from the PR, so
// Assembly.GetExecutingAssembly() points at this exe and the helper treats its folder as the SimHub folder.
//   SimHubWPF.exe                                    -> started again by update.cmd: writes restarted.txt
//   SimHubWPF.exe update DLL_URL RESX_URL|- [EXIT]   -> download + install, like the plugin buttons
//                                                       EXIT = ms to wait while closing, or "hang"
//   SimHubWPF.exe double DLL_URL                     -> two downloads at once, the second must be refused
using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DiyFfbPedal.UIFunction;

static class FakeSimHub
{
    static string dir = Path.GetDirectoryName(typeof(FakeSimHub).Assembly.Location);

    static void Log(string s)
    {
        File.AppendAllText(Path.Combine(dir, "result.txt"), s + Environment.NewLine);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            File.WriteAllText(Path.Combine(dir, "restarted.txt"), Environment.CurrentDirectory + Environment.NewLine + (admin ? "admin" : "user"));
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try
            {
                if (args[0] == "double")
                {
                    Task first = PluginUpdateHelper.DownloadAsync(args[1], null);
                    try
                    {
                        await PluginUpdateHelper.DownloadAsync(args[1], null);
                        Log("second download accepted");
                    }
                    catch (InvalidOperationException ex)
                    {
                        Log("second download refused: " + ex.Message);
                    }
                    await first;
                    Log("first download ok");
                    app.Shutdown();
                    return;
                }
                await PluginUpdateHelper.DownloadAsync(args[1], args[2] == "-" ? null : args[2]);
                Log("downloaded");
                PluginUpdateHelper.InstallAndRestart();
                Log("install started");
            }
            catch (Exception ex)
            {
                Log("error: " + ex.GetType().Name + ": " + ex.Message);
                app.Shutdown();
            }
        };
        app.Exit += (s, e) =>
        {
            if (args.Length > 3)
            {
                Thread.Sleep(args[3] == "hang" ? Timeout.Infinite : int.Parse(args[3]));
            }
        };
        app.Run();
        return 0;
    }
}
