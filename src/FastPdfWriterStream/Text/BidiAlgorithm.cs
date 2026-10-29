namespace FastPdfWriterStream.Text;

/// <summary>
/// پیاده‌سازی فشردهٔ الگوریتم دوطرفهٔ یونیکد (UAX #9) برای یک پاراگراف بدون embedding صریح:
/// قوانین W1–W7، N1–N2، I1–I2 و L1–L2.
/// </summary>
internal static class BidiAlgorithm
{
    /// <summary>سطح هر کاراکتر را محاسبه می‌کند.</summary>
    public static void ResolveLevels(ReadOnlySpan<char> text, int baseLevel, Span<byte> levels, Span<BidiClass> types)
    {
        int n = text.Length;
        var sor = baseLevel == 1 ? BidiClass.R : BidiClass.L;
        for (int i = 0; i < n; i++) types[i] = UnicodeData.GetBidi(text[i]);
        var original = types.Length >= 2 * n ? types.Slice(n, n) : default;
        if (!original.IsEmpty) types[..n].CopyTo(original);

        // X9: BN ها حذف نمی‌شوند ولی مانند کاراکتر قبلی رفتار می‌کنند
        // W1: NSM → نوع کاراکتر قبلی
        var prev = sor;
        for (int i = 0; i < n; i++)
        {
            var t = types[i];
            if (t == BidiClass.NSM || t == BidiClass.BN) types[i] = prev;
            else prev = t;
        }

        // W2: EN بعد از AL → AN ؛ W3: AL → R
        var lastStrong = sor;
        for (int i = 0; i < n; i++)
        {
            var t = types[i];
            if (t == BidiClass.L || t == BidiClass.R || t == BidiClass.AL) lastStrong = t;
            else if (t == BidiClass.EN && lastStrong == BidiClass.AL) types[i] = BidiClass.AN;
        }
        for (int i = 0; i < n; i++) if (types[i] == BidiClass.AL) types[i] = BidiClass.R;

        // W4: جداکنندهٔ تکی بین اعداد
        for (int i = 1; i < n - 1; i++)
        {
            var t = types[i];
            if (t == BidiClass.ES && types[i - 1] == BidiClass.EN && types[i + 1] == BidiClass.EN) types[i] = BidiClass.EN;
            else if (t == BidiClass.CS)
            {
                var a = types[i - 1]; var b = types[i + 1];
                if (a == b && (a == BidiClass.EN || a == BidiClass.AN)) types[i] = a;
            }
        }

        // W5: دنبالهٔ ET کنار EN → EN
        for (int i = 0; i < n; i++)
        {
            if (types[i] != BidiClass.ET) continue;
            int s = i, e = i;
            while (e < n && types[e] == BidiClass.ET) e++;
            bool adj = (s > 0 && types[s - 1] == BidiClass.EN) || (e < n && types[e] == BidiClass.EN);
            if (adj) for (int k = s; k < e; k++) types[k] = BidiClass.EN;
            i = e - 1;
        }

        // W6: باقی‌ماندهٔ جداکننده‌ها → ON
        for (int i = 0; i < n; i++)
        {
            var t = types[i];
            if (t == BidiClass.ES || t == BidiClass.ET || t == BidiClass.CS) types[i] = BidiClass.ON;
        }

        // W7: EN بعد از L → L
        lastStrong = sor;
        for (int i = 0; i < n; i++)
        {
            var t = types[i];
            if (t == BidiClass.L || t == BidiClass.R) lastStrong = t;
            else if (t == BidiClass.EN && lastStrong == BidiClass.L) types[i] = BidiClass.L;
        }

        // N1/N2: خنثی‌ها
        for (int i = 0; i < n; i++)
        {
            if (!IsNeutral(types[i])) continue;
            int s = i, e = i;
            while (e < n && IsNeutral(types[e])) e++;
            var before = s == 0 ? sor : Strong(types[s - 1]);
            var after = e == n ? sor : Strong(types[e]);
            var resolved = before == after ? before : (baseLevel == 1 ? BidiClass.R : BidiClass.L);
            for (int k = s; k < e; k++) types[k] = resolved;
            i = e - 1;
        }

        // I1/I2: سطح نهایی
        for (int i = 0; i < n; i++)
        {
            var t = types[i];
            int lvl = baseLevel;
            if ((baseLevel & 1) == 0)
            {
                if (t == BidiClass.R) lvl += 1;
                else if (t == BidiClass.AN || t == BidiClass.EN) lvl += 2;
            }
            else if (t == BidiClass.L || t == BidiClass.EN || t == BidiClass.AN) lvl += 1;
            levels[i] = (byte)lvl;
        }

        // L1: فاصله‌ها و جداکننده‌های انتهای پاراگراف به سطح پایه
        if (!original.IsEmpty)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                var t = original[i];
                if (t == BidiClass.WS || t == BidiClass.BN || t == BidiClass.S || t == BidiClass.B) levels[i] = (byte)baseLevel;
                else break;
            }
            for (int i = 0; i < n; i++)
            {
                if (original[i] != BidiClass.S && original[i] != BidiClass.B) continue;
                levels[i] = (byte)baseLevel;
                for (int k = i - 1; k >= 0 && (original[k] == BidiClass.WS || original[k] == BidiClass.BN); k--)
                    levels[k] = (byte)baseLevel;
            }
        }
    }

    private static bool IsNeutral(BidiClass t) =>
        t == BidiClass.ON || t == BidiClass.WS || t == BidiClass.S || t == BidiClass.B || t == BidiClass.BN;

    // در N1، اعداد مانند R رفتار می‌کنند
    private static BidiClass Strong(BidiClass t) => t == BidiClass.L ? BidiClass.L : BidiClass.R;

    /// <summary>
    /// L2: ترتیب نمایشی. <paramref name="order"/> با اندیس‌های منطقی پر و سپس مرتب می‌شود.
    /// </summary>
    public static void Reorder(ReadOnlySpan<byte> levels, Span<int> order)
    {
        int n = levels.Length;
        for (int i = 0; i < n; i++) order[i] = i;
        if (n == 0) return;
        byte max = 0, minOdd = byte.MaxValue;
        for (int i = 0; i < n; i++)
        {
            byte l = levels[i];
            if (l > max) max = l;
            if ((l & 1) == 1 && l < minOdd) minOdd = l;
        }
        if (minOdd == byte.MaxValue) return; // همه LTR
        for (int lvl = max; lvl >= minOdd; lvl--)
        {
            int i = 0;
            while (i < n)
            {
                if (levels[order[i]] >= lvl)
                {
                    // توجه: سطح‌ها بر اساس موقعیت فعلی مقایسه می‌شوند
                    int s = i;
                    while (i < n && levels[order[i]] >= lvl) i++;
                    order.Slice(s, i - s).Reverse();
                }
                else i++;
            }
        }
    }
}
