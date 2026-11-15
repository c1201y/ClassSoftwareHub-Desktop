using System.Diagnostics;
using System.IO.Compression;
using ClassSoftwareHub.Desktop.Services;

var root = Path.Combine(Path.GetTempPath(), "pkgtest-run");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

var pass = 0;
var fail = 0;

void Check(bool ok, string what)
{
    if (ok) { pass++; Console.WriteLine($"  OK    {what}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}"); }
}

// ── 1. 类型判定 ────────────────────────────────────────────────
Console.WriteLine("== 1. 类型判定 PackageKind.Of ==");
var cases = new (string? Name, PackageAction Want)[]
{
    ("setup.exe", PackageAction.Install),
    ("app.msi", PackageAction.Install),
    ("App.msixbundle", PackageAction.Install),
    ("pkg.appinstaller", PackageAction.Install),
    ("KB5000.msu", PackageAction.Install),
    ("portable.zip", PackageAction.Extract),
    ("data.7z", PackageAction.Extract),
    ("old.rar", PackageAction.Extract),
    ("src.tar.gz", PackageAction.Extract),
    ("src.tar.xz", PackageAction.Extract),
    ("bundle.tgz", PackageAction.Extract),
    ("DOC.TAR.GZ", PackageAction.Extract),
    ("disk.img.tar.zst", PackageAction.Extract),
    ("manual.pdf", PackageAction.None),
    ("photo.png", PackageAction.None),
    ("movie.mp4", PackageAction.None),
    ("noext", PackageAction.None),
    ("", PackageAction.None),
    (null, PackageAction.None),
};
foreach (var c in cases)
    Check(PackageKind.Of(c.Name) == c.Want, $"{c.Name ?? "(null)"} → {PackageKind.Of(c.Name)}（期望 {c.Want}）");

// ── 2. 目标目录命名 ────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== 2. 解压目标目录 TargetDirFor ==");
Check(Path.GetFileName(PackageRunner.TargetDirFor(Path.Combine(root, "foo.zip"))) == "foo", "foo.zip → foo");
Check(Path.GetFileName(PackageRunner.TargetDirFor(Path.Combine(root, "a.tar.gz"))) == "a", "a.tar.gz → a（连剥两层）");
Check(Path.GetFileName(PackageRunner.TargetDirFor(Path.Combine(root, "b.tar.xz"))) == "b", "b.tar.xz → b");
Check(Path.GetFileName(PackageRunner.TargetDirFor(Path.Combine(root, "c.7z"))) == "c", "c.7z → c");
Directory.CreateDirectory(Path.Combine(root, "dup"));
File.WriteAllText(Path.Combine(root, "dup", "occupied.txt"), "x");
Check(Path.GetFileName(PackageRunner.TargetDirFor(Path.Combine(root, "dup.zip"))) == "dup (2)", "同名非空目录 → dup (2)");

// ── 3. 造样本 ──────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== 3. 造压缩样本 ==");
var srcDir = Path.Combine(root, "src");
Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
File.WriteAllText(Path.Combine(srcDir, "a.txt"), "hello ClassSoftwareHub");
File.WriteAllText(Path.Combine(srcDir, "sub", "b.txt"), "第二个文件");

var zipPath = Path.Combine(root, "t.zip");
using (var fs = File.Create(zipPath))
using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
{
    void Add(string name, string body)
    {
        var e = za.CreateEntry(name);
        using var s = e.Open();
        using var w = new StreamWriter(s);
        w.Write(body);
    }
    Add("a.txt", "hello ClassSoftwareHub");
    Add("sub/b.txt", "第二个文件");
}
Check(File.Exists(zipPath) && new FileInfo(zipPath).Length > 100, "t.zip 已生成");

string Tar(string[] args, string outName)
{
    var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
    var psi = new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    psi.ArgumentList.Add("-C");       // ⚠️ -C 必须在打包目录之前，否则打的是当前目录
    psi.ArgumentList.Add(srcDir);
    psi.ArgumentList.Add(".");
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return Path.Combine(root, outName);
}

var tgz = Tar(new[] { "-caf", Path.Combine(root, "t.tar.gz") }, "t.tar.gz");
var txz = Tar(new[] { "-caf", Path.Combine(root, "t.tar.xz") }, "t.tar.xz");
var sev = Tar(new[] { "-caf", Path.Combine(root, "t.7z") }, "t.7z");
Check(new FileInfo(tgz).Length > 100, "t.tar.gz 已生成");
Check(new FileInfo(txz).Length > 100, "t.tar.xz 已生成");
Check(new FileInfo(sev).Length > 100, "t.7z 已生成");

