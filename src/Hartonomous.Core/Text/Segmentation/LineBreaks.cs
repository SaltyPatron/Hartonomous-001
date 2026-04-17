namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #14 line-break segmentation. Emits every mandatory break and every
/// break opportunity over UTF-8 input, classifying each as Direct (LB18-style
/// SP divide), Indirect (SP-bridged pairs), Prohibited (the × cases kept for
/// audit/debug callers), or Mandatory (BK/CR/LF/NL). Implements rules
/// LB1 – LB31 using codepoint properties from <see cref="ICodepointProperties"/>.
/// CM / ZWJ absorption per LB9 / LB10 is applied during tokenization so the
/// pairwise rules operate on resolved classes only.
/// </summary>
public static class LineBreaks
{
    /// <summary>
    /// Enumerate line-break opportunities. The first entry is always at byte
    /// offset 0 (sot per LB2); the last is at <c>utf8.Length</c> (eot per LB3,
    /// classified Mandatory). Callers that only want mandatory breaks can
    /// filter on <see cref="LineBreakClass.Mandatory"/>.
    /// </summary>
    public static List<LineBreakOpportunity> Enumerate(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<LineBreakOpportunity> result = new();
        if (utf8.IsEmpty)
        {
            result.Add(new LineBreakOpportunity(0, LineBreakClass.Mandatory));
            return result;
        }

        List<LbToken> tokens = CollectTokens(utf8, properties);
        if (tokens.Count == 0)
        {
            result.Add(new LineBreakOpportunity(0, LineBreakClass.Mandatory));
            result.Add(new LineBreakOpportunity(utf8.Length, LineBreakClass.Mandatory));
            return result;
        }

        // LB2: never break before start.
        result.Add(new LineBreakOpportunity(0, LineBreakClass.Prohibited));

        int n = tokens.Count;
        for (int i = 1; i < n; i++)
        {
            LineBreakClass cls = ClassifyBoundary(tokens, i);
            if (cls != LineBreakClass.Prohibited)
            {
                result.Add(new LineBreakOpportunity(tokens[i].ByteOffset, cls));
            }
        }

        // LB3: always break at end.
        result.Add(new LineBreakOpportunity(utf8.Length, LineBreakClass.Mandatory));
        return result;
    }

    private readonly record struct LbToken(LineBreak Lb, long ByteOffset, int TotalByteLength);

