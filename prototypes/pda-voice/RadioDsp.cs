namespace PdaVoicePrototype;

/// <summary>Suit-radio preview chain: band-pass 300-3400 Hz, light saturation, short beep before and after.</summary>
public static class RadioDsp
{
    sealed class Biquad
    {
        readonly float b0, b1, b2, a1, a2;
        float z1, z2;

        Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = (float)(b0 / a0); this.b1 = (float)(b1 / a0); this.b2 = (float)(b2 / a0);
            this.a1 = (float)(a1 / a0); this.a2 = (float)(a2 / a0);
        }

        // RBJ audio-EQ-cookbook filters.
        public static Biquad LowPass(double fs, double f0, double q)
        {
            double w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
            return new Biquad((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a);
        }

        public static Biquad HighPass(double fs, double f0, double q)
        {
            double w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
            return new Biquad((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a);
        }

        public float Process(float x)
        {
            float y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }

    public static float[] Process(float[] input, int rate, double lowHz = 300, double highHz = 3400, double drive = 2.2)
    {
        double q = 0.7071;
        // Two cascaded 2nd-order sections per edge for a steeper (4th-order) band-pass.
        var hp = new[] { Biquad.HighPass(rate, lowHz, q), Biquad.HighPass(rate, lowHz, q) };
        var lp = new[] { Biquad.LowPass(rate, highHz, q), Biquad.LowPass(rate, highHz, q) };
        var body = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            float x = input[i];
            foreach (var f in hp) x = f.Process(x);
            foreach (var f in lp) x = f.Process(x);
            body[i] = x;
        }

        // Normalise, then soft-clip for a slightly compressed, gritty radio tone.
        float peak = body.Select(MathF.Abs).DefaultIfEmpty(1e-6f).Max();
        float norm = 0.8f / Math.Max(peak, 1e-6f);
        float satNorm = MathF.Tanh((float)drive);
        for (int i = 0; i < body.Length; i++) body[i] = MathF.Tanh(body[i] * norm * (float)drive) / satNorm * 0.85f;

        var pre = Beep(rate, 1250, 0.085, 0.30);
        var post = Beep(rate, 880, 0.070, 0.30);
        int gap = (int)(0.12 * rate), tail = (int)(0.08 * rate);
        var outBuf = new float[pre.Length + gap + body.Length + tail + post.Length];
        Array.Copy(pre, 0, outBuf, 0, pre.Length);
        Array.Copy(body, 0, outBuf, pre.Length + gap, body.Length);
        Array.Copy(post, 0, outBuf, pre.Length + gap + body.Length + tail, post.Length);
        return outBuf;
    }

    static float[] Beep(int rate, double hz, double seconds, double amp)
    {
        int n = (int)(seconds * rate), fade = (int)(0.006 * rate);
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
            b[i] = (float)(Math.Sin(2 * Math.PI * hz * i / rate) * amp * env);
        }
        return b;
    }
}
