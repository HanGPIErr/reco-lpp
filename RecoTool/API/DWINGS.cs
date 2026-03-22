using RecoTool.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RecoTool.Services.External;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace RecoTool.API
{
    /*=====================================================================
      PUBLIC CLASS – Dwings
      =====================================================================*/
    public class Dwings : IDisposable
    {
        // -----------------------------------------------------------------
        // 1️⃣  Client partagé (conserve le cookie SMSESSION)
        // -----------------------------------------------------------------
        private DwingsHttpClient _httpClient;                 // lazy‑init, shared
        private readonly object _clientLock = new object();   // double‑check thread‑safe

        // -----------------------------------------------------------------
        // Retourne (et crée si nécessaire) le client partagé.
        // -----------------------------------------------------------------
        private DwingsHttpClient GetHttpClient()
        {
            if (_httpClient == null)
            {
                lock (_clientLock)                 // double‑check
                {
                    if (_httpClient == null)
                        _httpClient = new DwingsHttpClient(this);
                }
            }
            return _httpClient;
        }

        // -----------------------------------------------------------------
        // Existing GetClientCertificate – unchanged
        // -----------------------------------------------------------------
        public X509Certificate2 GetClientCertificate(string filter = "Users Authentication")
        {
            string storeScope = "CurrentUser";
            string sourceStorename = "My";

            var srcStore = new X509Store(sourceStorename,
                (StoreLocation)Enum.Parse(typeof(StoreLocation), storeScope));
            srcStore.Open(OpenFlags.ReadOnly);

            var smartCardCert = srcStore.Certificates
                .Find(X509FindType.FindByIssuerName, filter, false)
                .Cast<X509Certificate2>()
                .FirstOrDefault();

            srcStore.Close();
            return smartCardCert;
        }

        // -----------------------------------------------------------------
        // Helpers used by the HttpClient wrapper
        // -----------------------------------------------------------------
        internal string GetDwingsServer()
        {
            return Environment.UserName == "b04406"
                ? "https://wings-breakfix.dev.echonet"
                : "https://wings.cib.echonet";
        }

        internal int GetSleepBetweenCalls() => 250;   // ms – same as VBA

        // -----------------------------------------------------------------
        // 2️⃣  Exemple d’appel simple – GET /v1/user‑info
        // -----------------------------------------------------------------
        /// <summary>
        /// Returns the JSON object returned by GET /v1/user-info.
        /// The method automatically handles the 302 → SMSESSION flow,
        /// stores the cookie and re‑uses it for subsequent calls.
        /// </summary>
        public JObject GetUserInfo()
        {
            const string Resource = "/v1/user-info";

            // No query parameters, no body – we only need the cookie handling.
            string raw = GetHttpClient().Execute(
                resource: Resource,
                method: HttpMethod.Get,
                queryParams: null,
                body: null,
                maxAttempts: 3);

            // The endpoint returns a JSON object (or empty on error – let the caller decide).
            return string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
        }

        // -----------------------------------------------------------------
        // 3️⃣  Dwings_IsRedButtonTriggerAllowed – utilise le client partagé
        // -----------------------------------------------------------------
        /// <summary>
        /// GET  /v2/payment-requests/payment-trigger-authorization
        /// </summary>
        public Dictionary<string, object> Dwings_IsRedButtonTriggerAllowed(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("id cannot be null or empty.", nameof(id));

            const string Resource = "/v2/payment-requests/payment-trigger-authorization";

            var query = new Dictionary<string, string>
            {
                { "paymentRequestId", id },
                { "loggedUserId", GetUserName() }
            };

            try
            {
                string raw = GetHttpClient().Execute(
                    resource: Resource,
                    method: HttpMethod.Get,
                    queryParams: query,
                    body: null,
                    maxAttempts: 3);

                var token = JToken.Parse(raw);
                return ConvertJTokenToDictionary(token);
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------
        // 4️⃣  Dwings_PressRedButton – réutilise le même cookie (obsolète)
        // -----------------------------------------------------------------
        [Obsolete]
        public (bool, string) Dwings_PressRedButtonToReview(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("id cannot be null or empty.", nameof(id));

            const string Resource = "/v2/payment-requests";

            var bodyObj = new JObject
            {
                ["id"] = id,
                ["retry"] = "false"
            };
            string body = bodyObj.ToString(Formatting.None);

            try
            {
                string raw = GetHttpClient().Execute(
                    resource: Resource,
                    method: HttpMethod.Post,
                    queryParams: null,
                    body: body,
                    maxAttempts: 3);

                var token = JToken.Parse(raw);
                if (token.Type == JTokenType.Object && token["Results"] != null)
                    return (true, token["Results"]!.ToString());

                return (true, raw);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // -----------------------------------------------------------------
        // 5️⃣  Dwings_PressBlueButton – réutilise le même cookie
        // -----------------------------------------------------------------
        public (bool Success, string Message) Dwings_PressBlueButton(
            string externalReference,
            DateTime executionDate,          // UTC
            string amount,
            string bookingEntityCode,
            string currency,
            string paymentRequestId)
        {
            // -------- validation minimale ---------------------------------
            if (string.IsNullOrWhiteSpace(externalReference)) throw new ArgumentException(nameof(externalReference));
            if (string.IsNullOrWhiteSpace(bookingEntityCode)) throw new ArgumentException(nameof(bookingEntityCode));
            if (string.IsNullOrWhiteSpace(currency)) throw new ArgumentException(nameof(currency));
            if (string.IsNullOrWhiteSpace(paymentRequestId)) throw new ArgumentException(nameof(paymentRequestId));

            const string Resource = "/v1/payments";

            // ---------- création de la date UTC à 23:00 du jour précédent ----------
            // 1️⃣ on crée la date à 23:00 UTC du même jour
            var utcAt23 = new DateTime(
                executionDate.Year,
                executionDate.Month,
                executionDate.Day,
                0, 0, 0,
                DateTimeKind.Utc);

            // 2️⃣ on recule d’un jour pour obtenir la veille (ex. 01/12 → 30/11)
            DateTime utcResult = utcAt23.AddDays(0);

            // ---------- format exact demandé ----------
            // le Z doit être littéral, d’où les apostrophes autour du Z
            string isoDate = utcResult
                .ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            // -------- conversion du montant -------------------------------
            if (!TryParseAmount(amount, out decimal amountDecimal, out string parseError))
                return (false, $"Montant invalide : {parseError}");

            // -------- payload JSON ----------------------------------------
            var bodyObj = new JObject
            {
                ["externalReference"] = externalReference,
                ["executionDate"] = isoDate,
                ["amount"] = amountDecimal,
                ["bookingEntityCode"] = bookingEntityCode,
                ["currency"] = currency,
                ["paymentRequestId"] = paymentRequestId
            };
            string body = bodyObj.ToString(Formatting.None);

            // -------- appel HTTP -----------------------------------------
            try
            {
                string raw = GetHttpClient().Execute(
                    resource: Resource,
                    method: HttpMethod.Post,
                    queryParams: null,
                    body: body,
                    maxAttempts: 1);

                // 201 Created → empty body = success
                if (string.IsNullOrWhiteSpace(raw))
                    return (true, string.Empty);

                JToken token = JToken.Parse(raw);

                // tableau d’erreurs
                if (token.Type == JTokenType.Array)
                {
                    var errors = token
                        .Select(t => $"{t["title"]?.ToString()}: {t["detail"]?.ToString()}")
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToArray();

                    return (false, string.Join(" | ", errors));
                }

                // objet contenant “Results”
                if (token.Type == JTokenType.Object && token["Results"] != null)
                    return (true, token["Results"]!.ToString());

                // tout le reste
                return (true, raw);
            }
            catch (Exception ex)
            {
                return (false, $"{body} : {ex.Message}");
            }
        }

        // -----------------------------------------------------------------
        // Small utility methods
        // -----------------------------------------------------------------
        private string GetUserName() => Environment.UserName;

        private static Dictionary<string, object> ConvertJTokenToDictionary(JToken token)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (token.Type != JTokenType.Object)
                throw new ArgumentException("Root token must be an object.", nameof(token));

            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty prop in token)
                dict[prop.Name] = ConvertJTokenValue(prop.Value);
            return dict;
        }

        private static bool TryParseAmount(string text, out decimal value, out string error)
        {
            string normalized = text.Trim().Replace(',', '.');

            bool ok = decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out value);

            if (!ok)
            {
                error = $"Impossible de convertir \"{text}\" en nombre décimal.";
                return false;
            }

            const int maxDecimals = 4;
            int actualDecimals = BitConverter.GetBytes(decimal.GetBits(value)[3])[2];
            if (actualDecimals > maxDecimals)
            {
                error = $"Le montant possède {actualDecimals} décimales ; le maximum autorisé est {maxDecimals}.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static object ConvertJTokenValue(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object: return ConvertJTokenToDictionary(token);
                case JTokenType.Array: return token.Select(ConvertJTokenValue).ToList();
                case JTokenType.Integer: return token.Value<long>();
                case JTokenType.Float: return token.Value<double>();
                case JTokenType.Boolean: return token.Value<bool>();
                case JTokenType.Date: return token.Value<DateTime>();
                case JTokenType.String: return token.Value<string>();
                case JTokenType.Null: return null!;
                default: return token.ToString();
            }
        }

        // -----------------------------------------------------------------
        // IDisposable – libère le client HTTP partagé
        // -----------------------------------------------------------------
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /*=====================================================================
      INTERNAL CLASS – DwingsHttpClient
      =====================================================================*/
    internal sealed class DwingsHttpClient : IDisposable
    {
        private HttpClient _client;
        private HttpClientHandler _handler;
        private readonly Dwings _owner;
        private string _sessionCookie;          // "SMSESSION=…"
        private const string UserAgentDefault =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/99.0.4844.74 Safari/537.36 Edg/99.0.1150.46";

        public DwingsHttpClient(Dwings owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            CreateInternalClient();
        }

        private void CreateInternalClient()
        {
            // -----------------------------------------------------------------
            // Handler – client‑certificate + manual cookie handling
            // -----------------------------------------------------------------
            _handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                UseCookies = false,                // we manage the cookie ourselves
                AutomaticDecompression = DecompressionMethods.GZip |
                                          DecompressionMethods.Deflate,
                AllowAutoRedirect = false          // **important** – we need the 302 response
            };

            var cert = _owner.GetClientCertificate();
            if (cert != null)
                _handler.ClientCertificates.Add(cert);

            // -----------------------------------------------------------------
            // HttpClient + static headers
            // -----------------------------------------------------------------
            _client = new HttpClient(_handler)
            {
                BaseAddress = new Uri(_owner.GetDwingsServer()),
                Timeout = TimeSpan.FromSeconds(60)
            };

            _client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            _client.DefaultRequestHeaders.AcceptEncoding.Add(
                new StringWithQualityHeaderValue("gzip"));
            _client.DefaultRequestHeaders.AcceptEncoding.Add(
                new StringWithQualityHeaderValue("deflate"));
            _client.DefaultRequestHeaders.AcceptEncoding.Add(
                new StringWithQualityHeaderValue("br"));

            _client.DefaultRequestHeaders.AcceptLanguage.Add(
                new StringWithQualityHeaderValue("fr-FR", 0.9));
            _client.DefaultRequestHeaders.AcceptLanguage.Add(
                new StringWithQualityHeaderValue("en", 0.8));
            _client.DefaultRequestHeaders.AcceptLanguage.Add(
                new StringWithQualityHeaderValue("en-GB", 0.7));
            _client.DefaultRequestHeaders.AcceptLanguage.Add(
                new StringWithQualityHeaderValue("en-US", 0.6));

            _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentDefault);
        }

        /// <summary>
        /// Recrée le HttpClient et le handler pour purger l'état SSL/Schannel obsolète.
        /// Réinitialise également le cookie de session pour forcer une nouvelle authentification 302.
        /// </summary>
        private void RecreateHttpClient()
        {
            try { _client?.Dispose(); } catch { }
            try { _handler?.Dispose(); } catch { }
            _sessionCookie = null;
            CreateInternalClient();
        }

        /// <summary>
        /// Execute a request while handling the 302 → SMSESSION flow,
        /// storing the cookie and re‑using it for subsequent calls.
        /// </summary>
        public string Execute(
            string resource,
            HttpMethod method,
            IDictionary<string, string> queryParams = null,
            string body = null,
            int maxAttempts = 3)
        {
            // Certificate needed for the manual redirect
            X509Certificate2 cert = _owner.GetClientCertificate();

            int attempt = 0;
            while (true)
            {
                attempt++;

                // ---------------------------------------------------------
                // 1️⃣ Build the request URI (resource + optional query)
                // ---------------------------------------------------------
                var uriBuilder = new UriBuilder(new Uri(_client.BaseAddress, resource));

                if (queryParams != null && queryParams.Any())
                {
                    var qs = string.Join("&",
                        queryParams.Select(kv =>
                            $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
                    uriBuilder.Query = qs;
                }

                var request = new HttpRequestMessage(method, uriBuilder.Uri);

                // ---------------------------------------------------------
                // 2️⃣ Body (POST only)
                // ---------------------------------------------------------
                if (method == HttpMethod.Post && body != null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                // ---------------------------------------------------------
                // 3️⃣ Attach SMSESSION cookie if we already have it
                // ---------------------------------------------------------
                if (!string.IsNullOrEmpty(_sessionCookie))
                    request.Headers.Add("Cookie", _sessionCookie);

                // ---------------------------------------------------------
                // 4️⃣ Send request
                // ---------------------------------------------------------
                HttpResponseMessage response;
                try
                {
                    response = _client.SendAsync(request).Result;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine($"[Dwings] Connection error (attempt {attempt}), recreating client: {ex.Message}");
                    RecreateHttpClient(); // purge stale SSL state + reset session cookie
                    Thread.Sleep(_owner.GetSleepBetweenCalls());
                    continue;
                }

                // ---------------------------------------------------------
                // 5️⃣ 302 – obtain SMSESSION and retry the *original* request
                // ---------------------------------------------------------
                if (response.StatusCode == HttpStatusCode.Found) // 302
                {
                    // Capture the cookie that may already be present on the 302 response
                    ExtractSessionCookie(response);

                    var location = response.Headers.Location
                        ?? throw new InvalidOperationException("302 response without Location header.");

                    // Follow the redirect **once** (the redirect does NOT need a body)
                    var redirectHandler = new HttpClientHandler
                    {
                        ClientCertificateOptions = ClientCertificateOption.Manual,
                        UseCookies = false,
                        AutomaticDecompression = DecompressionMethods.GZip |
                                                  DecompressionMethods.Deflate,
                        AllowAutoRedirect = false
                    };
                    if (cert != null)
                        redirectHandler.ClientCertificates.Add(cert);

                    using (var redirectClient = new HttpClient(redirectHandler))
                    {
                        redirectClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentDefault);

                        var redirectResponse = redirectClient.GetAsync(location).Result;
                        ExtractSessionCookie(redirectResponse);

                        // Small pause (mimic original VBA delay) then retry the **original** request
                        Thread.Sleep(_owner.GetSleepBetweenCalls());
                        continue;
                    }
                }

                // ---------------------------------------------------------
                // 6️⃣ Store cookie if the *final* response carries it (some APIs
                //     set SMSESSION on the 200/201 response as well)
                // ---------------------------------------------------------
                ExtractSessionCookie(response);

                // ---------------------------------------------------------
                // 7️⃣ Session expirée (401/403) – vider le cookie et relancer
                // ---------------------------------------------------------
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    System.Diagnostics.Debug.WriteLine($"[Dwings] Session expired ({(int)response.StatusCode}), clearing cookie and retrying.");
                    _sessionCookie = null; // force 302 flow on next attempt
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(_owner.GetSleepBetweenCalls());
                        continue;
                    }
                }

                // ---------------------------------------------------------
                // 8️⃣ Success ?
                // ---------------------------------------------------------
                if (response.IsSuccessStatusCode)
                {
                    return response.Content.ReadAsStringAsync().Result;
                }

                // ---------------------------------------------------------
                // 9️⃣ Retry / fail
                // ---------------------------------------------------------
                if (attempt >= maxAttempts)
                {
                    var payload = response.Content.ReadAsStringAsync().Result;
                    throw new InvalidOperationException(
                        $"Dwings request failed after {maxAttempts} attempts. " +
                        $"Status: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                        $"Content: {payload}");
                }

                Thread.Sleep(_owner.GetSleepBetweenCalls());
            }
        }

        // -----------------------------------------------------------------
        // Extracts the SMSESSION cookie from a response (if present) and
        // stores it in the private field.
        // -----------------------------------------------------------------
        private void ExtractSessionCookie(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                var smsession = setCookies
                    .FirstOrDefault(c => c.StartsWith("SMSESSION=", StringComparison.OrdinalIgnoreCase));

                if (smsession != null)
                {
                    var semi = smsession.IndexOf(';');
                    _sessionCookie = semi > 0 ? smsession.Substring(0, semi) : smsession;
                }
            }
        }

        // -----------------------------------------------------------------
        public void Dispose()
        {
            try { _client?.Dispose(); } catch { }
            try { _handler?.Dispose(); } catch { }
        }
    }
}
