using System.IO;
using System.Runtime.InteropServices;

namespace ShibaPlayer;

// ShibaPlayer's own audio engine, no player library:
//   Decoder     - Windows' built-in MP3/AAC codecs (Media Foundation source reader), raw COM vtable calls -> float PCM
//   EqProcessor - our 10-band equalizer (RBJ peaking biquads)
//   Player      - our playback thread: decode -> speed/pitch -> EQ -> effects -> volume -> waveOut buffers, plus pause/seek/position
//   (the effects themselves are in Effects.cs)

/// <summary>Equalizer settings shared with the playback thread. Gains in dB per band, -12..+12.</summary>
public sealed class Equalizer
{
    public static readonly float[] Bands = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static readonly (string Name, float[] Gains)[] Presets =
    [
        ("Flat", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        ("Extra woof", [8, 6, 4, 1, 0, 0, 0, -1, -2, -2]),
        ("Classical", [0, 0, 0, 0, 0, 0, -3, -3, -3, -5]),
        ("Club", [0, 0, 3, 5, 5, 5, 3, 0, 0, 0]),
        ("Dance", [8, 6, 2, 0, 0, -3, -5, -5, 0, 0]),
        ("Full bass", [8, 8, 8, 4, 1, -3, -6, -8, -9, -9]),
        ("Full bass and treble", [6, 5, 0, -4, -3, 1, 6, 9, 10, 10]),
        ("Full treble", [-8, -8, -8, -3, 2, 9, 12, 12, 12, 12]),
        ("Headphones", [4, 9, 4, -2, -1, 1, 4, 8, 10, 11]),
        ("Large hall", [9, 9, 5, 5, 0, -3, -3, -3, 0, 0]),
        ("Live", [-3, 0, 3, 4, 4, 4, 3, 2, 2, 2]),
        ("Party", [6, 6, 0, 0, 0, 0, 0, 0, 6, 6]),
        ("Pop", [-1, 4, 6, 7, 4, -1, -2, -2, -1, -1]),
        ("Reggae", [0, 0, 0, -3, 0, 5, 5, 0, 0, 0]),
        ("Rock", [7, 4, -4, -6, -2, 3, 7, 9, 9, 9]),
        ("Ska", [-2, -3, -3, -1, 3, 4, 7, 7, 9, 7]),
        ("Soft", [4, 1, -1, -2, -1, 3, 7, 8, 9, 10]),
        ("Soft rock", [3, 3, 1, -1, -3, -3, -2, -1, 3, 7]),
        ("Techno", [7, 5, 0, -4, -3, 0, 7, 9, 9, 8]),
        ("Vocal", [-2, -2, -1, 0, 2, 4, 4, 2, 0, -1]),
    ];

    public float[] Gains { get; } = new float[Bands.Length];
    private int _version;
    public int Version => Volatile.Read(ref _version);
    private float _preamp;

    public bool On { get; set; } = true;

    public float Preamp
    {
        get => _preamp;
        set { _preamp = value; Interlocked.Increment(ref _version); }
    }

    public void Set(int band, float db)
    {
        Gains[band] = Math.Clamp(db, -12f, 12f);
        Interlocked.Increment(ref _version);
    }
}

/// <summary>Runs the equalizer over interleaved float samples. One per track (filter state is per channel).</summary>
public sealed class EqProcessor(Equalizer eq, int channels, int rate)
{
    private const double Q = 1.41; // ~1 octave wide, matches the band spacing
    private readonly double[,] _c = new double[Equalizer.Bands.Length, 5];             // b0 b1 b2 a1 a2
    private readonly double[,,] _z = new double[channels, Equalizer.Bands.Length, 2];  // transposed direct form II state
    private int[] _active = [];
    private double _preamp = 1;
    private int _built = -1;

    public void Reset() => Array.Clear(_z);

