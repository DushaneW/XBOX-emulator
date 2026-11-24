using System.Numerics;

namespace XboxPc.Emulation.Gpu.Nv2a;

/// <summary>
/// NV2A vertex program interpreter (Kelvin "transform program", vs.1.1-class ISA). Each instruction is 128 bits
/// (4 words; word 0 unused). Fields (word, bit, width) as documented by xemu's vsh.c / nouveau:
/// ILU (1,25,3) MAC (1,21,4) CONST (1,13,8) V (1,9,4); input A neg/swizzle (1,8..0), reg (2,28,4), mux (2,26,2);
/// input B neg/swizzle (2,25..17), reg (2,13,4), mux (2,11,2); input C neg/swizzle (2,10..2), reg high (2,0,2) +
/// low (3,30,2), mux (3,28,2); outputs: MAC temp mask (3,24,4), temp reg (3,20,4), ILU temp mask (3,16,4), output
/// mask (3,12,4), output-or-constant (3,11), output address (3,3,8), output source ILU/MAC (3,2), A0 relative (3,1),
/// final (3,0). Operand mux: 1 = temp R, 2 = input v, 3 = constant c. Hardware constant index = D3D index + 96.
/// </summary>
public sealed class VertexProgram
{
    public const int Slots = 136, Constants = 192, Inputs = 16, Temps = 12, Outputs = 13;

    /// <summary>Output register numbers.</summary>
    public const int OPos = 0, OD0 = 3, OD1 = 4, OFog = 5, OPts = 6, OB0 = 7, OB1 = 8, OT0 = 9;

    public uint[,] Program { get; } = new uint[Slots, 4];
    public Vector4[] Constant { get; } = new Vector4[Constants];

    private enum Mac { Nop, Mov, Mul, Add, Mad, Dp3, Dph, Dp4, Dst, Min, Max, Slt, Sge, Arl }

    private enum Ilu { Nop, Mov, Rcp, Rcc, Rsq, Exp, Log, Lit }

    private static uint Field(uint[,] p, int slot, int word, int bit, int width) => (p[slot, word] >> bit) & ((1u << width) - 1);

