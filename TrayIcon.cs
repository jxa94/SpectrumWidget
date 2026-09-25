using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace SpectrumWidget;

sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;

    public TrayIcon(Action onLeftClick, Action onRightClick)
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "频谱音乐小部件",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) onLeftClick();
            else if (e.Button == WinForms.MouseButtons.Right) onRightClick();
        };
    }

    public void ShowTip(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None);

    public static Bitmap DrawLogo(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float[] hs = { 0.45f, 0.8f, 1f, 0.6f, 0.85f };
        float slot = size / (float)hs.Length, bw = slot * 0.66f;
        using var brush = new LinearGradientBrush(new Rectangle(0, 0, size, size),
            Color.FromArgb(0xC0, 0x9B, 0xFF), Color.FromArgb(0x6E, 0xA8, 0xFE), LinearGradientMode.Vertical);
        for (int i = 0; i < hs.Length; i++)
        {
            float h = hs[i] * size * 0.92f;
            float x = i * slot + (slot - bw) / 2, y = size - h;
            float r = Math.Min(bw, 6);
            using var path = new GraphicsPath();
            path.AddArc(x, y, r, r, 180, 90);
            path.AddArc(x + bw - r, y, r, r, 270, 90);
            path.AddLine(x + bw, size, x, size);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
        return bmp;
    }

    static Icon CreateIcon()
    {
        using var bmp = DrawLogo(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
