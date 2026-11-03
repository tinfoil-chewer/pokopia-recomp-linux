// SPDX-License-Identifier: MIT
// Ampere (Volta-family SASS) instructions into Ryujinx's IR. A partial of Ryujinx's InstEmit, so it reuses the
// translator's private helpers (comparisons, LOP3 truth tables, texture binding, constant/local/shared/global memory)
// and, for attribute I/O and S2R, Ryujinx's own Maxwell emitters on a transcoded 64-bit Maxwell opcode.
// Semantics notes marked "inferred" are read from the compiler's output, not from documentation.
using Nx.Shaders.Ampere;
using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitHelper;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitAluHelper;
using Op = Nx.Shaders.Ampere.Operand;
using IrOperand = Ryujinx.Graphics.Shader.IntermediateRepresentation.Operand;

namespace Ryujinx.Graphics.Shader.Instructions
{
    sealed class AmpereFrame
    {
        public Dictionary<int, IrOperand> Labels;   // instruction address -> label inside an inlined subroutine
        public IrOperand ReturnLabel;
        public int Depth;
    }

    static partial class InstEmit
    {
        sealed class AmpereSkip : Exception
        {
            public AmpereSkip(string why) : base(why ?? "not handled") { }
        }

        const int UGprBase = 4, UPredBase = 4 + 64;
        // NVN keeps graphics texture and image handles in c[2] (the bank a bound TEX names); Ryujinx calls that the default bank.
        const int AmpereTextureBank = 2;

        static Exception Unsup(string why = null) => new AmpereSkip(why);

        // ---- entry point ------------------------------------------------------------------------------------------

        internal static void EmitAmpere(EmitterContext ctx, AmpereProgram prog, SassInst i, AmpereFrame frame)
        {
            if (i.Pred == 7 && i.PredNeg) return;   // @!PT: never executes
            try
            {
                IrOperand guard = i.Pred == 7 ? null : Guard(ctx, i);
                // Control flow with a condition predicate ("@!P3 BRA P5, target", "BRA.U !UP0, target"): taken when the guard
                // and every condition hold (inferred: the condition is ANDed with the guard).
                if (i.Opcode is "BRA" or "CALL" or "BRX" or "BRXU" or "RET" or "EXIT" or "KILL")
                {
                    foreach (Op c in i.Operands.TakeWhile(IsPredKind))
                    {
                        if (c.Reg == 7 && !c.Not) continue;
                        IrOperand cv = PredSrc(ctx, c);
                        guard = guard == null ? cv : ctx.Add(Instruction.LogicalAnd, Local(), guard, cv);
                    }
                }
                switch (i.Opcode)
                {
                    case "BRA": EmitBranch(ctx, guard, TargetLabel(ctx, BranchTarget(i), frame)); return;
                    case "CALL": EmitCall(ctx, prog, i, guard, frame); return;
                    case "BRX": case "BRXU": EmitBrx(ctx, prog, i, guard, frame); return;
                    case "RET":
                        if (frame == null) throw Unsup("RET outside an inlined subroutine");
                        EmitBranch(ctx, guard, frame.ReturnLabel);
                        return;
                }
                IrOperand skip = null;
                if (guard != null)
                {
                    skip = Label();
                    ctx.BranchIfFalse(skip, guard);
                }
                EmitAmpereOp(ctx, prog, i);
                if (skip != null) ctx.MarkLabel(skip);
            }
            catch (AmpereSkip e)
            {
                prog.Fail(i, e.Message);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException or FormatException)
            {
                prog.Fail(i, $"{e.GetType().Name} {e.Message}");
            }
        }

        static IrOperand Guard(EmitterContext ctx, SassInst i)
        {
            IrOperand p = i.PredIsUniform ? Register(UPredBase + i.Pred, RegisterType.Flag) : Register(i.Pred, RegisterType.Predicate);
            return i.PredNeg ? ctx.Add(Instruction.LogicalNot, Local(), p) : p;
        }

        static int BranchTarget(SassInst i)
        {
            Op l = i.Operands.FirstOrDefault(o => o.Kind == OperandKind.Label) ?? throw Unsup("no branch target");
            return (int)l.Offset;
        }

        static IrOperand TargetLabel(EmitterContext ctx, int address, AmpereFrame frame)
        {
            if (frame != null)
            {
                if (frame.Labels.TryGetValue(address, out IrOperand l)) return l;
                throw Unsup("branch out of an inlined subroutine");
            }
            return ctx.GetLabel((ulong)address);
        }

        static void EmitBranch(EmitterContext ctx, IrOperand guard, IrOperand label)
        {
            if (guard == null) ctx.Branch(label);
            else ctx.BranchIfTrue(label, guard);
        }

        static void EmitCall(EmitterContext ctx, AmpereProgram prog, SassInst i, IrOperand guard, AmpereFrame frame)
        {
            if (!prog.Subroutines.TryGetValue(i.Address, out var region))
            {
                // CALL.REL.NOINC to code without RET: for one invocation that is a jump (the pushed return is never popped).
                EmitBranch(ctx, guard, TargetLabel(ctx, BranchTarget(i), frame));
                return;
            }
            int depth = frame?.Depth ?? 0;
            if (depth >= 4) throw Unsup("subroutine nesting deeper than 4");
            // The callee returns through RET Rn; the caller loads the return address with "MOV Rx, <CALL + 0x10>".
            int idx = prog.IndexOf[i.Address];
            bool movFound = false;
            for (int k = idx - 1; k >= 0 && k >= idx - 12 && !movFound; k--)
            {
                SassInst x = prog.Insts[k];
                movFound = x.Opcode == "MOV" && x.Operands.Count >= 2 && x.Operands[1].Kind == OperandKind.Imm && (int)x.Operands[1].ImmBits == i.Address + 0x10;
            }
            if (!movFound) throw Unsup("subroutine call without a return address to this call site");

            List<SassInst> body = prog.Insts.Where(x => x.Address >= region.Start && x.Address <= region.End).ToList();
            AmpereFrame inner = new() { Labels = new Dictionary<int, IrOperand>(), ReturnLabel = Label(), Depth = depth + 1 };
            foreach (SassInst x in body) inner.Labels[x.Address] = Label();

            IrOperand skip = null;
            if (guard != null)
            {
                skip = Label();
                ctx.BranchIfFalse(skip, guard);
            }
            foreach (SassInst x in body)
            {
                ctx.MarkLabel(inner.Labels[x.Address]);
                EmitAmpere(ctx, prog, x, inner);
            }
            ctx.MarkLabel(inner.ReturnLabel);
            if (skip != null) ctx.MarkLabel(skip);
        }

        static void EmitBrx(EmitterContext ctx, AmpereProgram prog, SassInst i, IrOperand guard, AmpereFrame frame)
        {
            int[] targets = AmpereTranslator.BrxTargets(prog, i);
            if (targets.Length == 0) throw Unsup("no jump table");
            IrOperand skip = null;
            if (guard != null)
            {
                skip = Label();
                ctx.BranchIfFalse(skip, guard);
            }
            (Op sel, long rel) = AmpereTranslator.IndirectOperand(i);
            int baseAddr = i.Address + 0x10 + (int)rel;
            IrOperand value = ctx.IAdd(Src(ctx, sel), Const(baseAddr));
            for (int k = 0; k < targets.Length - 1; k++)
            {
                ctx.BranchIfTrue(TargetLabel(ctx, targets[k], frame), ctx.ICompareEqual(value, Const(targets[k])));
            }
            ctx.Branch(TargetLabel(ctx, targets[^1], frame));
            if (skip != null) ctx.MarkLabel(skip);
        }

        // ---- operands ---------------------------------------------------------------------------------------------

        static IrOperand Gpr(int r) => r >= 255 ? Const(0) : Register(r, RegisterType.Gpr);
        static IrOperand UGpr(int r) => r >= 63 ? Const(0) : Register(UGprBase + r, RegisterType.Flag);

        static bool HasSuffix(Op o, string s) => (o.Suffixes != null && o.Suffixes.Contains(s)) || (o.Kind == OperandKind.Mem && o.Text != null && System.Text.RegularExpressions.Regex.IsMatch(o.Text, @"R(\d+|Z)" + System.Text.RegularExpressions.Regex.Escape(s) + @"(?![0-9A-Z])"));

        /// Raw 32-bit value of a source operand (no modifiers), `word` = 0/1 for the low/high register of a pair.
        static IrOperand Raw(EmitterContext ctx, Op o, int word = 0)
        {
            switch (o.Kind)
            {
                case OperandKind.Reg: return o.Reg >= 255 ? Const(0) : Gpr(o.Reg + word);
                case OperandKind.UReg: return o.Reg >= 63 ? Const(0) : UGpr(o.Reg + word);
                case OperandKind.Imm:
                case OperandKind.FImm:
                    return word == 0 ? Const((int)(uint)o.ImmBits) : Const(o.Kind == OperandKind.Imm && (long)o.ImmBits < 0 ? -1 : 0);
                case OperandKind.CBuf: return LoadCbuf(ctx, o, word);
                case OperandKind.Pred:
                case OperandKind.UPred:
                    return PredSrc(ctx, o);
            }
            throw Unsup($"operand {o.Kind} {o.Text}");
        }

        static IrOperand LoadCbuf(EmitterContext ctx, Op o, int word)
        {
            long off = o.Offset + word * 4;
            if (!o.BankIsUReg && o.IndexReg < 0)
            {
                if ((off & 3) != 0) throw Unsup("unaligned constant buffer offset");
                return Cbuf(o.Bank, (int)(off >> 2));
            }
            IrOperand slot = o.BankIsUReg ? UGpr(o.Bank) : Const(o.Bank);
            IrOperand addr = Const((int)off);
            if (o.IndexReg >= 0) addr = ctx.IAdd(o.IndexIsUReg ? UGpr(o.IndexReg) : Gpr(o.IndexReg), addr);
            return EmitLoadConstant(ctx, slot, ctx.ShiftRightU32(addr, Const(2)));
        }

        static IrOperand PredSrc(EmitterContext ctx, Op o)
        {
            IrOperand p = o.Reg == 7 ? Const(IrConsts.True)
                : o.Kind == OperandKind.UPred ? Register(UPredBase + o.Reg, RegisterType.Flag) : Register(o.Reg, RegisterType.Predicate);
            return o.Not ? ctx.Add(Instruction.LogicalNot, Local(), p) : p;
        }

        static IrOperand Src(EmitterContext ctx, Op o)
        {
            if (o.Kind is OperandKind.Pred or OperandKind.UPred) return PredSrc(ctx, o);
            IrOperand v = Raw(ctx, o);
            if (o.Kind == OperandKind.Imm || o.Kind == OperandKind.FImm) return v;   // sign already folded in
            if (o.Abs) v = ctx.IAbsolute(v);
            if (o.Neg) v = ctx.INegate(v);
            if (o.Not) v = ctx.BitwiseNot(v);
            return v;
        }

        static IrOperand FSrc(EmitterContext ctx, Op o)
        {
            if (o.Kind == OperandKind.Imm) throw Unsup("integer immediate in a float operand");
            IrOperand v = Raw(ctx, o);
            if (o.Kind == OperandKind.FImm) return v;
            return ctx.FPAbsNeg(v, o.Abs, o.Neg);
        }

        static void Dst(EmitterContext ctx, Op o, IrOperand v)
        {
            switch (o.Kind)
            {
                case OperandKind.Reg: if (o.Reg < 255) ctx.Copy(Gpr(o.Reg), v); return;
                case OperandKind.UReg: if (o.Reg < 63) ctx.Copy(UGpr(o.Reg), v); return;
                case OperandKind.Pred: if (o.Reg < 7) ctx.Copy(Register(o.Reg, RegisterType.Predicate), v); return;
                case OperandKind.UPred: if (o.Reg < 7) ctx.Copy(Register(UPredBase + o.Reg, RegisterType.Flag), v); return;
            }
            throw Unsup($"destination {o.Kind}");
        }

        static void DstWord(EmitterContext ctx, Op o, int word, IrOperand v)
        {
            if (o.Kind == OperandKind.Reg && o.Reg + word < 255) ctx.Copy(Gpr(o.Reg + word), v);
            else if (o.Kind == OperandKind.UReg && o.Reg + word < 63) ctx.Copy(UGpr(o.Reg + word), v);
            else if (o.Kind is not (OperandKind.Reg or OperandKind.UReg)) throw Unsup($"destination {o.Kind}");
        }

        static bool IsPredKind(Op o) => o.Kind is OperandKind.Pred or OperandKind.UPred;
        static IrOperand Bool(EmitterContext ctx, IrOperand b) => ctx.ConditionalSelect(b, Const(1), Const(0));
        static bool Has(SassInst i, string m) => i.Mods.Contains(m);
        static int IndexOfMod(SassInst i, string m) { for (int k = 0; k < i.Mods.Count; k++) if (i.Mods[k] == m) return k; return -1; }

        // ---- dispatch ---------------------------------------------------------------------------------------------

