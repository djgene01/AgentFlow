namespace AgentFlow.Core.Sounds;

/// <summary>
/// Renders AgentFlow's sound effects from scratch: FM bells, marimba, soft pads and filtered noise,
/// mixed in stereo through a plate-style reverb, gently saturated and mastered to a fixed peak.
/// Nothing is downloaded; every cue is computed the same way each time (fixed noise seeds).
/// </summary>
public static class SoundSynth
{
    public const int Rate = 48000;

    /// <summary>Bump when a cue's design changes, so cached WAV files are rendered again.</summary>
    public const int Version = 1;

    /// <summary>A rendered cue as a 16-bit, 48 kHz stereo WAV file.</summary>
    public static byte[] RenderWav(SoundCue cue)
    {
        var (l, r) = Render(cue);
        return Wav.Encode(l, r, Rate);
    }

    public static (float[] Left, float[] Right) Render(SoundCue cue)
    {
        var d = Design(cue);
        var mix = new Mix(d.Length + d.Tail);
        d.Compose(mix);
        return Master(mix, d);
    }

    // ---- the cues ----

    private sealed record CueDesign(double Length, Action<Mix> Compose, double Loudness,
        double ReverbWet = 0.22, double Room = 0.6, double LowPass = 13000, double Tail = 1.6);

