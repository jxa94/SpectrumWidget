using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpectrumWidget;

static class ColorUtil
{
    public static readonly Color DefaultA = Color.FromRgb(0x6E, 0xA8, 0xFE);
    public static readonly Color DefaultB = Color.FromRgb(0xC0, 0x9B, 0xFF);

    /// <summary>从封面挑出主色和辅色（按色相直方图 + 饱和度/亮度加权），再调成适合深色背景的亮色。</summary>
    public static (Color a, Color b) Extract(BitmapSource src)
    {
        const int S = 40;
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var small = new TransformedBitmap(conv, new ScaleTransform((double)S / conv.PixelWidth, (double)S / conv.PixelHeight));
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);

        const int Bins = 36;
        var weight = new double[Bins];
        var sumR = new double[Bins]; var sumG = new double[Bins]; var sumB = new double[Bins];
        double total = 0, grayV = 0, grayN = 0;

        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            ToHsv(r, g, b, out double hh, out double s, out double v);
            grayV += v; grayN++;
            double wt = s * s * (0.25 + v) * (v > 0.12 ? 1 : 0.1);
            int bin = (int)(hh / 360 * Bins) % Bins;
            weight[bin] += wt; sumR[bin] += r * wt; sumG[bin] += g * wt; sumB[bin] += b * wt;
            total += wt;
        }

        // 基本是黑白灰的封面：用冷灰白
        if (total / grayN < 0.02)
            return (Color.FromRgb(0xA9, 0xB4, 0xC8), Color.FromRgb(0xEE, 0xF1, 0xF7));

        // 相邻 bin 合并平滑
        var sm = new double[Bins];
        for (int i = 0; i < Bins; i++)
            sm[i] = weight[i] + 0.5 * (weight[(i + Bins - 1) % Bins] + weight[(i + 1) % Bins]);

        int best = 0;
        for (int i = 1; i < Bins; i++) if (sm[i] > sm[best]) best = i;
        int second = -1;
        for (int i = 0; i < Bins; i++)
        {
            int d = Math.Min(Math.Abs(i - best), Bins - Math.Abs(i - best));
            if (d < 5) continue; // 至少差 50°
            if (second < 0 || sm[i] > sm[second]) second = i;
        }

        Color a = Avg(best), b2;
        ToHsv(a.R / 255.0, a.G / 255.0, a.B / 255.0, out double ha, out double sa, out double va);
        a = FromHsv(ha, Math.Clamp(sa, 0.45, 0.8), Math.Clamp(va, 0.85, 1));

        if (second >= 0 && sm[second] > sm[best] * 0.18)
        {
            b2 = Avg(second);
            ToHsv(b2.R / 255.0, b2.G / 255.0, b2.B / 255.0, out double hb, out double sb, out double vb);
            b2 = FromHsv(hb, Math.Clamp(sb, 0.3, 0.7), Math.Clamp(vb, 0.92, 1));
        }
        else
        {
            b2 = FromHsv((ha + 35) % 360, Math.Clamp(sa * 0.7, 0.25, 0.6), 1);
        }
        return (a, b2);

        Color Avg(int bin)
        {
            double wsum = 0, r = 0, g = 0, bb = 0;
            for (int d = -1; d <= 1; d++)
            {
                int k = (bin + d + Bins) % Bins;
                wsum += weight[k]; r += sumR[k]; g += sumG[k]; bb += sumB[k];
            }
            if (wsum <= 0) return DefaultA;
            return Color.FromRgb((byte)(r / wsum * 255), (byte)(g / wsum * 255), (byte)(bb / wsum * 255));
        }
    }

    public static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 1e-9) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
