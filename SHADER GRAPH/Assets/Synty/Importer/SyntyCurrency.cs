#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Synty.Tools
{
    /// <summary>
    /// Central currency handling for the Passport tool. All prices in the catalog are in USD;
    /// this converts and formats them into the user's selected display currency using live
    /// exchange rates fetched from the worker (cached server-side). The selected currency is
    /// persisted to EditorPrefs. On first login it auto-picks a currency from the account's
    /// country, unless the user has already chosen one manually.
    ///
    /// Every price the tool shows should go through Format(usd) (full string e.g. "$16.50 NZD")
    /// or Convert(usd) (numeric, converted) so a single switch updates the whole UI.
    /// </summary>
    public static class SyntyCurrency
    {
        private const string WORKER_URL = "https://synty-downloads.syntystore.workers.dev";
        private const string PREF_CURRENCY = "SyntyPassport_DisplayCurrency";
        private const string PREF_CURRENCY_MANUAL = "SyntyPassport_CurrencyManual"; // user picked explicitly
        private const string PREF_RATES_JSON = "SyntyPassport_ExchangeRatesJson";
        private const string PREF_RATES_TIME = "SyntyPassport_ExchangeRatesTime";

        // Currencies offered in the dropdown. Symbol is what precedes the amount.
        public static readonly (string code, string symbol, string label)[] Supported = new (string, string, string)[]
        {
            ("USD", "$",   "USD - US Dollar"),
            ("NZD", "$",   "NZD - NZ Dollar"),
            ("AUD", "$",   "AUD - Australian Dollar"),
            ("EUR", "\u20AC", "EUR - Euro"),
            ("GBP", "\u00A3", "GBP - British Pound"),
            ("CAD", "$",   "CAD - Canadian Dollar"),
            ("JPY", "\u00A5", "JPY - Japanese Yen"),
        };

        // Map of country code -> default currency for auto-detect.
        private static readonly Dictionary<string, string> CountryToCurrency = new Dictionary<string, string>
        {
            { "NZ", "NZD" }, { "AU", "AUD" }, { "US", "USD" }, { "CA", "CAD" },
            { "GB", "GBP" }, { "JP", "JPY" },
            // Eurozone members
            { "DE", "EUR" }, { "FR", "EUR" }, { "IE", "EUR" }, { "ES", "EUR" }, { "IT", "EUR" },
            { "NL", "EUR" }, { "BE", "EUR" }, { "AT", "EUR" }, { "PT", "EUR" }, { "FI", "EUR" },
        };

        private static Dictionary<string, float> _rates = new Dictionary<string, float> { { "USD", 1f } };
        private static bool _ratesLoaded;

        /// <summary>Currently selected display currency code (e.g. "USD", "NZD").</summary>
        public static string Current
        {
            get { return EditorPrefs.GetString(PREF_CURRENCY, "USD"); }
            private set { EditorPrefs.SetString(PREF_CURRENCY, value); }
        }

        public static bool IsManuallySet
        {
            get { return EditorPrefs.GetBool(PREF_CURRENCY_MANUAL, false); }
        }

        /// <summary>Symbol for the current currency (e.g. "$").</summary>
        public static string Symbol
        {
            get
            {
                foreach (var c in Supported)
                    if (c.code == Current) return c.symbol;
                return "$";
            }
        }

        /// <summary>Raised whenever the currency or rates change, so the UI can rebuild prices.</summary>
        public static event Action OnCurrencyChanged;

        // ── Selection ──

        /// <summary>User explicitly picks a currency (from the dropdown). Persisted + flagged manual.</summary>
        public static void SetCurrencyManual(string code)
        {
            if (!IsSupported(code)) return;
            Current = code;
            EditorPrefs.SetBool(PREF_CURRENCY_MANUAL, true);
            OnCurrencyChanged?.Invoke();
        }

        /// <summary>Switch back to automatic (location-based) selection and re-detect immediately.</summary>
        public static void SetAuto()
        {
            EditorPrefs.SetBool(PREF_CURRENCY_MANUAL, false);
            _ = DetectFromLocationAsync();
        }

        /// <summary>
        /// Auto-pick a currency from the account country, but only if the user has not already
        /// chosen one manually. Called after login once the country is known.
        /// </summary>
        public static void AutoSelectFromCountry(string countryCode)
        {
            if (IsManuallySet) return;
            if (string.IsNullOrEmpty(countryCode)) return;
            if (CountryToCurrency.TryGetValue(countryCode.ToUpperInvariant(), out var cur))
            {
                if (cur != Current)
                {
                    Current = cur;
                    OnCurrencyChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// On open, asks the worker for the caller's country from Cloudflare's edge geo-IP and
        /// auto-selects the matching currency — unless the user has already picked one manually.
        /// Any failure (offline, no geo, unmapped country) leaves the currency at its default (USD).
        /// </summary>
        public static async Task DetectFromLocationAsync()
        {
            if (IsManuallySet) return; // never override an explicit choice
            try
            {
                string json = await HttpGetAsync(WORKER_URL + "/geo");
                if (string.IsNullOrEmpty(json)) return;
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"country\"\\s*:\\s*\"([A-Za-z]{2})\"");
                if (m.Success)
                {
                    string country = m.Groups[1].Value;
                    EditorApplication.delayCall += () => AutoSelectFromCountry(country); // apply on the UI tick
                }
            }
            catch { }
        }

        public static bool IsSupported(string code)
        {
            foreach (var c in Supported)
                if (c.code == code) return true;
            return false;
        }

        // ── Conversion + formatting ──

        /// <summary>Converts a USD amount into the current display currency.</summary>
        public static float Convert(float usd)
        {
            if (Current == "USD") return usd;
            if (_rates.TryGetValue(Current, out var rate) && rate > 0f)
                return usd * rate;
            return usd; // no rate -> show USD value rather than nothing
        }

        /// <summary>
        /// Formats a USD amount as a full price string in the current currency, e.g.
        /// "$16.50 NZD". JPY is shown with no decimals. Set includeCode=false to omit the
        /// trailing code (e.g. just "$16.50").
        /// </summary>
        public static string Format(float usd, bool includeCode = true)
        {
            float val = Convert(usd);
            string body;
            if (Current == "JPY")
                body = Symbol + Mathf.Round(val).ToString("N0", CultureInfo.InvariantCulture);
            else
                body = Symbol + val.ToString("N2", CultureInfo.InvariantCulture);
            return includeCode ? body + " " + Current : body;
        }

        /// <summary>The trailing code with a leading space, e.g. " NZD". For sites that build
        /// the numeric part themselves but want the right suffix.</summary>
        public static string CodeSuffix { get { return " " + Current; } }

        // ── Rates loading ──

        /// <summary>
        /// Loads exchange rates: uses the EditorPrefs cache if fresh (&lt; 12h), otherwise
        /// fetches from the worker and caches. Safe to call on every login. Fires
        /// OnCurrencyChanged when fresh rates arrive so prices re-render.
        /// </summary>
        public static async Task EnsureRatesAsync()
        {
            // Load cached rates into memory first (instant), even if we then refresh.
            if (!_ratesLoaded) LoadCachedRates();

            string lastTime = EditorPrefs.GetString(PREF_RATES_TIME, "");
            bool fresh = false;
            if (!string.IsNullOrEmpty(lastTime) && DateTime.TryParse(lastTime, null,
                DateTimeStyles.RoundtripKind, out var t))
            {
                fresh = (DateTime.UtcNow - t.ToUniversalTime()).TotalHours < 12.0;
            }
            if (fresh && _rates.Count > 1) return; // cache good enough

            try
            {
                string json = await HttpGetAsync(WORKER_URL + "/exchange-rates");
                if (!string.IsNullOrEmpty(json))
                {
                    var parsed = ParseRates(json);
                    if (parsed != null && parsed.Count > 1)
                    {
                        _rates = parsed;
                        _ratesLoaded = true;
                        EditorPrefs.SetString(PREF_RATES_JSON, json);
                        EditorPrefs.SetString(PREF_RATES_TIME, DateTime.UtcNow.ToString("o"));
                        OnCurrencyChanged?.Invoke();
                    }
                }
            }
            catch
            {
                // Keep whatever cached/USD-only rates we have; never throw to the caller.
            }
        }

        private static void LoadCachedRates()
        {
            _ratesLoaded = true;
            string json = EditorPrefs.GetString(PREF_RATES_JSON, "");
            if (string.IsNullOrEmpty(json)) return;
            var parsed = ParseRates(json);
            if (parsed != null && parsed.Count > 0) _rates = parsed;
        }

        [Serializable]
        private class RatesEnvelope
        {
            public bool success;
            public string @base;
            public string updated;
            // Unity's JsonUtility can't deserialize an arbitrary-key map, so rates are parsed
            // manually below rather than via this field.
        }

        // The rates object has dynamic keys (currency codes), which JsonUtility can't map. Parse
        // the codes we support directly out of the JSON text.
        private static Dictionary<string, float> ParseRates(string json)
        {
            var dict = new Dictionary<string, float> { { "USD", 1f } };
            if (string.IsNullOrEmpty(json)) return dict;
            // Find the "rates":{ ... } block.
            int ri = json.IndexOf("\"rates\"", StringComparison.Ordinal);
            if (ri < 0) return dict;
            int open = json.IndexOf('{', ri);
            if (open < 0) return dict;
            int depth = 0, close = -1;
            for (int i = open; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { close = i; break; } }
            }
            if (close < 0) return dict;
            string block = json.Substring(open + 1, close - open - 1);
            foreach (var c in Supported)
            {
                if (c.code == "USD") continue;
                string key = "\"" + c.code + "\"";
                int ki = block.IndexOf(key, StringComparison.Ordinal);
                if (ki < 0) continue;
                int colon = block.IndexOf(':', ki + key.Length);
                if (colon < 0) continue;
                int j = colon + 1;
                while (j < block.Length && (block[j] == ' ')) j++;
                int start = j;
                while (j < block.Length && (char.IsDigit(block[j]) || block[j] == '.' || block[j] == '-' || block[j] == 'e' || block[j] == 'E' || block[j] == '+')) j++;
                string num = block.Substring(start, j - start);
                if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0f)
                    dict[c.code] = rate;
            }
            return dict;
        }

        private static async Task<string> HttpGetAsync(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }
}
#endif