    public void Process(Span<float> s)
    {
        if (!eq.On) return;
        if (eq.Version != _built) Build();
        if (_active.Length == 0 && _preamp == 1) return;
        for (int i = 0; i < s.Length; i++)
        {
            int ch = i % channels;
            double x = s[i];
            foreach (var b in _active)
            {
                double y = _c[b, 0] * x + _z[ch, b, 0];
                _z[ch, b, 0] = _c[b, 1] * x - _c[b, 3] * y + _z[ch, b, 1];
                _z[ch, b, 1] = _c[b, 2] * x - _c[b, 4] * y;
                x = y;
            }
            s[i] = (float)(x * _preamp);
        }
    }

    /// <summary>RBJ audio-EQ-cookbook peaking filters. Boosts are matched by a negative preamp so loud songs don't clip.</summary>
    private void Build()
    {
        _built = eq.Version;
        var active = new List<int>();
        double maxBoost = 0;
        for (int b = 0; b < Equalizer.Bands.Length; b++)
        {
            double gain = eq.Gains[b], f = Equalizer.Bands[b];
            if (gain == 0 || f >= rate * 0.45) continue; // flat band = no-op; skip bands near Nyquist
            double a = Math.Pow(10, gain / 40), w = 2 * Math.PI * f / rate, alpha = Math.Sin(w) / (2 * Q), cos = Math.Cos(w);
            double a0 = 1 + alpha / a;
            _c[b, 0] = (1 + alpha * a) / a0;
            _c[b, 1] = -2 * cos / a0;
            _c[b, 2] = (1 - alpha * a) / a0;
            _c[b, 3] = -2 * cos / a0;
            _c[b, 4] = (1 - alpha / a) / a0;
            active.Add(b);
            maxBoost = Math.Max(maxBoost, gain);
        }
        _active = [.. active];
        _preamp = Math.Pow(10, (eq.Preamp - maxBoost) / 20);
    }
}

/// <summary>Decodes an MP3/M4A file to interleaved 32-bit float PCM with Windows' own codecs (Media Foundation).</summary>
public sealed unsafe class Decoder : IDisposable
{
    private const uint FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid NumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Duration100ns = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    private IntPtr _reader;
    private bool _eof;
    public int Channels { get; }
    public int SampleRate { get; }
    public TimeSpan Duration { get; }

    static Decoder() => Check(MFStartup(0x20070, 0)); // MF_VERSION, full startup

    public Decoder(string path)
    {
        Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out _reader));
        try
        {
            // only the audio stream (cover art can show up as a video stream)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(_reader, 4))(_reader, AllStreams, 0));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(_reader, 4))(_reader, FirstAudio, 1));

            Check(MFCreateMediaType(out var want));
            try
            {
                SetGuid(want, MajorType, Audio);
                SetGuid(want, SubType, Float);
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(_reader, 7))(_reader, FirstAudio, IntPtr.Zero, want));
            }
            finally { Release(want); }

            IntPtr got;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(_reader, 6))(_reader, FirstAudio, &got));
            try
            {
                Channels = (int)GetUInt32(got, NumChannels);
                SampleRate = (int)GetUInt32(got, SamplesPerSecond);
            }
            finally { Release(got); }

            var key = Duration100ns;
            var pv = new PropVariant();
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, PropVariant*, int>)Slot(_reader, 12))(_reader, MediaSource, &key, &pv) >= 0)
                Duration = TimeSpan.FromTicks(pv.Value); // MF time and TimeSpan ticks are both 100 ns
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Next chunk of samples; empty when the codec had nothing this time; null at the end.</summary>
    public float[]? Read()
    {
        if (_eof) return null;
        uint index, flags;
        long time;
        IntPtr sample;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(_reader, 9))(
            _reader, FirstAudio, 0, &index, &flags, &time, &sample));
        if ((flags & 1) != 0) throw new IOException("The audio codec reported an error.");
        _eof = (flags & 2) != 0;
        if (sample == IntPtr.Zero) return _eof ? null : [];
        try
        {
            IntPtr buffer;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer)); // ConvertToContiguousBuffer
            try
            {
                byte* data;
                uint max, length;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length)); // Lock
                var floats = new float[length / 4];
                new Span<float>(data, floats.Length).CopyTo(floats);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); // Unlock
                return floats;
            }
            finally { Release(buffer); }
        }
        finally { Release(sample); }
    }

    public void Seek(TimeSpan to)
    {
        var format = Guid.Empty; // 100 ns units
        var pv = new PropVariant { Vt = 20, Value = Math.Max(0, to.Ticks) }; // VT_I8
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, PropVariant*, int>)Slot(_reader, 8))(_reader, &format, &pv));
        _eof = false;
    }

    public void Dispose()
    {
        Release(_reader);
        _reader = IntPtr.Zero;
    }

    // ---- COM by hand: slot = index in the interface's vtable (IUnknown = 0..2, IMFAttributes = 3..32) ----

    internal static void* Slot(IntPtr obj, int slot) => (*(void***)obj)[slot];

    internal static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
    }

    internal static uint GetUInt32(IntPtr attrs, Guid key)
    {
        uint v;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(attrs, 7))(attrs, &key, &v));
        return v;
    }

    private static void SetGuid(IntPtr attrs, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &value));

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public long Value;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
}

