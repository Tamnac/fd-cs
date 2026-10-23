namespace Fd;

/// <summary>A -S constraint: <c>+n</c> at least, <c>-n</c> at most, <c>n</c> exactly.</summary>
readonly record struct SizeFilter(char Sign, ulong Limit)
{
    public static SizeFilter Parse(string text)
    {
        UsageException Invalid() => new($"invalid value '{text}' for '--size <size>': '{text}' is not a valid size constraint. See 'fd --help'.");

        int p = 0;
        char sign = '=';
        if (text.Length > 0 && text[0] is '+' or '-') sign = text[p++];

        int digitsStart = p;
        while (p < text.Length && char.IsAsciiDigit(text[p])) p++;
        if (p == digitsStart || !ulong.TryParse(text.AsSpan(digitsStart, p - digitsStart), out ulong quantity)) throw Invalid();

        ulong unit = text[p..].ToLowerInvariant() switch
        {
            "b" => 1,
            "k" or "kb" => 1000UL,
            "ki" or "kib" => 1UL << 10,
            "m" or "mb" => 1000UL * 1000,
            "mi" or "mib" => 1UL << 20,
            "g" or "gb" => 1000UL * 1000 * 1000,
            "gi" or "gib" => 1UL << 30,
            "t" or "tb" => 1000UL * 1000 * 1000 * 1000,
            "ti" or "tib" => 1UL << 40,
            _ => throw Invalid(),
        };
        if (quantity > ulong.MaxValue / unit) throw Invalid();
        return new SizeFilter(sign, quantity * unit);
    }

    public bool IsWithin(long size) => Sign switch
    {
        '+' => (ulong)size >= Limit,
        '-' => (ulong)size <= Limit,
        _ => (ulong)size == Limit,
    };
}