        static void EmitAmpereOp(EmitterContext ctx, AmpereProgram prog, SassInst i)
        {
            IReadOnlyList<Op> o = i.Operands;
            string op = i.Opcode;
            // Uniform-datapath forms share the semantics of their vector counterparts; operands carry the register file.
            if (op.Length > 1 && op[0] == 'U' && op is "UMOV" or "UIADD3" or "UIMAD" or "USHF" or "ULEA" or "ULOP3" or "USEL" or "UPRMT" or "UPOPC" or "UFLO" or "UISETP" or "UPLOP3" or "UBREV" or "USGXT" or "UIMNMX")
                op = op[1..];
            switch (op)
            {
                // no-ops for a single invocation: scheduling, reconvergence and error barriers
                case "NOP": case "DEPBAR": case "BSSY": case "BSYNC": case "BREAK": case "WARPSYNC": case "ERRBAR": case "YIELD": case "CCTL":
                    return;
                case "EXIT":
                    if (ctx.PrepareForReturn()) ctx.Return();
                    return;
                case "KILL":
                    ctx.Discard();
                    return;
                case "BAR":
                    if (!Has(i, "SYNC") && !(i.Mods.Count == 0)) throw Unsup();
                    ctx.Barrier();
                    return;
                case "MEMBAR":
                    if (Has(i, "CTA")) ctx.GroupMemoryBarrier(); else ctx.MemoryBarrier();
                    return;

                // ---- moves and selects
                case "MOV": case "R2UR":
                    Dst(ctx, o[0], Src(ctx, o[1]));
                    return;
                case "SEL":
                    Dst(ctx, o[0], ctx.ConditionalSelect(Src(ctx, o[3]), Src(ctx, o[1]), Src(ctx, o[2])));
                    return;
                case "FSEL":
                    Dst(ctx, o[0], ctx.ConditionalSelect(Src(ctx, o[3]), FSrc(ctx, o[1]), FSrc(ctx, o[2])));
                    return;
                case "CS2R":
                    if (o[1].Text != "SRZ" && !o[1].Text.StartsWith("SR_CLOCK")) throw Unsup(o[1].Text);
                    DstWord(ctx, o[0], 0, Const(0));
                    if (!Has(i, "32")) DstWord(ctx, o[0], 1, Const(0));
                    return;
                case "ISBERD":
                    // ISBERD Rd, [Ra]: read of the internal stage buffer; Ryujinx (Maxwell Isberd) copies Ra to Rd
                    Dst(ctx, i.Operands[0], i.Operands[1].Kind == OperandKind.Mem ? Gpr(i.Operands[1].Reg) : Src(ctx, i.Operands[1]));
                    return;
                case "LEPC":
                    // The PC for an ATEXIT_PC handler ("LEPC R0; IADD3 R0, R0, off; BMOV.64 ATEXIT_PC, R0"): the handler (OUT.FINAL;
                    // BMOV.64 ATEXIT_PC, 0x0; EXIT) is the end-of-shader work the host does itself, so the address is never used.
                    Dst(ctx, i.Operands[0], Const(0));
                    return;
                case "BMOV" when Has(i, "64") && i.Operands.Count > 0 && i.Operands[0].Text == "ATEXIT_PC":
                    return;   // the exit handler is not run (see LEPC)
                case "S2R": case "S2UR":
                    EmitS2rAmpere(ctx, i);
                    return;
                case "P2R": case "UP2UR": EmitP2rAmpere(ctx, i); return;
                case "R2P": EmitR2pAmpere(ctx, i); return;

                // ---- float
                case "FADD":
                    Dst(ctx, o[0], FinishF(ctx, i, ctx.FPAdd(FSrc(ctx, o[1]), FSrc(ctx, o[2]))));
                    return;
                case "FMUL":
                    {
                        IrOperand r = ctx.FPMultiply(FSrc(ctx, o[1]), FSrc(ctx, o[2]));
                        float scale = Has(i, "M2") ? 2 : Has(i, "M4") ? 4 : Has(i, "M8") ? 8 : Has(i, "D2") ? 0.5f : Has(i, "D4") ? 0.25f : Has(i, "D8") ? 0.125f : 1;
                        if (scale != 1) r = ctx.FPMultiply(r, ConstF(scale));
                        Dst(ctx, o[0], FinishF(ctx, i, r));
                        return;
                    }
                case "FFMA":
                    Dst(ctx, o[0], FinishF(ctx, i, ctx.FPFusedMultiplyAdd(FSrc(ctx, o[1]), FSrc(ctx, o[2]), FSrc(ctx, o[3]))));
                    return;
                case "FMNMX":
                    {
                        IrOperand a = FSrc(ctx, o[1]), b = FSrc(ctx, o[2]);
                        Dst(ctx, o[0], ctx.ConditionalSelect(Src(ctx, o[3]), ctx.FPMinimum(a, b), ctx.FPMaximum(a, b)));
                        return;
                    }
                case "FSETP": EmitFsetpAmpere(ctx, i, setp: true); return;
                case "FSET": EmitFsetpAmpere(ctx, i, setp: false); return;
                case "FRND":
                    {
                        if (Has(i, "F64")) throw Unsup();
                        IrOperand a = FSrc(ctx, o[1]);
                        Dst(ctx, o[0], Has(i, "FLOOR") ? ctx.FPFloor(a) : Has(i, "CEIL") ? ctx.FPCeiling(a) : Has(i, "TRUNC") ? ctx.FPTruncate(a) : ctx.FPRound(a));
                        return;
                    }
                case "MUFU": EmitMufuAmpere(ctx, i); return;
                case "F2I": EmitF2iAmpere(ctx, i); return;
                case "I2F": EmitI2fAmpere(ctx, i); return;
                case "I2FP":
                    if (!Has(i, "F32")) throw Unsup();
                    Dst(ctx, o[0], Has(i, "U32") ? ctx.IConvertU32ToFP32(Src(ctx, o[1])) : ctx.IConvertS32ToFP32(Src(ctx, o[1])));
                    return;
                case "F2F": EmitF2fAmpere(ctx, i); return;
                case "F2FP": EmitF2fpAmpere(ctx, i); return;
                case "HADD2": case "HMUL2": case "HFMA2": case "HMNMX2": EmitHalfAmpere(ctx, i); return;
                case "HSETP2": case "HSET2": EmitHsetAmpere(ctx, i); return;
                case "BMOV" when Has(i, "32") && i.Operands.Any(x => x.Text == "MACTIVE"):
                    // "BMOV.32 B15, MACTIVE; BMOV.32.PQUAD MACTIVE, B15 ... BMOV.32 MACTIVE, B15": saves the active mask, makes
                    // the whole quad active around SHFL/FSWZADD derivative code, then restores it. The host runs quad helpers
                    // for derivatives itself, so the save and restore are dropped.
                    return;

                // ---- integer
                case "IADD3": EmitIadd3Ampere(ctx, i); return;
                case "IMAD": EmitImadAmpere(ctx, i); return;
                case "LEA": EmitLeaAmpere(ctx, i); return;
                case "SHF": EmitShfAmpere(ctx, i); return;
                case "LOP3": EmitLop3Ampere(ctx, i); return;
                case "PLOP3": EmitPlop3Ampere(ctx, i); return;
                case "ISETP": EmitIsetpAmpere(ctx, i); return;
                case "IMNMX":
                    {
                        IrOperand a = Src(ctx, o[1]), b = Src(ctx, o[2]);
                        bool u = Has(i, "U32");
                        IrOperand min = u ? ctx.IMinimumU32(a, b) : ctx.IMinimumS32(a, b);
                        IrOperand max = u ? ctx.IMaximumU32(a, b) : ctx.IMaximumS32(a, b);
                        Dst(ctx, o[0], ctx.ConditionalSelect(Src(ctx, o[3]), min, max));
                        return;
                    }
                case "IABS": Dst(ctx, o[0], ctx.IAbsolute(Src(ctx, o[1]))); return;
                case "POPC": Dst(ctx, o[0], ctx.BitCount(Src(ctx, o[1]))); return;
                case "BREV": Dst(ctx, o[0], ctx.BitfieldReverse(Src(ctx, o[1]))); return;
                case "FLO":
                    {
                        IrOperand a = Src(ctx, o[1]);
                        // .SH: the shift that brings the found bit to bit 31 (31 - msb), all ones when none: Ryujinx's Maxwell
                        // FLO.SH, FindLSB of the reversed value.
                        if (Has(i, "SH")) Dst(ctx, o[0], ctx.FindLSB(ctx.BitfieldReverse(a)));
                        else Dst(ctx, o[0], Has(i, "U32") ? ctx.FindMSBU32(a) : ctx.FindMSBS32(a));
                        return;
                    }
                case "SGXT":
                    {
                        IrOperand a = Src(ctx, o[1]), bits = Src(ctx, o[2]);
                        Dst(ctx, o[0], Has(i, "U32") ? ctx.BitfieldExtractU32(a, Const(0), bits) : ctx.BitfieldExtractS32(a, Const(0), bits));
                        return;
                    }
                case "PRMT": EmitPrmtAmpere(ctx, i); return;

                // ---- constants and memory
                case "LDC": case "ULDC": EmitLdcAmpere(ctx, i); return;
                case "LDS": EmitLdStAmpere(ctx, i, StorageKind.SharedMemory, load: true); return;
                case "STS": EmitLdStAmpere(ctx, i, StorageKind.SharedMemory, load: false); return;
                case "LDL": EmitLdStAmpere(ctx, i, StorageKind.LocalMemory, load: true); return;
                case "STL": EmitLdStAmpere(ctx, i, StorageKind.LocalMemory, load: false); return;
                case "LDG": EmitGlobalAmpere(ctx, i, load: true); return;
                case "STG": EmitGlobalAmpere(ctx, i, load: false); return;
                case "ATOMS": EmitAtomsAmpere(ctx, i); return;
                case "ATOMG": EmitAtomgAmpere(ctx, i); return;
                case "RED": EmitRedAmpere(ctx, i); return;

                // ---- attributes (Ryujinx's Maxwell emitters on transcoded opcodes)
                case "ALD": EmitAldAmpere(ctx, i); return;
                case "AST": EmitAstAmpere(ctx, i); return;
                case "IPA": EmitIpaAmpere(ctx, i); return;
                case "OUT": EmitOutAmpere(ctx, i); return;

                // ---- textures and images
                case "TEX": EmitTexAmpere(ctx, i, fetch: false); return;
                case "TLD": EmitTexAmpere(ctx, i, fetch: true); return;
                case "TLD4": EmitTld4Ampere(ctx, i); return;
                case "TXQ": EmitTxqAmpere(ctx, i); return;
                case "TXD": EmitTxdAmpere(ctx, i); return;
                case "TMML": EmitTmmlAmpere(ctx, i); return;
                case "FSWZADD": EmitFswzaddAmpere(ctx, i); return;
                case "SUST": EmitSustAmpere(ctx, i); return;
                case "SULD": EmitSuldAmpere(ctx, i); return;

                // ---- warp
                case "SHFL": EmitShflAmpere(ctx, i); return;
                case "VOTE": case "VOTEU": EmitVoteAmpere(ctx, i); return;
                case "REDUX": EmitReduxAmpere(ctx, i); return;
                case "MATCH": EmitMatchAmpere(ctx, i); return;
            }
            throw Unsup();
        }

        static IrOperand FinishF(EmitterContext ctx, SassInst i, IrOperand r) => Has(i, "SAT") ? ctx.FPSaturate(r) : r;

        // ---- comparisons ------------------------------------------------------------------------------------------

        static readonly string[] FCompNames = ["F", "LT", "EQ", "LE", "GT", "NE", "GE", "NUM", "NAN", "LTU", "EQU", "LEU", "GTU", "NEU", "GEU", "T"];
        static readonly string[] ICompNames = ["F", "LT", "EQ", "LE", "GT", "NE", "GE", "T"];

        static BoolOp BopOf(SassInst i) => Has(i, "OR") ? BoolOp.Or : Has(i, "XOR") ? BoolOp.Xor : Has(i, "AND") ? BoolOp.And : throw Unsup("no boolean op");

        static void EmitFsetpAmpere(EmitterContext ctx, SassInst i, bool setp)
        {
            int c = Array.FindIndex(FCompNames, n => i.Mods.Contains(n));
            if (c < 0) throw Unsup("no comparison");
            BoolOp bop = BopOf(i);
            IReadOnlyList<Op> o = i.Operands;
            if (setp)
            {
                // FSETP Pd, Pd2, a, b, Pc
                IrOperand cmp = GetFPComparison(ctx, (FComp)c, FSrc(ctx, o[2]), FSrc(ctx, o[3]));
                IrOperand pc = Src(ctx, o[4]);
                Dst(ctx, o[0], GetPredLogicalOp(ctx, bop, cmp, pc));
                if (!(o[1].Kind == OperandKind.Pred && o[1].Reg == 7))
                    Dst(ctx, o[1], GetPredLogicalOp(ctx, bop, ctx.Add(Instruction.LogicalNot, Local(), cmp), pc));
            }
            else
            {
                // FSET.BF Rd, a, b, Pc
                IrOperand cmp = GetFPComparison(ctx, (FComp)c, FSrc(ctx, o[1]), FSrc(ctx, o[2]));
                IrOperand res = GetPredLogicalOp(ctx, bop, cmp, Src(ctx, o[3]));
                Dst(ctx, o[0], Has(i, "BF") ? ctx.ConditionalSelect(res, ConstF(1), ConstF(0)) : ctx.ConditionalSelect(res, Const(-1), Const(0)));
            }
        }

