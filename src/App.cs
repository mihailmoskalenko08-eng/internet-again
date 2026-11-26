using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InternetAgain
{
    internal static class Program
    {
        public const string Title = "Интернет опять отвалился";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string sid;
            using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            using (var mutex = new Mutex(false, @"Global\InternetAgain-" + sid))
            {
                bool entered;
                try { entered = mutex.WaitOne(args.Contains("--handoff") ? 10000 : 0); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered)
                {
                    if (!args.Contains("--tray")) MessageBox.Show(
                        "Приложение уже работает. Откройте его значок в области уведомлений.",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    using (var window = new Dashboard(sid, args.Contains("--handoff")))
                    {
                        if (!args.Contains("--tray")) window.Show();
                        Application.Run();
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class Dashboard : Form
    {
        private static readonly Color Background = Color.FromArgb(16, 21, 33);
        private static readonly Color Surface = Color.FromArgb(26, 34, 50);
        private static readonly Color Raised = Color.FromArgb(37, 47, 68);
        private static readonly Color TextColor = Color.FromArgb(240, 243, 252);
        private static readonly Color Muted = Color.FromArgb(170, 182, 205);
        private static readonly Color Accent = Color.FromArgb(194, 179, 255);
        private static readonly Color Good = Color.FromArgb(126, 227, 182);
        private static readonly Color Warning = Color.FromArgb(255, 194, 134);
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValue = "InternetAgain";

        private readonly System.Windows.Forms.Timer checkTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip trayMenu;
        private readonly bool elevated = TrafficMeter.IsElevated();
        private readonly string userSid;
        private readonly AppState state;
        private readonly bool storageAvailable;
        private readonly Panel content;
        private readonly Panel[] pages = new Panel[4];
        private readonly Button[] tabs = new Button[4];
        private readonly Label footer;
        private readonly Label headline, detail, lastCheck, wifi, link, router, internet, quality;
        private readonly Button checkButton, trafficButton;
        private readonly Label trafficStatus, journalStatus, notice;
        private readonly DataGridView trafficGrid, journalGrid;
        private readonly DateTimePicker due;
        private readonly NumericUpDown lead;
        private readonly CheckBox startup;
        private readonly List<bool> routerSamples = new List<bool>();
        private CheckResult latest;
        private DateTime lastWrite = DateTime.MinValue;
        private Dictionary<string, TrafficTotal> rates = new Dictionary<string, TrafficTotal>();
        private TrafficMeter meter;
        private string meterError, storageError;
        private int selectedPage;
        private int lastJournalMinute = -1;
        private bool dirty, checking, exiting, updatingStartup;

        public Dashboard(string sid, bool openTraffic)
        {
            userSid = sid;
            try
            {
                state = StateFile.Read();
                storageAvailable = true;
            }
            catch (Exception e)
            {
                state = new AppState();
                storageError = "Данные не удалось прочитать. Изменения доступны до закрытия приложения. " + e.Message;
            }
            state.BreakObservation();
            dirty = true;

            Text = Program.Title;
            Icon = SystemIcons.Information;
            ClientSize = new Size(980, 680);
            MinimumSize = new Size(840, 680);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Background;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 10);
            Padding = new Padding(26, 18, 26, 12);

            var shell = Layout(104, 49, -1, 30);
            Controls.Add(shell);
            var brand = Layout(38, 50);
            brand.Controls.Add(Caption("МОНИТОР ПОДКЛЮЧЕНИЯ", 10, Accent, true), 0, 0);
            brand.Controls.Add(Caption(Program.Title, 27, TextColor, true), 0, 1);
            shell.Controls.Add(brand, 0, 0);

            var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
            string[] tabNames = { "Подключение", "Трафик", "Журнал сбоев", "Оплата" };
            for (int i = 0; i < tabs.Length; i++)
            {
                int page = i;
                navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                tabs[i] = ActionButton(tabNames[i], delegate { ShowPage(page); });
                tabs[i].Margin = new Padding(0, 0, i == 3 ? 0 : 8, 8);
                navigation.Controls.Add(tabs[i], i, 0);
            }
            shell.Controls.Add(navigation, 0, 1);
            content = new Panel { Dock = DockStyle.Fill };
            shell.Controls.Add(content, 0, 2);
            footer = Caption("", 9, Muted, false);
            footer.AutoEllipsis = true;
            shell.Controls.Add(footer, 0, 3);

            var connection = Layout(78, 44, 39, 20, 70, 70, 70, -1);
            connection.Padding = new Padding(20, 8, 20, 8);
            connection.BackColor = Surface;
            headline = Caption("Проверяем подключение", 24, TextColor, true);
            detail = Caption("Смотрим состояние адаптеров, шлюза и доступность сайтов.", 10, Muted, false);
            lastCheck = Caption("Ожидаем результат", 9, Muted, false);
            checkButton = ActionButton("Проверить сейчас", RunCheck);
            var controls = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            controls.Controls.Add(lastCheck, 0, 0);
            controls.Controls.Add(checkButton, 1, 0);
            connection.Controls.Add(headline, 0, 0);
            connection.Controls.Add(detail, 0, 1);
            connection.Controls.Add(controls, 0, 2);
            connection.Controls.Add(Stage("Компьютер", out wifi, out link), 0, 4);
            connection.Controls.Add(Stage("Роутер / шлюз", out router, out quality), 0, 5);
            Label internetHint;
            connection.Controls.Add(Stage("Интернет", out internet, out internetHint), 0, 6);
            internetHint.Text = "Доступность контрольных сайтов по HTTPS";
            pages[0] = Wrap(connection);

            var traffic = Layout(43, 44, -1, 26);
            traffic.Padding = new Padding(12, 8, 12, 8);
            traffic.BackColor = Surface;
            var trafficTop = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
            trafficTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            trafficTop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 252));
            trafficTop.Controls.Add(Caption("Трафик по программам", 17, TextColor, true), 0, 0);
            trafficButton = ActionButton(elevated ? "Включить учёт" : "Включить учёт трафика", StartTraffic);
            trafficTop.Controls.Add(trafficButton, 1, 0);
            trafficStatus = Caption("", 9, Muted, false);
            trafficGrid = Grid(new[] { "Программа", "↓ Сейчас", "↑ Сейчас", "↓ За день", "↑ За день" },
                new float[] { 37, 16, 16, 16, 15 });
            traffic.Controls.Add(trafficTop, 0, 0);
            traffic.Controls.Add(trafficStatus, 0, 1);
            traffic.Controls.Add(trafficGrid, 0, 2);
            traffic.Controls.Add(Caption("↓ Получено   ·   ↑ Отправлено   ·   С начала учёта за местные сутки", 9, Muted, false), 0, 3);
            pages[1] = Wrap(traffic);

            var journal = Layout(44, 42, -1);
            journal.Padding = new Padding(12, 8, 12, 8);
            journal.BackColor = Surface;
            var journalTop = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
            journalTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            journalTop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 186));
            journalTop.Controls.Add(Caption("Журнал отключений", 17, TextColor, true), 0, 0);
            journalTop.Controls.Add(ActionButton("Сохранить отчёт", Export), 1, 0);
            journalStatus = Caption("", 9, Muted, false);
            journalGrid = Grid(new[] { "Обнаружен сбой", "Восстановление", "Длительность", "Этап сбоя" },
                new float[] { 26, 29, 17, 28 });
            journal.Controls.Add(journalTop, 0, 0);
            journal.Controls.Add(journalStatus, 0, 1);
            journal.Controls.Add(journalGrid, 0, 2);
            pages[2] = Wrap(journal);

            var payment = Layout(58, 52, 75, 75, 50, 54, -1);
            payment.Padding = new Padding(20, 8, 20, 8);
            payment.BackColor = Surface;
            payment.Controls.Add(Caption("Оплата интернета", 21, TextColor, true), 0, 0);
            notice = Caption("", 11, Accent, true);
            payment.Controls.Add(notice, 0, 1);
            due = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy",
                Font = new Font("Segoe UI", 12), Width = 205 };
            DateTime saved;
            due.Value = ReadDue(out saved) ? saved : DateTime.Today.AddDays(7);
            payment.Controls.Add(PaymentField("Дата оплаты", due), 0, 2);
            lead = new NumericUpDown { Minimum = 0, Maximum = 30, Value = state.LeadDays, Width = 100,
                Font = new Font("Segoe UI", 12) };
            payment.Controls.Add(PaymentField("За сколько дней напомнить", lead), 0, 3);
            var paymentActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var save = ActionButton("Сохранить дату", SaveReminder);
            var clear = ActionButton("Убрать напоминание", ClearReminder);
            save.Dock = clear.Dock = DockStyle.None;
            save.Size = new Size(164, 37);
            clear.Size = new Size(200, 37);
            save.BackColor = Accent;
            save.ForeColor = Background;
            paymentActions.Controls.AddRange(new Control[] { save, clear });
            payment.Controls.Add(paymentActions, 0, 4);
            startup = new CheckBox { Text = "Запускать в области уведомлений при входе в Windows",
                ForeColor = TextColor, Dock = DockStyle.Fill, Checked = IsStartupEnabled() };
            startup.CheckedChanged += delegate { SetStartup(); };
            payment.Controls.Add(startup, 0, 5);
            pages[3] = Wrap(payment);

            for (int i = 0; i < pages.Length; i++) content.Controls.Add(pages[i]);
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Открыть", null, delegate { OpenWindow(); });
            trayMenu.Items.Add("Проверить подключение", null, delegate { RunCheck(); });
            trayMenu.Items.Add("Выйти", null, delegate { Exit(); });
            tray = new NotifyIcon { Text = Program.Title, Icon = Icon, ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += delegate { OpenWindow(); };
            tray.BalloonTipClicked += delegate { OpenWindow(); ShowPage(3); };

            checkTimer.Tick += delegate { RunCheck(); };
            updateTimer.Tick += delegate { Tick(); };
            Microsoft.Win32.SystemEvents.PowerModeChanged += HandlePower;
            if (elevated) EnableMeter();
            ShowPage(openTraffic ? 1 : 0);
            Tick();
            checkTimer.Start();
            updateTimer.Start();
            RunCheck();
        }

        private static TableLayoutPanel Layout(params int[] heights)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length,
                Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in heights)
                table.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100)
                    : new RowStyle(SizeType.Absolute, height));
            return table;
        }

        private static Panel Wrap(Control control)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Surface };
            panel.Controls.Add(control);
            return panel;
        }

        private static Label Caption(string text, int size, Color color, bool bold)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = color, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
        }

        private static Button ActionButton(string text, Action action)
        {
            var button = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                ForeColor = TextColor, BackColor = Raised, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(71, 69, 106);
            button.Click += delegate { action(); };
            return button;
        }

        private static Control Stage(string title, out Label value, out Label note)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
                BackColor = Raised, Padding = new Padding(14, 3, 14, 3), Margin = new Padding(0, 0, 0, 8) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 59));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 41));
            value = Caption("Проверяем…", 11, TextColor, true);
            note = Caption("", 9, Muted, false);
            var heading = Caption(title, 11, Accent, true);
            row.Controls.Add(heading, 0, 0);
            row.SetRowSpan(heading, 2);
            row.Controls.Add(value, 1, 0);
            row.Controls.Add(note, 1, 1);
            return row;
        }

        private static Control PaymentField(string title, Control input)
        {
            var field = Layout(28, 37);
            field.Controls.Add(Caption(title, 10, Muted, false), 0, 0);
            field.Controls.Add(input, 0, 1);
            return field;
        }

        private static DataGridView Grid(string[] headings, float[] widths)
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToResizeRows = false, MultiSelect = false,
                RowHeadersVisible = false, ColumnHeadersHeight = 34, GridColor = Raised,
                BorderStyle = BorderStyle.None, BackgroundColor = Surface, EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Raised, ForeColor = Accent,
                SelectionBackColor = Raised, SelectionForeColor = Accent, Padding = new Padding(6, 0, 3, 0) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = TextColor,
                SelectionBackColor = Raised, SelectionForeColor = TextColor, Padding = new Padding(6, 0, 3, 0) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(30, 39, 56);
            grid.RowTemplate.Height = 31;
            foreach (string heading in headings)
            {
                int i = grid.Columns.Count;
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = heading, FillWeight = widths[i],
                    SortMode = DataGridViewColumnSortMode.NotSortable });
            }
            return grid;
        }

        private void ShowPage(int page)
        {
            selectedPage = page;
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = i == page;
                tabs[i].BackColor = i == page ? Accent : Raised;
                tabs[i].ForeColor = i == page ? Background : TextColor;
            }
            pages[page].BringToFront();
            if (page == 1) RefreshTraffic();
            if (page == 2) RefreshJournal();
        }

        private async void RunCheck()
        {
            if (checking || exiting) return;
            checking = true;
            checkButton.Enabled = false;
            checkButton.Text = "Проверяем…";
            try
            {
                var result = await Task.Run(() => NetworkCheck.Run());
                if (exiting) return;
                if (latest != null && (result.At - latest.At > TimeSpan.FromSeconds(25) ||
                    !Object.Equals(latest.Gateway, result.Gateway))) routerSamples.Clear();
                if (result.GatewayTested)
                {
                    routerSamples.Add(result.GatewayResponded);
                    if (routerSamples.Count > 12) routerSamples.RemoveAt(0);
                }
                else routerSamples.Clear();
                if (state.Record(result, latest == null ? (DateTime?)null : latest.At))
                {
                    dirty = true;
                    SaveState();
                }
                latest = result;
                RenderConnection();
                if (Visible && selectedPage == 2) RefreshJournal();
            }
            catch (Exception e)
            {
                if (exiting) return;
                state.BreakObservation();
                dirty = true;
                headline.Text = "Не удалось проверить подключение";
                headline.ForeColor = Warning;
                detail.Text = e.Message;
                latest = null;
                routerSamples.Clear();
                wifi.Text = router.Text = internet.Text = "Нет данных";
                link.Text = quality.Text = "";
                lastCheck.Text = "Повторим автоматически";
            }
            finally
            {
                checking = false;
                if (!exiting)
                {
                    checkButton.Text = "Проверить сейчас";
                    checkButton.Enabled = true;
                }
            }
        }

        private void RenderConnection()
        {
            headline.Text = latest.Headline;
            headline.ForeColor = latest.Online == true ? Good : Warning;
            detail.Text = latest.Detail;
            lastCheck.Text = "Проверка в " + latest.At.ToLocalTime().ToString("HH:mm:ss");
            wifi.Text = "Wi-Fi · " + latest.Wifi;
            link.Text = "Активное подключение · " + latest.Link;
            router.Text = latest.Router;
            internet.Text = latest.Internet;
            quality.Text = routerSamples.Count == 0 ? "" : "Без ответа: " +
                routerSamples.Count(success => !success) + " из " + routerSamples.Count + " ping";
            tray.Text = latest.Online == true ? "Интернет работает" : Program.Title;
        }

        private void HandlePower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Suspend || exiting || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (exiting) return;
                    state.BreakObservation();
                    routerSamples.Clear();
                    dirty = true;
                    SaveState();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void Tick()
        {
            if (exiting) return;
            CollectTraffic();
            CheckReminder();
            if (dirty && DateTime.UtcNow - lastWrite > TimeSpan.FromSeconds(8)) SaveState();
            if (Visible && selectedPage == 1) RefreshTraffic();
            if (Visible && selectedPage == 2 && DateTime.Now.Minute != lastJournalMinute) RefreshJournal();
            footer.Text = storageError ?? "Проверка каждые 8 секунд · мониторинг продолжается в области уведомлений";
            footer.ForeColor = storageError == null ? Muted : Warning;
        }

        private void CollectTraffic()
        {
            DateTime now = DateTime.UtcNow;
            string today = StateFile.LocalDay(now);
            if (state.TrafficDate != today)
            {
                state.TrafficDate = today;
                state.Traffic.Clear();
                dirty = true;
            }
            if (meter == null) return;
            bool changed;
            rates = meter.Drain(today, now, state, out changed);
            dirty |= changed;
        }

        private void RefreshTraffic()
        {
            trafficStatus.Text = meter != null ? meter.Status : meterError ??
                (elevated ? "Учёт выключен." : "Для сбора трафика требуется запуск с правами администратора.");
            trafficButton.Visible = meter == null || meter.HasFailed;
            var rows = state.Traffic.OrderByDescending(item =>
            {
                TrafficTotal recent;
                return rates.TryGetValue(item.Key, out recent) ? recent.Received + recent.Sent : 0;
            }).ThenByDescending(item => item.Value.Received + item.Value.Sent)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            string selected = trafficGrid.SelectedRows.Count == 0 ? null : trafficGrid.SelectedRows[0].Tag as string;
            int scroll = trafficGrid.FirstDisplayedScrollingRowIndex;
            trafficGrid.SuspendLayout();
            try
            {
                trafficGrid.Rows.Clear();
                int selection = -1;
                foreach (var item in rows)
                {
                    TrafficTotal recent;
                    rates.TryGetValue(item.Key, out recent);
                    string path = item.Key;
                    int index = trafficGrid.Rows.Add(Path.GetFileName(path), meter == null ? "—" :
                        Rate(recent == null ? 0 : recent.Received), meter == null ? "—" :
                        Rate(recent == null ? 0 : recent.Sent), Bytes(item.Value.Received), Bytes(item.Value.Sent));
                    var row = trafficGrid.Rows[index];
                    row.Tag = path;
                    row.Cells[0].ToolTipText = path;
                    if (StringComparer.OrdinalIgnoreCase.Equals(selected, path)) selection = index;
                }
                trafficGrid.ClearSelection();
                if (selection >= 0) trafficGrid.Rows[selection].Selected = true;
                if (scroll > 0 && scroll < trafficGrid.RowCount) trafficGrid.FirstDisplayedScrollingRowIndex = scroll;
            }
            finally { trafficGrid.ResumeLayout(); }
            if (rows.Length == 0 && meter != null)
                trafficStatus.Text = meter.Status + " · ожидаем сетевую активность";
        }

        private static string Rate(long bytes) { return Bytes(bytes / (double)TrafficMeter.RateSeconds) + "/с"; }

        private static string Bytes(double amount)
        {
            string[] units = { "Б", "КиБ", "МиБ", "ГиБ", "ТиБ" };
            int unit = 0;
            while (amount >= 1024 && unit < units.Length - 1)
            {
                amount /= 1024;
                unit++;
            }
            return amount.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        private void StartTraffic()
        {
            if (elevated)
            {
                EnableMeter();
                RefreshTraffic();
                return;
            }
            try
            {
                dirty = true;
                SaveState();
                using (Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--handoff")
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory })) { }
                Exit();
            }
            catch (Win32Exception e)
            {
                meterError = e.NativeErrorCode == 1223 ? "Запрос на запуск от администратора отменён."
                    : "Не удалось запустить учёт. " + e.Message;
                RefreshTraffic();
            }
        }

        private void EnableMeter()
        {
            if (meter != null)
            {
                if (!meter.HasFailed) return;
                meter.Dispose();
                CollectTraffic();
                meter = null;
            }
            try
            {
                meter = new TrafficMeter(userSid);
                meterError = null;
            }
            catch (Win32Exception e) { meterError = "Не удалось включить учёт ETW. " + e.Message; }
        }

        private void RefreshJournal()
        {
            lastJournalMinute = DateTime.Now.Minute;
            journalStatus.Text = state.Outages.Count == 0
                ? "Сбоев пока не обнаружено. Журнал пополняется во время работы приложения."
                : "Время обнаружения указано по часам компьютера.";
            journalGrid.Rows.Clear();
            foreach (var item in state.Outages.AsEnumerable().Reverse())
            {
                journalGrid.Rows.Add(LocalStamp(item.Start), EndLabel(item), Duration(item), StageLabel(item));
            }
            journalGrid.ClearSelection();
        }

        private static string EndLabel(Outage outage)
        {
            if (outage.End.HasValue) return LocalStamp(outage.End.Value);
            return outage.Interrupted ? "Наблюдение прервано" : "Сбой продолжается";
        }

        private static string StageLabel(Outage outage)
        {
            return outage.FirstStage == outage.LastStage ? outage.FirstStage
                : outage.FirstStage + " → " + outage.LastStage;
        }

        private static string Duration(Outage outage)
        {
            TimeSpan length = outage.DurationEnd - outage.Start;
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            string value = length.TotalHours >= 1 ? ((long)length.TotalHours) + " ч " + length.Minutes + " мин"
                : length.Minutes > 0 ? length.Minutes + " мин " + length.Seconds + " с" : length.Seconds + " с";
            return outage.End.HasValue ? value : "≥ " + value;
        }

        private static string LocalStamp(DateTime time)
        {
            return time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void Export()
        {
            using (var dialog = new SaveFileDialog { Title = "Отчёт о подключении", Filter = "Текстовый файл (*.txt)|*.txt",
                FileName = "internet-report-" + StateFile.LocalDay(DateTime.UtcNow) + ".txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var text = new StringBuilder();
                text.AppendLine("Отчёт о подключении · " + LocalStamp(DateTime.UtcNow));
                text.AppendLine("Часовой пояс: " + TimeZoneInfo.Local.DisplayName);
                text.AppendLine();
                text.AppendLine("Последняя проверка");
                if (latest != null)
                {
                    text.AppendLine(LocalStamp(latest.At) + " · " + latest.Headline);
                    text.AppendLine("Wi-Fi: " + latest.Wifi);
                    text.AppendLine("Шлюз: " + latest.Router);
                    text.AppendLine("Интернет: " + latest.Internet);
                }
                else text.AppendLine("Результат недоступен.");
                text.AppendLine();
                text.AppendLine("Обнаружен сбой | Восстановление | Длительность | Этап");
                if (state.Outages.Count == 0) text.AppendLine("За время наблюдения сбоев не обнаружено.");
                foreach (var item in state.Outages.AsEnumerable().Reverse())
                    text.AppendLine(string.Join(" | ", LocalStamp(item.Start), EndLabel(item), Duration(item), StageLabel(item)));
                try
                {
                    File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
                    journalStatus.Text = "Отчёт сохранён.";
                }
                catch (Exception e) { ShowError("Не удалось сохранить отчёт.", e); }
            }
        }

        private bool ReadDue(out DateTime date)
        {
            return DateTime.TryParseExact(state.PaymentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date) && date >= DateTimePicker.MinimumDateTime &&
                date <= DateTimePicker.MaximumDateTime;
        }

        private void SaveReminder()
        {
            string date = due.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int ahead = decimal.ToInt32(lead.Value);
            if (state.PaymentDate != date || state.LeadDays != ahead) state.LastNotice = null;
            state.PaymentDate = date;
            state.LeadDays = ahead;
            dirty = true;
            CheckReminder();
            SaveState();
        }

        private void ClearReminder()
        {
            state.PaymentDate = state.LastNotice = null;
            dirty = true;
            CheckReminder();
            SaveState();
        }

        private void CheckReminder()
        {
            DateTime date;
            if (!ReadDue(out date))
            {
                notice.Text = "Напоминание не задано";
                return;
            }
            int remaining = (date.Date - DateTime.Today).Days;
            string when = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            notice.Text = remaining > 0 ? "Следующая оплата · " + when :
                remaining == 0 ? "Дата оплаты сегодня" : "Дата оплаты прошла · " + when;
            if (remaining > state.LeadDays) return;
            string phase = remaining < 0 ? "late" : remaining == 0 ? "today" : "ahead";
            string key = state.PaymentDate + "|" + phase;
            if (state.LastNotice == key) return;
            tray.ShowBalloonTip(7000, "Оплата интернета", notice.Text, ToolTipIcon.Info);
            state.LastNotice = key;
            dirty = true;
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(StartupKey))
                    return key != null && String.Equals(key.GetValue(StartupValue) as string, StartupCommand(),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        private static string StartupCommand() { return "\"" + Application.ExecutablePath + "\" --tray"; }

        private void SetStartup()
        {
            if (updatingStartup) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(StartupKey))
                {
                    if (startup.Checked) key.SetValue(StartupValue, StartupCommand());
                    else key.DeleteValue(StartupValue, false);
                }
            }
            catch (Exception e)
            {
                updatingStartup = true;
                startup.Checked = IsStartupEnabled();
                updatingStartup = false;
                ShowError("Не удалось изменить параметры автозапуска.", e);
            }
        }

        private void SaveState()
        {
            if (!storageAvailable) return;
            lastWrite = DateTime.UtcNow;
            try
            {
                StateFile.Save(state);
                dirty = false;
                storageError = null;
            }
            catch (Exception e) { storageError = "Не удалось сохранить данные. " + e.Message; }
        }

        private void ShowError(string message, Exception error)
        {
            MessageBox.Show(this, message + Environment.NewLine + error.Message, Program.Title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OpenWindow()
        {
            if (exiting) return;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void Stop()
        {
            exiting = true;
            checkTimer.Stop();
            updateTimer.Stop();
            SystemEvents.PowerModeChanged -= HandlePower;
            if (meter != null) meter.Dispose();
            CollectTraffic();
            state.BreakObservation();
            SaveState();
            tray.Visible = false;
        }

        private void Exit()
        {
            if (exiting) return;
            Stop();
            Close();
            Application.ExitThread();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            else if (!exiting)
            {
                Stop();
                Application.ExitThread();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                checkTimer.Dispose();
                updateTimer.Dispose();
                if (tray != null) tray.Dispose();
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
