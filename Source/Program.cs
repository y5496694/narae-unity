using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace NaraeUnity;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--help",StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
        {
            Console.WriteLine("Narae Unity 0.2\nNaraeUnity.exe --action validate|export|render [--project <path>] [--editor <Unity.exe>]\nNaraeUnity.exe --self-test\nExport uses the independent map serializer when present; no Editor is required.\nValidate and Render require a working Unity Editor.\nWithout arguments: open the desktop app.");
            return 0;
        }
        if (args.Contains("--action", StringComparer.OrdinalIgnoreCase))
            return Cli.RunAsync(args).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();
        Application.Run(new MainForm());
        return 0;
    }
}

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--action" or "--project" or "--editor"))
                    throw new ArgumentException("사용법: NaraeUnity.exe --action validate|export|render [--project 경로] [--editor Unity.exe]");
                if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("중복 인수: " + args[i]);
            }
            var settings = Settings.Load();
            var action = values.GetValueOrDefault("--action", "").ToLowerInvariant() switch
            { "validate" => "Validate", "export" => "Export", "render" => "Render", _ => throw new ArgumentException("지원 작업: validate, export, render") };
            var project = values.GetValueOrDefault("--project", settings.ProjectPath);
            var editor = values.GetValueOrDefault("--editor", settings.EditorPath);
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            var started = DateTime.UtcNow;
            var result = await CommandRunner.RunAsync(CommandRunner.Build(project, editor, action), line => Console.Error.WriteLine(line), cancel.Token);
            result = CommandRunner.AttachFreshNativeLog(result,Path.Combine(project,"Build","unity-"+action+".log"),started);
            var snapshot = ProjectSnapshot.Read(project);
            var status = Classify(result);
            bool exportRefreshed = action == "Export" && HasFreshExport(snapshot.OutputPath, started);
            if (action == "Export" && status == "completed" && !exportRefreshed) status = "output_not_refreshed";
            var report = new { status, action, project = Path.GetFullPath(project), exit_code = result.ExitCode,
                output_path = snapshot.OutputPath, unity_log = Path.Combine(project, "Build", "unity-" + action + ".log"),
                existing_export_files = snapshot.HasExport, export_created_this_run = status == "completed" && exportRefreshed,
                export_route = action == "Export" && snapshot.HasIndependentExporter ? "independent_serializer" : "unity_editor",
                structural_reparse = snapshot.ExportReport.StructuralPassed ? "passed" : "unverified",
                gameplay_verified = snapshot.ExportReport.GameplayVerified };
            Console.WriteLine(JsonSerializer.Serialize(report));
            return status == "completed" ? 0 : status == "license_blocked" ? 3 : status == "cancelled" ? 130 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "error", message = ex.Message }));
            return 2;
        }
    }

    public static string Classify(RunResult result) => result.Cancelled ? "cancelled" : result.LicenseBlocked ? "license_blocked" : result.Success ? "completed" : "failed";
    public static bool HasFreshExport(string folder, DateTime sinceUtc)
    {
        var files=new[] {"data","metadata.json","thumbnail.png"}.Select(name=>Path.Combine(folder,name)).ToArray();
        if(!files.All(File.Exists))return false;
        var report=Path.Combine(folder,"export-report.json");
        if(File.Exists(report))
        {
            if(File.GetLastWriteTimeUtc(report)<sinceUtc||File.GetLastWriteTimeUtc(files[0])<sinceUtc)return false;
            var native=NativeExportReport.Read(folder);
            if(native.StructuralPassed&&native.DataSha256.Length==64)
            {
                using var stream=File.OpenRead(files[0]);
                return Convert.ToHexString(SHA256.HashData(stream)).Equals(native.DataSha256,StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        return files.All(path=>File.GetLastWriteTimeUtc(path)>=sinceUtc);
    }
}

internal sealed record Settings(string ProjectPath, string EditorPath)
{
    public static string AppRoot => AppContext.BaseDirectory;
    public static string StateRoot => Path.Combine(AppRoot, "state");
    public static string SettingsPath => Path.Combine(StateRoot, "settings.json");
    public static string EvidenceRoot => Path.GetFullPath(Path.Combine(AppRoot, "..", "Evidence"));
    public static Settings Defaults()
    {
        var project = Path.GetFullPath(Path.Combine(AppRoot, "..", "..", "LastGripTower"));
        var workspace = Path.GetFullPath(Path.Combine(AppRoot, "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(workspace, "work", "Unity2017Portable", "Editor", "Unity.exe"),
            Path.Combine(workspace, "work", "Unity2017", "Editor", "Unity.exe"),
            @"C:\Program Files\Unity\Hub\Editor\2017.4.13f1\Editor\Unity.exe"
        };
        return new Settings(project, candidates.FirstOrDefault(File.Exists) ?? candidates[0]);
    }
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? Defaults(); }
        catch { return Defaults(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(StateRoot);
        var temporary = SettingsPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporary, SettingsPath, true);
    }
}

internal sealed record Stage(int Number, string Name, string KoreanName, double Height, bool Checkpoint, string Tip);

internal sealed record NativeExportReport(bool StructuralPassed,int SceneObjects,int PhysicsBodies,bool GameplayVerified,string DataSha256)
{
    public static NativeExportReport Empty=>new(false,0,0,false,"");
    public static NativeExportReport Read(string folder)
    {
        try
        {
            var path=Path.Combine(folder,"export-report.json");
            if(!File.Exists(path))return Empty;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var root=doc.RootElement;
            bool structural=false;int objects=0,bodies=0;string hash="";
            if(root.TryGetProperty("native_writer",out var writer))
            {
                structural=writer.TryGetProperty("structural_reparse",out var s)&&s.GetString()=="passed";
                objects=writer.TryGetProperty("scene_objects",out var o)?o.GetInt32():0;
            }
            if(root.TryGetProperty("counts",out var counts)&&counts.TryGetProperty("54",out var body))bodies=body.GetInt32();
            if(root.TryGetProperty("data_sha256",out var digest))hash=digest.GetString()??"";
            bool gameplay=root.TryGetProperty("gameplay_verified",out var g)&&g.ValueKind==JsonValueKind.True;
            return new(structural,objects,bodies,gameplay,hash);
        }
        catch(IOException){return Empty;}
        catch(JsonException){return Empty;}
    }
}

internal sealed class ProjectSnapshot
{
    public string Title { get; init; } = "프로젝트";
    public string RequiredVersion { get; init; } = "확인 필요";
    public string ScenePath { get; init; } = "";
    public string PreviewPath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string Error { get; init; } = "";
    public string LastLog { get; init; } = "";
    public string LicenseMessage { get; init; } = "라이선스 상태 미확인";
    public bool LicenseBlocked { get; init; }
    public bool HasWrapper { get; init; }
    public bool HasIndependentExporter { get; init; }
    public bool HasSdk { get; init; }
    public bool HasExport { get; init; }
    public bool HasUnityValidation { get; init; }
    public bool OfflinePassed { get; init; }
    public int PhysicsBodies { get; init; }
    public int Objects { get; init; }
    public NativeExportReport ExportReport { get; init; } = NativeExportReport.Empty;
    public double Height { get; init; }
    public IReadOnlyList<Stage> Stages { get; init; } = Array.Empty<Stage>();

    public static ProjectSnapshot Read(string project)
    {
        try
        {
            project = Path.GetFullPath(project);
            if (!Directory.Exists(project)) return new() { Error = "프로젝트 폴더를 찾을 수 없습니다." };
            var stages = new List<Stage>();
            string title = Path.GetFileName(project);
            double height = 0;
            var manifest = Path.Combine(project, "stage-manifest.json");
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                var r = json.RootElement;
                title = r.TryGetProperty("title", out var t) ? t.GetString() ?? title : title;
                height = r.TryGetProperty("height_m", out var h) ? h.GetDouble() : 0;
                if (r.TryGetProperty("stages", out var stageArray))
                    foreach (var s in stageArray.EnumerateArray())
                        stages.Add(new(s.GetProperty("number").GetInt32(), s.GetProperty("name").GetString() ?? "",
                            s.GetProperty("name_ko").GetString() ?? "", s.GetProperty("height_m").GetDouble(),
                            s.GetProperty("checkpoint").GetBoolean(), s.GetProperty("tip").GetString() ?? ""));
            }
            var versionPath = Path.Combine(project, "ProjectSettings", "ProjectVersion.txt");
            var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim().Replace("m_EditorVersion:", "").Trim() : "확인 필요";
            int bodies = 0, objects = 0; bool offline = false;
            var verification = Path.Combine(project, "verification.json");
            if (File.Exists(verification))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(verification));
                var r = json.RootElement;
                bodies = r.TryGetProperty("physics_bodies", out var b) ? b.GetInt32() : 0;
                objects = r.TryGetProperty("game_objects", out var o) ? o.GetInt32() : 0;
                offline = r.TryGetProperty("status", out var s) && s.GetString() == "passed";
            }
            var build = Path.Combine(project, "Build");
            var logs = Directory.Exists(build) ? Directory.GetFiles(build, "unity-*.log").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : Array.Empty<string>();
            var log = logs.Length > 0 ? ReadTail(logs[0], 30) : "";
            bool blocked = log.Contains("Unity has not been activated with a valid License", StringComparison.OrdinalIgnoreCase);
            var output = Path.Combine(build, "LastGrip");
            bool independent=CommandRunner.HasIndependentExporter(project);
            var exportReport=NativeExportReport.Read(output);
            return new()
            {
                Title = title, Height = height, RequiredVersion = version, Stages = stages,
                PhysicsBodies = bodies, Objects = objects, OfflinePassed = offline,
                ScenePath = Path.Combine(project, "Assets", "LastGrip", "LastGripTower.unity"),
                PreviewPath = Path.Combine(project, "Previews", "overview.png"), OutputPath = output,
                HasWrapper = File.Exists(Path.Combine(project, "Tools", "unity-cli.ps1")),
                HasIndependentExporter=independent,ExportReport=exportReport,
                HasSdk = File.Exists(Path.Combine(project, "Assets", "HFF-Code-CSharp.dll")),
                HasExport = new[] { "data", "metadata.json", "thumbnail.png" }.All(n => File.Exists(Path.Combine(output, n))),
                HasUnityValidation = File.Exists(Path.Combine(build, "unity-validation.txt")),
                LastLog = log, LicenseBlocked = blocked,
                LicenseMessage = independent ? "독립 맵 내보내기 사용 가능" : blocked ? "마지막 Unity 로그: 라이선스 활성화 필요" : "라이선스는 실제 실행으로 확인합니다"
            };
        }
        catch (Exception ex) { return new() { Error = ex.Message }; }
    }

    public static string ReadTail(string path, int lines = 40)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>();
        while (reader.ReadLine() is { } line) { queue.Enqueue(line); if (queue.Count > lines) queue.Dequeue(); }
        return string.Join(Environment.NewLine, queue);
    }
}

