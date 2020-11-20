using System.Text.Json;

namespace GitMirrorAutomation.Logic.Extensions
{
    public static class JsonElementExtensions
    {
        public static T ToObject<T>(this JsonElement element, JsonSerializerOptions? serializerOptions = null) where T : new()
        {
            var json = element.GetRawText();
            return JsonSerializer.Deserialize<T>(json, serializerOptions ?? JsonSettings.Default) ?? new T();
        }
    }
}