        static void EmitIsetpAmpere(EmitterContext ctx, SassInst i)
        {
            if (Has(i, "EX")) throw Unsup("64-bit compare chain");
            int c = Array.FindIndex(ICompNames, n => i.Mods.Contains(n));
            if (c < 0) throw Unsup("no comparison");
            IReadOnlyList<Op> o = i.Operands;
            IrOperand cmp = GetIntComparison(ctx, (IComp)c, Src(ctx, o[2]), Src(ctx, o[3]), !Has(i, "U32"));
            BoolOp bop = BopOf(i);
            IrOperand pc = Src(ctx, o[4]);
            Dst(ctx, o[0], GetPredLogicalOp(ctx, bop, cmp, pc));
            if (!(IsPredKind(o[1]) && o[1].Reg == 7))
                Dst(ctx, o[1], GetPredLogicalOp(ctx, bop, ctx.Add(Instruction.LogicalNot, Local(), cmp), pc));
        }

        static void EmitPlop3Ampere(EmitterContext ctx, SassInst i)
        {
            // PLOP3.LUT Pd, Pd2, a, b, c, lut, lut2
            IReadOnlyList<Op> o = i.Operands;
            IrOperand a = ctx.ConditionalSelect(Src(ctx, o[2]), Const(-1), Const(0));
            IrOperand b = ctx.ConditionalSelect(Src(ctx, o[3]), Const(-1), Const(0));
            IrOperand c = ctx.ConditionalSelect(Src(ctx, o[4]), Const(-1), Const(0));
            int lut = (int)o[5].ImmBits & 0xff, lut2 = o.Count > 6 ? (int)o[6].ImmBits & 0xff : 0;
            Dst(ctx, o[0], ctx.ICompareNotEqual(Lop3Expression.GetFromTruthTable(ctx, a, b, c, lut), Const(0)));
            if (!(IsPredKind(o[1]) && o[1].Reg == 7))
                Dst(ctx, o[1], ctx.ICompareNotEqual(Lop3Expression.GetFromTruthTable(ctx, a, b, c, lut2), Const(0)));
        }

        static void EmitLop3Ampere(EmitterContext ctx, SassInst i)
        {
            // LOP3.LUT Rd, [Pd,] a, b, c, lut, Pin
            if (!Has(i, "LUT")) throw Unsup();
            IReadOnlyList<Op> o = i.Operands;
            int s = IsPredKind(o[1]) ? 2 : 1;
            IrOperand r = Lop3Expression.GetFromTruthTable(ctx, Src(ctx, o[s]), Src(ctx, o[s + 1]), Src(ctx, o[s + 2]), (int)o[s + 3].ImmBits & 0xff);
            Dst(ctx, o[0], r);
            if (s == 2) Dst(ctx, o[1], ctx.ICompareNotEqual(r, Const(0)));
        }

        // ---- integer arithmetic -----------------------------------------------------------------------------------

        static void EmitIadd3Ampere(EmitterContext ctx, SassInst i)
        {
            // IADD3[.X] Rd, [Pco0, [Pco1,]] a, b, c [, Pci0, Pci1]
            IReadOnlyList<Op> o = i.Operands;
            int k = 1;
            List<Op> carryOut = new();
            while (k < o.Count && IsPredKind(o[k])) carryOut.Add(o[k++]);
            IrOperand a = Src(ctx, o[k]), b = Src(ctx, o[k + 1]), c = Src(ctx, o[k + 2]);
            IrOperand s1 = ctx.IAdd(a, b);
            IrOperand carries = Bool(ctx, ctx.ICompareLessUnsigned(s1, a));
            IrOperand s2 = ctx.IAdd(s1, c);
            carries = ctx.IAdd(carries, Bool(ctx, ctx.ICompareLessUnsigned(s2, s1)));
            if (Has(i, "X"))
            {
                for (int n = k + 3; n < o.Count; n++)
                {
                    if (!IsPredKind(o[n])) continue;
                    IrOperand prev = s2;
                    s2 = ctx.IAdd(s2, Bool(ctx, Src(ctx, o[n])));
                    carries = ctx.IAdd(carries, Bool(ctx, ctx.ICompareLessUnsigned(s2, prev)));
                }
            }
            Dst(ctx, o[0], s2);
            if (carryOut.Count > 0) Dst(ctx, carryOut[0], ctx.ICompareNotEqual(ctx.BitwiseAnd(carries, Const(1)), Const(0)));
            if (carryOut.Count > 1) Dst(ctx, carryOut[1], ctx.ICompareNotEqual(ctx.BitwiseAnd(carries, Const(2)), Const(0)));
        }

        static void EmitImadAmpere(EmitterContext ctx, SassInst i)
        {
            // IMAD[.MOV|.SHL|.IADD|.HI|.WIDE|.X][.U32] Rd, [Pco,] a, b, c [, Pci]
            IReadOnlyList<Op> o = i.Operands;
            int k = 1;
            Op co = null;
            if (IsPredKind(o[1])) { co = o[1]; k = 2; }
            IrOperand a = Src(ctx, o[k]), b = Src(ctx, o[k + 1]);
            bool unsigned = Has(i, "U32");
            if (Has(i, "WIDE"))
            {
                Op cop = o[k + 2];
                IrOperand lo = ctx.IMultiply(a, b);
                IrOperand hi = unsigned ? ctx.MultiplyHighU32(a, b) : ctx.MultiplyHighS32(a, b);
                IrOperand clo = Raw(ctx, cop, 0), chi = Raw(ctx, cop, 1);
                if (cop.Neg || cop.Not || cop.Abs) throw Unsup("modified 64-bit addend");
                IrOperand rlo = ctx.IAdd(lo, clo);
                IrOperand carry = Bool(ctx, ctx.ICompareLessUnsigned(rlo, lo));
                IrOperand rhi = ctx.IAdd(ctx.IAdd(hi, chi), carry);
                DstWord(ctx, o[0], 0, rlo);
                DstWord(ctx, o[0], 1, rhi);
                if (co != null) throw Unsup("carry out");
                return;
            }
            IrOperand c = Src(ctx, o[k + 2]);
            IrOperand prod = Has(i, "HI") ? (unsigned ? ctx.MultiplyHighU32(a, b) : ctx.MultiplyHighS32(a, b)) : ctx.IMultiply(a, b);
            IrOperand r = ctx.IAdd(prod, c);
            IrOperand carries = co != null ? Bool(ctx, ctx.ICompareLessUnsigned(r, prod)) : null;
            if (Has(i, "X"))
            {
                for (int n = k + 3; n < o.Count; n++)
                {
                    if (!IsPredKind(o[n])) continue;
                    IrOperand prev = r;
                    r = ctx.IAdd(r, Bool(ctx, Src(ctx, o[n])));
                    if (carries != null) carries = ctx.IAdd(carries, Bool(ctx, ctx.ICompareLessUnsigned(r, prev)));
                }
            }
            Dst(ctx, o[0], r);
            if (co != null) Dst(ctx, co, ctx.ICompareNotEqual(carries, Const(0)));
        }

        static void EmitLeaAmpere(EmitterContext ctx, SassInst i)
        {
            // LEA Rd, [Pco,] a, b, shift [, Pci]       = (a << shift) + b
            // LEA.HI Rd, [Pco,] a, b, c, shift [, Pci] = high word of ({c:a} << shift) + b
            IReadOnlyList<Op> o = i.Operands;
            int k = 1;
            Op co = null;
            if (IsPredKind(o[1])) { co = o[1]; k = 2; }
            IrOperand a = Src(ctx, o[k]), b = Src(ctx, o[k + 1]);
            bool hi = Has(i, "HI");
            Op shOp = o[hi ? k + 3 : k + 2];
            if (shOp.Kind != OperandKind.Imm) throw Unsup("register shift");
            int sh = (int)shOp.ImmBits & 31;
            IrOperand shifted;
            if (hi)
            {
                IrOperand c = Src(ctx, o[k + 2]);
                shifted = sh == 0 ? c : ctx.BitwiseOr(ctx.ShiftLeft(c, Const(sh)), ctx.ShiftRightU32(a, Const(32 - sh)));
            }
            else
            {
                shifted = ctx.ShiftLeft(a, Const(sh));
            }
            IrOperand r = ctx.IAdd(shifted, b);
            IrOperand carries = co != null ? Bool(ctx, ctx.ICompareLessUnsigned(r, b)) : null;
            if (Has(i, "X"))
            {
                for (int n = (hi ? k + 4 : k + 3); n < o.Count; n++)
                {
                    if (!IsPredKind(o[n])) continue;
                    IrOperand prev = r;
                    r = ctx.IAdd(r, Bool(ctx, Src(ctx, o[n])));
                    if (carries != null) carries = ctx.IAdd(carries, Bool(ctx, ctx.ICompareLessUnsigned(r, prev)));
                }
            }
            Dst(ctx, o[0], r);
            if (co != null) Dst(ctx, co, ctx.ICompareNotEqual(carries, Const(0)));
        }

        static void EmitShfAmpere(EmitterContext ctx, SassInst i)
        {
            // SHF.L|R.U32|S32[.HI][.W] Rd, lo, shift, hi: funnel shift of the 64-bit {hi:lo}.
            if (Has(i, "U64") || Has(i, "S64")) throw Unsup("64-bit funnel shift");
            IReadOnlyList<Op> o = i.Operands;
            IrOperand lo = Src(ctx, o[1]), hiw = Src(ctx, o[3]);
            bool left = Has(i, "L"), high = Has(i, "HI"), signed = Has(i, "S32");
            Op shOp = o[2];
            IrOperand r;
            if (shOp.Kind == OperandKind.Imm)
            {
                int s = (int)shOp.ImmBits;
                s = Has(i, "W") ? s & 31 : Math.Min(s, 32);
                if (left)
                {
                    r = high ? (s == 0 ? hiw : s == 32 ? lo : ctx.BitwiseOr(ctx.ShiftLeft(hiw, Const(s)), ctx.ShiftRightU32(lo, Const(32 - s))))
                             : (s == 32 ? Const(0) : ctx.ShiftLeft(lo, Const(s)));
                }
                else if (high)
                {
                    r = s == 32 ? (signed ? ctx.ShiftRightS32(hiw, Const(31)) : Const(0)) : signed ? ctx.ShiftRightS32(hiw, Const(s)) : ctx.ShiftRightU32(hiw, Const(s));
                }
                else
                {
                    r = s == 0 ? lo : s == 32 ? hiw : ctx.BitwiseOr(ctx.ShiftRightU32(lo, Const(s)), ctx.ShiftLeft(hiw, Const(32 - s)));
                }
            }
            else
            {
                // Register shift: amount masked to 0..31 (exact for .W; without .W amounts >= 32 differ, not seen in practice).
                IrOperand s = ctx.BitwiseAnd(Src(ctx, shOp), Const(31));
                IrOperand zero = ctx.ICompareEqual(s, Const(0));
                IrOperand inv = ctx.ISubtract(Const(32), s);
                if (left)
                {
                    r = high ? ctx.ConditionalSelect(zero, hiw, ctx.BitwiseOr(ctx.ShiftLeft(hiw, s), ctx.ShiftRightU32(lo, inv)))
                             : ctx.ShiftLeft(lo, s);
                }
                else if (high)
                {
                    r = signed ? ctx.ShiftRightS32(hiw, s) : ctx.ShiftRightU32(hiw, s);
                }
                else
                {
                    r = ctx.ConditionalSelect(zero, lo, ctx.BitwiseOr(ctx.ShiftRightU32(lo, s), ctx.ShiftLeft(hiw, inv)));
                }
            }
            Dst(ctx, o[0], r);
        }

        static void EmitPrmtAmpere(EmitterContext ctx, SassInst i)
        {
            // PRMT Rd, a, sel, b: byte k of the result = byte (sel nibble k & 7) of {b:a}; nibble bit 3 replicates its sign.
            if (i.Mods.Count != 0) throw Unsup("PRMT mode");
            IReadOnlyList<Op> o = i.Operands;
            if (o[2].Kind != OperandKind.Imm) throw Unsup("register selector");
            int sel = (int)o[2].ImmBits;
            IrOperand a = Src(ctx, o[1]), b = Src(ctx, o[3]);
            IrOperand r = Const(0);
            for (int k = 0; k < 4; k++)
            {
                int nib = (sel >> (k * 4)) & 0xf;
                IrOperand src = (nib & 4) == 0 ? a : b;
                IrOperand v = ctx.BitfieldExtractU32(src, Const((nib & 3) * 8), Const(8));
                if ((nib & 8) != 0) v = ctx.BitwiseAnd(ctx.ShiftRightS32(ctx.ShiftLeft(v, Const(24)), Const(31)), Const(0xff));
                r = ctx.BitwiseOr(r, k == 0 ? v : ctx.ShiftLeft(v, Const(k * 8)));
            }
            Dst(ctx, o[0], r);
        }

