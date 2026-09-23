using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Application.Abstractions.Ports;
using LMLocal.Core.Common;
using LMLocal.Core.Models.Instructions;
using LMLocal.Infrastructure.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace LMLocal.Infrastructure.Instructions
{
    internal sealed class InstructionsManager : IInstructionsManager
    {
        private const int UserTabIdStart = 50;
        private const int ReservedMaxDefaultId = 49;
        private const int MaxUserTabs = 50;
        private const double DefaultTemperature = 0.5;

        private readonly string _filePath;
        private readonly IFileSystem _fileSystem;
        private readonly ISettingsManager _settingsManager;
        private readonly IInstructionDefaultsProvider _defaultsProvider;
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);


        public InstructionsManager(IFileSystem fileSystem, ISettingsManager settingsManager, IInstructionDefaultsProvider defaultsProvider)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _defaultsProvider = defaultsProvider ?? throw new ArgumentNullException(nameof(defaultsProvider));

            var filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    _settingsManager?.LocalAppDataFolder ?? "LMLocalChat",
                    _settingsManager?.LocalAppInstructionsFileName ?? "instructions.json"
                );

            _fileSystem.ValidateFilePath(filePath);
            _fileSystem.EnsureDirectoryExistsForFile(filePath);
            _filePath = filePath;
        }

        /// <summary>
        /// Returns the effective instructions document.
        /// </summary>
        public async Task<string> GetAsync(CancellationToken cancellationToken = default)
        {
            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_fileSystem.FileExists(_filePath))
                    return "{}";

                string raw = await _fileSystem.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                    return "{}";

                JObject fileDoc;
                try
                {
                    fileDoc = JObject.Parse(raw);
                }
                catch (Exception ex)
                {
                    InternalLogger.Warn("Error parsing instructions: " + ex.Message);
                    return "{}";
                }

                IReadOnlyList<InstructionDefaultTab> defaults = await GetDefaultsSafeAsync(cancellationToken).ConfigureAwait(false);
                JObject effective = BuildEffectiveDocument(fileDoc, defaults);
                return effective.ToString(Formatting.Indented);
            }
            catch (Exception ex)
            {
                InternalLogger.Warn("Error reading instructions: " + ex.Message);
                return "{}";
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Validates, normalizes and persists the full instructions document.
        /// </summary>
        public async Task<InstructionUpdateResult> UpdateAsync(string jsonInstructions, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(jsonInstructions))
                return InstructionUpdateResult.Fail("The instructions payload is empty.");

            JObject doc;
            try
            {
                doc = JObject.Parse(jsonInstructions);
            }
            catch (Exception ex)
            {
                return InstructionUpdateResult.Fail("Invalid JSON format: " + ex.Message);
            }

            IReadOnlyList<InstructionDefaultTab> defaults = await GetDefaultsSafeAsync(cancellationToken).ConfigureAwait(false);

            if (!TryNormalize(doc, defaults, out JObject normalized, out string error))
                return InstructionUpdateResult.Fail(error);

            byte[] data = Encoding.UTF8.GetBytes(normalized.ToString(Formatting.Indented));

            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _fileSystem.WriteAllBytesAsync(_filePath, data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fileLock.Release();
            }

            return InstructionUpdateResult.Ok();
        }

        /// <summary>
        /// Updates only <c>selectedTabId</c>.
        /// </summary>
        public async Task UpdateSelectedTabAsync(string selectedTabId, CancellationToken cancellationToken = default)
        {
            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_fileSystem.FileExists(_filePath))
                    return;

                string raw = await _fileSystem.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                JObject doc;
                try
                {
                    doc = JObject.Parse(raw);
                }
                catch (Exception ex)
                {
                    InternalLogger.Warn("Error parsing instructions for selected tab update: " + ex.Message);
                    return;
                }

                if (!TryGetIntValue(selectedTabId, out int tabId))
                    return;

                IReadOnlyList<InstructionDefaultTab> defaults = await GetDefaultsSafeAsync(cancellationToken).ConfigureAwait(false);
                List<JObject> effectiveTabs = BuildEffectiveTabs(doc, defaults);

                if (!effectiveTabs.Any(t => TryGetInt(t["id"], out int id) && id == tabId))
                    return;

                doc["selectedTabId"] = tabId;

                byte[] data = Encoding.UTF8.GetBytes(doc.ToString(Formatting.Indented));
                await _fileSystem.WriteAllBytesAsync(_filePath, data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task<string> GetInstructionTabIdByDisplayNameAsync(string displayName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return null;

            string json = await GetAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                JObject jObject = JObject.Parse(json);
                if (!(jObject["tabs"] is JArray tabs))
                    return null;

                foreach (JToken tab in tabs)
                {
                    string name = tab["displayName"]?.Value<string>();
                    bool enabled = tab["enabled"]?.Value<bool>() ?? false;
                    string id = tab["id"]?.Value<string>();

                    if (enabled &&
                        !string.IsNullOrWhiteSpace(name) &&
                        !string.IsNullOrWhiteSpace(id) &&
                        string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                InternalLogger.Warn("Error searching instructions by displayName: " + ex.Message);
                return null;
            }
        }

        private async Task<IReadOnlyList<InstructionDefaultTab>> GetDefaultsSafeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var defaults = await _defaultsProvider.GetDefaultTabsAsync(cancellationToken).ConfigureAwait(false);
                return defaults ?? new List<InstructionDefaultTab>();
            }
            catch (Exception ex)
            {
                InternalLogger.Warn("Error reading instruction defaults: " + ex.Message);
                return new List<InstructionDefaultTab>();
            }
        }

        private JObject BuildEffectiveDocument(JObject fileDoc, IReadOnlyList<InstructionDefaultTab> defaults)
        {
            List<JObject> tabs = BuildEffectiveTabs(fileDoc, defaults);

            var effective = (JObject)fileDoc.DeepClone();
            effective["tabs"] = new JArray(tabs.Cast<JToken>());
            effective["selectedTabId"] = NormalizeSelectedTabId(fileDoc["selectedTabId"], tabs, defaults);
            return effective;
        }

        private static List<JObject> BuildEffectiveTabs(JObject fileDoc, IReadOnlyList<InstructionDefaultTab> defaults)
        {
            var defaultIds = new HashSet<int>(defaults.Select(d => d.Id));

            var fileTabsById = new Dictionary<int, JObject>();
            var userTabs = new List<JObject>();

            if (fileDoc["tabs"] is JArray fileTabs)
            {
                foreach (JToken token in fileTabs)
                {
                    if (!(token is JObject tab))
                        continue;

                    if (!TryGetInt(tab["id"], out int id))
                        continue;

                    if (defaultIds.Contains(id))
                    {
                        if (!fileTabsById.ContainsKey(id))
                            fileTabsById[id] = tab;
                    }
                    else
                    {
                        userTabs.Add(tab);
                    }
                }
            }

            var result = new List<JObject>();

            foreach (InstructionDefaultTab def in defaults)
            {
                JObject tab;
                if (fileTabsById.TryGetValue(def.Id, out JObject existing))
                {
                    tab = (JObject)existing.DeepClone();
                    tab["id"] = def.Id;
                    tab["displayName"] = def.DisplayName;

                    if (tab["prompt"] == null || tab["prompt"].Type != JTokenType.String)
                        tab["prompt"] = def.Prompt;
                    if (!TryGetDouble(tab["temperature"], out _))
                        tab["temperature"] = def.Temperature;
                    if (tab["enabled"] == null || tab["enabled"].Type != JTokenType.Boolean)
                        tab["enabled"] = def.Enabled;
                }
                else
                {
                    tab = new JObject
                    {
                        ["id"] = def.Id,
                        ["displayName"] = def.DisplayName,
                        ["enabled"] = def.Enabled,
                        ["temperature"] = def.Temperature,
                        ["prompt"] = def.Prompt,
                    };
                }

                result.Add(tab);
            }

            var orderedUserTabs = userTabs
                .Select(t => { bool ok = TryGetInt(t["id"], out int id); return new { Tab = t, Id = ok ? id : 0 }; })
                .OrderBy(x => x.Id);

            foreach (var entry in orderedUserTabs)
            {
                var clone = (JObject)entry.Tab.DeepClone();
                clone["id"] = entry.Id;

                string name = clone["displayName"]?.Type == JTokenType.String ? clone["displayName"].ToString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    clone["displayName"] = "Custom " + entry.Id;

                result.Add(clone);
            }

            return result;
        }

        private static int NormalizeSelectedTabId(JToken token, List<JObject> tabs, IReadOnlyList<InstructionDefaultTab> defaults)
        {
            int fallback = defaults.Count > 0
                ? defaults[0].Id
                : (tabs.Count > 0 && TryGetInt(tabs[0]["id"], out int firstId) ? firstId : 0);

            if (TryGetInt(token, out int selectedId) &&
                tabs.Any(t => TryGetInt(t["id"], out int id) && id == selectedId))
            {
                return selectedId;
            }

            return fallback;
        }

        private static bool TryNormalize(JObject doc, IReadOnlyList<InstructionDefaultTab> defaults, out JObject normalized, out string error)
        {
            normalized = null;
            error = null;

            var defaultIds = new HashSet<int>(defaults.Select(d => d.Id));

            if (!(doc["tabs"] is JArray tabsToken) || tabsToken.Count == 0)
            {
                error = "The 'tabs' array is required and must not be empty.";
                return false;
            }

            var seenIds = new HashSet<int>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedTabs = new List<JObject>();
            int userTabCount = 0;

            foreach (JToken token in tabsToken)
            {
                if (!(token is JObject tab))
                {
                    error = "Each tab must be a JSON object.";
                    return false;
                }

                if (!TryGetInt(tab["id"], out int id))
                {
                    error = "Each tab must have an integer 'id'.";
                    return false;
                }

                if (!seenIds.Add(id))
                {
                    error = "Duplicate tab id: " + id + ".";
                    return false;
                }

                bool isDefault = defaultIds.Contains(id);

                if (!isDefault)
                {
                    if (id < UserTabIdStart)
                    {
                        error = "Tab id " + id + " falls in the reserved range 10.." + ReservedMaxDefaultId +
                                ". User tabs must have id >= " + UserTabIdStart + ".";
                        return false;
                    }

                    userTabCount++;
                    if (userTabCount > MaxUserTabs)
                    {
                        error = "Too many user tabs (maximum is " + MaxUserTabs + ").";
                        return false;
                    }
                }

                string displayName = tab["displayName"]?.Type == JTokenType.String
                    ? tab["displayName"].ToString().Trim()
                    : null;

                if (isDefault)
                {
                    string canonical = defaults.First(d => d.Id == id).DisplayName;
                    if (!string.Equals(displayName, canonical, StringComparison.Ordinal))
                    {
                        error = "The display name of the built-in tab '" + canonical + "' cannot be changed.";
                        return false;
                    }

                    displayName = canonical;
                }
                else if (string.IsNullOrWhiteSpace(displayName))
                {
                    error = "Each tab must have a non-empty 'displayName'.";
                    return false;
                }

                if (!seenNames.Add(displayName))
                {
                    error = "Duplicate tab display name: '" + displayName + "'.";
                    return false;
                }

                if (tab["prompt"] != null && tab["prompt"].Type != JTokenType.String)
                {
                    error = "The 'prompt' of tab '" + displayName + "' must be a string.";
                    return false;
                }

                string prompt = tab["prompt"] != null ? tab["prompt"].ToString() : string.Empty;

                InstructionDefaultTab fallbackDefault = defaults.FirstOrDefault(d => d.Id == id);

                double temperature = fallbackDefault?.Temperature ?? DefaultTemperature;
                if (tab["temperature"] != null)
                {
                    if (!TryGetDouble(tab["temperature"], out temperature) ||
                        double.IsNaN(temperature) || double.IsInfinity(temperature))
                    {
                        error = "The 'temperature' of tab '" + displayName + "' must be a number.";
                        return false;
                    }

                    if (temperature < 0.0 || temperature > 1.0)
                    {
                        error = "The 'temperature' of tab '" + displayName + "' must be between 0 and 1.";
                        return false;
                    }
                }

                bool enabled = fallbackDefault?.Enabled ?? true;
                if (tab["enabled"] != null)
                {
                    if (tab["enabled"].Type != JTokenType.Boolean)
                    {
                        error = "The 'enabled' flag of tab '" + displayName + "' must be a boolean.";
                        return false;
                    }

                    enabled = tab["enabled"].Value<bool>();
                }

                var normalizedTab = (JObject)tab.DeepClone();
                normalizedTab["id"] = id;
                normalizedTab["displayName"] = displayName;
                normalizedTab["prompt"] = prompt;
                normalizedTab["temperature"] = temperature;
                normalizedTab["enabled"] = enabled;
                normalizedTabs.Add(normalizedTab);
            }

            foreach (InstructionDefaultTab def in defaults)
            {
                if (!seenIds.Contains(def.Id))
                {
                    error = "The built-in tab '" + def.DisplayName + "' is missing.";
                    return false;
                }
            }

            List<JObject> orderedTabs = OrderTabs(normalizedTabs, defaults);
            int selectedTabId = NormalizeSelectedTabId(doc["selectedTabId"], orderedTabs, defaults);

            normalized = (JObject)doc.DeepClone();
            normalized["tabs"] = new JArray(orderedTabs.Cast<JToken>());
            normalized["selectedTabId"] = selectedTabId;
            return true;
        }

        private static List<JObject> OrderTabs(List<JObject> tabs, IReadOnlyList<InstructionDefaultTab> defaults)
        {
            var byId = new Dictionary<int, JObject>();
            foreach (JObject tab in tabs)
            {
                if (TryGetInt(tab["id"], out int id))
                    byId[id] = tab;
            }

            var ordered = new List<JObject>();

            foreach (InstructionDefaultTab def in defaults)
            {
                if (byId.TryGetValue(def.Id, out JObject defaultTab))
                    ordered.Add(defaultTab);
            }

            var defaultIds = new HashSet<int>(defaults.Select(d => d.Id));
            foreach (var entry in byId.Where(kv => !defaultIds.Contains(kv.Key)).OrderBy(kv => kv.Key))
                ordered.Add(entry.Value);

            return ordered;
        }

        private static bool TryGetInt(JToken token, out int value)
        {
            value = 0;
            if (token == null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            if (token.Type == JTokenType.String)
                return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

            if (token.Type == JTokenType.Float)
            {
                double d = token.Value<double>();
                if (!double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Floor(d))
                {
                    value = (int)d;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetIntValue(string raw, out int value)
        {
            value = 0;
            return !string.IsNullOrWhiteSpace(raw) &&
                   int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetDouble(JToken token, out double value)
        {
            value = 0;
            if (token == null)
                return false;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<double>();
                return true;
            }

            if (token.Type == JTokenType.String)
                return double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            return false;
        }
    }
}
