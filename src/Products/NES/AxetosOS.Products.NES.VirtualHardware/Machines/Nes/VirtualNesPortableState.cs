using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

/// <summary>
/// Versioned cross-process machine-state codec used by host applications that
/// persist NES save states. The payload contains mutable physical machine state
/// only; ROM bytes and host/application chrome are never embedded in it.
/// </summary>
internal static class VirtualNesPortableState
{
    private const int HardwareFormatVersion = 1;
    private const int MaximumMemberCount = 200_000;
    private const int MaximumMemberPayloadBytes = 64 * 1024 * 1024;
    private static readonly byte[] HardwareMagic = "AXNESHW1"u8.ToArray();

    public static byte[] Capture(RegionalNesVirtualMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var capture = new CaptureContext(machine.Slot.InsertedImage);
        capture.Visit(machine);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(HardwareMagic);
        writer.Write(HardwareFormatVersion);
        writer.Write(capture.Members.Count);
        foreach (var member in capture.Members)
        {
            writer.Write((byte)member.Kind);
            writer.Write(member.Signature);
            writer.Write(member.Payload.Length);
            writer.Write(member.Payload);
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static void Restore(RegionalNesVirtualMachine machine, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (payload.Length == 0 || payload.Length > MaximumMemberPayloadBytes)
            throw new InvalidDataException("NES portable hardware state has an invalid size.");

        var savedMembers = ReadMembers(payload);
        var targets = new RestoreContext(machine.Slot.InsertedImage);
        targets.Visit(machine);

        if (targets.Members.Count != savedMembers.Length)
        {
            throw new InvalidDataException(
                $"NES portable hardware-state schema mismatch: save has {savedMembers.Length:N0} members, " +
                $"loaded hardware has {targets.Members.Count:N0}.");
        }

        for (var index = 0; index < savedMembers.Length; index++)
        {
            var saved = savedMembers[index];
            var target = targets.Members[index];
            if (saved.Kind != target.Kind || !string.Equals(saved.Signature, target.Signature, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"NES portable hardware-state schema mismatch at member {index:N0}. " +
                    "The save was created by an incompatible hardware-state layout.");
            }
        }

        // Validate the complete schema before mutating the machine. Only after
        // every member matches do we apply the saved values in deterministic order.
        for (var index = 0; index < savedMembers.Length; index++)
        {
            targets.Members[index].Restore(savedMembers[index].Payload);
        }
    }

    private static PortableMember[] ReadMembers(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadBytes(HardwareMagic.Length);
        if (!magic.AsSpan().SequenceEqual(HardwareMagic))
            throw new InvalidDataException("The NES portable hardware state has an invalid signature.");

        var version = reader.ReadInt32();
        if (version != HardwareFormatVersion)
        {
            throw new NotSupportedException(
                $"NES portable hardware-state format {version} is not supported by this engine (expected {HardwareFormatVersion}).");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumMemberCount)
            throw new InvalidDataException("NES portable hardware state contains an invalid member count.");

        var members = new PortableMember[count];
        for (var index = 0; index < count; index++)
        {
            var kindValue = reader.ReadByte();
            if (!Enum.IsDefined(typeof(PortableMemberKind), kindValue))
                throw new InvalidDataException("NES portable hardware state contains an invalid member kind.");

            var signature = reader.ReadString();
            var length = reader.ReadInt32();
            if (length < 0 || length > MaximumMemberPayloadBytes || length > stream.Length - stream.Position)
                throw new InvalidDataException("NES portable hardware state contains an invalid member payload length.");

            var memberPayload = reader.ReadBytes(length);
            if (memberPayload.Length != length)
                throw new EndOfStreamException("NES portable hardware state ended unexpectedly.");

            members[index] = new PortableMember((PortableMemberKind)kindValue, signature, memberPayload);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("NES portable hardware state contains trailing data.");

        return members;
    }

    private abstract class TraversalContext
    {
        protected static readonly Assembly HardwareAssembly = typeof(RegionalNesVirtualMachine).Assembly;
        protected static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _immutable = new(ReferenceEqualityComparer.Instance);
        private readonly byte[]? _prgRom;
        private readonly byte[]? _chrRom;

        protected TraversalContext(VirtualHardwareNesRomImage? image)
        {
            if (image is null) return;
            _prgRom = image.PrgRom;
            _chrRom = image.ChrRom;
            _immutable.Add(image);
            _immutable.Add(image.PrgRom);
            _immutable.Add(image.ChrRom);
        }

        public void Visit(object? value)
        {
            if (value is null) return;

            var type = value.GetType();
            if (IsTerminalObject(type)) return;
            if (_immutable.Contains(value)) return;
            if (!_visited.Add(value)) return;

            if (value is Array array)
            {
                VisitArrayReferences(array);
                return;
            }

            // The machine graph intentionally contains framework-owned lists and
            // sets that group physical/runtime objects. Their collection internals
            // are topology, not state; traverse their hardware members only.
            if (type.Assembly != HardwareAssembly)
            {
                // Traverse only ordered framework collections. HashSet/Dictionary
                // enumeration can depend on object identity/hash allocation and would
                // make a cross-process schema nondeterministic. Unordered topology
                // members are reachable through the machine's ordered board/slot graph.
                if (value is IList list)
                {
                    foreach (var item in list)
                    {
                        Visit(item);
                    }
                }
                return;
            }

            if (type.Assembly != HardwareAssembly) return;

            foreach (var field in FieldCache.GetOrAdd(type, BuildFieldList))
            {
                var fieldValue = field.GetValue(value);
                ObserveField(value, field, fieldValue);
                Visit(fieldValue);
            }
        }

        protected bool IsImmutableArray(FieldInfo field, Array array)
        {
            if (_immutable.Contains(array)) return true;
            if (array is not byte[] bytes) return false;

            // Cartridge packages intentionally own private ROM copies so their
            // pin-driven read circuitry does not depend on the loader object. A
            // portable state must not duplicate those copyrighted ROM bytes.
            // Identify only the cartridge ROM-storage fields and require an exact
            // content match with the inserted image; writable RAM fields are never
            // excluded merely because they happen to contain similar bytes.
            var fieldName = field.Name;
            if (fieldName is "_prg" or "_prgRom")
            {
                return _prgRom is { Length: > 0 } &&
                       bytes.AsSpan().SequenceEqual(_prgRom);
            }

            if (fieldName is "_chr" or "_chrRom")
            {
                return _chrRom is { Length: > 0 } &&
                       bytes.AsSpan().SequenceEqual(_chrRom);
            }

            return false;
        }

        protected abstract void ObserveField(object target, FieldInfo field, object? value);

        protected static string BuildSignature(FieldInfo field, PortableMemberKind kind)
        {
            var declaringType = field.DeclaringType?.FullName ?? "<unknown>";
            var fieldType = field.FieldType.FullName ?? field.FieldType.Name;
            return $"{(byte)kind}|{declaringType}|{field.Name}|{fieldType}";
        }

        protected static FieldInfo[] BuildFieldList(Type type)
        {
            var fields = new List<FieldInfo>();
            for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                fields.AddRange(current.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsStatic && !typeof(Delegate).IsAssignableFrom(field.FieldType))
                    .OrderBy(field => field.MetadataToken));
            }
            return fields.ToArray();
        }

        private void VisitArrayReferences(Array array)
        {
            var elementType = array.GetType().GetElementType();
            if (elementType is null || elementType.IsValueType || elementType == typeof(string)) return;

            foreach (var item in array)
            {
                Visit(item);
            }
        }

        private static bool IsTerminalObject(Type type) =>
            type.IsValueType ||
            type == typeof(string) ||
            type == typeof(decimal) ||
            type == typeof(DateTime) ||
            type == typeof(TimeSpan) ||
            type == typeof(Guid) ||
            typeof(Type).IsAssignableFrom(type) ||
            typeof(MemberInfo).IsAssignableFrom(type) ||
            typeof(Delegate).IsAssignableFrom(type);
    }

    private sealed class CaptureContext(VirtualHardwareNesRomImage? image) : TraversalContext(image)
    {
        public List<PortableMember> Members { get; } = [];

        protected override void ObserveField(object target, FieldInfo field, object? value)
        {
            if (value is Array array)
            {
                if (IsImmutableArray(field, array)) return;
                var elementType = field.FieldType.GetElementType();
                if (elementType is not null && field.FieldType.GetArrayRank() == 1 && PortableValueCodec.CanSerializeArrayElement(elementType))
                {
                    Members.Add(new PortableMember(
                        PortableMemberKind.Array,
                        BuildSignature(field, PortableMemberKind.Array),
                        PortableValueCodec.SerializeArray(elementType, array)));
                }
                return;
            }

            if (field.IsInitOnly) return;

            if (field.FieldType == typeof(string) || field.FieldType.IsValueType)
            {
                if (!PortableValueCodec.CanSerialize(field.FieldType))
                {
                    throw new NotSupportedException(
                        $"NES portable save state cannot encode mutable field {field.DeclaringType?.FullName}.{field.Name} " +
                        $"of type {field.FieldType.FullName}.");
                }

                Members.Add(new PortableMember(
                    PortableMemberKind.Scalar,
                    BuildSignature(field, PortableMemberKind.Scalar),
                    PortableValueCodec.Serialize(field.FieldType, value)));
            }
        }
    }

    private sealed class RestoreContext(VirtualHardwareNesRomImage? image) : TraversalContext(image)
    {
        public List<PortableTargetMember> Members { get; } = [];

        protected override void ObserveField(object target, FieldInfo field, object? value)
        {
            if (value is Array array)
            {
                if (IsImmutableArray(field, array)) return;
                var elementType = field.FieldType.GetElementType();
                if (elementType is not null && field.FieldType.GetArrayRank() == 1 && PortableValueCodec.CanSerializeArrayElement(elementType))
                {
                    Members.Add(new PortableTargetMember(
                        PortableMemberKind.Array,
                        BuildSignature(field, PortableMemberKind.Array),
                        payload => RestoreArray(target, field, array, elementType, payload)));
                }
                return;
            }

            if (field.IsInitOnly) return;

            if (field.FieldType == typeof(string) || field.FieldType.IsValueType)
            {
                if (!PortableValueCodec.CanSerialize(field.FieldType))
                {
                    throw new NotSupportedException(
                        $"NES portable save state cannot decode mutable field {field.DeclaringType?.FullName}.{field.Name} " +
                        $"of type {field.FieldType.FullName}.");
                }

                Members.Add(new PortableTargetMember(
                    PortableMemberKind.Scalar,
                    BuildSignature(field, PortableMemberKind.Scalar),
                    payload => field.SetValue(target, PortableValueCodec.Deserialize(field.FieldType, payload))));
            }
        }

        private static void RestoreArray(
            object target,
            FieldInfo field,
            Array current,
            Type elementType,
            byte[] payload)
        {
            var restored = PortableValueCodec.DeserializeArray(elementType, payload);
            if (current.Length == restored.Length)
            {
                Array.Copy(restored, current, restored.Length);
                return;
            }

            if (field.IsInitOnly)
            {
                throw new InvalidDataException(
                    $"NES portable state array length mismatch for readonly field {field.DeclaringType?.FullName}.{field.Name}: " +
                    $"save={restored.Length:N0}, machine={current.Length:N0}.");
            }

            field.SetValue(target, restored);
        }
    }

    private sealed record PortableMember(PortableMemberKind Kind, string Signature, byte[] Payload);

    private sealed record PortableTargetMember(
        PortableMemberKind Kind,
        string Signature,
        Action<byte[]> Restore);

    private enum PortableMemberKind : byte
    {
        Scalar = 1,
        Array = 2
    }

    private static class PortableValueCodec
    {
        private const int MaximumArrayElements = 16 * 1024 * 1024;
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> ValueFieldCache = new();
        private static readonly ConcurrentDictionary<Type, bool> SerializableTypeCache = new();

        public static bool CanSerialize(Type type) => SerializableTypeCache.GetOrAdd(type, ComputeCanSerialize);

        public static bool CanSerializeArrayElement(Type type)
        {
            if (type == typeof(string)) return true;
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null) return CanSerializeArrayElement(nullable);
            if (type.IsEnum) return CanSerializeArrayElement(Enum.GetUnderlyingType(type));
            return type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(TimeSpan) ||
                   type == typeof(Guid);
        }

        public static byte[] Serialize(Type type, object? value)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            WriteValue(writer, type, value);
            writer.Flush();
            return stream.ToArray();
        }

