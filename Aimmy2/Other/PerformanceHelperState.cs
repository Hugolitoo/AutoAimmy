using Class;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.IO;

namespace Other
{
    internal static class PerformanceHelperState
    {
        private const int CurrentPromptVersion = 1;
        private const string StatePath = "bin\\performance-helper.cfg";

        private static readonly Dictionary<string, dynamic> State = new();

        internal static bool ShouldPrompt()
        {
            Load();

            int promptVersion = ReadInt("PromptVersion", 0);
            string choice = ReadString("Choice", "Pending");

            return ShouldPromptForState(promptVersion, choice);
        }

        internal static bool ShouldPromptForState(int promptVersion, string? choice)
        {
            return promptVersion < CurrentPromptVersion ||
                   string.Equals(choice ?? "Pending", "Pending", StringComparison.OrdinalIgnoreCase);
        }

        internal static void SaveChoice(string choice)
        {
            State["PromptVersion"] = CurrentPromptVersion;
            State["Choice"] = choice;
            State["UpdatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            SaveDictionary.WriteJSON(State, StatePath);
        }

        private static void Load()
        {
            ResetDefaults();

            if (File.Exists(StatePath))
            {
                try
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(File.ReadAllText(StatePath));
                    if (loaded != null)
                    {
                        foreach (var item in loaded)
                        {
                            State[item.Key] = item.Value;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void ResetDefaults()
        {
            State.Clear();
            State["PromptVersion"] = CurrentPromptVersion;
            State["Choice"] = "Pending";
        }

        private static int ReadInt(string key, int fallback)
        {
            try
            {
                if (!State.TryGetValue(key, out dynamic? value))
                    return fallback;

                object? rawValue = value is JValue jValue ? jValue.Value : value;
                return Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static string ReadString(string key, string fallback)
        {
            try
            {
                if (!State.TryGetValue(key, out dynamic? value))
                    return fallback;

                object? rawValue = value is JValue jValue ? jValue.Value : value;
                return Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
