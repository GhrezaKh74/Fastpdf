namespace FastPdfWriterStream.Text;

internal enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON
}

internal enum JoiningType : byte
{
    U, // non-joining
    R, // right-joining (فقط به حرف قبل می‌چسبد)
    D, // dual-joining
    C, // join-causing (tatweel, ZWJ)
    T  // transparent (اعراب)
}

/// <summary>داده‌های یونیکد لازم برای bidi و شکل‌دهی حروف فارسی/عربی (جدول‌های فشرده).</summary>
internal static class UnicodeData
{
    private static readonly BidiClass[] Bidi = BuildBidi();
    private static readonly JoiningType[] Joining = BuildJoining();
    private static readonly Dictionary<char, (char Isol, char Fina, char Init, char Medi)> Forms = BuildForms();
    private static readonly Dictionary<char, char> Mirror = new()
    {
        ['('] = ')', [')'] = '(', ['['] = ']', [']'] = '[', ['{'] = '}', ['}'] = '{',
        ['<'] = '>', ['>'] = '<', ['«'] = '»', ['»'] = '«', ['‹'] = '›', ['›'] = '‹',
        ['≤'] = '≥', ['≥'] = '≤', ['⁅'] = '⁆', ['⁆'] = '⁅'
    };

    /// <summary>کاراکتر داخلی (Private Use) که جای «تعداد کل صفحات» را نگه می‌دارد.</summary>
    public const char TotalPagesMarker = '\uE000';

    /// <summary>شناسهٔ glyph مجازی برای نشانگر تعداد کل صفحات.</summary>
    public const ushort SlotGlyph = 0xFFFF;

    public static BidiClass GetBidi(char c) => Bidi[c];
    public static JoiningType GetJoining(char c) => c >= 0x0600 && c <= 0x08FF ? Joining[c - 0x0600] : c switch
    {
        '‍' => JoiningType.C,
        _ => JoiningType.U
    };

    public static bool TryGetForms(char c, out (char Isol, char Fina, char Init, char Medi) forms) => Forms.TryGetValue(c, out forms);

    public static bool TryMirror(char c, out char m) => Mirror.TryGetValue(c, out m);

    /// <summary>آیا کاراکتر راست‌به‌چپ است (R یا AL)؟</summary>
    public static bool IsRtl(char c) => c >= 0x0590 && (Bidi[c] == BidiClass.R || Bidi[c] == BidiClass.AL);