    private static CueDesign Design(SoundCue cue) => cue switch
    {
        // Two bright notes going up, like a question. Loud enough to notice from across the room.
        SoundCue.PermissionRequest => new(0.6, m =>
        {
            Chime(m, 83, 0.000, 1.0, -0.2, decay: 0.55);
            Chime(m, 88, 0.130, 1.0, 0.2, decay: 0.75);
            m.Add(Bell(Hz(100), 0.8, ratio: 3.5, index: 0.9, decay: 0.35), 0.13, 0.12, 0.35);
        }, Loudness: 1.0, ReverbWet: 0.24, Room: 0.62),

        // A rising major arpeggio, softer: "your turn".
        SoundCue.ReviewWaiting => new(0.8, m =>
        {
            m.Add(Marimba(Hz(72), 0.9, 0.55), 0.00, 0.8, -0.3);
            m.Add(Marimba(Hz(76), 0.9, 0.55), 0.11, 0.8, 0.0);
            m.Add(Marimba(Hz(79), 1.1, 0.70), 0.22, 0.85, 0.3);
            m.Add(Bell(Hz(91), 1.2, ratio: 3.5, index: 1.0, decay: 0.6), 0.22, 0.18, 0.3);
        }, Loudness: 0.85, ReverbWet: 0.3, Room: 0.7),

        SoundCue.Answered => new(0.25, m =>
        {
            m.Add(Marimba(Hz(91), 0.25, 0.09), 0, 1.0, 0.1);
            m.Add(Marimba(Hz(96), 0.2, 0.05), 0.035, 0.45, -0.1);
        }, Loudness: 0.45, ReverbWet: 0.12, Room: 0.4, Tail: 0.6),

        SoundCue.AutoAllowed => new(0.15, m => m.Add(Marimba(Hz(96), 0.15, 0.05), 0, 1.0, 0.25),
            Loudness: 0.28, ReverbWet: 0.12, Room: 0.4, Tail: 0.5),

        // A soft upward "pop" with a breath of air.
        SoundCue.MessageSent => new(0.2, m =>
        {
            m.Add(Sweep(520, 980, 0.16, 0.05), 0, 1.0, 0);
            m.AddStereo(Swoosh(0.14, 1800, 6000, seed: 11), Swoosh(0.14, 1800, 6000, seed: 12), 0, 0.12);
        }, Loudness: 0.42, ReverbWet: 0.14, Room: 0.45, Tail: 0.6),

        SoundCue.TurnFinished => new(0.5, m =>
        {
            Chime(m, 79, 0.00, 0.8, -0.15, decay: 0.4);
            Chime(m, 86, 0.09, 0.85, 0.15, decay: 0.6);
        }, Loudness: 0.68, ReverbWet: 0.26, Room: 0.62),

        // A muffled falling minor third over a low thump.
        SoundCue.TurnFailed => new(0.6, m =>
        {
            m.Add(Thump(95, 48, 0.3), 0, 0.6, 0);
            m.Add(Marimba(Hz(64), 0.6, 0.35), 0.0, 0.9, -0.15);
            m.Add(Marimba(Hz(61), 0.8, 0.5), 0.14, 0.9, 0.15);
        }, Loudness: 0.75, ReverbWet: 0.2, Room: 0.55, LowPass: 2600),

        SoundCue.Stopped => new(0.3, m => m.Add(Sweep(620, 240, 0.24, 0.09), 0, 1.0, 0),
            Loudness: 0.55, ReverbWet: 0.16, Room: 0.5, LowPass: 5000, Tail: 0.8),

        // Air rising into a warm chord.
        SoundCue.RunStarted => new(1.1, m =>
        {
            m.AddStereo(Swoosh(0.55, 250, 5000, seed: 3), Swoosh(0.55, 250, 5000, seed: 4), 0, 0.5);
            foreach (var (n, p) in new[] { (60, -0.4), (67, 0.4), (72, 0.0) })
                m.Add(Pad(Hz(n), 0.75, attack: 0.18), 0.25, 0.16, p);
            m.Add(Marimba(Hz(72), 1.0, 0.6), 0.48, 0.7, 0);
            m.Add(Bell(Hz(84), 1.0, ratio: 3.5, index: 0.8, decay: 0.5), 0.48, 0.12, 0.2);
        }, Loudness: 0.72, ReverbWet: 0.3, Room: 0.72),

        SoundCue.StageStarted => new(0.3, m =>
        {
            m.Add(Marimba(Hz(79), 0.35, 0.14), 0, 1.0, -0.1);
            m.Add(Marimba(Hz(86), 0.3, 0.08), 0.004, 0.25, 0.1);
        }, Loudness: 0.45, ReverbWet: 0.2, Room: 0.55, Tail: 0.9),

        SoundCue.StageFinished => new(0.45, m =>
        {
            m.Add(Marimba(Hz(76), 0.5, 0.3), 0.00, 0.9, -0.2);
            m.Add(Marimba(Hz(81), 0.7, 0.45), 0.085, 0.9, 0.2);
        }, Loudness: 0.55, ReverbWet: 0.24, Room: 0.6),

        // Up a fifth, then the octave: a small "ta-da".
        SoundCue.ReviewPassed => new(0.7, m =>
        {
            Chime(m, 79, 0.00, 0.75, -0.25, decay: 0.35);
            Chime(m, 86, 0.08, 0.8, 0.0, decay: 0.4);
            Chime(m, 91, 0.16, 0.9, 0.25, decay: 0.8);
        }, Loudness: 0.72, ReverbWet: 0.28, Room: 0.66),

        SoundCue.ChangesRequested => new(0.55, m =>
        {
            m.Add(Marimba(Hz(74), 0.5, 0.32), 0.00, 0.9, 0.15);
            m.Add(Marimba(Hz(71), 0.7, 0.5), 0.12, 0.9, -0.15);
            m.Add(Bell(Hz(83), 0.7, ratio: 3.5, index: 0.7, decay: 0.35), 0.12, 0.1, -0.15);
        }, Loudness: 0.6, ReverbWet: 0.24, Room: 0.6, LowPass: 9000),

        // The big one: a C major arpeggio over a pad, with a shimmer on top.
        SoundCue.RunCompleted => new(1.8, m =>
        {
            int[] notes = [72, 76, 79, 84];
            for (var i = 0; i < notes.Length; i++)
                Chime(m, notes[i], i * 0.09, i == 3 ? 0.95 : 0.75, -0.45 + i * 0.3, decay: i == 3 ? 1.1 : 0.45);
            foreach (var (n, p) in new[] { (48, 0.0), (60, -0.5), (64, 0.5), (67, -0.2), (72, 0.2) })
                m.Add(Pad(Hz(n), 1.5, attack: 0.12), 0.27, n == 48 ? 0.14 : 0.1, p);
            m.Add(Bell(Hz(96), 1.4, ratio: 3.5, index: 0.6, decay: 0.7), 0.42, 0.08, 0.4);
            m.Add(Bell(Hz(103), 1.2, ratio: 3.5, index: 0.5, decay: 0.6), 0.5, 0.05, -0.4);
        }, Loudness: 0.9, ReverbWet: 0.32, Room: 0.8, Tail: 2.0),

        SoundCue.RunFailed => new(1.1, m =>
        {
            foreach (var (n, p) in new[] { (57, -0.4), (60, 0.4), (64, 0.0) })
                m.Add(Pad(Hz(n), 0.9, attack: 0.04), 0, 0.12, p);
            m.Add(Marimba(Hz(64), 0.6, 0.35), 0.00, 0.85, 0.2);
            m.Add(Marimba(Hz(60), 0.6, 0.35), 0.13, 0.85, 0.0);
            m.Add(Marimba(Hz(57), 0.9, 0.6), 0.26, 0.9, -0.2);
            m.Add(Thump(85, 42, 0.35), 0.26, 0.45, 0);
        }, Loudness: 0.75, ReverbWet: 0.26, Room: 0.68, LowPass: 3200),

        // A low, slightly dissonant bell: noticeable, not alarming.
        SoundCue.Error => new(0.6, m =>
        {
            m.Add(Thump(110, 55, 0.28), 0, 0.7, 0);
            m.Add(Bell(Hz(58), 0.8, ratio: 1.41, index: 1.6, decay: 0.38), 0, 0.7, -0.1);
            m.Add(Bell(Hz(59), 0.8, ratio: 1.41, index: 1.2, decay: 0.3), 0.012, 0.35, 0.1);
        }, Loudness: 0.7, ReverbWet: 0.2, Room: 0.55, LowPass: 3500),

        SoundCue.GitSuccess => new(0.55, m =>
        {
            Chime(m, 81, 0.00, 0.8, -0.2, decay: 0.35);
            Chime(m, 88, 0.075, 0.85, 0.2, decay: 0.65);
        }, Loudness: 0.65, ReverbWet: 0.25, Room: 0.62),

        SoundCue.GitFailed => new(0.5, m =>
        {
            m.Add(Marimba(Hz(62), 0.5, 0.32), 0.00, 0.9, 0.1);
            m.Add(Marimba(Hz(61), 0.7, 0.45), 0.12, 0.9, -0.1);
            m.Add(Thump(90, 50, 0.22), 0.0, 0.4, 0);
        }, Loudness: 0.6, ReverbWet: 0.2, Room: 0.55, LowPass: 3000),

        _ => throw new ArgumentOutOfRangeException(nameof(cue)),
    };

