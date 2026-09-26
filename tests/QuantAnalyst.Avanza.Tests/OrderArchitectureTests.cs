using System.Reflection;
using System.Reflection.Emit;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Orders;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// ADR 0002/0003 and CLAUDE.md: "Order code paths are reachable only through Trading.OrderGateway … No other class
/// may call the order endpoints." These tests read the IL of every production assembly (so they also cover code no
/// test runs) and check who calls the order channel, who creates approved orders, and who touches the order routes.
/// Each rule has a positive control, so a scanner that finds nothing fails instead of passing.
/// </summary>
public sealed class OrderArchitectureTests
{
    private static readonly string[] Production =
        ["QuantAnalyst.Core", "QuantAnalyst.Avanza", "QuantAnalyst.Data", "QuantAnalyst.Analytics", "QuantAnalyst.Trading", "qa" /* the CLI */, "QuantAnalyst.Native"];

    private static readonly Lazy<IReadOnlyList<IlReference>> All = new(() =>
        [.. Production.Select(Assembly.Load).SelectMany(IlScanner.References)]);

    private static readonly string[] ChannelMethods = [nameof(IBrokerOrderChannel.PlaceAsync), nameof(IBrokerOrderChannel.ModifyAsync), nameof(IBrokerOrderChannel.CancelAsync)];

    private static bool IsChannelCall(IlReference r) =>
        r.Target is MethodInfo m && ChannelMethods.Contains(m.Name) && m.DeclaringType is { } t && typeof(IBrokerOrderChannel).IsAssignableFrom(t);

    [Fact]
    public void EveryProductionAssembly_IsScanned()
    {
        Assert.Equal(Production.Order(StringComparer.Ordinal), All.Value.Select(r => r.Caller.Module.Assembly.GetName().Name!).Distinct().Order(StringComparer.Ordinal));
        Assert.True(All.Value.Count > 10_000, $"only {All.Value.Count} references found; the scanner is broken");
    }

    [Fact]
    public void OnlyOrderGateway_CallsAnOrderChannel()
    {
        IlReference[] calls = [.. All.Value.Where(IsChannelCall)];
        Assert.Contains(calls, c => c.Owner == typeof(OrderGateway) && c.Target.Name == nameof(IBrokerOrderChannel.PlaceAsync));
        Assert.Contains(calls, c => c.Owner == typeof(OrderGateway) && c.Target.Name == nameof(IBrokerOrderChannel.CancelAsync));
        Assert.Empty(calls.Where(c => c.Owner != typeof(OrderGateway)).Select(c => c.ToString()));
    }

    [Fact]
    public void OnlyOrderGateway_CreatesApprovedOrders()
    {
        Type[] approved = [typeof(ApprovedOrder), typeof(ApprovedModify), typeof(ApprovedCancel)];
        IlReference[] creations = [.. All.Value.Where(r => r.Target is ConstructorInfo c && approved.Contains(c.DeclaringType) && r.Owner != c.DeclaringType)];
        Assert.Contains(creations, c => c.Owner == typeof(OrderGateway) && c.Target.DeclaringType == typeof(ApprovedOrder));
        Assert.Contains(creations, c => c.Owner == typeof(OrderGateway) && c.Target.DeclaringType == typeof(ApprovedCancel));
        Assert.Empty(creations.Where(c => c.Owner != typeof(OrderGateway)).Select(c => c.ToString()));
    }

    [Fact]
    public void OnlyAvanzaOrderChannel_UsesTheOrderRoutes()
    {
        IlReference[] uses = [.. All.Value.Where(r => r.Target is FieldInfo or MethodInfo && r.Target.DeclaringType == typeof(AvanzaOrderRoutes) && r.Owner != typeof(AvanzaOrderRoutes))];
        Assert.Equal(
            [nameof(AvanzaOrderRoutes.Delete), nameof(AvanzaOrderRoutes.Modify), nameof(AvanzaOrderRoutes.Place)],
            uses.Where(u => u.Owner == typeof(AvanzaOrderChannel)).Select(u => u.Target.Name).Distinct().Order(StringComparer.Ordinal));
        Assert.Empty(uses.Where(u => u.Owner != typeof(AvanzaOrderChannel)).Select(u => u.ToString()));
    }

    [Fact]
    public void TheAvanzaOrderChannel_IsBuiltOnlyByTheConnection_AndNothingAsksForItInPhase6()
    {
        IlReference[] built = [.. All.Value.Where(r => r.Target is ConstructorInfo c && c.DeclaringType == typeof(AvanzaOrderChannel))];
        Assert.Equal([typeof(AvanzaConnection)], built.Select(b => b.Owner).Distinct());
        Assert.True(typeof(AvanzaOrderChannel).GetConstructors().Length == 0, "the channel must have no public constructor");

        MethodInfo factory = typeof(AvanzaConnection).GetMethod("CreateOrderChannel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(factory.IsAssembly, "CreateOrderChannel must stay internal in Phase 6");
        Assert.Empty(All.Value.Where(r => r.Target == factory).Select(r => r.ToString()));
    }
}

internal sealed record IlReference(MethodBase Caller, MemberInfo Target)
{
    /// <summary>Gets the top-level type of the caller (async state machines, lambdas and local functions count as their outer type).</summary>
    public Type Owner
    {
        get
        {
            Type t = Caller.DeclaringType!;
            while (t.DeclaringType is not null)
            {
                t = t.DeclaringType;
            }

            return t;
        }
    }

    public override string ToString() => $"{Caller.DeclaringType?.FullName}.{Caller.Name} -> {Target.DeclaringType?.FullName}.{Target.Name}";
}

/// <summary>A minimal IL reader: every method, field and type token referenced from every method body of an assembly.</summary>
internal static class IlScanner
{
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => unchecked((ushort)o.Value));

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static IEnumerable<IlReference> References(Assembly assembly)
    {
        var found = new List<IlReference>();
        foreach (Type type in assembly.GetTypes())
        {
            IEnumerable<MethodBase> methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));
            foreach (MethodBase method in methods)
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is not null)
                {
                    Read(method, il, found);
                }
            }
        }

        return found;
    }

    private static void Read(MethodBase method, byte[] il, List<IlReference> found)
    {
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        Type[]? methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            OpCode op = il[i] == 0xFE ? Codes[(ushort)(0xFE00 | il[i + 1])] : Codes[il[i]];
            i += op.Size;
            switch (op.OperandType)
            {
                case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok:
                    try
                    {
                        MemberInfo? target = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                        if (target is not null)
                        {
                            found.Add(new IlReference(method, target));
                        }
                    }
                    catch (ArgumentException)
                    {
                        // A token the runtime cannot resolve in this generic context; not an order member.
                    }

                    i += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + (4 * BitConverter.ToInt32(il, i));
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }
}
