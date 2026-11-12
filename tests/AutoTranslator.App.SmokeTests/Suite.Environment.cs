using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AutoTranslator.Core;
using AutoTranslator.Desktop;
using Microsoft.Win32;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    // ------------------------------------------------------------------ 1 environment

    private Task CheckEnvironmentAsync()
    {
        Report.Info("OS: " + RuntimeInformation.OSDescription + " (" + Environment.OSVersion.VersionString + ")");
        try
        {
            using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key != null)
                {
                    Report.Info("Windows: " + key.GetValue("ProductName") + ", version " + key.GetValue("DisplayVersion")
                                + ", build " + key.GetValue("CurrentBuildNumber") + "." + key.GetValue("UBR"));
                }
            }
        }
        catch (Exception ex)
        {
            Report.Info("reading the Windows version from the registry failed: " + ex.Message);
        }

        Report.Info(".NET: " + RuntimeInformation.FrameworkDescription + ", process " + RuntimeInformation.ProcessArchitecture);

        bool stationVisible = Native.WindowStationVisible();
        string station = Native.UserObjectName(Native.GetProcessWindowStation());
        string desktop = Native.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
        Report.Info("session id " + Process.GetCurrentProcess().SessionId
                    + ", user \"" + Environment.UserName + "\""
                    + ", Environment.UserInteractive=" + Environment.UserInteractive
                    + ", window station \"" + station + "\" visible=" + stationVisible
                    + ", desktop \"" + desktop + "\""
                    + ", remote session=" + (Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0));

        int width = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        int height = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        ScreenRect virtualScreen = ScreenCapture.GetVirtualScreen();
        uint dpi = Native.GetDpiForSystem();
        Report.Info("primary screen " + width + "x" + height
                    + ", virtual screen " + Shots.Describe(virtualScreen)
                    + ", monitors " + Native.GetSystemMetrics(Native.SM_CMONITORS)
                    + ", system DPI " + dpi + " (" + (dpi * 100 / 96) + " %)");

        bool composition;
        int hr = Native.DwmIsCompositionEnabled(out composition);
        Report.Info("DWM composition: " + (hr == 0 ? (composition ? "on" : "off") : "query failed, HRESULT 0x" + hr.ToString("X8")));
        Report.Info("foreground window at start: " + Native.Describe(Native.GetForegroundWindow()));

        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Windows.Media.FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            families.Add(family.Source);
        }

        string[] cjk = { "Microsoft YaHei", "Microsoft YaHei UI", "SimSun", "NSimSun", "SimHei", "DengXian", "Microsoft JhengHei" };
        string installed = string.Join(", ", cjk.Where(families.Contains));
        Report.Info("Chinese fonts installed: " + (installed.Length > 0 ? installed : "NONE (Chinese test text would render as boxes)"));
        Report.Info("Segoe UI (overlay font) installed: " + families.Contains("Segoe UI"));

        _interactive = stationVisible && width >= 800 && height >= 600;
        Report.Check(_interactive, "1 environment",
            "interactive desktop " + (_interactive ? "available" : "NOT available") + ": " + width + "x" + height
            + " at " + (dpi * 100 / 96) + " % DPI, DWM composition " + (composition ? "on" : "off")
            + ", " + Native.GetSystemMetrics(Native.SM_CMONITORS) + " monitor(s)");

        if (Native.GetSystemMetrics(Native.SM_CMONITORS) < 2 || dpi != 96)
        {
            Report.Info("only one monitor and/or " + (dpi * 100 / 96) + " % scaling here.");
        }

        Report.Skip("1 environment: multi-monitor / mixed-DPI / scaling other than 100 %",
            "the runner has " + Native.GetSystemMetrics(Native.SM_CMONITORS) + " monitor(s) at "
            + (dpi * 100 / 96) + " % scaling; other configurations cannot be tested here");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ 2 Chinese OCR pack

    private Task CheckOcrPackAsync()
    {
        var tags = new List<string>();
        try
        {
            foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
            {
                tags.Add(language.LanguageTag);
            }

            Report.Info("OCR recognizer languages: " + (tags.Count > 0 ? string.Join(", ", tags) : "(none)")
                        + "; max image dimension " + OcrEngine.MaxImageDimension);
        }
        catch (Exception ex)
        {
            Report.Info("listing the OCR recognizers failed: " + ex);
        }

        _chineseOcr = OcrTextSource.IsChineseOcrAvailable();
        if (_chineseOcr)
        {
            _ocrUsable = true;
            Report.Pass("2 chinese-ocr-pack", "the app found a Chinese recognizer ("
                + string.Join(", ", tags.Where(t => t.StartsWith("zh", StringComparison.OrdinalIgnoreCase))) + ")");
            return Task.CompletedTask;
        }

        string? fallback = tags.FirstOrDefault(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (fallback != null)
        {
            // Same capture -> scale -> OCR -> region code path, only with another language and
            // without the "must contain Han" filter (test seams in OcrTextSource).
            OcrTextSource.TestLanguageTag = fallback;
            OcrTextSource.TestKeepNonHanText = true;
            _fallback = true;
            _ocrUsable = true;
            _ocrSkipReason = "no Chinese OCR recognizer could be installed on this runner (installed: "
                             + string.Join(", ", tags) + "); the OCR code path is exercised with "
                             + fallback + " text instead";
        }
        else
        {
            _ocrSkipReason = "no OCR recognizer at all is installed on this runner";
        }

        Report.Skip("2 chinese-ocr-pack", _ocrSkipReason);
        return Task.CompletedTask;
    }
}
