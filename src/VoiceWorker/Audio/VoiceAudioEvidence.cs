namespace DeadlockVoiceLab.Audio;

internal static class VoiceAudioEvidence
{
    public static bool IsEffectLike(ReadOnlySpan<float> samples)
    {
        const int window = 480, hop = 320;
        if (samples.Length < window + 8 * hop) return false;

        int frameCount = (samples.Length - window) / hop + 1;
        Span<double> energies = frameCount <= 320 ? stackalloc double[frameCount] : new double[frameCount];
        double peak = 0;
        for (int i = 0; i < energies.Length; i++)
        {
            var frame = samples.Slice(i * hop, window);
            double energy = 0;
            for (int j = 200; j < window; j += 2)
                energy += frame[j] * frame[j];
            energies[i] = energy;
            peak = Math.Max(peak, energy);
        }

        double active = Math.Max(1e-8, peak / 100);
        int adjacent = 0, steady = 0;
        for (int i = 1; i < energies.Length; i++)
        {
            double lower = Math.Min(energies[i], energies[i - 1]);
            if (lower <= active) continue;
            adjacent++;
            if (Math.Max(energies[i], energies[i - 1]) <= lower * 1.32)
                steady++;
        }
        // Effects can be loud enough to pass the volume gate. Unlike speech,
        // repeated noise bursts often have nearly constant energy between bursts.
        if (adjacent < 8 || steady < adjacent * 0.70) return false;

        int pitched = 0;
        for (int i = 0; i < energies.Length; i++)
            if (energies[i] > active && VoiceSegmenter.HasPitchPeriod(samples.Slice(i * hop, window), active / 140)
                && ++pitched == 2)
                return false;
        return true;
    }
}
