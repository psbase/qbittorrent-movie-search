using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MovieSearch
{
    internal class Provider
    {
        public string name { get; set; }
        public string fullName { get; set; }
        public bool enabled { get; set; }
        public override string ToString() { return fullName ?? name; }
    }
    internal class Hit
    {
        public string fileName { get; set; }
        public string fileUrl { get; set; }
        public string engineName { get; set; }
        public string siteUrl { get; set; }
        public long fileSize { get; set; }
        public long nbSeeders { get; set; }
        public long nbLeechers { get; set; }
    }
    internal class Page
    {
        public int total { get; set; }
        public string status { get; set; }
        public Hit[] results { get; set; }
    }

    internal sealed class ApiError : HttpRequestException
    {
        public readonly HttpStatusCode Status;
        public ApiError(string endpoint, HttpStatusCode status) : base("qBittorrent " + endpoint.Split('?')[0]
            + " (HTTP " + (int)status + " " + status + ")") { Status = status; }
    }

    internal sealed class Client : IDisposable
    {
        private readonly HttpClient http;
        private NetworkCredential credentials;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16000000 };
        public static bool IsLocal(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsLoopback &&
                (uri.Scheme == "http" || uri.Scheme == "https") && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "";
        }
        public Client(string url, HttpMessageHandler transport = null)
        {
            if (!IsLocal(url)) throw new ArgumentException("Use a local Web UI address.");
            var origin = new Uri(url.TrimEnd('/') + "/");
            http = new HttpClient(transport ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() });
            http.BaseAddress = new Uri(origin, "api/v2/");
            http.DefaultRequestHeaders.Referrer = origin;
            http.Timeout = TimeSpan.FromSeconds(20);
        }
        public void Dispose() { credentials = null; http.Dispose(); }
        private async Task<string> Send(string method, CancellationToken token, params string[] pairs)
        {
            using (var msg = new HttpRequestMessage(pairs.Length == 0 ? HttpMethod.Get : HttpMethod.Post, method))
            {
                if (pairs.Length > 0)
                    msg.Content = new FormUrlEncodedContent(Enumerable.Range(0, pairs.Length / 2)
                        .Select(i => new KeyValuePair<string, string>(pairs[2*i], pairs[2*i+1])));
                using (var res = await http.SendAsync(msg, token).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode) throw new ApiError(method, res.StatusCode);
                    return await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
        private async Task Login(NetworkCredential login)
        {
            string result = await Send("auth/login", CancellationToken.None, "username", login.UserName, "password", login.Password);
            if (result.Trim() == "Fails.") throw new UnauthorizedAccessException("Open Connection and sign in to qBittorrent again.");
        }
        public async Task<Provider[]> Connect(string user, string password)
        {
            var login = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, password);
            if (login != null) await Login(login);
            var providers = json.Deserialize<Provider[]>(await Send("search/plugins", CancellationToken.None));
            credentials = login;
            return providers;
        }
        public async Task Search(string pattern, string provider, Func<Hit[], Task> append, CancellationToken token)
        {
            // Always recover the job ID, even if Stop was clicked while the POST was in flight.
            string start = await Send("search/start", CancellationToken.None, "pattern", pattern, "category", "all", "plugins", provider);
            string id = json.Deserialize<Dictionary<string, int>>(start)["id"].ToString(CultureInfo.InvariantCulture);
            bool finished = false;
            ExceptionDispatchInfo failure = null;
            try
            {
                int offset = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page = json.Deserialize<Page>(await Send("search/results?id=" + id + "&limit=200&offset=" + offset, token));
                    token.ThrowIfCancellationRequested();
                    if (page.results.Length > 0) await append(page.results);
                    offset += page.results.Length;
                    if (offset >= page.total && page.status == "Stopped") { finished = true; break; }
                    if (offset >= page.total || page.results.Length == 0) await Task.Delay(700, token);
                }
            }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            if (!finished)
            {
                try { await Send("search/stop", CancellationToken.None, "id", id); }
                catch (Exception e) { if (failure == null) failure = ExceptionDispatchInfo.Capture(e); }
            }
            try { await Send("search/delete", CancellationToken.None, "id", id); }
            catch (Exception e) { if (failure == null) failure = ExceptionDispatchInfo.Capture(e); }
            if (failure != null) failure.Throw();
        }
        private async Task EnsureAddSession()
        {
            // A read-only check can be retried safely. Submit the actual add exactly once.
            try { await Send("app/version", CancellationToken.None); return; }
            catch (ApiError e) { if (e.Status != HttpStatusCode.Forbidden) throw; }

            var login = credentials;
            if (login == null) throw new UnauthorizedAccessException("Open Connection and sign in to qBittorrent again.");
            try { await Login(login); }
            catch (ApiError e)
            {
                if (e.Status != HttpStatusCode.Unauthorized && e.Status != HttpStatusCode.Forbidden) throw;
                credentials = null;
                throw new UnauthorizedAccessException("Open Connection and sign in to qBittorrent again.", e);
            }
            catch (UnauthorizedAccessException) { credentials = null; throw; }
        }
        public async Task Download(Hit hit)
        {
            string url = hit.fileUrl;
            Uri parsed;
            if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != "magnet" && parsed.Scheme != "http" && parsed.Scheme != "https"))
                throw new ArgumentException("This result has no usable download link.");

            await EnsureAddSession();
            string[] form = parsed.Scheme == "magnet" || string.IsNullOrEmpty(hit.engineName)
                ? new[] { "urls", url } : new[] { "urls", url, "downloader", hit.engineName };
            string result = await Send("torrents/add", CancellationToken.None, form);
            // qBittorrent 5.3 reports accepted and pending additions in JSON. Older APIs return Ok.
            if (result.Trim() == "Ok.") return;
            if (result.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                var counts = json.Deserialize<Dictionary<string, object>>(result);
                object added, pending;
                if ((counts.TryGetValue("success_count", out added) && added is int && (int)added > 0)
                    || (counts.TryGetValue("pending_count", out pending) && pending is int && (int)pending > 0)) return;
            }
            throw new InvalidOperationException("qBittorrent did not confirm the addition. Check Transfers before trying again.");
        }
    }
}
