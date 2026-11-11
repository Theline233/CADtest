using System.Globalization;
using System.Text;

public readonly record struct CadTextResult(string Text, bool RequiresReview, string? Reason);

public static class SafeCadText
{
    private static readonly Lazy<Encoding> KoreanMif = new(()=>{
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
    });
    public static string Symbols(string text) => ReadPlain(text).Text;
    private static string PercentSymbols(string text) => text.Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase)
        .Replace("%%d", "°", StringComparison.OrdinalIgnoreCase).Replace("%%p", "±", StringComparison.OrdinalIgnoreCase);

    private static bool Mif(string raw, int slash, out char value)
    {
        value = default;
        // Only the CP949 marker verified by the current drawings is supported.
        // Other MIF code pages remain visible and require review.
        if (slash + 8 > raw.Length || !raw.AsSpan(slash,4).SequenceEqual("\\M+3")) return false;
        if (!byte.TryParse(raw.AsSpan(slash+4,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var hi) ||
            !byte.TryParse(raw.AsSpan(slash+6,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var lo)) return false;
        try {
            var decoded = KoreanMif.Value.GetString([hi,lo]);
            if (decoded.Length != 1 || char.IsControl(decoded[0]) || char.IsSurrogate(decoded[0])) return false;
            value = decoded[0]; return true;
        } catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or NotSupportedException) { return false; }
    }

    // AutoCAD text uses a fixed-width \U+hhhh sequence: do not accept the
    // whitespace allowed by NumberStyles.HexNumber, or rescan decoded text.
    private static bool UnicodeUnit(string raw, int slash, out char value)
    {
        value = default;
        if (slash > raw.Length - 7 || raw[slash] != '\\' || raw[slash+1] != 'U' || raw[slash+2] != '+') return false;
        int number = 0;
        for (int j = slash + 3; j < slash + 7; j++)
        {
            char c = raw[j];
            int digit = c is >= '0' and <= '9' ? c - '0' :
                        c is >= 'A' and <= 'F' ? c - 'A' + 10 :
                        c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
            if (digit < 0) return false;
            number = (number << 4) | digit;
        }
        value = (char)number;
        return true;
    }

    private static bool UnsafeTextCategory(UnicodeCategory category) => category is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    public static CadTextResult ReadPlain(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new("",false,null);
        if (raw.Length > 1_000_000) return new(raw,true,"TEXT exceeds formatting limit; raw text retained");
        var result = new StringBuilder(raw.Length); string? issue = null;
        for(int i=0;i<raw.Length;){
            if(raw[i]=='\\' && i+1<raw.Length && raw[i+1]=='\\') { result.Append("\\\\"); i+=2; continue; }
            if(raw[i]=='\\' && i+2<raw.Length && raw[i+1] is 'U' or 'u' && raw[i+2]=='+'){
                if (!UnicodeUnit(raw,i,out var unit)) {
                    issue ??= "Unsupported or malformed TEXT Unicode encoding retained raw";
                    // Preserve the damaged token, but do not swallow a following escape.
                    int end = i + 3;
                    while (end < raw.Length && end < i + 7 && raw[end] != '\\') end++;
                    result.Append(raw,i,end-i); i=end; continue;
                }
                if (char.IsHighSurrogate(unit)) {
                    if (UnicodeUnit(raw,i+7,out var low) && char.IsLowSurrogate(low)) {
                        if (UnsafeTextCategory(Rune.GetUnicodeCategory(new Rune(unit,low)))) {
                            issue ??= "TEXT Unicode control or format character retained raw";
                            result.Append(raw,i,14);
                        } else { result.Append(unit); result.Append(low); }
                        i+=14; continue;
                    }
                    issue ??= "Unpaired TEXT Unicode surrogate retained raw";
                    result.Append(raw,i,7); i+=7; continue;
                }
                if (char.IsLowSurrogate(unit) || UnsafeTextCategory(char.GetUnicodeCategory(unit))) {
                    issue ??= "TEXT Unicode control, format character, or unpaired surrogate retained raw";
                    result.Append(raw,i,7); i+=7; continue;
                }
                result.Append(unit); i+=7; continue;
            }
            if(raw[i]=='\\' && i+2<raw.Length && raw[i+1]=='M' && raw[i+2]=='+'){
                if(Mif(raw,i,out var ch)){result.Append(ch);i+=8;continue;}
                issue ??= "Unsupported or malformed MIF encoding retained raw";
            }
            if (char.IsHighSurrogate(raw[i])) {
                if (i+1 < raw.Length && char.IsLowSurrogate(raw[i+1])) {
                    if (UnsafeTextCategory(Rune.GetUnicodeCategory(new Rune(raw[i],raw[i+1]))))
                        issue ??= "TEXT contains a control or format character";
                    result.Append(raw[i]); result.Append(raw[i+1]); i+=2; continue;
                }
                issue ??= "TEXT contains an unpaired surrogate";
            } else if (char.IsLowSurrogate(raw[i])) issue ??= "TEXT contains an unpaired surrogate";
            else if (UnsafeTextCategory(char.GetUnicodeCategory(raw[i]))) issue ??= "TEXT contains a control or format character";
            // Recognize percent codes only in the original input, never in a
            // Unicode-decoded value (which could turn literal %%p into ±).
            if (raw[i]=='%' && i+2<raw.Length && raw[i+1]=='%') {
                char symbol = char.ToLowerInvariant(raw[i+2]) switch { 'c' => 'Ø', 'd' => '°', 'p' => '±', _ => '\0' };
                if (symbol != '\0') { result.Append(symbol); i+=3; continue; }
            }
            result.Append(raw[i++]);
        }
        return new(result.ToString(),issue is not null,issue);
    }

    // Every branch advances the cursor. Malformed/unknown formatting keeps the
    // source visible, rather than guessing content or calling the library's parser.
    public static CadTextResult Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new("", false, null);
        if (raw.Length > 1_000_000) return new(PercentSymbols(raw), true, "MTEXT exceeds formatting limit; raw text retained");
        var output = new StringBuilder(raw.Length);
        string? reason = null;
        for (int i = 0; i < raw.Length;)
        {
            char c = raw[i++];
            if (c is '{' or '}') continue;
            if (c != '\\') { output.Append(c); continue; }
            if (i == raw.Length) { reason = "Trailing MTEXT escape"; break; }
            char code = raw[i++];
            if (code == 'M') {
                if (Mif(raw,i-2,out var ch)) { output.Append(ch); i+=6; continue; }
                reason="Unsupported or malformed MIF encoding retained raw";break;
            }
            if (code is '\\' or '{' or '}') { output.Append(code); continue; }
            if (code is 'P' or 'n') { output.Append('\n'); continue; }
            if (code == '~') { output.Append(' '); continue; }
            if ("LlOoKk".Contains(code)) continue;
            if (code == 'U' && i + 5 <= raw.Length && raw[i] == '+' &&
                ushort.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unicode))
            { output.Append((char)unicode); i += 5; continue; }
            if ("AaCcFfHhWwTtQqp".Contains(code))
            {
                int end = i;
                while (end < raw.Length && raw[end] is not (';' or '{' or '}' or '\\' or '\r' or '\n')) end++;
                if (end == raw.Length || raw[end] != ';') { reason = $"Unterminated MTEXT \\{code} formatting"; break; }
                i = end + 1;
                continue;
            }
            reason = code == 'S' ? "Stacked MTEXT kept raw; fraction/quantity semantics require review" : $"Unsupported MTEXT escape \\{code}";
            break;
        }
        return reason is null ? new(PercentSymbols(output.ToString()), false, null) : new(PercentSymbols(raw), true, reason);
    }
}