        static void EmitP2rAmpere(EmitterContext ctx, SassInst i)
        {
            // P2R Rd, PR, a, mask: Rd = (a & ~mask) | (predicate bits & mask); .B1-.B3 select a byte.
            IReadOnlyList<Op> o = i.Operands;
            bool uni = o[1].Text == "UPR";   // UP2UR: the uniform predicates
            if (o[1].Text is not ("PR" or "UPR") || o[3].Kind != OperandKind.Imm) throw Unsup();
            int shift = Has(i, "B1") ? 8 : Has(i, "B2") ? 16 : Has(i, "B3") ? 24 : 0;
            int mask = ((int)o[3].ImmBits & 0x7f) << shift;
            IrOperand r = ctx.BitwiseAnd(Src(ctx, o[2]), Const(~mask));
            for (int p = 0; p < 7; p++)
            {
                if ((mask & (1 << (p + shift))) == 0) continue;
                IrOperand pr = uni ? Register(UPredBase + p, RegisterType.Flag) : Register(p, RegisterType.Predicate);
                r = ctx.BitwiseOr(r, ctx.ConditionalSelect(pr, Const(1 << (p + shift)), Const(0)));
            }
            Dst(ctx, o[0], r);
        }

        static void EmitR2pAmpere(EmitterContext ctx, SassInst i)
        {
            // R2P PR, a, mask
            IReadOnlyList<Op> o = i.Operands;
            if (o[0].Text != "PR" || o[2].Kind != OperandKind.Imm) throw Unsup();
            int shift = Has(i, "B1") ? 8 : Has(i, "B2") ? 16 : Has(i, "B3") ? 24 : 0;
            int mask = (int)o[2].ImmBits & 0x7f;
            IrOperand a = Src(ctx, o[1]);
            for (int p = 0; p < 7; p++)
            {
                if ((mask & (1 << p)) == 0) continue;
                ctx.Copy(Register(p, RegisterType.Predicate), ctx.ICompareNotEqual(ctx.BitwiseAnd(a, Const(1 << (p + shift))), Const(0)));
            }
        }

        // ---- conversions and special functions --------------------------------------------------------------------

        /// One lane of a 16-bit source: .H1 = high half, otherwise low.
        static IrOperand HalfLane(EmitterContext ctx, Op o)
        {
            if (o.Kind == OperandKind.FImm) return Raw(ctx, o);
            IrOperand v = Raw(ctx, o);
            v = HasSuffix(o, ".H1") ? ctx.UnpackHalf2x16High(v) : ctx.UnpackHalf2x16Low(v);
            return ctx.FPAbsNeg(v, o.Abs, o.Neg);
        }

        const float TwoPi = 6.28318530717958647692f;

        static void EmitMufuAmpere(EmitterContext ctx, SassInst i)
        {
            IReadOnlyList<Op> o = i.Operands;
            bool f16 = Has(i, "F16");
            IrOperand a = f16 ? HalfLane(ctx, o[1]) : FSrc(ctx, o[1]);
            IrOperand r;
            if (Has(i, "RCP")) r = ctx.FPReciprocal(a);
            else if (Has(i, "RSQ")) r = ctx.FPReciprocalSquareRoot(a);
            else if (Has(i, "SQRT")) r = ctx.FPSquareRoot(a);
            else if (Has(i, "EX2")) r = ctx.FPExponentB2(a);
            else if (Has(i, "LG2")) r = ctx.FPLogarithmB2(a);
            // Volta+ MUFU.SIN/COS take the angle in turns (sin(2*pi*x)): there is no RRO, the compiler emits
            // FMUL.RZ x, 1/(2*pi) before them (Mesa NAK builder.rs fsin/fcos, sm >= 70). Maxwell's MUFU took radians after RRO.
            else if (Has(i, "SIN")) r = ctx.FPSine(ctx.FPMultiply(a, ConstF(TwoPi)));
            else if (Has(i, "COS")) r = ctx.FPCosine(ctx.FPMultiply(a, ConstF(TwoPi)));
            else throw Unsup();
            if (f16) r = ctx.PackHalf2x16(r, ConstF(0));   // inferred: the F16 result is in the low half
            Dst(ctx, o[0], r);
        }

        static void EmitF2iAmpere(EmitterContext ctx, SassInst i)
        {
            if (Has(i, "F64") || Has(i, "U64") || Has(i, "S64")) throw Unsup("64-bit");
            IReadOnlyList<Op> o = i.Operands;
            IrOperand src = Has(i, "F16") ? HalfLane(ctx, o[1]) : FSrc(ctx, o[1]);
            src = Has(i, "TRUNC") ? ctx.FPTruncate(src) : Has(i, "FLOOR") ? ctx.FPFloor(src) : Has(i, "CEIL") ? ctx.FPCeiling(src) : ctx.FPRound(src);
            bool unsigned = Has(i, "U32") || Has(i, "U16") || Has(i, "U8");
            if (unsigned) src = ctx.FPMaximum(src, ConstF(0));
            IrOperand r = unsigned ? ctx.FP32ConvertToU32(src) : ctx.FP32ConvertToS32(src);
            if (Has(i, "U16")) r = ctx.IClampU32(r, Const(0), Const(0xffff));
            else if (Has(i, "U8")) r = ctx.IClampU32(r, Const(0), Const(0xff));
            else if (Has(i, "S16")) r = ctx.IClampS32(r, Const(short.MinValue), Const(short.MaxValue));
            else if (Has(i, "S8")) r = ctx.IClampS32(r, Const(sbyte.MinValue), Const(sbyte.MaxValue));
            Dst(ctx, o[0], r);
        }

        static void EmitI2fAmpere(EmitterContext ctx, SassInst i)
        {
            if (Has(i, "F64") || Has(i, "F16") || Has(i, "U64") || Has(i, "S64")) throw Unsup();
            IReadOnlyList<Op> o = i.Operands;
            IrOperand v = Raw(ctx, o[1]);
            bool unsigned = Has(i, "U8") || Has(i, "U16") || Has(i, "U32");
            int bits = Has(i, "U8") || Has(i, "S8") ? 8 : Has(i, "U16") || Has(i, "S16") ? 16 : 32;
            int byteSel = HasSuffix(o[1], ".B1") ? 1 : HasSuffix(o[1], ".B2") ? 2 : HasSuffix(o[1], ".B3") ? 3 : HasSuffix(o[1], ".H1") ? 2 : 0;
            if (bits < 32 || byteSel != 0)
                v = unsigned ? ctx.BitfieldExtractU32(v, Const(byteSel * 8), Const(bits)) : ctx.BitfieldExtractS32(v, Const(byteSel * 8), Const(bits));
            if (o[1].Abs) v = ctx.IAbsolute(v);
            if (o[1].Neg) v = ctx.INegate(v);
            Dst(ctx, o[0], unsigned ? ctx.IConvertU32ToFP32(v) : ctx.IConvertS32ToFP32(v));
        }

        static void EmitF2fAmpere(EmitterContext ctx, SassInst i)
        {
            IReadOnlyList<Op> o = i.Operands;
            int f16 = IndexOfMod(i, "F16"), f32 = IndexOfMod(i, "F32");
            if (f16 < 0 || f32 < 0 || Has(i, "F64")) throw Unsup();
            if (f16 < f32)
            {
                // F2F.F16.F32: f32 -> f16 in the low half
                Dst(ctx, o[0], ctx.PackHalf2x16(FSrc(ctx, o[1]), ConstF(0)));
            }
            else
            {
                Dst(ctx, o[0], HalfLane(ctx, o[1]));
            }
        }

        static void EmitF2fpAmpere(EmitterContext ctx, SassInst i)
        {
            IReadOnlyList<Op> o = i.Operands;
            if (Has(i, "PACK_AB"))
            {
                // {a -> high half, b -> low half}
                Dst(ctx, o[0], ctx.PackHalf2x16(FSrc(ctx, o[2]), FSrc(ctx, o[1])));
                return;
            }
            if (Has(i, "MERGE_C"))
            {
                // inferred from the compiler's use: Rd = {low half of c -> high half, f16(a) -> low half}
                IrOperand lo = ctx.BitwiseAnd(ctx.PackHalf2x16(FSrc(ctx, o[1]), ConstF(0)), Const(0xffff));
                Dst(ctx, o[0], ctx.BitwiseOr(lo, ctx.ShiftLeft(Src(ctx, o[2]), Const(16))));
                return;
            }
            throw Unsup();
        }

        /// Both lanes of a packed-half source. Immediates come as one operand (raw bits) or two FImm operands.
        static IrOperand[] HalfPair(EmitterContext ctx, IReadOnlyList<Op> o, ref int k)
        {
            Op x = o[k++];
            if (x.Kind is OperandKind.FImm or OperandKind.Imm)
            {
                if (k < o.Count && o[k].Kind == OperandKind.FImm && x.Kind == OperandKind.FImm)
                {
                    Op y = o[k++];
                    return [Raw(ctx, x), Raw(ctx, y)];
                }
                return GetHalfSrc(ctx, (int)x.ImmBits);
            }
            IrOperand v = Raw(ctx, x);
            IrOperand[] r;
            if (HasSuffix(x, ".H0_H0")) r = [ctx.UnpackHalf2x16Low(v), ctx.UnpackHalf2x16Low(v)];
            else if (HasSuffix(x, ".H1_H1")) r = [ctx.UnpackHalf2x16High(v), ctx.UnpackHalf2x16High(v)];
            else if (HasSuffix(x, ".F32")) r = [v, v];
            // .H0_NH1 (second operand of HADD2/HFMA2/HMUL2): the selector names the high lane first (.H1_H0 is the identity), so the
            // high lane takes B.H0 and the low lane -B.H1. It used to fall through to the identity: no swap, no negation (drakolordx7/pokopia-recomp issue #3).
            else if (HasSuffix(x, ".H0_NH1")) r = [ctx.FPAbsNeg(ctx.UnpackHalf2x16High(v), false, true), ctx.UnpackHalf2x16Low(v)];
            else r = [ctx.UnpackHalf2x16Low(v), ctx.UnpackHalf2x16High(v)];
            return FPAbsNeg(ctx, r, x.Abs, x.Neg);
        }

        static void EmitHsetAmpere(EmitterContext ctx, SassInst i)
        {
            // HSETP2[.H_AND] Pd, Pd2, a, b, Pc / HSET2[.BF] Rd, a, b, Pc: per-half float compare (Ryujinx Maxwell Hsetp2/Hset2).
            int c = Array.FindIndex(FCompNames, n => i.Mods.Contains(n));
            if (c < 0) throw Unsup("no comparison");
            BoolOp bop = BopOf(i);
            IReadOnlyList<Op> o = i.Operands;
            bool setp = i.Opcode == "HSETP2";
            int k = setp ? 2 : 1;
            IrOperand[] a = HalfPair(ctx, o, ref k);
            IrOperand[] b = HalfPair(ctx, o, ref k);
            IrOperand pc = Src(ctx, o[k]);
            IrOperand[] r = [GetFPComparison(ctx, (FComp)c, a[0], b[0]), GetFPComparison(ctx, (FComp)c, a[1], b[1])];
            if (setp)
            {
                IrOperand p0, p1;
                if (Has(i, "H_AND"))
                {
                    IrOperand both = ctx.BitwiseAnd(r[0], r[1]);
                    p0 = GetPredLogicalOp(ctx, bop, both, pc);
                    p1 = GetPredLogicalOp(ctx, bop, ctx.Add(Instruction.LogicalNot, Local(), both), pc);
                }
                else
                {
                    p0 = GetPredLogicalOp(ctx, bop, r[0], pc);
                    p1 = GetPredLogicalOp(ctx, bop, r[1], pc);
                }
                Dst(ctx, o[0], p0);
                if (!(o[1].Kind == OperandKind.Pred && o[1].Reg == 7)) Dst(ctx, o[1], p1);
                return;
            }
            for (int n = 0; n < 2; n++) r[n] = GetPredLogicalOp(ctx, bop, r[n], pc);
            if (Has(i, "BF"))
                Dst(ctx, o[0], ctx.PackHalf2x16(ctx.ConditionalSelect(r[0], ConstF(1), ConstF(0)), ctx.ConditionalSelect(r[1], ConstF(1), ConstF(0))));
            else
                Dst(ctx, o[0], ctx.BitwiseOr(ctx.ConditionalSelect(r[0], Const(0xffff), Const(0)), ctx.ConditionalSelect(r[1], Const(unchecked((int)0xffff0000)), Const(0))));
        }