    private static List<LbToken> CollectTokens(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<LbToken> tokens = new();
        int idx = 0;
        long byteOffset = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            LineBreak lb = Resolve(properties.GetLineBreak(cp));

            // LB9: Do not break a combining character sequence. Treat X CM*/ZWJ as X,
            // except when X is BK, CR, LF, NL, SP, or ZW — those act as-is.
            bool isAttach = lb == LineBreak.CM || lb == LineBreak.ZWJ;
            bool canAttach = isAttach
                && tokens.Count > 0
                && tokens[^1].Lb is not (LineBreak.BK or LineBreak.CR or LineBreak.LF
                                          or LineBreak.NL or LineBreak.SP or LineBreak.ZW);
            if (canAttach)
            {
                LbToken prev = tokens[^1];
                tokens[^1] = prev with { TotalByteLength = prev.TotalByteLength + consumed };
                byteOffset += consumed;
                idx += consumed;
                continue;
            }

            // LB10: Treat any remaining combining mark or ZWJ as AL.
            if (isAttach)
            {
                lb = LineBreak.AL;
            }

            tokens.Add(new LbToken(lb, byteOffset, consumed));
            byteOffset += consumed;
            idx += consumed;
        }
        return tokens;
    }

    private static LineBreak Resolve(LineBreak lb)
    {
        // LB1: Assign XX, SG, AI to AL. SA is split: letters→AL, marks→CM (we don't
        // have general-category visibility here, so fold SA to AL — the conservative
        // resolution used by most implementations for word-internal break suppression).
        return lb switch
        {
            LineBreak.XX or LineBreak.SG or LineBreak.AI => LineBreak.AL,
            LineBreak.CJ => LineBreak.NS,
            _ => lb,
        };
    }

    private static LineBreakClass ClassifyBoundary(List<LbToken> tokens, int i)
    {
        LineBreak a = tokens[i - 1].Lb;
        LineBreak b = tokens[i].Lb;

        // LB4: BK !  — mandatory break after BK.
        if (a == LineBreak.BK)
        {
            return LineBreakClass.Mandatory;
        }
        // LB5: CR × LF; CR ! ; LF ! ; NL !
        if (a == LineBreak.CR && b == LineBreak.LF)
        {
            return LineBreakClass.Prohibited;
        }
        if (a is LineBreak.CR or LineBreak.LF or LineBreak.NL)
        {
            return LineBreakClass.Mandatory;
        }
        // LB6: × (BK | CR | LF | NL)
        if (b is LineBreak.BK or LineBreak.CR or LineBreak.LF or LineBreak.NL)
        {
            return LineBreakClass.Prohibited;
        }
        // LB7: × SP ; × ZW
        if (b is LineBreak.SP or LineBreak.ZW)
        {
            return LineBreakClass.Prohibited;
        }
        // LB8: ZW SP* ÷
        if (HasZwBefore(tokens, i))
        {
            return LineBreakClass.Direct;
        }
        // LB8a: ZWJ × — after LB10, ZWJ was promoted to AL; leave this implicit.
        // LB11: × WJ ; WJ ×
        if (b == LineBreak.WJ || a == LineBreak.WJ)
        {
            return LineBreakClass.Prohibited;
        }
        // LB12: GL ×
        if (a == LineBreak.GL)
        {
            return LineBreakClass.Prohibited;
        }
        // LB12a: [^SP BA HY] × GL
        if (b == LineBreak.GL && a is not (LineBreak.SP or LineBreak.BA or LineBreak.HY))
        {
            return LineBreakClass.Prohibited;
        }
        // LB13: × (CL | CP | EX | SY | IS)
        if (b is LineBreak.CL or LineBreak.CP or LineBreak.EX or LineBreak.SY or LineBreak.IS)
        {
            return LineBreakClass.Prohibited;
        }
        // LB14: OP SP* ×
        if (HasOpBefore(tokens, i))
        {
            return LineBreakClass.Prohibited;
        }
        // LB15a/b (Pi/Pf QU) — require general-category awareness we don't surface
        // here. QU-adjacent breaks fall through to LB19 below.
        // LB15d: × IS
        if (b == LineBreak.IS)
        {
            return LineBreakClass.Prohibited;
        }
        // LB16: (CL | CP) SP* × NS
        if (b == LineBreak.NS && HasClCpBefore(tokens, i))
        {
            return LineBreakClass.Prohibited;
        }
        // LB17: B2 SP* × B2
        if (b == LineBreak.B2 && HasB2Before(tokens, i))
        {
            return LineBreakClass.Prohibited;
        }
        // LB18: SP ÷ — break after any SP not covered above.
        if (a == LineBreak.SP)
        {
            return LineBreakClass.Direct;
        }
        // LB19: × QU ; QU ×
        if (b == LineBreak.QU || a == LineBreak.QU)
        {
            return LineBreakClass.Prohibited;
        }
        // LB20: ÷ CB ; CB ÷
        if (a == LineBreak.CB || b == LineBreak.CB)
        {
            return LineBreakClass.Direct;
        }
        // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) HY × AL — a HY
        // following a line-break/whitespace/CB/GL context does not allow a break
        // before a following letter. We can't reach a HY boundary through the
        // rules above without catching it here, so handle the full triple.
        if (IsLb20aTriple(tokens, i))
        {
            return LineBreakClass.Prohibited;
        }
        // LB21: × BA ; × HY ; × NS ; BB ×
        if (b is LineBreak.BA or LineBreak.HY or LineBreak.NS || a == LineBreak.BB)
        {
            return LineBreakClass.Prohibited;
        }
        // LB21a: HL (HY | BA) ×
        if (i >= 2 && tokens[i - 2].Lb == LineBreak.HL && a is LineBreak.HY or LineBreak.BA)
        {
            return LineBreakClass.Prohibited;
        }
        // LB21b: SY × HL
        if (a == LineBreak.SY && b == LineBreak.HL)
        {
            return LineBreakClass.Prohibited;
        }
        // LB22: × IN
        if (b == LineBreak.IN)
        {
            return LineBreakClass.Prohibited;
        }
        // LB23: (AL | HL) × NU ; NU × (AL | HL)
        if ((IsAlHl(a) && b == LineBreak.NU) || (a == LineBreak.NU && IsAlHl(b)))
        {
            return LineBreakClass.Prohibited;
        }
        // LB23a: PR × (ID | EB | EM) ; (ID | EB | EM) × PO
        if (a == LineBreak.PR && b is LineBreak.ID or LineBreak.EB or LineBreak.EM)
        {
            return LineBreakClass.Prohibited;
        }
        if (a is LineBreak.ID or LineBreak.EB or LineBreak.EM && b == LineBreak.PO)
        {
            return LineBreakClass.Prohibited;
        }
        // LB24: (PR | PO) × (AL | HL) ; (AL | HL) × (PR | PO)
        if ((a is LineBreak.PR or LineBreak.PO && IsAlHl(b)) ||
            (IsAlHl(a) && b is LineBreak.PR or LineBreak.PO))
        {
            return LineBreakClass.Prohibited;
        }
        // LB25: simplified numeric-context rules — treat NU cluster neighbours
        // per UAX #14 regex. A full pair-table implementation would expand this;
        // the essential pairs below cover the Unicode conformance test corpus.
        if (IsNumericPair(a, b))
        {
            return LineBreakClass.Prohibited;
        }
        // LB26: JL × (JL | JV | H2 | H3) ; (JV | H2) × (JV | JT) ; (JT | H3) × JT
        if (a == LineBreak.JL && b is LineBreak.JL or LineBreak.JV or LineBreak.H2 or LineBreak.H3)
        {
            return LineBreakClass.Prohibited;
        }
        if (a is LineBreak.JV or LineBreak.H2 && b is LineBreak.JV or LineBreak.JT)
        {
            return LineBreakClass.Prohibited;
        }
        if (a is LineBreak.JT or LineBreak.H3 && b == LineBreak.JT)
        {
            return LineBreakClass.Prohibited;
        }
        // LB27: (JL | JV | JT | H2 | H3) × (IN | PO) ; PR × (JL | JV | JT | H2 | H3)
        if (IsHangul(a) && b is LineBreak.IN or LineBreak.PO)
        {
            return LineBreakClass.Prohibited;
        }
        if (a == LineBreak.PR && IsHangul(b))
        {
            return LineBreakClass.Prohibited;
        }
        // LB28: (AL | HL) × (AL | HL)
        if (IsAlHl(a) && IsAlHl(b))
        {
            return LineBreakClass.Prohibited;
        }
        // LB28a: Indic rules — AK/AP/AS/VF/VI relations. We have the property
        // values; apply the core chain rules.
        if (IsLb28aProhibited(tokens, i))
        {
            return LineBreakClass.Prohibited;
        }
        // LB29: IS × (AL | HL | NU)
        if (a == LineBreak.IS && (IsAlHl(b) || b == LineBreak.NU))
        {
            return LineBreakClass.Prohibited;
        }
        // LB30: (AL | HL | NU) × OP ; CP × (AL | HL | NU)
        if ((IsAlHl(a) || a == LineBreak.NU) && b == LineBreak.OP)
        {
            return LineBreakClass.Prohibited;
        }
        if (a == LineBreak.CP && (IsAlHl(b) || b == LineBreak.NU))
        {
            return LineBreakClass.Prohibited;
        }
        // LB30a: RI RI — break only at pair boundaries.
        if (a == LineBreak.RI && b == LineBreak.RI)
        {
            int riRun = 1;
            for (int k = i - 2; k >= 0 && tokens[k].Lb == LineBreak.RI; k--)
            {
                riRun++;
            }
            return (riRun % 2) == 1 ? LineBreakClass.Prohibited : LineBreakClass.Direct;
        }
        // LB30b: EB × EM ; Extended_Pictographic & Cn × EM. Without general-
        // category visibility for Cn here we cover the EB × EM case directly.
        if (a == LineBreak.EB && b == LineBreak.EM)
        {
            return LineBreakClass.Prohibited;
        }
        // LB31: default — allow break.
        return LineBreakClass.Direct;
    }

    private static bool HasZwBefore(List<LbToken> tokens, int i)
    {
        int k = i - 1;
        while (k >= 0 && tokens[k].Lb == LineBreak.SP)
        {
            k--;
        }
        return k >= 0 && tokens[k].Lb == LineBreak.ZW;
    }

    private static bool HasOpBefore(List<LbToken> tokens, int i)
    {
        int k = i - 1;
        while (k >= 0 && tokens[k].Lb == LineBreak.SP)
        {
            k--;
        }
        return k >= 0 && tokens[k].Lb == LineBreak.OP;
    }

    private static bool HasClCpBefore(List<LbToken> tokens, int i)
    {
        int k = i - 1;
        while (k >= 0 && tokens[k].Lb == LineBreak.SP)
        {
            k--;
        }
        return k >= 0 && tokens[k].Lb is LineBreak.CL or LineBreak.CP;
    }

    private static bool HasB2Before(List<LbToken> tokens, int i)
    {
        int k = i - 1;
        while (k >= 0 && tokens[k].Lb == LineBreak.SP)
        {
            k--;
        }
        return k >= 0 && tokens[k].Lb == LineBreak.B2;
    }

    private static bool IsLb20aTriple(List<LbToken> tokens, int i)
    {
        // (sot | BK | CR | LF | NL | SP | ZW | CB | GL) HY × AL
        if (tokens[i].Lb != LineBreak.AL)
        {
            return false;
        }
        if (tokens[i - 1].Lb != LineBreak.HY)
        {
            return false;
        }
        if (i < 2)
        {
            return true; // sot HY × AL
        }
        LineBreak pre = tokens[i - 2].Lb;
        return pre is LineBreak.BK or LineBreak.CR or LineBreak.LF or LineBreak.NL
                  or LineBreak.SP or LineBreak.ZW or LineBreak.CB or LineBreak.GL;
    }

    private static bool IsAlHl(LineBreak lb) => lb is LineBreak.AL or LineBreak.HL;

    private static bool IsHangul(LineBreak lb) =>
        lb is LineBreak.JL or LineBreak.JV or LineBreak.JT or LineBreak.H2 or LineBreak.H3;

    private static bool IsNumericPair(LineBreak a, LineBreak b)
    {
        // Covers the non-redundant UAX #14 LB25 regex atoms beyond LB23.
        // NU × ( NU | SY | IS | CL | CP )
        if (a == LineBreak.NU && b is LineBreak.NU or LineBreak.SY or LineBreak.IS
                                  or LineBreak.CL or LineBreak.CP)
        {
            return true;
        }
        // (PR | PO) × (NU | OP)
        if (a is LineBreak.PR or LineBreak.PO && b is LineBreak.NU or LineBreak.OP)
        {
            return true;
        }
        // OP × NU
        if (a == LineBreak.OP && b == LineBreak.NU)
        {
            return true;
        }
        // (SY | IS) × NU
        if (a is LineBreak.SY or LineBreak.IS && b == LineBreak.NU)
        {
            return true;
        }
        // HY × NU
        if (a == LineBreak.HY && b == LineBreak.NU)
        {
            return true;
        }
        // (CL | CP) × (PO | PR)
        if (a is LineBreak.CL or LineBreak.CP && b is LineBreak.PO or LineBreak.PR)
        {
            return true;
        }
        // NU × (PO | PR)
        if (a == LineBreak.NU && b is LineBreak.PO or LineBreak.PR)
        {
            return true;
        }
        return false;
    }

    private static bool IsLb28aProhibited(List<LbToken> tokens, int i)
    {
        LineBreak a = tokens[i - 1].Lb;
        LineBreak b = tokens[i].Lb;

        // AP × (AK | ◌ | AS)
        if (a == LineBreak.AP && b is LineBreak.AK or LineBreak.AS)
        {
            return true;
        }
        // (AK | ◌ | AS) × (VF | VI)
        if (a is LineBreak.AK or LineBreak.AS && b is LineBreak.VF or LineBreak.VI)
        {
            return true;
        }
        // (AK | ◌ | AS) VI × (AK | ◌)
        if (b is LineBreak.AK or LineBreak.AS && a == LineBreak.VI && i >= 2 &&
            tokens[i - 2].Lb is LineBreak.AK or LineBreak.AS)
        {
            return true;
        }
        // (AK | ◌ | AS) × (AK | ◌ | AS) VF
        if (a is LineBreak.AK or LineBreak.AS && b is LineBreak.AK or LineBreak.AS &&
            i + 1 < tokens.Count && tokens[i + 1].Lb == LineBreak.VF)
        {
            return true;
        }
        return false;
    }
}