        public static object? Deserialize(Type type, ReadOnlySpan<byte> payload)
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var value = ReadValue(reader, type);
            if (stream.Position != stream.Length)
                throw new InvalidDataException($"NES portable state scalar {type.FullName} contains trailing data.");
            return value;
        }

        public static byte[] SerializeArray(Type elementType, Array array)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(array.Length);

            if (elementType == typeof(byte))
            {
                writer.Write((byte[])array);
            }
            else
            {
                for (var index = 0; index < array.Length; index++)
                {
                    WriteValue(writer, elementType, array.GetValue(index));
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static Array DeserializeArray(Type elementType, ReadOnlySpan<byte> payload)
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumArrayElements)
                throw new InvalidDataException("NES portable state contains an invalid array length.");

            var array = Array.CreateInstance(elementType, count);
            if (elementType == typeof(byte))
            {
                var bytes = reader.ReadBytes(count);
                if (bytes.Length != count) throw new EndOfStreamException("NES portable state array ended unexpectedly.");
                Array.Copy(bytes, array, count);
            }
            else
            {
                for (var index = 0; index < count; index++)
                {
                    array.SetValue(ReadValue(reader, elementType), index);
                }
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException($"NES portable state array {elementType.FullName} contains trailing data.");
            return array;
        }

        private static bool ComputeCanSerialize(Type type)
        {
            if (type == typeof(string)) return true;
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null) return CanSerialize(nullable);
            if (type.IsEnum) return CanSerialize(Enum.GetUnderlyingType(type));
            if (type.IsPrimitive) return type != typeof(IntPtr) && type != typeof(UIntPtr);
            if (type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid))
                return true;
            if (!type.IsValueType) return false;

            foreach (var field in GetValueFields(type))
            {
                if (!CanSerialize(field.FieldType)) return false;
            }
            return true;
        }

        private static void WriteValue(BinaryWriter writer, Type type, object? value)
        {
            if (type == typeof(string))
            {
                writer.Write(value is not null);
                if (value is not null) writer.Write((string)value);
                return;
            }

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
            {
                writer.Write(value is not null);
                if (value is not null) WriteValue(writer, nullable, value);
                return;
            }

            if (type.IsEnum)
            {
                WriteValue(writer, Enum.GetUnderlyingType(type), Convert.ChangeType(value!, Enum.GetUnderlyingType(type)));
                return;
            }

            if (type == typeof(bool)) { writer.Write((bool)value!); return; }
            if (type == typeof(byte)) { writer.Write((byte)value!); return; }
            if (type == typeof(sbyte)) { writer.Write((sbyte)value!); return; }
            if (type == typeof(short)) { writer.Write((short)value!); return; }
            if (type == typeof(ushort)) { writer.Write((ushort)value!); return; }
            if (type == typeof(int)) { writer.Write((int)value!); return; }
            if (type == typeof(uint)) { writer.Write((uint)value!); return; }
            if (type == typeof(long)) { writer.Write((long)value!); return; }
            if (type == typeof(ulong)) { writer.Write((ulong)value!); return; }
            if (type == typeof(char)) { writer.Write((char)value!); return; }
            if (type == typeof(float)) { writer.Write((float)value!); return; }
            if (type == typeof(double)) { writer.Write((double)value!); return; }
            if (type == typeof(decimal)) { writer.Write((decimal)value!); return; }
            if (type == typeof(DateTime)) { writer.Write(((DateTime)value!).ToBinary()); return; }
            if (type == typeof(TimeSpan)) { writer.Write(((TimeSpan)value!).Ticks); return; }
            if (type == typeof(Guid)) { writer.Write(((Guid)value!).ToByteArray()); return; }

            if (!type.IsValueType)
                throw new NotSupportedException($"NES portable state cannot serialize value type {type.FullName}.");

            var boxed = value ?? Activator.CreateInstance(type)!;
            foreach (var field in GetValueFields(type))
            {
                WriteValue(writer, field.FieldType, field.GetValue(boxed));
            }
        }

        private static object? ReadValue(BinaryReader reader, Type type)
        {
            if (type == typeof(string)) return reader.ReadBoolean() ? reader.ReadString() : null;

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
            {
                if (!reader.ReadBoolean()) return null;
                return ReadValue(reader, nullable);
            }

            if (type.IsEnum)
            {
                var raw = ReadValue(reader, Enum.GetUnderlyingType(type));
                return Enum.ToObject(type, raw!);
            }

            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadInt16();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(char)) return reader.ReadChar();
            if (type == typeof(float)) return reader.ReadSingle();
            if (type == typeof(double)) return reader.ReadDouble();
            if (type == typeof(decimal)) return reader.ReadDecimal();
            if (type == typeof(DateTime)) return DateTime.FromBinary(reader.ReadInt64());
            if (type == typeof(TimeSpan)) return TimeSpan.FromTicks(reader.ReadInt64());
            if (type == typeof(Guid))
            {
                var bytes = reader.ReadBytes(16);
                if (bytes.Length != 16) throw new EndOfStreamException("NES portable GUID value ended unexpectedly.");
                return new Guid(bytes);
            }

            if (!type.IsValueType)
                throw new NotSupportedException($"NES portable state cannot deserialize value type {type.FullName}.");

            var boxed = Activator.CreateInstance(type)!;
            foreach (var field in GetValueFields(type))
            {
                field.SetValue(boxed, ReadValue(reader, field.FieldType));
            }
            return boxed;
        }

        private static FieldInfo[] GetValueFields(Type type) => ValueFieldCache.GetOrAdd(
            type,
            static valueType => valueType
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .OrderBy(field => field.MetadataToken)
                .ToArray());
    }
}
