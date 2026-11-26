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
        private static readonly Color Background = Color.FromArgb(243, 242, 235);
        private static readonly Color Surface = Color.FromArgb(233, 233, 225);
        private static readonly Color Raised = Color.FromArgb(222, 223, 214);
        private static readonly Color TextColor = Color.FromArgb(23, 26, 22);
        private static readonly Color Muted = Color.FromArgb(88, 91, 81);
        private static readonly Color Rule = Color.FromArgb(201, 202, 191);
        private static readonly Color Dark = Color.FromArgb(23, 26, 22);
        private static readonly Color Light = Color.FromArgb(243, 242, 235);
        private static readonly Color Warning = Color.FromArgb(159, 50, 37);
        private const double SlideMilliseconds = 300;
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValue = "InternetAgain";

        private readonly System.Windows.Forms.Timer checkTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly System.Windows.Forms.Timer motionTimer = new System.Windows.Forms.Timer { Interval = 16 };
        private readonly System.Windows.Forms.Timer elapsedTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch motionClock = Stopwatch.StartNew();
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip trayMenu;
        private readonly bool elevated = TrafficMeter.IsElevated();
        private readonly string userSid;
        private readonly AppState state;
        private readonly bool storageAvailable;
        private readonly Panel content;
        private readonly Panel[] pages = new Panel[4];
        private readonly Button[] tabs = new Button[4];
        private readonly RevealRule[] pageRules = new RevealRule[4];
        private readonly Label footer;
        private readonly DisplayText headline, outageClock;
        private readonly Label detail, lastCheck, wifi, link, router, internet, quality, outageLabel, outageStart;
        private readonly TabRail tabRail;
        private readonly RouteGraphic routeGraphic;
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
        private int selectedPage = -1, outgoingPage = -1;
        private double transitionStarted, checkStarted, faultStarted;
        private double? ruleStarted;
        private bool allowMotion;
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
            ClientSize = new Size(1080, 635);
            MinimumSize = new Size(800, 600);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Background;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 10);
            Padding = new Padding(36, 6, 36, 10);
            allowMotion = Motion.EnabledInWindows();

            var shell = Layout(59, 14, -1, 28);
            Controls.Add(shell);
            var navigation = Layout(-1, 11);
            var navigationButtons = Columns(4);
            string[] tabNames = { "Подключение", "Трафик", "Журнал", "Оплата" };
            for (int i = 0; i < tabs.Length; i++)
            {
                int page = i;
                tabs[i] = ActionButton(tabNames[i], delegate { ShowPage(page); });
                tabs[i].BackColor = Background;
                tabs[i].ForeColor = Muted;
                tabs[i].FlatAppearance.MouseOverBackColor = Surface;
                tabs[i].FlatAppearance.MouseDownBackColor = Raised;
                tabs[i].TextAlign = ContentAlignment.MiddleLeft;
                tabs[i].Margin = Padding.Empty;
                navigationButtons.Controls.Add(tabs[i], i, 0);
            }
            navigation.Controls.Add(navigationButtons, 0, 0);
            tabRail = new TabRail(tabs.Length, Rule) { Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = Background, ForeColor = TextColor };
            navigation.Controls.Add(tabRail, 0, 1);
            shell.Controls.Add(navigation, 0, 0);
            content = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            shell.Controls.Add(content, 0, 2);
            footer = Caption("", 9, Muted, false);
            footer.AutoEllipsis = true;
            shell.Controls.Add(footer, 0, 3);

            var connection = Layout(-1, 1, 66, 106, 1, 62);
            var hero = Split(70);
            var summary = Layout(33, -1, 65);
            summary.Margin = new Padding(0, 8, 31, 14);
            summary.Controls.Add(Caption("ДИАГНОСТИКА ПОДКЛЮЧЕНИЯ", 9, Muted, false), 0, 0);
            headline = new DisplayText("ПРОВЕРЯЕМ\nПОДКЛЮЧЕНИЕ", new Font("Arial Black", 68), 1.4f)
                { ForeColor = TextColor };
            summary.Controls.Add(headline, 0, 1);
            detail = Caption("Проверяем адаптер, шлюз и доступность сайтов.", 12, Muted, false);
            detail.AutoEllipsis = true;
            summary.Controls.Add(detail, 0, 2);
            hero.Controls.Add(summary, 0, 0);

            var elapsed = new DraftingPanel();
            var elapsedContent = Layout(42, -1, 52);
            elapsedContent.Padding = new Padding(19, 13, 12, 13);
            elapsedContent.BackColor = Color.Transparent;
            outageLabel = Caption("ДЛИТЕЛЬНОСТЬ СБОЯ", 9, Muted, true);
            outageLabel.TextAlign = ContentAlignment.BottomLeft;
            outageClock = new DisplayText("—", new Font("Arial", 73), 1.15f)
                { ForeColor = TextColor, BackColor = Color.Transparent };
            outageStart = Caption("Ожидаем результат проверки", 10, Muted, false);
            outageStart.AutoEllipsis = true;
            foreach (var label in new[] { outageLabel, outageStart }) label.BackColor = Color.Transparent;
            elapsedContent.Controls.Add(outageLabel, 0, 0);
            elapsedContent.Controls.Add(outageClock, 0, 1);
            elapsedContent.Controls.Add(outageStart, 0, 2);
            elapsed.Controls.Add(elapsedContent);
            hero.Controls.Add(elapsed, 1, 0);
            connection.Controls.Add(hero, 0, 0);
            connection.Controls.Add(Divider(), 0, 1);

            routeGraphic = new RouteGraphic(TextColor, Rule, Warning) { Dock = DockStyle.Fill, BackColor = Background,
                Margin = Padding.Empty };
            connection.Controls.Add(routeGraphic, 0, 2);
            var stages = Columns(3);
            stages.Controls.Add(Stage("Компьютер", out wifi, out link), 0, 0);
            stages.Controls.Add(Stage("Роутер", out router, out quality), 1, 0);
            Label internetHint;
            stages.Controls.Add(Stage("Интернет", out internet, out internetHint), 2, 0);
            internetHint.Text = "Контрольные сайты по HTTPS";
            connection.Controls.Add(stages, 0, 3);
            connection.Controls.Add(Divider(), 0, 4);

            var actions = Split(100, 365);
            checkButton = ActionButton("Проверить сейчас   ↗", RunCheck);
            checkButton.Dock = DockStyle.Left;
            checkButton.Width = 192;
            checkButton.Margin = new Padding(0, 11, 0, 9);
            lastCheck = Caption("Ожидаем результат", 9, Muted, false);
            lastCheck.TextAlign = ContentAlignment.MiddleRight;
            actions.Controls.Add(checkButton, 0, 0);
            actions.Controls.Add(lastCheck, 1, 0);
            connection.Controls.Add(actions, 0, 5);
            pages[0] = Wrap(connection);

            var traffic = Layout(70, 2, 39, -1, 35);
            var trafficTop = Split(100, 229);
            trafficTop.Controls.Add(Caption("Трафик по программам", 30, TextColor, true), 0, 0);
            trafficButton = ActionButton(elevated ? "Включить учёт" : "Включить учёт трафика", StartTraffic);
            trafficButton.Margin = new Padding(0, 12, 0, 14);
            trafficTop.Controls.Add(trafficButton, 1, 0);
            trafficStatus = Caption("", 10, Muted, false);
            trafficGrid = Grid(new[] { "Программа", "↓ Сейчас", "↑ Сейчас", "↓ За день", "↑ За день" },
                new float[] { 37, 16, 16, 16, 15 });
            traffic.Controls.Add(trafficTop, 0, 0);
            traffic.Controls.Add(PageRule(1), 0, 1);
            traffic.Controls.Add(trafficStatus, 0, 2);
            traffic.Controls.Add(trafficGrid, 0, 3);
            traffic.Controls.Add(Caption("↓ Получено   ·   ↑ Отправлено   ·   Учтено за местные сутки", 9, Muted, false), 0, 4);
            pages[1] = Wrap(traffic);

            var journal = Layout(70, 2, 39, -1);
            var journalTop = Split(100, 186);
            journalTop.Controls.Add(Caption("Журнал отключений", 30, TextColor, true), 0, 0);
            var export = ActionButton("Сохранить отчёт   ↗", Export);
            export.Margin = new Padding(0, 12, 0, 14);
            journalTop.Controls.Add(export, 1, 0);
            journalStatus = Caption("", 10, Muted, false);
            journalGrid = Grid(new[] { "Обнаружен сбой", "Восстановление", "Длительность", "Этап сбоя" },
                new float[] { 26, 29, 17, 28 });
            journal.Controls.Add(journalTop, 0, 0);
            journal.Controls.Add(PageRule(2), 0, 1);
            journal.Controls.Add(journalStatus, 0, 2);
            journal.Controls.Add(journalGrid, 0, 3);
            pages[2] = Wrap(journal);

            var payment = Split(60);
            var paymentForm = Layout(62, 69, 69, 49, 52, -1);
            paymentForm.Padding = new Padding(0, 0, 18, 0);
            paymentForm.Controls.Add(Caption("Оплата интернета", 30, TextColor, true), 0, 0);
            due = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy",
                Font = new Font("Segoe UI", 12), Width = 205 };
            DateTime saved;
            due.Value = ReadDue(out saved) ? saved : DateTime.Today.AddDays(7);
            paymentForm.Controls.Add(PaymentField("Дата оплаты", due), 0, 1);
            lead = new NumericUpDown { Minimum = 0, Maximum = 30, Value = state.LeadDays, Width = 100,
                Font = new Font("Segoe UI", 12), BackColor = Surface, ForeColor = TextColor };
            paymentForm.Controls.Add(PaymentField("За сколько дней напомнить", lead), 0, 2);
            var paymentActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var save = ActionButton("Сохранить дату", SaveReminder);
            var clear = ActionButton("Убрать напоминание", ClearReminder);
            save.Dock = clear.Dock = DockStyle.None;
            save.Size = new Size(154, 38);
            clear.Size = new Size(185, 38);
            clear.BackColor = Surface;
            clear.ForeColor = TextColor;
            clear.FlatAppearance.BorderSize = 1;
            clear.FlatAppearance.BorderColor = Rule;
            clear.FlatAppearance.MouseOverBackColor = Raised;
            paymentActions.Controls.AddRange(new Control[] { save, clear });
            paymentForm.Controls.Add(paymentActions, 0, 3);
            startup = new CheckBox { Text = "Запускать в области уведомлений при входе в Windows",
                ForeColor = TextColor, Dock = DockStyle.Fill, Checked = IsStartupEnabled() };
            startup.CheckedChanged += delegate { SetStartup(); };
            paymentForm.Controls.Add(startup, 0, 4);
            payment.Controls.Add(paymentForm, 0, 0);
            var paymentSummary = Layout(42, 2, -1);
            paymentSummary.Padding = new Padding(18, 10, 18, 15);
            paymentSummary.BackColor = Surface;
            paymentSummary.Controls.Add(Caption("НАПОМИНАНИЕ ОБ ОПЛАТЕ", 9, Muted, true), 0, 0);
            notice = Caption("", 23, TextColor, true);
            notice.TextAlign = ContentAlignment.TopLeft;
            notice.Padding = new Padding(0, 22, 0, 0);
            paymentSummary.Controls.Add(PageRule(3), 0, 1);
            paymentSummary.Controls.Add(notice, 0, 2);
            payment.Controls.Add(paymentSummary, 1, 0);
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
            motionTimer.Tick += delegate { DrawMotion(); };
            elapsedTimer.Tick += delegate { RenderOutageClock(); };
            content.SizeChanged += delegate { ArrangePages(); };
            VisibleChanged += delegate { SyncAnimation(); };
            Resize += delegate { SyncAnimation(); };
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

        private static TableLayoutPanel Split(float leftPercent, int rightWidth = 0)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPercent));
            table.ColumnStyles.Add(rightWidth > 0 ? new ColumnStyle(SizeType.Absolute, rightWidth)
                : new ColumnStyle(SizeType.Percent, 100 - leftPercent));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return table;
        }

        private static TableLayoutPanel Columns(int count)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = count, RowCount = 1,
                Margin = Padding.Empty };
            for (int i = 0; i < count; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return table;
        }

        private RevealRule PageRule(int page)
        {
            var rule = new RevealRule { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Rule,
                ForeColor = TextColor };
            pageRules[page] = rule;
            return rule;
        }

        private static Panel Divider()
        {
            return new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Rule };
        }

        private sealed class DraftingPanel : BufferedPanel
        {
            public DraftingPanel()
            {
                Dock = DockStyle.Fill;
                Margin = new Padding(0, 20, 0, 18);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                int width = ClientSize.Width - 1;
                int height = ClientSize.Height - 1;
                if (width < 2 || height < 2) return;
                using (var guide = new Pen(Color.FromArgb(226, 226, 216)))
                {
                    e.Graphics.DrawLine(guide, 0, 0, 0, height);
                    e.Graphics.DrawLine(guide, 0, height, width, 0);
                }
            }
        }

        private static Panel Wrap(Control control)
        {
            var panel = new BufferedPanel { Visible = false, BackColor = Background, Margin = Padding.Empty };
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
                ForeColor = Light, BackColor = Dark, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(57, 59, 52);
            button.Click += delegate { action(); };
            return button;
        }

        private static Control Stage(string title, out Label value, out Label note)
        {
            var row = Layout(36, 30, -1);
            row.Margin = new Padding(32, 0, 10, 0);
            var heading = Caption(title, 16, TextColor, true);
            value = Caption("Ожидаем проверку", 11, Muted, false);
            value.AutoEllipsis = true;
            note = Caption("", 9, Muted, false);
            note.AutoEllipsis = true;
            row.Controls.Add(heading, 0, 0);
            row.Controls.Add(value, 0, 1);
            row.Controls.Add(note, 0, 2);
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
                RowHeadersVisible = false, ColumnHeadersHeight = 38, GridColor = Rule, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                BorderStyle = BorderStyle.None, BackgroundColor = Surface, EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Dark, ForeColor = Light,
                SelectionBackColor = Dark, SelectionForeColor = Light, Padding = new Padding(6, 0, 3, 0) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = TextColor,
                SelectionBackColor = Dark, SelectionForeColor = Light, Padding = new Padding(6, 0, 3, 0) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Background;
            grid.RowTemplate.Height = 31;
            foreach (string heading in headings)
            {
                int i = grid.Columns.Count;
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = heading, FillWeight = widths[i],
                    SortMode = DataGridViewColumnSortMode.NotSortable });
            }
            return grid;
        }

        private bool WindowCanAnimate
        {
            get { return Visible && WindowState != FormWindowState.Minimized && !exiting; }
        }

        private void ShowPage(int page)
        {
            if (page == selectedPage) return;
            int previous = selectedPage;
            FinishTransition();
            FinishRule();
            selectedPage = page;
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].ForeColor = i == page ? TextColor : Muted;
                tabs[i].AccessibleDescription = i == page ? "Текущий раздел" : null;
            }

            if (allowMotion && WindowCanAnimate && previous >= 0 && content.ClientSize.Width > 0)
            {
                outgoingPage = previous;
                transitionStarted = motionClock.Elapsed.TotalMilliseconds;
                ArrangePages();
                pages[page].BringToFront();
                pages[page].Visible = true;
            }
            else FinishTransition();

            if (page == 0) RenderOutageClock();
            if (page == 1) RefreshTraffic();
            if (page == 2) RefreshJournal();
            if (previous >= 0) HighlightPage();
            SyncAnimation();
        }

        private void FinishTransition()
        {
            if (selectedPage < 0) return;
            outgoingPage = -1;
            for (int i = 0; i < pages.Length; i++)
                pages[i].Visible = i == selectedPage;
            pages[selectedPage].Bounds = content.ClientRectangle;
            pages[selectedPage].BringToFront();
            tabRail.Position(selectedPage, 1);
        }

        private void ArrangePages()
        {
            if (selectedPage < 0) return;
            Rectangle area = content.ClientRectangle;
            if (outgoingPage < 0)
            {
                pages[selectedPage].Bounds = area;
                return;
            }

            double progress = Math.Min(1, (motionClock.Elapsed.TotalMilliseconds - transitionStarted) / SlideMilliseconds);
            if (progress >= 1)
            {
                FinishTransition();
                return;
            }
            double eased = Motion.Ease(progress);
            int direction = selectedPage > outgoingPage ? 1 : -1;
            int width = area.Width;
            int exited = (int)Math.Round(width * eased);
            pages[outgoingPage].Bounds = new Rectangle(-exited * direction, 0, width, area.Height);
            pages[selectedPage].Bounds = new Rectangle((width - exited) * direction, 0, width, area.Height);
            tabRail.Position(outgoingPage + (selectedPage - outgoingPage) * eased,
                1 - 0.63 * Math.Sin(Math.PI * progress));
        }

        private void HighlightPage()
        {
            if (selectedPage < 1 || !allowMotion || !WindowCanAnimate) return;
            ruleStarted = motionClock.Elapsed.TotalMilliseconds;
            pageRules[selectedPage].Reveal(0);
            SyncAnimation();
        }

        private void FinishRule()
        {
            if (!ruleStarted.HasValue) return;
            ruleStarted = null;
            pageRules[selectedPage].Reveal(1);
        }

        private void SyncAnimation()
        {
            bool visible = WindowCanAnimate;
            bool routeVisible = visible && selectedPage == 0;
            elapsedTimer.Enabled = routeVisible;
            if (!visible || !allowMotion)
            {
                if (outgoingPage >= 0) FinishTransition();
                FinishRule();
                routeGraphic.SetMotion(0, null);
            }
            motionTimer.Enabled = visible && allowMotion &&
                (outgoingPage >= 0 || ruleStarted.HasValue || (routeVisible && (checking || routeGraphic.HasFailure)));
        }

        private void DrawMotion()
        {
            if (!WindowCanAnimate || !allowMotion)
            {
                SyncAnimation();
                return;
            }
            if (outgoingPage >= 0) ArrangePages();
            if (ruleStarted.HasValue)
            {
                double progress = Math.Min(1, (motionClock.Elapsed.TotalMilliseconds - ruleStarted.Value) / 460);
                if (progress >= 1) FinishRule();
                else pageRules[selectedPage].Reveal(Motion.Ease(progress));
            }
            if (selectedPage == 0 && (checking || routeGraphic.HasFailure))
            {
                double now = motionClock.Elapsed.TotalMilliseconds;
                double pulse = routeGraphic.HasFailure ? ((now - faultStarted) % 1650) / 1650 : 0;
                double? scan = checking && !routeGraphic.HasFailure ?
                    (double?)Motion.Ease(((now - checkStarted) % 1100) / 1100) : null;
                routeGraphic.SetMotion(pulse, scan);
            }
            else if (outgoingPage < 0 && !ruleStarted.HasValue) motionTimer.Stop();
        }

        private async void RunCheck()
        {
            if (checking || exiting) return;
            checking = true;
            checkButton.Enabled = false;
            checkButton.Text = "Проверяем…";
            checkStarted = motionClock.Elapsed.TotalMilliseconds;
            SyncAnimation();
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
                if (latest == null || latest.Fault != result.Fault)
                    faultStarted = motionClock.Elapsed.TotalMilliseconds;
                latest = result;
                RenderConnection();
                if (Visible && selectedPage == 2) RefreshJournal();
            }
            catch (Exception e)
            {
                if (exiting) return;
                state.BreakObservation();
                dirty = true;
                headline.Text = "ОШИБКА\nПРОВЕРКИ";
                detail.Text = e.Message;
                latest = null;
                routerSamples.Clear();
                wifi.Text = router.Text = internet.Text = "Нет данных";
                wifi.ForeColor = router.ForeColor = internet.ForeColor = Muted;
                link.Text = quality.Text = "";
                lastCheck.Text = "Повторим автоматически";
                routeGraphic.SetResult(null, null);
                RenderOutageClock();
            }
            finally
            {
                checking = false;
                if (!exiting)
                {
                    checkButton.Text = "Проверить сейчас   ↗";
                    checkButton.Enabled = true;
                    if (!routeGraphic.HasFailure) routeGraphic.SetMotion(0, null);
                    SyncAnimation();
                }
            }
        }

        private void RenderConnection()
        {
            if (latest.Online == true) headline.Text = "ИНТЕРНЕТ\nРАБОТАЕТ";
            else if (latest.Fault == RouteStep.Computer) headline.Text = "НЕТ\nПОДКЛЮЧЕНИЯ";
            else if (latest.Fault == RouteStep.Gateway)
                headline.Text = latest.GatewayTested ? "ШЛЮЗ НЕ\nОТВЕЧАЕТ" : "НЕТ\nМАРШРУТА";
            else headline.Text = latest.Online == false ? "ИНТЕРНЕТ\nНЕДОСТУПЕН" : "ОШИБКА\nПРОВЕРКИ";
            detail.Text = latest.Detail;
            lastCheck.Text = "Последняя проверка · " + latest.At.ToLocalTime().ToString("HH:mm:ss");
            wifi.Text = "Wi-Fi · " + latest.Wifi;
            link.Text = "Активный адаптер · " + latest.Link;
            router.Text = latest.Router;
            internet.Text = latest.Internet;
            wifi.ForeColor = latest.Fault == RouteStep.Computer ? Warning : TextColor;
            router.ForeColor = latest.Fault == RouteStep.Gateway ? Warning : TextColor;
            internet.ForeColor = latest.Fault == RouteStep.Internet ? Warning : Muted;
            quality.Text = routerSamples.Count == 0 ? "" : "Без ответа: " +
                routerSamples.Count(success => !success) + " из " + routerSamples.Count + " ping";
            routeGraphic.SetResult(latest.Online, latest.Fault);
            RenderOutageClock();
            tray.Text = latest.Online == true ? "Интернет работает" : Program.Title;
        }

        private void RenderOutageClock()
        {
            var current = state.Outages.LastOrDefault();
            bool active = current != null && !current.End.HasValue && !current.Interrupted;
            if (!active)
            {
                outageClock.Text = "—";
                outageStart.Text = latest == null || !latest.Online.HasValue ? "Ожидаем результат проверки" :
                    "Активного сбоя нет";
                return;
            }

            TimeSpan duration = DateTime.UtcNow - current.Start;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            string minutes = duration.Minutes.ToString("00", CultureInfo.InvariantCulture);
            string seconds = duration.Seconds.ToString("00", CultureInfo.InvariantCulture);
            outageClock.Text = duration.TotalHours < 1 ? minutes + ":" + seconds :
                ((long)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + minutes + ":" + seconds;
            outageStart.Text = "Обнаружен в " + current.Start.ToLocalTime().ToString("HH:mm:ss");
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
                    RenderOutageClock();
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
            bool windowsMotion = Motion.EnabledInWindows();
            if (windowsMotion != allowMotion)
            {
                allowMotion = windowsMotion;
                SyncAnimation();
            }
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
                    HighlightPage();
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
            HighlightPage();
        }

        private void ClearReminder()
        {
            state.PaymentDate = state.LastNotice = null;
            dirty = true;
            CheckReminder();
            SaveState();
            HighlightPage();
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
            motionTimer.Stop();
            elapsedTimer.Stop();
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
                motionTimer.Dispose();
                elapsedTimer.Dispose();
                if (tray != null) tray.Dispose();
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
