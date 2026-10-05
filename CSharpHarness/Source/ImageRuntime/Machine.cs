// Copyright (c) 2026 Russell Philip Smithson. SPDX-License-Identifier: GPL-3.0-only
// Generic bounded JSON VM. No game operations, I/O, reflection, eval, or dynamic loading.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageRuntime;

public sealed class VMException(string message) : Exception(message);

public static class Machine
{
    public const string Schema = "image-vm/1";
    public const int MaxRegisters = 32, MaxInstructions = 256, MaxProgramBytes = 16384;
    public const int MaxFuel = 50000, MaxDepth = 16, MaxValueNodes = 2048;
    public const int MaxLiveNodes = 16384, MaxContainerItems = 1024, MaxStringBytes = 256;
    public const int MaxWork = 2_000_000, MaxJsonBytes = 1_048_576;
    public const long MaxInteger = (1L << 53) - 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Dictionary<string, int> Arity = new(StringComparer.Ordinal)
    {
        ["CONST"] = 3, ["COPY"] = 3, ["GET"] = 4, ["SET"] = 5,
        ["ADD"] = 4, ["SUB"] = 4, ["MUL"] = 4, ["MOD"] = 4,
        ["EQ"] = 4, ["LT"] = 4, ["LE"] = 4, ["AND"] = 4, ["OR"] = 4,
        ["NOT"] = 3, ["LEN"] = 3, ["LIST"] = 3, ["APPEND"] = 4,
        ["PREPEND"] = 4, ["SLICE"] = 5, ["CONTAINS"] = 4,
        ["JUMP"] = 2, ["JZ"] = 3, ["RETURN"] = 2
    };

    private sealed class Work
    {
        public int Remaining = MaxWork;
        public void Use()
        {
            if (--Remaining < 0) throw new VMException("VM work budget exhausted");
        }
    }

