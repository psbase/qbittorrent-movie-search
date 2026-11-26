using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace MovieSearch
{
    internal sealed class Settings
    {
        public string Language = "en", Address = "http://127.0.0.1:8080", User = "";
        public string[] Hidden = { "Votes", "Genre", "Runtime" };
        public bool GermanAliases;
        public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieSearchForQBittorrent");
        public static readonly string Index = Path.Combine(Home, "movies.zip");
        private static readonly string FileName = Path.Combine(Home, "settings.json");
        public static Settings Load()
        {
            try { return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FileName)) ?? new Settings(); }
            catch (IOException) { return new Settings(); }
            catch (ArgumentException) { return new Settings(); }
            catch (InvalidOperationException) { return new Settings(); }
        }
        public void Save()
        {
            Directory.CreateDirectory(Home);
            string tmp = Path.Combine(Home, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
            try { if (File.Exists(FileName)) File.Replace(tmp, FileName, null); else File.Move(tmp, FileName); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
    internal sealed class Strings
    {
        private readonly Dictionary<string, string> german = new Dictionary<string, string>();
        public string Language;
        public Strings(string lang)
        {
            Language = lang;
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("de.tsv")))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var fields = line.Split('\t');
                    if (fields.Length == 2) german.Add(fields[0], fields[1]);
                }
            }
        }
        public string Get(string key)
        {
            string value;
            return Language == "de" && german.TryGetValue(key, out value) ? value : key;
        }
    }
    internal static class Program
    {
        internal static string Launcher(string exe)
        {
            // Quote the Windows argument and escape it as a Lua string literal.
            var command = ("\"" + exe + "\"").Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "NAME = \"Movie Search\"\nVERSION = \"0.1\"\n\nfunction invoke()\n    qBittorrent.exec(\"" + command + "\")\nend\n";
        }
        [STAThread]
        private static void Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length == 1 && args[0] == "--install")
                {
                    Directory.CreateDirectory(Settings.Home);
                    var exe = Path.Combine(Settings.Home, "MovieSearch.exe");
                    if (!string.Equals(exe, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        File.Copy(Application.ExecutablePath, exe, true);
                    var path = Path.Combine(Settings.Home, "movie_search.lua");
                    File.WriteAllText(path, Launcher(exe), new UTF8Encoding(false));
                    MessageBox.Show("In qBittorrent, open Plugins → Manage plugins. Install the selected movie_search.lua file and enable Movie Search.", "Movie Search");
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }
                Application.Run(new Window(Settings.Load()));
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Movie Search", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
