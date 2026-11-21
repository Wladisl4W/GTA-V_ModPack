using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace ModPack.PedImporter
{
    public sealed class EditorAutomation
    {
        private readonly string gameDirectory;
        private readonly Action<string> report;
        private readonly CancellationToken cancel;
        private int pid;
        private AutomationElement main;

        public EditorAutomation(string gameDirectory, Action<string> report, CancellationToken cancel)
        {
            this.gameDirectory = Path.GetFullPath(gameDirectory);
            this.report = report;
            this.cancel = cancel;
        }

        public void Connect()
        {
            string executable = Path.Combine(gameDirectory, "AddonPeds Editor.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(gameDirectory, "AddonPedsVars.xml")))
                throw new IOException("В папке GTA не найдены AddonPeds Editor.exe и AddonPedsVars.xml.");
            var processes = Process.GetProcessesByName("AddonPeds Editor")
                .Where(p => string.Equals(p.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (processes.Length > 1) throw new InvalidOperationException("Закройте лишние экземпляры AddonPeds Editor.");
            var process = processes.Length == 1 ? processes[0] : Process.Start(new ProcessStartInfo(executable) {
                WorkingDirectory = gameDirectory, UseShellExecute = true });
            if (process == null) throw new InvalidOperationException("Не удалось запустить редактор.");
            pid = process.Id;
            main = Wait(() => Windows().FirstOrDefault(w => w.Current.Name == "AddonPeds Editor v3.0"), 15000, "Окно AddonPeds Editor v3.0 не найдено.");
            report("Подключён AddonPeds Editor, PID " + pid);
        }

        public void Inspect()
        {
            Connect();
            string path = Path.Combine(ToolData.DirectoryPath, "EditorControls.txt");
            using (var output = new StreamWriter(path, false))
            {
                output.AutoFlush = true;
                foreach (var window in Windows())
                {
                    output.WriteLine("WINDOW: " + window.Current.Name);
                    report("Чтение формы: " + window.Current.Name);
                    foreach (var item in Descendants(window))
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.WriteLine(item.Current.ControlType.ProgrammaticName + " | " + item.Current.AutomationId + " | " + item.Current.Name + " | " + Value(item) + " | " + string.Join(",", item.GetSupportedPatterns().Select(p => p.ProgrammaticName)));
                    }
                }
            }
            report("Список элементов записан: " + path);
        }

        public void Import(IList<PedModel> models)
        {
            Connect();
            var grids = Descendants(main).Where(e => e.Current.ControlType == ControlType.DataGrid || e.Current.ControlType == ControlType.Table).ToArray();
            if (grids.Length != 1) throw new InvalidOperationException("Не удалось однозначно найти таблицу моделей. Выполните проверку редактора.");
            var grid = grids[0];
            // Validate that model values are readable before changing the registry.
            var registered = ModelNames(grid);
            if (registered.Count == 0) throw new InvalidOperationException("Не удалось прочитать список педов. Сначала выполните проверку редактора.");
            foreach (var model in models)
            {
                cancel.ThrowIfCancellationRequested();
                ImportForm.EnsureGameClosed();
                if (registered.Contains(model.Name)) { report(model.Name + ": уже зарегистрирован, пропущен"); continue; }
                report(model.Name + ": добавление (Male, Streamed=" + model.Streamed + ")");
                var dialog = Windows().FirstOrDefault(w => w.Current.Name == "New Ped");
                if (dialog == null)
                {
                    Invoke(FindNamed(main, "Peds"));
                    Invoke(Wait(() => FindNamedOptional(main, "New Ped"), 3000, "Пункт New Ped недоступен."));
                    dialog = Wait(() => Windows().FirstOrDefault(w => w.Current.Name == "New Ped"), 5000, "Форма New Ped не открылась.");
                }
                var edits = Descendants(dialog).Where(e => e.Current.ControlType == ControlType.Edit && e.Current.IsEnabled).ToArray();
                if (edits.Length != 1) throw new InvalidOperationException("Не удалось однозначно определить поле Model Name.");
                SetText(edits[0], model.Name);
                var combos = Descendants(dialog).Where(e => e.Current.ControlType == ControlType.ComboBox).OrderBy(e => e.Current.BoundingRectangle.Top).ToArray();
                if (combos.Length != 2) throw new InvalidOperationException("Ожидалось два списка: Ped Type и Is Streamed.");
                Select(combos[0], "Male");
                Select(combos[1], model.Streamed ? "true" : "false");
                if (!Value(edits[0]).Equals(model.Name, StringComparison.Ordinal)) throw new InvalidOperationException("Имя модели не подтвердилось.");
                Invoke(FindNamed(dialog, "Add Ped"));
                Wait(() => {
                    var names = ModelNames(grid);
                    return names.Contains(model.Name) ? main : null;
                }, 10000, "Добавление не подтверждено: " + model.Name + ". Избранное не изменено.");
                registered.Add(model.Name);
                report(model.Name + ": запись подтверждена");
            }
            cancel.ThrowIfCancellationRequested();
            ImportForm.EnsureGameClosed();
            if (Windows().Any(w => w.Current.Name != main.Current.Name))
                throw new InvalidOperationException("Закройте открытые диалоги редактора перед Rebuild.");
            report("Rebuild: запуск один раз для всей пачки");
            Invoke(FindNamed(main, "REBUILD"));
            var completed = Wait(() => {
                foreach (var window in Windows().Where(w => w.Current.Name != main.Current.Name))
                {
                    string message = string.Join(" ", Descendants(window).Where(e => e.Current.ControlType == ControlType.Text).Select(e => e.Current.Name));
                    if (IsRebuildSuccess(message)) return window;
                    if (!string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Ответ Rebuild требует проверки: " + message);
                }
                return null;
            }, 120000, "Rebuild не подтвердил завершение за 120 секунд. Избранное не изменено.");
            report("Rebuild: получено подтверждение успешного завершения");
            var ok = Descendants(completed).FirstOrDefault(e => e.Current.ControlType == ControlType.Button && (e.Current.Name == "OK" || e.Current.Name == "ОК"));
            if (ok != null) Invoke(ok);
        }

        public static bool IsRebuildSuccess(string message)
        {
            string value = message.ToLowerInvariant();
            if (value.Contains("error") || value.Contains("fail") || value.Contains("unsuccess") || value.Contains("not successful")) return false;
            return value.Contains("rebuild") && (value.Contains("success") || value.Contains("completed"));
        }

        private AutomationElement[] Windows()
        {
            return AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>().ToArray();
        }
        private static IEnumerable<AutomationElement> Descendants(AutomationElement element)
        {
            return WalkControls(element, 0);
        }
        private static IEnumerable<AutomationElement> WalkControls(AutomationElement element, int depth)
        {
            if (depth >= 6) yield break;
            foreach (AutomationElement child in element.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                yield return child;
                // Old WinForms grids expose hundreds of cells through a slow accessibility bridge.
                // Read only the first model column via GridPattern when needed, not the whole tree.
                if (child.Current.ControlType == ControlType.Table || child.Current.ControlType == ControlType.DataGrid) continue;
                foreach (var nested in WalkControls(child, depth + 1)) yield return nested;
            }
        }
        private static AutomationElement Unique(AutomationElement root, ControlType type)
        {
            var items = Descendants(root).Where(e => e.Current.ControlType == type).ToArray();
            if (items.Length != 1) throw new InvalidOperationException("Неоднозначный элемент: " + type.ProgrammaticName);
            return items[0];
        }
        private static AutomationElement FindNamedOptional(AutomationElement root, string name)
        {
            return Descendants(root).FirstOrDefault(e => e.Current.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        private static AutomationElement FindNamed(AutomationElement root, string name)
        {
            return FindNamedOptional(root, name) ?? throw new InvalidOperationException("Элемент не найден: " + name);
        }
        private static string Value(AutomationElement element)
        {
            object pattern;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return ((ValuePattern)pattern).Current.Value ?? "";
            return "";
        }
        private static HashSet<string> ModelNames(AutomationElement grid)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object pattern;
            if (!grid.TryGetCurrentPattern(GridPattern.Pattern, out pattern))
                throw new InvalidOperationException("Таблица не поддерживает чтение строк через GridPattern. Сохраните результат проверки редактора.");
            var table = (GridPattern)pattern;
            int count = table.Current.RowCount;
            if (count > 20000) throw new InvalidOperationException("Некорректное количество строк в редакторе.");
            for (int row = 0; row < count; row++)
            {
                string value = Value(table.GetItem(row, 0));
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            }
            return names;
        }
        private static void SetText(AutomationElement edit, string value)
        {
            object pattern;
            if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) throw new InvalidOperationException("Поле не поддерживает ввод.");
            ((ValuePattern)pattern).SetValue(value);
        }
        private void Select(AutomationElement combo, string value)
        {
            object pattern;
            if (!combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) throw new InvalidOperationException("Список нельзя раскрыть.");
            var expand = (ExpandCollapsePattern)pattern;
            expand.Expand();
            var item = Wait(() => Windows().SelectMany(Descendants).FirstOrDefault(e => e.Current.ControlType == ControlType.ListItem && e.Current.Name.Equals(value, StringComparison.OrdinalIgnoreCase)),
                3000, "В списке нет значения " + value);
            if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) throw new InvalidOperationException("Элемент списка нельзя выбрать.");
            ((SelectionItemPattern)pattern).Select();
            expand.Collapse();
            if (!Value(combo).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                if (!combo.TryGetCurrentPattern(SelectionPattern.Pattern, out pattern) ||
                    !((SelectionPattern)pattern).Current.GetSelection().Any(e => e.Current.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Выбранное значение не подтвердилось: " + value);
            }
        }
        private static void Invoke(AutomationElement element)
        {
            if (!element.Current.IsEnabled) throw new InvalidOperationException("Кнопка недоступна: " + element.Current.Name);
            object pattern;
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke();
            else throw new InvalidOperationException("Элемент не поддерживает нажатие: " + element.Current.Name);
        }
        private AutomationElement Wait(Func<AutomationElement> probe, int timeout, string error)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeout)
            {
                cancel.ThrowIfCancellationRequested();
                var element = probe();
                if (element != null) return element;
                if (cancel.WaitHandle.WaitOne(200)) cancel.ThrowIfCancellationRequested();
            }
            throw new TimeoutException(error);
        }
    }

    internal static class ToolData
    {
        public static string DirectoryPath
        {
            get { string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModPack", "PedImporter"); Directory.CreateDirectory(path); return path; }
        }
        public static void Log(string message)
        {
            File.AppendAllText(Path.Combine(DirectoryPath, "Import.log"), DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
        }
    }
}