    /// <summary>
    /// Runs the program from <paramref name="start"/> for one vertex. Returns the 13 output registers (unwritten
    /// colours default to (0,0,0,1), others to 0). Writes to constants (c[] outputs) are applied only when
    /// <paramref name="constantWriteEnable"/> is set.
    /// </summary>
    public Vector4[] Execute(Vector4[] v, int start, bool constantWriteEnable, Action<string>? report = null)
    {
        var o = new Vector4[Outputs];
        o[OD0] = o[OD1] = o[OB0] = o[OB1] = new Vector4(0, 0, 0, 1);
        var r = new Vector4[Temps];
        var a0 = 0;
        var p = Program;

        for (var slot = start; slot < Slots; slot++)
        {
            var mac = (Mac)Field(p, slot, 1, 21, 4);
            var ilu = (Ilu)Field(p, slot, 1, 25, 3);
            var constIndex = (int)Field(p, slot, 1, 13, 8);
            if (Field(p, slot, 3, 1, 1) != 0) constIndex += a0;
            var input = (int)Field(p, slot, 1, 9, 4);

            Vector4 Operand(int negBit, int negWord, int swzWord, int swzBit, int reg, int mux)
            {
                var src = mux switch
                {
                    1 => reg == 12 ? o[OPos] : reg < Temps ? r[reg] : Vector4.Zero,
                    2 => v[input],
                    3 => Constant[Math.Clamp(constIndex, 0, Constants - 1)],
                    _ => Vector4.Zero,
                };
                var sx = (int)Field(p, slot, swzWord, swzBit + 6, 2);
                var sy = (int)Field(p, slot, swzWord, swzBit + 4, 2);
                var sz = (int)Field(p, slot, swzWord, swzBit + 2, 2);
                var sw = (int)Field(p, slot, swzWord, swzBit, 2);
                var result = new Vector4(src[sx], src[sy], src[sz], src[sw]);
                return Field(p, slot, negWord, negBit, 1) != 0 ? -result : result;
            }

            var a = Operand(8, 1, 1, 0, (int)Field(p, slot, 2, 28, 4), (int)Field(p, slot, 2, 26, 2));
            var b = Operand(25, 2, 2, 17, (int)Field(p, slot, 2, 13, 4), (int)Field(p, slot, 2, 11, 2));
            var cReg = (int)((Field(p, slot, 2, 0, 2) << 2) | Field(p, slot, 3, 30, 2));
            var c = Operand(10, 2, 2, 2, cReg, (int)Field(p, slot, 3, 28, 2));

            Vector4 macOut = default, iluOut = default;
            switch (mac)
            {
                case Mac.Nop: break;
                case Mac.Mov: macOut = a; break;
                case Mac.Mul: macOut = a * b; break;
                case Mac.Add: macOut = a + c; break;
                case Mac.Mad: macOut = (a * b) + c; break;
                case Mac.Dp3: macOut = new Vector4((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z)); break;
                case Mac.Dph: macOut = new Vector4((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + b.W); break;
                case Mac.Dp4: macOut = new Vector4(Vector4.Dot(a, b)); break;
                case Mac.Dst: macOut = new Vector4(1, a.Y * b.Y, a.Z, b.W); break;
                case Mac.Min: macOut = Vector4.Min(a, b); break;
                case Mac.Max: macOut = Vector4.Max(a, b); break;
                case Mac.Slt: macOut = new Vector4(a.X < b.X ? 1 : 0, a.Y < b.Y ? 1 : 0, a.Z < b.Z ? 1 : 0, a.W < b.W ? 1 : 0); break;
                case Mac.Sge: macOut = new Vector4(a.X >= b.X ? 1 : 0, a.Y >= b.Y ? 1 : 0, a.Z >= b.Z ? 1 : 0, a.W >= b.W ? 1 : 0); break;
                case Mac.Arl: a0 = (int)MathF.Floor(a.X); break;
                default: report?.Invoke($"NV2A vertex program: reserved MAC opcode {(int)mac} at slot {slot}"); break;
            }
            var x = c.X;
            switch (ilu)
            {
                case Ilu.Nop: break;
                case Ilu.Mov: iluOut = c; break;
                case Ilu.Rcp: iluOut = new Vector4(1f / x); break;
                case Ilu.Rcc:
                    {
                        var rc = 1f / x;
                        var mag = Math.Clamp(MathF.Abs(rc), 5.42101e-20f, 1.884467e19f);
                        iluOut = new Vector4(rc < 0 ? -mag : mag);
                        break;
                    }
                case Ilu.Rsq: iluOut = new Vector4(1f / MathF.Sqrt(MathF.Abs(x))); break;
                case Ilu.Exp:
                    {
                        var fl = MathF.Floor(x);
                        iluOut = new Vector4(MathF.Pow(2, fl), x - fl, MathF.Pow(2, x), 1);
                        break;
                    }
                case Ilu.Log:
                    {
                        var ax = MathF.Abs(x);
                        if (ax == 0) { iluOut = new Vector4(float.NegativeInfinity, 1, float.NegativeInfinity, 1); break; }
                        var e = MathF.Floor(MathF.Log2(ax));
                        iluOut = new Vector4(e, ax / MathF.Pow(2, e), MathF.Log2(ax), 1);
                        break;
                    }
                case Ilu.Lit:
                    {
                        var diffuse = MathF.Max(c.X, 0);
                        var power = Math.Clamp(c.W, -127.9961f, 127.9961f);
                        var specular = c.X > 0 ? MathF.Pow(MathF.Max(c.Y, 0), power) : 0;
                        iluOut = new Vector4(1, diffuse, specular, 1);
                        break;
                    }
            }

            var macMask = (int)Field(p, slot, 3, 24, 4);
            var iluMask = (int)Field(p, slot, 3, 16, 4);
            var tempReg = (int)Field(p, slot, 3, 20, 4);
            var macWritesTemp = mac != Mac.Nop && mac != Mac.Arl && macMask != 0;
            if (macWritesTemp && tempReg < Temps) Masked(ref r[tempReg], macOut, macMask);
            if (ilu != Ilu.Nop && iluMask != 0)
            {
                var iluReg = macWritesTemp ? 1 : tempReg; // both units writing temps: the ILU result goes to R1
                if (iluReg < Temps) Masked(ref r[iluReg], iluOut, iluMask);
            }
            var outMask = (int)Field(p, slot, 3, 12, 4);
            if (outMask != 0)
            {
                var value = Field(p, slot, 3, 2, 1) != 0 ? iluOut : macOut;
                var address = (int)Field(p, slot, 3, 3, 8);
                if (Field(p, slot, 3, 11, 1) != 0)
                {
                    if (address < Outputs) Masked(ref o[address], value, outMask);
                }
                else if (constantWriteEnable)
                {
                    if (address < Constants) Masked(ref Constant[address], value, outMask);
                }
                else report?.Invoke("NV2A vertex program wrote a constant while constant writes are disabled - ignored");
            }
            if (Field(p, slot, 3, 0, 1) != 0) return o;
        }
        report?.Invoke($"NV2A vertex program ran past slot {Slots - 1} without a final instruction (start {start})");
        return o;
    }

    private static void Masked(ref Vector4 dst, Vector4 value, int mask)
    {
        if ((mask & 8) != 0) dst.X = value.X;
        if ((mask & 4) != 0) dst.Y = value.Y;
        if ((mask & 2) != 0) dst.Z = value.Z;
        if ((mask & 1) != 0) dst.W = value.W;
    }
}
