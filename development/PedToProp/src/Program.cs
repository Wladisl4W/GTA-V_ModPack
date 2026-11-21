using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ModPack.PedToProp
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--check")
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                try { Console.WriteLine(InputPack.Check(args.Skip(1).ToArray()).Name); return 0; }
                catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ConverterForm());
            return 0;
        }
    }

    internal sealed class InputPack
    {
        private static readonly string[] Extensions = { ".ydd", ".ytd", ".yft", ".ymt" };
        internal string Name;
        internal string Directory;
        internal string[] Files;

        internal static InputPack Check(string[] files)
        {
            if (files == null || files.Length != 4)
                throw new InvalidDataException("Выберите ровно четыре файла: .ydd, .ytd, .yft и .ymt.");
            string[] paths = files.Select(Path.GetFullPath).ToArray();
            string name = Path.GetFileNameWithoutExtension(paths[0]);
            string directory = Path.GetDirectoryName(paths[0]);
            if (name.Length == 0 || name.Any(c => c > 127 || !(char.IsLetterOrDigit(c) || c == '_')))
                throw new InvalidDataException("Имя модели должно состоять из латинских букв, цифр и подчёркивания.");
            if (paths.Any(p => !string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) ||
                Extensions.Any(ext => paths.Count(p => string.Equals(Path.GetExtension(p), ext, StringComparison.OrdinalIgnoreCase)) != 1))
                throw new InvalidDataException("Все четыре файла должны быть в одной папке, с одинаковым именем и разными нужными расширениями.");
            foreach (string path in paths)
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var header = new byte[4];
                    if (stream.Length < 16 || stream.Read(header, 0, 4) != 4 || Encoding.ASCII.GetString(header) != "RSC7")
                        throw new InvalidDataException("Файл не похож на GTA V Legacy RSC7: " + Path.GetFileName(path));
                }
            }
            return new InputPack { Name = name, Directory = directory, Files = paths };
        }
    }

    internal sealed class ConverterForm : Form
    {
        private static readonly string ToolDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModPack", "PedToProp");
        private static readonly string DependencyDir = Path.Combine(DataDir, "Dependencies", "2.9.0");
        private static readonly string AgreementUrl = "https://static.cfx.re/PyMateria-License-Agreement.pdf";
        private readonly TextBox input = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        private readonly TextBox output = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox blender = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button browseInput = new Button { Text = "Выбрать 4 файла", AutoSize = true };
        private readonly Button browseOutput = new Button { Text = "Обзор", AutoSize = true };
        private readonly Button browseBlender = new Button { Text = "Обзор", AutoSize = true };
        private readonly Button setup = new Button { Text = "Настроить конвертер", AutoSize = true };
        private readonly Button convert = new Button { Text = "Конвертировать", AutoSize = true };
        private readonly Button cancel = new Button { Text = "Остановить", AutoSize = true, Enabled = false };
        private readonly TrackBar scale = new TrackBar { Minimum = 1, Maximum = 400, Value = 10, TickFrequency = 50, SmallChange = 1, LargeChange = 10, Dock = DockStyle.Fill };
        private readonly Label scaleValue = new Label { Text = "x1,0", Width = 65, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private string[] selectedFiles;
        private CancellationTokenSource cancellation;
        private string lastOutput;

        internal ConverterForm()
        {
            Text = "ModPack - Ped to Prop";
            MinimumSize = new Size(680, 420);
            Size = new Size(820, 540);
            AllowDrop = true;
            output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Converted Props");
            string steamBlender = @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
            blender.Text = File.Exists(steamBlender) ? steamBlender : "";
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.Controls.Add(Row("Файлы педа", input, browseInput), 0, 0);
            table.Controls.Add(Row("Папка результата", output, browseOutput), 0, 1);
            table.Controls.Add(Row("Blender", blender, browseBlender), 0, 2);
            var scaleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            scaleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            scaleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scaleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            scaleRow.Controls.Add(new Label { Text = "Масштаб", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            scaleRow.Controls.Add(scale, 1, 0);
            scaleRow.Controls.Add(scaleValue, 2, 0);
            table.Controls.Add(scaleRow, 0, 3);
            scale.ValueChanged += (s, e) => scaleValue.Text = "x" + (scale.Value / 10.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            buttons.Controls.AddRange(new Control[] { setup, convert, cancel });
            table.Controls.Add(buttons, 0, 4);
            table.Controls.Add(status, 0, 5);
            table.Controls.Add(log, 0, 6);
            Controls.Add(table);
            browseInput.Click += (s, e) => SelectFiles();
            browseOutput.Click += (s, e) => { using (var dialog = new FolderBrowserDialog()) if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath; };
            browseBlender.Click += (s, e) => { using (var dialog = new OpenFileDialog { Filter = "Blender|blender.exe" }) if (dialog.ShowDialog(this) == DialogResult.OK) blender.Text = dialog.FileName; };
            setup.Click += async (s, e) => await Configure();
            convert.Click += async (s, e) => await Convert();
            cancel.Click += (s, e) => cancellation?.Cancel();
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => SetFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            FormClosing += (s, e) => { if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; } };
            RefreshStatus();
        }

        private static Control Row(string label, Control field, Control button)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            row.Controls.Add(field, 1, 0); row.Controls.Add(button, 2, 0);
            return row;
        }

        private void SelectFiles()
        {
            using (var dialog = new OpenFileDialog { Filter = "GTA ped files|*.ydd;*.ytd;*.yft;*.ymt", Multiselect = true, Title = "Выберите четыре файла одной модели" })
                if (dialog.ShowDialog(this) == DialogResult.OK) SetFiles(dialog.FileNames);
        }

        private void SetFiles(string[] files)
        {
            try
            {
                var pack = InputPack.Check(files);
                selectedFiles = pack.Files;
                input.Text = pack.Name + " (4 файла, " + pack.Directory + ")";
                Write("Выбрана модель " + pack.Name);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void RefreshStatus()
        {
            status.Text = File.Exists(Path.Combine(DependencyDir, "ready.txt")) ?
                "Sollumz 2.9.0 готов. Файлы GTA не меняются." :
                "Нужна разовая настройка Sollumz 2.9.0.";
        }

        private void Write(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Write(message))); return; }
            log.AppendText(message + Environment.NewLine);
        }

        private async Task Configure()
        {
            if (cancellation != null) return;
            if (!File.Exists(blender.Text)) { MessageBox.Show("Укажите blender.exe.", Text); return; }
            if (File.Exists(Path.Combine(DependencyDir, "ready.txt"))) { RefreshStatus(); return; }
            if (MessageBox.Show("Будут загружены Sollumz 2.9.0 и зависимости szio/PyMateria в папку ModPack вашего профиля.\n\nЛицензия PyMateria: " + AgreementUrl + "\n\nПродолжить?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await Execute(async token =>
            {
                string script = Path.Combine(ToolDir, "setup.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("Не найден setup.ps1 рядом с программой.", script);
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " -BlenderExe " + Quote(blender.Text));
                await Run(psi, token);
                if (!File.Exists(Path.Combine(DependencyDir, "ready.txt"))) throw new IOException("Настройка не завершилась.");
            });
            RefreshStatus();
        }

        private async Task Convert()
        {
            if (cancellation != null) return;
            InputPack pack;
            try { pack = InputPack.Check(selectedFiles); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text); return; }
            if (!File.Exists(blender.Text) || !File.Exists(Path.Combine(DependencyDir, "ready.txt")))
            { MessageBox.Show("Укажите Blender и сначала нажмите 'Настроить конвертер'.", Text); return; }
            string root;
            try
            {
                root = Path.GetFullPath(output.Text);
                if (string.Equals(root.TrimEnd('\\'), pack.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Выберите папку результата отдельно от исходников.");
                Directory.CreateDirectory(root);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text); return; }
            await Execute(async token =>
            {
                string script = Path.Combine(ToolDir, "convert_blender.py");
                if (!File.Exists(script)) throw new FileNotFoundException("Не найден convert_blender.py рядом с программой.", script);
                string propName = "prop_p2p_" + pack.Name.ToLowerInvariant();
                string stage = Path.Combine(root, "." + propName + ".pending-" + Guid.NewGuid().ToString("N"));
                string inputStage = Path.Combine(DataDir, "Work", Guid.NewGuid().ToString("N"));
                string destination = Path.Combine(root, propName);
                for (int suffix = 2; Directory.Exists(destination); suffix++) destination = Path.Combine(root, propName + "-" + suffix);
                string captured = "";
                try
                {
                    Directory.CreateDirectory(inputStage);
                    foreach (string source in pack.Files)
                        File.Copy(source, Path.Combine(inputStage, Path.GetFileName(source)));
                    var psi = new ProcessStartInfo(blender.Text, "-b --factory-startup --python " + Quote(script) + " -- " + Quote(inputStage) + " " + Quote(pack.Name) + " " + Quote(stage) + " " + (scale.Value / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    psi.EnvironmentVariables["PEDTOPROP_SOLLUMZ_PARENT"] = Path.Combine(DependencyDir, "addon");
                    psi.EnvironmentVariables["PEDTOPROP_SOLLUMZ_LIB"] = Path.Combine(DependencyDir, "lib");
                    captured = await Run(psi, token);
                    if (!captured.Contains("PEDTOPROP_OK " + propName + " ")) throw new IOException("Blender не подтвердил результат.");
                    foreach (string ext in new[] { ".ydr", "_preview.png" })
                        if (!File.Exists(Path.Combine(stage, propName + ext))) throw new IOException("Не создан файл: " + propName + ext);
                    File.WriteAllText(Path.Combine(stage, "conversion.log"), captured, new UTF8Encoding(false));
                    if (!Path.GetFullPath(destination).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Папка результата вышла за пределы выбранного каталога.");
                    Directory.Move(stage, destination);
                    lastOutput = destination;
                    Write("Готово: " + destination);
                }
                finally
                {
                    DeleteOwnDirectory(stage, root);
                    DeleteOwnDirectory(inputStage, Path.Combine(DataDir, "Work"));
                }
            });
            if (lastOutput != null && Directory.Exists(lastOutput))
                MessageBox.Show("Проп подготовлен в отдельной папке:\n" + lastOutput + "\n\nУстановка в GTA не выполнялась.", Text);
        }

        private async Task Execute(Func<CancellationToken, Task> action)
        {
            cancellation = new CancellationTokenSource();
            setup.Enabled = convert.Enabled = browseInput.Enabled = browseOutput.Enabled = browseBlender.Enabled = scale.Enabled = false;
            cancel.Enabled = true;
            lastOutput = null;
            try { await action(cancellation.Token); }
            catch (OperationCanceledException) { Write("Остановлено пользователем."); }
            catch (Exception ex) { Write("Ошибка: " + ex.Message); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                setup.Enabled = convert.Enabled = browseInput.Enabled = browseOutput.Enabled = browseBlender.Enabled = scale.Enabled = true;
                cancel.Enabled = false;
            }
        }

        private async Task<string> Run(ProcessStartInfo start, CancellationToken token)
        {
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            var all = new StringBuilder();
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (all) all.AppendLine(e.Data); Write(e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (all) all.AppendLine(e.Data); Write(e.Data); } };
                if (!process.Start()) throw new IOException("Не удалось запустить внешний инструмент.");
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                    await Task.Run(() => process.WaitForExit());
                if (token.IsCancellationRequested || process.ExitCode != 0)
                {
                    string logs = Path.Combine(DataDir, "Logs");
                    Directory.CreateDirectory(logs);
                    string file = Path.Combine(logs, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
                    lock (all) File.WriteAllText(file, all.ToString(), new UTF8Encoding(false));
                    Write("Журнал ошибки: " + file);
                }
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new IOException("Внешний инструмент завершился с кодом " + process.ExitCode + ". Подробности выше.");
            }
            lock (all) return all.ToString();
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        private static void DeleteOwnDirectory(string directory, string parent)
        {
            string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullDirectory = Path.GetFullPath(directory);
            if (!fullDirectory.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Временная папка вышла за пределы рабочего каталога.");
            if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, true);
        }
    }
}
