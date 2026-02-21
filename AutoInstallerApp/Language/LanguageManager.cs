using System.Globalization;
using System.Text.Json;

namespace AutoInstallerApp.Language
{
    public static class LanguageManager
    {
        private static Dictionary<string, string> _strings = new();
        public static string CurrentLanguage { get; private set; } = "en";

        public static void Initialize(string? resourcesFolder = null)
        {
            try
            {
                resourcesFolder ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                CurrentLanguage = lang.Equals("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
                var file = Path.Combine(resourcesFolder, $"lang.{CurrentLanguage}.json");
                if (!File.Exists(file))
                {
                    // fallback to English if specific file not found
                    file = Path.Combine(resourcesFolder, "lang.en.json");
                }

                if (File.Exists(file))
                {
                    var txt = File.ReadAllText(file);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(txt, opts);
                    if (dict != null)
                        _strings = dict;
                }
            }
            catch
            {
                // ignore and leave defaults
            }
        }

        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            if (_strings.TryGetValue(key, out var v))
                return v ?? string.Empty;

            return key; // fallback to key so missing values are visible
        }

        // Optional helper to apply text to a Form by mapping control Name -> resource key
        public static void ApplyToForm(Form form, Dictionary<string, string>? mapping = null)
        {
            if (form == null) return;
            if (mapping == null)
            {
                // default mapping: form.Name + "_Title" -> form.Text, control.Name -> resource key with underscores
                var titleKey = form.Name + "_Title";
                form.Text = Get(titleKey);

                foreach (Control c in form.Controls)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(c.Name)) continue;
                        var key = c.Name;
                        var text = Get(key);
                        if (!string.IsNullOrEmpty(text) && text != key)
                            c.Text = text;
                    }
                    catch { }
                }
            }
            else
            {
                foreach (var kv in mapping)
                {
                    try
                    {
                        if (kv.Key.Equals("FormTitle", StringComparison.OrdinalIgnoreCase))
                        {
                            form.Text = Get(kv.Value);
                            continue;
                        }

                        var ctl = form.Controls[kv.Key];
                        if (ctl != null) ctl.Text = Get(kv.Value);
                    }
                    catch { }
                }
            }
        }
    }
}
