using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace NaraeUnity;

internal static class SelfTest
{
    public static int Run()
    {
        var checks = new List<object>();
        var root = Path.Combine(Settings.StateRoot, "selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "sample & quoted' project");
            Directory.CreateDirectory(Path.Combine(project, "Tools"));
            var wrapper = Path.Combine(project, "Tools", "unity-cli.ps1");
            File.WriteAllText(wrapper, "Write-Output 'placeholder'", Encoding.UTF8);
            var editor = Path.Combine(root, "editor & quoted' path", "Unity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(editor)!);
            File.WriteAllText(editor, "not executed", Encoding.UTF8);
            var command = CommandRunner.Build(project, editor, "Validate");
            Assert(command.ArgumentList.Contains(wrapper) && command.ArgumentList.Contains(editor), "paths_are_individual_literal_arguments", checks);
            Assert(!command.UseShellExecute && command.CreateNoWindow && command.WindowStyle == ProcessWindowStyle.Hidden,
                "background_process_has_no_visible_window", checks);
            bool rejected = false;
            try { CommandRunner.Build(project, editor, "not an action"); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "rejects_unsupported_action", checks);
            rejected = false;
            try { CommandRunner.Build(Path.Combine(root, "absent"), editor, "Validate"); } catch (FileNotFoundException) { rejected = true; }
            Assert(rejected, "missing_wrapper_reports_failure", checks);
            Assert(ProjectSnapshot.Read(Path.Combine(root,"absent")).Error.Length > 0, "missing_project_is_explained", checks);
            rejected=false;
            try{CommandRunner.Build(project,"","Export");}catch(FileNotFoundException){rejected=true;}
            Assert(rejected,"editorless_export_requires_independent_helper",checks);
            var helper=Path.Combine(project,"Tools","IndependentExporter","export_tower.py");
            Directory.CreateDirectory(Path.GetDirectoryName(helper)!);File.WriteAllText(helper,"# placeholder; not executed");
            var independent=CommandRunner.Build(project,"","Export");
            Assert(!independent.ArgumentList.Contains("-EditorPath")&&CommandRunner.HasIndependentExporter(project),
                "independent_export_does_not_require_editor_path",checks);
            rejected=false;
            try{CommandRunner.Build(project,"","Validate");}catch(FileNotFoundException){rejected=true;}
            Assert(rejected,"unity_validation_still_requires_editor",checks);

            var failure = Fixture(root, "failure.ps1", "Write-Output 'Unity has not been activated with a valid License'; exit 13");
            var failed = CommandRunner.RunAsync(failure, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(failed.ExitCode == 13 && failed.LicenseBlocked && !failed.Success, "license_failure_preserves_exit_and_status", checks);
            Assert(CommandRunner.IsLicenseFailure("Unity 라이선스가 활성화되어 있지 않아 실행을 중단했습니다."), "korean_only_license_failure_is_detected", checks);
            Directory.CreateDirectory(Path.Combine(project,"Build"));
            File.WriteAllText(Path.Combine(project,"Build","unity-Validate.log"),"Unity has not been activated with a valid License");
            var nativeLog=Path.Combine(project,"Build","unity-Validate.log");
            File.SetLastWriteTimeUtc(nativeLog,DateTime.UtcNow.AddDays(-1));
            var unrelated=new RunResult(7,false,false,"preflight failure");
            Assert(!CommandRunner.AttachFreshNativeLog(unrelated,nativeLog,DateTime.UtcNow).LicenseBlocked,
                "stale_native_log_is_not_used_for_current_failure",checks);
            var runStarted=DateTime.UtcNow.AddSeconds(-1); File.SetLastWriteTimeUtc(nativeLog,DateTime.UtcNow);
            Assert(CommandRunner.AttachFreshNativeLog(unrelated,nativeLog,runStarted).LicenseBlocked,
                "fresh_native_license_log_survives_console_encoding_loss",checks);
            Assert(ProjectSnapshot.Read(project).LicenseBlocked && Cli.Classify(new RunResult(7,false,false,"preflight failure"))=="failed",
                "historical_license_log_does_not_override_current_unrelated_failure", checks);
            Assert(Cli.Classify(new RunResult(0,false,false,"completed"))=="completed", "historical_license_log_does_not_override_current_success", checks);
            var oldExport=Path.Combine(project,"Build","LastGrip"); Directory.CreateDirectory(oldExport);
            foreach(var name in new[]{"data","metadata.json","thumbnail.png"})
            {
                var p=Path.Combine(oldExport,name);File.WriteAllText(p,"old");File.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddDays(-1));
            }
            Assert(!Cli.HasFreshExport(oldExport,DateTime.UtcNow), "old_export_files_do_not_count_as_current_export", checks);
            var exportStarted=DateTime.UtcNow.AddSeconds(-1);
            var dataPath=Path.Combine(oldExport,"data");File.WriteAllText(dataPath,"original serialized bundle");
            var digest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataPath))).ToLowerInvariant();
            var exportReport=Path.Combine(oldExport,"export-report.json");
            File.WriteAllText(exportReport,JsonSerializer.Serialize(new {status="serialized",data_sha256=digest,gameplay_verified=false,
                native_writer=new {structural_reparse="passed",scene_objects=1274},counts=new Dictionary<string,int>{{"54",40}}}));
            Assert(Cli.HasFreshExport(oldExport,exportStarted),"fresh_independent_bundle_hash_allows_unchanged_thumbnail",checks);
            var exported=ProjectSnapshot.Read(project);
            Assert(exported.HasIndependentExporter&&exported.HasExport&&exported.ExportReport.StructuralPassed&&exported.ExportReport.PhysicsBodies==40&&!exported.ExportReport.GameplayVerified,
                "independent_output_report_keeps_structural_and_gameplay_status_separate",checks);
            File.WriteAllText(dataPath,"changed bytes after report");
            Assert(!Cli.HasFreshExport(oldExport,exportStarted),"independent_export_rejects_data_hash_mismatch",checks);
            var success = Fixture(root, "success.ps1", "Write-Output 'done'; exit 0");
            var succeeded = CommandRunner.RunAsync(success, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(succeeded.Success && succeeded.Output.Contains("done"), "process_output_is_flushed", checks);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
            var sleep = Fixture(root, "cancel.ps1", "Start-Sleep -Seconds 20");
            var timer = Stopwatch.StartNew();
            var stopped = CommandRunner.RunAsync(sleep, _ => { }, cancel.Token).GetAwaiter().GetResult();
            Assert(stopped.Cancelled && !stopped.Success && timer.Elapsed.TotalSeconds < 10, "cancellation_terminates_owned_process", checks);
            var real = ProjectSnapshot.Read(Settings.Defaults().ProjectPath);
            Assert(real.Stages.Count == 12 && real.PhysicsBodies == 35 && real.OfflinePassed, "real_project_manifest_is_loaded", checks);
            var preview = Path.Combine(Settings.AppRoot, "app-preview.png");
            using (var form = new MainForm())
            {
                form.CapturePreview(preview);
                form.CapturePreview(Path.Combine(Settings.AppRoot,"export-preview.png"),1);
                form.CapturePreview(Path.Combine(Settings.AppRoot,"rea-preview.png"),2);
            }
            Assert(File.Exists(preview) && new FileInfo(preview).Length > 10000, "hidden_form_preview_created_without_show", checks);
            using(var bitmap=new Bitmap(preview))
            {
                var colors=new HashSet<int>();
                for(int y=80;y<bitmap.Height-40;y+=30) for(int x=40;x<bitmap.Width-40;x+=30) colors.Add(bitmap.GetPixel(x,y).ToArgb());
                Assert(colors.Count>15,"hidden_preview_contains_rendered_controls",checks);
            }
            var report = new { status = "passed", checks, preview, note = "Self-tests verify app behavior only. Unity export and gameplay are not claimed." };
            File.WriteAllText(Path.Combine(Settings.AppRoot,"self-test.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }),Encoding.UTF8);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception ex)
        {
            var report = new { status = "failed", checks, error = ex.ToString() };
            File.WriteAllText(Path.Combine(Settings.AppRoot,"self-test.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }),Encoding.UTF8);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 1;
        }
    }

    static void Assert(bool condition,string name,List<object> checks)
    {
        checks.Add(new { name, passed=condition });
        if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
    }

    static ProcessStartInfo Fixture(string directory,string name,string body)
    {
        var path=Path.Combine(directory,name); File.WriteAllText(path,body,Encoding.UTF8);
        var info=CommandRunner.CreateInfo(CommandRunner.PowerShellPath(),directory);
        foreach(var arg in new[] {"-NoLogo","-NoProfile","-NonInteractive","-File",path}) info.ArgumentList.Add(arg);
        return info;
    }
}