        static void EmitHalfAmpere(EmitterContext ctx, SassInst i)
        {
            IReadOnlyList<Op> o = i.Operands;
            int k = 1;
            IrOperand[] a = HalfPair(ctx, o, ref k);
            IrOperand[] b = HalfPair(ctx, o, ref k);
            IrOperand[] r = new IrOperand[2];
            switch (i.Opcode)
            {
                case "HADD2": for (int n = 0; n < 2; n++) r[n] = ctx.FPAdd(a[n], b[n]); break;
                case "HMUL2": for (int n = 0; n < 2; n++) r[n] = ctx.FPMultiply(a[n], b[n]); break;
                case "HFMA2":
                    {
                        IrOperand[] c = HalfPair(ctx, o, ref k);
                        for (int n = 0; n < 2; n++) r[n] = ctx.FPFusedMultiplyAdd(a[n], b[n], c[n]);
                        break;
                    }
                case "HMNMX2":
                    {
                        IrOperand p = Src(ctx, o[k]);
                        for (int n = 0; n < 2; n++) r[n] = ctx.ConditionalSelect(p, ctx.FPMinimum(a[n], b[n]), ctx.FPMaximum(a[n], b[n]));
                        break;
                    }
            }
            for (int n = 0; n < 2; n++)
            {
                if (Has(i, "SAT")) r[n] = ctx.FPSaturate(r[n]);
                if (Has(i, "RELU")) r[n] = ctx.FPMaximum(r[n], ConstF(0));
            }
            if (Has(i, "F32")) Dst(ctx, o[0], r[0]);
            else if (i.Mods.All(m => m is "SAT" or "RELU" or "FTZ" or "MMA")) Dst(ctx, o[0], ctx.PackHalf2x16(r[0], r[1]));
            else throw Unsup();
        }

        // ---- system values ----------------------------------------------------------------------------------------

        static readonly Dictionary<string, SReg> SRegs = new()
        {
            ["SR_LANEID"] = SReg.LaneId, ["SR_TID"] = SReg.TId, ["SR_TID.X"] = SReg.TIdX, ["SR_TID.Y"] = SReg.TIdY, ["SR_TID.Z"] = SReg.TIdZ,
            ["SR_CTAID.X"] = SReg.CtaIdX, ["SR_CTAID.Y"] = SReg.CtaIdY, ["SR_CTAID.Z"] = SReg.CtaIdZ,
            ["SR_EQMASK"] = SReg.EqMask, ["SR_LTMASK"] = SReg.LtMask, ["SR_LEMASK"] = SReg.LeMask, ["SR_GTMASK"] = SReg.GtMask, ["SR_GEMASK"] = SReg.GeMask,
            ["SR_INVOCATIONID"] = SReg.InvocationId, ["SR_Y_DIRECTION"] = SReg.YDirection, ["SR_THREAD_KILL"] = SReg.ThreadKill,
            ["SR_INVOCATIONINFO"] = SReg.InvocationInfo, ["SR_CLOCKLO"] = SReg.ClockLo, ["SR_CLOCKHI"] = SReg.ClockHi,
            // nvdisasm spells these two with an underscore
            ["SR_INVOCATION_ID"] = SReg.InvocationId, ["SR_INVOCATION_INFO"] = SReg.InvocationInfo,
        };

        static void EmitS2rAmpere(EmitterContext ctx, SassInst i)
        {
            Op d = i.Operands[0];
            string name = i.Operands[1].Text;
            if (!SRegs.TryGetValue(name, out SReg sr)) throw Unsup(name);
            if (sr is SReg.ClockLo or SReg.ClockHi) { Dst(ctx, d, Const(0)); return; }
            // Ryujinx's S2R on a transcoded Maxwell opcode, writing a scratch-free destination: use the Gpr directly when
            // the destination is a vector register, else a temporary Gpr-shaped write through RZ is not possible, so
            // emit into R255's slot via a local copy.
            if (d.Kind == OperandKind.Reg)
            {
                RunMaxwell(ctx, i, "1111000011001", (ulong)(d.Reg & 0xff) | ((ulong)sr << 20));
                return;
            }
            // S2UR: emit into the vector form on a fresh local, then copy.
            IrOperand v = sr switch
            {
                SReg.TIdX => ctx.Load(StorageKind.Input, IoVariable.ThreadId, null, Const(0)),
                SReg.TIdY => ctx.Load(StorageKind.Input, IoVariable.ThreadId, null, Const(1)),
                SReg.TIdZ => ctx.Load(StorageKind.Input, IoVariable.ThreadId, null, Const(2)),
                SReg.CtaIdX => ctx.Load(StorageKind.Input, IoVariable.CtaId, null, Const(0)),
                SReg.CtaIdY => ctx.Load(StorageKind.Input, IoVariable.CtaId, null, Const(1)),
                SReg.CtaIdZ => ctx.Load(StorageKind.Input, IoVariable.CtaId, null, Const(2)),
                _ => throw Unsup(name),
            };
            Dst(ctx, d, v);
        }

        /// Runs Ryujinx's Maxwell emitter for an opcode built from a 13-bit pattern prefix and field bits (guard = PT).
        static void RunMaxwell(EmitterContext ctx, SassInst i, string pattern, ulong fields)
        {
            ulong raw = fields | (7UL << 16);
            for (int b = 0; b < pattern.Length; b++)
            {
                if (pattern[b] == '1') raw |= 1UL << (63 - b);
            }
            InstOp op = InstTable.GetOp((ulong)i.Address, raw);
            if (op.Emitter == null) throw Unsup("transcoding failed");
            InstOp saved = ctx.CurrOp;
            ctx.CurrOp = op;
            op.Emitter(ctx);
            ctx.CurrOp = saved;
        }

        // ---- attributes -------------------------------------------------------------------------------------------

        static int AlSizeOf(SassInst i) => Has(i, "128") ? 3 : Has(i, "96") ? 2 : Has(i, "64") ? 1 : 0;

        static (int reg, int imm) AttrAddress(Op a)
        {
            if (a.Kind != OperandKind.Attr) throw Unsup("attribute operand");
            int reg = a.IndexReg >= 0 ? a.IndexReg : 255;
            if (reg != 255 && a.Offset != 0) throw Unsup("indexed attribute with offset");
            if (a.Offset < 0 || a.Offset > 0x7ff) throw Unsup("attribute offset");
            return (reg, (int)a.Offset);
        }

        static int RegOrRz(Op o) => o == null ? 255 : o.Kind == OperandKind.Reg ? o.Reg : throw Unsup($"operand {o.Kind}");

        static void EmitAldAmpere(EmitterContext ctx, SassInst i)
        {
            // ALD[.size][.O][.P] Rd, a[...] [, Rvertex]
            IReadOnlyList<Op> o = i.Operands;
            if (o[0].Kind != OperandKind.Reg) throw Unsup("destination");
            (int reg, int imm) = AttrAddress(o[1]);
            int vtx = RegOrRz(o.Count > 2 ? o[2] : null);
            ulong f = (ulong)(o[0].Reg & 0xff) | ((ulong)reg << 8) | ((ulong)imm << 20) | ((ulong)vtx << 39) | ((ulong)AlSizeOf(i) << 47);
            if (Has(i, "P")) f |= 1UL << 31;
            if (Has(i, "O")) f |= 1UL << 32;
            RunMaxwell(ctx, i, "1110111111011", f);
        }

        static void EmitAstAmpere(EmitterContext ctx, SassInst i)
        {
            // AST[.size][.P] a[...], Rsrc [, Rvertex]
            IReadOnlyList<Op> o = i.Operands;
            (int reg, int imm) = AttrAddress(o[0]);
            int src = RegOrRz(o[1]);
            int vtx = RegOrRz(o.Count > 2 ? o[2] : null);
            ulong f = (ulong)src | ((ulong)reg << 8) | ((ulong)imm << 20) | ((ulong)vtx << 39) | ((ulong)AlSizeOf(i) << 47);
            if (Has(i, "P")) f |= 1UL << 31;
            RunMaxwell(ctx, i, "1110111111110", f);
        }

        static void EmitIpaAmpere(EmitterContext ctx, SassInst i)
        {
            // IPA.PASS|CONSTANT|SC[.CENTROID|.OFFSET][.SAT] Rd, [Pd,] a[...] [, Roffset]
            // Volta's IPA has no multiply operand: IPA.PASS of a perspective user attribute returns attr/w (screen-linear)
            // and the code corrects it itself, e.g. 2db3327d9fbc5bb3:
            //   IPA.PASS R6, a[0x7c]; IPA.PASS R5, a[0x90]; MUFU.RCP R6, R6; FMUL R4, R6, R5
            // Vulkan's input is already perspective-correct, so IPA.PASS of a user attribute goes through Ryujinx's
            // Maxwell IPA.PASS, which multiplies a Perspective (SPH imap) input by gl_FragCoord.w (= 1/w) to give the
            // same attr/w; linear and flat inputs are not multiplied. IPA.CONSTANT (flat) stays a plain load (ALD path).
            // The rest is Ryujinx's IPA.PASS too (fragment coordinates with render scale and y flip, front facing, ...).
            IReadOnlyList<Op> o = i.Operands;
            if (o[0].Kind != OperandKind.Reg) throw Unsup("destination");
            if (IsPredKind(o[1])) throw Unsup("predicate destination");
            // .CENTROID samples at the covered centroid of a multisampled pixel; Ryujinx's Maxwell IPA decodes the sample mode (Msi)
            // and ignores it, so the input is read as at the pixel centre, which equals centroid without multisampling (Mario Kart
            // World: nearly every fragment shader).
            if (Has(i, "OFFSET") || Has(i, "SAT")) throw Unsup("interpolation mode");
            (int reg, int imm) = AttrAddress(o[1]);
            if (reg != 255) throw Unsup("indexed attribute");
            if (o.Count > 2 && !(o[2].Kind == OperandKind.Reg && o[2].Reg == 255)) throw Unsup("offset register");
            if (imm is >= AttributeConsts.UserAttributeBase and < AttributeConsts.UserAttributeEnd && Has(i, "CONSTANT"))
            {
                ulong f = (ulong)(o[0].Reg & 0xff) | (255UL << 8) | ((ulong)imm << 20) | (255UL << 39);
                RunMaxwell(ctx, i, "1110111111011", f);
                return;
            }
            if (imm > 0x3ff) throw Unsup("attribute offset");
            ulong g = (ulong)(o[0].Reg & 0xff) | (255UL << 8) | (255UL << 20) | (255UL << 39) | ((ulong)imm << 28) | ((ulong)IpaOp.Pass << 54) | (7UL << 47);
            RunMaxwell(ctx, i, "11100000", g);
        }

        static void EmitOutAmpere(EmitterContext ctx, SassInst i)
        {
            // OUT.EMIT|CUT|EMIT_THEN_CUT Rd, Ra, Rb
            IReadOnlyList<Op> o = i.Operands;
            int type = Has(i, "EMIT_THEN_CUT") ? 3 : Has(i, "CUT") ? 2 : Has(i, "EMIT") ? 1 : throw Unsup();
            if (o.Count < 3) throw Unsup();
            ulong f = (ulong)RegOrRz(o[0]) | ((ulong)RegOrRz(o[1]) << 8) | ((ulong)RegOrRz(o[2]) << 20) | ((ulong)type << 39);
            RunMaxwell(ctx, i, "1111101111100", f);
        }

        // ---- constant buffers and memory --------------------------------------------------------------------------

        static void EmitLdcAmpere(EmitterContext ctx, SassInst i)
        {
            // LDC[.U8|.S8|.U16|.S16|.64] Rd, c[bank][Ri+off]   (ULDC: uniform destination, no index register)
            IReadOnlyList<Op> o = i.Operands;
            Op c = o[1];
            if (c.Kind != OperandKind.CBuf) throw Unsup("source");
            if (Has(i, "IL")) throw Unsup("address mode");
            int words = Has(i, "64") ? 2 : 1;
            LsSize small = Has(i, "U8") ? LsSize.U8 : Has(i, "S8") ? LsSize.S8 : Has(i, "U16") ? LsSize.U16 : Has(i, "S16") ? LsSize.S16 : LsSize.B32;
            if (Has(i, "IS") || Has(i, "ISL"))
            {
                // Indexed segmented (Ryujinx Maxwell Ldc): the index register holds the bank offset in bits 16..31 and the byte
                // offset in bits 0..15, e.g. "LEA R0, R0, 0x10000, 0x3; LDC.64.IS R8, c[0x0][R0]" reads c[1][index * 8].
                if (small != LsSize.B32 || c.BankIsUReg || c.IndexReg < 0 || c.IndexReg >= 255) throw Unsup("address mode");
                IrOperand ri = c.IndexIsUReg ? UGpr(c.IndexReg) : Gpr(c.IndexReg);
                IrOperand slotIs = ctx.IAdd(Const(c.Bank), ctx.ShiftRightU32(ri, Const(16)));
                IrOperand wordIs = ctx.ShiftRightU32(ctx.IAdd(ctx.BitwiseAnd(ri, Const(0xffff)), Const((int)c.Offset)), Const(2));
                for (int w = 0; w < words; w++) DstWord(ctx, o[0], w, EmitLoadConstant(ctx, slotIs, w == 0 ? wordIs : ctx.IAdd(wordIs, Const(w))));
                return;
            }
            if (small != LsSize.B32)
            {
                IrOperand slot = c.BankIsUReg ? UGpr(c.Bank) : Const(c.Bank);
                IrOperand addr = Const((int)c.Offset);
                if (c.IndexReg >= 0) addr = ctx.IAdd(c.IndexIsUReg ? UGpr(c.IndexReg) : Gpr(c.IndexReg), addr);
                IrOperand v = EmitLoadConstant(ctx, slot, ctx.ShiftRightU32(addr, Const(2)));
                Dst(ctx, o[0], ExtractSmallInt(ctx, small, GetBitOffset(ctx, addr), v));
                return;
            }
            for (int w = 0; w < words; w++) DstWord(ctx, o[0], w, LoadCbuf(ctx, c, w));
        }

