namespace Aiagent.Services.Scheduling;

/// <summary>
/// متن را به قطعات با مرز کلمه‌ای می‌شکند.
/// استفاده از مرز کلمه‌ای نسبت به مرز کاراکتری، هم‌ترازی توکنی بهتری می‌دهد.
/// </summary>
public static class PrefillChunker
{
    /// <summary>
    /// متن را به قطعات با اندازه‌ی تقریبی chunkSize chars تقسیم می‌کند.
    /// هر قطعه در یک مرز کلمه‌ای (فاصله یا newline) قطع می‌شود.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text) || chunkSize <= 0 || text.Length <= chunkSize)
            return new[] { text };

        var chunks = new List<string>(capacity: text.Length / chunkSize + 1);
        int pos = 0;

        while (pos < text.Length)
        {
            var remaining = text.Length - pos;

            if (remaining <= chunkSize)
            {
                chunks.Add(text.Substring(pos));
                break;
            }

            // هدف: pos + chunkSize، اما در مرز کلمه‌ای
            var targetEnd = pos + chunkSize;
            var end = targetEnd;

            // عقب‌نشینی برای یافتن مرز کلمه‌ای
            // اگر تا ۳۰٪ عقب‌تر مرزی نبود، از targetEnd استفاده می‌کنیم
            var minEnd = pos + (int)(chunkSize * 0.7);
            while (end > minEnd && !char.IsWhiteSpace(text[end - 1]))
                end--;

            // اگر مرز پیدا نشد، به‌زور در targetEnd قطع می‌کنیم
            if (end <= minEnd)
                end = targetEnd;

            // اگر مرز پیدا شد، آن را شامل می‌شود (شامل whitespace)
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            if (end < text.Length) end++;   // whitespace را شامل کن

            chunks.Add(text.Substring(pos, end - pos));
            pos = end;
        }

        return chunks;
    }
}