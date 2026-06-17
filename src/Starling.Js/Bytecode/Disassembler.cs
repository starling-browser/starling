using System.Buffers.Binary;
using System.Text;

namespace Starling.Js.Bytecode;

/// <summary>
/// Renders a <see cref="Chunk"/> as human-readable text. Used by tests
/// (snapshot/diff) and by the future devtools bytecode viewer.
/// </summary>
public static class Disassembler
{
    public static string Disassemble(Chunk chunk)
    {
        var sb = new StringBuilder();
        if (chunk.Name is not null)
        {
            sb.Append("# chunk: ").AppendLine(chunk.Name);
        }

        if (chunk.Constants.Count > 0)
        {
            sb.AppendLine("# constants:");
            for (var k = 0; k < chunk.Constants.Count; k++)
            {
                sb.Append("  ").Append(k).Append(": ").AppendLine(FormatConstant(chunk.Constants[k]));
            }
        }
        sb.AppendLine("# code:");

        var code = chunk.Code;
        var i = 0;
        while (i < code.Length)
        {
            sb.Append("  ").Append(i.ToString("D4")).Append("  ");
            var op = (Opcode)code[i];
            i++;
            switch (op)
            {
                // u16 operand opcodes
                case Opcode.LoadConst:
                case Opcode.LoadFunction:
                case Opcode.LoadGlobal:
                case Opcode.LoadGlobalChecked:
                case Opcode.StoreGlobal:
                case Opcode.DeclareGlobalVar:
                case Opcode.SetFunctionName:
                case Opcode.DefineGetter:
                case Opcode.DefineSetter:
                case Opcode.DefineDataProperty:
                case Opcode.RestArray:
                case Opcode.RestObject:
                case Opcode.LoadSuperProperty:
                case Opcode.StoreSuperProperty:
                case Opcode.PrivateGet:
                case Opcode.PrivateSet:
                case Opcode.DefinePrivateField:
                case Opcode.PrivateIn:
                case Opcode.TemplateObject:
                case Opcode.BuildClass:
                case Opcode.LoadEvalScope:
                case Opcode.StoreEvalScope:
                case Opcode.DeclareEvalVar:
                case Opcode.StoreEvalVar:
                case Opcode.DeleteEvalVar:
                    {
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        sb.Append(op).Append(' ').Append(idx);
                        var c = chunk.Constants[idx];
                        sb.Append("  ; ").Append(FormatConstant(c));
                        break;
                    }
                // named-property opcodes: [u16 nameIdx][u16 cacheId]
                case Opcode.LoadProperty:
                case Opcode.StoreProperty:
                    {
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        var cacheId = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i + 2, 2));
                        i += 4;
                        sb.Append(op).Append(' ').Append(idx);
                        sb.Append("  ; ").Append(FormatConstant(chunk.Constants[idx]));
                        sb.Append(" [ic ").Append(cacheId).Append(']');
                        break;
                    }
                // u16 local-slot operand opcodes (slots are addressed with a
                // u16 — see ChunkBuilder.EmitSlot).
                case Opcode.LoadLocal:
                case Opcode.StoreLocal:
                case Opcode.DeclareLocal:
                case Opcode.DeclareLocalTdz:
                case Opcode.InitCellLocal:
                case Opcode.InitCellLocalTdz:
                case Opcode.LoadCellLocal:
                case Opcode.StoreCellLocal:
                case Opcode.LoadLocalChecked:
                case Opcode.LoadCellLocalChecked:
                case Opcode.StoreCellLocalChecked:
                case Opcode.PromoteParamCell:
                case Opcode.RefreshLetBinding:
                case Opcode.MakeArguments:
                case Opcode.BindCallee:
                // u16 upvalue-index operand opcodes (upvalue indices are
                // addressed with a u16 — see ChunkBuilder.EmitUpvalue).
                case Opcode.LoadUpvalue:
                case Opcode.LoadUpvalueChecked:
                case Opcode.StoreUpvalue:
                case Opcode.StoreUpvalueChecked:
                case Opcode.LoadUpvalueCell:
                    {
                        var slot = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        sb.Append(op).Append(' ').Append(slot);
                        break;
                    }
                // u8 operand opcodes
                case Opcode.Call:
                case Opcode.CallMethod:
                case Opcode.New:
                case Opcode.Suspend:
                    {
                        var slot = code[i];
                        i++;
                        sb.Append(op).Append(' ').Append(slot);
                        break;
                    }
                // wp:M3-72 — u16 + u8 — DirectEval [descriptorIdx][argc]
                case Opcode.DirectEval:
                    {
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var argc = code[i];
                        i++;
                        sb.Append(op).Append(' ').Append(idx).Append(' ').Append(argc);
                        sb.Append("  ; direct-eval argc=").Append(argc);
                        break;
                    }
                // u16 slot + u16 paramCount + paramCount × u16 paramSlot
                case Opcode.MakeMappedArguments:
                    {
                        var slot = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var paramCount = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        sb.Append(op).Append(' ').Append(slot).Append(' ').Append(paramCount);
                        sb.Append("  ; mapped slots=[");
                        for (var p = 0; p < paramCount; p++)
                        {
                            var ps = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                            i += 2;
                            if (p > 0)
                            {
                                sb.Append(',');
                            }

                            sb.Append(ps == 0xFFFF ? "-" : ps.ToString());
                        }
                        sb.Append(']');
                        break;
                    }
                // u16 + u16 — MakeClosure [fnIdx][nUpvalues]
                case Opcode.MakeClosure:
                    {
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var n = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        sb.Append(op).Append(' ').Append(idx).Append(' ').Append(n);
                        sb.Append("  ; template=").Append(FormatConstant(chunk.Constants[idx]))
                          .Append(" upvalues=").Append(n);
                        break;
                    }
                // u16 + u16 + u16 — LoadRegExp [srcIdx][flagsIdx][cacheId]
                case Opcode.LoadRegExp:
                    {
                        var srcIdx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var flagsIdx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var cacheId = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        sb.Append(op).Append(' ').Append(srcIdx).Append(' ').Append(flagsIdx).Append(' ').Append(cacheId);
                        sb.Append("  ; /").Append(FormatConstant(chunk.Constants[srcIdx]))
                          .Append('/').Append(FormatConstant(chunk.Constants[flagsIdx]));
                        break;
                    }
                // i32 jump-offset opcodes
                case Opcode.Jump:
                case Opcode.JumpIfTrue:
                case Opcode.JumpIfFalse:
                case Opcode.JumpIfNotNullish:
                case Opcode.LogAnd:
                case Opcode.LogOr:
                case Opcode.Coalesce:
                    {
                        var offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i, 4));
                        i += 4;
                        sb.Append(op).Append(' ').Append(offset);
                        sb.Append("  ; → ").Append((i + offset).ToString("D4"));
                        break;
                    }
                case Opcode.EnterTry:
                    {
                        var catchOffset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i, 4));
                        i += 4;
                        var finallyOffset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i, 4));
                        i += 4;
                        sb.Append(op).Append(' ').Append(catchOffset).Append(' ').Append(finallyOffset);
                        sb.Append("  ; catch→").Append(catchOffset == -1 ? "<none>" : (i + catchOffset).ToString("D4"))
                          .Append(" finally→").Append(finallyOffset == -1 ? "<none>" : (i + finallyOffset).ToString("D4"));
                        break;
                    }
                // u8 + i32 — BranchThroughFinally [unwindCount][target]
                case Opcode.BranchThroughFinally:
                    {
                        var unwindCount = code[i];
                        i++;
                        var offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i, 4));
                        i += 4;
                        sb.Append(op).Append(' ').Append(unwindCount).Append(' ').Append(offset);
                        sb.Append("  ; unwind=").Append(unwindCount)
                          .Append(" → ").Append((i + offset).ToString("D4"));
                        break;
                    }
                // u16 nameIdx + i32 missOffset — with-aware identifier opcodes
                case Opcode.WithLoadOrMiss:
                case Opcode.WithLoadMethodOrMiss:
                case Opcode.WithStoreOrMiss:
                case Opcode.WithDeleteOrMiss:
                    {
                        var nameIdx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var miss = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i, 4));
                        i += 4;
                        sb.Append(op).Append(' ').Append(nameIdx).Append(' ').Append(miss);
                        sb.Append("  ; ").Append(FormatConstant(chunk.Constants[nameIdx]))
                          .Append(" miss→").Append((i + miss).ToString("D4"));
                        break;
                    }
                // u16 nameIdx + u16 baseSlot + i32 missOffset — with-aware
                // compound-assignment read/write halves (§13.15.2).
                case Opcode.WithCompoundLoad:
                case Opcode.WithCompoundStore:
                    {
                        var nameIdx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var baseSlot = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(i, 2));
                        i += 2;
                        var miss = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i, 4));
                        i += 4;
                        sb.Append(op).Append(' ').Append(nameIdx).Append(" base=").Append(baseSlot)
                          .Append(' ').Append(miss);
                        sb.Append("  ; ").Append(FormatConstant(chunk.Constants[nameIdx]))
                          .Append(" miss→").Append((i + miss).ToString("D4"));
                        break;
                    }
                case Opcode.YieldDelegate:
                    {
                        var isAsync = code[i];
                        i++;
                        sb.Append(op).Append(' ').Append(isAsync);
                        break;
                    }
                default:
                    sb.Append(op);
                    break;
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatConstant(object? c) => c switch
    {
        null => "(null)",
        string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        JsBigIntPlaceholder bi => $"{bi.Digits}n",
        TemplateObjectTemplate t => $"template[{t.Cooked.Count}]",
        _ => c.ToString() ?? "(?)",
    };
}
