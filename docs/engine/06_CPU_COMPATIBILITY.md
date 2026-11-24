# CPU compatibility report: Pentium III model (M2)

Oracle: the host CPU (AMD Ryzen 7 250, x86-64). The same instruction bytes are executed natively and by the
interpreter (`[rcx+disp]` encodes identically to `[ecx+disp]`), and result registers/memory plus every
architecturally **defined** flag are compared. Undefined flags are masked per the Intel SDM, never "fixed".
Comparisons executed in the M2 suite: ~274 000 integer + 1 252 666 x87/SSE/MMX (counted from test output).

## Integer (IA-32)
| Area | Status | Verification |
|---|---|---|
| ADD/OR/ADC/SBB/AND/SUB/XOR/CMP/TEST (8/16/32, reg/mem/imm forms) | Implemented | differential, 0 mismatches |
| INC/DEC/NEG/NOT, MUL/IMUL (1/2/3-op), DIV/IDIV incl. #DE | Implemented | differential + unit (#DE edge cases) |
| SHL/SHR/SAR/ROL/ROR/RCL/RCR, SHLD/SHRD | Implemented | differential (count 0/1/>1 flag rules) |
| BT/BTS/BTR/BTC, BSF/BSR, BSWAP, MOVZX/MOVSX, SETcc, CMOVcc, XCHG/XADD/CMPXCHG/CMPXCHG8B | Implemented | differential / unit |
| Control flow, stack, string ops with REP/REPE/REPNE, LEA, CDQ/CWDE, LAHF/SAHF, PUSHFD/POPFD | Implemented | unit tests |
| Prefixes 66/F2/F3/F0/segment (FS/GS bases); 67 (16-bit addressing) | 67 not implemented (stops) | unit |
| CPUID (P-III signature 0x68A; advertises only implemented features), RDTSC (virtual cycles), RDMSR 0x10/0x2A | Implemented | unit |
| Segment loads, far CALL/JMP, IN/OUT to real devices, privileged control registers | Not implemented (stop) |, |

## x87 FPU
| Area | Status | Verification |
|---|---|---|
| Register file in native 80-bit format; FLD/FSTP m80, FILD/FISTP m64 exact | Implemented | differential: 3 000 random bit patterns each, 0 mismatches |
| FADD/FSUB/FSUBR/FMUL/FDIV/FDIVR (all register, memory, integer-memory and pop forms) | Implemented | differential at PC=53 and PC=24, ~3 600 pairs per form, 0 mismatches |
| Precision control 24/53 with correct single rounding (TwoSum/FMA residual tie-breaking) | Implemented | differential (caught and fixed a double-rounding bug) |
| FIST/FISTP m16/m32/m64 in all four rounding modes, overflow -> integer indefinite + IE | Implemented | differential |
| FCOM/FCOMP/FCOMPP/FUCOM*/FCOMI/FUCOMI(P), FTST, FXAM | Implemented | differential (C0-C3, EFLAGS) |
| FSQRT, FRNDINT (all RC), FCHS, FABS, FSCALE, FPREM, FPREM1 (quotient bits) | Implemented | differential, 0 mismatches |
| FSIN, FCOS, F2XM1 | Implemented | ≤1 ulp from host x87 (measured max) |
| FPTAN, FPATAN, FYL2X, FYL2XP1, FSINCOS, FXTRACT | Implemented | not differentially tested yet |
| Constants, FXCH, FFREE, FINCSTP/FDECSTP, FCMOVcc, FNINIT/FNCLEX, FLDCW/FNSTCW/FNSTSW, FNSTENV/FLDENV/FNSAVE/FRSTOR | Implemented | partly differential, rest unit |
| SNaN/QNaN propagation (x87 rules), stack overflow/underflow -> indefinite + IE/SF | Implemented | differential |
| **Known deviations** | PC=64 approximated by 53 bits; RC≠nearest ignored for + − x ÷ √; DE/UE/PE and C1 round-up not tracked; FPU IP/DP not stored by FNSTENV; results outside the double exponent range overflow early; a few subnormal results differ by 1 ulp from the host's double rounding (counted and reported by the tests); unmasked exceptions (#MF) stop execution; FBLD/FBSTP not implemented |, |

## SSE (SSE1) and MMX
| Area | Status | Verification |
|---|---|---|
| ADD/SUB/MUL/DIV/SQRT/MIN/MAX PS/SS, AND/ANDN/OR/XOR PS, CMPPS/CMPSS (8 predicates), SHUFPS, UNPCK(L/H)PS, MOVHLPS/MOVLHPS/MOVLPS/MOVHPS/MOVSS/MOVUPS/MOVAPS/MOVNTPS, MOVMSKPS | Implemented | differential, 3 000 vectors each, with and without FTZ, 0 mismatches |
| CVTSS2SI/CVTTSS2SI (all RC), CVTSI2SS, CVTPI2PS/CVT(T)PS2PI, COMISS/UCOMISS | Implemented | differential (MXCSR IE/ZE, EFLAGS) |
| RCPPS/RSQRTPS(SS) | Implemented (12-bit rounded) | within Intel bound 1.5·2⁻¹²; **not** bit-identical to Pentium III tables |
| LDMXCSR/STMXCSR (reserved bits -> #GP, DAZ absent), SFENCE, PREFETCHh | Implemented | unit |
| MOVAPS/MOVNTPS misalignment -> #GP | Implemented | unit |
| All MMX arithmetic/logic/compare/pack/unpack/shift (reg, mem, imm), EMMS, aliasing with x87 registers/tags | Implemented | differential (52 ops x reg/mem x 3 000) + unit |
| P-III MMX extensions: PSHUFW, PINSRW, PEXTRW, PMOVMSKB, PMIN/PMAX(UB/SW), PAVGB/W, PMULHUW, PSADBW, MASKMOVQ, MOVNTQ | Implemented | differential (MASKMOVQ unit only) |
| **Known deviations** | MXCSR RC ignored by arithmetic (conversions honour it); DE/PE not tracked; #XM not delivered; FXSAVE/FXRSTOR not implemented; SSE2+ encodings stop as "not present on Pentium III" |, |

## Exceptions and execution state
- Faults (#DE, #UD, #GP, #PF from unmapped/unemulated memory) restore EIP to the faulting instruction; ESP is untouched
  by a faulting PUSH; straddling writes fault before any byte is written. Traps (INT3, INT n) retire the instruction.
- **No IDT delivery and no paging** (see M2 report §4 for why this is correct for an HLE design and what will be
  needed).
