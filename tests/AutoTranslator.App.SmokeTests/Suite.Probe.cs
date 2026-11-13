using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutoTranslator.Core;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    /// <summary>
    /// Not a check of the app: asks this Windows version what a plain (non-WPF) layered,
    /// colour-keyed, click-through window can do. Printed as information for choosing the
    /// overlay technique per Windows version.
    /// </summary>
    private async Task ProbeLayeredWindowAsync()
    {
        const string Name = "probe: plain layered colour-key window";
        TextWindow text = _text!;
        TextLayout layout = await text.SetAsync();
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(300);
        ScreenRect client = layout.Client;
        Shot baseline = Shots.Grab(client);
        var box = new ScreenRect(client.X + 80, client.Y + 80, 200, 60);

        var form = new ProbeForm();
        form.FormBorderStyle = FormBorderStyle.None;
        form.StartPosition = FormStartPosition.Manual;
        form.ShowInTaskbar = false;
        form.TopMost = true;
        form.BackColor = Color.Magenta;
        form.Bounds = new Rectangle(client.X + 20, client.Y + 20, 420, 220);
        form.Box = new Rectangle(60, 60, 200, 60);
        try
        {
            form.Show();
            bool keyed = Native.SetLayeredWindowAttributes(form.Handle, 0x00FF00FF, 255, Native.LWA_COLORKEY);
            await Task.Delay(500);

            bool excluded = Native.SetWindowDisplayAffinity(form.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            int error = Marshal.GetLastWin32Error();
            await Task.Delay(500);
            ShotDiff whileExcluded = Shots.Diff(baseline, Shots.Grab(client));

            Native.SetWindowDisplayAffinity(form.Handle, Native.WDA_NONE);
            await Task.Delay(500);
            Shot visible = Shots.Grab(client);
            Shots.Save(visible, "04p-probe-layered-colorkey");
            ShotDiff whileVisible = Shots.Diff(baseline, visible);

            text.ClearClicks();
            bool clicked = Native.Click(box.X + 100, box.Y + 30);
            ScreenRect? click = clicked ? await text.WaitForClickAsync(TimeSpan.FromSeconds(2)) : null;

            Report.Info(Name + ": SetLayeredWindowAttributes(colour key)=" + keyed
                        + "; SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE)=" + excluded + (excluded ? string.Empty : " (error " + error + ")")
                        + "; capture while excluded: " + whileExcluded
                        + "; capture without exclusion: " + whileVisible + " (box drawn at " + Shots.Describe(box) + ")"
                        + "; click through the box: " + (click != null ? "yes" : "NO")
                        + "; extended style 0x" + Native.GetExStyle(form.Handle).ToString("X"));
        }
        finally
        {
            form.Close();
            form.Dispose();
        }
    }

    private sealed class ProbeForm : Form
    {
        public Rectangle Box { get; set; }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= unchecked((int)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
                                                      | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
                return parameters;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var brush = new SolidBrush(Color.FromArgb(32, 32, 32)))
            {
                e.Graphics.FillRectangle(brush, Box);
            }
        }
    }
}