    private static object? Parse(string source)
    {
        if (source is null || source.Length > MaxJsonBytes)
            throw new VMException("VM JSON input byte limit exceeded");
        try
        {
            if (Utf8.GetByteCount(source) > MaxJsonBytes)
                throw new VMException("VM JSON input byte limit exceeded");
            using JsonDocument document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 64 });
            int nodes = 0;
            object? Visit(JsonElement item)
            {
                // Cap parsing allocations before the stricter per-value snapshot check.
                if (++nodes > 16384) throw new VMException("VM JSON parse node limit exceeded");
                switch (item.ValueKind)
                {
                    case JsonValueKind.Null: return null;
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.String: return item.GetString()!;
                    case JsonValueKind.Number:
                        string raw = item.GetRawText();
                        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || !item.TryGetInt64(out long number))
                            throw new VMException("VM values require integers; floats are unsupported");
                        return number;
                    case JsonValueKind.Array:
                        if (item.GetArrayLength() > MaxContainerItems)
                            throw new VMException("VM container size exceeded");
                        return item.EnumerateArray().Select(Visit).ToList();
                    case JsonValueKind.Object:
                        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (JsonProperty property in item.EnumerateObject())
                        {
                            if (++nodes > 16384 || result.Count >= MaxContainerItems)
                                throw new VMException("VM JSON parse node/container limit exceeded");
                            if (!result.TryAdd(property.Name, Visit(property.Value)))
                                throw new VMException("Duplicate VM JSON object key");
                        }
                        return result;
                    default: throw new VMException("Unsupported VM JSON value");
                }
            }
            return Visit(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or EncoderFallbackException or InvalidOperationException)
        {
            throw new VMException("Malformed VM JSON or invalid Unicode string");
        }
    }

    private static (object? Value, int Nodes) Clone(object? value, int maximum = MaxValueNodes, Work? work = null)
    {
        int nodes = 0;
        object? Visit(object? item, int depth)
        {
            nodes++; work?.Use();
            if (nodes > maximum || depth > MaxDepth)
                throw new VMException("VM value node/depth budget exceeded");
            if (item is null or bool) return item;
            if (item is long integer)
            {
                if (integer < -MaxInteger || integer > MaxInteger)
                    throw new VMException("VM integer range exceeded");
                return integer;
            }
            if (item is string text)
            {
                try
                {
                    if (Utf8.GetByteCount(text) > MaxStringBytes)
                        throw new VMException("VM string budget exceeded");
                }
                catch (EncoderFallbackException) { throw new VMException("Invalid VM Unicode string"); }
                return text;
            }
            if (item is List<object?> array)
            {
                if (array.Count > MaxContainerItems) throw new VMException("VM container size exceeded");
                return array.Select(child => Visit(child, depth + 1)).ToList();
            }
            if (item is Dictionary<string, object?> map)
            {
                if (map.Count > MaxContainerItems) throw new VMException("VM container size exceeded");
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in map)
                    copy.Add((string)Visit(pair.Key, depth + 1)!, Visit(pair.Value, depth + 1));
                return copy;
            }
            throw new VMException("VM values must use exact JSON types; floats are unsupported");
        }
        return (Visit(value, 0), nodes);
    }

    private sealed class UnicodeComparer : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            using var a = (left ?? "").EnumerateRunes().GetEnumerator();
            using var b = (right ?? "").EnumerateRunes().GetEnumerator();
            while (true)
            {
                bool moreA = a.MoveNext(), moreB = b.MoveNext();
                if (!moreA || !moreB) return moreA.CompareTo(moreB);
                int difference = a.Current.Value.CompareTo(b.Current.Value);
                if (difference != 0) return difference;
            }
        }
    }

    // Matches Python json.dumps(sort_keys=True,separators=(',',':'),ensure_ascii=True).
    private static string Canonical(object? value)
    {
        var output = new StringBuilder();
        void String(string text)
        {
            output.Append('"');
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (ch < 32 || ch > 126) output.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(ch);
                        break;
                }
            }
            output.Append('"');
        }
        void Write(object? item)
        {
            switch (item)
            {
                case null: output.Append("null"); break;
                case bool boolean: output.Append(boolean ? "true" : "false"); break;
                case long integer: output.Append(integer.ToString(CultureInfo.InvariantCulture)); break;
                case string text: String(text); break;
                case List<object?> list:
                    output.Append('[');
                    for (int i = 0; i < list.Count; i++) { if (i > 0) output.Append(','); Write(list[i]); }
                    output.Append(']'); break;
                case Dictionary<string, object?> map:
                    output.Append('{'); bool first = true;
                    foreach (string key in map.Keys.OrderBy(key => key, new UnicodeComparer()))
                    {
                        if (!first) output.Append(','); first = false;
                        String(key); output.Append(':'); Write(map[key]);
                    }
                    output.Append('}'); break;
                default: throw new VMException("Unsupported canonical VM value");
            }
        }
        Write(value); return output.ToString();
    }

    /// <summary>Canonical sorted ASCII JSON for carrier envelopes; input is bounded to 1 MiB.</summary>
    /// <remarks>Envelopes allow strings larger than VM values, e.g. an embedded assembly.</remarks>
    public static string CanonicalJson(string json) => Canonical(Parse(json));

    private static int Register(object? value)
    {
        if (value is not long index || index < 0 || index >= MaxRegisters)
            throw new VMException("Register index must be an integer in 0..31");
        return (int)index;
    }

    private static (List<object?> Code, string Hash) Program(object? program)
    {
        var snapshot = Clone(program, 8192).Value as Dictionary<string, object?>;
        if (snapshot is null || snapshot.Count != 2 || !snapshot.TryGetValue("schema", out object? schema)
            || schema is not string schemaName || schemaName != Schema || !snapshot.ContainsKey("code"))
            throw new VMException("Unsupported VM program schema");
        string raw = Canonical(snapshot);
        if (raw.Length > MaxProgramBytes) throw new VMException("VM program byte limit exceeded");
        if (snapshot["code"] is not List<object?> code || code.Count < 1 || code.Count > MaxInstructions)
            throw new VMException("VM requires 1..256 instructions");
        bool hasReturn = false;
        foreach (object? item in code)
        {
            if (item is not List<object?> instruction || instruction.Count == 0 || instruction[0] is not string op)
                throw new VMException("VM instruction must be an opcode array");
            if (!Arity.TryGetValue(op, out int arity) || instruction.Count != arity)
                throw new VMException("Unknown opcode or wrong instruction arity");
            if (op is "JUMP" or "JZ")
            {
                if (op == "JZ") Register(instruction[1]);
                if (instruction[^1] is not long target || target < 0 || target >= code.Count)
                    throw new VMException("Jump target outside the program");
            }
            else if (op == "CONST") { Register(instruction[1]); Clone(instruction[2]); }
            else if (op == "LIST")
            {
                Register(instruction[1]);
                if (instruction[2] is not List<object?> operands || operands.Count > MaxRegisters)
                    throw new VMException("LIST requires at most 32 register operands");
                foreach (object? operand in operands) Register(operand);
            }
            else foreach (object? operand in instruction.Skip(1)) Register(operand);
            if (op == "RETURN") hasReturn = true;
        }
        if (!hasReturn) throw new VMException("VM program requires a RETURN instruction");
        return (code, Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(raw))));
    }

    private static long Integer(object? value) => value is long integer ? integer
        : throw new VMException("Arithmetic and indexes require integers, not booleans");
    private static bool Boolean(object? value) => value is bool boolean ? boolean
        : throw new VMException("Logical operations and JZ require booleans");

    private static bool Equal(object? a, object? b, Work work)
    {
        work.Use();
        if (a is null || b is null) return a is null && b is null;
        if (a.GetType() != b.GetType()) return false;
        if (a is List<object?> listA && b is List<object?> listB)
            return listA.Count == listB.Count && listA.Zip(listB).All(pair => Equal(pair.First, pair.Second, work));
        if (a is Dictionary<string, object?> mapA && b is Dictionary<string, object?> mapB)
            return mapA.Count == mapB.Count && mapA.Keys.All(mapB.ContainsKey)
                && mapA.All(pair => Equal(pair.Value, mapB[pair.Key], work));
        return a.Equals(b);
    }

    /// <summary>Execute generic image bytecode. JSON inputs contain no executable CLR code.</summary>
    public static string Execute(string programJson, string stateJson, string inputJson, int fuel = 30000)
    {
        if (fuel < 1 || fuel > MaxFuel) throw new VMException("Fuel must be an integer in 1..50000");
        var (code, hash) = Program(Parse(programJson));
        var work = new Work();
        object missing = new();
        object?[] registers = Enumerable.Repeat<object?>(missing, MaxRegisters).ToArray();
        int[] sizes = new int[MaxRegisters];
        object? Get(object? index)
        {
            object? value = registers[Register(index)];
            return ReferenceEquals(value, missing) ? throw new VMException("Read of an uninitialized register") : value;
        }
        void Put(int index, object? value)
        {
            var (cloned, nodes) = Clone(value, work: work);
            if (sizes.Sum() - sizes[index] + nodes > MaxLiveNodes)
                throw new VMException("VM live-register memory limit exceeded");
            registers[index] = cloned; sizes[index] = nodes;
        }
        Put(0, Parse(stateJson)); Put(1, Parse(inputJson));
        int pc = 0, steps = 0;
        while (pc >= 0 && pc < code.Count)
        {
            if (steps >= fuel) throw new VMException("VM instruction fuel exhausted");
            var instruction = (List<object?>)code[pc++]!;
            string op = (string)instruction[0]!; steps++;
            if (op == "RETURN")
            {
                object? result = Clone(Get(instruction[1]), work: work).Value;
                return Canonical(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = result, ["steps"] = (long)steps, ["program_sha256"] = hash,
                    ["execution_class"] = "BOUNDED_JSON_REGISTER_VM_CPU", ["schema"] = Schema,
                    ["work_units"] = (long)(MaxWork - work.Remaining)
                });
            }
            if (op == "JUMP") { pc = (int)(long)instruction[1]!; continue; }
            if (op == "JZ")
            {
                if (!Boolean(Get(instruction[1]))) pc = (int)(long)instruction[2]!;
                continue;
            }
            int destination = Register(instruction[1]);
            object? value;
            switch (op)
            {
                case "CONST": value = instruction[2]; break;
                case "COPY": value = Get(instruction[2]); break;
                case "GET": case "SET":
                {
                    object? container = Get(instruction[2]), key = Get(instruction[3]);
                    if (container is Dictionary<string, object?> map)
                    {
                        if (key is not string name) throw new VMException("Dictionary access requires a string key");
                        if (op == "GET")
                        {
                            if (!map.TryGetValue(name, out value)) throw new VMException("Missing dictionary key");
                        }
                        else
                        {
                            var copy = (Dictionary<string, object?>)Clone(map, work: work).Value!;
                            copy[name] = Get(instruction[4]); value = copy;
                        }
                    }
                    else if (container is List<object?> list)
                    {
                        long index = Integer(key);
                        if (index < 0 || index >= list.Count) throw new VMException("List index out of range");
                        if (op == "GET") value = list[(int)index];
                        else
                        {
                            var copy = (List<object?>)Clone(list, work: work).Value!;
                            copy[(int)index] = Get(instruction[4]); value = copy;
                        }
                    }
                    else throw new VMException("GET/SET requires a dictionary or list");
                    break;
                }
                case "ADD": case "SUB": case "MUL": case "MOD": case "LT": case "LE":
                {
                    long a = Integer(Get(instruction[2])), b = Integer(Get(instruction[3]));
                    if (op == "LT") value = a < b;
                    else if (op == "LE") value = a <= b;
                    else if (op == "MOD")
                    {
                        if (b <= 0) throw new VMException("MOD divisor must be positive");
                        long remainder = a % b; value = remainder < 0 ? remainder + b : remainder;
                    }
                    else
                    {
                        try { value = op == "ADD" ? checked(a + b) : op == "SUB" ? checked(a - b) : checked(a * b); }
                        catch (OverflowException) { throw new VMException("VM integer range exceeded"); }
                    }
                    break;
                }
                case "EQ": value = Equal(Get(instruction[2]), Get(instruction[3]), work); break;
                case "AND": case "OR":
                {
                    bool a = Boolean(Get(instruction[2])), b = Boolean(Get(instruction[3]));
                    value = op == "AND" ? a && b : a || b; break;
                }
                case "NOT": value = !Boolean(Get(instruction[2])); break;
                case "LEN":
                    value = Get(instruction[2]) switch
                    {
                        List<object?> list => (long)list.Count,
                        Dictionary<string, object?> map => (long)map.Count,
                        string text => (long)text.EnumerateRunes().Count(),
                        _ => throw new VMException("LEN requires a list, dictionary or string")
                    }; break;
                case "LIST": value = ((List<object?>)instruction[2]!).Select(Get).ToList(); break;
                case "APPEND": case "PREPEND": case "SLICE": case "CONTAINS":
                {
                    object? container = Get(instruction[2]);
                    if (op == "CONTAINS" && container is Dictionary<string, object?> map)
                    {
                        if (Get(instruction[3]) is not string key)
                            throw new VMException("Dictionary membership requires a string key");
                        value = map.ContainsKey(key); break;
                    }
                    if (container is not List<object?> list) throw new VMException(op + " requires a list");
                    if (op == "CONTAINS") value = list.Any(item => Equal(item, Get(instruction[3]), work));
                    else if (op == "SLICE")
                    {
                        long start = Integer(Get(instruction[3])), end = Integer(Get(instruction[4]));
                        if (start < 0 || start > end || end > list.Count)
                            throw new VMException("SLICE requires 0 <= start <= end <= length");
                        value = list.GetRange((int)start, (int)(end - start));
                    }
                    else
                    {
                        var copy = new List<object?>(list);
                        if (op == "APPEND") copy.Add(Get(instruction[3]));
                        else copy.Insert(0, Get(instruction[3]));
                        value = copy;
                    }
                    break;
                }
                default: throw new VMException("Unsupported VM opcode");
            }
            Put(destination, value);
        }
        throw new VMException("VM program ended without RETURN");
    }
}
