using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MovieSearch
{
    internal sealed class Window : Form
    {
        private readonly Settings prefs;
        private readonly Strings strings;
        private readonly TextBox query = new TextBox();
        private readonly ComboBox providers = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly Button search = new Button { AutoSize = true };
        internal readonly DataGridView Grid = new DataGridView();
        private readonly DataTable table = new DataTable();
        private readonly MenuStrip menu = new MenuStrip();
        private readonly Label status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolTip tips = new ToolTip();
        private readonly Font linkFont;
        private Catalog catalog = new Catalog(Settings.Index);
        private Client client;
        private CancellationTokenSource cancel;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool checking;
        private string updateStage;
        private bool busy, adding, pendingClose;
        private string addStatus;
        private string stage;
        private readonly Action<string> openUrl;
        private static readonly string[] Columns = { "Name", "IMDb", "Votes", "Genre", "Runtime", "Size", "Seeds", "Leechers", "Source" };
        private static readonly Type[] Types = { typeof(string), typeof(double), typeof(int), typeof(string), typeof(int), typeof(long), typeof(long), typeof(long), typeof(string) };
        private static readonly int[] Widths = { 420, 60, 78, 145, 75, 95, 64, 72, 120 };
        private string T(string key) { return strings.Get(key); }
        private CultureInfo Culture { get { return CultureInfo.GetCultureInfo(strings.Language == "de" ? "de-DE" : "en-US"); } }

        public Window(Settings settings, Action<string> open = null)
        {
            prefs = settings;
            strings = new Strings(prefs.Language);
            openUrl = open ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
            Font = new Font("Segoe UI", 9);
            linkFont = new Font(Font, FontStyle.Underline);
            BackColor = Color.FromArgb(39, 41, 45);
            ForeColor = Color.WhiteSmoke;
            Width = 1140; Height = 520; MinimumSize = new Size(710, 320);
            StartPosition = FormStartPosition.CenterScreen;
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(7, 6, 7, 3), ColumnCount = 3, RowCount = 1 };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            query.Dock = DockStyle.Fill; query.BackColor = Color.FromArgb(47, 50, 54); query.ForeColor = ForeColor;
            search.ForeColor = Color.Black; search.Click += async (s, e) => await StartSearch();
            bar.Controls.Add(query, 0, 0); bar.Controls.Add(providers, 1, 0); bar.Controls.Add(search, 2, 0);
            AcceptButton = search;
            ConfigureGrid();
            Controls.Add(Grid); Controls.Add(bar); Controls.Add(menu); Controls.Add(status);
            MainMenuStrip = menu;
            providers.Items.Add(new Provider { name = "enabled" }); providers.SelectedIndex = 0;
            ApplyLanguage();
            Shown += async (s, e) => await CheckForUpdates();
            FormClosing += (s, e) =>
            {
                if (busy || checking || adding)
                {
                    e.Cancel = true; pendingClose = true; lifetime.Cancel();
                    if (cancel != null) cancel.Cancel();
                }
            };
            FormClosed += (s, e) => { if (client != null) client.Dispose(); lifetime.Dispose(); linkFont.Dispose(); tips.Dispose(); };
        }

        private void ConfigureGrid()
        {
            Grid.Dock = DockStyle.Fill; Grid.ReadOnly = true; Grid.AllowUserToAddRows = false; Grid.AllowUserToDeleteRows = false;
            Grid.AutoGenerateColumns = false; Grid.RowHeadersVisible = false; Grid.MultiSelect = false;
            Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            Grid.BackgroundColor = BackColor; Grid.EnableHeadersVisualStyles = false;
            Grid.GridColor = Color.FromArgb(66, 69, 74); Grid.BorderStyle = BorderStyle.None;
            Grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(55, 58, 63);
            Grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
            Grid.DefaultCellStyle.BackColor = BackColor; Grid.DefaultCellStyle.ForeColor = ForeColor;
            Grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(44, 74, 100);
            Grid.DefaultCellStyle.SelectionForeColor = Color.White; Grid.DefaultCellStyle.NullValue = "—";
            for (int i = 0; i < Columns.Length; i++)
            {
                string key = Columns[i];
                table.Columns.Add(key, Types[i]);
                var col = new DataGridViewTextBoxColumn { Name = key, DataPropertyName = key, Width = Widths[i],
                    SortMode = DataGridViewColumnSortMode.Automatic, Visible = !(prefs.Hidden ?? new string[0]).Contains(key) };
                if (i == 0) { col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; col.MinimumWidth = 180; col.Visible = true; }
                Grid.Columns.Add(col);
            }
            table.Columns.Add("Movie", typeof(Movie)); table.Columns.Add("Hit", typeof(Hit));
            Grid.DataSource = table;
            Grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.Value == null || e.Value == DBNull.Value) return;
                string key = Grid.Columns[e.ColumnIndex].Name;
                if (key == "IMDb")
                {
                    e.CellStyle.Font = linkFont; e.CellStyle.ForeColor = Color.LightSkyBlue;
                    e.Value = ((double)e.Value).ToString("0.0", Culture);
                }
                else if (key == "Size") e.Value = ((long)e.Value / 1073741824.0).ToString("0.00", Culture) + " GiB";
                else if (key == "Votes") e.Value = ((int)e.Value).ToString("N0", Culture);
                else if (key == "Runtime") e.Value = e.Value + " min";
                else return;
                e.FormattingApplied = true;
            };
            Grid.CellMouseMove += (s, e) => Grid.Cursor = RatingAt(e.RowIndex, e.ColumnIndex) == null ? Cursors.Default : Cursors.Hand;
            Grid.CellMouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenRating(e.RowIndex, e.ColumnIndex); };
            Grid.CellMouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                    Grid.CurrentCell = Grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            };
            Grid.KeyDown += (s, e) =>
            {
                var cell = Grid.CurrentCell;
                if (cell != null && e.KeyCode == Keys.Enter && OpenRating(cell.RowIndex, cell.ColumnIndex)) e.SuppressKeyPress = true;
            };
            Grid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                var movie = Row(e.RowIndex)["Movie"] as Movie;
                if (movie != null && (Grid.Columns[e.ColumnIndex].Name == "Name" || RatingAt(e.RowIndex, e.ColumnIndex) != null))
                    e.ToolTipText = movie.Title + (RatingAt(e.RowIndex, e.ColumnIndex) != null ? " · " + T("Open on IMDb") : "");
            };
        }
        private DataRow Row(int i) { return ((DataRowView)Grid.Rows[i].DataBoundItem).Row; }
        private string RatingAt(int row, int col)
        {
            if (row < 0 || col < 0 || row >= Grid.Rows.Count || Grid.Columns[col].Name != "IMDb") return null;
            var movie = Row(row)["Movie"] as Movie;
            return movie == null ? null : movie.RatingUrl;
        }
        internal bool OpenRating(int row, int col)
        {
            string url = RatingAt(row, col);
            if (url == null) return false;
            try { openUrl(url); } catch (Exception e) { Error(e); }
            return true;
        }
        private void Error(Exception e) { MessageBox.Show(this, T(e.Message), T("Movie Search"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        private void ApplyLanguage()
        {
            Text = T("Movie Search") + " · qBittorrent";
            search.Text = T("Search"); tips.SetToolTip(query, T("Search torrents"));
            var chosen = providers.SelectedIndex;
            var engines = providers.Items.Cast<Provider>().ToArray();
            engines[0].fullName = T("Enabled providers");
            providers.Items.Clear(); providers.Items.AddRange(engines); providers.SelectedIndex = Math.Max(0, chosen);
            foreach (var key in Columns) Grid.Columns[key].HeaderText = T(key);
            menu.Items.Clear();
            menu.Items.Add(T("Connection"), null, async (s, e) => await ConfigureConnection()).Enabled = !adding;
            var data = (ToolStripMenuItem)menu.Items.Add(T("IMDb data"));
            var aliases = (ToolStripMenuItem)data.DropDownItems.Add(T("Include German titles on the next update"));
            aliases.Checked = prefs.GermanAliases; aliases.CheckOnClick = true;
            aliases.CheckedChanged += (s, e) => { prefs.GermanAliases = aliases.Checked; prefs.Save(); };
            data.DropDownItems.Add(T("Import from folder"), null, async (s, e) => await Import(true));
            data.DropDownOpening += (s, e) => { data.DropDownItems[1].Enabled = !checking; };
            var columns = (ToolStripMenuItem)menu.Items.Add(T("Columns"));
            foreach (var key in Columns.Skip(1))
            {
                string k = key;
                var item = (ToolStripMenuItem)columns.DropDownItems.Add(T(k));
                item.Checked = Grid.Columns[k].Visible; item.CheckOnClick = true;
                item.CheckedChanged += (s, e) =>
                {
                    Grid.Columns[k].Visible = item.Checked;
                    prefs.Hidden = Columns.Where(n => !Grid.Columns[n].Visible).ToArray();
                    prefs.Save();
                };
            }
            var lang = (ToolStripMenuItem)menu.Items.Add(T("Language"));
            foreach (var item in new[] { new[] { "en", "English" }, new[] { "de", "Deutsch" } })
            {
                string code = item[0];
                var choice = (ToolStripMenuItem)lang.DropDownItems.Add(item[1], null, (s, e) =>
                { prefs.Language = strings.Language = code; prefs.Save(); ApplyLanguage(); });
                choice.Checked = code == strings.Language;
            }
            var old = Grid.ContextMenuStrip;
            var context = new ContextMenuStrip();
            context.Items.Add(T("Add to qBittorrent"), null, async (s, e) => await AddSelected());
            context.Opening += (s, e) => e.Cancel = adding || client == null || Grid.CurrentRow == null || (busy && cancel == null);
            Grid.ContextMenuStrip = context;
            if (old != null) old.Dispose();
            Grid.Invalidate(); Status();
        }
        private async Task AddSelected()
        {
            if (adding || client == null || Grid.CurrentRow == null || pendingClose || (busy && cancel == null)) return;
            var hit = (Hit)Row(Grid.CurrentRow.Index)["Hit"];
            adding = true; menu.Items[0].Enabled = false; addStatus = null; Status();
            try { await client.Download(hit); addStatus = "Sent to qBittorrent"; }
            catch (ApiError e)
            {
                if (!pendingClose)
                {
                    string message = e.Status == System.Net.HttpStatusCode.Forbidden
                        ? "qBittorrent refused the request (HTTP 403). Check your Web UI login under Connection."
                        : e.Message;
                    MessageBox.Show(this, T(message), T("Add to qBittorrent"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (System.Net.Http.HttpRequestException) { ShowUnconfirmedAdd(); }
            catch (OperationCanceledException) { ShowUnconfirmedAdd(); }
            catch (Exception e) { if (!pendingClose) Error(e); }
            finally
            {
                adding = false; menu.Items[0].Enabled = true; Status();
                if (pendingClose && !busy && !checking) BeginInvoke(new Action(Close));
            }
        }
        private void ShowUnconfirmedAdd()
        {
            if (!pendingClose) MessageBox.Show(this, T("qBittorrent did not confirm the addition. Check Transfers before trying again."),
                T("Add to qBittorrent"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        private void Status()
        {
            status.Text = "  " + table.Rows.Count.ToString(Culture) + " " + T("results");
            if (addStatus != null) status.Text += "   ·   " + T(addStatus);
            if (stage != null) status.Text += "   ·   " + T(stage);
            if (updateStage != null) status.Text += "   ·   " + T(updateStage);
            else if (!catalog.Exists) status.Text += "   ·   " + T("IMDb ratings pending");
        }
        private void SetBusy(bool value, string message)
        {
            busy = value; stage = message; menu.Enabled = !value; providers.Enabled = !value;
            search.Text = T(value ? "Stop" : "Search"); search.Enabled = !value || cancel != null;
            Status();
            if (!value && !checking && !adding && pendingClose) BeginInvoke(new Action(Close));
        }
        private async Task CheckForUpdates()
        {
            checking = true;
            updateStage = "Checking IMDb data"; Status();
            var token = lifetime.Token;
            bool aliases = prefs.GermanAliases;
            var progress = new Progress<string>(value => { if (checking) { updateStage = value; Status(); } });
            try
            {
                bool changed = await Task.Run(() => Updates.Refresh(Settings.Index, DateTime.UtcNow,
                    () => Updates.Revision(aliases, token), () => Catalog.Build(Settings.Index, null, aliases,
                        ((IProgress<string>)progress).Report, token)));
                if (changed && !pendingClose) await ReloadMovies();
                updateStage = null;
            }
            catch (Exception) { updateStage = "IMDb update unavailable. Will retry on a later launch."; }
            finally { checking = false; Status(); if (pendingClose && !busy && !adding) BeginInvoke(new Action(Close)); }
        }
        private async Task ReloadMovies()
        {
            var fresh = new Catalog(Settings.Index);
            catalog = fresh;
            var rows = table.Rows.Cast<DataRow>().ToArray();
            var names = rows.Select(row => (string)row["Name"]).ToArray();
            var movies = await Task.Run(() => names.Select(fresh.Match).ToArray());
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].RowState != DataRowState.Detached) PutMovie(rows[i], movies[i]);
        }
        private async Task<bool> ConfigureConnection()
        {
            using (var form = new Form { Text = T("Connection"), ClientSize = new Size(400, 200),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent })
            {
                var fields = new List<TextBox>();
                string[] labels = { "Local Web UI address", "Username", "Password" };
                for (int i = 0; i < labels.Length; i++)
                {
                    form.Controls.Add(new Label { Text = T(labels[i]), Left = 10, Top = 16 + i * 35, Width = 145 });
                    var field = new TextBox { Left = 160, Top = 12 + i * 35, Width = 228, UseSystemPasswordChar = i == 2 };
                    fields.Add(field); form.Controls.Add(field);
                }
                fields[0].Text = prefs.Address; fields[1].Text = prefs.User;
                form.Controls.Add(new Label { Text = T("Enable the Web UI in qBittorrent options. Your password is used for this session only."), Left = 10, Top = 116, Width = 375, Height = 40 });
                var ok = new Button { Text = T("Connect"), Left = 294, Top = 162, Width = 94, DialogResult = DialogResult.OK };
                form.Controls.Add(ok); form.AcceptButton = ok;
                if (form.ShowDialog(this) != DialogResult.OK) return false;
                if (!Client.IsLocal(fields[0].Text.Trim())) { MessageBox.Show(this, T("Use a local Web UI address.")); return false; }
                SetBusy(true, "Connecting"); Client next = null;
                try
                {
                    next = new Client(fields[0].Text.Trim());
                    var engines = await next.Connect(fields[1].Text, fields[2].Text);
                    prefs.Address = fields[0].Text.Trim(); prefs.User = fields[1].Text; prefs.Save();
                    if (client != null) client.Dispose();
                    client = next; next = null;
                    providers.Items.Clear(); providers.Items.Add(new Provider { name = "enabled", fullName = T("Enabled providers") });
                    foreach (var provider in engines.Where(p => p.enabled)) providers.Items.Add(provider);
                    providers.SelectedIndex = 0; return true;
                }
                catch (Exception e) { Error(e); return false; }
                finally { if (next != null) next.Dispose(); SetBusy(false, null); }
            }
        }

        internal void Append(Hit hit, Movie movie)
        {
            var row = table.NewRow();
            row["Name"] = hit.fileName ?? ""; row["Source"] = hit.engineName ?? hit.siteUrl ?? "";
            if (hit.fileSize >= 0) row["Size"] = hit.fileSize;
            if (hit.nbSeeders >= 0) row["Seeds"] = hit.nbSeeders;
            if (hit.nbLeechers >= 0) row["Leechers"] = hit.nbLeechers;
            row["Hit"] = hit; PutMovie(row, movie); table.Rows.Add(row);
        }
        private static void PutMovie(DataRow row, Movie movie)
        {
            foreach (string key in new[] { "Movie", "IMDb", "Genre", "Runtime", "Votes" }) row[key] = DBNull.Value;
            if (movie == null) return;
            row["Movie"] = movie;
            if (movie.Rating.HasValue) { row["IMDb"] = movie.Rating.Value; row["Votes"] = movie.Votes; }
            if (!string.IsNullOrEmpty(movie.Genres)) row["Genre"] = movie.Genres.Replace(",", ", ");
            if (movie.Minutes > 0) row["Runtime"] = movie.Minutes;
        }
        private async Task StartSearch()
        {
            if (busy) { if (cancel != null) cancel.Cancel(); return; }
            string pattern = query.Text.Trim();
            if (pattern.Length == 0) { query.Focus(); return; }
            if (client == null && !await ConfigureConnection()) return;
            if (pendingClose) return;
            if (providers.Items.Count < 2) { MessageBox.Show(this, T("Enable at least one search provider in qBittorrent.")); return; }
            cancel = new CancellationTokenSource();
            table.Clear(); addStatus = null; SetBusy(true, "Searching");
            try
            {
                var provider = (Provider)providers.SelectedItem;
                await client.Search(pattern, provider.name, async hits =>
                {
                    Catalog snapshot;
                    Movie[] movies;
                    do
                    {
                        snapshot = catalog;
                        movies = await Task.Run(() => hits.Select(hit => snapshot.Match(hit.fileName)).ToArray());
                    } while (snapshot != catalog);
                    for (int i = 0; i < hits.Length; i++) Append(hits[i], movies[i]);
                    Status();
                }, cancel.Token);
            }
            catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) MessageBox.Show(this, T("Connection timed out.")); }
            catch (Exception e) { Error(e); }
            finally { cancel.Dispose(); cancel = null; SetBusy(false, null); }
        }
        private async Task Import(bool local)
        {
            string source = null;
            if (local)
                using (var folder = new FolderBrowserDialog { Description = T("Choose the folder containing the IMDb .tsv.gz files.") })
                {
                    if (folder.ShowDialog(this) != DialogResult.OK) return;
                    source = folder.SelectedPath;
                }
            var answer = MessageBox.Show(this, T("Import IMDb data for personal, non-commercial use. Include German alternative titles? This requires the additional title.akas dataset."),
                T("IMDb data"), MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel) return;
            cancel = new CancellationTokenSource(); SetBusy(true, "Ratings");
            var progress = new Progress<string>(text => { if (busy) { stage = text; Status(); } });
            try
            {
                await Task.Run(() => Catalog.Build(Settings.Index, source, answer == DialogResult.Yes,
                    ((IProgress<string>)progress).Report, cancel.Token));
                prefs.GermanAliases = answer == DialogResult.Yes; prefs.Save();
                await ReloadMovies(); updateStage = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Error(e); }
            finally { cancel.Dispose(); cancel = null; SetBusy(false, null); }
        }
    }
}
