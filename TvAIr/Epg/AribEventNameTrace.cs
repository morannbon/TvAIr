using System.Text;

namespace TvAIr.Epg;

internal static class AribEventNameTrace
{
    public static string Build(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "empty";

        var state = new TraceState();
        var parts = new List<string>();
        var run = new List<byte>();
        CodeSet runSet = CodeSet.Unknown;
        CharSize runSize = state.Size;

        void FlushRun()
        {
            if (run.Count == 0) return;
            var hex = string.Join(" ", run.Select(b => b.ToString("X2")));
            if (runSet == CodeSet.Alnum)
            {
                var ascii = new string(run.Select(ToMiddleAlnumChar).ToArray());
                var current = new string(run.Select(ToCurrentAlnumChar).ToArray());
                parts.Add($"Alnum[{hex}] size={runSize} current='{current}' middle='{ascii}'");
            }
            else if (runSet == CodeSet.PropAlnum)
            {
                var ascii = new string(run.Select(ToMiddleAlnumChar).ToArray());
                parts.Add($"PropAlnum[{hex}] size={runSize} ascii='{ascii}'");
            }
            else
            {
                parts.Add($"{Name(runSet)}[{hex}]");
            }
            run.Clear();
            runSet = CodeSet.Unknown;
            runSize = state.Size;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0x89)
            {
                FlushRun();
                state.Size = CharSize.Middle;
                parts.Add("MSZ(size=Middle)");
                continue;
            }
            if (b == 0x8A)
            {
                FlushRun();
                state.Size = CharSize.Normal;
                parts.Add("NSZ(size=Normal)");
                continue;
            }
            if (b == 0x0E)
            {
                FlushRun();
                state.Gl = 1;
                parts.Add($"LS1(GL=G1:{Name(state.G[1])})");
                continue;
            }
            if (b == 0x0F)
            {
                FlushRun();
                state.Gl = 0;
                parts.Add($"LS0(GL=G0:{Name(state.G[0])})");
                continue;
            }
            if (b == 0x1B)
            {
                FlushRun();
                var escStart = i;
                var esc = ReadEsc(bytes, ref i, state);
                var escHex = string.Join(" ", bytes.Slice(escStart, i - escStart + 1).ToArray().Select(x => x.ToString("X2")));
                parts.Add($"ESC[{escHex}] {esc}");
                continue;
            }

            var set = b >= 0x21 && b <= 0x7E ? state.G[state.Gl] : CodeSet.Control;
            if (set == CodeSet.Kanji && b >= 0x21 && b <= 0x7E && i + 1 < bytes.Length && bytes[i + 1] >= 0x21 && bytes[i + 1] <= 0x7E)
            {
                if (runSet != set || runSize != state.Size) FlushRun();
                runSet = set;
                runSize = state.Size;
                run.Add(b);
                run.Add(bytes[++i]);
                continue;
            }

            if (set != runSet || runSize != state.Size) FlushRun();
            runSet = set;
            runSize = state.Size;
            run.Add(b);
        }
        FlushRun();

        var result = string.Join(" | ", parts);
        return result.Length <= 1800 ? result : result[..1800] + "...";
    }

    private static string ReadEsc(ReadOnlySpan<byte> bytes, ref int i, TraceState state)
    {
        if (i + 1 >= bytes.Length) return "truncated";
        i++;
        var b1 = bytes[i];
        var target = -1;

        if (b1 >= 0x28 && b1 <= 0x2B)
        {
            target = b1 - 0x28;
            if (i + 1 >= bytes.Length) return $"designate_G{target}_truncated";
            i++;
            var final = bytes[i];
            var set = MapFinal(final, multiByte: false);
            state.G[target] = set;
            return $"designate G{target}={Name(set)} final=0x{final:X2}";
        }

        if (b1 == 0x24)
        {
            if (i + 1 >= bytes.Length) return "multibyte_designate_truncated";
            i++;
            var b2 = bytes[i];
            if (b2 >= 0x28 && b2 <= 0x2B)
            {
                target = b2 - 0x28;
                if (i + 1 >= bytes.Length) return $"multibyte_designate_G{target}_truncated";
                i++;
                var final = bytes[i];
                var set = MapFinal(final, multiByte: true);
                state.G[target] = set;
                return $"designate G{target}={Name(set)} multi final=0x{final:X2}";
            }
            else
            {
                target = 0;
                var set = MapFinal(b2, multiByte: true);
                state.G[target] = set;
                return $"designate G0={Name(set)} multi final=0x{b2:X2}";
            }
        }

        return $"unknown first=0x{b1:X2}";
    }

    private static CodeSet MapFinal(byte final, bool multiByte)
    {
        if (multiByte)
        {
            return final switch
            {
                0x42 => CodeSet.Kanji,
                0x39 => CodeSet.JisKp1,
                0x3A => CodeSet.JisKp2,
                0x3B => CodeSet.AddSym,
                _ => CodeSet.Unknown,
            };
        }

        return final switch
        {
            0x4A => CodeSet.Alnum,
            0x36 => CodeSet.PropAlnum,
            0x30 => CodeSet.Hira,
            0x31 => CodeSet.Kata,
            0x49 => CodeSet.JisKata,
            0x70 => CodeSet.Macro,
            _ => CodeSet.Unknown,
        };
    }

    private static char ToCurrentAlnumChar(byte b)
    {
        if (b == 0x20) return '\u3000';
        if (b >= 0x21 && b <= 0x7E) return (char)(0xFF00 + b - 0x20);
        return '?';
    }

    private static char ToMiddleAlnumChar(byte b)
    {
        if (b == 0x20) return ' ';
        if (b == 0x5C) return '\u00A5';
        if (b == 0x7E) return '\u203E';
        if (b >= 0x21 && b <= 0x7D) return (char)b;
        return '?';
    }

    private static string Name(CodeSet set) => set switch
    {
        CodeSet.Kanji => "Kanji",
        CodeSet.Alnum => "Alnum",
        CodeSet.PropAlnum => "PropAlnum",
        CodeSet.Hira => "Hira",
        CodeSet.Kata => "Kata",
        CodeSet.JisKata => "JisKata",
        CodeSet.JisKp1 => "JisKp1",
        CodeSet.JisKp2 => "JisKp2",
        CodeSet.AddSym => "AddSym",
        CodeSet.Macro => "Macro",
        CodeSet.Control => "Control",
        _ => "Unknown",
    };

    private enum CodeSet
    {
        Kanji,
        Alnum,
        PropAlnum,
        Hira,
        Kata,
        JisKata,
        JisKp1,
        JisKp2,
        AddSym,
        Macro,
        Control,
        Unknown,
    }

    private enum CharSize
    {
        Normal,
        Middle,
    }

    private sealed class TraceState
    {
        public CodeSet[] G { get; } = { CodeSet.Kanji, CodeSet.Alnum, CodeSet.Hira, CodeSet.Kata };
        public int Gl { get; set; }
        public CharSize Size { get; set; } = CharSize.Normal;
    }
}
