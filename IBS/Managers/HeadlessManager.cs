using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using IBS.Common;
using IBS.Helpers;

using Newtonsoft.Json.Linq;

using ResoniteLink.RPath;

using Spectre.Console;

namespace IBS.Managers;

public static class HeadlessManager
{
    private static readonly JObject headless_config;
    private static Process? running_process = null;

    static HeadlessManager()
    {
        var login_credential = ConfigManager.GetOrInput(key: "login_credential", prompt: "headless account email");
        var login_password = ConfigManager.GetOrInput(key: "login_password", prompt: "headless account password");
        var tester_username = ConfigManager.GetOrInput(key: "tester_username", prompt: "tester username");
        var world_res_rec = ConfigManager.GetOrInput(key: "world_res_rec", prompt: "world resspec");

        headless_config = new JObject
        {
            ["logsFolder"] = Path.Combine(Utils.ResoniteRootFolderFullPath, @"Logs"),
            ["dataFolder"] = Path.Combine(Utils.ResoniteRootFolderFullPath, @"Data"),
            ["cacheFolder"] = Path.Combine(Utils.ResoniteRootFolderFullPath, @"Cache"),
            ["loginCredential"] = login_credential,
            ["loginPassword"] = login_password,
            ["loginRequired"] = true,
            ["startWorlds"] = new JArray
            {
                new JObject
                {
                    ["accessLevel"] = "Private",
                    ["loadWorldURL"] = world_res_rec,
                    ["enableResoniteLink"] = true,
                    ["forceResoniteLinkPort"] = Constants.ResoLinkPort,
                    ["autoSleep"] = false,
                    ["waitForLogin"] = true,
                    ["autoInviteUsernames"] = new JArray{ tester_username },
                },
            },
        };

    }

    public static void KillOldInstances()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Constants.HeadlessFullPath)).Where(p => p.MainModule?.FileName == Constants.HeadlessFullPath).ToArray();
        if (processes.Length is 0)
            return;
        lock (Utils.OutLock)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Found {processes.Length} old headless processes[/]");
            if (!AnsiConsole.Confirm("[red]Kill them?[/]"))
                return;
        }
        foreach (var p in processes)
            p.Kill(entireProcessTree: true);
    }

    public static Process EnsureRunning()
    {
        if (running_process is { } && !running_process.HasExited)
            return running_process;
        running_process = Run();
        return running_process;
    }

    public static async Task EnsureRestarted(Int32 expected_n_children)
    {
        EnsureRunning().StandardInput.WriteLine($"restart");
        while (true)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                using var link = await ResoLinkManager.Connect(req_cancel_token: cts.Token);
                var t_root = Query.Root.Single(link);
                while (!t_root.IsCompleted && link.IsConnected)
                    await Task.Delay(1);
                if (!link.IsConnected)
                    continue;
                var root = await t_root;
                if (root.Children.Count == expected_n_children)
                    break;
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Waiting for headless to restart, expected {expected_n_children} children but got {root.Children.Count}[/]");
            }
            catch (Exception ex)
            {
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[red]Exception while waiting for headless to restart: {ex}[/]");
            }
            await Task.Delay(300);
        }
    }

    public static void KillIfRunning()
    {
        if (running_process is { } && !running_process.HasExited)
        {
            running_process.Kill(entireProcessTree: true);
            running_process.WaitForExit();
        }
    }

    private static Process Run()
    {
        AnsiConsole.MarkupLineInterpolated($"[green]Headless config:[/]");
        var headless_config_str = headless_config.WriteToString();
        AnsiConsole.MarkupLineInterpolated($"[aqua]{headless_config_str}[/]");
        File.WriteAllText(Constants.HeadlessConfigPath, headless_config_str);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Utils.ResoniteRootFolderFullPath,
            FileName = Constants.HeadlessFullPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-HeadlessConfig");
        psi.ArgumentList.Add(Path.GetFullPath(Constants.HeadlessConfigPath));

        var p = new Process
        {
            StartInfo = psi,
        };

        p.Start();

        lock (Utils.OutLock)
            AnsiConsole.MarkupLineInterpolated($"[aqua]Starting headless with arguments: {String.Join(" ", p.StartInfo.ArgumentList)}[/]");

        void BeginReadingStream(StreamReader stream, String prefix)
        {
            Err.TaskWithHandling(async () =>
            {
                while (true)
                {
                    var l = stream.ReadLine();
                    if (l is null)
                        break;
                    //lock (Utils.OutLock)
                    //    AnsiConsole.MarkupLineInterpolated($"[grey]{DateTime.Now:HH:mm:ss.fffff} Headless>{prefix}> {l}[/]");
                }
            });
        }
        BeginReadingStream(p.StandardOutput, "OUT");
        BeginReadingStream(p.StandardError, "ERR");

        Err.TaskWithHandling(async () =>
        {
            await p.WaitForExitAsync();
            if (p.ExitCode is 0)
                return;
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[red]Headless process exited with code {p.ExitCode}[/]");
            Console.ReadLine();
            Environment.Exit(-1);
        });

        //while (true)
        //{
        //    var l = Console.ReadLine() ?? throw null!;
        //    p.StandardInput.WriteLine(l);
        //}

        return p;
    }

}
