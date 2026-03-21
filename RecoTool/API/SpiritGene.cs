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

        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;
        private DateTime _authenticatedTime;

        private RequestCache _cache = new RequestCache();

        public SpiritGene()
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

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(BASE_URL),
                Timeout = TimeSpan.FromSeconds(30)
            };

            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0");
        }

        /// <summary>
        /// Authentification utilisant une Smartcard Windows et récupération automatique du cookie.
        /// </summary>
        public async Task AuthenticateAsync()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var request = new HttpRequestMessage(HttpMethod.Get, "/AVPW1/");

            // Headers spécifiques à l'authentification
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
            catch
            {
                MessageBox.Show("Impossible to connect to " + _httpClient.BaseAddress);
            }
        }

        private async Task EnsureAuthenticated()
        {
            if ((DateTime.Now - _authenticatedTime).TotalMinutes >= 60)
                await AuthenticateAsync();

        }

        public async Task<SpiritGeneUser.FoncOut101> GetUserInfo()
        {
            await EnsureAuthenticated();

            var request = new HttpRequestMessage(HttpMethod.Post, "/spirit-server/AVPW1/recup_user.1.0.o_recup_user")
            {
                Content = new StringContent("{\"no_version_1\":1}", Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Referrer = new Uri($"{_httpClient.BaseAddress}/AVPW1/");
            request.Headers.Add("Origin", $"{_httpClient.BaseAddress}");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            var response = await _httpClient.SendAsync(request);

            try
            {
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<SpiritGeneUser.User>(content).Result.FoncOut1.FoncOut101;
            }
            catch
            {
                _authenticatedTime = DateTime.MinValue;
                MessageBox.Show("Problem retrieving data from SpiritGene, please retry later.");
            }
            return null;
        }

        public async Task<SpiritGeneTransactionsOutput.FoncOut101> GetTransactions(DateTime DateDebut, DateTime DateFin, string BIC, decimal MontantMin, decimal MontantMax, string Sens = "R")
        {
            await EnsureAuthenticated();

            var request = new HttpRequestMessage(HttpMethod.Post, "/spirit-server/AVPW1/list_operation.1.0.o_list_ope")
            {
                Content = new StringContent(SpiritGeneTransactionsInput.CreateTransactionBody(DateDebut, DateFin, BIC, (int)(MontantMin * 10000), (int)(MontantMax * 10000), Sens), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Referrer = new Uri($"{_httpClient.BaseAddress}/AVPW1/");
            request.Headers.Add("Origin", $"{_httpClient.BaseAddress}");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            var cacheKey = RequestCache.GenerateKey(request);

            if (!_cache.TryGet(cacheKey, out var result))
            {
                try
                {
                    var response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsStringAsync();

                    _cache.Set(cacheKey, content);
                }
                catch
                {
                    _authenticatedTime = DateTime.MinValue;
                    MessageBox.Show("Problem retrieving data from SpiritGene, please retry later.");
                }
            }

            _cache.TryGet(cacheKey, out var json);
            if (json != "")
            {
                return JsonSerializer.Deserialize<SpiritGeneTransactionsOutput.Output>(json).Result.FoncOut1.FoncOut101;
            }

            return null;
        }


        public async Task<SpiritGeneTransactionDetailOutput.GDetOpe> GetTransactionDetails(string TransactionId, string MsgId, string Sens = "R")
        {
            await EnsureAuthenticated();

            var request = new HttpRequestMessage(HttpMethod.Post, "/spirit-server/AVPW1/detail_ope_2.1.0.o_detail_ope")
            {
                Content = new StringContent(SpiritGeneTransactionDetailInput.CreateTransactionBody(TransactionId, MsgId, Sens), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Referrer = new Uri($"{_httpClient.BaseAddress}/AVPW1/");
            request.Headers.Add("Origin", $"{_httpClient.BaseAddress}");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            var cacheKey = RequestCache.GenerateKey(request);

            if (!_cache.TryGet(cacheKey, out var result))
            {
                try
                {
                    var response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsStringAsync();

                    _cache.Set(cacheKey, content);
                }
                catch
                {
                    MessageBox.Show("Problem retrieving data from SpiritGene, please retry later.");
                }
            }

            _cache.TryGet(cacheKey, out var json);
            if (json != "")
            {
                return JsonSerializer.Deserialize<SpiritGeneTransactionDetailOutput.Root>(json).Result.FoncOut1.FoncOut101.GDetOpe;
            }

            return null;
        }

        public string GetStoredCookies()
        {
            return _cookieContainer.GetCookieHeader(_httpClient.BaseAddress);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}