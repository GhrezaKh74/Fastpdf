using System.Collections.Concurrent;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;

namespace FastPdfWriterStream.Templates;

/// <summary>دسترسی به یک فیلد از ردیف داده (کامپایل‌شده برای سرعت).</summary>
public abstract class FieldGetter<T>
{
    /// <summary>نوشتن مقدار فرمت‌شده در بافر؛ طول برگردانده می‌شود.</summary>
    public abstract int Write(T row, FormatSpec spec, ref char[] buffer);

    /// <summary>مقدار عددی (برای جمع و رنگ منفی).</summary>
    public abstract decimal? GetNumber(T row);

    public abstract object? GetValue(T row);
}

/// <summary>ساخت <see cref="FieldGetter{T}"/> برای انواع مختلف ردیف.</summary>
public static class FieldAccess
{
    private static readonly ConcurrentDictionary<(Type, string), object> Cache = new();

    /// <summary>
    /// getter برای فیلد. پشتیبانی: کلاس/رکورد (property/field، مسیر نقطه‌دار)، IDictionary، DataRow، IDataRecord.
    /// اگر فیلد پیدا نشود null برمی‌گردد.
    /// </summary>
    public static FieldGetter<T>? Create<T>(string name)
    {
        var t = typeof(T);
        if (typeof(IDictionary<string, object?>).IsAssignableFrom(t) || typeof(IDictionary<string, object>).IsAssignableFrom(t))
            return new DictionaryGetter<T>(name);
        if (typeof(IReadOnlyDictionary<string, object?>).IsAssignableFrom(t))
            return new ReadOnlyDictionaryGetter<T>(name);
        if (typeof(DataRow).IsAssignableFrom(t)) return new DataRowGetter<T>(name);
        if (typeof(IDataRecord).IsAssignableFrom(t)) return new DataRecordGetter<T>(name);
        if (t == typeof(object)) return new DynamicGetter<T>(name);

        var cached = Cache.GetOrAdd((t, name), key => (object?)BuildMemberGetter<T>(key.Item2) ?? Missing.Instance);
        return cached as FieldGetter<T>;
    }

    private sealed class Missing
    {
        public static readonly Missing Instance = new();
    }