        /// Byte address of a [Ra(.X4|.X8|.X16) + URb + imm] operand (shared/local memory).
        static IrOperand MemAddress(EmitterContext ctx, Op m)
        {
            if (m.Kind != OperandKind.Mem) throw Unsup("memory operand");
            IrOperand addr = Const((int)m.Offset);
            if (m.Reg >= 0 && m.Reg < 255)
            {
                IrOperand r = Gpr(m.Reg);
                int scale = HasSuffix(m, ".X4") ? 2 : HasSuffix(m, ".X8") ? 3 : HasSuffix(m, ".X16") ? 4 : 0;
                if (HasSuffix(m, ".64")) throw Unsup("64-bit address");
                if (scale != 0) r = ctx.ShiftLeft(r, Const(scale));
                addr = ctx.IAdd(r, addr);
            }
            if (m.IndexReg >= 0) addr = ctx.IAdd(m.IndexIsUReg ? UGpr(m.IndexReg) : Gpr(m.IndexReg), addr);
            return addr;
        }

        static LsSize2 LsSizeOf(SassInst i) =>
            Has(i, "U8") ? LsSize2.U8 : Has(i, "S8") ? LsSize2.S8 : Has(i, "U16") ? LsSize2.U16 : Has(i, "S16") ? LsSize2.S16 :
            Has(i, "128") ? LsSize2.B128 : Has(i, "64") ? LsSize2.B64 : LsSize2.B32;

        static void EmitLdStAmpere(EmitterContext ctx, SassInst i, StorageKind kind, bool load)
        {
            // LDS/LDL Rd, [addr]   STS/STL [addr], Rs
            IReadOnlyList<Op> o = i.Operands;
            Op data = load ? o[0] : o[1];
            Op mem = load ? o[1] : o[0];
            if (data.Kind != OperandKind.Reg) throw Unsup("data operand");
            if (kind == StorageKind.SharedMemory && ctx.TranslatorContext.Definitions.Stage != ShaderStage.Compute) throw Unsup("shared memory outside compute");
            IrOperand addr = MemAddress(ctx, mem);
            if (load) EmitLoad(ctx, kind, LsSizeOf(i), addr, data.Reg, 0);
            else EmitStore(ctx, kind, LsSizeOf(i), addr, data.Reg, 0);
        }

        /// 64-bit global address of [Ra(.64|.U32) + URb(pair) + imm] (+ extra: the byte offset of a vector element). The immediate and
        /// the element offset are one constant add on top of the register sum: Ryujinx's GlobalToStorage peels a single constant
        /// before it looks for the c[0] base, so "(R + c0) + imm + 4n" as two adds lost the storage buffer (cbabef05, 6 more).
        static (IrOperand lo, IrOperand hi) GlobalAddress(EmitterContext ctx, SassInst i, Op m, long extra = 0)
        {
            if (m.Kind != OperandKind.Mem) throw Unsup("memory operand");
            IrOperand lo = Const(0), hi = Const(0);
            if (m.Reg >= 0 && m.Reg < 255)
            {
                lo = Gpr(m.Reg);
                // .E = 64-bit addresses: the base is a register pair unless printed as Rn.U32 (32-bit base plus a UR pair).
                bool wide = (Has(i, "E") && !HasSuffix(m, ".U32")) || HasSuffix(m, ".64");
                hi = wide ? Gpr(m.Reg + 1) : Const(0);
            }
            if (m.IndexReg >= 0)
            {
                if (!m.IndexIsUReg) throw Unsup("address index");
                IrOperand ulo = UGpr(m.IndexReg), uhi = m.IndexReg < 63 ? UGpr(m.IndexReg + 1) : Const(0);
                IrOperand nlo = ctx.IAdd(lo, ulo);
                hi = ctx.IAdd(ctx.IAdd(hi, uhi), Bool(ctx, ctx.ICompareLessUnsigned(nlo, ulo)));
                lo = nlo;
            }
            long imm = m.Offset + extra;
            if (imm != 0)
            {
                IrOperand off = Const((int)imm);
                IrOperand nlo = ctx.IAdd(lo, off);
                // a negative offset is sign-extended: hi gains carry - 1
                IrOperand carry = Bool(ctx, ctx.ICompareLessUnsigned(nlo, lo));
                hi = ctx.IAdd(hi, imm < 0 ? ctx.ISubtract(carry, Const(1)) : carry);
                lo = nlo;
            }
            return (lo, hi);
        }

        static void EmitGlobalAmpere(EmitterContext ctx, SassInst i, bool load)
        {
            // LDG.E[.size] Rd, [addr]   STG.E[.size] [addr], Rs
            IReadOnlyList<Op> o = i.Operands;
            Op data = load ? o[0] : o[1];
            Op mem = load ? o[1] : o[0];
            LsSize2 size = LsSizeOf(i);
            StorageKind kind = GetStorageKind((LsSize)size);
            int count = GetVectorCount((LsSize)size);
            if (data.Kind != OperandKind.Reg) throw Unsup("data operand");
            // every element's address before the first load writes a register (LDG.E.64 R0, [R0.64] reads R0 for both words)
            var addr = new (IrOperand lo, IrOperand hi)[count];
            for (int n = 0; n < count; n++) addr[n] = GlobalAddress(ctx, i, mem, n * 4);
            for (int n = 0; n < count; n++)
            {
                (IrOperand a, IrOperand hi) = addr[n];
                if (load)
                {
                    if (data.Reg + n >= 255) break;
                    ctx.Copy(Gpr(data.Reg + n), ctx.Load(kind, a, hi));
                }
                else
                {
                    ctx.Store(kind, a, hi, data.Reg >= 255 ? Const(0) : Gpr(data.Reg + n));
                }
            }
        }

        static AtomOp AtomOpOf(SassInst i) =>
            Has(i, "ADD") ? AtomOp.Add : Has(i, "MIN") ? AtomOp.Min : Has(i, "MAX") ? AtomOp.Max : Has(i, "INC") ? AtomOp.Inc :
            Has(i, "DEC") ? AtomOp.Dec : Has(i, "AND") ? AtomOp.And : Has(i, "OR") ? AtomOp.Or : Has(i, "XOR") ? AtomOp.Xor :
            Has(i, "EXCH") ? AtomOp.Exch : throw Unsup("atomic op");

        static void EmitAtomsAmpere(EmitterContext ctx, SassInst i)
        {
            // ATOMS.op[.S32] Rd, [addr], Rb
            IReadOnlyList<Op> o = i.Operands;
            IrOperand offset = ctx.ShiftRightU32(MemAddress(ctx, o[1]), Const(2));
            if (Has(i, "POPC") && Has(i, "INC"))
            {
                // ATOMS.POPC.INC.32 Rd, [addr]: the word gains the number of lanes that address it; one atomic add of 1 per lane does the
                // same (Rd, the old value, is then per lane rather than the warp's; the corpus only has Rd = RZ)
                IrOperand r = EmitAtomicOp(ctx, StorageKind.SharedMemory, AtomOp.Add, AtomSize.U32, Const(ctx.ResourceManager.SharedMemoryId), offset, Const(1));
                Dst(ctx, o[0], r);
                return;
            }
            if (Has(i, "CAS") || Has(i, "64")) throw Unsup();
            AtomSize size = Has(i, "S32") ? AtomSize.S32 : AtomSize.U32;
            IrOperand res = EmitAtomicOp(ctx, StorageKind.SharedMemory, AtomOpOf(i), size, Const(ctx.ResourceManager.SharedMemoryId), offset, Src(ctx, o[2]));
            Dst(ctx, o[0], res);
        }

        static void EmitAtomgAmpere(EmitterContext ctx, SassInst i)
        {
            // ATOMG.E.op[.size].STRONG.GPU Pd, Rd, [addr], Rb
            if (Has(i, "CAS") || Has(i, "64") || Has(i, "F32") || Has(i, "F16x2")) throw Unsup();
            IReadOnlyList<Op> o = i.Operands;
            int k = IsPredKind(o[0]) ? 1 : 0;
            (IrOperand lo, IrOperand hi) = GlobalAddress(ctx, i, o[k + 1]);
            AtomSize size = Has(i, "S32") ? AtomSize.S32 : AtomSize.U32;
            IrOperand res = EmitAtomicOp(ctx, StorageKind.GlobalMemory, AtomOpOf(i), size, lo, hi, Src(ctx, o[k + 2]));
            Dst(ctx, o[k], res);
            if (k == 1) Dst(ctx, o[0], Const(IrConsts.True));
        }

        static void EmitRedAmpere(EmitterContext ctx, SassInst i)
        {
            // RED.E.op[.size].STRONG.GPU [addr], Rb: a global atomic whose result is not returned
            if (Has(i, "CAS") || Has(i, "64") || Has(i, "F32") || Has(i, "F16x2")) throw Unsup();
            IReadOnlyList<Op> o = i.Operands;
            (IrOperand lo, IrOperand hi) = GlobalAddress(ctx, i, o[0]);
            AtomSize size = Has(i, "S32") ? AtomSize.S32 : AtomSize.U32;
            EmitAtomicOp(ctx, StorageKind.GlobalMemory, AtomOpOf(i), size, lo, hi, Src(ctx, o[1]));
        }

        // ---- textures ---------------------------------------------------------------------------------------------

        static (SamplerType type, bool array) DimOf(string dim) => dim switch
        {
            "1D" => (SamplerType.Texture1D, false),
            "2D" => (SamplerType.Texture2D, false),
            "3D" => (SamplerType.Texture3D, false),
            "CUBE" => (SamplerType.TextureCube, false),
            "ARRAY_1D" => (SamplerType.Texture1D | SamplerType.Array, true),
            "ARRAY_2D" => (SamplerType.Texture2D | SamplerType.Array, true),
            "ARRAY_3D" => (SamplerType.Texture3D | SamplerType.Array, true),
            "ARRAY_CUBE" => (SamplerType.TextureCube | SamplerType.Array, true),
            _ => throw Unsup($"texture shape {dim}"),
        };

        /// Splits the operands of TEX/TLD/TLD4/TXQ: Rd2, Rd, Ra, [Rb], bank, handle, [shape], [mask].
        static (Op rd2, Op rd, Op ra, Op rb, int bank, int handle, string dim, int mask) TexOperands(SassInst i) => TexOperands(i, out _, false);

        /// The same, optionally accepting the predicate destination after Rd (bit 81: the residency result of a sparse access).
        static (Op rd2, Op rd, Op ra, Op rb, int bank, int handle, string dim, int mask) TexOperands(SassInst i, out Op pred, bool allowPred)
        {
            IReadOnlyList<Op> o = i.Operands;
            int k = 0;
            pred = null;
            List<Op> regs = new();
            while (k < o.Count && (o[k].Kind == OperandKind.Reg || IsPredKind(o[k])))
            {
                if (IsPredKind(o[k]))
                {
                    if (!allowPred || pred != null) throw Unsup("residency predicate");
                    pred = o[k++];
                    continue;
                }
                regs.Add(o[k++]);
            }
            if (regs.Count < 3) throw Unsup("register operands");
            List<Op> rest = o.Skip(k).ToList();
            List<Op> imms = rest.Where(x => x.Kind == OperandKind.Imm).ToList();
            string dim = rest.FirstOrDefault(x => x.Kind == OperandKind.Other)?.Text;
            if (imms.Count < 2) throw Unsup("bindless or unknown handle form");
            int mask = imms.Count > 2 ? (int)imms[2].ImmBits & 0xf : 0xf;
            return (regs[0], regs[1], regs[2], regs.Count > 3 ? regs[3] : null, (int)imms[0].ImmBits, (int)imms[1].ImmBits, dim, mask);
        }

        static int CbufSlotOf(EmitterContext ctx, int bank) => bank == AmpereTextureBank ? TextureOperation.DefaultCbufSlot : bank;

        /// Destination registers for a 4-bit component mask: the first two enabled components go to Rd, Rd+1, the next
        /// two to Rd2, Rd2+1 (inferred from the compiler's use of the two destination operands).
        static IrOperand[] TexDests(Op rd, Op rd2, int mask)
        {
            int n = BitOperations.PopCount((uint)mask);
            IrOperand[] d = new IrOperand[n];
            for (int c = 0; c < n; c++)
            {
                Op baseReg = c < 2 ? rd : rd2;
                int r = baseReg.Reg >= 255 ? 255 : baseReg.Reg + (c & 1);
                d[c] = r >= 255 ? Local() : Gpr(r);
            }
            return d;
        }

