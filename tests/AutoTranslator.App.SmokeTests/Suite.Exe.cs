using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using AutoTranslator.Core;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    private const int BridgePort = 47613;
    private const string GoodOrigin = "chrome-extension://smoketest";

    // ------------------------------------------------------------------ 6 the shipped executable

    private async Task CheckExecutableAsync()
    {
        const string Name = "6 exe";
        if (_exePath == null || !File.Exists(_exePath))
        {
            Report.Skip(Name, "no published AutoTranslator.exe given (--exe); path: " + (_exePath ?? "(none)"));
            return;
        }

        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            && Environment.GetEnvironmentVariable("SMOKE_RESET_APPDATA") != "1")
        {
            Report.Skip(Name, "this check deletes %AppData%\\AutoTranslator; it only runs on CI (or with SMOKE_RESET_APPDATA=1)");
            return;
        }

        Report.Info("exe: " + _exePath + " (" + Report.F(new FileInfo(_exePath).Length / (1024.0 * 1024.0)) + " MB)");

        // A: first start ever, default settings (OCR mode, help window opens).
        await RunExeScenarioAsync(Name + " A (first run, OCR)", null, true);

        // B: UI Automation mode from an existing settings file (no help window, no OCR pack needed).
        await RunExeScenarioAsync(Name + " B (settings: UI Automation)", "{ \"TextSourceMode\": \"UiAutomation\" }", false);
    }

    /// <summary>
    /// Bridge protocol 2 (docs/ARCHITECTURE.md): a version 2 client is told the app state right
    /// after its hello; without an API key the app offers no premium engine and refuses
    /// page-translate. A version 1 client never sees the new messages.
    /// </summary>
    private static async Task CheckProtocolVersionAsync(string name, FakeExtension extension, int helloVersion)
    {
        Func<string, List<string>> ofType = type => extension.OtherMessages()
            .Where(m => m.Replace(" ", string.Empty).Contains("\"type\":\"" + type + "\"")).ToList();

        if (helloVersion < 2)
        {
            await Task.Delay(1500);
            List<string> states = ofType("app-state");
            Report.Check(states.Count == 0, name + ": protocol 1 client gets no app-state message",
                "messages other than translate/ping within 1.5 s after hello: "
                + (extension.OtherMessages().Count == 0 ? "none" : string.Join(" ", extension.OtherMessages())));
            return;
        }

        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(5) && ofType("app-state").Count == 0)
        {
            await Task.Delay(50);
        }

        List<string> appStates = ofType("app-state");
        string state = appStates.Count > 0 ? appStates[0].Replace(" ", string.Empty) : string.Empty;
        Report.Check(appStates.Count == 1 && state.Contains("\"premium\":false") && state.Contains("\"engine\":\"chrome\""),
            name + ": protocol 2 client gets app-state after hello",
            appStates.Count == 0 ? "no app-state message within 5 s"
                : appStates.Count + " message(s) " + Report.F(wait.Elapsed.TotalMilliseconds) + " ms after hello: " + string.Join(" ", appStates)
                  + " (expected premium false, engine chrome: no API key is configured)");

        await extension.SendJsonAsync("{\"type\":\"page-translate\",\"id\":\"p1\",\"source\":\"zh\",\"target\":\"en\",\"texts\":[\"你好\"]}");
        wait.Restart();
        while (wait.Elapsed < TimeSpan.FromSeconds(5) && ofType("page-error").Count + ofType("page-result").Count == 0)
        {
            await Task.Delay(50);
        }

        List<string> errors = ofType("page-error");
        Report.Check(errors.Count == 1 && errors[0].Replace(" ", string.Empty).Contains("\"id\":\"p1\"") && ofType("page-result").Count == 0,
            name + ": page-translate without premium engine is answered with page-error",
            errors.Count == 0 ? "no page-error within 5 s; page-result messages: " + ofType("page-result").Count
                : string.Join(" ", errors));
    }

    private async Task RunExeScenarioAsync(string name, string? settingsJson, bool ocrMode)
    {
        int helloVersion = settingsJson == null ? 1 : 2;
        Report.Info("--- " + name);
        TextWindow text = _text!;
        KillStrayApps();

        string directory = AppSettings.DefaultDirectory;
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex)
        {
            Report.Info("could not reset " + directory + ": " + ex.Message);
        }

        if (settingsJson != null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), settingsJson);
        }

        // The exe has no test seams: OCR mode needs real Chinese, UI Automation mode always works with it.
        TextLayout layout = await text.SetAsync(BuildScene(ChineseRows, ChineseColumns));
        if (!await EnsureForegroundAsync(name))
        {
            return;
        }

        await Task.Delay(300);
        Shot baseline = Shots.Grab(layout.Client);

        Stopwatch startup = Stopwatch.StartNew();
        Process app = Process.Start(new ProcessStartInfo(_exePath!) { UseShellExecute = false })!;
        FakeExtension? extension = null;
        try
        {
            // ---- start-up
            bool listening = false;
            while (startup.Elapsed < TimeSpan.FromSeconds(40) && !app.HasExited)
            {
                if (await PortOpenAsync(BridgePort))
                {
                    listening = true;
                    break;
                }

                await Task.Delay(150);
            }

            if (!Report.Check(listening && !app.HasExited, name + ": starts and listens on 127.0.0.1:" + BridgePort,
                    app.HasExited
                        ? "the process exited with code " + app.ExitCode + " after " + Report.F(startup.Elapsed.TotalSeconds) + " s"
                        : listening ? "bridge reachable " + Report.F(startup.Elapsed.TotalSeconds) + " s after starting the exe"
                                    : "bridge not reachable within 40 s"))
            {
                return;
            }

            await Task.Delay(1500); // let the first-run help window appear

            // ---- first-run behaviour
            IntPtr foreground = Native.GetForegroundWindow();
            bool stolen = Native.ProcessIdOf(foreground) == (uint)app.Id;
            Report.Info("windows of the app: " + string.Join("; ", Native.TopLevelWindowsOf(app.Id)
                .Where(Native.IsWindowVisible).Select(Native.Describe)));
            Report.Info("foreground window after start: " + Native.Describe(foreground)
                        + (stolen ? "  <-- the app's own window took the foreground" : string.Empty));
            if (settingsJson == null)
            {
                Report.Info("FINDING: on the first run the help window " + (stolen ? "takes" : "does not take")
                            + " the foreground. While an AutoTranslator window is in front, nothing is read; "
                            + "translation starts when the user switches to another window.");
            }
            else
            {
                Report.Check(!stolen, name + ": does not take the foreground on a normal start",
                    "foreground after start: " + Native.Describe(foreground));
            }

            // ---- origin check
            string wrongOrigin = await TryConnectAsync("https://evil.example");
            string noOrigin = await TryConnectAsync(null);
            Report.Check(wrongOrigin != "accepted" && noOrigin != "accepted", name + ": bridge rejects other origins",
                "Origin https://evil.example: " + wrongOrigin + "; no Origin header: " + noOrigin);

            try
            {
                extension = await FakeExtension.ConnectAsync(BridgePort, GoodOrigin);
                await extension.SendHelloAsync(helloVersion);
                Report.Pass(name + ": bridge accepts " + GoodOrigin, "WebSocket open, hello (protocol version " + helloVersion + ") sent");
            }
            catch (Exception ex)
            {
                Report.Fail(name + ": bridge accepts " + GoodOrigin, ex.GetType().Name + ": " + ex.Message);
                return;
            }

            await CheckProtocolVersionAsync(name, extension, helloVersion);

            // The user switches (back) to the program with the Chinese text.
            if (!await EnsureForegroundAsync(name))
            {
                return;
            }

            DateTime activatedUtc = DateTime.UtcNow;

            // ---- capture -> OCR / UIA -> bridge
            string requestName = name + ": exe sends the on-screen Chinese text to the extension";
            if (ocrMode && !_chineseOcr)
            {
                await Task.Delay(4000);
                Report.Skip(requestName, "OCR mode needs the Chinese OCR pack, which could not be installed on this runner; "
                    + "requests received in 4 s: " + extension.Requests().Count + " (expected 0)");
                Report.Check(!app.HasExited, name + ": keeps running without the OCR pack",
                    "process alive: " + !app.HasExited);
            }
            else
            {
                string[] expected = ChineseRows.Concat(ChineseColumns).ToArray();
                BridgeRequest? hit = null;
                int matched = 0;
                Stopwatch wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(15))
                {
                    List<string> all = extension.Requests().SelectMany(r => r.Texts).ToList();
                    matched = expected.Count(e => all.Any(t => ocrMode ? Accuracy(e, t) >= 0.8 : t == e));
                    if (matched == expected.Length)
                    {
                        hit = extension.Requests().Last();
                        break;
                    }

                    await Task.Delay(100);
                }

                foreach (BridgeRequest request in extension.Requests())
                {
                    Report.Info("  translate request " + request.Id + ": " + string.Join(" | ", request.Texts));
                }

                Report.Check(hit != null, requestName,
                    matched + "/" + expected.Length + " expected strings requested"
                    + (hit != null ? " " + Report.F((hit.Utc - activatedUtc).TotalMilliseconds)
                        + " ms after the text window came to the front" : " within 15 s"));

                // ---- overlay of the exe
                await Task.Delay(1200);
                IntPtr overlay = Native.TopLevelWindowsOf(app.Id).FirstOrDefault(h => Native.TitleOf(h) == "AutoTranslator Overlay");
                uint affinity;
                bool gotAffinity = Native.GetWindowDisplayAffinity(overlay, out affinity);
                Shot now = Shots.Grab(layout.Client);
                ShotDiff diff = Shots.Diff(baseline, now);
                Shots.Save(Shots.Grab(new ScreenRect(0, 0, Native.GetSystemMetrics(Native.SM_CXSCREEN), Native.GetSystemMetrics(Native.SM_CYSCREEN))),
                    "06-exe-" + (ocrMode ? "A" : "B") + "-screen-capture");
                Report.Check(overlay != IntPtr.Zero && Native.IsWindowVisible(overlay), name + ": exe shows its overlay window",
                    overlay == IntPtr.Zero ? "overlay window not found"
                        : "overlay " + Native.Describe(overlay) + " visible=" + Native.IsWindowVisible(overlay)
                          + ", display affinity " + (gotAffinity ? "0x" + affinity.ToString("X") : "not readable from another process"));
                Report.Check(diff.ChangedPixels == 0, name + ": exe overlay is invisible to screen capture",
                    "capture of the text window with the exe's overlay on top vs. before the start: " + diff);

                // ---- no feedback loop: the fake translations contain the Chinese original, so
                // an app that read its own overlay would ask for "EN[...]" texts.
                int requestsBefore = extension.Requests().Count;
                await Task.Delay(12000); // spans one forced 10 s refresh
                List<BridgeRequest> later = extension.Requests().Skip(requestsBefore).ToList();
                bool loop = extension.Requests().Any(r => r.Texts.Any(t => t.Contains("EN[") || t.Contains("EN【")));
                Report.Check(!loop && later.Count == 0, name + ": stable for 12 s (no feedback loop, no repeated requests)",
                    later.Count + " further request(s) while the screen was static"
                    + (later.Count > 0 ? ": " + string.Join(" || ", later.Select(r => string.Join(" | ", r.Texts))) : string.Empty)
                    + "; requests containing the app's own translations: " + (loop ? "YES" : "none"));

                // ---- changed text
                int before = extension.Requests().Count;
                TextLayout changed = await text.SetAsync(
                    new TextItem(40, 60, 44, ChineseChanged[0]), new TextItem(40, 220, 44, ChineseChanged[1]));
                BridgeRequest? update = null;
                wait.Restart();
                while (wait.Elapsed < TimeSpan.FromSeconds(8) && update == null)
                {
                    List<BridgeRequest> fresh = extension.Requests().Skip(before).ToList();
                    List<string> all = fresh.SelectMany(r => r.Texts).ToList();
                    if (ChineseChanged.All(e => all.Any(t => ocrMode ? Accuracy(e, t) >= 0.8 : t == e)))
                    {
                        update = fresh.Last();
                    }
                    else
                    {
                        await Task.Delay(50);
                    }
                }

                Report.Check(update != null && (update.Utc - changed.ReportedUtc).TotalMilliseconds <= 2500,
                    name + ": changed text is requested within 2.5 s",
                    update != null
                        ? "new texts requested " + Report.F((update.Utc - changed.ReportedUtc).TotalMilliseconds) + " ms after the change"
                        : "no request with the new texts within 8 s");
            }

            // ---- second instance
            if (settingsJson == null)
            {
                await CheckSecondInstanceAsync(name, app, extension);
            }

            // ---- resources of the running app
            app.Refresh();
            Report.Info("exe after " + Report.F(startup.Elapsed.TotalSeconds) + " s: working set " + Mb(app.WorkingSet64)
                        + " MB, private " + Mb(app.PrivateMemorySize64) + " MB, handles " + app.HandleCount
                        + ", threads " + app.Threads.Count);
            Report.Check(!app.HasExited && extension.IsOpen, name + ": still running and connected at the end",
                "process alive: " + !app.HasExited + ", bridge connection open: " + extension.IsOpen);
        }
        finally
        {
            if (extension != null)
            {
                extension.Dispose();
            }

            try
            {
                if (!app.HasExited)
                {
                    app.Kill(true);
                    app.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Report.Info("killing the app failed: " + ex.Message);
            }

            app.Dispose();
        }

        // ---- log file
        string logPath = Path.Combine(directory, "log.txt");
        string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        Report.Info("%AppData%\\AutoTranslator\\log.txt:");
        foreach (string line in log.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                Report.Info("  | " + line.TrimEnd());
            }
        }

        int errors = log.Split('\n').Count(l => l.Contains("[ERROR]"));
        Report.Check(log.Contains("AutoTranslator starting") && errors == 0, name + ": log.txt has no errors",
            (log.Length == 0 ? "log.txt is missing or empty" : errors + " [ERROR] line(s)")
            + " (the app was terminated with TerminateProcess, so there is no shutdown entry)");

        string settingsPath = Path.Combine(directory, "settings.json");
        Report.Info("settings.json after the run: " + (File.Exists(settingsPath)
            ? File.ReadAllText(settingsPath).Replace("\r", string.Empty).Replace("\n", " ") : "(missing)"));
    }

    private async Task CheckSecondInstanceAsync(string name, Process first, FakeExtension extension)
    {
        string checkName = name + ": second start does not hang or disturb the first";
        Process second = Process.Start(new ProcessStartInfo(_exePath!) { UseShellExecute = false })!;
        try
        {
            IntPtr dialog = IntPtr.Zero;
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(25) && !second.HasExited && dialog == IntPtr.Zero)
            {
                dialog = Native.TopLevelWindowsOf(second.Id)
                    .FirstOrDefault(h => Native.IsWindowVisible(h) && Native.ClassOf(h) == "#32770");
                if (dialog == IntPtr.Zero)
                {
                    await Task.Delay(150);
                }
            }

            string what;
            if (second.HasExited)
            {
                what = "exited by itself with code " + second.ExitCode + " after " + Report.F(wait.Elapsed.TotalSeconds) + " s";
            }
            else if (dialog != IntPtr.Zero)
            {
                what = "showed the message box \"" + Native.TitleOf(dialog) + "\" after " + Report.F(wait.Elapsed.TotalSeconds) + " s";
                Native.PostMessageW(dialog, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Stopwatch exit = Stopwatch.StartNew();
                while (exit.Elapsed < TimeSpan.FromSeconds(8) && !second.HasExited)
                {
                    await Task.Delay(100);
                }

                what += second.HasExited
                    ? ", exited with code " + second.ExitCode + " after the box was closed"
                    : ", but did NOT exit within 8 s after the box was closed";
            }
            else
            {
                what = "neither exited nor showed a message within 25 s";
            }

            bool firstOk = !first.HasExited && extension.IsOpen && await PortOpenAsync(BridgePort);
            Report.Check(second.HasExited && firstOk, checkName,
                "second instance " + what + "; first instance alive and connected: " + firstOk);
        }
        finally
        {
            try
            {
                if (!second.HasExited)
                {
                    second.Kill(true);
                }
            }
            catch
            {
                // best effort
            }

            second.Dispose();
        }

        await EnsureForegroundAsync(name);
    }

    private static async Task<string> TryConnectAsync(string? origin)
    {
        try
        {
            using (FakeExtension extension = await FakeExtension.ConnectAsync(BridgePort, origin))
            {
                return "accepted";
            }
        }
        catch (Exception ex)
        {
            return "rejected (" + ex.Message.Trim() + ")";
        }
    }

    private static async Task<bool> PortOpenAsync(int port)
    {
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(1));
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void KillStrayApps()
    {
        foreach (Process process in Process.GetProcessesByName("AutoTranslator"))
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch
            {
                // best effort
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
