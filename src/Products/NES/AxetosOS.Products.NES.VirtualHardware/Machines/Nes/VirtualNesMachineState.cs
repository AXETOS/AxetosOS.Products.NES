using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

/// <summary>
/// Opaque, in-memory capture of one loaded NES machine at an exact point in
/// time. The state belongs to the host instance and cartridge generation that
/// created it; hosts may restore it repeatedly while that cartridge remains
/// loaded.
/// </summary>
public sealed class VirtualNesMachineState
{
    internal VirtualNesMachineState(
        Guid ownerId,
        ulong cartridgeGeneration,
        ActiveNesMotherboard motherboard,
        int mapperNumber,
        ulong masterCycles,
        bool resetVectorObserved,
        bool firstOpcodeObserved,
        bool firstVblankObserved,
        bool firstNmiObserved,
        bool bootChecksComplete,
        InMemoryHardwareState hardware)
    {
        OwnerId = ownerId;
        CartridgeGeneration = cartridgeGeneration;
        Motherboard = motherboard;
        MapperNumber = mapperNumber;
        MasterCycles = masterCycles;
        ResetVectorObserved = resetVectorObserved;
        FirstOpcodeObserved = firstOpcodeObserved;
        FirstVblankObserved = firstVblankObserved;
        FirstNmiObserved = firstNmiObserved;
        BootChecksComplete = bootChecksComplete;
        Hardware = hardware;
    }

    public ActiveNesMotherboard Motherboard { get; }
    public int MapperNumber { get; }
    public ulong MasterCycles { get; }

    internal Guid OwnerId { get; }
    internal ulong CartridgeGeneration { get; }
    internal bool ResetVectorObserved { get; }
    internal bool FirstOpcodeObserved { get; }
    internal bool FirstVblankObserved { get; }
    internal bool FirstNmiObserved { get; }
    internal bool BootChecksComplete { get; }
    internal InMemoryHardwareState Hardware { get; }
}

/// <summary>
/// Captures the mutable state of an already assembled physical machine without
/// reconstructing its topology. Save-state restoration writes state back into
/// the same chips, pins, nets, memories and compiled runtime objects that were
/// present when the snapshot was taken.
/// </summary>
internal sealed class InMemoryHardwareState
{
    private readonly FieldState[] _fields;
    private readonly ArrayState[] _arrays;

    private InMemoryHardwareState(FieldState[] fields, ArrayState[] arrays)
    {
        _fields = fields;
        _arrays = arrays;
    }

    public static InMemoryHardwareState Capture(RegionalNesVirtualMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var capture = new CaptureContext(machine.Slot.InsertedImage);
        capture.Visit(machine);
        return new InMemoryHardwareState(capture.Fields.ToArray(), capture.Arrays.ToArray());
    }

    public void Restore()
    {
        // Restore references/scalars first. This re-establishes any runtime-owned
        // buffer reference that changed after the capture (for example a ring
        // buffer that grew) before its captured contents are copied back.
        foreach (var state in _fields)
        {
            state.Field.SetValue(state.Target, state.Value);
        }

        foreach (var state in _arrays)
        {
            Array.Copy(state.Values, state.Target, state.Values.Length);
        }
    }

    private sealed class CaptureContext
    {
        private static readonly Assembly HardwareAssembly = typeof(RegionalNesVirtualMachine).Assembly;
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _immutable = new(ReferenceEqualityComparer.Instance);

        public CaptureContext(VirtualHardwareNesRomImage? image)
        {
            if (image is null) return;
            _immutable.Add(image);
            _immutable.Add(image.PrgRom);
            _immutable.Add(image.ChrRom);
        }

        public List<FieldState> Fields { get; } = [];
        public List<ArrayState> Arrays { get; } = [];

        public void Visit(object? value)
        {
            if (value is null) return;

            var type = value.GetType();
            if (IsTerminal(type)) return;
            if (_immutable.Contains(value)) return;
            if (!_visited.Add(value)) return;

            if (value is Array array)
            {
                CaptureArray(array);
                return;
            }

            if (value is IEnumerable enumerable && type.Assembly != HardwareAssembly)
            {
                foreach (var item in enumerable)
                {
                    Visit(item);
                }
                return;
            }

            if (type.Assembly != HardwareAssembly) return;

            foreach (var field in FieldCache.GetOrAdd(type, BuildFieldList))
            {
                var fieldValue = field.GetValue(value);

                // Mutable fields include both scalar circuit state and the
                // occasional runtime-owned reference that may be replaced as
                // buffers grow. Snapshot the reference itself, then recurse
                // into the object it points to.
                if (!field.IsInitOnly)
                {
                    Fields.Add(new FieldState(value, field, fieldValue));
                }

                Visit(fieldValue);
            }
        }

        private static FieldInfo[] BuildFieldList(Type type)
        {
            var fields = new List<FieldInfo>();
            for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                fields.AddRange(current.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsStatic && !typeof(Delegate).IsAssignableFrom(field.FieldType)));
            }
            return fields.ToArray();
        }

        private void CaptureArray(Array array)
        {
            Arrays.Add(new ArrayState(array, (Array)array.Clone()));

            var elementType = array.GetType().GetElementType();
            if (elementType is null || elementType.IsValueType || elementType == typeof(string)) return;

            foreach (var item in array)
            {
                Visit(item);
            }
        }

        private static bool IsTerminal(Type type) =>
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

    private sealed record FieldState(object Target, FieldInfo Field, object? Value);
    private sealed record ArrayState(Array Target, Array Values);
}