    /// <summary>A marimba note with a faint FM bell on top: the house "chime".</summary>
    private static void Chime(Mix m, int midi, double at, double gain, double pan, double decay)
    {
        var f = Hz(midi);
        m.Add(Marimba(f, decay + 0.4, decay), at, gain, pan);
        m.Add(Bell(f, decay + 0.6, ratio: 3.5, index: 1.4, decay: decay * 1.2), at + 0.002, gain * 0.32, -pan);
    }

    // ---- voices (mono) ----

    public static double Hz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    /// <summary>Two-operator FM: bright at the strike, mellowing as the modulation decays.</summary>
    private static float[] Bell(double f, double dur, double ratio, double index, double decay)
    {
        var buf = new float[Samples(dur)];
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            var mod = index * Math.Exp(-t / (decay * 0.35)) + index * 0.08;
            var s = Math.Sin(2 * Math.PI * f * t + mod * Math.Sin(2 * Math.PI * f * ratio * t));
            s += 0.18 * Math.Sin(2 * Math.PI * f * 2.0 * t + 0.7) * Math.Exp(-t / (decay * 0.5));
            buf[i] = (float)(s * Attack(t, 0.0025) * Math.Exp(-t / decay) * Release(t, dur));
        }
        return buf;
    }

    /// <summary>Tuned-bar partials (1 : 3.93 : 9.2) with fast-decaying overtones and a short mallet click.</summary>
    private static float[] Marimba(double f, double dur, double decay)
    {
        double[] ratios = [1, 3.93, 9.2], amps = [1, 0.28, 0.07], decays = [1, 0.22, 0.07];
        var buf = new float[Samples(dur)];
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            double s = 0;
            for (var k = 0; k < ratios.Length; k++)
                if (f * ratios[k] < Rate * 0.45)
                    s += amps[k] * Math.Sin(2 * Math.PI * f * ratios[k] * t) * Math.Exp(-t / (decay * decays[k]));
            // Mallet: a 2 ms raised-cosine burst two octaves up.
            if (t < 0.002) s += 0.25 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 0.002)) * Math.Sin(2 * Math.PI * f * 4 * t);
            buf[i] = (float)(s * Attack(t, 0.0012) * Release(t, dur));
        }
        return buf;
    }

    /// <summary>A warm pad: band-limited saw (additive) plus a detuned sine, slow attack, low-passed.</summary>
    private static float[] Pad(double f, double dur, double attack)
    {
        var buf = new float[Samples(dur)];
        var lp = Biquad.LowPass(Math.Min(f * 6, 4000), 0.6);
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            double s = 0;
            for (var n = 1; n <= 10 && f * n < Rate * 0.4; n++)
                s += Math.Sin(2 * Math.PI * f * n * t) / n * 0.5 + Math.Sin(2 * Math.PI * f * 1.004 * n * t) / n * 0.5;
            s = s * 0.5 + Math.Sin(2 * Math.PI * f * 0.997 * t) * 0.5;
            var env = Math.Min(1, t / attack);
            env = env * env * (3 - 2 * env);
            buf[i] = (float)(lp.Process(s) * env * Release(t, dur, 0.35));
        }
        return buf;
    }

    /// <summary>A sine gliding from one pitch to another, with an exponential decay.</summary>
    private static float[] Sweep(double from, double to, double dur, double decay)
    {
        var buf = new float[Samples(dur)];
        double phase = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            var f = from * Math.Pow(to / from, Math.Min(1, t / (dur * 0.5)));
            phase += 2 * Math.PI * f / Rate;
            var s = Math.Sin(phase) + 0.15 * Math.Sin(2 * phase);
            buf[i] = (float)(s * Attack(t, 0.003) * Math.Exp(-t / decay) * Release(t, dur));
        }
        return buf;
    }

    /// <summary>A low kick-like thump: falling pitch, quick decay. Gives weight to bad news.</summary>
    private static float[] Thump(double from, double to, double dur)
    {
        var buf = new float[Samples(dur)];
        double phase = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            var f = to + (from - to) * Math.Exp(-t / 0.04);
            phase += 2 * Math.PI * f / Rate;
            buf[i] = (float)(Math.Sin(phase) * Attack(t, 0.004) * Math.Exp(-t / (dur * 0.35)) * Release(t, dur));
        }
        return buf;
    }

    /// <summary>Noise through a band-pass whose centre glides up: a soft "whoosh".</summary>
    private static float[] Swoosh(double dur, double fromHz, double toHz, int seed)
    {
        var buf = new float[Samples(dur)];
        var rng = new Random(seed);
        double low = 0, band = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            var x = rng.NextDouble() * 2 - 1;
            var fc = fromHz * Math.Pow(toHz / fromHz, t / dur);
            var g = 2 * Math.Sin(Math.PI * Math.Min(fc, Rate / 6.0) / Rate);
            low += g * band;
            var high = x - low - 0.7 * band;
            band += g * high;
            var env = Math.Sin(Math.PI * t / dur);
            buf[i] = (float)(band * env * env);
        }
        return buf;
    }

    private static int Samples(double seconds) => Math.Max(1, (int)(seconds * Rate));

    /// <summary>A short sine-shaped fade in, so no note starts with a click.</summary>
    private static double Attack(double t, double a) => t >= a ? 1 : Math.Sin(Math.PI / 2 * t / a);

    /// <summary>Fades the last part of a voice to zero.</summary>
    private static double Release(double t, double dur, double r = 0.03)
    {
        var left = dur - t;
        return left >= r ? 1 : Math.Max(0, left / r);
    }

    // ---- mixing and mastering ----

    private sealed class Mix(double seconds)
    {
        public readonly float[] L = new float[Samples(seconds)];
        public readonly float[] R = new float[Samples(seconds)];

        /// <summary>Adds a mono voice with an equal-power pan (-1 left … 1 right).</summary>
        public void Add(float[] mono, double at, double gain, double pan)
        {
            var angle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
            float gl = (float)(Math.Cos(angle) * gain * Math.Sqrt(2)), gr = (float)(Math.Sin(angle) * gain * Math.Sqrt(2));
            var o = (int)(at * Rate);
            for (var i = 0; i < mono.Length && o + i < L.Length; i++)
            {
                L[o + i] += mono[i] * gl;
                R[o + i] += mono[i] * gr;
            }
        }

        public void AddStereo(float[] left, float[] right, double at, double gain)
        {
            var o = (int)(at * Rate);
            for (var i = 0; i < left.Length && o + i < L.Length; i++) L[o + i] += (float)(left[i] * gain);
            for (var i = 0; i < right.Length && o + i < R.Length; i++) R[o + i] += (float)(right[i] * gain);
        }
    }

    private static (float[] L, float[] R) Master(Mix mix, CueDesign d)
    {
        var n = mix.L.Length;
        float[] l = new float[n], r = new float[n];

        // Normalise the dry mix first so the reverb and saturation behave the same for every cue.
        var dryPeak = Peak(mix.L, mix.R);
        var pre = dryPeak > 0 ? 1.0 / dryPeak : 1;

        var verb = new Reverb(d.Room, damp: 0.45);
        var preDelay = new float[(int)(0.014 * Rate)];
        var pd = 0;
        Biquad hpL = Biquad.HighPass(45, 0.7), hpR = Biquad.HighPass(45, 0.7);
        Biquad lpL = Biquad.LowPass(d.LowPass, 0.7), lpR = Biquad.LowPass(d.LowPass, 0.7);
        // The reverb return is kept out of the mud: high-passed at 220 Hz.
        Biquad vhL = Biquad.HighPass(220, 0.7), vhR = Biquad.HighPass(220, 0.7);
        for (var i = 0; i < n; i++)
        {
            var dl = mix.L[i] * pre;
            var dr = mix.R[i] * pre;
            var delayed = preDelay[pd];
            preDelay[pd] = (float)((dl + dr) * 0.5);
            pd = (pd + 1) % preDelay.Length;
            var (wl, wr) = verb.Process(delayed);
            var ol = dl * (1 - d.ReverbWet * 0.5) + vhL.Process(wl) * d.ReverbWet * 3.2;
            var or = dr * (1 - d.ReverbWet * 0.5) + vhR.Process(wr) * d.ReverbWet * 3.2;
            l[i] = (float)lpL.Process(hpL.Process(ol));
            r[i] = (float)lpR.Process(hpR.Process(or));
        }

        // Gentle tape-style saturation for glue, then master to the cue's loudness (peak -1 dBFS at 1.0).
        var peak = Peak(l, r);
        const double drive = 1.4;
        var norm = Math.Tanh(drive);
        for (var i = 0; i < n; i++)
        {
            l[i] = (float)(Math.Tanh(drive * l[i] / peak) / norm);
            r[i] = (float)(Math.Tanh(drive * r[i] / peak) / norm);
        }
        var target = 0.891 * d.Loudness / Math.Max(1e-9, Peak(l, r));

        // Trim the silent end of the tail (below -66 dB) and fade the last 60 ms.
        var end = n;
        while (end > Rate / 10 && Math.Abs(l[end - 1]) < 0.0005 && Math.Abs(r[end - 1]) < 0.0005) end--;
        var fade = Math.Min(end, (int)(0.06 * Rate));
        for (var i = 0; i < end; i++)
        {
            var g = target * (i >= end - fade ? (double)(end - i) / fade : 1);
            l[i] = (float)(l[i] * g);
            r[i] = (float)(r[i] * g);
        }
        return (l[..end], r[..end]);
    }

    private static double Peak(float[] l, float[] r)
    {
        double p = 0;
        for (var i = 0; i < l.Length; i++) p = Math.Max(p, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        return p;
    }

    /// <summary>Freeverb-style stereo reverb (8 damped combs into 4 all-passes per side).</summary>
    private sealed class Reverb
    {
        private static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
        private static readonly int[] AllpassTuning = [556, 441, 341, 225];
        private const int Spread = 23;

        private readonly Comb[] _cl, _cr;
        private readonly Allpass[] _al, _ar;

        public Reverb(double room, double damp)
        {
            var scale = Rate / 44100.0;
            var fb = room * 0.28 + 0.7;
            _cl = CombTuning.Select(t => new Comb((int)(t * scale), fb, damp * 0.4)).ToArray();
            _cr = CombTuning.Select(t => new Comb((int)((t + Spread) * scale), fb, damp * 0.4)).ToArray();
            _al = AllpassTuning.Select(t => new Allpass((int)(t * scale))).ToArray();
            _ar = AllpassTuning.Select(t => new Allpass((int)((t + Spread) * scale))).ToArray();
        }

        public (double L, double R) Process(double x)
        {
            var input = x * 0.015;
            double l = 0, r = 0;
            for (var i = 0; i < _cl.Length; i++) { l += _cl[i].Process(input); r += _cr[i].Process(input); }
            foreach (var a in _al) l = a.Process(l);
            foreach (var a in _ar) r = a.Process(r);
            return (l, r);
        }

        private sealed class Comb(int size, double feedback, double damp)
        {
            private readonly double[] _buf = new double[size];
            private int _i;
            private double _store;

            public double Process(double x)
            {
                var y = _buf[_i];
                _store = y * (1 - damp) + _store * damp;
                _buf[_i] = x + _store * feedback;
                if (++_i >= _buf.Length) _i = 0;
                return y;
            }
        }

        private sealed class Allpass(int size)
        {
            private readonly double[] _buf = new double[size];
            private int _i;

            public double Process(double x)
            {
                var b = _buf[_i];
                _buf[_i] = x + b * 0.5;
                if (++_i >= _buf.Length) _i = 0;
                return b - x;
            }
        }
    }

    /// <summary>RBJ cookbook biquad.</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public static Biquad LowPass(double f, double q)
        {
            f = Math.Min(f, Rate * 0.45);
            var w = 2 * Math.PI * f / Rate;
            var alpha = Math.Sin(w) / (2 * q);
            var c = Math.Cos(w);
            return new((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        public static Biquad HighPass(double f, double q)
        {
            var w = 2 * Math.PI * f / Rate;
            var alpha = Math.Sin(w) / (2 * q);
            var c = Math.Cos(w);
            return new((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        public double Process(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}

/// <summary>Writes 16-bit PCM WAV files.</summary>
public static class Wav
{
    public static byte[] Encode(float[] left, float[] right, int rate)
    {
        var frames = Math.Min(left.Length, right.Length);
        using var ms = new MemoryStream(44 + frames * 4);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 4);
        var rng = new Random(7); // TPDF dither, fixed seed so files are identical every time
        for (var i = 0; i < frames; i++)
        {
            w.Write(ToPcm(left[i], rng));
            w.Write(ToPcm(right[i], rng));
        }
        w.Flush();
        return ms.ToArray();
    }

    private static short ToPcm(float x, Random rng)
    {
        var d = (rng.NextDouble() - rng.NextDouble()) / 32768.0;
        return (short)Math.Clamp(Math.Round((x + d) * 32767), short.MinValue, short.MaxValue);
    }
}
