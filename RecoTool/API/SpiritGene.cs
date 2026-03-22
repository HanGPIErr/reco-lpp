using RecoTool.Utils;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RecoTool.API
{
    public class SpiritGene : IDisposable
    {
        private const string BASE_URL = "https://spirit-gene.bddf.echonet";
        private const int SESSION_TIMEOUT_MINUTES = 30;

        private HttpClient _httpClient;
        private CookieContainer _cookieContainer;
        private DateTime _authenticatedTime;

        private readonly RequestCache _cache = new RequestCache();

        public SpiritGene()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _httpClient = CreateHttpClient();
        }

        private HttpClient CreateHttpClient()
        {
            _cookieContainer = new CookieContainer();

            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Automatic,
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = _cookieContainer
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(BASE_URL),
                Timeout = TimeSpan.FromSeconds(30)
            };

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0");
            return client;
        }

        private void AddApiHeaders(HttpRequestMessage request)
        {
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Referrer = new Uri($"{BASE_URL}/AVPW1/");
            request.Headers.Add("Origin", BASE_URL);
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
        }

        /// <summary>
        /// Authentification utilisant une Smartcard Windows et récupération automatique du cookie.
        /// Recrée systématiquement le HttpClient pour purger l'état SSL/Schannel obsolète.
        /// </summary>
        public async Task AuthenticateAsync()
        {
            var old = _httpClient;
            _httpClient = CreateHttpClient();
            try { old?.Dispose(); } catch { }

            var request = new HttpRequestMessage(HttpMethod.Get, "/AVPW1/");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Sec-Fetch-Dest", "document");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");

            try
            {
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                _authenticatedTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                _authenticatedTime = DateTime.MinValue;
                System.Diagnostics.Debug.WriteLine($"[SpiritGene] AuthenticateAsync failed: {ex.Message}");
                MessageBox.Show($"Impossible to connect to {BASE_URL}\n{ex.Message}");
            }
        }

        private async Task EnsureAuthenticated()
        {
            if (_authenticatedTime == DateTime.MinValue ||
                (DateTime.Now - _authenticatedTime).TotalMinutes >= SESSION_TIMEOUT_MINUTES)
                await AuthenticateAsync();
        }

        /// <summary>
        /// Exécute un appel API avec un retry automatique en cas d'erreur SSL/réseau/session :
        /// recrée le client HTTP, se ré-authentifie, puis renvoie une seule fois.
        /// </summary>
        private async Task<string> ExecuteApiAsync(Func<HttpRequestMessage> requestFactory, string cacheKey = null)
        {
            if (cacheKey != null && _cache.TryGet(cacheKey, out var cached))
                return cached;

            await EnsureAuthenticated();

            async Task<string> SendOnce()
            {
                var req = requestFactory();
                var response = await _httpClient.SendAsync(req);
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException($"Session expired (HTTP {(int)response.StatusCode})");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }

            string json;
            try
            {
                json = await SendOnce();
            }
            catch (Exception ex) when (
                ex is HttpRequestException ||
                ex is UnauthorizedAccessException ||
                ex is System.Security.Authentication.AuthenticationException ||
                ex is System.IO.IOException)
            {
                System.Diagnostics.Debug.WriteLine($"[SpiritGene] Connection error, retrying after re-auth: {ex.Message}");
                _authenticatedTime = DateTime.MinValue;
                await AuthenticateAsync();
                try
                {
                    json = await SendOnce();
                }
                catch (Exception retryEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[SpiritGene] Retry failed: {retryEx.Message}");
                    MessageBox.Show($"SpiritGene request failed after retry: {retryEx.Message}");
                    return null;
                }
            }

            if (json != null && cacheKey != null)
                _cache.Set(cacheKey, json);

            return json;
        }

        public async Task<SpiritGeneUser.FoncOut101> GetUserInfo()
        {
            var body = "{\"no_version_1\":1}";
            var cacheKey = $"POST:/spirit-server/AVPW1/recup_user.1.0.o_recup_user:{body}";

            var json = await ExecuteApiAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/spirit-server/AVPW1/recup_user.1.0.o_recup_user")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                AddApiHeaders(req);
                return req;
            }, cacheKey);

            if (json == null) return null;
            try { return JsonSerializer.Deserialize<SpiritGeneUser.User>(json).Result.FoncOut1.FoncOut101; }
            catch { return null; }
        }

        public async Task<SpiritGeneTransactionsOutput.FoncOut101> GetTransactions(DateTime DateDebut, DateTime DateFin, string BIC, decimal MontantMin, decimal MontantMax, string Sens = "R")
        {
            var body = SpiritGeneTransactionsInput.CreateTransactionBody(DateDebut, DateFin, BIC, (int)(MontantMin * 10000), (int)(MontantMax * 10000), Sens);
            var cacheKey = $"POST:/spirit-server/AVPW1/list_operation.1.0.o_list_ope:{body}";

            var json = await ExecuteApiAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/spirit-server/AVPW1/list_operation.1.0.o_list_ope")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                AddApiHeaders(req);
                return req;
            }, cacheKey);

            if (json == null) return null;
            try { return JsonSerializer.Deserialize<SpiritGeneTransactionsOutput.Output>(json).Result.FoncOut1.FoncOut101; }
            catch { return null; }
        }

        public async Task<SpiritGeneTransactionDetailOutput.GDetOpe> GetTransactionDetails(string TransactionId, string MsgId, string Sens = "R")
        {
            var body = SpiritGeneTransactionDetailInput.CreateTransactionBody(TransactionId, MsgId, Sens);
            var cacheKey = $"POST:/spirit-server/AVPW1/detail_ope_2.1.0.o_detail_ope:{body}";

            var json = await ExecuteApiAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/spirit-server/AVPW1/detail_ope_2.1.0.o_detail_ope")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                AddApiHeaders(req);
                return req;
            }, cacheKey);

            if (json == null) return null;
            try { return JsonSerializer.Deserialize<SpiritGeneTransactionDetailOutput.Root>(json).Result.FoncOut1.FoncOut101.GDetOpe; }
            catch { return null; }
        }

        public string GetStoredCookies()
        {
            return _cookieContainer?.GetCookieHeader(new Uri(BASE_URL));
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}