// ── 4. 逐个真解压 ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== 4. 真解压（走产品里的 ExtractAsync）==");
foreach (var (file, label) in new[] { (zipPath, "zip(BCL)"), (tgz, "tar.gz(系统tar)"), (txz, "tar.xz(系统tar)"), (sev, "7z(系统tar)") })
{
    try
    {
        var r = await PackageRunner.ExtractAsync(file);
        var a = File.Exists(Path.Combine(r.Directory, "a.txt"));
        var b = File.Exists(Path.Combine(r.Directory, "sub", "b.txt"));
        var body = a ? File.ReadAllText(Path.Combine(r.Directory, "a.txt")) : "";
        var listing = string.Join(" , ", Directory.EnumerateFileSystemEntries(r.Directory)
            .Select(Path.GetFileName).OrderBy(x => x));
        Check(a && b && body.Contains("ClassSoftwareHub"),
            $"{label} → {Path.GetFileName(r.Directory)}  内容完整={a && b} 正文对={body.Contains("ClassSoftwareHub")}  实际内容=[{listing}]");
    }
    catch (Exception ex)
    {
        Check(false, $"{label} 抛异常：{ex.GetType().Name} {ex.Message}");
    }
}

// ── 5. Zip Slip 防护 ───────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== 5. Zip Slip 防护 ==");
var evil = Path.Combine(root, "evil.zip");
using (var fs = File.Create(evil))
using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
{
    var e = za.CreateEntry("..\\evil.txt");
    using var s = e.Open();
    using var w = new StreamWriter(s);
    w.Write("越界内容");
}
try
{
    var r = await PackageRunner.ExtractAsync(evil);
    Check(false, $"越界条目竟然解开了 → {r.Directory}");
}
catch (Exception ex)
{
    var leaked = File.Exists(Path.Combine(root, "evil.txt"));
    Check(!leaked && ex.Message.Contains("越界"), $"已拦住：{ex.Message}（外层文件未落盘={!leaked}）");
}
Check(!Directory.Exists(Path.Combine(root, "evil")), "失败后半截产物已清掉");

// ── 6. 自动删除 ────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== 6. 删除源文件 TryDeleteAsync ==");
var victim = Path.Combine(root, "to-delete.zip");
File.Copy(zipPath, victim);
var gone = await PackageRunner.TryDeleteAsync(victim);
Check(gone && !File.Exists(victim), "普通文件删除成功");
var missing = await PackageRunner.TryDeleteAsync(victim);
Check(missing, "文件本就不存在 → 也算成功");

// ── 7. RunAsync 编排（解压 + 自动删除 + 自动打开产物目录）──────
Console.WriteLine();
Console.WriteLine("== 7. 编排 RunAsync（解压 + 自动删除 + 自动打开）==");
var shell = new ClassSoftwareHub.Desktop.MainWindowStub();
ClassSoftwareHub.Desktop.App.MainWindow = shell;
ClassSoftwareHub.Desktop.App.Settings.Current.AutoDeletePackage = true;
ClassSoftwareHub.Desktop.App.Settings.Current.AutoOpenExtractFolder = true;

var taskZip = Path.Combine(root, "auto.zip");
File.Copy(zipPath, taskZip);
var task = new DownloadTask { Path = taskZip, Action = PackageAction.Extract, Title = "auto.zip" };
await PackageRunner.RunAsync(task);
Check(task.ExtractedDir is { Length: > 0 } && Directory.Exists(task.ExtractedDir), $"产物目录已回填：{task.ExtractedDir}");
Check(!File.Exists(taskZip), "压缩包已按开关删除");
Check(task.WorkText.Contains("已删除压缩包"), $"卡片文案：{task.WorkText}");
Check(shell.RevealCount == 1 && shell.LastRevealed == task.ExtractedDir,
    $"已按开关自动打开产物目录（调用 {shell.RevealCount} 次）");

// 关掉自动打开 → 不该再开
shell.RevealCount = 0;
ClassSoftwareHub.Desktop.App.Settings.Current.AutoOpenExtractFolder = false;
ClassSoftwareHub.Desktop.App.Settings.Current.AutoDeletePackage = false;
var taskZip2 = Path.Combine(root, "auto2.zip");
File.Copy(zipPath, taskZip2);
var task2 = new DownloadTask { Path = taskZip2, Action = PackageAction.Extract, Title = "auto2.zip" };
await PackageRunner.RunAsync(task2);
Check(shell.RevealCount == 0, "关掉开关后不再自动打开");
Check(File.Exists(taskZip2), "关掉开关后压缩包保留");

Console.WriteLine();
Console.WriteLine("== 8. 文件不存在时的兜底 ==");
var ghost = new DownloadTask { Path = Path.Combine(root, "nope.zip"), Action = PackageAction.Extract };
await PackageRunner.RunAsync(ghost);
Check(ghost.WorkText.Contains("不在原位置"), $"卡片文案：{ghost.WorkText}");

Console.WriteLine();
Console.WriteLine($"===== 通过 {pass} · 失败 {fail} =====");
return fail == 0 ? 0 : 1;
