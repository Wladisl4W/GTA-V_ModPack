using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

namespace ModPack.PedImporter
{
    internal static class AutomationWorker
    {
        public static int Run(string job)
        {
            try
            {
                var xml = XDocument.Load(job).Root;
                ImportForm.EnsureGameClosed();
                Action<string> report = text => { ToolData.Log(text); Console.WriteLine(text); Console.Out.Flush(); };
                var automation = new EditorAutomation((string)xml.Element("Game"), report, CancellationToken.None);
                if ((string)xml.Attribute("mode") == "inspect") automation.Inspect();
                else
                {
                    var models = xml.Elements("Ped").Select(e => new PedModel {
                        Name = (string)e.Attribute("name"), Streamed = (bool)e.Attribute("streamed"), Valid = true, Selected = true }).ToList();
                    automation.Import(models);
                    ImportForm.EnsureGameClosed();
                    report("Готово. Добавлено в избранное: " + Catalog.AddFavorites((string)xml.Element("Game"), models));
                }
                return 0;
            }
            catch (Exception ex)
            {
                ToolData.Log(ex.ToString());
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        public static void Execute(string game, System.Collections.Generic.IList<PedModel> models,
            bool inspect, Action<string> report, CancellationToken cancellation)
        {
            string job = Path.Combine(ToolData.DirectoryPath, "job-" + Guid.NewGuid().ToString("N") + ".xml");
            new XDocument(new XElement("Job", new XAttribute("mode", inspect ? "inspect" : "import"),
                new XElement("Game", game), models.Select(m => new XElement("Ped", new XAttribute("name", m.Name), new XAttribute("streamed", m.Streamed))))).Save(job);
            long lastMessage = Stopwatch.GetTimestamp();
            int idleLimitSeconds = 30;
            string error = "";
            try
            {
                using (var worker = new Process { StartInfo = new ProcessStartInfo {
                    FileName = System.Windows.Forms.Application.ExecutablePath,
                    Arguments = "--worker \"" + job + "\"", UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    CreateNoWindow = true } })
                {
                    worker.OutputDataReceived += (s,e) => {
                        if (e.Data == null) return;
                        Interlocked.Exchange(ref lastMessage, Stopwatch.GetTimestamp());
                        Volatile.Write(ref idleLimitSeconds, e.Data.StartsWith("Rebuild:", StringComparison.Ordinal) ? 135 : 30);
                        report(e.Data);
                    };
                    worker.ErrorDataReceived += (s,e) => { if (e.Data != null) error += e.Data + Environment.NewLine; };
                    worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
                    while (!worker.WaitForExit(200))
                    {
                        bool timeout = (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastMessage)) / (double)Stopwatch.Frequency > Volatile.Read(ref idleLimitSeconds);
                        if (!timeout && !cancellation.IsCancellationRequested) continue;
                        worker.Kill(); worker.WaitForExit();
                        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                        throw new TimeoutException("Редактор не ответил вовремя. Проверка остановлена, интерфейс разблокирован. Если добавление или Rebuild уже начались, проверьте результат в редакторе перед повторным импортом.");
                    }
                    worker.WaitForExit();
                    if (worker.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error.Trim() : "Рабочий процесс завершился с ошибкой.");
                }
            }
            finally { if (File.Exists(job)) File.Delete(job); }
        }
    }
}