internal sealed record RunResult(int ExitCode, bool Cancelled, bool LicenseBlocked, string Output)
{
    public bool Success => ExitCode == 0 && !Cancelled && !LicenseBlocked;
}

internal static class CommandRunner
{
    public static string PowerShellPath()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(entry.Trim('"'), "pwsh.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public static ProcessStartInfo Build(string project, string editor, string action)
    {
        if (action is not ("Validate" or "Export" or "Render")) throw new ArgumentException("허용되지 않은 작업입니다.", nameof(action));
        project = Path.GetFullPath(project);
        var script = Path.Combine(project, "Tools", "unity-cli.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("이 프로젝트에 Tools/unity-cli.ps1가 없습니다.", script);
        if (!(action=="Export"&&HasIndependentExporter(project))&&!File.Exists(editor)) throw new FileNotFoundException("선택한 Unity 에디터가 없습니다.", editor);
        var info = CreateInfo(PowerShellPath(), project);
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, "-Action", action }) info.ArgumentList.Add(arg);
        if(!string.IsNullOrWhiteSpace(editor)){info.ArgumentList.Add("-EditorPath");info.ArgumentList.Add(Path.GetFullPath(editor));}
        return info;
    }

    public static bool HasIndependentExporter(string project)=>File.Exists(Path.Combine(project,"Tools","IndependentExporter","export_tower.py"));