        static void EmitTexAmpere(EmitterContext ctx, SassInst i, bool fetch)
        {
            (Op rd2, Op rd, Op ra, Op rb, int bank, int handle, string dim, int mask) = TexOperands(i);
            if (rd.Reg >= 255 && rd2.Reg >= 255) return;
            foreach (string m in i.Mods)
            {
                if (m is not ("SCR" or "LZ" or "LL" or "LB" or "DC" or "AOFFI" or "NODEP" or "MS" or "NDV" or "EF" or "EL" or "LU" or "EU" or "NA")) throw Unsup($"modifier {m}");
            }
            bool scr = Has(i, "SCR");
            (SamplerType type, bool array) = DimOf(dim);
            bool lz = Has(i, "LZ"), ll = Has(i, "LL"), bias = Has(i, "LB"), dc = Has(i, "DC"), aoffi = Has(i, "AOFFI"), ms = Has(i, "MS");
            TextureFlags flags = fetch ? TextureFlags.IntCoords : TextureFlags.None;
            int coords = (type & SamplerType.Mask) switch { SamplerType.Texture1D => 1, SamplerType.Texture2D => 2, _ => 3 };

            // Register parameter lists, in the order the hardware reads them.
            int raN, rbN;
            List<string> raList = new(), rbList = new();
            if (!scr)
            {
                // Same split as Maxwell TEX/TLD: Ra = [array index, coords...], Rb = [lod|bias, offset, dc, ms sample].
                if (array) raList.Add("arr");
                for (int c = 0; c < coords; c++) raList.Add("c" + c);
                if (ll || bias) rbList.Add("lod");
                if (aoffi) rbList.Add("off");
                if (dc) rbList.Add("dc");
                if (ms) rbList.Add("ms");
            }
            else
            {
                // .SCR: the compact Maxwell TEXS/TLDS split (inferred): the parameters [array, coords..., dc, lod,
                // offset, ms] fill Ra then Rb, one each when there are two, else two in Ra and the rest in Rb.
                // Fetch from an array follows the same rule: TLD.SCR ARRAY_2D is Ra = [array index, x], Rb = [y]
                // (0cd3176a: "MOV R34, 0x3; MOV R35, R27(x); TLD.SCR.LZ R35, R2, R34, R20(y), ARRAY_2D"; Rb+1 is never
                // written before it). Reading x from Rb fetched texel (y, 0) for every pixel: one value per screen row.
                List<string> all = new();
                if (array) all.Add("arr");
                for (int c = 0; c < coords; c++) all.Add("c" + c);
                if (dc) all.Add("dc");
                if (ll || bias) all.Add("lod");
                if (aoffi) all.Add("off");
                if (ms) all.Add("ms");
                int split = all.Count <= 2 ? 1 : 2;
                raList.AddRange(all.Take(split));
                rbList.AddRange(all.Skip(split));
            }
            raN = raList.Count;
            rbN = rbList.Count;
            if (rbN > 0 && rb == null) throw Unsup("missing Rb");
            Dictionary<string, IrOperand> p = new();
            for (int k = 0; k < raN; k++) p[raList[k]] = ra.Reg >= 255 ? Const(0) : ctx.Copy(Gpr(ra.Reg + k));
            for (int k = 0; k < rbN; k++) p[rbList[k]] = rb.Reg >= 255 ? Const(0) : ctx.Copy(Gpr(rb.Reg + k));

            // 1D fetch from a buffer texture (texelFetch on samplerBuffer).
            if (fetch && type == SamplerType.Texture1D && !array && !ll && !aoffi && !ms &&
                ctx.TranslatorContext.GpuAccessor.QuerySamplerType(handle, CbufSlotOf(ctx, bank)) == SamplerType.TextureBuffer)
            {
                type = SamplerType.TextureBuffer;
            }

            List<IrOperand> sources = new();
            for (int c = 0; c < coords; c++) sources.Add(p["c" + c]);
            bool is1DTo2D = false;
            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(fetch ? Const(0) : ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
                is1DTo2D = true;
            }
            if (array) sources.Add(p["arr"]);
            if (dc) { sources.Add(p["dc"]); type |= SamplerType.Shadow; }
            if ((lz || ll) && !ms && type != SamplerType.TextureBuffer)
            {
                sources.Add(ll ? p["lod"] : fetch ? Const(0) : ConstF(0));
                flags |= TextureFlags.LodLevel;
            }
            if (aoffi)
            {
                for (int c = 0; c < coords; c++) sources.Add(ctx.BitfieldExtractS32(p["off"], Const(c * 4), Const(4)));
                if (is1DTo2D) sources.Add(Const(0));
                flags |= TextureFlags.Offset;
            }
            if (bias) { sources.Add(p["lod"]); flags |= TextureFlags.LodBias; }
            if (ms) { sources.Add(p["ms"]); type |= SamplerType.Multisample; }
            if (Has(i, "NDV") && !fetch) { /* no derivatives across the quad: plain sample on the host */ }

            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.TextureSample, type, TextureFormat.Unknown, flags, CbufSlotOf(ctx, bank), handle);
            ctx.TextureSample(type, flags, binding, mask, TexDests(rd, rd2, mask), sources.ToArray());
        }

