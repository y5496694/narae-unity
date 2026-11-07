using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace NaraeUnity;

internal sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(13, 19, 30), Surface = Color.FromArgb(22, 31, 46), Border = Color.FromArgb(44, 58, 77);
    static readonly Color Ink = Color.FromArgb(227, 236, 248), Muted = Color.FromArgb(145, 164, 189), Accent = Color.FromArgb(83, 225, 180), Amber = Color.FromArgb(255, 190, 91);
    readonly TextBox _project = Field(), _editor = Field();
    readonly Label _state = Label("연결을 확인하고 있습니다", 10, Muted), _title = Label("프로젝트", 23, Ink, true);
    readonly Label _stats = Label("", 11, Muted), _stageDetail = Label("구간을 선택하면 설계 정보를 볼 수 있어요.", 10, Muted);
    readonly Label _bottom = Label("준비", 10, Muted), _capabilities = Label("", 12, Ink);
    readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Bg };
    readonly RichTextBox _logs = EditorText(), _evidence = EditorText();
    readonly DataGridView _stages = new();
    readonly Button _cancel = Button("작업 중지", false), _validate = Button("Unity 씬 검사", false), _export = Button("독립 맵 내보내기", true), _render = Button("Unity 렌더", false);
    readonly List<Control> _locking = new();
    readonly WorkbenchTabs _tabs = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1200 };
    ProjectSnapshot _snapshot = new();
    CancellationTokenSource? _running;
    bool _closeAfterRun;
    string _activeLog = "", _lastUnityLogText = "", _sessionLog = "";

    public MainForm()
    {
        Text = "나래 유니티 0.2  |  Narae Unity";
        ClientSize = new Size(1460, 980); MinimumSize = new Size(1120, 790);
        BackColor = Bg; ForeColor = Ink; Font = new Font("맑은 고딕", 10);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 16, 26, 12), ColumnCount = 1, RowCount = 6, BackColor = Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 89));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 111));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 63));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var brand = Label("나래 유니티", 27, Ink, true); brand.Location = new Point(0, 0); brand.AutoSize = true;
        var en = Label("NARAE UNITY 0.2  /  INDEPENDENT MAP EXPORT", 9, Accent, true); en.Location = new Point(3, 52); en.AutoSize = true;
        _state.AutoSize = true; _state.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        header.Controls.AddRange(new Control[] { brand, en, _state });
        header.Resize += (_, _) => _state.Location = new Point(Math.Max(430, header.Width - _state.PreferredWidth - 5), 19);
        root.Controls.Add(header, 0, 0);

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(14, 10, 14, 9), ColumnCount = 4, RowCount = 2 };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 93)); paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 107));
        paths.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); paths.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddPathRow(paths, 0, "프로젝트", _project, BrowseProject);
        AddPathRow(paths, 1, "Unity 에디터", _editor, BrowseEditor);
        var save = Button("설정 저장", false); save.Click += (_, _) => SaveAndRefresh();
        var detect = Button("자동 찾기", false); detect.Click += (_, _) => { _editor.Text = Settings.Defaults().EditorPath; SaveAndRefresh(); };
        paths.Controls.Add(save, 3, 0); paths.Controls.Add(detect, 3, 1);
        _locking.AddRange(new Control[] { _project, _editor, save, detect });
        root.Controls.Add(paths, 0, 1);

        var summary = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _title.AutoSize = true; _title.Location = new Point(0, 13);
        _stats.AutoSize = true; _stats.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        summary.Controls.AddRange(new Control[] { _title, _stats });
        summary.Resize += (_, _) => _stats.Location = new Point(Math.Max(300, summary.Width - _stats.PreferredWidth), 24);
        root.Controls.Add(summary, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, WrapContents = false, Padding = new Padding(0, 3, 0, 3) };
        var open = Button("Unity 전체 에디터 열기", false); open.Width = 211;
        open.Click += (_, _) => OpenEditor();
        _validate.Click += async (_, _) => await RunAsync("Validate");
        _export.Click += async (_, _) => await RunAsync("Export");
        _render.Click += async (_, _) => await RunAsync("Render");
        _cancel.Click += (_, _) => _running?.Cancel(); _cancel.Enabled = false;
        var refresh = Button("새로고침", false); refresh.Click += (_, _) => RefreshProject();
        actions.Controls.AddRange(new Control[] { open, _validate, _export, _render, _cancel, refresh });
        _locking.AddRange(new Control[] { open, _validate, _export, _render, refresh });
        root.Controls.Add(actions, 0, 3);

        Panel sceneTab = Tab("씬 · 구간"), exportTab = Tab("빌드 · 상태"), reaTab = Tab("REA 분석"), logTab = Tab("실행 로그");
        _tabs.AddPages(new[] { sceneTab, exportTab, reaTab, logTab });
        root.Controls.Add(_tabs, 0, 4);
        BuildSceneTab(sceneTab); BuildExportTab(exportTab);
        var reaTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 53, BackColor = Surface, Padding = new Padding(10, 5, 0, 0) };
        var refreshEvidence = Button("분석 결과 다시 읽기", false); refreshEvidence.Width = 190; refreshEvidence.Click += (_, _) => RefreshEvidence();
        reaTop.Controls.Add(refreshEvidence); reaTab.Controls.Add(_evidence); reaTab.Controls.Add(reaTop);
        logTab.Controls.Add(_logs);
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _bottom.AutoSize = true; _bottom.Location = new Point(0, 10); footer.Controls.Add(_bottom);
        root.Controls.Add(footer, 0, 5);

        var settings = Settings.Load(); _project.Text = settings.ProjectPath; _editor.Text = settings.EditorPath;
        _timer.Tick += (_, _) => PollUnityLog();
        FormClosing += (_, e) =>
        {
            if (_running is null) return;
            e.Cancel=true; _closeAfterRun=true; _running.Cancel();
            _bottom.Text="실행 중인 작업을 종료한 뒤 프로그램을 닫습니다…";
        };
        RefreshProject(); RefreshEvidence();
        AppendLog("나래 유니티 준비. 화면 입력 없이 명령줄로 검사·내보내기를 실행합니다.");
    }

    void AddPathRow(TableLayoutPanel parent, int row, string caption, TextBox field, Action browse)
    {
        var label = Label(caption, 10, Muted); label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft;
        field.Dock = DockStyle.Fill; field.Margin = new Padding(3, 5, 8, 5);
        var button = Button("찾아보기", false); button.Click += (_, _) => browse(); _locking.Add(button);
        parent.Controls.Add(label, 0, row); parent.Controls.Add(field, 1, row); parent.Controls.Add(button, 2, row);
    }

    void BuildSceneTab(Panel page)
    {
        var split = new SplitContainer { Size = new Size(1380,600), SplitterDistance = 1000, Dock = DockStyle.Fill, BackColor = Border, SplitterWidth = 6, FixedPanel = FixedPanel.Panel2, Panel2MinSize = 310, Panel1MinSize = 420 };
        // SplitterDistance is assigned once there is enough client width.
        split.SizeChanged += (_, _) => { if (split.Width > 760) split.SplitterDistance = Math.Max(420, split.Width - 370); };
        split.Panel1.BackColor = Bg; split.Panel2.BackColor = Surface;
        var previewTop = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Surface };
        var previewLabel = Label("씬 형상 미리보기  ·  게임 캡처가 아닙니다", 9, Muted); previewLabel.AutoSize = true; previewLabel.Location = new Point(14, 12); previewTop.Controls.Add(previewLabel);
        split.Panel1.Controls.Add(_preview); split.Panel1.Controls.Add(previewTop);
        var grid = _stages; grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.RowHeadersVisible = false;
        grid.BackgroundColor = Surface; grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Border; grid.EnableHeadersVisualStyles = false; grid.AutoGenerateColumns = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Muted, Font = new Font(Font, FontStyle.Bold), SelectionBackColor = Surface };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Ink, SelectionBackColor = Color.FromArgb(30, 75, 73), SelectionForeColor = Accent, Padding = new Padding(3) };
        grid.ColumnHeadersHeight = 37; grid.RowTemplate.Height = 34;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "number", HeaderText = "#", Width = 38 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "기믹", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "height", HeaderText = "높이", Width = 53 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "save", HeaderText = "저장", Width = 60 });
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not Stage stage) return;
            _stageDetail.Text = $"{stage.Number:00}  {stage.KoreanName}\n{stage.Name}\n\n{stage.Tip}\n\n{(stage.Checkpoint ? "체크포인트에서 시작하는 구간" : "구간 내 체크포인트 없음")}\n시작 높이 {stage.Height:0.#}m\n\n설계 초안입니다. 실제 완주와 물리 동작은\nUnity 및 게임 안에서 확인해야 합니다.";
        };
        _stageDetail.Dock = DockStyle.Bottom; _stageDetail.Height = 175; _stageDetail.Padding = new Padding(13); _stageDetail.BackColor = Surface;
        split.Panel2.Controls.Add(grid); split.Panel2.Controls.Add(_stageDetail);
        page.Controls.Add(split);
    }

    void BuildExportTab(Panel page)
    {
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(25), RowCount = 4, ColumnCount = 1 };
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); container.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); container.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        var heading = Label("프로젝트를 확인하고, 게임용 맵을 내보내요", 19, Ink, true); heading.Dock = DockStyle.Fill;
        var description = Label("독립 내보내기로 게임용 맵을 생성하고, 게임에서 플레이를 확인합니다.\nUnity 씬 검사·렌더는 별도의 에디터 기능입니다. 이 프로그램은 Unity 라이선스나 기능 잠금을 변경하지 않습니다.", 11, Muted); description.Dock = DockStyle.Fill;
        _capabilities.Dock = DockStyle.Fill; _capabilities.Padding = new Padding(3, 16, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, WrapContents = false };
        var output = Button("내보낸 폴더 열기", false); output.Width = 190; output.Click += (_, _) => Reveal(_snapshot.OutputPath);
        var scene = Button("씬 파일 위치", false); scene.Click += (_, _) => Reveal(_snapshot.ScenePath);
        var docs = Button("프로젝트 안내", false); docs.Click += (_, _) => Reveal(Path.Combine(_project.Text, "README.md"));
        buttons.Controls.AddRange(new Control[] { output, scene, docs });
        container.Controls.Add(heading, 0, 0); container.Controls.Add(description, 0, 1); container.Controls.Add(_capabilities, 0, 2); container.Controls.Add(buttons, 0, 3);
        page.Controls.Add(container);
    }

    void BrowseProject()
    {
        using var dialog = new FolderBrowserDialog { Description = "Unity 프로젝트 폴더", SelectedPath = _project.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) { _project.Text = dialog.SelectedPath; SaveAndRefresh(); }
    }

    void BrowseEditor()
    {
        using var dialog = new OpenFileDialog { Title = "Unity 에디터 실행 파일", Filter = "Unity 에디터|Unity.exe|실행 파일|*.exe", CheckFileExists = true };
        if (File.Exists(_editor.Text)) dialog.FileName = _editor.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) { _editor.Text = dialog.FileName; SaveAndRefresh(); }
    }

    void SaveAndRefresh()
    {
        try { new Settings(_project.Text.Trim(), _editor.Text.Trim()).Save(); RefreshProject(); _bottom.Text = "연결 설정을 저장했습니다."; }
        catch (Exception ex) { AppendLog(ex.Message); _bottom.Text = "설정을 저장하지 못했습니다."; }
    }

    public void RefreshProject()
    {
        _snapshot = ProjectSnapshot.Read(_project.Text);
        _title.Text = _snapshot.Title;
        _stats.Text = $"{_snapshot.Stages.Count:00} 구간    ·    {_snapshot.Height:0}m    ·    물리 {_snapshot.PhysicsBodies}개    ·    오브젝트 {_snapshot.Objects}개";
        _stats.Left = Math.Max(300, _stats.Parent!.Width - _stats.PreferredWidth);
        _state.Text = _snapshot.Error.Length > 0 ? "프로젝트를 확인해 주세요" : _snapshot.LicenseMessage;
        _state.ForeColor = _snapshot.Error.Length > 0 ? Amber : _snapshot.HasIndependentExporter ? Accent : _snapshot.LicenseBlocked ? Amber : Muted;
        _state.Left = Math.Max(430, _state.Parent!.Width - _state.PreferredWidth - 5);
        _preview.Image?.Dispose(); _preview.Image = null;
        if (File.Exists(_snapshot.PreviewPath))
        {
            using var stream = new FileStream(_snapshot.PreviewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var image = Image.FromStream(stream); _preview.Image = new Bitmap(image);
        }
        _stages.Rows.Clear();
        foreach (var stage in _snapshot.Stages) { var i = _stages.Rows.Add(stage.Number.ToString("00"), stage.KoreanName, $"{stage.Height:0}m", stage.Checkpoint ? "●" : ""); _stages.Rows[i].Tag = stage; }
        var editorExists = File.Exists(_editor.Text);
        _export.Text=_snapshot.HasIndependentExporter?"독립 맵 내보내기":"Unity 맵 내보내기";
        _capabilities.Text = _snapshot.Error.Length > 0 ? _snapshot.Error :
            $"{Check(editorExists)} Unity 에디터 연결                 요구 버전  {_snapshot.RequiredVersion}\n\n" +
            $"{Check(_snapshot.HasIndependentExporter)} 독립 맵 내보내기 도구                 에디터 없이 게임용 번들 생성\n\n" +
            $"{Check(_snapshot.OfflinePassed)} 오프라인 구조 검사                  {_snapshot.Objects}개 오브젝트 / {_snapshot.PhysicsBodies}개 물리 오브젝트\n\n" +
            $"{Check(_snapshot.HasUnityValidation)} Unity 임포트·씬 검사                {(_snapshot.HasUnityValidation ? "결과 파일 있음" : "실행 확인 전")}\n\n" +
            $"{Check(_snapshot.HasExport)} 게임용 맵 출력                       {(_snapshot.HasExport ? "필수 출력 파일 3개 있음" : "출력 파일 없음")}\n\n" +
            $"{Check(_snapshot.ExportReport.StructuralPassed)} 출력 번들 구조 검사                  {(_snapshot.ExportReport.StructuralPassed ? $"{_snapshot.ExportReport.SceneObjects}개 구성요소 / 물리 {_snapshot.ExportReport.PhysicsBodies}개" : "확인 전")}\n\n" +
            (_snapshot.LicenseBlocked ? "Unity 에디터 검사·렌더는 활성화가 필요합니다. 독립 내보내기는 별도로 사용할 수 있습니다.\n" : "") +
            "실제 완주와 멀티플레이 검증은 별도입니다.";
        if (_running is null) SetActionEligibility();
        _bottom.Text = _snapshot.Error.Length > 0 ? _snapshot.Error : "독립 내보내기 · Unity 에디터 검사 · 실제 게임 검증을 구분해 표시합니다";
    }

    static string Check(bool value) => value ? "●" : "○";

    public void RefreshEvidence()
    {
        var paths = new[] { "findings.json", "findings.md", "REA-findings.md", "summary.md" }.Select(n => Path.Combine(Settings.EvidenceRoot, n)).Where(File.Exists).ToArray();
        if (paths.Length == 0) { _evidence.Text = "REA 분석 결과를 준비하고 있습니다.\n\nEvidence 폴더의 findings.json 또는 findings.md를 여기에 표시합니다.\n\nREA는 분석 도구이며 Unity 전체 기능 구현이나 실행 성공을 의미하지 않습니다."; return; }
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            var text = File.ReadAllText(path);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                try { using var doc = JsonDocument.Parse(text); text = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); } catch { }
            builder.AppendLine(Path.GetFileName(path)).AppendLine(new string('─', 50)).AppendLine(text).AppendLine();
        }
        _evidence.Text = builder.ToString();
    }

    async Task RunAsync(string action)
    {
        if (_running is not null) return;
        try
        {
            var info = CommandRunner.Build(_project.Text.Trim(), _editor.Text.Trim(), action);
            new Settings(_project.Text.Trim(), _editor.Text.Trim()).Save();
            _running = new CancellationTokenSource();
            foreach (var control in _locking) control.Enabled = false;
            _cancel.Enabled = true;
            _tabs.SelectedIndex = 3;
            _activeLog = Path.Combine(_project.Text, "Build", "unity-" + action + ".log");
            _lastUnityLogText = "";
            Directory.CreateDirectory(Settings.StateRoot);
            _sessionLog = Path.Combine(Settings.StateRoot, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            AppendLog($"{action} 시작. 기존 Unity 프로세스에는 입력을 보내지 않습니다.");
            _bottom.Text = action + " 실행 중…"; _timer.Start();
            var started = DateTime.UtcNow;
            var result = await CommandRunner.RunAsync(info, line =>
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(() => AppendLog(line));
            }, _running.Token);
            result=CommandRunner.AttachFreshNativeLog(result,_activeLog,started);
            PollUnityLog(); RefreshProject();
            bool exportRefreshed = action != "Export" || Cli.HasFreshExport(_snapshot.OutputPath, started);
            var resultText = result.Cancelled ? "작업을 중지했습니다." : result.LicenseBlocked ? "Unity 라이선스 활성화가 필요합니다. 내보내기는 완료되지 않았습니다." : result.Success && exportRefreshed ? action + " 완료. 로그와 출력 파일을 확인하세요." : result.Success ? "실행은 종료됐지만 이번 내보내기의 출력 갱신을 확인하지 못했습니다." : $"작업 실패 (종료 코드 {result.ExitCode}). 실행 로그를 확인하세요.";
            AppendLog(resultText); _bottom.Text = resultText;
        }
        catch (Exception ex) { AppendLog(ex.Message); _bottom.Text = ex.Message; }
        finally
        {
            _timer.Stop(); _running?.Dispose(); _running = null; _sessionLog = "";
            foreach (var control in _locking) control.Enabled = true;
            _cancel.Enabled = false;
            SetActionEligibility();
            if (_closeAfterRun && !IsDisposed) BeginInvoke(Close);
        }
    }

    void SetActionEligibility()
    {
        bool editorExists=File.Exists(_editor.Text);
        _validate.Enabled=_render.Enabled=_snapshot.HasWrapper&&editorExists;
        _export.Enabled=_snapshot.HasWrapper&&(_snapshot.HasIndependentExporter||editorExists);
    }

    void PollUnityLog()
    {
        try
        {
            if (!File.Exists(_activeLog)) return;
            var value = ProjectSnapshot.ReadTail(_activeLog, 24);
            if (value.Length == 0 || value == _lastUnityLogText) return;
            var prior = _lastUnityLogText.Split(Environment.NewLine).LastOrDefault();
            var lines = value.Split(Environment.NewLine);
            var start = !string.IsNullOrEmpty(prior) ? Array.LastIndexOf(lines, prior) + 1 : 0;
            foreach (var line in lines.Skip(start)) AppendLog("Unity  ·  " + line);
            _lastUnityLogText = value;
        }
        catch (IOException) { }
    }

    void OpenEditor()
    {
        try
        {
            if (!File.Exists(_editor.Text)) throw new FileNotFoundException("Unity 에디터를 선택해 주세요.");
            if (!Directory.Exists(_project.Text)) throw new DirectoryNotFoundException("프로젝트를 선택해 주세요.");
            var info = new ProcessStartInfo(_editor.Text) { UseShellExecute = false, WorkingDirectory = _project.Text };
            info.ArgumentList.Add("-projectPath"); info.ArgumentList.Add(_project.Text);
            Process.Start(info); _bottom.Text = "사용자 요청으로 정식 Unity 에디터를 열었습니다.";
        }
        catch (Exception ex) { AppendLog(ex.Message); _bottom.Text = ex.Message; }
    }

    void Reveal(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("아직 파일 또는 폴더가 생성되지 않았습니다.", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog(ex.Message); _bottom.Text = ex.Message; }
    }

    void AppendLog(string value)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}]  {value}{Environment.NewLine}";
        _logs.AppendText(line); _logs.SelectionStart = _logs.TextLength; _logs.ScrollToCaret();
        if (_sessionLog.Length > 0) try { File.AppendAllText(_sessionLog, line, Encoding.UTF8); } catch (IOException) { }
    }

    static Panel Tab(string text) => new() { Text=text, BackColor = Surface, ForeColor = Ink, Padding = new Padding(1) };
    static Label Label(string text, float size, Color color, bool bold = false) => new() { Text = text, ForeColor = color, Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular), BackColor = Color.Transparent };
    static TextBox Field() => new() { BackColor = Bg, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Font = new Font("맑은 고딕", 10), Height = 31 };
    static RichTextBox EditorText() => new() { Dock = DockStyle.Fill, BackColor = Bg, ForeColor = Ink, Font = new Font("맑은 고딕", 10), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, DetectUrls = false, Padding = new Padding(14) };
    static Button Button(string text, bool primary)
    {
        var button = new Button { Text = text, Width = Math.Max(115, text.Length * 13 + 32), Height = 40, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? Bg : Ink, Cursor = Cursors.Hand, Margin = new Padding(0, 3, 9, 3), Font = new Font("맑은 고딕", 10, primary ? FontStyle.Bold : FontStyle.Regular), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = primary ? Accent : Border; button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(125, 240, 204) : Color.FromArgb(34, 49, 70);
        return button;
    }

    public void CapturePreview(string path,int tabIndex=0)
    {
        _tabs.SelectedIndex=tabIndex;
        ClientSize = new Size(1460, 980);
        CreateControl();
        void CreateAll(Control root) { foreach (Control child in root.Controls) { _=child.Handle; CreateAll(child); } root.PerformLayout(); }
        CreateAll(this); PerformLayout();
        // DrawToBitmap normally skips the child tree of a hidden top-level form.
        // Set only the managed visibility bit for this owned preview instance;
        // do not call Show(), ShowWindow, Activate(), or pump a visible window.
        var setState=typeof(Control).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).First(m=>m.Name=="SetState"&&m.GetParameters().Length==2);
        var visibleFlag=Enum.Parse(setState.GetParameters()[0].ParameterType,"Visible");
        setState.Invoke(this,new object[]{visibleFlag,true});
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        setState.Invoke(this,new object[]{visibleFlag,false});
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _preview.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class WorkbenchTabs : Panel
{
    readonly FlowLayoutPanel _bar=new(){Dock=DockStyle.Top,Height=45,WrapContents=false,BackColor=Color.FromArgb(13,19,30),Margin=Padding.Empty,Padding=Padding.Empty};
    readonly Panel _body=new(){Dock=DockStyle.Fill,BackColor=Color.FromArgb(22,31,46)};
    readonly List<Panel> _pages=new();
    readonly List<Button> _buttons=new();
    int _selected;
    public WorkbenchTabs(){BackColor=Color.FromArgb(13,19,30);Controls.Add(_body);Controls.Add(_bar);}
    public void AddPages(IEnumerable<Panel> pages)
    {
        foreach(var page in pages)
        {
            var index=_pages.Count; _pages.Add(page);page.Dock=DockStyle.Fill;page.Visible=false;_body.Controls.Add(page);
            var button=new Button {Text=page.Text,Width=155,Height=43,Margin=new Padding(0,0,3,0),FlatStyle=FlatStyle.Flat,Font=new Font("맑은 고딕",10),UseVisualStyleBackColor=false,Cursor=Cursors.Hand};
            button.FlatAppearance.BorderSize=0;
            button.Paint+=(_,e)=>{if(_selected==index){using var pen=new Pen(Color.FromArgb(83,225,180),3);e.Graphics.DrawLine(pen,12,button.Height-3,button.Width-12,button.Height-3);}};
            button.Click+=(_,_)=>SelectedIndex=index;
            _buttons.Add(button);_bar.Controls.Add(button);
        }
        SelectedIndex=0;
    }
    public int SelectedIndex
    {
        get=>_selected;
        set
        {
            if(value<0||value>=_pages.Count)throw new ArgumentOutOfRangeException(nameof(value));
            _selected=value;
            for(int i=0;i<_pages.Count;i++)
            {
                _pages[i].Visible=i==value;
                _buttons[i].BackColor=i==value?Color.FromArgb(22,31,46):Color.FromArgb(13,19,30);
                _buttons[i].ForeColor=i==value?Color.FromArgb(83,225,180):Color.FromArgb(145,164,189);
                _buttons[i].Invalidate();
            }
            _pages[value].BringToFront();
        }
    }
}
