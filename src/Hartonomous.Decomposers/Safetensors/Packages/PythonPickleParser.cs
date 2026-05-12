using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed class PythonPickleParser
{
    private static readonly object Mark = new();

    private sealed record GlobalRef(string Module, string Name);

    private sealed record PickleStorageRef(string StorageId, string DtypeCanonical, long Numel);

    private sealed record PickleTensorPlaceholder(
        string DtypeCanonical,
        int[] Shape,
        string StorageKey,
        long StorageElementOffset);

    public IReadOnlyList<PickleTensorEntry> Parse(Stream pickleStream)
    {
        ArgumentNullException.ThrowIfNull(pickleStream);

        List<object?> stack = new(64);
        Dictionary<int, object?> memo = new(64);
        object? root = null;

        bool stop = false;
        while (!stop)
        {
            int opByte = pickleStream.ReadByte();
            if (opByte < 0)
            {
                throw new InvalidDataException("Unexpected end of pickle stream before STOP opcode.");
            }
            byte op = (byte)opByte;
            long opOffset = pickleStream.Position - 1;

            switch (op)
            {
                case 0x80: // PROTO
                {
                    int proto = pickleStream.ReadByte();
                    if (proto < 0 || proto > 5)
                    {
                        throw new NotSupportedException($"Unsupported pickle protocol version {proto}.");
                    }
                    break;
                }

                case 0x95: // FRAME
                {
                    Span<byte> frameLen = stackalloc byte[8];
                    ReadExact(pickleStream, frameLen);
                    // length is informational; we stream straight through
                    break;
                }

                case 0x7d: // EMPTY_DICT
                    stack.Add(new Dictionary<object, object?>());
                    break;

                case 0x29: // EMPTY_TUPLE
                    stack.Add(Array.Empty<object?>());
                    break;

                case 0x8f: // EMPTY_SET
                    stack.Add(new HashSet<object?>());
                    break;

                case 0x5d: // EMPTY_LIST
                    stack.Add(new List<object?>());
                    break;

                case 0x28: // MARK
                    stack.Add(Mark);
                    break;

                case 0x2e: // STOP
                    if (stack.Count == 0)
                    {
                        throw new InvalidDataException("STOP opcode encountered with empty stack.");
                    }
                    root = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    stop = true;
                    break;

                case 0x74: // TUPLE (pop until MARK)
                {
                    int markIdx = FindMark(stack);
                    object?[] tuple = new object?[stack.Count - markIdx - 1];
                    for (int i = 0; i < tuple.Length; i++)
                    {
                        tuple[i] = stack[markIdx + 1 + i];
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    stack.Add(tuple);
                    break;
                }

                case 0x85: // TUPLE1
                {
                    object? a = Pop(stack);
                    stack.Add(new object?[] { a });
                    break;
                }

                case 0x86: // TUPLE2
                {
                    object? b = Pop(stack);
                    object? a = Pop(stack);
                    stack.Add(new object?[] { a, b });
                    break;
                }

                case 0x87: // TUPLE3
                {
                    object? c = Pop(stack);
                    object? b = Pop(stack);
                    object? a = Pop(stack);
                    stack.Add(new object?[] { a, b, c });
                    break;
                }

                case 0x6c: // LIST (pop until MARK into list)
                {
                    int markIdx = FindMark(stack);
                    List<object?> list = new(stack.Count - markIdx - 1);
                    for (int i = markIdx + 1; i < stack.Count; i++)
                    {
                        list.Add(stack[i]);
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    stack.Add(list);
                    break;
                }

                case 0x64: // DICT (pop key/value pairs until MARK)
                {
                    int markIdx = FindMark(stack);
                    Dictionary<object, object?> dict = new();
                    for (int i = markIdx + 1; i < stack.Count; i += 2)
                    {
                        object? k = stack[i] ?? throw new InvalidDataException("DICT key must not be null.");
                        object? v = stack[i + 1];
                        dict[k] = v;
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    stack.Add(dict);
                    break;
                }

                case 0x8c: // SHORT_BINUNICODE
                {
                    int len = pickleStream.ReadByte();
                    if (len < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    stack.Add(ReadUtf8(pickleStream, len));
                    break;
                }

                case 0x58: // BINUNICODE (4-byte length + utf8)
                {
                    long len = ReadUInt32(pickleStream);
                    stack.Add(ReadUtf8(pickleStream, checked((int)len)));
                    break;
                }

                case 0x8d: // BINUNICODE8 (8-byte length + utf8)
                {
                    long len = ReadInt64(pickleStream);
                    stack.Add(ReadUtf8(pickleStream, checked((int)len)));
                    break;
                }

                case 0x42: // BINBYTES (4-byte length + bytes)
                {
                    long len = ReadUInt32(pickleStream);
                    byte[] buf = new byte[len];
                    ReadExact(pickleStream, buf);
                    stack.Add(buf);
                    break;
                }

                case 0x43: // SHORT_BINBYTES (1-byte length + bytes)
                {
                    int len = pickleStream.ReadByte();
                    if (len < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    byte[] buf = new byte[len];
                    ReadExact(pickleStream, buf);
                    stack.Add(buf);
                    break;
                }

                case 0x8e: // BINBYTES8 (8-byte length + bytes)
                {
                    long len = ReadInt64(pickleStream);
                    byte[] buf = new byte[len];
                    ReadExact(pickleStream, buf);
                    stack.Add(buf);
                    break;
                }

                case 0x96: // BYTEARRAY8 — protocol 5 (8-byte length + bytes)
                {
                    long len = ReadInt64(pickleStream);
                    byte[] buf = new byte[len];
                    ReadExact(pickleStream, buf);
                    stack.Add(buf);
                    break;
                }

                case 0x68: // BINGET
                {
                    int idx = pickleStream.ReadByte();
                    if (idx < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    if (!memo.TryGetValue(idx, out object? memoVal))
                    {
                        throw new InvalidDataException($"BINGET referenced missing memo index {idx} at offset {opOffset}.");
                    }
                    stack.Add(memoVal);
                    break;
                }

                case 0x6a: // LONG_BINGET
                {
                    int idx = checked((int)ReadUInt32(pickleStream));
                    if (!memo.TryGetValue(idx, out object? memoVal))
                    {
                        throw new InvalidDataException($"LONG_BINGET referenced missing memo index {idx} at offset {opOffset}.");
                    }
                    stack.Add(memoVal);
                    break;
                }

                case 0x71: // BINPUT
                {
                    int idx = pickleStream.ReadByte();
                    if (idx < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    memo[idx] = stack[^1];
                    break;
                }

                case 0x72: // LONG_BINPUT
                {
                    int idx = checked((int)ReadUInt32(pickleStream));
                    memo[idx] = stack[^1];
                    break;
                }

                case 0x94: // MEMOIZE — memo[next] = top
                {
                    memo[memo.Count] = stack[^1];
                    break;
                }

                case 0x4a: // BININT (signed 4 bytes little-endian)
                {
                    Span<byte> ib = stackalloc byte[4];
                    ReadExact(pickleStream, ib);
                    stack.Add((long)BinaryPrimitives.ReadInt32LittleEndian(ib));
                    break;
                }

                case 0x4b: // BININT1
                {
                    int v = pickleStream.ReadByte();
                    if (v < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    stack.Add((long)v);
                    break;
                }

                case 0x4d: // BININT2
                {
                    Span<byte> ib = stackalloc byte[2];
                    ReadExact(pickleStream, ib);
                    stack.Add((long)BinaryPrimitives.ReadUInt16LittleEndian(ib));
                    break;
                }

                case 0x8a: // LONG1
                {
                    int len = pickleStream.ReadByte();
                    if (len < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    stack.Add(ReadLongLE(pickleStream, len));
                    break;
                }

                case 0x8b: // LONG4
                {
                    int len = checked((int)ReadUInt32(pickleStream));
                    stack.Add(ReadLongLE(pickleStream, len));
                    break;
                }

                case 0x88: // NEWTRUE
                    stack.Add(true);
                    break;

                case 0x89: // NEWFALSE
                    stack.Add(false);
                    break;

                case 0x4e: // NONE
                    stack.Add(null);
                    break;

                case 0x63: // GLOBAL "module\nname\n"
                {
                    string module = ReadLine(pickleStream);
                    string name = ReadLine(pickleStream);
                    stack.Add(new GlobalRef(module, name));
                    break;
                }

                case 0x93: // STACK_GLOBAL — pops (module, name) as two unicode strings
                {
                    object? nameObj = Pop(stack);
                    object? modObj = Pop(stack);
                    string mod = modObj as string ?? throw new InvalidDataException("STACK_GLOBAL module operand was not a string.");
                    string nm = nameObj as string ?? throw new InvalidDataException("STACK_GLOBAL name operand was not a string.");
                    stack.Add(new GlobalRef(mod, nm));
                    break;
                }

                case 0x52: // REDUCE — args = pop, callable = pop, push callable(*args)
                {
                    object? argsObj = Pop(stack);
                    object? callableObj = Pop(stack);
                    object? result = ApplyReduce(callableObj, argsObj, opOffset);
                    stack.Add(result);
                    break;
                }

                case 0x62: // BUILD — apply state to top object; for our purposes, drop the state
                {
                    object? state = Pop(stack);
                    object? target = stack[^1];
                    // OrderedDict-style state: list of (k, v) pairs. We currently only need to
                    // honour BUILD on the dict-targets; tensor placeholders ignore extra state.
                    if (target is Dictionary<object, object?> dict && state is List<object?> pairs)
                    {
                        foreach (object? item in pairs)
                        {
                            if (item is object?[] kv && kv.Length == 2 && kv[0] is not null)
                            {
                                dict[kv[0]!] = kv[1];
                            }
                        }
                    }
                    break;
                }

                case 0x81: // NEWOBJ — args = pop, cls = pop, push cls(*args)
                {
                    object? argsObj = Pop(stack);
                    object? clsObj = Pop(stack);
                    stack.Add(ApplyReduce(clsObj, argsObj, opOffset));
                    break;
                }

                case 0x92: // NEWOBJ_EX — kwargs, args, cls -> cls(*args, **kwargs)
                {
                    _ = Pop(stack); // kwargs ignored
                    object? argsObj = Pop(stack);
                    object? clsObj = Pop(stack);
                    stack.Add(ApplyReduce(clsObj, argsObj, opOffset));
                    break;
                }

                case 0x73: // SETITEM — value = pop, key = pop, dict = top
                {
                    object? value = Pop(stack);
                    object? key = Pop(stack);
                    object? dictObj = stack[^1];
                    AssignDictItem(dictObj, key, value);
                    break;
                }

                case 0x75: // SETITEMS — key/value pairs from MARK; dict is below MARK
                {
                    int markIdx = FindMark(stack);
                    object? dictObj = stack[markIdx - 1];
                    for (int i = markIdx + 1; i < stack.Count; i += 2)
                    {
                        AssignDictItem(dictObj, stack[i], stack[i + 1]);
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    break;
                }

                case 0x61: // APPEND — pop value, list is top
                {
                    object? value = Pop(stack);
                    if (stack[^1] is List<object?> list)
                    {
                        list.Add(value);
                    }
                    else
                    {
                        throw new InvalidDataException("APPEND target is not a list.");
                    }
                    break;
                }

                case 0x65: // APPENDS — pop until MARK into list
                {
                    int markIdx = FindMark(stack);
                    if (stack[markIdx - 1] is not List<object?> list)
                    {
                        throw new InvalidDataException("APPENDS target is not a list.");
                    }
                    for (int i = markIdx + 1; i < stack.Count; i++)
                    {
                        list.Add(stack[i]);
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    break;
                }

                case 0x90: // ADDITEMS — set additions; pop until MARK into set
                {
                    int markIdx = FindMark(stack);
                    if (stack[markIdx - 1] is not HashSet<object?> set)
                    {
                        throw new InvalidDataException("ADDITEMS target is not a set.");
                    }
                    for (int i = markIdx + 1; i < stack.Count; i++)
                    {
                        set.Add(stack[i]);
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    break;
                }

                case 0x91: // FROZENSET — pop until MARK into frozen set (we use HashSet)
                {
                    int markIdx = FindMark(stack);
                    HashSet<object?> set = new();
                    for (int i = markIdx + 1; i < stack.Count; i++)
                    {
                        set.Add(stack[i]);
                    }
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    stack.Add(set);
                    break;
                }

                case 0x30: // POP
                    Pop(stack);
                    break;

                case 0x31: // POP_MARK
                {
                    int markIdx = FindMark(stack);
                    stack.RemoveRange(markIdx, stack.Count - markIdx);
                    break;
                }

                case 0x51: // BINPERSID — pop persistent id object, push storage ref
                {
                    object? pid = Pop(stack);
                    stack.Add(BuildStorageRef(pid, opOffset));
                    break;
                }

                case 0x50: // PERSID — readline, push persistent ref by string id
                {
                    string pid = ReadLine(pickleStream);
                    stack.Add(new PickleStorageRef(pid, "F32", 0));
                    break;
                }

                case 0x4c: // LONG (text) "decimal\n"
                {
                    string s = ReadLine(pickleStream);
                    if (s.EndsWith('L'))
                    {
                        s = s[..^1];
                    }
                    if (!BigInteger.TryParse(s, out BigInteger bi))
                    {
                        throw new InvalidDataException($"LONG opcode payload '{s}' could not be parsed.");
                    }
                    stack.Add((long)bi);
                    break;
                }

                default:
                    throw new NotSupportedException(
                        $"Unsupported pickle opcode 0x{op:X2} at offset {opOffset}.");
            }
        }

        if (root is null)
        {
            throw new InvalidDataException("Pickle stream produced no root object.");
        }

        return ExtractEntries(root);
    }

    public static IReadOnlyList<PickleTensorEntry> ParsePackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path must not be null or whitespace.", nameof(packagePath));
        }
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("PyTorch package file not found.", packagePath);
        }

        using FileStream file = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> sig = stackalloc byte[4];
        int read = file.Read(sig);
        file.Position = 0;

        if (read >= 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04)
        {
            using ZipArchive zip = new(file, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? dataPkl = null;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/data.pkl", StringComparison.Ordinal)
                    || string.Equals(entry.Name, "data.pkl", StringComparison.Ordinal))
                {
                    dataPkl = entry;
                    break;
                }
            }
            if (dataPkl is null)
            {
                throw new InvalidDataException(
                    $"PyTorch ZIP archive '{packagePath}' contains no data.pkl entry.");
            }

            using Stream pklStream = dataPkl.Open();
            using MemoryStream buffer = new();
            pklStream.CopyTo(buffer);
            buffer.Position = 0;
            return new PythonPickleParser().Parse(buffer);
        }

        if (read >= 2 && sig[0] == 0x80)
        {
            throw new NotSupportedException(
                $"Pre-1.6 torch pickle format detected at '{packagePath}'. " +
                "Resave with torch.save(..., _use_new_zipfile_serialization=True) or supply the safetensors variant.");
        }

        throw new InvalidDataException(
            $"File '{packagePath}' is neither a torch.save ZIP archive nor a recognized pickle stream.");
    }

    private static int FindMark(List<object?> stack)
    {
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(stack[i], Mark))
            {
                return i;
            }
        }
        throw new InvalidDataException("MARK opcode expected on stack but not found.");
    }

    private static object? Pop(List<object?> stack)
    {
        if (stack.Count == 0)
        {
            throw new InvalidDataException("Pickle stack underflow.");
        }
        object? top = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return top;
    }

    private static void AssignDictItem(object? dictObj, object? key, object? value)
    {
        if (dictObj is not Dictionary<object, object?> dict)
        {
            throw new InvalidDataException("SETITEM/SETITEMS target is not a dict.");
        }
        if (key is null)
        {
            throw new InvalidDataException("Dict key may not be null.");
        }
        dict[key] = value;
    }

    private static string ReadUtf8(Stream stream, int byteLen)
    {
        if (byteLen == 0)
        {
            return string.Empty;
        }
        byte[] buf = new byte[byteLen];
        ReadExact(stream, buf);
        return Encoding.UTF8.GetString(buf);
    }

    private static long ReadUInt32(Stream stream)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExact(stream, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> b = stackalloc byte[8];
        ReadExact(stream, b);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    private static long ReadLongLE(Stream stream, int len)
    {
        if (len == 0)
        {
            return 0;
        }
        if (len > 8)
        {
            byte[] buf = new byte[len];
            ReadExact(stream, buf);
            BigInteger big = new(buf, isUnsigned: false, isBigEndian: false);
            return (long)big;
        }
        Span<byte> data = stackalloc byte[8];
        Span<byte> read = stackalloc byte[len];
        ReadExact(stream, read);
        bool negative = (read[len - 1] & 0x80) != 0;
        for (int i = 0; i < len; i++)
        {
            data[i] = read[i];
        }
        for (int i = len; i < 8; i++)
        {
            data[i] = negative ? (byte)0xFF : (byte)0x00;
        }
        return BinaryPrimitives.ReadInt64LittleEndian(data);
    }

    private static string ReadLine(Stream stream)
    {
        StringBuilder sb = new();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException("Unexpected EOF reading newline-terminated pickle token.");
            }
            if (b == (byte)'\n')
            {
                return sb.ToString();
            }
            sb.Append((char)b);
        }
    }

    private static void ReadExact(Stream stream, Span<byte> dest)
    {
        int total = 0;
        while (total < dest.Length)
        {
            int n = stream.Read(dest.Slice(total));
            if (n <= 0)
            {
                throw new EndOfStreamException(
                    $"Pickle stream ended after {total} bytes; expected {dest.Length}.");
            }
            total += n;
        }
    }

    private static object? ApplyReduce(object? callableObj, object? argsObj, long opOffset)
    {
        if (callableObj is not GlobalRef g)
        {
            throw new InvalidDataException(
                $"REDUCE callable at offset {opOffset} is not a GLOBAL reference (got {callableObj?.GetType().Name ?? "null"}).");
        }
        object?[] args = argsObj as object?[]
            ?? (argsObj is List<object?> list ? list.ToArray() : Array.Empty<object?>());

        if (g.Module.StartsWith("torch.jit", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "torchscript not supported; use safetensors variant of this model");
        }

        // _rebuild_tensor_v2(storage, storage_offset, size, stride, requires_grad, backward_hooks)
        if (g.Module == "torch._utils" &&
            (g.Name == "_rebuild_tensor_v2" || g.Name == "_rebuild_tensor" ||
             g.Name == "_rebuild_parameter" || g.Name == "_rebuild_meta_tensor_no_storage" ||
             g.Name == "_rebuild_qtensor"))
        {
            if (g.Name == "_rebuild_parameter")
            {
                // _rebuild_parameter(data, requires_grad, backward_hooks)
                if (args.Length >= 1 && args[0] is PickleTensorPlaceholder inner)
                {
                    return inner;
                }
                throw new InvalidDataException(
                    $"_rebuild_parameter at offset {opOffset} did not receive a tensor placeholder.");
            }

            if (args.Length < 4)
            {
                throw new InvalidDataException(
                    $"{g.Name} at offset {opOffset} expected at least 4 args; got {args.Length}.");
            }
            PickleStorageRef storage = args[0] as PickleStorageRef
                ?? throw new InvalidDataException(
                    $"{g.Name} at offset {opOffset} expected a storage reference as first arg; got {args[0]?.GetType().Name ?? "null"}.");
            long storageOffset = ToInt64(args[1]);
            int[] shape = ToShape(args[2]);
            // stride at args[3] is ignored for metadata enumeration

            return new PickleTensorPlaceholder(
                storage.DtypeCanonical,
                shape,
                storage.StorageId,
                storageOffset);
        }

        // OrderedDict / dict / list / set constructors
        if ((g.Module == "collections" && g.Name == "OrderedDict")
            || (g.Module == "builtins" && (g.Name == "dict" || g.Name == "OrderedDict")))
        {
            return new Dictionary<object, object?>();
        }
        if (g.Module == "builtins" && g.Name == "list")
        {
            return new List<object?>();
        }
        if (g.Module == "builtins" && g.Name == "set")
        {
            return new HashSet<object?>();
        }
        if (g.Module == "builtins" && g.Name == "tuple")
        {
            return Array.Empty<object?>();
        }

        // Storage class constructors invoked via REDUCE (rare in modern torch.save)
        if (g.Module == "torch" && g.Name.EndsWith("Storage", StringComparison.Ordinal))
        {
            return new GlobalRef(g.Module, g.Name);
        }

        throw new NotSupportedException(
            $"Unsupported REDUCE target {g.Module}.{g.Name} at offset {opOffset}.");
    }

    private static PickleStorageRef BuildStorageRef(object? pid, long opOffset)
    {
        // Persistent id for a torch storage is normally a tuple:
        //   ('storage', <storage_dtype_class>, <storage_id_str>, <location_str>, <numel>)
        if (pid is not object?[] tuple || tuple.Length < 3)
        {
            throw new InvalidDataException(
                $"BINPERSID at offset {opOffset} expected a tuple persistent id; got {pid?.GetType().Name ?? "null"}.");
        }

        if (tuple[0] is not string tag || tag != "storage")
        {
            throw new InvalidDataException(
                $"BINPERSID at offset {opOffset} expected leading 'storage' tag.");
        }

        GlobalRef? dtypeClass = tuple[1] as GlobalRef;
        string dtypeCanonical = dtypeClass is null
            ? "F32"
            : MapStorageDtype(dtypeClass);

        string storageId = tuple[2] switch
        {
            string s => s,
            long n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException(
                $"BINPERSID at offset {opOffset} expected string or int storage id; got {tuple[2]?.GetType().Name ?? "null"}."),
        };

        long numel = 0;
        if (tuple.Length >= 5)
        {
            numel = ToInt64(tuple[4]);
        }

        return new PickleStorageRef(storageId, dtypeCanonical, numel);
    }

    private static string MapStorageDtype(GlobalRef cls)
    {
        if (cls.Module != "torch")
        {
            throw new NotSupportedException($"Unknown storage dtype class {cls.Module}.{cls.Name}.");
        }
        return cls.Name switch
        {
            "FloatStorage" => "F32",
            "DoubleStorage" => "F64",
            "HalfStorage" => "F16",
            "BFloat16Storage" => "BF16",
            "CharStorage" => "I8",
            "ByteStorage" => "U8",
            "ShortStorage" => "I16",
            "IntStorage" => "I32",
            "LongStorage" => "I64",
            "BoolStorage" => "BOOL",
            "Float8_e4m3fnStorage" => "F8_E4M3",
            "Float8_e5m2Storage" => "F8_E5M2",
            "UntypedStorage" => "U8",
            _ => throw new NotSupportedException($"Unknown torch storage class torch.{cls.Name}."),
        };
    }

    private static int DtypeByteSize(string dtypeCanonical) => dtypeCanonical switch
    {
        "F32" => 4,
        "F64" => 8,
        "F16" => 2,
        "BF16" => 2,
        "I8" => 1,
        "U8" => 1,
        "I16" => 2,
        "I32" => 4,
        "I64" => 8,
        "BOOL" => 1,
        "F8_E4M3" => 1,
        "F8_E5M2" => 1,
        _ => throw new NotSupportedException($"No byte size known for canonical dtype '{dtypeCanonical}'."),
    };

    private static long ToInt64(object? v) => v switch
    {
        long l => l,
        int i => i,
        BigInteger b => (long)b,
        bool bv => bv ? 1L : 0L,
        null => 0L,
        _ => throw new InvalidDataException($"Expected integer, got {v.GetType().Name}."),
    };

    private static int[] ToShape(object? v)
    {
        switch (v)
        {
            case object?[] arr:
            {
                int[] shape = new int[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    shape[i] = checked((int)ToInt64(arr[i]));
                }
                return shape;
            }
            case List<object?> list:
            {
                int[] shape = new int[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    shape[i] = checked((int)ToInt64(list[i]));
                }
                return shape;
            }
            case null:
                return Array.Empty<int>();
            default:
                throw new InvalidDataException($"Tensor shape was not a tuple/list (got {v.GetType().Name}).");
        }
    }

    private static List<PickleTensorEntry> ExtractEntries(object root)
    {
        if (root is not Dictionary<object, object?> dict)
        {
            throw new InvalidDataException(
                $"Pickle root is not a dict (got {root.GetType().Name}); expected a state_dict.");
        }

        List<PickleTensorEntry> entries = new(dict.Count);
        foreach (KeyValuePair<object, object?> kv in dict)
        {
            if (kv.Key is not string name)
            {
                throw new InvalidDataException(
                    $"state_dict key '{kv.Key}' is not a string ({kv.Key.GetType().Name}).");
            }
            if (kv.Value is not PickleTensorPlaceholder ph)
            {
                // Skip non-tensor entries (e.g., '_metadata' OrderedDict that some checkpoints include).
                continue;
            }

            long elements = 1;
            foreach (int dim in ph.Shape)
            {
                if (dim < 0)
                {
                    throw new InvalidDataException(
                        $"Tensor '{name}' has negative dimension {dim}.");
                }
                elements = checked(elements * dim);
            }
            long byteLen = checked(elements * DtypeByteSize(ph.DtypeCanonical));

            entries.Add(new PickleTensorEntry(
                name,
                ph.DtypeCanonical,
                ph.Shape,
                ph.StorageKey,
                ph.StorageElementOffset,
                byteLen));
        }
        return entries;
    }
}
