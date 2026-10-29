using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

/// <summary>Display-only aggregation shared by both players; source envelopes and PCM are never changed.</summary>
internal static class AudioWaveformDisplay
{
    internal static AudioEnvelope[] Create(IReadOnlyList<AudioEnvelope> envelope, double width)
    {
        if (envelope.Count == 0 || !double.IsFinite(width) || width <= 0) { return []; }
        // Two effective pixels per bar keep density consistent across recordings, widths, and DPI.
        int columns = Math.Min(envelope.Count, (int)Math.Clamp(Math.Floor(width / 2), 1, 1024));
        var peaks = new AudioEnvelope[columns];
        for (int column = 0; column < columns; column++)
        {
            int first = (int)((long)column * envelope.Count / columns), last = (int)((long)(column + 1) * envelope.Count / columns);
            float minimum = float.MaxValue, maximum = float.MinValue;
            for (int index = first; index < last; index++)
            {
                minimum = Math.Min(minimum, envelope[index].Minimum);
                maximum = Math.Max(maximum, envelope[index].Maximum);
            }
            peaks[column] = new(minimum, maximum);
        }
        var display = new AudioEnvelope[columns];
        for (int column = 0; column < columns; column++)
        {
            var current = peaks[column];
            // Keep truly silent bars flat instead of spreading adjacent speech into their gaps.
            if (current.Minimum == 0 && current.Maximum == 0) { display[column] = current; continue; }
            var previous = peaks[Math.Max(0, column - 1)];
            var next = peaks[Math.Min(columns - 1, column + 1)];
            display[column] = new((previous.Minimum + 2 * current.Minimum + next.Minimum) / 4,
                (previous.Maximum + 2 * current.Maximum + next.Maximum) / 4);
        }
        return display;
    }
}
