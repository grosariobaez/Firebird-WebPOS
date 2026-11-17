using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Serialization;

public sealed class InvoiceJsonSerializer
{
    public const int FormatVersion = 1;
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            var ordered = info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < ordered.Length; i++) ordered[i].Order = i;
        });
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never, TypeInfoResolver = resolver,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new StableDecimalConverter());
        options.Converters.Add(new SourceDateConverter());
        options.MakeReadOnly();
        return options;
    }

    public string Serialize(InvoicePayload payload) => JsonSerializer.Serialize(payload with
    {
        Details = payload.Details.OrderBy(x => x.Secuencia).ToArray(),
        Payments = payload.Payments.OrderBy(x => x.Secuencia).ToArray()
    }, Options);

    public string Fingerprint(InvoiceSnapshot snapshot) => HashUtf8(JsonSerializer.Serialize(snapshot with
    {
        Details = snapshot.Details.OrderBy(x => x.Secuencia).ToArray(),
        Payments = snapshot.Payments.OrderBy(x => x.Secuencia).ToArray()
    }, Options));

    public static string HashUtf8(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    // SQL HASHBYTES(CONVERT(varbinary(max), NVARCHAR)) hashes UTF-16LE without BOM.
    public static string HashSqlPayload(string value) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(value)));

    private sealed class StableDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.ToString("0.############################", CultureInfo.InvariantCulture));
    }
    private sealed class SourceDateConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            DateTime.SpecifyKind(DateTime.ParseExact(reader.GetString()!, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
    }
}