    /// <summary>کاراکترهای کنترلی که glyph ندارند (ZWNJ، ZWJ، LRM، RLM و ...).</summary>
    public static bool IsInvisibleControl(char c) =>
        c == '‌' || c == '‍' || c == '‎' || c == '‏' || c == '؜' ||
        (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩') || c == '﻿' || c == '​' || c == '­';

    private static BidiClass[] BuildBidi()
    {
        var t = new BidiClass[65536];
        // پیش‌فرض L؛ سپس محدوده‌ها
        void Set(int from, int to, BidiClass c) { for (int i = from; i <= to; i++) t[i] = c; }

        Set(0x0000, 0x0008, BidiClass.BN);
        t[0x0009] = BidiClass.S; t[0x000A] = BidiClass.B; t[0x000B] = BidiClass.S; t[0x000C] = BidiClass.WS; t[0x000D] = BidiClass.B;
        Set(0x000E, 0x001B, BidiClass.BN);
        Set(0x001C, 0x001E, BidiClass.B); t[0x001F] = BidiClass.S;
        t[0x0020] = BidiClass.WS;
        foreach (var c in "!\"&'()*;<=>?@[\\]^_`{|}~") t[c] = BidiClass.ON;
        foreach (var c in "#$%") t[c] = BidiClass.ET;
        t['+'] = BidiClass.ES; t['-'] = BidiClass.ES;
        foreach (var c in ",./:") t[c] = BidiClass.CS;
        Set('0', '9', BidiClass.EN);
        Set(0x007F, 0x0084, BidiClass.BN); t[0x0085] = BidiClass.B; Set(0x0086, 0x009F, BidiClass.BN);
        t[0x00A0] = BidiClass.CS; t[0x00A1] = BidiClass.ON; Set(0x00A2, 0x00A5, BidiClass.ET);
        Set(0x00A6, 0x00A9, BidiClass.ON); t[0x00AB] = BidiClass.ON; t[0x00AC] = BidiClass.ON; t[0x00AD] = BidiClass.BN;
        t[0x00AE] = BidiClass.ON; t[0x00AF] = BidiClass.ON; t[0x00B0] = BidiClass.ET; t[0x00B1] = BidiClass.ET;
        t[0x00B2] = BidiClass.EN; t[0x00B3] = BidiClass.EN; t[0x00B4] = BidiClass.ON; Set(0x00B6, 0x00B8, BidiClass.ON);
        t[0x00B9] = BidiClass.EN; Set(0x00BB, 0x00BF, BidiClass.ON); t[0x00D7] = BidiClass.ON; t[0x00F7] = BidiClass.ON;
        Set(0x0300, 0x036F, BidiClass.NSM);

        // عبری
        Set(0x0590, 0x05FF, BidiClass.R); Set(0x0591, 0x05BD, BidiClass.NSM);
        // عربی/فارسی
        Set(0x0600, 0x07BF, BidiClass.AL);
        Set(0x0600, 0x0605, BidiClass.AN);
        Set(0x0609, 0x060A, BidiClass.ET);
        t[0x060C] = BidiClass.CS;
        Set(0x0610, 0x061A, BidiClass.NSM);
        Set(0x064B, 0x065F, BidiClass.NSM);
        Set(0x0660, 0x0669, BidiClass.AN);
        t[0x066A] = BidiClass.ET; t[0x066B] = BidiClass.AN; t[0x066C] = BidiClass.AN;
        t[0x0670] = BidiClass.NSM;
        Set(0x06D6, 0x06DC, BidiClass.NSM); t[0x06DD] = BidiClass.AN; t[0x06DE] = BidiClass.ON;
        Set(0x06DF, 0x06E4, BidiClass.NSM); Set(0x06E7, 0x06E8, BidiClass.NSM); t[0x06E9] = BidiClass.ON;
        Set(0x06EA, 0x06ED, BidiClass.NSM);
        Set(0x06F0, 0x06F9, BidiClass.EN);
        Set(0x07C0, 0x08FF, BidiClass.R); Set(0x0860, 0x08FF, BidiClass.AL); Set(0x08D3, 0x08FF, BidiClass.NSM);

        // نقطه‌گذاری عمومی
        Set(0x2000, 0x200A, BidiClass.WS);
        Set(0x200B, 0x200D, BidiClass.BN);
        t[0x200E] = BidiClass.L; t[0x200F] = BidiClass.R;
        Set(0x2010, 0x2027, BidiClass.ON);
        t[0x2028] = BidiClass.WS; t[0x2029] = BidiClass.B;
        Set(0x202A, 0x202E, BidiClass.BN);
        t[0x202F] = BidiClass.CS;
        Set(0x2030, 0x2034, BidiClass.ET);
        Set(0x2035, 0x205E, BidiClass.ON); t[0x2044] = BidiClass.CS;
        t[0x205F] = BidiClass.WS;
        Set(0x2060, 0x206F, BidiClass.BN);
        t[0x2070] = BidiClass.EN; Set(0x2074, 0x2079, BidiClass.EN); t[0x207A] = BidiClass.ES; t[0x207B] = BidiClass.ES;
        Set(0x207C, 0x207E, BidiClass.ON);
        Set(0x2080, 0x2089, BidiClass.EN); t[0x208A] = BidiClass.ES; t[0x208B] = BidiClass.ES;
        Set(0x20A0, 0x20CF, BidiClass.ET);
        Set(0x2190, 0x2BFF, BidiClass.ON);
        t[0x3000] = BidiClass.WS;

        Set(0xFB1D, 0xFB4F, BidiClass.R); t[0xFB29] = BidiClass.ES;
        Set(0xFB50, 0xFDFF, BidiClass.AL);
        Set(0xFE00, 0xFE0F, BidiClass.NSM);
        Set(0xFE50, 0xFE6F, BidiClass.ON); t[0xFE50] = BidiClass.CS; t[0xFE52] = BidiClass.CS; t[0xFE55] = BidiClass.CS;
        Set(0xFE70, 0xFEFE, BidiClass.AL);
        t[0xFEFF] = BidiClass.BN;
        Set(0xFF01, 0xFF0F, BidiClass.ON); Set(0xFF10, 0xFF19, BidiClass.EN);
        // نشانگر داخلی «تعداد کل صفحات» مانند عدد رفتار می‌کند
        t[TotalPagesMarker] = BidiClass.EN;
        return t;
    }

    private static JoiningType[] BuildJoining()
    {
        var t = new JoiningType[0x0300];
        void Set(int from, int to, JoiningType j) { for (int i = from; i <= to; i++) t[i - 0x0600] = j; }

        Set(0x0622, 0x0625, JoiningType.R);
        Set(0x0626, 0x0626, JoiningType.D);
        Set(0x0627, 0x0627, JoiningType.R);
        Set(0x0628, 0x0628, JoiningType.D);
        Set(0x0629, 0x0629, JoiningType.R);
        Set(0x062A, 0x062E, JoiningType.D);
        Set(0x062F, 0x0632, JoiningType.R);
        Set(0x0633, 0x063F, JoiningType.D);
        Set(0x0640, 0x0640, JoiningType.C);
        Set(0x0641, 0x0647, JoiningType.D);
        Set(0x0648, 0x0648, JoiningType.R);
        Set(0x0649, 0x064A, JoiningType.D);
        Set(0x064B, 0x065F, JoiningType.T);
        Set(0x066E, 0x066F, JoiningType.D);
        Set(0x0670, 0x0670, JoiningType.T);
        Set(0x0671, 0x0673, JoiningType.R);
        Set(0x0675, 0x0677, JoiningType.R);
        Set(0x0678, 0x0687, JoiningType.D);
        Set(0x0688, 0x0699, JoiningType.R);
        Set(0x069A, 0x06BF, JoiningType.D);
        Set(0x06C0, 0x06C0, JoiningType.R);
        Set(0x06C1, 0x06C2, JoiningType.D);
        Set(0x06C3, 0x06CB, JoiningType.R);
        Set(0x06CC, 0x06CC, JoiningType.D);
        Set(0x06CD, 0x06CD, JoiningType.R);
        Set(0x06CE, 0x06CE, JoiningType.D);
        Set(0x06CF, 0x06CF, JoiningType.R);
        Set(0x06D0, 0x06D1, JoiningType.D);
        Set(0x06D2, 0x06D3, JoiningType.R);
        Set(0x06D5, 0x06D5, JoiningType.R);
        Set(0x0610, 0x061A, JoiningType.T);
        Set(0x06D6, 0x06DC, JoiningType.T);
        Set(0x06DF, 0x06E4, JoiningType.T);
        Set(0x06E7, 0x06E8, JoiningType.T);
        Set(0x06EA, 0x06ED, JoiningType.T);
        Set(0x06EE, 0x06EF, JoiningType.R);
        Set(0x06FA, 0x06FC, JoiningType.D);
        Set(0x06FF, 0x06FF, JoiningType.D);
        Set(0x0750, 0x077F, JoiningType.D);
        return t;
    }

    private static Dictionary<char, (char, char, char, char)> BuildForms()
    {
        var d = new Dictionary<char, (char, char, char, char)>();
        // (isol, fina, init, medi) — صفر یعنی وجود ندارد
        void R(char c, int isol) => d[c] = ((char)isol, (char)(isol + 1), '\0', '\0');
        void D(char c, int isol) => d[c] = ((char)isol, (char)(isol + 1), (char)(isol + 2), (char)(isol + 3));

        d['ء'] = ('ﺀ', '\0', '\0', '\0');
        R('آ', 0xFE81); R('أ', 0xFE83); R('ؤ', 0xFE85); R('إ', 0xFE87);
        D('ئ', 0xFE89); R('ا', 0xFE8D); D('ب', 0xFE8F); R('ة', 0xFE93);
        D('ت', 0xFE95); D('ث', 0xFE99); D('ج', 0xFE9D); D('ح', 0xFEA1);
        D('خ', 0xFEA5); R('د', 0xFEA9); R('ذ', 0xFEAB); R('ر', 0xFEAD);
        R('ز', 0xFEAF); D('س', 0xFEB1); D('ش', 0xFEB5); D('ص', 0xFEB9);
        D('ض', 0xFEBD); D('ط', 0xFEC1); D('ظ', 0xFEC5); D('ع', 0xFEC9);
        D('غ', 0xFECD); D('ف', 0xFED1); D('ق', 0xFED5); D('ك', 0xFED9);
        D('ل', 0xFEDD); D('م', 0xFEE1); D('ن', 0xFEE5); D('ه', 0xFEE9);
        R('و', 0xFEED); D('ي', 0xFEF1);
        d['ى'] = ('ﻯ', 'ﻰ', 'ﯨ', 'ﯩ');
        // حروف فارسی (Presentation Forms-A)
        D('پ', 0xFB56); D('چ', 0xFB7A); R('ژ', 0xFB8A);
        D('ک', 0xFB8E); D('گ', 0xFB92); D('ی', 0xFBFC);
        R('ۀ', 0xFBA4); D('ھ', 0xFBAA); R('ٱ', 0xFB50);
        return d;
    }

    /// <summary>لیگچر لام‌الف: (isol, fina) برای هر نوع الف.</summary>
    public static bool TryLamAlef(char alef, out char isol, out char fina)
    {
        switch (alef)
        {
            case 'آ': isol = 'ﻵ'; fina = 'ﻶ'; return true;
            case 'أ': isol = 'ﻷ'; fina = 'ﻸ'; return true;
            case 'إ': isol = 'ﻹ'; fina = 'ﻺ'; return true;
            case 'ا': isol = 'ﻻ'; fina = 'ﻼ'; return true;
            default: isol = fina = '\0'; return false;
        }
    }
}
