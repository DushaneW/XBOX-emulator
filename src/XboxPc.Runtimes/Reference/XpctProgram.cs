using System.Globalization;

namespace XboxPc.Runtimes.Reference;

/// <summary>
/// XPCT ("XBOX PC Test") is an original, tiny register-machine format used to prove the runtime contract end to end.
/// It is not related to any Xbox executable format.
/// </summary>
public enum OpCode
{
    Set,
    Mov,
    Add,
    Sub,
    Mul,
    AddI,
    Inc,
    Dec,
    Jmp,
    Jz,
    Jnz,
    Jlt,
    Out,
    Halt,
}

/// <summary>A, B: registers. Target: jump target index. Imm: immediate.</summary>
public readonly record struct Instruction(OpCode Op, int A, int B, long Imm, int SourceLine, int Target = 0);

public sealed class XpctProgram
{
    public const string Header = "XPCT 1";
    public const int RegisterCount = 8;
    public const int MaxInstructions = 4096;
    public const int MaxFileBytes = 64 * 1024;

    public required string Name { get; init; }
    public required IReadOnlyList<Instruction> Instructions { get; init; }

    public static (XpctProgram? Program, IReadOnlyList<string> Errors) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var errors = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header)
            return (null, [$"Line 1: missing header '{Header}'."]);

        var name = "Unnamed test program";
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = new List<(string[] Tokens, int Line)>();

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf(';', StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;
            var lineNo = i + 1;

            if (line.StartsWith(".name ", StringComparison.Ordinal)) { name = line[6..].Trim(); continue; }
            if (line.EndsWith(':'))
            {
                var label = line[..^1];
                if (label.Length == 0 || !label.All(c => char.IsLetterOrDigit(c) || c == '_')) errors.Add($"Line {lineNo}: invalid label '{label}'.");
                else if (!labels.TryAdd(label, pending.Count)) errors.Add($"Line {lineNo}: duplicate label '{label}'.");
                continue;
            }
            pending.Add((line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), lineNo));
        }

        if (pending.Count > MaxInstructions) errors.Add($"Program has {pending.Count} instructions; limit is {MaxInstructions}.");

        var instructions = new List<Instruction>(pending.Count);
        foreach (var (tokens, lineNo) in pending)
        {
            var result = ParseInstruction(tokens, lineNo, labels, out var error);
            if (error is not null) errors.Add(error);
            else instructions.Add(result);
        }

        if (instructions.Count == 0 && errors.Count == 0) errors.Add("Program contains no instructions.");
        return errors.Count > 0 ? (null, errors) : (new XpctProgram { Name = name, Instructions = instructions }, errors);
    }

    private static Instruction ParseInstruction(string[] t, int line, Dictionary<string, int> labels, out string? error)
    {
        string? err = null;
        var mnemonic = t[0].ToUpperInvariant();
        bool Arity(int n)
        {
            if (t.Length == n + 1) return true;
            return Fail($"'{t[0]}' expects {n} operand(s).");
        }
        bool Fail(string message) { err ??= $"Line {line}: {message}"; return false; }
        int Reg(int index)
        {
            var s = t[index];
            if (s.Length == 2 && (s[0] == 'r' || s[0] == 'R') && s[1] >= '0' && s[1] < '0' + RegisterCount) return s[1] - '0';
            Fail($"'{s}' is not a register (r0-r{RegisterCount - 1}).");
            return 0;
        }
        long Imm(int index)
        {
            if (long.TryParse(t[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) return v;
            Fail($"'{t[index]}' is not an integer.");
            return 0;
        }
        int Label(int index)
        {
            if (labels.TryGetValue(t[index], out var target)) return target;
            Fail($"unknown label '{t[index]}'.");
            return 0;
        }

        var arity = mnemonic switch
        {
            "HALT" => 0,
            "INC" or "DEC" or "JMP" or "OUT" => 1,
            "SET" or "MOV" or "ADD" or "SUB" or "MUL" or "ADDI" or "JZ" or "JNZ" => 2,
            "JLT" => 3,
            _ => -1,
        };
        error = null;
        if (arity < 0) { Fail($"unknown instruction '{t[0]}'."); error = err; return default; }
        if (!Arity(arity)) { error = err; return default; }

        Instruction result = mnemonic switch
        {
            "SET" => new(OpCode.Set, Reg(1), 0, Imm(2), line),
            "MOV" => new(OpCode.Mov, Reg(1), Reg(2), 0, line),
            "ADD" => new(OpCode.Add, Reg(1), Reg(2), 0, line),
            "SUB" => new(OpCode.Sub, Reg(1), Reg(2), 0, line),
            "MUL" => new(OpCode.Mul, Reg(1), Reg(2), 0, line),
            "ADDI" => new(OpCode.AddI, Reg(1), 0, Imm(2), line),
            "INC" => new(OpCode.Inc, Reg(1), 0, 0, line),
            "DEC" => new(OpCode.Dec, Reg(1), 0, 0, line),
            "JMP" => new(OpCode.Jmp, 0, 0, 0, line, Label(1)),
            "JZ" => new(OpCode.Jz, Reg(1), 0, 0, line, Label(2)),
            "JNZ" => new(OpCode.Jnz, Reg(1), 0, 0, line, Label(2)),
            "JLT" => new(OpCode.Jlt, Reg(1), Reg(2), 0, line, Label(3)),
            "OUT" => new(OpCode.Out, Reg(1), 0, 0, line),
            _ => new(OpCode.Halt, 0, 0, 0, line),
        };
        error = err;
        return result;
    }
}

public enum ExecutionOutcome
{
    Halted,
    BudgetExceeded,
    FellOffEnd,
    OutputLimitExceeded,
}

public sealed record ExecutionResult(ExecutionOutcome Outcome, long Steps, IReadOnlyList<long> Output);

public static class XpctInterpreter
{
    public const int MaxOutputValues = 100_000;

    /// <summary>Deterministic execution with a step budget. Arithmetic wraps (two's complement).</summary>
    public static ExecutionResult Run(XpctProgram program, long maxSteps, CancellationToken cancellationToken, Action<long>? onOutput = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        var r = new long[XpctProgram.RegisterCount];
        var output = new List<long>();
        var code = program.Instructions;
        var pc = 0;
        long steps = 0;

        while (true)
        {
            if (pc < 0 || pc >= code.Count) return new(ExecutionOutcome.FellOffEnd, steps, output);
            if (steps >= maxSteps) return new(ExecutionOutcome.BudgetExceeded, steps, output);
            if ((steps & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            steps++;

            var ins = code[pc];
            pc++;
            unchecked
            {
                switch (ins.Op)
                {
                    case OpCode.Set: r[ins.A] = ins.Imm; break;
                    case OpCode.Mov: r[ins.A] = r[ins.B]; break;
                    case OpCode.Add: r[ins.A] += r[ins.B]; break;
                    case OpCode.Sub: r[ins.A] -= r[ins.B]; break;
                    case OpCode.Mul: r[ins.A] *= r[ins.B]; break;
                    case OpCode.AddI: r[ins.A] += ins.Imm; break;
                    case OpCode.Inc: r[ins.A]++; break;
                    case OpCode.Dec: r[ins.A]--; break;
                    case OpCode.Jmp: pc = ins.Target; break;
                    case OpCode.Jz: if (r[ins.A] == 0) pc = ins.Target; break;
                    case OpCode.Jnz: if (r[ins.A] != 0) pc = ins.Target; break;
                    case OpCode.Jlt: if (r[ins.A] < r[ins.B]) pc = ins.Target; break;
                    case OpCode.Out:
                        if (output.Count >= MaxOutputValues) return new(ExecutionOutcome.OutputLimitExceeded, steps, output);
                        output.Add(r[ins.A]);
                        onOutput?.Invoke(r[ins.A]);
                        break;
                    case OpCode.Halt: return new(ExecutionOutcome.Halted, steps, output);
                    default: throw new InvalidOperationException($"Unknown opcode {ins.Op}.");
                }
            }
        }
    }
}
