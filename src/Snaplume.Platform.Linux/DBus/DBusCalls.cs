using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.DBus;

/// <summary>One argument of a D-Bus call, with its signature; written in order by <see cref="DBusCalls.Build"/>.</summary>
internal abstract record DArg(string Signature)
{
    public abstract void Write(ref MessageWriter writer);
}

internal sealed record DString(string Value) : DArg("s")
{
    public override void Write(ref MessageWriter writer) => writer.WriteString(Value);
}

internal sealed record DPath(string Value) : DArg("o")
{
    public override void Write(ref MessageWriter writer) => writer.WriteObjectPath(Value);
}

internal sealed record DUInt(uint Value) : DArg("u")
{
    public override void Write(ref MessageWriter writer) => writer.WriteUInt32(Value);
}

internal sealed record DInt(int Value) : DArg("i")
{
    public override void Write(ref MessageWriter writer) => writer.WriteInt32(Value);
}

internal sealed record DDouble(double Value) : DArg("d")
{
    public override void Write(ref MessageWriter writer) => writer.WriteDouble(Value);
}

internal sealed record DStrings(IReadOnlyList<string> Value) : DArg("as")
{
    public override void Write(ref MessageWriter writer) => writer.WriteArray(Value.ToArray());
}

/// <summary>The a{sv} options dictionary every portal method takes.</summary>
internal sealed record DOptions(Dictionary<string, VariantValue> Value) : DArg("a{sv}")
{
    public override void Write(ref MessageWriter writer) => writer.WriteDictionary(Value);
}

/// <summary>GlobalShortcuts.BindShortcuts list: a(sa{sv}), an id and its properties per shortcut.</summary>
internal sealed record DShortcuts(IReadOnlyList<(string Id, Dictionary<string, VariantValue> Properties)> Value) : DArg("a(sa{sv})")
{
    public override void Write(ref MessageWriter writer)
    {
        var array = writer.WriteArrayStart(DBusType.Struct);
        foreach (var (id, properties) in Value)
        {
            writer.WriteStructureStart();
            writer.WriteString(id);
            writer.WriteDictionary(properties);
        }
        writer.WriteArrayEnd(array);
    }
}

/// <summary>Method calls on the session bus with Tmds.DBus.Protocol.</summary>
internal static class DBusCalls
{
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);
    private static DBusConnection? _session;

    /// <summary>The shared session-bus connection, opened on first use.</summary>
    /// <exception cref="InvalidOperationException">No session bus (DBUS_SESSION_BUS_ADDRESS unset).</exception>
    public static async Task<DBusConnection> SessionAsync()
    {
        if (_session != null) return _session;
        await ConnectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != null) return _session;
            var address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus");
            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            _session = connection;
            return connection;
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    public static string SignatureOf(IEnumerable<DArg> args) => string.Concat(args.Select(a => a.Signature));

    public static MessageBuffer Build(DBusConnection connection, string destination, string path, string @interface, string member, params DArg[] args)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, args.Length == 0 ? null : SignatureOf(args), MessageFlags.None);
            foreach (var arg in args) arg.Write(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public static Task CallAsync(DBusConnection connection, string destination, string path, string @interface, string member, params DArg[] args) =>
        connection.CallMethodAsync(Build(connection, destination, path, @interface, member, args));

    public static Task<T> CallAsync<T>(DBusConnection connection, MessageValueReader<T> reader, string destination, string path,
        string @interface, string member, params DArg[] args) =>
        connection.CallMethodAsync(Build(connection, destination, path, @interface, member, args), reader, null);

    /// <summary>org.freedesktop.DBus.Properties.Get; null when the object or the interface does not exist.</summary>
    public static async Task<VariantValue?> GetPropertyAsync(DBusConnection connection, string destination, string path, string @interface, string property)
    {
        try
        {
            return await CallAsync(connection, (m, _) => m.GetBodyReader().ReadVariantValue(), destination, path,
                "org.freedesktop.DBus.Properties", "Get", new DString(@interface), new DString(property)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            return null;
        }
    }

    /// <summary>The value inside a variant, however deeply it is wrapped.</summary>
    public static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
        return value;
    }

    public static string? GetString(this Dictionary<string, VariantValue> results, string key) =>
        results.TryGetValue(key, out var value) && Unwrap(value) is { Type: VariantValueType.String or VariantValueType.ObjectPath } v
            ? (v.Type == VariantValueType.String ? v.GetString() : v.GetObjectPathAsString())
            : null;

    public static uint? GetUInt(this Dictionary<string, VariantValue> results, string key) =>
        results.TryGetValue(key, out var value) && Unwrap(value) is { Type: VariantValueType.UInt32 } v ? v.GetUInt32() : null;

    /// <summary>A number from a property reply (versions and bit masks are "u").</summary>
    public static uint? AsUInt(VariantValue? value) =>
        value is { } v && Unwrap(v) is { Type: VariantValueType.UInt32 } u ? u.GetUInt32() : null;
}
