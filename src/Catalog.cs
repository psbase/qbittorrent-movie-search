using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MovieSearch
{
    internal sealed class Movie
    {
        public string Id, Title, Genres;
        public int Votes, Minutes;
        public double? Rating;
        public string RatingUrl
        {
            get { return Rating.HasValue && Regex.IsMatch(Id, @"^tt[0-9]{7,}$")
                ? "https://www.imdb.com/title/" + Id + "/" : null; }
        }
    }

    internal sealed class Catalog
    {
        private readonly string path;
        private readonly Dictionary<string, Dictionary<string, Movie>> years = new Dictionary<string, Dictionary<string, Movie>>();
        private static readonly Regex Year = new Regex(@"(?<![\p{L}\p{N}])(?:18|19|20)\d{2}(?![\p{L}\p{N}])", RegexOptions.Compiled);
        private static readonly Regex Episode = new Regex(@"(?i)(?:^|[\W_])(?:s\d{1,2}(?:e\d+)?|\d{1,2}x\d{1,2}|season[ ._-]*\d+)(?:$|[\W_])", RegexOptions.Compiled);
        private static readonly Regex ReleaseTag = new Regex(@"(?i)(?:^|[\W_])(?:2160p|1080[pi]|720p|480p|web[ ._-]?(?:dl|rip)|blu[ ._-]?ray|x26[45]|h[ ._-]?26[45])(?:$|[\W_])", RegexOptions.Compiled);
        private static readonly Regex Words = new Regex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);
        private static readonly Regex AlternateTitleSeparator = new Regex(@"\s+/\s+", RegexOptions.Compiled);
        public Catalog(string path) { this.path = path; }
        public bool Exists { get { return File.Exists(path); } }

        internal static string Key(string text)
        {
            return Words.Replace(WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant().Replace("&", " and ").Replace("’", "").Replace("'", ""), " ").Trim();
        }

        public Movie Match(string name)
        {
            if (!Exists || string.IsNullOrWhiteSpace(name) || Episode.IsMatch(name)) return null;
            var tag = ReleaseTag.Match(name);
            if (tag.Success) name = name.Substring(0, tag.Index);
            var candidates = Year.Matches(name);
            for (int i = candidates.Count - 1; i >= 0; --i)
            {
                var year = candidates[i];
                string title = name.Substring(0, year.Index);
                string fullKey = Key(title);
                if (fullKey.Length == 0) continue;
                lock (years)
                {
                    var titles = ReadYear(year.Value);
                    Movie movie;
                    if (titles.TryGetValue(fullKey, out movie)) return movie;
                    if (!AlternateTitleSeparator.IsMatch(title)) continue;

                    // Providers may supply a localized title followed by the original title.
                    // Accept only when all recognized alternatives identify the same film.
                    Movie match = null;
                    foreach (string alternative in AlternateTitleSeparator.Split(title))
                    {
                        if (!titles.TryGetValue(Key(alternative), out movie)) continue;
                        if (movie == null || (match != null && movie != match)) return null;
                        match = movie;
                    }
                    if (match != null) return match;
                }
            }
            return null;
        }

        private Dictionary<string, Movie> ReadYear(string year)
        {
            Dictionary<string, Movie> titles;
            if (years.TryGetValue(year, out titles)) return titles;
            titles = new Dictionary<string, Movie>(StringComparer.Ordinal);
            var ids = new Dictionary<string, Movie>();
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var archive = new ZipArchive(file))
            {
                var entry = archive.GetEntry(year);
                if (entry != null)
                using (var reader = new StreamReader(entry.Open()))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var p = line.Split('\t');
                        Movie movie;
                        if (p.Length == 8)
                        {
                            double rating;
                            movie = new Movie { Id = p[0], Title = p[1], Minutes = Number(p[3]), Genres = p[4],
                                Rating = double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out rating) ? (double?)rating : null,
                                Votes = Number(p[6]) };
                            ids[movie.Id] = movie;
                            Add(titles, p[1], movie);
                            Add(titles, p[2], movie);
                        }
                        else if (p.Length == 2 && ids.TryGetValue(p[0], out movie)) Add(titles, p[1], movie);
                    }
                }
            }
            if (years.Count >= 12) years.Clear();
            years.Add(year, titles);
            return titles;
        }

        private static int Number(string value) { int n; return int.TryParse(value, out n) ? n : 0; }
        private static void Add(Dictionary<string, Movie> titles, string title, Movie movie)
        {
            if (title == @"\N") return;
            var key = Key(title);
            Movie old;
            if (!titles.TryGetValue(key, out old)) titles[key] = movie;
            else if (old != movie) titles[key] = null; // Preserve ambiguity, including unrated films.
        }

        private sealed class MeasuredGzip : GZipStream
        {
            public long BytesRead;
            public MeasuredGzip(Stream input) : base(input, CompressionMode.Decompress, true) { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = base.Read(buffer, offset, count);
                BytesRead += n;
                return n;
            }
        }

        private static void Read(string source, string temp, string dataset, string header, Action<string[]> visit, CancellationToken token)
        {
            var name = dataset + ".tsv.gz";
            string path = Path.Combine(source ?? temp, name);
            if (source == null)
            {
                var request = (HttpWebRequest)WebRequest.Create("https://datasets.imdbws.com/" + name);
                request.Timeout = 30000; request.ReadWriteTimeout = 30000;
                using (var registration = token.Register(request.Abort))
                using (var response = request.GetResponse())
                using (var body = response.GetResponseStream())
                using (var file = File.Create(path))
                {
                    body.CopyTo(file);
                    if (response.ContentLength >= 0 && file.Length != response.ContentLength)
                        throw new EndOfStreamException(name);
                }
            }
            using (var stream = File.OpenRead(path))
            using (var gzip = new MeasuredGzip(stream))
            using (var reader = new StreamReader(gzip, Encoding.UTF8, true, 65536))
            {
                if (reader.ReadLine() != header) throw new InvalidDataException(name);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    visit(line.Split('\t'));
                }
                stream.Seek(-4, SeekOrigin.End);
                using (var trailer = new BinaryReader(stream, Encoding.UTF8, true))
                    if (unchecked((uint)gzip.BytesRead) != trailer.ReadUInt32()) throw new InvalidDataException(name);
            }
            if (source == null) File.Delete(path);
        }

        public static void Build(string destination, string source, bool aliases, Action<string> progress, CancellationToken token)
        {
            var parent = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(parent);
            var temp = Path.Combine(parent, "import-" + Guid.NewGuid().ToString("N"));
            using (var guard = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Directory.CreateDirectory(temp);
                var writers = new Dictionary<string, StreamWriter>();
                try
                {
                    var ratings = new Dictionary<string, string>(StringComparer.Ordinal);
                    var movieYears = new Dictionary<string, string>(StringComparer.Ordinal);
                    progress("Ratings");
                    Read(source, temp, "title.ratings", "tconst\taverageRating\tnumVotes", p => ratings[p[0]] = p[1] + "\t" + p[2], token);
                    progress("Titles");
                    Read(source, temp, "title.basics", "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres", p =>
                    {
                        if ((p[1] != "movie" && p[1] != "tvMovie") || p[5].Length != 4 || !Year.IsMatch(p[5])) return;
                        StreamWriter writer;
                        if (!writers.TryGetValue(p[5], out writer))
                            writers[p[5]] = writer = new StreamWriter(Path.Combine(temp, p[5]), false, new UTF8Encoding(false));
                        string score;
                        if (!ratings.TryGetValue(p[0], out score)) score = "\t0";
                        writer.WriteLine(string.Join("\t", p[0], p[2], p[3], p[7], p[8] == @"\N" ? "" : p[8], score, p[1]));
                        if (aliases) movieYears[p[0]] = p[5];
                    }, token);
                    if (writers.Count == 0 || ratings.Count == 0) throw new InvalidDataException("Empty IMDb dataset");
                    ratings.Clear();
                    if (aliases)
                    {
                        progress("German titles");
                        Read(source, temp, "title.akas", "titleId\tordering\ttitle\tregion\tlanguage\ttypes\tattributes\tisOriginalTitle", p =>
                        {
                            string year;
                            if ((p[3] == "DE" || p[4] == "de") && movieYears.TryGetValue(p[0], out year))
                                writers[year].WriteLine(p[0] + "\t" + p[2]);
                        }, token);
                    }
                    foreach (var writer in writers.Values) writer.Dispose();
                    writers.Clear();
                    progress("Saving index");
                    string staged = Path.Combine(temp, "index.zip");
                    using (var zip = ZipFile.Open(staged, ZipArchiveMode.Create))
                        foreach (var year in Directory.GetFiles(temp))
                            if (year != staged)
                            {
                                token.ThrowIfCancellationRequested();
                                zip.CreateEntryFromFile(year, Path.GetFileName(year), CompressionLevel.Fastest);
                            }
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(destination)) File.Replace(staged, destination, null);
                    else File.Move(staged, destination);
                }
                finally
                {
                    foreach (var writer in writers.Values) writer.Dispose();
                    Directory.Delete(temp, true);
                }
            }
        }
    }
}
