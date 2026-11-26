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
        public int nbSeeders { get; set; }
        public int nbLeechers { get; set; }
    }
    internal class Page
    {
        public int total { get; set; }
        public string status { get; set; }
        public Hit[] results { get; set; }
    }

    internal sealed class Client : IDisposable
    {
        private readonly HttpClient http;
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
        public void Dispose() { http.Dispose(); }
        private async Task<string> Send(string method, CancellationToken token, params string[] pairs)
        {
            using (var msg = new HttpRequestMessage(pairs.Length == 0 ? HttpMethod.Get : HttpMethod.Post, method))
            {
                if (pairs.Length > 0)
                    msg.Content = new FormUrlEncodedContent(Enumerable.Range(0, pairs.Length / 2)
                        .Select(i => new KeyValuePair<string, string>(pairs[2*i], pairs[2*i+1])));
                using (var res = await http.SendAsync(msg, token).ConfigureAwait(false))
                {
                    res.EnsureSuccessStatusCode();
                    return await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
        public async Task<Provider[]> Connect(string user, string password)
        {
            if (!string.IsNullOrEmpty(user))
            {
                if ((await Send("auth/login", CancellationToken.None, "username", user, "password", password)).Trim() == "Fails.")
                    throw new UnauthorizedAccessException();
            }
            return json.Deserialize<Provider[]>(await Send("search/plugins", CancellationToken.None));
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
        public Task Download(Hit hit)
        {
            return Send("search/downloadTorrent", CancellationToken.None, "torrentUrl", hit.fileUrl, "pluginName", hit.engineName);
        }
    }
}
