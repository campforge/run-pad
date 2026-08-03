string[] b64 = { "SGVsbG8=", "!!!!", "AB==CD==", "SGVsbG8", "AAAA", "A===", "  SGVsbG8=  ", "SGVs bG8=", "ab==", "abc", "abcd", "" };
foreach (var s in b64)
{
    var buffer = new byte[s.Length];
    bool tryOk = Convert.TryFromBase64String(s, buffer, out int written);
    bool ctorOk;
    try { Convert.FromBase64String(s); ctorOk = true; } catch (FormatException) { ctorOk = false; }
    Console.WriteLine($"[{s}] tryFromBase64(buf={buffer.Length})={tryOk} written={written} fromBase64Str={ctorOk} match={tryOk == ctorOk}");
}
