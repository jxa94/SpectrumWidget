using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpectrumWidget;

/// <summary>
/// 瀵归粯璁ゆ挱鏀捐澶囧仛 WASAPI 鐜洖閲囬泦锛屾寜闇€绠楀嚭瀵规暟鍒嗗竷鐨勯娈电數骞筹紙0..1锛夈€?/// 榛樿璁惧鍒囨崲锛堟瘮濡傛彃鎷旇€虫満锛夋椂鑷姩閲嶈繛銆?/// </summary>
sealed class AudioCapture : IDisposable
{
    const int FftSize = 4096;
    const double MinFreq = 40, MaxFreq = 16000;

    readonly float[] _ring = new float[FftSize * 2];
    int _write;
    readonly object _ringLock = new();
    readonly object _restartLock = new();

    readonly float[] _window = new float[FftSize];
    readonly float[] _re = new float[FftSize];
    readonly float[] _im = new float[FftSize];
    readonly float[] _mag = new float[FftSize / 2];
    double[] _db = Array.Empty<double>();

    WasapiLoopbackCapture? _capture;
    string? _deviceId;
    long _lastDataTick;
    Timer? _watchdog;
    double _agcRef = -30;
    volatile int _sampleRate = 48000;
    bool _disposed;

    public AudioCapture()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    public void Start()
    {
        // 鍦ㄧ嚎绋嬫睜锛圡TA锛夐噷寤?COM 瀵硅薄锛岄伩鍏嶅拰 WPF 鐨?STA 绾跨▼绾犵紶
        _watchdog = new Timer(_ => CheckDevice(), null, 0, 2000);
    }

    void CheckDevice()
    {
        if (_disposed) return;
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) { StopCapture(); _deviceId = null; return; }
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (dev.ID != _deviceId || _capture == null) Restart();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void StopCapture()
    {
        lock (_restartLock)
        {
            var old = _capture;
            _capture = null;
            if (old == null) return;
            old.DataAvailable -= OnData;
            try { old.StopRecording(); } catch { }
            try { old.Dispose(); } catch { }
        }
    }

    void Restart()
    {
        lock (_restartLock)
        {
            StopCapture();
            try
            {
                var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var cap = new WasapiLoopbackCapture(dev);
                _sampleRate = cap.WaveFormat.SampleRate;
                cap.DataAvailable += OnData;
                cap.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null) { _deviceId = null; App.Log(e.Exception); }
                };
                cap.StartRecording();
                _capture = cap;
                _deviceId = dev.ID;
            }
            catch (Exception ex)
            {
                _deviceId = null;
                App.Log(ex);
            }
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture cap || e.BytesRecorded == 0) return;
        var wf = cap.WaveFormat;
        int ch = Math.Max(1, wf.Channels);
        int bytes = wf.BitsPerSample / 8;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                       || (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);
        int frameBytes = bytes * ch;
        int frames = e.BytesRecorded / frameBytes;
        var buf = e.Buffer;

        lock (_ringLock)
        {
            for (int f = 0; f < frames; f++)
            {
                int o = f * frameBytes;
                float sum = 0;
                for (int c = 0; c < ch; c++, o += bytes)
                {
                    sum += bytes switch
                    {
                        4 when isFloat => BitConverter.ToSingle(buf, o),
                        4 => BitConverter.ToInt32(buf, o) / 2147483648f,
                        3 => ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f,
                        2 => BitConverter.ToInt16(buf, o) / 32768f,
                        _ => 0f,
                    };
                }
                _ring[_write] = sum / ch;
                if (++_write == _ring.Length) _write = 0;
            }
        }
        Interlocked.Exchange(ref _lastDataTick, Environment.TickCount64);
    }

    /// <summary>濉厖 bands锛?..1锛夈€傛病鏈夊０闊虫椂杩斿洖 false 骞舵竻闆躲€?/summary>
    public bool GetBands(float[] bands, double dt)
    {
        int n = bands.Length;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastDataTick) > 250)
        {
            Array.Clear(bands);
            return false;
        }

        lock (_ringLock)
        {
            int start = _write - FftSize;
            if (start < 0) start += _ring.Length;
            for (int i = 0; i < FftSize; i++)
            {
                int idx = start + i;
                if (idx >= _ring.Length) idx -= _ring.Length;
                _re[i] = _ring[idx] * _window[i];
                _im[i] = 0;
            }
        }

        Fft(_re, _im);
        const float norm = 4f / FftSize; // Hann 鐩稿共澧炵泭 0.5 脳 鍗曡竟璋?脳2
        for (int k = 0; k < _mag.Length; k++)
            _mag[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * norm;

        if (_db.Length != n) _db = new double[n];
        double sr = _sampleRate;
        double lo = MinFreq, hi = Math.Min(MaxFreq, sr * 0.45);
        double binHz = sr / FftSize;
        double frameMax = -200;

        for (int b = 0; b < n; b++)
        {
            double f0 = lo * Math.Pow(hi / lo, (double)b / n);
            double f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / n);
            double k0 = f0 / binHz, k1 = f1 / binHz;
            double m;
            if (k1 - k0 < 1.0)
            {
                double kc = (k0 + k1) * 0.5;
                int ki = (int)kc;
                double t = kc - ki;
                m = _mag[ki] * (1 - t) + _mag[Math.Min(ki + 1, _mag.Length - 1)] * t;
            }
            else
            {
                m = 0;
                int a = (int)Math.Ceiling(k0), z = Math.Min((int)k1, _mag.Length - 1);
                for (int k = a; k <= z; k++) if (_mag[k] > m) m = _mag[k];
            }
            double fc = Math.Sqrt(f0 * f1);
            // +3 dB/鍊嶉绋?鍊炬枩琛ュ伩锛岃绮夌孩鍣０/甯歌闊充箰鐪嬭捣鏉ユ槸骞崇殑
            double db = 20 * Math.Log10(m + 1e-12) + 3.0 * Math.Log2(fc / 1000.0);
            _db[b] = db;
            if (db > frameMax) frameMax = db;
        }

        // 鑷姩澧炵泭锛氫笉绠＄郴缁熼煶閲忓澶э紝棰戣氨閮借兘鎾戞弧
        if (frameMax > _agcRef) _agcRef += (frameMax - _agcRef) * Math.Min(1, dt * 20);
        else _agcRef = Math.Max(frameMax, _agcRef - 5 * dt);
        double reference = Math.Max(_agcRef, -48);
        const double range = 40;

        for (int b = 0; b < n; b++)
        {
            double v = (_db[b] - (reference - range)) / range;
            v = Math.Clamp(v, 0, 1);
            bands[b] = (float)(Math.Pow(v, 1.6) * 0.96);
        }
        return true;
    }

    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    float xr = re[b] * cr - im[b] * ci;
                    float xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    float ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog?.Dispose();
        StopCapture();
    }
}
