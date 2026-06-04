using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

using Newtonsoft.Json.Linq;

namespace IBS.Helpers;

public static class Ext
{

    public static T2[] ToArray<T1, T2>(this IReadOnlyCollection<T1> coll, Converter<T1, T2> conv)
    {
        var res = new T2[coll.Count];
        var i = 0;
        foreach (var item in coll)
            res[i++] = conv(item);
        if (i != res.Length)
            throw new ArgumentException($"Collection size changed {res.Length}=>{i} during conversion");
        if (i == 0)
            return [];
        return res;
    }

    public static String WriteToString(this JToken j_token, Newtonsoft.Json.Formatting formatting = Newtonsoft.Json.Formatting.Indented)
    {
        var sw = new StringWriter();
        var json_writer = new Newtonsoft.Json.JsonTextWriter(sw)
        {
            Formatting = formatting,
        };
        j_token.WriteTo(json_writer);
        return sw.ToString();
    }

    private static class EnumOps<T>
        where T : struct, Enum
    {
        public static readonly Action<BinaryWriter, T> write;
        public static readonly Func<BinaryReader, T> read;

        static EnumOps()
        {
            var int_type = typeof(T).GetEnumUnderlyingType();

            var p_write_bw = Expression.Parameter(typeof(BinaryWriter), "bw");
            var p_write_val = Expression.Parameter(typeof(T), "val");
            var mi_write = typeof(BinaryWriter).GetMethod(
                nameof(BinaryWriter.Write),
                BindingFlags.Instance | BindingFlags.Public,
                [int_type]
            ) ?? throw new InvalidOperationException($"Couldn't find {nameof(BinaryWriter)}.{nameof(BinaryWriter.Write)} with an argument of type {int_type}");
            write = Expression.Lambda<Action<BinaryWriter, T>>(
                Expression.Call(p_write_bw, mi_write, Expression.Convert(p_write_val, int_type)),
                parameters: [p_write_bw, p_write_val]
            ).Compile();

            var p_read_br = Expression.Parameter(typeof(BinaryReader), "br");
            var mi_read_name = "Read"+int_type.Name;
            var mi_read = typeof(BinaryReader).GetMethod(
                mi_read_name,
                BindingFlags.Instance | BindingFlags.Public,
                []
            ) ?? throw new InvalidOperationException($"Couldn't find {nameof(BinaryReader)}.{mi_read_name} method");
            read = Expression.Lambda<Func<BinaryReader, T>>(
                Expression.Convert(Expression.Call(p_read_br, mi_read), typeof(T)),
                parameters: [p_read_br]
            ).Compile();

        }

    }

    public static void WriteEnum<T>(this BinaryWriter bw, T val)
        where T : struct, Enum => EnumOps<T>.write(bw, val);

    public static T ReadEnum<T>(this BinaryReader br)
        where T : struct, Enum => EnumOps<T>.read(br);

}