    public static ProcessStartInfo CreateInfo(string executable, string directory) => new(executable)
    {
        WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };

    public static async Task<RunResult> RunAsync(ProcessStartInfo info, Action<string> emit, CancellationToken cancellation, Action<Process>? started = null)
    {
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var collected = new StringBuilder(); var gate = new object();
        void Record(string? line)
        {
            if (line is null) return;
            lock (gate) collected.AppendLine(line);
            emit(line);
        }
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        cancellation.ThrowIfCancellationRequested();
        process.Start(); started?.Invoke(process);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        bool cancelled = false;
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process.WaitForExit(); // flush asynchronous stdout / stderr callbacks
        string output; lock (gate) output = collected.ToString();
        return new(process.ExitCode, cancelled, IsLicenseFailure(output), output);
    }

    public static bool IsLicenseFailure(string output) =>
        output.Contains("Unity has not been activated with a valid License", StringComparison.OrdinalIgnoreCase)
        || output.Contains("라이선스",StringComparison.OrdinalIgnoreCase) && output.Contains("활성화",StringComparison.OrdinalIgnoreCase);

    public static RunResult AttachFreshNativeLog(RunResult result,string nativeLog,DateTime startedUtc)
    {
        if (result.Success || result.Cancelled || result.LicenseBlocked) return result;
        try
        {
            if (File.Exists(nativeLog) && File.GetLastWriteTimeUtc(nativeLog)>=startedUtc)
            {
                var tail=ProjectSnapshot.ReadTail(nativeLog,80);
                if(IsLicenseFailure(tail)) return result with {LicenseBlocked=true};
            }
        }
        catch(IOException) { }
        return result;
    }
}
