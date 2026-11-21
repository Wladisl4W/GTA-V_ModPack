using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ModPack.PedImporter
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--worker")
            {
                // Pipe writers must not change console code pages: this is a WinExe without a console.
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
                Console.WriteLine("Диагностика: рабочий процесс запущен");
                return AutomationWorker.Run(args[1]);
            }
            using (var instance = new Mutex(false, "Local\\ModPackPedImporter"))
            {
                bool acquired;
                try { acquired = instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { MessageBox.Show("PedImporter уже запущен."); return 0; }
                try
                {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ImportForm());
                }
                finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
    }

    internal sealed class ImportForm : Form
    {
        private readonly TextBox modelsPath = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox gamePath = new TextBox { Dock = DockStyle.Fill };
        private readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private List<PedModel> models = new List<PedModel>();
        private readonly TextBox progress = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button import = new Button { Text = "Зарегистрировать и Rebuild", AutoSize = true };
        private readonly Button inspect = new Button { Text = "Проверить редактор", AutoSize = true };
        private readonly Button stop = new Button { Text = "Остановить", AutoSize = true, Enabled = false };
        private readonly Button editor = new Button { Text = "Открыть AddonPeds", AutoSize = true };
        private readonly Button favorites = new Button { Text = "Добавить выбранных", AutoSize = true };
        private readonly RadioButton favoritesMode = new RadioButton { Text = "Избранное Menyoo", AutoSize = true, Checked = true };
        private readonly RadioButton fullMode = new RadioButton { Text = "Полный импорт", AutoSize = true };
        private CancellationTokenSource cancellation;
        public ImportForm()
        {
            Text = "ModPack - Ped Importer"; MinimumSize = new Size(760, 450); Size = new Size(900, 600);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            modes.Controls.AddRange(new Control[] { favoritesMode, fullMode });
            layout.Controls.Add(modes, 0, 0);
            layout.Controls.Add(PathRow("Папка моделей", modelsPath, true), 0, 1);
            layout.Controls.Add(PathRow("Папка GTA V", gamePath, false), 0, 2);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Selected", HeaderText = "Выбрать", FillWeight = 40 });
            foreach (string field in new[] { "Name", "Files", "Streamed", "Status", "FavoriteStatus" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = field,
                    HeaderText = field == "Name" ? "Модель" : field == "Files" ? "Файлов" :
                        field == "Status" ? "Файлы" : field == "FavoriteStatus" ? "Избранное" : field,
                    ReadOnly = true });
            layout.Controls.Add(grid, 0, 3);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            var scan = new Button { Text = "Проверить модели", AutoSize = true };
            scan.Click += (s,e) => Run(() => { models = Catalog.Scan(modelsPath.Text); RefreshFavoriteStatuses(); grid.DataSource = models; SavePaths(); });
            editor.Click += (s,e) => Run(() => { string exe = Path.Combine(gamePath.Text, "AddonPeds Editor.exe"); Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gamePath.Text, UseShellExecute = true }); });
            favorites.Click += (s,e) => Run(() => {
                grid.EndEdit();
                EnsureGameClosed();
                var selected = models.Where(m => m.Selected && m.Valid).ToList();
                if (selected.Count == 0) throw new InvalidOperationException("Сначала выберите исправные модели.");
                RefreshFavoriteStatuses();
                int already = selected.Count(m => m.FavoriteStatus == "Уже в избранном");
                int add = selected.Count - already;
                if (add == 0) { MessageBox.Show("Все выбранные педы уже в избранном.", Text); return; }
                if (MessageBox.Show("Будет добавлено: " + add + ". Уже в избранном: " + already +
                    ".\n\nФайлы педов должны быть установлены в OpenIV, а записи AddonPeds уже созданы. Продолжить?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                int count = Catalog.AddFavorites(gamePath.Text, models);
                RefreshFavoriteStatuses();
                SavePaths();
                MessageBox.Show("Добавлено: " + count + ". Пропущено как существующие: " + already +
                    ".\nПроверьте педов в Menyoo после запуска GTA.", Text);
            });
            import.Click += async (s,e) => await Execute(false);
            inspect.Click += async (s,e) => await Execute(true);
            stop.Click += (s,e) => cancellation?.Cancel();
            actions.Controls.AddRange(new Control[] { scan, editor, inspect, import, favorites, stop });
            actions.AutoScroll = true;
            favoritesMode.CheckedChanged += (s,e) => UpdateMode();
            fullMode.CheckedChanged += (s,e) => UpdateMode();
            UpdateMode();
            layout.Controls.Add(actions, 0, 4); layout.Controls.Add(progress, 0, 5); Controls.Add(layout);
            FormClosing += (s,e) => { if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; } };
            Run(LoadPaths);
        }
        private void UpdateMode()
        {
            bool full = fullMode.Checked;
            favorites.Visible = !full;
            editor.Visible = full;
            inspect.Visible = full;
            import.Visible = full;
            stop.Visible = full;
        }
        private void RefreshFavoriteStatuses()
        {
            if (models.Count == 0) return;
            if (string.IsNullOrWhiteSpace(gamePath.Text))
            {
                foreach (var model in models)
                    model.FavoriteStatus = model.Valid ? "Не проверено" : "Недоступен";
                grid.Refresh();
                return;
            }
            Catalog.UpdateFavoriteStatuses(gamePath.Text, models);
            grid.Refresh();
        }
        private Control PathRow(string label, TextBox field, bool scan)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
            row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft },0,0); row.Controls.Add(field,1,0);
            var browse = new Button { Text = "Обзор", Dock = DockStyle.Fill };
            browse.Click += (s,e) => { using (var dialog = new FolderBrowserDialog()) if (dialog.ShowDialog() == DialogResult.OK) { field.Text = dialog.SelectedPath; Run(() => { SavePaths(); if (scan) { models = Catalog.Scan(field.Text); grid.DataSource = models; } if (models.Count > 0 && !string.IsNullOrWhiteSpace(gamePath.Text)) RefreshFavoriteStatuses(); }); } };
            row.Controls.Add(browse,2,0); return row;
        }
        private void Run(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); } }

        private async Task Execute(bool inspection)
        {
            if (cancellation != null) return;
            try
            {
                grid.EndEdit();
                EnsureGameClosed();
                var selected = models.Where(m => m.Selected && m.Valid).ToList();
                if (!inspection && selected.Count == 0) throw new InvalidOperationException("Сначала выберите исправные модели.");
                if (!inspection && MessageBox.Show("Файлы выбранных моделей уже перенесены в OpenIV? Будут добавлены записи Male, выполнен один Rebuild и обновлено избранное Menyoo.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                SavePaths();
                string game = Path.GetFullPath(gamePath.Text);
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                foreach (Control row in Controls[0].Controls) row.Enabled = false;
                ((Control)import.Parent).Enabled = true;
                foreach (Control action in import.Parent.Controls) action.Enabled = action == stop;
                var messages = new Progress<string>(text => { progress.AppendText(text + Environment.NewLine); });
                await Task.Run(() => {
                    Action<string> report = text => { ((IProgress<string>)messages).Report(text); };
                    try
                    {
                        AutomationWorker.Execute(game, selected, inspection, report, token);
                    }
                    catch (Exception ex) { ToolData.Log(ex.ToString()); throw; }
                });
            }
            catch (OperationCanceledException) { progress.AppendText("Остановлено. Подтверждённые записи остаются в AddonPeds. Можно повторить импорт; существующие модели будут пропущены." + Environment.NewLine); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                cancellation?.Dispose(); cancellation = null;
                foreach (Control row in Controls[0].Controls) row.Enabled = true;
                foreach (Control action in import.Parent.Controls) action.Enabled = action != stop;
            }
        }

        internal static void EnsureGameClosed()
        {
            if (Process.GetProcessesByName("GTA5").Length > 0 || Process.GetProcessesByName("GTA5_Enhanced").Length > 0)
                throw new InvalidOperationException("Закройте GTA перед импортом.");
        }

        private void LoadPaths()
        {
            string path = Path.Combine(ToolData.DirectoryPath, "Settings.xml");
            if (!File.Exists(path)) return;
            var xml = XDocument.Load(path);
            modelsPath.Text = (string)xml.Root.Element("Models") ?? "";
            gamePath.Text = (string)xml.Root.Element("Game") ?? "";
        }
        private void SavePaths()
        {
            string path = Path.Combine(ToolData.DirectoryPath, "Settings.xml");
            string temp = path + ".tmp";
            new XDocument(new XElement("Settings", new XElement("Models", modelsPath.Text), new XElement("Game", gamePath.Text))).Save(temp);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
    }
}
