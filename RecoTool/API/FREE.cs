using RecoTool.Utils;
using RecoTool.Services.External;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Xml.Linq;

namespace RecoTool.API
{
    public static class XmlFlattener
    {
        /// <summary>
        /// Convertit un XDocument en un dictionnaire « chemin => valeur ».
        /// </summary>
        public static Dictionary<string, string> Flatten(XDocument doc)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FlattenElement(doc.Root, "", result);
            return result;
        }

        private static void FlattenElement(XElement element, string prefix,
                                            Dictionary<string, string> dict)
        {
            // Nom complet (sans le namespace) – on garde le préfixe XML s’il existe
            string localName = element.Name.LocalName;
            string currentPath = string.IsNullOrEmpty(prefix)
                                    ? localName
                                    : $"{prefix}/{localName}";

            // Si l’élément possède des attributs qui nous intéressent (par ex. Ccy),
            // on les ajoute comme sous‑chemin.
            foreach (var attr in element.Attributes())
            {
                // On ignore les attributs de namespace (xmlns)
                if (attr.IsNamespaceDeclaration) continue;

                string attrPath = $"{currentPath}/@{attr.Name.LocalName}";
                dict[attrPath] = attr.Value.Trim();
            }

            // Si l’élément possède du texte (et pas d’enfants texte seulement),
            // on le considère comme valeur.
            if (!element.HasElements)
            {
                string value = element.Value?.Trim();
                if (!string.IsNullOrEmpty(value))
                    dict[currentPath] = value;
            }

            // Recurse sur les enfants
            var grouped = element.Elements()
                                 .GroupBy(e => e.Name.LocalName); // groupe les répétitions

            foreach (var g in grouped)
            {
                if (g.Count() == 1)
                {
                    // Pas de tableau → on garde le même chemin
                    FlattenElement(g.First(), currentPath, dict);
                }
                else
                {
                    // Plusieurs éléments de même nom → on indexe
                    int idx = 0;
                    foreach (var child in g)
                    {
                        string indexedPath = $"{currentPath}/{g.Key}[{idx}]";
                        FlattenElement(child, indexedPath, dict);
                        idx++;
                    }
                }
            }
        }
    }

    public class Free : IFreeApiClient, IDisposable
    {
        private const int AUTH_VALIDITY_MINUTES = 60; // 1 hour
        private const string BASE_URL = "https://free.group.echonet";

        private HttpClient _httpClient; // Not readonly to allow recreation
        private readonly CookieContainer _cookieContainer;
        private HttpClientHandler _handler; // SECURE: keep reference for recreation

        // -----------------------------------------------------------------
        // 1️⃣  Authentication state
        // -----------------------------------------------------------------
        private DateTime _authenticatedTime = DateTime.MinValue;   // “never”
        private Task<bool>? _authTask;                           // currently‑running login
        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1); // SECURE: serialize auth attempts

        public bool IsAuthenticated => _authenticatedTime != DateTime.MinValue;


        public Free()
        {
            _cookieContainer = new CookieContainer();
            _handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = _cookieContainer
            };

            _httpClient = new HttpClient(_handler)
            {
                BaseAddress = new Uri(BASE_URL),
                Timeout = TimeSpan.FromSeconds(120)
            };

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0");
        }

        /// <summary>
        /// SECURE: Recreate HttpClient to recover from transient failures (WebSocket drop, connection issues)
        /// Preserves cookies so we don't lose authentication.
        /// </summary>
        private void RecreateHttpClient()
        {
            try { _httpClient?.Dispose(); } catch { }
            try { _handler?.Dispose(); } catch { }

            _handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = _cookieContainer // Preserve cookies!
            };

            var newClient = new HttpClient(_handler)
            {
                BaseAddress = new Uri(BASE_URL),
                Timeout = TimeSpan.FromSeconds(120)
            };
            newClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0");

            // Thread-safe swap
            System.Threading.Volatile.Write(ref _httpClient, newClient);
            System.Diagnostics.Debug.WriteLine("[Free] HttpClient recreated (cookies preserved)");
        }

        private async Task EnsureAuthenticatedAsync()
        {
            // If we already have a fresh cookie – nothing to do.
            if (!IsAuthenticationStale())
                return;

            // SECURE: Use lock to serialize auth attempts and avoid race condition
            await _authLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Another thread might have refreshed the cookie while we were waiting.
                if (!IsAuthenticationStale())
                    return;

                // -----------------------------------------------------------------
                // 2b️⃣  If a login is already running we simply await it.
                // -----------------------------------------------------------------
                if (_authTask != null && !_authTask.IsCompleted)
                {
                    bool success = await _authTask.ConfigureAwait(false);   // will throw if the login failed
                    if (!success)
                        throw new InvalidOperationException("Authentication was cancelled or timed‑out.");
                    // _authenticatedTime is already set by AuthenticateAsync() below.
                    return;
                }

                // -----------------------------------------------------------------
                // 2c️⃣  No login in progress → start a new one.
                // -----------------------------------------------------------------
                _authTask = RunAuthenticationAsync();   // returns Task<bool>
                bool ok = await _authTask.ConfigureAwait(false);
                if (!ok)
                    throw new InvalidOperationException("Authentication was cancelled or timed‑out.");
            }
            finally
            {
                _authLock.Release();
            }
        }

        // -----------------------------------------------------------------
        // 3️⃣  The *real* authentication routine – runs once at a time.
        // -----------------------------------------------------------------
        private async Task<bool> RunAuthenticationAsync()
        {
            var freeAuth = new FreeAuth(_cookieContainer);
            try
            {
                await freeAuth.AuthenticateAsync();   // this method now blocks until the modal dialog finishes
                _authenticatedTime = DateTime.UtcNow; // success → remember the moment
                return true;
            }
            catch (Exception ex)
            {
                // Propagate the exception to the caller (it will be observed in EnsureAuthenticatedAsync)
                System.Windows.MessageBox.Show($"Authentification échouée : {ex.Message}",
                                "Free – Authentification", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                freeAuth.Dispose();
            }
        }

        // -----------------------------------------------------------------
        // 4️⃣  Helper – decides whether we need to re‑login.
        // -----------------------------------------------------------------
        private bool IsAuthenticationStale()
        {
            if (_authenticatedTime == DateTime.MinValue) return true;
            return (DateTime.UtcNow - _authenticatedTime).TotalMinutes >= AUTH_VALIDITY_MINUTES;
        }


        public async Task<string?> SearchAsync(DateTime startDate, string flowTrn, string codeSector)
        {
            // --------------------------------------------------------------
            // 1️⃣  Acquire the *search* lock – only one thread may be here.
            // --------------------------------------------------------------
            try
            {
                // ----------------------------------------------------------
                // 2️⃣  Try the “Expression” endpoint (payload extraction)
                // ----------------------------------------------------------
                string? payload = await SearchMessageAsync(codeSector, startDate, flowTrn: flowTrn)
                                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(payload))
                    return payload;                     // payload (Ustrd) found – stop here

                payload = await SearchMessageAsync(null, startDate, freeText: flowTrn)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(payload))
                    return payload;                     // payload (Ustrd) found – stop here

                return null;

                char[] leadingChars = new[]
                    {
                        '\r',        // carriage return
                        '\n',        // line feed
                        '\t',        // tabulation
                        '\0',        // null
                        '\uFEFF',    // BOM
                        '\u200B',    // zero‑width space
                        ' '          // espace classique
                    };

                // ----------------------------------------------------------
                // 3️⃣  Fallback – Search with format "MT"
                // ----------------------------------------------------------
                var mtResult = await Search(
                    formatMessage: "MT",
                    dateDebut: startDate,
                    codeService: codeSector,
                    referenceMessage: flowTrn)
                    .ConfigureAwait(false);

                if (mtResult?.SearchDetails?.TexteMessage?.TexteExportMT != null)
                    return mtResult.SearchDetails.TexteMessage.TexteExportMT.TrimStart(leadingChars);

                // ----------------------------------------------------------
                // 4️⃣  Final fallback – Search with format "MX"
                // ----------------------------------------------------------
                var mxResult = await Search(
                    formatMessage: "MX",
                    dateDebut: startDate,
                    codeService: codeSector,
                    referenceMessage: flowTrn)
                    .ConfigureAwait(false);

                if (mxResult?.SearchDetails?.TexteMessage?.TexteExportMX != null)
                    return mxResult.SearchDetails.TexteMessage.TexteExportMX.TrimStart(leadingChars);

                // ----------------------------------------------------------
                // 5️⃣  Nothing found
                // ----------------------------------------------------------
                return null;
            }
            finally
            {
            }
        }

        /// <summary>
        /// Authentification utilisant une Smartcard Windows et récupération automatique du cookie.
        /// </summary>
        public async Task<bool> AuthenticateAsync()
        {
            var free = new FreeAuth(_cookieContainer);
            var result = await free.AuthenticateAsync();           // may throw on failure
            if (result)
                _authenticatedTime = DateTime.UtcNow;    // success → remember time
            return result;
        }


        public async Task<FreeSearchItems.Message> Search(
                                                            string formatMessage,
                                                            DateTime dateDebut,
                                                            string codeService,
                                                            string referenceMessage)
        {
            await EnsureAuthenticatedAsync();

            // -----------------------------------------------------------------
            // 1️⃣  Build the dictionary – exactly the JSON you posted
            // -----------------------------------------------------------------
            var parameters = new Dictionary<string, object?>
            {
                // ----- variables kept from the original signature -----
                { "formatMessage",    formatMessage },          // e.g. "MT"
                { "dateDebut",        dateDebut.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) },
                { "codeService",      codeService },            // e.g. "19190"
                { "referenceMessage", referenceMessage },       // empty string in the example

                // ----- fixed constants (match the sample JSON) -----
                { "sens",                     "E" },
                { "intervalleDate",           "1jour" },
                { "intervalleHeure",          "P" },
                { "dateFin",                  dateDebut.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) },
                { "categorieMessage",         "CP" },
                { "typeMessage",              "" },
                { "operateurSwiftnet",        "CP" },
                { "codeApplication",          "" },
                { "typeReseau",               "*" },

                { "rechercheTxt1Champ",       "" },
                { "rechercheTxt2Champ",       "" },

                { "menuBicOperateurEmetteur","st" },
                { "identifiantBCodeEmetteur", "" },

                { "menuBicOperateurRecepteur","st" },
                { "identifiantBCodeRecepteur", "" },

                { "mbawReceiverIdmbp",        "" },

                // the JSON uses the exact name « champSw103 »
                { "champSw103",               "" },

                { "referenceMurImr",          "" },
                { "operateurTRN",             "st" },

                { "menuMontant",              "eq" },
                { "montantMin",               "" },

                { "menuDevise",               "eq" },
                { "codeDevise",               "" },

                { "menuDateDeValeur",         "eq" },

                // ----- arrays – they will be emitted as JSON arrays -----
                { "flowStatus", new[]
                    {
                        "filtreAEmettre","filtreDoublon","filtreAckShine","filtreAckFrontal",
                        "filtreAckReseau","filtreAckOlaf","filtreEnCours","filtreTransmis",
                        "filtreNackSibes","filtreNackShine","filtreNackFrontal",
                        "filtreNackReseau","filtreNackOlaf"
                    }
                },

                { "flowAntifraud", new[]
                    {
                        "filtreBloquantOlaf","filtreByPasseOlaf",
                        "filtreNonFiltreOlaf","filtreAPosterioriOlaf"
                    }
                },

                { "flowSanctions", new[]
                    {
                        "filtreBloquant","filtreByPasse","filtreNonFiltre",
                        "filtreNonBloquant","filtreAPosteriori"
                    }
                },

                // ----- null values required by the sample -----
                { "connector",      null },
                { "dateValeurMin",  null }
            };

            // -----------------------------------------------------------------
            // 2️⃣  Serialise the dictionary to a JSON string
            // -----------------------------------------------------------------
            string payload = PayloadGenerator.GeneratePayload(parameters);

            // -----------------------------------------------------------------
            // 3️⃣  Build the HTTP request (content‑type = application/json)
            // -----------------------------------------------------------------
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/free-ombrelle/messages/search")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            // -----------------------------------------------------------------
            // 4️⃣  Headers (unchanged from your original code)
            // -----------------------------------------------------------------
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Referrer = new Uri($"{_httpClient.BaseAddress}/");
            request.Headers.Add("Origin", $"{_httpClient.BaseAddress}");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("X-XSRF-TOKEN", GetXSRF());

            // -----------------------------------------------------------------
            // 5️⃣  Send request & handle response (original logic)
            // -----------------------------------------------------------------
            var response = await _httpClient.SendAsync(request);
            try
            {
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var liste = JsonSerializer.Deserialize<FreeSearchItems.Search>(content).Liste.FirstOrDefault();
                if (liste != null)  
                    liste.SearchDetails = await GetSearchDetails(liste);

                    return liste;
            }
            catch
            {
                //_authenticatedTime = DateTime.MinValue;
            }

            return null;
        }

        public static string BuildPayload(FreeSearchItems.Message detail)
        {
            if (detail == null) throw new ArgumentNullException(nameof(detail));

            // La sérialisation directes de l’objet donne le même arbre que l’exemple,
            // parce que toutes les propriétés sont déjà présentes dans le modèle.
            return JsonSerializer.Serialize(detail);
        }


        public async Task<FreeSearchDetails.Root> GetSearchDetails(FreeSearchItems.Message fullDetails)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/free-ombrelle/messages")
            {
                Content = new StringContent(BuildPayload(fullDetails), Encoding.UTF8, "application/json")
        };

            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "fr");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Referrer = new Uri($"{_httpClient.BaseAddress}/");
            request.Headers.Add("Origin", $"{_httpClient.BaseAddress}");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            request.Headers.Add("X-XSRF-TOKEN", GetXSRF());

            var response = await _httpClient.SendAsync(request);

            try
            {
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<FreeSearchDetails.Root>(content?.Replace("^~", ""));
            }
            catch
            {
                return null;
            }

        }


        private static ExpandoObject BuildPayload(
            DateTime startDate,
            DateTime effectiveEnd,
            string direction,
            string? codeSector,
            string? freeText,
            string? flowTrn)
        {
            // ---------- 1️⃣ dates + direction ----------
            dynamic startOperand = new ExpandoObject();
            startOperand.type = "Operand";
            startOperand.name = "flow_startDate";
            startOperand.@operator = 2;
            startOperand.value = startDate
                .ToString("yyyy-MM-dd'T'00:00:00.fff'Z'", CultureInfo.InvariantCulture);

            dynamic endOperand = new ExpandoObject();
            endOperand.type = "Operand";
            endOperand.name = "flow_endDate";
            endOperand.@operator = 1;
            endOperand.value = effectiveEnd
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            dynamic datesExpr = new ExpandoObject();
            datesExpr.type = "Expression";
            datesExpr.leftOperand = startOperand;
            datesExpr.@operator = 3;
            datesExpr.rightOperand = endOperand;

            dynamic directionOperand = new ExpandoObject();
            directionOperand.type = "Operand";
            directionOperand.name = "flow_direction";
            directionOperand.@operator = 0;
            directionOperand.value = direction;

            dynamic baseExpr = new ExpandoObject();
            baseExpr.type = "Expression";
            baseExpr.leftOperand = datesExpr;
            baseExpr.@operator = 3;
            baseExpr.rightOperand = directionOperand;

            // ---------- 2️⃣ sector (optional) ----------
            dynamic leftSide = baseExpr; // default without sector

            if (!string.IsNullOrWhiteSpace(codeSector))
            {
                dynamic sectorOperand = new ExpandoObject();
                sectorOperand.type = "Operand";
                sectorOperand.name = "flow_sectorCode";
                sectorOperand.@operator = 9;
                sectorOperand.value = codeSector;

                leftSide = new ExpandoObject();
                leftSide.type = "Expression";
                leftSide.leftOperand = baseExpr;
                leftSide.@operator = 3;
                leftSide.rightOperand = sectorOperand;
            }

            // ---------- 3️⃣ optional rightOperand ----------
            var optionalRight = BuildOptionalRightOperand(freeText, flowTrn);

            // ---------- 4️⃣ payload ----------
            dynamic payload = new ExpandoObject();
            payload.type = "Expression";
            payload.leftOperand = leftSide;
            payload.@operator = 3;
            if (optionalRight != null)
                payload.rightOperand = optionalRight;

            return payload;
        }


        #region Nouvelle méthode – SearchMessage
        /// <summary>
        /// Recherche de messages via l’endpoint /api/messages en construisant dynamiquement le payload « Expression ».
        /// </summary>
        /// <param name="codeSector">Code secteur (ex. "76860")</param>
        /// <param name="startDate">Date de début (heure 00:00)</param>
        /// <param name="endDate">
        /// Date de fin (heure 23:59 59). Si null, sera calculée : startDate + 1 jour – 1 minute.
        /// </param>
        /// <param name="direction">Direction du flux (« INCOMING » ou « OUTGOING »). Valeur par défaut : INCOMING.</param>
        /// <param name = "freeText" > Valeur libre(ex.IPA…, peut être null).</param>
        /// <param name="flowTrn">Identifiant TRN (peut être null).</param>
        /// <returns>Le texte extrait du message ou <c>null</c> si aucun résultat.</returns>
        public async Task<string?> SearchMessageAsync(
        string? codeSector,
        DateTime startDate,
        string? freeText = null,
        string? flowTrn = null)
        {
            await EnsureAuthenticatedAsync();

            // SECURE: Retry logic for transient failures
            int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                string direction = "INCOMING";

                // --------------------------------------------------------------
                // 1️⃣  Calcul de la date de fin si elle n’est pas fournie
                // --------------------------------------------------------------
                var effectiveEnd = startDate.Date.AddDays(1).AddMinutes(-1); // 23:59 du même jour

                // --------------------------------------------------------------
                // 2️⃣  Construction du payload dynamique
                // --------------------------------------------------------------
                var payloadObject = BuildPayload(startDate, effectiveEnd, direction, codeSector, freeText, flowTrn);

                string payload = JsonSerializer.Serialize(payloadObject, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null,               // conserve exactement les noms JSON attendus
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                    WriteIndented = false
                });

                // --------------------------------------------------------------
                // 3️⃣  Création de la requête HTTP
                // --------------------------------------------------------------
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "/api/messages?pageNumber=0&pageSize=10&sortDirection=desc&sortedBy=receptionDateTime")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                // En-têtes généraux (à adapter à votre contexte)
                request.Headers.Add("Accept", "*/*");
                request.Headers.Add("Accept-Language", "fr");
                request.Headers.Add("Connection", "keep-alive");
                request.Headers.Referrer = new Uri($"{_httpClient.BaseAddress}/");
                request.Headers.Add("Origin", $"{_httpClient.BaseAddress}");
                request.Headers.Add("Sec-Fetch-Dest", "empty");
                request.Headers.Add("Sec-Fetch-Mode", "cors");
                request.Headers.Add("Sec-Fetch-Site", "same-origin");
                request.Headers.Add("X-XSRF-TOKEN", GetXSRF());
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                // --------------------------------------------------------------
                // 4️⃣  Envoi et traitement de la réponse
                // --------------------------------------------------------------
                try
                {
                    var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                    
                    // SECURE: Handle 401/403 as auth errors - don't recreate client, let auth flow handle
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Free] Auth error {(int)response.StatusCode}, NOT resetting auth (tokens may still be valid)");
                        // Don't reset _authenticatedTime - the cookies may still be valid, this could be a server-side issue
                        return null;
                    }
                    
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // Désérialisation minimale – adapté à votre modèle de réponse
                    var root = JsonSerializer.Deserialize<MessageSearchRoot>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (root?.Content == null || root.Content.Count == 0)
                        return null;

                    var first = root.Content[0];
                    var rawPayload = first.Payload ?? string.Empty;

                    // Suppression éventuelle de caractères de contrôle précédant le XML
                    var startIdx = rawPayload.IndexOf("<?", StringComparison.Ordinal);
                    var cleaned = startIdx > 0 ? rawPayload.Substring(startIdx) : rawPayload;

                    return cleaned;

                    // Tentative d’analyse XML (déjà fournie dans votre implémentation)
                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Parse(cleaned, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                        var ustrd = ExtractUstrdAndEndToEndId(doc);
                        if (!string.IsNullOrWhiteSpace(ustrd)) return ustrd;

                        var instrInf = ExtractInstrInf(doc);
                        if (!string.IsNullOrWhiteSpace(instrInf)) return instrInf;

                        return RenderKeyValue(doc);
                    }
                    catch
                    {
                        // Si le XML ne peut pas être parsé, on renvoie le payload brut nettoyé.
                        return cleaned;
                    }
                }
                catch (HttpRequestException ex) when (IsTransientError(ex) && attempt < maxAttempts)
                {
                    // SECURE: Transient error (timeout, WebSocket drop, network) - recreate client and retry
                    System.Diagnostics.Debug.WriteLine($"[Free] Transient error on attempt {attempt}: {ex.Message} - recreating HttpClient");
                    RecreateHttpClient();
                    await Task.Delay(500).ConfigureAwait(false); // Small delay before retry
                    continue;
                }
                catch (TaskCanceledException ex) when (attempt < maxAttempts)
                {
                    // SECURE: Timeout - recreate client and retry
                    System.Diagnostics.Debug.WriteLine($"[Free] Timeout on attempt {attempt} - recreating HttpClient");
                    RecreateHttpClient();
                    await Task.Delay(500).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    // SECURE: Other errors - log and return null, but DON'T reset auth
                    System.Diagnostics.Debug.WriteLine($"[Free] Error: {ex.Message}");
                    return null;
                }
            }
            
            return null;
        }

        /// <summary>
        /// SECURE: Determine if an error is transient (network, timeout, WebSocket) vs permanent.
        /// </summary>
        private static bool IsTransientError(HttpRequestException ex)
        {
            // Common transient error patterns
            var message = ex.Message?.ToLowerInvariant() ?? "";
            return message.Contains("timeout") ||
                   message.Contains("connection") ||
                   message.Contains("network") ||
                   message.Contains("websocket") ||
                   message.Contains("socket") ||
                   message.Contains("reset") ||
                   message.Contains("closed") ||
                   ex.InnerException is System.Net.Sockets.SocketException ||
                   ex.InnerException is IOException;
        }

        /// <summary>
        /// Construit le <c>rightOperand</c> final du payload uniquement quand
        /// <paramref name="freeText"/> ou <paramref name="flowTrn"/> sont fournis.
        /// </summary>
        private static object? BuildOptionalRightOperand(string? freeText, string? flowTrn)
        {
            // Aucun paramètre optionnel → on retourne <c>null</c> (le sérialiseur l’ignore)
            if (string.IsNullOrWhiteSpace(freeText) && string.IsNullOrWhiteSpace(flowTrn))
                return null;

            // On crée une chaîne d’« AND » entre les éventuels opérandes
            object operand(string name, string value, int ope = 9) => new
            {
                type = "Operand",
                name,
                @operator = ope,   // 9 > Equal, 12 > Contains
                value
            };

            // Commence par le premier opérande présent
            object? current = null;

            if (!string.IsNullOrWhiteSpace(freeText))
                current = operand("flow_freetext", freeText, 12);

            if (!string.IsNullOrWhiteSpace(flowTrn))
            {
                var trnOperand = operand("flow_trn", flowTrn);
                current = current == null
                    ? trnOperand
                    : new
                    {
                        type = "Expression",
                        leftOperand = current,
                        @operator = 3,                 // AND
                    rightOperand = trnOperand
                    };
            }

            return current;
        }
        #endregion


        /// <summary>
        /// Retourne le texte de l’élément <c>Ustrd</c> et, s’il existe,
        /// celui de l’élément <c>EndToEndId</c>.
        /// </summary>
        /// <param name="doc">Le document XML déjà chargé dans un <c>XDocument</c>.</param>
        /// <returns>
        /// Un tuple contenant :
        ///   • <c>Ustrd</c> : valeur (ou <c>null</c> si l’élément est absent) ;
        ///   • <c>EndToEndId</c> : valeur (ou <c>null</c> si l’élément est absent).
        /// </returns>
        public static string ExtractUstrdAndEndToEndId(XDocument doc)
        {
            // Recherche du premier élément <Ustrd> (insensible à la casse)
            var ustrdElt = doc
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName,
                                                    "Ustrd",
                                                    StringComparison.InvariantCultureIgnoreCase));

            // Recherche du premier élément <EndToEndId> (insensible à la casse)
            var endToEndElt = doc
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName,
                                                    "EndToEndId",
                                                    StringComparison.InvariantCultureIgnoreCase));

            // Nettoyage des valeurs éventuelles
            string? ustrd = ustrdElt?.Value.Trim();
            string? endToEndId = endToEndElt?.Value.Trim();

            return String.Concat(ustrd," ", endToEndId);
        }

        private static string ExtractInstrInf(XDocument doc)
        {
            // Tous les éléments <InstrInf> quel que soit leur namespace / profondeur
            var instrInfNodes = doc
                .Descendants()
                .Where(e => string.Equals(e.Name.LocalName,
                                          "InstrInf",
                                          StringComparison.InvariantCultureIgnoreCase));

            if (!instrInfNodes.Any())
                return null;

            // Nettoyage (trim + collapse des espaces multiples)
            var values = instrInfNodes
                       .Select(e => Regex.Replace(e.Value.Trim(), @"\s+", " "));

            // Vous pouvez changer le séparateur selon vos besoins (ex. " | " ou "\n")
            return string.Join(" ", values);
        }

        #region Fallback – key/value rendering

        private static string RenderKeyValue(XDocument doc)
        {
            var flat = XmlFlattener.Flatten(doc);

            return string.Join(
                Environment.NewLine,
                flat.OrderBy(k => k.Key)
                    .Select(k => $"{k.Key} = {k.Value}")
            );
        }
        #endregion

        #region DTOs for the /api/messages response (only the fields we need)
        public class MessageSearchRoot
        {
            [JsonPropertyName("content")]
            public List<MessageSearchItem> Content { get; set; } = new List<MessageSearchItem>();
        }

        public class MessageSearchItem
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("payload")]
            public string? Payload { get; set; }

            // you can add other properties (receptionDateTime, amount, …) as required
        }
        #endregion

        private string GetXSRF()
        {
            var xsrfCookie = _cookieContainer.GetCookies(new Uri(BASE_URL)).Cast<Cookie>().FirstOrDefault(x => x.Name.Contains("XSRF"));
            return xsrfCookie?.Value ?? "";
        }

        public string GetStoredCookies()
        {
            return _cookieContainer.GetCookieHeader(_httpClient.BaseAddress);
        }

        public void Dispose()
        {
            try { _httpClient?.Dispose(); } catch { }
            try { _handler?.Dispose(); } catch { }
            try { _authLock?.Dispose(); } catch { }
        }
    }
}