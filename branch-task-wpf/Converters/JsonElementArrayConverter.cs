using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BranchTaskWpf.Converters;

/// <summary>
/// JsonElement 序列化兜底：新建 TaskNode 的 Messages 默认是 Undefined（default(JsonElement)），
/// 直接 Serialize 会抛 InvalidOperationException("Operation is not valid due to the current state of the object.")
/// → Save 失败 → 新建项目永远无法落盘（老项目是反序列化来的所以正常）。
/// 此转换器把 Undefined 序列化为 []，其余情况原样输出。
/// </summary>
public class JsonElementArrayConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 兼容旧格式：对象数组或字符串数组都原样解析
        return JsonElement.ParseValue(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}
