using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MovieSearch
{
    // Run only in the network-isolated CI container namespace with its disposable profile.
    internal static class LiveAdd
    {
        private static async Task Run(string password)
        {
            var address = new Uri("http://127.0.0.1:8080/");
            var cookies = new CookieContainer();
            using (var client = new Client(address.AbsoluteUri, new HttpClientHandler { UseProxy = false, CookieContainer = cookies }))
            using (var inspect = new HttpClient(new HttpClientHandler { UseProxy = false, CookieContainer = cookies }))
            {
                inspect.BaseAddress = new Uri(address, "api/v2/");
                inspect.DefaultRequestHeaders.Referrer = address;
                await client.Connect("admin", password);
                string version = await inspect.GetStringAsync("app/version");
                if (version.Trim() != "v5.3.0") throw new Exception("Unexpected server " + version);
                using (var response = await inspect.PostAsync("auth/logout", new StringContent("")))
                    response.EnsureSuccessStatusCode();
                using (var response = await inspect.GetAsync("app/version"))
                    if (response.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Expected HTTP 403 after logout.");

                const string hash = "0123456789abcdef0123456789abcdef01234567";
                await client.Download(new Hit { fileUrl = "magnet:?xt=urn:btih:" + hash + "&dn=MovieSearchFixture" });
                var json = new JavaScriptSerializer();
                for (int i = 0; i < 20; i++)
                {
                    var rows = json.Deserialize<Dictionary<string, object>[]>(await inspect.GetStringAsync("torrents/info?hashes=" + hash));
                    if (rows.Length == 1 && string.Equals(rows[0]["hash"] as string, hash, StringComparison.OrdinalIgnoreCase)) return;
                    await Task.Delay(250);
                }
                throw new Exception("The test magnet did not appear in Transfers.");
            }
        }

        public static int Main()
        {
            try
            {
                Run(Console.ReadLine()).GetAwaiter().GetResult();
                Console.WriteLine("PASS qBittorrent 5.3.0 returned 403 after logout, renewed the session, and listed the test magnet.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