        static void EmitTxdAmpere(EmitterContext ctx, SassInst i)
        {
            // TXD Rd2, Rd, Ra, Rb, bank, handle, dim[, mask]. Ra = [coords..., packed (array index in bits 0..15, AOFFI offsets
            // 4 bits each from bit 16)], as Maxwell TXD (Ryujinx EmitTxd). Rb = the gradients per coordinate, d/dx then d/dy
            // (s: dx dy, t: dx dy, ...): seen in 22f0d8d9, whose Rb is built as [P1.s-P0.s, P2.s-P0.s, P1.t-P0.t, P2.t-P0.t].
            (Op rd2, Op rd, Op ra, Op rb, int bank, int handle, string dim, int mask) = TexOperands(i);
            if (rd.Reg >= 255 && rd2.Reg >= 255) return;
            foreach (string m in i.Mods)
            {
                if (m is not ("AOFFI" or "NODEP" or "EF" or "EL" or "LU" or "EU" or "NA")) throw Unsup($"modifier {m}");
            }
            if (rb == null) throw Unsup("missing Rb");
            bool aoffi = Has(i, "AOFFI");
            (SamplerType type, bool array) = DimOf(dim);
            int coords = (type & SamplerType.Mask) switch { SamplerType.Texture1D => 1, SamplerType.Texture2D => 2, _ => 3 };
            int a = ra.Reg, bReg = rb.Reg;
            IrOperand RaN() => a >= 255 ? Const(0) : ctx.Copy(Gpr(a++));
            IrOperand RbN() => bReg >= 255 ? ConstF(0) : ctx.Copy(Gpr(bReg++));
            List<IrOperand> sources = new();
            for (int c = 0; c < coords; c++) sources.Add(RaN());
            bool is1DTo2D = false;
            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
                is1DTo2D = true;
            }
            IrOperand packed = (array || aoffi) ? RaN() : null;
            if (array) sources.Add(ctx.BitwiseAnd(packed, Const(0xffff)));
            IrOperand[] dx = new IrOperand[coords], dy = new IrOperand[coords];
            for (int c = 0; c < coords; c++) { dx[c] = RbN(); dy[c] = RbN(); }
            sources.AddRange(dx);
            if (is1DTo2D) sources.Add(ConstF(0));
            sources.AddRange(dy);
            if (is1DTo2D) sources.Add(ConstF(0));
            TextureFlags flags = TextureFlags.Derivatives;
            if (aoffi)
            {
                for (int c = 0; c < coords; c++) sources.Add(ctx.BitfieldExtractS32(packed, Const(16 + c * 4), Const(4)));
                if (is1DTo2D) sources.Add(Const(0));
                flags |= TextureFlags.Offset;
            }
            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.TextureSample, type, TextureFormat.Unknown, flags, CbufSlotOf(ctx, bank), handle);
            ctx.TextureSample(type, flags, binding, mask, TexDests(rd, rd2, mask), sources.ToArray());
        }

        static void EmitTmmlAmpere(EmitterContext ctx, SassInst i)
        {
            // TMML[.NDV] Rd2, Rd, Ra, [Rb], bank, handle, dim[, mask]: the LOD the sample would use, as Ryujinx's Maxwell TMML:
            // Ra = [array index, coords...]; components x/y = the computed and the clamped LOD in 8.8 fixed point (GLSL's
            // textureQueryLod order swapped), z/w = 0. Destinations split like TEX (Rd, Rd+1 then Rd2, Rd2+1).
            (Op rd2, Op rd, Op ra, Op rb, int bank, int handle, string dim, int mask) = TexOperands(i, out Op residency, true);
            // Every texture is fully resident on the host (sparse textures are whole images): the residency predicate is true.
            if (residency != null) Dst(ctx, residency, Const(IrConsts.True));
            if (rd.Reg >= 255 && rd2.Reg >= 255) return;
            foreach (string m in i.Mods)
            {
                if (m is not ("NDV" or "NODEP" or "EF" or "EL" or "LU" or "EU" or "NA")) throw Unsup($"modifier {m}");
            }
            (SamplerType type, bool array) = DimOf(dim);
            int coords = (type & SamplerType.Mask) switch { SamplerType.Texture1D => 1, SamplerType.Texture2D => 2, _ => 3 };
            int a = ra.Reg;
            IrOperand RaN() => a >= 255 ? Const(0) : ctx.Copy(Gpr(a++));
            IrOperand arrayIndex = array ? RaN() : null;
            List<IrOperand> sources = new();
            for (int c = 0; c < coords; c++) sources.Add(RaN());
            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }
            if (array) sources.Add(arrayIndex);
            IrOperand[] src = sources.ToArray();
            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.Lod, type, TextureFormat.Unknown, TextureFlags.None, CbufSlotOf(ctx, bank), handle);
            IrOperand[] dests = TexDests(rd, rd2, mask);
            int k = 0;
            for (int comp = 0; comp < 4; comp++)
            {
                if ((mask & (1 << comp)) == 0) continue;
                IrOperand d = dests[k++];
                if (comp >= 2) { ctx.Copy(d, Const(0)); continue; }
                IrOperand lod = ctx.Lod(type, TextureFlags.None, binding, comp ^ 1, src);
                ctx.Copy(d, ctx.FP32ConvertToS32(ctx.FPMultiply(lod, ConstF(256.0f))));
            }
        }

        static void EmitFswzaddAmpere(EmitterContext ctx, SassInst i)
        {
            // FSWZADD Rd, Ra, Rb, ops: per quad lane l, Rd = Ra op_l Rb with op_l = bits 32 + 2l..33 + 2l (0 a+b, 1 b-a, 2 a-b,
            // 3 b): Ryujinx's Maxwell SwizzleAdd (same per-lane op table). The neighbour value comes from a SHFL before it.
            IReadOnlyList<Op> o = i.Operands;
            int ops = (int)((i.Lo >> 32) & 0xff);
            Dst(ctx, o[0], ctx.FPSwizzleAdd(Src(ctx, o[1]), Src(ctx, o[2]), ops));
        }

        static void EmitTld4Ampere(EmitterContext ctx, SassInst i)
        {
            (Op rd2, Op rd, Op ra, Op rb, int bank, int handle, string dim, int mask) = TexOperands(i);
            if (rd.Reg >= 255 && rd2.Reg >= 255) return;
            bool dc = Has(i, "DC"), aoffi = Has(i, "AOFFI"), scr = Has(i, "SCR");
            foreach (string m in i.Mods)
            {
                if (m is not ("SCR" or "R" or "G" or "B" or "A" or "DC" or "AOFFI" or "NODEP" or "EF" or "EL" or "LU" or "EU" or "NA")) throw Unsup($"modifier {m}");
            }
            int comp = Has(i, "G") ? 1 : Has(i, "B") ? 2 : Has(i, "A") ? 3 : 0;
            (SamplerType type, bool array) = DimOf(dim ?? "2D");
            if ((type & SamplerType.Mask) != SamplerType.Texture2D || array && (!scr || dc)) throw Unsup("gather shape");
            IrOperand Ra(int k) => ra.Reg >= 255 ? Const(0) : ctx.Copy(Gpr(ra.Reg + k));
            IrOperand Rb(int k) => rb == null || rb.Reg >= 255 ? Const(0) : ctx.Copy(Gpr(rb.Reg + k));
            List<IrOperand> sources = new();
            TextureFlags flags = TextureFlags.Gather;
            int rbk = 0;
            if (array)
            {
                // TLD4.SCR on ARRAY_2D: Ra = [array index (U16), x], Rb = [y, (AOFFI offsets)] (b7fef02a: "F2I.U16 R20; TLD4.SCR.R
                // R16, R14, R20, R18, ARRAY_2D" with x in R21 and y in R18, the AOFFI forms with offsets in R19 / R1 / R7)
                IrOperand arr = Ra(0);
                sources.Add(Ra(1)); sources.Add(Rb(rbk++));
                sources.Add(ctx.BitwiseAnd(arr, Const(0xffff)));
                type |= SamplerType.Array;
            }
            else if (scr && !dc && !aoffi) { sources.Add(Ra(0)); sources.Add(Rb(rbk++)); }
            else { sources.Add(Ra(0)); sources.Add(Ra(1)); }
            int dcIndex = sources.Count;
            IrOperand dcv = null;
            if (aoffi)
            {
                IrOperand packed = Rb(rbk++);
                for (int c = 0; c < 2; c++) sources.Add(ctx.BitfieldExtractS32(packed, Const(c * 8), Const(6)));
                flags |= TextureFlags.Offset;
            }
            if (dc) { dcv = Rb(rbk++); sources.Insert(dcIndex, dcv); type |= SamplerType.Shadow; }
            else sources.Add(Const(comp));
            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.TextureSample, type, TextureFormat.Unknown, flags, CbufSlotOf(ctx, bank), handle);
            ctx.TextureSample(type, flags, binding, mask, TexDests(rd, rd2, mask), sources.ToArray());
        }

        static void EmitTxqAmpere(EmitterContext ctx, SassInst i)
        {
            // TXQ Rd2, Rd, Ra(lod), TEX_HEADER_DIMENSION, bank, handle, mask
            IReadOnlyList<Op> o = i.Operands;
            if (o.Count < 6 || o[3].Text != "TEX_HEADER_DIMENSION") throw Unsup(o.Count > 3 ? o[3].Text : "form");
            Op rd2 = o[0], rd = o[1], ra = o[2];
            int bank = (int)o[4].ImmBits, handle = (int)o[5].ImmBits, mask = o.Count > 6 ? (int)o[6].ImmBits & 0xf : 0xf;
            int slot = CbufSlotOf(ctx, bank);
            SamplerType type = ctx.TranslatorContext.GpuAccessor.QuerySamplerType(handle, slot);
            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.TextureQuerySize, type, TextureFormat.Unknown, TextureFlags.None, slot, handle);
            IrOperand lod = ra.Reg >= 255 ? Const(0) : ctx.Copy(Gpr(ra.Reg));
            IrOperand[] dests = TexDests(rd, rd2, mask);
            int n = 0;
            for (int c = 0; c < 4; c++)
            {
                if ((mask & (1 << c)) == 0) continue;
                // A component past the sampler type's size vector (depth of a 2D texture) reads as 1 (inferred); index 3 is
                // the mip count. Compute texture types are not known, so the accessor's 2D default meets depth queries.
                int dims = ((type & SamplerType.Mask) == SamplerType.TextureCube ? 2 : type.Dimensions) + (type.HasFlag(SamplerType.Array) ? 1 : 0);
                ctx.Copy(dests[n++], c < 3 && c >= dims ? Const(1) : ctx.TextureQuerySize(type, TextureFlags.None, binding, c, [lod]));
            }
        }

        static void EmitSustAmpere(EmitterContext ctx, SassInst i)
        {
            // SUST.P.dim[.IGN] [Ra], Rdata, bank, handle   (formatted store of RGBA)
            IReadOnlyList<Op> o = i.Operands;
            if (!Has(i, "P") || o.Count < 4 || o[0].Kind != OperandKind.Mem || o[1].Kind != OperandKind.Reg) throw Unsup("form");
            string dim = i.Mods.FirstOrDefault(m => m is "1D" or "2D" or "3D" or "1D_BUFFER" or "1D_ARRAY" or "2D_ARRAY") ?? throw Unsup("shape");
            SamplerType type = dim switch
            {
                "1D" => SamplerType.Texture1D, "2D" => SamplerType.Texture2D, "3D" => SamplerType.Texture3D, "1D_BUFFER" => SamplerType.TextureBuffer,
                "1D_ARRAY" => SamplerType.Texture1D | SamplerType.Array, _ => SamplerType.Texture2D | SamplerType.Array,
            };
            if (i.Mods.Any(m => m.Length <= 4 && m.All(ch => ch is 'R' or 'G' or 'B' or 'A'))) throw Unsup("component subset");
            int ra = o[0].Reg;
            if (ra >= 255 || o[0].IndexReg >= 0 || o[0].Offset != 0) throw Unsup("address");
            int bank = (int)o[2].ImmBits, handle = (int)o[3].ImmBits;
            List<IrOperand> sources = new();
            int k = 0;
            for (int c = 0; c < type.Dimensions; c++) sources.Add(ctx.Copy(Gpr(ra + k++)));
            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(Const(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }
            if (type.HasFlag(SamplerType.Array)) sources.Add(ctx.Copy(Gpr(ra + k++)));
            int data = o[1].Reg;
            for (int c = 0; c < 4; c++) sources.Add(data >= 255 ? Const(0) : ctx.Copy(Gpr(data + c)));
            TextureFormat format = Ryujinx.Graphics.Shader.StructuredIr.ShaderProperties.GetTextureFormat(ctx.TranslatorContext.GpuAccessor, handle);
            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.ImageStore, type, format, TextureFlags.None, CbufSlotOf(ctx, bank), handle);
            ctx.ImageStore(type, format, TextureFlags.None, binding, sources.ToArray());
        }

        static void EmitSuldAmpere(EmitterContext ctx, SassInst i)
        {
            // SULD.P.dim[.RGBA subset].IGN Rd, [Ra], bank, handle   (formatted load; the components named, or all four, land in Rd, Rd+1, ...
            // packed as Ryujinx's SULD.P does). Corpus forms: SULD.P.2D.R.IGN, SULD.P.2D_ARRAY.R.IGN, SULD.P.2D_ARRAY.IGN.
            IReadOnlyList<Op> o = i.Operands;
            if (!Has(i, "P") || Has(i, "B") || o.Count < 4 || o[0].Kind != OperandKind.Reg || o[1].Kind != OperandKind.Mem) throw Unsup("form");
            string dim = i.Mods.FirstOrDefault(m => m is "1D" or "2D" or "3D" or "1D_BUFFER" or "1D_ARRAY" or "2D_ARRAY") ?? throw Unsup("shape");
            SamplerType type = dim switch
            {
                "1D" => SamplerType.Texture1D, "2D" => SamplerType.Texture2D, "3D" => SamplerType.Texture3D, "1D_BUFFER" => SamplerType.TextureBuffer,
                "1D_ARRAY" => SamplerType.Texture1D | SamplerType.Array, _ => SamplerType.Texture2D | SamplerType.Array,
            };
            string comps = i.Mods.FirstOrDefault(m => m.Length <= 4 && m.Length > 0 && m.All(ch => ch is 'R' or 'G' or 'B' or 'A'));
            int mask = comps == null ? 0xf : (comps.Contains('R') ? 1 : 0) | (comps.Contains('G') ? 2 : 0) | (comps.Contains('B') ? 4 : 0) | (comps.Contains('A') ? 8 : 0);
            int ra = o[1].Reg;
            if (ra >= 255 || o[1].IndexReg >= 0 || o[1].Offset != 0) throw Unsup("address");
            int bank = (int)o[2].ImmBits, handle = (int)o[3].ImmBits;
            List<IrOperand> sources = new();
            int k = 0;
            for (int c = 0; c < type.Dimensions; c++) sources.Add(ctx.Copy(Gpr(ra + k++)));
            if (Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(Const(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }
            if (type.HasFlag(SamplerType.Array)) sources.Add(ctx.Copy(Gpr(ra + k++)));
            int rd = o[0].Reg;
            List<IrOperand> dests = new();
            for (int c = 0; c < BitOperations.PopCount((uint)mask) && rd + c < 255; c++) dests.Add(Gpr(rd + c));
            if (dests.Count == 0) return;   // RZ destination: nothing observable
            TextureFormat format = Ryujinx.Graphics.Shader.StructuredIr.ShaderProperties.GetTextureFormat(ctx.TranslatorContext.GpuAccessor, handle);
            SetBindingPair binding = ctx.ResourceManager.GetTextureOrImageBinding(Instruction.ImageLoad, type, format, TextureFlags.None, CbufSlotOf(ctx, bank), handle);
            ctx.ImageLoad(type, format, TextureFlags.None, binding, mask, dests.ToArray(), sources.ToArray());
        }

        // ---- warp -------------------------------------------------------------------------------------------------

        static void EmitShflAmpere(EmitterContext ctx, SassInst i)
        {
            // SHFL.IDX|UP|DOWN|BFLY Pd, Rd, a, b, c
            IReadOnlyList<Op> o = i.Operands;
            IrOperand a = Src(ctx, o[2]), b = Src(ctx, o[3]), c = Src(ctx, o[4]);
            (IrOperand res, IrOperand valid) = Has(i, "IDX") ? ctx.Shuffle(a, b, c) : Has(i, "UP") ? ctx.ShuffleUp(a, b, c)
                : Has(i, "DOWN") ? ctx.ShuffleDown(a, b, c) : Has(i, "BFLY") ? ctx.ShuffleXor(a, b, c) : throw Unsup();
            Dst(ctx, o[1], res);
            Dst(ctx, o[0], valid);
        }

        static void EmitVoteAmpere(EmitterContext ctx, SassInst i)
        {
            // VOTE[U].ANY|ALL|EQ [Rd,] Pd, Ppred
            IReadOnlyList<Op> o = i.Operands;
            VoteMode mode = Has(i, "ANY") ? VoteMode.Any : Has(i, "ALL") ? VoteMode.All : Has(i, "EQ") ? VoteMode.Eq : throw Unsup();
            IrOperand pred = Src(ctx, o[^1]);
            if (o.Count == 3) Dst(ctx, o[0], EmitBallot(ctx, pred));
            Op pd = o[^2];
            if (!(IsPredKind(pd) && pd.Reg == 7)) Dst(ctx, pd, EmitVote(ctx, mode, pred));
        }

        /// Value of `v` in lane `lane` (an IDX shuffle over the whole warp).
        static IrOperand ReadLane(EmitterContext ctx, IrOperand v, IrOperand lane) => ctx.Shuffle(v, lane, Const(0x1f)).Item1;

        static void EmitReduxAmpere(EmitterContext ctx, SassInst i)
        {
            // REDUX.AND|OR|XOR|SUM|MIN|MAX[.S32] URd, Ra: Ra reduced over the active lanes into a uniform register. Every lane reads every
            // lane (32 IDX shuffles) and keeps the active ones (ballot), so it is right under any active mask.
            IReadOnlyList<Op> o = i.Operands;
            IrOperand v = Src(ctx, o[1]);
            IrOperand mask = EmitBallot(ctx, Const(IrConsts.True));
            bool s = Has(i, "S32");
            Func<IrOperand, IrOperand, IrOperand> f =
                Has(i, "SUM") ? ctx.IAdd : Has(i, "AND") ? ctx.BitwiseAnd : Has(i, "OR") ? ctx.BitwiseOr : Has(i, "XOR") ? ctx.BitwiseExclusiveOr
                : Has(i, "MIN") ? (s ? ctx.IMinimumS32 : ctx.IMinimumU32) : Has(i, "MAX") ? (s ? ctx.IMaximumS32 : ctx.IMaximumU32) : throw Unsup();
            // start from the first active lane's value, then fold in the other active lanes
            IrOperand first = ctx.FindLSB(mask);
            IrOperand acc = ReadLane(ctx, v, first);
            for (int l = 0; l < 32; l++)
            {
                IrOperand take = ctx.BitwiseAnd(ctx.ICompareNotEqual(ctx.BitwiseAnd(mask, Const(1 << l)), Const(0)), ctx.ICompareNotEqual(first, Const(l)));
                acc = ctx.ConditionalSelect(take, f(acc, ReadLane(ctx, v, Const(l))), acc);
            }
            Dst(ctx, o[0], acc);
        }

        static void EmitMatchAmpere(EmitterContext ctx, SassInst i)
        {
            // MATCH.ALL[.U64] [Pd,] Rd, Ra: Pd = every active lane has the same Ra (Ra pair for .U64); Rd = the active mask then, else 0
            if (!Has(i, "ALL")) throw Unsup("MATCH.ANY");
            IReadOnlyList<Op> o = i.Operands;
            int k = IsPredKind(o[0]) ? 1 : 0;
            Op ra = o[k + 1];
            if (ra.Kind != OperandKind.Reg) throw Unsup("source");
            IrOperand mask = EmitBallot(ctx, Const(IrConsts.True));
            IrOperand first = ctx.FindLSB(mask);
            IrOperand lo = Src(ctx, ra);
            IrOperand eq = ctx.ICompareEqual(lo, ReadLane(ctx, lo, first));
            if (Has(i, "U64"))
            {
                IrOperand hi = ra.Reg < 254 ? Gpr(ra.Reg + 1) : Const(0);
                eq = ctx.BitwiseAnd(eq, ctx.ICompareEqual(hi, ReadLane(ctx, hi, first)));
            }
            IrOperand all = EmitVote(ctx, VoteMode.All, eq);
            Dst(ctx, o[k], ctx.ConditionalSelect(all, mask, Const(0)));
            if (k == 1) Dst(ctx, o[0], all);
        }
    }
}