/// <summary>Plays one file at a time: a background thread decodes, equalizes and feeds 4 x 50 ms waveOut buffers.
/// Events are raised on the thread that created the Player (the UI thread).</summary>
public sealed unsafe class Player : IDisposable
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private Session? _session;
    private bool _paused;

    public Equalizer Eq { get; } = new();
    public EffectSettings Fx { get; set; } = new();
    public float Volume { get; set; } = 0.7f;
    public event Action? Ended;
    public event Action<string>? Failed;

    public bool IsOpen => _session != null;
    public TimeSpan Duration => _session?.Duration ?? TimeSpan.Zero;
    public TimeSpan Position => _session?.Position ?? TimeSpan.Zero;

    public void Open(string path)
    {
        Stop();
        _paused = false;
        _session = new Session(this, path);
    }

    public void Stop()
    {
        _session?.Close();
        _session = null;
    }

    public void Pause()
    {
        _paused = true;
        _session?.SetPaused(true);
    }

    public void Resume()
    {
        _paused = false;
        _session?.SetPaused(false);
    }

    public void Seek(TimeSpan to) => _session?.Seek(to);

    public int VisualRate => _session?.Rate is > 0 and var rate ? rate : 44100;

    /// <summary>Fills an interleaved stereo buffer with the sound coming out of the speakers right now. False if nothing is playing.</summary>
    public bool ReadVisual(float[] stereo) => _session?.ReadVisual(stereo) ?? false;

    public void Dispose() => Stop();

    private void Raise(Session s, Action raise) => _ui.Post(_ => { if (_session == s) raise(); }, null);

    private sealed class Session
    {
        private const uint Done = 1, CallbackEvent = 0x50000, WaveMapper = 0xFFFFFFFF;
        private const int Buffers = 4;

        private readonly Player _owner;
        private readonly string _path;
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _thread;
        private volatile bool _closing;
        private long _seekTicks = -1;
        private IntPtr _wave;
        private int _rate, _blockAlign;
        private double _lastSeconds;
        private readonly Queue<(long End, double Seconds, double Speed)> _marks = new(); // output frame -> place in the song
        private readonly float[] _ring = new float[1 << 17];                             // recent output, stereo, for the visualizer
        private long _ringWritten;

        public int Rate => _rate;

        public TimeSpan Duration { get; private set; }

        public Session(Player owner, string path)
        {
            _owner = owner;
            _path = path;
            _thread = new Thread(Run) { IsBackground = true, Name = "ShibaPlayer audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public TimeSpan Position
        {
            get
            {
                var seek = Interlocked.Read(ref _seekTicks);
                if (seek >= 0) return TimeSpan.FromTicks(seek);
                var played = Played();
                lock (_marks)
                {
                    if (played < 0) return TimeSpan.FromSeconds(_lastSeconds);
                    while (_marks.Count > 0 && _marks.Peek().End <= played) _lastSeconds = _marks.Dequeue().Seconds;
                    if (_marks.Count == 0) return TimeSpan.FromSeconds(_lastSeconds);
                    var (end, seconds, speed) = _marks.Peek();
                    return TimeSpan.FromSeconds(Math.Max(0, seconds - (end - played) * speed / _rate));
                }
            }
        }

        /// <summary>Frames the sound card has played since the last seek; -1 if unknown.</summary>
        private long Played()
        {
            var wave = _wave;
            if (wave == IntPtr.Zero || _rate == 0) return -1;
            var t = new MmTime { Type = 2 }; // TIME_SAMPLES
            if (waveOutGetPosition(wave, ref t, sizeof(MmTime)) != 0) return -1;
            return t.Type == 4 ? t.Value / (uint)_blockAlign : t.Value; // some drivers answer in bytes
        }

        public bool ReadVisual(float[] stereo)
        {
            var played = Played();
            if (played < 0) return false;
            int frames = stereo.Length / 2, ringFrames = _ring.Length / 2;
            var start = Math.Min(played, Volatile.Read(ref _ringWritten)) - frames;
            for (int i = 0; i < frames; i++)
            {
                var at = start + i;
                stereo[2 * i] = at < 0 ? 0 : _ring[2 * (at % ringFrames)];
                stereo[2 * i + 1] = at < 0 ? 0 : _ring[2 * (at % ringFrames) + 1];
            }
            return true;
        }

        private void WriteRing(ReadOnlySpan<float> samples, int ch)
        {
            int ringFrames = _ring.Length / 2;
            var at = _ringWritten;
            for (int i = 0; i + ch <= samples.Length; i += ch, at++)
            {
                _ring[2 * (at % ringFrames)] = samples[i];
                _ring[2 * (at % ringFrames) + 1] = samples[ch > 1 ? i + 1 : i];
            }
            Volatile.Write(ref _ringWritten, at);
        }

        public void Seek(TimeSpan to)
        {
            // right after Open the length isn't known yet
            var last = Duration > TimeSpan.Zero ? Duration.Ticks - 1 : long.MaxValue;
            Interlocked.Exchange(ref _seekTicks, Math.Clamp(to.Ticks, 0, last));
            _wake.Set();
        }

        public void SetPaused(bool paused)
        {
            var wave = _wave;
            if (wave == IntPtr.Zero) return;
            if (paused) waveOutPause(wave); else waveOutRestart(wave);
        }

        public void Close()
        {
            _closing = true;
            _wake.Set();
            _thread.Join();
            _wake.Dispose();
        }

        private void Run()
        {
            Decoder? dec = null;
            var headers = new WaveHdr*[Buffers];
            IntPtr wave = IntPtr.Zero;
            var ended = false;
            try
            {
                dec = new Decoder(_path);
                Duration = dec.Duration;
                int ch = dec.Channels;
                _rate = dec.SampleRate;
                _blockAlign = 4 * ch;
                var fmt = new WaveFormat
                {
                    Tag = 3, // IEEE float
                    Channels = (ushort)ch,
                    Rate = (uint)_rate,
                    AvgBytes = (uint)(_rate * _blockAlign),
                    BlockAlign = (ushort)_blockAlign,
                    Bits = 32,
                };
                CheckMm(waveOutOpen(out wave, WaveMapper, ref fmt, _wake.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent));
                if (_owner._paused) waveOutPause(wave);
                _wave = wave;

                int samplesPerBuffer = _rate / 20 * ch; // 50 ms
                for (int i = 0; i < Buffers; i++)
                {
                    headers[i] = (WaveHdr*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHdr));
                    headers[i]->Data = (IntPtr)NativeMemory.Alloc((nuint)(samplesPerBuffer * 4));
                    headers[i]->Length = (uint)(samplesPerBuffer * 4);
                    CheckMm(waveOutPrepareHeader(wave, headers[i], sizeof(WaveHdr)));
                    headers[i]->Flags |= Done; // free to fill
                }

                var eq = new EqProcessor(_owner.Eq, ch, _rate);
                var chain = new EffectChain(_owner.Fx, ch, _rate);
                var stretcher = new Stretcher(ch, _rate);
                var stretching = false;
                float[] pending = [];
                int pendingAt = 0;
                double pendingSpeed = 1;
                long decoded = 0, written = 0;
                var eof = false;

                while (!_closing)
                {
                    var seek = Interlocked.Read(ref _seekTicks);
                    if (seek >= 0)
                    {
                        waveOutReset(wave); // hands every buffer back and zeroes the device position
                        if (_owner._paused) waveOutPause(wave);
                        dec.Seek(TimeSpan.FromTicks(seek));
                        lock (_marks)
                        {
                            _marks.Clear();
                            _lastSeconds = TimeSpan.FromTicks(seek).TotalSeconds;
                        }
                        Volatile.Write(ref _ringWritten, 0);
                        decoded = (long)(_lastSeconds * _rate);
                        written = 0;
                        stretcher.Reset();
                        chain.Reset();
                        Interlocked.CompareExchange(ref _seekTicks, -1, seek);
                        pending = [];
                        pendingAt = 0;
                        eof = false;
                        eq.Reset();
                    }

                    foreach (var h in headers)
                    {
                        if (eof || (Volatile.Read(ref h->Flags) & Done) == 0) continue;
                        var dst = new Span<float>((void*)h->Data, samplesPerBuffer);
                        int n = 0;
                        while (n < dst.Length)
                        {
                            if (pendingAt == pending.Length)
                            {
                                var next = dec.Read();
                                if (next == null) { eof = true; break; }
                                decoded += next.Length / ch;
                                var fx = _owner.Fx;
                                var pitchRatio = Math.Pow(2, fx.Pitch / 12);
                                var stretch = fx.Speed != 1 || pitchRatio != 1;
                                if (stretch != stretching) stretcher.Reset();
                                stretching = stretch;
                                pending = stretch ? stretcher.Process(next, fx.Speed, pitchRatio) : next;
                                pendingSpeed = fx.Speed;
                                pendingAt = 0;
                                continue;
                            }
                            int k = Math.Min(dst.Length - n, pending.Length - pendingAt);
                            pending.AsSpan(pendingAt, k).CopyTo(dst[n..]);
                            n += k;
                            pendingAt += k;
                        }
                        n -= n % ch; // whole frames only
                        if (n == 0) break;
                        var filled = dst[..n];
                        eq.Process(filled);
                        chain.Process(filled);
                        WriteRing(filled, ch);
                        written += n / ch;
                        var songAt = (decoded - (pending.Length - pendingAt) / ch * pendingSpeed) / _rate;
                        lock (_marks) _marks.Enqueue((written, songAt, pendingSpeed));
                        var vol = _owner.Volume;
                        for (int i = 0; i < filled.Length; i++) filled[i] = Math.Clamp(filled[i] * vol, -1f, 1f);
                        h->Length = (uint)(n * 4);
                        CheckMm(waveOutWrite(wave, h, sizeof(WaveHdr)));
                    }

                    var allDone = true;
                    foreach (var h in headers) allDone &= (Volatile.Read(ref h->Flags) & Done) != 0;
                    if (eof && allDone)
                    {
                        ended = true;
                        break;
                    }
                    _wake.WaitOne(50);
                }
            }
            catch (Exception ex)
            {
                if (!_closing) _owner.Raise(this, () => _owner.Failed?.Invoke(ex.Message));
            }
            finally
            {
                _wave = IntPtr.Zero;
                if (wave != IntPtr.Zero)
                {
                    waveOutReset(wave);
                    foreach (var h in headers)
                        if (h != null) waveOutUnprepareHeader(wave, h, sizeof(WaveHdr));
                    waveOutClose(wave);
                }
                foreach (var h in headers)
                {
                    if (h == null) continue;
                    NativeMemory.Free((void*)h->Data);
                    NativeMemory.Free(h);
                }
                dec?.Dispose();
            }
            if (ended && !_closing) _owner.Raise(this, () => _owner.Ended?.Invoke());
        }

        private static void CheckMm(int result)
        {
            if (result != 0) throw new IOException($"The sound device refused to play (waveOut error {result}).");
        }
    }

    // ---- winmm waveOut ----

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, AvgBytes;
        public ushort BlockAlign, Bits, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr wave, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr wave, WaveHdr* header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr wave, WaveHdr* header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr wave, WaveHdr* header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr wave);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr wave, ref MmTime time, int size);
}