    private static FieldGetter<T>? BuildMemberGetter<T>(string path)
    {
        var param = Expression.Parameter(typeof(T), "row");
        Expression current = param;
        Expression? nullCheck = null;
        bool first = true;
        foreach (var part in path.Split('.'))
        {
            var type = current.Type;
            var member = (MemberInfo?)type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                         ?? type.GetField(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (member == null) return null;
            bool checkNull = !first && !type.IsValueType;
            first = false;
            if (checkNull)
            {
                var isNull = Expression.Equal(current, Expression.Constant(null, type));
                nullCheck = nullCheck == null ? isNull : Expression.OrElse(nullCheck, isNull);
            }
            current = Expression.MakeMemberAccess(current, member);
        }

        var valueType = current.Type;
        // مقدار پیش‌فرض در صورت null بودن یکی از اجزای مسیر (به جز خود ردیف)
        Expression body = current;
        if (nullCheck != null)
        {
            var resultType = valueType.IsValueType && Nullable.GetUnderlyingType(valueType) == null
                ? typeof(Nullable<>).MakeGenericType(valueType)
                : valueType;
            body = Expression.Condition(nullCheck, Expression.Default(resultType), Expression.Convert(current, resultType));
            valueType = resultType;
        }

        var funcType = typeof(Func<,>).MakeGenericType(typeof(T), valueType);
        var lambda = Expression.Lambda(funcType, body, param).Compile();
        var getterType = typeof(MemberGetter<,>).MakeGenericType(typeof(T), valueType);
        return (FieldGetter<T>)Activator.CreateInstance(getterType, lambda)!;
    }

    /// <summary>getter بر اساس نوع واقعی مقدار (برای ردیف‌های object/dynamic).</summary>
    private sealed class DynamicGetter<T> : FieldGetter<T>
    {
        private readonly string _name;
        private readonly ConcurrentDictionary<Type, Func<object, object?>?> _byType = new();

        public DynamicGetter(string name) => _name = name;

        public override object? GetValue(T row)
        {
            if (row == null) return null;
            switch (row)
            {
                case IDictionary<string, object?> d: return d.TryGetValue(_name, out var v) ? v : null;
                case DataRow dr: return dr.Table.Columns.Contains(_name) ? (dr[_name] is DBNull ? null : dr[_name]) : null;
                case IDataRecord rec:
                    int o = rec.GetOrdinal(_name);
                    return rec.IsDBNull(o) ? null : rec.GetValue(o);
            }
            var getter = _byType.GetOrAdd(row.GetType(), type =>
            {
                var p = type.GetProperty(_name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null) return o => p.GetValue(o);
                var f = type.GetField(_name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                return f != null ? o => f.GetValue(o) : null;
            });
            return getter?.Invoke(row);
        }

        public override int Write(T row, FormatSpec spec, ref char[] buffer) => ValueFormatter.WriteObject(GetValue(row), spec, ref buffer);
        public override decimal? GetNumber(T row) => ValueFormatter.ToNumber(GetValue(row));
    }
}

/// <summary>getter کامپایل‌شده با نوع مشخص (بدون boxing).</summary>
public sealed class MemberGetter<T, TValue> : FieldGetter<T>
{
    private readonly Func<T, TValue> _get;

    public MemberGetter(Func<T, TValue> get) => _get = get;

    public override int Write(T row, FormatSpec spec, ref char[] buffer) => ValueFormatter.Write(_get(row), spec, ref buffer);
    public override decimal? GetNumber(T row) => ValueFormatter.ToNumber(_get(row));
    public override object? GetValue(T row) => _get(row);
}

internal sealed class DictionaryGetter<T> : FieldGetter<T>
{
    private readonly string _name;
    public DictionaryGetter(string name) => _name = name;

    public override object? GetValue(T row)
    {
        object? v = null;
        if (row is IDictionary<string, object?> d)
        {
            if (!d.TryGetValue(_name, out v))
                foreach (var kv in d)
                    if (string.Equals(kv.Key, _name, StringComparison.OrdinalIgnoreCase)) { v = kv.Value; break; }
        }
        else if (row is IDictionary<string, object> d2) d2.TryGetValue(_name, out v);
        return v is DBNull ? null : v;
    }

    public override int Write(T row, FormatSpec spec, ref char[] buffer) => ValueFormatter.WriteObject(GetValue(row), spec, ref buffer);
    public override decimal? GetNumber(T row) => ValueFormatter.ToNumber(GetValue(row));
}

internal sealed class ReadOnlyDictionaryGetter<T> : FieldGetter<T>
{
    private readonly string _name;
    public ReadOnlyDictionaryGetter(string name) => _name = name;

    public override object? GetValue(T row) =>
        row is IReadOnlyDictionary<string, object?> d && d.TryGetValue(_name, out var v) && v is not DBNull ? v : null;

    public override int Write(T row, FormatSpec spec, ref char[] buffer) => ValueFormatter.WriteObject(GetValue(row), spec, ref buffer);
    public override decimal? GetNumber(T row) => ValueFormatter.ToNumber(GetValue(row));
}

internal sealed class DataRowGetter<T> : FieldGetter<T>
{
    private readonly string _name;
    private DataTable? _table;
    private int _ordinal = -1;

    public DataRowGetter(string name) => _name = name;

    public override object? GetValue(T row)
    {
        var dr = (DataRow)(object)row!;
        if (!ReferenceEquals(dr.Table, _table))
        {
            _table = dr.Table;
            _ordinal = dr.Table.Columns.IndexOf(_name);
        }
        if (_ordinal < 0) return null;
        var v = dr[_ordinal];
        return v is DBNull ? null : v;
    }

    public override int Write(T row, FormatSpec spec, ref char[] buffer) => ValueFormatter.WriteObject(GetValue(row), spec, ref buffer);
    public override decimal? GetNumber(T row) => ValueFormatter.ToNumber(GetValue(row));
}

internal sealed class DataRecordGetter<T> : FieldGetter<T>
{
    private readonly string _name;
    private int _ordinal = -2;

    public DataRecordGetter(string name) => _name = name;

    public override object? GetValue(T row)
    {
        var rec = (IDataRecord)row!;
        if (_ordinal == -2)
        {
            _ordinal = -1;
            for (int i = 0; i < rec.FieldCount; i++)
                if (string.Equals(rec.GetName(i), _name, StringComparison.OrdinalIgnoreCase)) { _ordinal = i; break; }
        }
        if (_ordinal < 0 || rec.IsDBNull(_ordinal)) return null;
        return rec.GetValue(_ordinal);
    }

    public override int Write(T row, FormatSpec spec, ref char[] buffer) => ValueFormatter.WriteObject(GetValue(row), spec, ref buffer);
    public override decimal? GetNumber(T row) => ValueFormatter.ToNumber(GetValue(row));
}

/// <summary>تبدیل شیء پارامترها (anonymous/POCO/Dictionary) به Dictionary.</summary>
internal static class ParameterReader
{
    public static Dictionary<string, object?> Read(object? parameters)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (parameters)
        {
            case null: return result;
            case IDictionary<string, object?> d:
                foreach (var kv in d) result[kv.Key] = kv.Value;
                return result;
            case IDictionary<string, string?> ds:
                foreach (var kv in ds) result[kv.Key] = kv.Value;
                return result;
            case IReadOnlyDictionary<string, object?> rd:
                foreach (var kv in rd) result[kv.Key] = kv.Value;
                return result;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Object:
                foreach (var p in je.EnumerateObject()) result[p.Name] = p.Value;
                return result;
        }
        foreach (var p in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0) result[p.Name] = p.GetValue(parameters);
        foreach (var f in parameters.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            result[f.Name] = f.GetValue(parameters);
        return result;
    }
}
