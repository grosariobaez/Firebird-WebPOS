using System.Text.Json;
using System.Text.Json.Nodes;

namespace JomliaWebPosSync.Configurator;

internal static class JsonConfigurationEditor
{
    public static JsonObject LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section) return section;
        section = new JsonObject();
        root[name] = section;
        return section;
    }

    public static void Set(JsonObject root, string sectionName, string key, JsonNode? value) =>
        Section(root, sectionName)[key] = value;

    public static string GetString(JsonObject section, string key, string fallback = "") =>
        section[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? fallback : fallback;

    public static bool GetBool(JsonObject section, string key, bool fallback) =>
        section[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    public static int GetInt(JsonObject section, string key, int fallback) =>
        section[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    public static int? GetNullableInt(JsonObject section, string key) =>
        section[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    public static void MergeCompanyInitialInvoiceId(JsonObject webPosSection, int? companyId, int? initialInvoiceId)
    {
        var initial = webPosSection["InitialInvoiceIdByCompany"] is JsonObject existing
            ? (JsonObject)existing.DeepClone()
            : new JsonObject();

        if (companyId is int company)
        {
            var key = company.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (initialInvoiceId is int invoice)
                initial[key] = JsonValue.Create(invoice);
            else
                initial.Remove(key);
        }

        webPosSection["InitialInvoiceIdByCompany"] = initial;
    }

    public static void Flatten(JsonNode? node, string prefix, IDictionary<string, string?> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Key : prefix + ":" + property.Key;
                    Flatten(property.Value, key, values);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    Flatten(array[index], prefix + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), values);
                break;
            case JsonValue value:
                values[prefix] = value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
                break;
            default:
                values[prefix] = null;
                break;
        }
    }
}
