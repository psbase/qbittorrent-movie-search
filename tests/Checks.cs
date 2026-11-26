using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MovieSearch
{
    internal static class Checks
    {
        private static int count;
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name); count++;
        }
        private static void Gzip(string dir, string name, params string[] lines)
        {
            using (var file = File.Create(Path.Combine(dir, name + ".tsv.gz")))
            using (var zip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new StreamWriter(zip, new UTF8Encoding(false)))
                foreach (var line in lines) writer.WriteLine(line);
        }
        private static void Ratings(string dir, string score)
        {
            Gzip(dir, "title.ratings", "tconst\taverageRating\tnumVotes", "tt0000001\t" + score + "\t123");
        }
        private static void Fixtures(string dir)
        {
            Gzip(dir, "title.basics", "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres",
                "tt0000001\tmovie\tFixture Moon\tFixture Moon\t0\t2026\t\\N\t99\tAdventure",
                "tt0000002\tmovie\tNo Score\tNo Score\t0\t2026\t\\N\t99\tDrama",
                "tt0000003\tmovie\tShared Title\tShared Title\t0\t2026\t\\N\t99\tDrama",
                "tt0000004\tmovie\tShared Title\tShared Title\t0\t2026\t\\N\t99\tDrama");
            Ratings(dir, "8.2");
            Gzip(dir, "title.akas", "titleId\tordering\ttitle\tregion\tlanguage\ttypes\tattributes\tisOriginalTitle",
                "tt0000001\t1\tBeispielmond\tDE\tde\t\\N\t\\N\t0");
        }
        private static void TestUpdates(string dir)
        {
            Fixtures(dir);
            string index = Path.Combine(dir, "index.zip"), version = "a";
            DateTime day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int checks = 0, builds = 0;
            Func<string> probe = () => { checks++; return version; };
            Action build = () => { builds++; Catalog.Build(index, dir, true, delegate {}, CancellationToken.None); };
            Check(Updates.Refresh(index, day, probe, build), "first run creates index automatically");
            var catalog = new Catalog(index);
            var movie = catalog.Match("Fixture.Moon.2026.2160p.WEB-DL");
            Check(movie != null && movie.Rating == 8.2 && movie.RatingUrl.EndsWith("/tt0000001/"), "title, year and rating link");
            Check(catalog.Match("Beispielmond (2026)").Id == movie.Id, "German alias");
            Check(catalog.Match("Луна / Fixture Moon (2026) UHD BDRip 2160p | 4K").Id == movie.Id,
                "localized and original title with release tags");
            Check(catalog.Match("Beispielmond / Fixture Moon (2026)").Id == movie.Id,
                "two alternate titles identify the same film");
            Check(catalog.Match("Fixture Moon / No Score (2026)") == null &&
                catalog.Match("Fixture Moon / Shared Title (2026)") == null, "conflicting or ambiguous alternatives stay unmatched");
            Check(catalog.Match("Shared Title (2026)") == null, "ambiguous titles");
            Check(catalog.Match("Fixture.Moon.S01E02.2026") == null && catalog.Match("Fixture Moon 2025") == null, "episodes and different years");
            Check(catalog.Match("No Score (2026)").RatingUrl == null, "unrated title has no clickable rating");
            Check(!Updates.Refresh(index, day.AddHours(23), probe, build) && checks == 1, "repeat launch uses cache without network");
            Check(!Updates.Refresh(index, day.AddDays(1), probe, build) && builds == 1, "unchanged revision skips download");
            version = "b"; Ratings(dir, "9.1");
            Check(Updates.Refresh(index, day.AddDays(2), probe, build) && new Catalog(index).Match("Fixture Moon 2026").Rating == 9.1, "new revision replaces index");
            using (var lease = File.Open(index + ".check", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!Updates.Refresh(index, day.AddDays(3), probe, build), "overlapping launch skips duplicate update");
            var original = File.ReadAllBytes(index);
            try { Updates.Refresh(index, day.AddDays(3), () => { throw new WebException("Offline"); }, build); throw new Exception("Expected offline"); }
            catch (WebException) { }
            Check(original.SequenceEqual(File.ReadAllBytes(index)) && !Updates.Refresh(index, day.AddDays(3).AddMinutes(1), probe, build), "offline launch preserves index and daily limit");
            string gzip = Path.Combine(dir, "title.basics.tsv.gz");
            using (var file = File.OpenWrite(gzip)) file.SetLength(file.Length - 5);
            version = "c"; bool rejected = false;
            try { Updates.Refresh(index, day.AddDays(4), probe, build); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && original.SequenceEqual(File.ReadAllBytes(index)), "truncated download preserves previous index");
            var options = new Settings();
            Check(options.Language == "en" && new Strings("de").Get("Search") == "Suchen", "English default and German translations");
            Check(Program.Launcher(@"C:\Test Path\MovieSearch.exe").Contains("qBittorrent.exec("), "Lua launcher generation");
            string opened = null;
            using (var window = new Window(options, url => opened = url))
            {
                window.BindingContext = new BindingContext();
                window.Append(new Hit { fileName = "unrated" }, null);
                window.Append(new Hit { fileName = "rated" }, movie);
                window.Grid.Sort(window.Grid.Columns["IMDb"], ListSortDirection.Descending);
                int col = window.Grid.Columns["IMDb"].Index;
                Check(window.OpenRating(0, col) && opened == movie.RatingUrl, "sorted rating opens the matching IMDb page");
                Check(!window.OpenRating(1, col) && !window.OpenRating(0, 0), "missing rating and unrelated cells do not open links");
                window.Append(new Hit { fileName = "large counts", nbSeeders = 4294967317L, nbLeechers = 4294967318L }, null);
                window.Append(new Hit { fileName = "unknown counts", nbSeeders = -1, nbLeechers = -1 }, null);
                window.Grid.Sort(window.Grid.Columns["Seeds"], ListSortDirection.Descending);
                Check((long)window.Grid.Rows[0].Cells["Seeds"].Value == 4294967317L &&
                    (long)window.Grid.Rows[0].Cells["Leechers"].Value == 4294967318L,
                    "large peer counts display and sort without narrowing");
                Check(window.Grid.Rows[window.Grid.Rows.Count - 1].Cells["Seeds"].Value == DBNull.Value &&
                    window.Grid.Rows[window.Grid.Rows.Count - 1].Cells["Leechers"].Value == DBNull.Value,
                    "unknown peer counts remain empty in the results grid");
            }
        }

        private sealed class FixtureApi : HttpMessageHandler
        {
            public readonly List<string> Calls = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken token)
            {
                string path = req.RequestUri.PathAndQuery.Replace("/api/v2/", "");
                string form = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
                Calls.Add(path + " " + form);
                string result = "";
                if (path == "auth/login") result = "Ok.";
                if (path == "search/plugins") result = "[{\"name\":\"test\",\"fullName\":\"Fixture\",\"enabled\":true}]";
                if (path == "search/start") result = "{\"id\":17}";
                if (path.StartsWith("search/results"))
                    result = "{\"status\":\"Stopped\",\"total\":2,\"results\":[{\"fileName\":\"Fixture Moon\",\"engineName\":\"test\","
                        + "\"fileSize\":4294967317,\"nbSeeders\":4294967317,\"nbLeechers\":4294967318}]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) };
            }
        }

        private static async Task TestApi()
        {
            var api = new FixtureApi();
            using (var client = new Client("http://127.0.0.1:8080", api))
            {
                var providers = await client.Connect("reader", "fixture");
                Check(providers.Length == 1 && providers[0].enabled, "provider discovery and login");
                int rows = 0;
                Hit received = null;
                await client.Search("2026 2160p", "enabled", page => { received = page[0]; rows += page.Length; return Task.FromResult(0); }, CancellationToken.None);
                Check(received != null && received.nbSeeders == 4294967317L && received.nbLeechers == 4294967318L &&
                    received.fileSize == 4294967317L, "64-bit Web API counts survive deserialization and paging");
                Check(rows == 2 && api.Calls.Any(c => c.Contains("plugins=enabled")) && api.Calls.Any(c => c.Contains("offset=1")), "enabled providers and paginated results");
                Check(api.Calls.Last() == "search/delete id=17", "finished job cleanup");
                var cts = new CancellationTokenSource(); cts.Cancel();
                try { await client.Search("fixture", "enabled", page => Task.FromResult(0), cts.Token); }
                catch (OperationCanceledException) { }
                Check(api.Calls.Any(c => c == "search/stop id=17") && api.Calls.Last() == "search/delete id=17", "cancelled job cleanup");
            }
        }
        [STAThread]
        private static int Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "movie-search-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                TestUpdates(dir);
                Task.Run((Func<Task>)TestApi).GetAwaiter().GetResult();
                Console.WriteLine(count + " checks passed."); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { Directory.Delete(dir, true); }
        }
    }
}
