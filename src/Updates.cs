using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace MovieSearch
{
    internal static class Updates
    {
        internal static string Revision(bool aliases, CancellationToken token)
        {
            var datasets = aliases ? new[] { "title.ratings", "title.basics", "title.akas" }
                                   : new[] { "title.ratings", "title.basics" };
            var versions = new StringBuilder();
            foreach (string dataset in datasets)
            {
                var request = (HttpWebRequest)WebRequest.Create("https://datasets.imdbws.com/" + dataset + ".tsv.gz");
                request.Method = "HEAD"; request.Timeout = 20000;
                using (token.Register(request.Abort))
                {
                    try
                    {
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            string tag = response.Headers["ETag"] ?? response.Headers["Last-Modified"];
                            if (string.IsNullOrEmpty(tag)) return "";
                            versions.Append(dataset).Append('=').Append(tag).Append('|');
                        }
                    }
                    catch (WebException e)
                    {
                        token.ThrowIfCancellationRequested();
                        using (var response = e.Response as HttpWebResponse)
                        {
                            if (response != null && (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
                                response.StatusCode == HttpStatusCode.NotImplemented)) return "";
                        }
                        throw;
                    }
                }
            }
            return versions.ToString();
        }

        // One lease across all running windows. Record the attempt before contacting IMDb,
        // so an offline launch or an interrupted download cannot cause a retry loop.
        internal static bool Refresh(string index, DateTime now, Func<string> revision, Action build)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(index));
            FileStream lease;
            try { lease = new FileStream(index + ".check", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) { if ((e.HResult & 65535) == 32) return false; throw; }
            using (lease)
            {
                var bytes = new byte[8];
                if (lease.Read(bytes, 0, bytes.Length) == 8)
                {
                    long ticks = BitConverter.ToInt64(bytes, 0);
                    if (ticks > 0 && ticks <= now.Ticks && now.Ticks - ticks < TimeSpan.TicksPerDay) return false;
                }
                lease.Position = 0;
                bytes = BitConverter.GetBytes(now.Ticks);
                lease.Write(bytes, 0, bytes.Length); lease.SetLength(bytes.Length); lease.Flush(true);
                string version = revision();
                string stamp = index + ".revision";
                if (version.Length > 0 && File.Exists(index) && File.Exists(stamp) && File.ReadAllText(stamp) == version) return false;
                build(); // Catalog.Build publishes only a completely constructed index.
                File.WriteAllText(stamp, version);
                return true;
            }
        }
    }
}
