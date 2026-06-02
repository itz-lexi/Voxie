using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Voxie.Models;

namespace Voxie.Services;

public static class UpdateService
{
    private const string ApplyPortableUpdateArgument = "--apply-portable-update";
    private const string LatestReleaseUrl = "https://api.github.com/repos/itz-lexi/Voxie/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/itz-lexi/Voxie/releases";
    private static readonly HttpClient HttpClient = new();

    static UpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Voxie");
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        using var response = await HttpClient.GetAsync(LatestReleaseUrl);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        var latestTag = root.GetProperty("tag_name").GetString() ?? "";
        var portablePackageName = "";
        var portablePackageDownloadUrl = "";

        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString() ?? "";
                if (!assetName.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                portablePackageName = assetName;
                portablePackageDownloadUrl = asset.TryGetProperty("browser_download_url", out var download)
                    ? download.GetString() ?? ""
                    : "";
                break;
            }
        }

        var currentVersion = NormalizeVersion(CurrentVersion);
        var latestVersion = NormalizeVersion(latestTag);
        return new UpdateCheckResult
        {
            CurrentVersion = currentVersion.ToString(),
            LatestVersion = latestVersion.ToString(),
            ReleaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
            PortablePackageName = portablePackageName,
            PortablePackageDownloadUrl = portablePackageDownloadUrl,
            UpdateAvailable = latestVersion > currentVersion
        };
    }

    public static async Task PrepareAndLaunchPortableUpdateAsync(UpdateCheckResult update)
    {
        if (!update.HasPortablePackage)
            throw new InvalidOperationException("The latest GitHub release does not include a portable app package.");

        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not find the running app path.");
        var targetDirectory = Path.GetDirectoryName(currentExePath)
            ?? throw new InvalidOperationException("Could not find the app install folder.");
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Voxie", "UpdateStaging", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var extractionPath = Path.Combine(updateRoot, "extracted");
        Directory.CreateDirectory(extractionPath);

        var packagePath = Path.Combine(updateRoot, update.PortablePackageName);
        using var response = await HttpClient.GetAsync(update.PortablePackageDownloadUrl);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var destination = File.Create(packagePath))
            await source.CopyToAsync(destination);

        ZipFile.ExtractToDirectory(packagePath, extractionPath, overwriteFiles: true);
        var extractedExePath = Directory.EnumerateFiles(extractionPath, "Voxie.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("The portable update did not contain Voxie.exe.");
        var sourceDirectory = Path.GetDirectoryName(extractedExePath)
            ?? throw new InvalidOperationException("Could not find the extracted app folder.");
        var startInfo = new ProcessStartInfo
        {
            FileName = extractedExePath,
            UseShellExecute = true,
            WorkingDirectory = sourceDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(" ", new[]
            {
                Quote(ApplyPortableUpdateArgument), Quote("--process-id"), Quote(Environment.ProcessId.ToString()),
                Quote("--source"), Quote(sourceDirectory), Quote("--target"), Quote(targetDirectory),
                Quote("--exe"), Quote(currentExePath), Quote("--staging"), Quote(updateRoot)
            })
        };
        if (!CanWriteToDirectory(targetDirectory))
            startInfo.Verb = "runas";
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Could not launch the staged updater.");
    }

    public static bool IsPortableUpdateCommand(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, ApplyPortableUpdateArgument, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> ApplyPortableUpdateFromCommandLineAsync(IReadOnlyList<string> args)
    {
        var options = ParseArguments(args);
        var updateRoot = GetOption(options, "staging");
        var logPath = Path.Combine(updateRoot, "update.log");
        try
        {
            Directory.CreateDirectory(updateRoot);
            var processId = int.Parse(GetOption(options, "process-id"));
            var sourceDirectory = GetOption(options, "source");
            var targetDirectory = GetOption(options, "target");
            var exePath = GetOption(options, "exe");
            await AppendLogAsync(logPath, $"Waiting for app process {processId} to exit.");
            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.WaitForExit(60000))
                    throw new InvalidOperationException("The app did not exit within 60 seconds.");
            }
            catch (ArgumentException)
            {
                await AppendLogAsync(logPath, "App process already exited.");
            }

            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var targetPath = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDirectory);
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            Process.Start(new ProcessStartInfo { FileName = exePath, WorkingDirectory = targetDirectory, UseShellExecute = true });
            await AppendLogAsync(logPath, "Updater finished successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            await AppendLogAsync(logPath, $"Updater failed: {ex}");
            MessageBox.Show($"The update could not be installed.\n\n{ex.Message}\n\nLog: {logPath}", "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            var path = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, "");
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseArguments(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == ApplyPortableUpdateArgument)
                continue;
            if (index + 1 >= args.Count)
                throw new InvalidOperationException($"Missing value for update argument: {args[index]}");
            options[args[index][2..]] = args[++index];
        }
        return options;
    }

    private static string GetOption(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing update argument: --{name}");

    private static Task AppendLogAsync(string path, string message) =>
        File.AppendAllTextAsync(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static Version NormalizeVersion(string version)
    {
        var clean = version.Trim().TrimStart('v', 'V');
        var metadataIndex = clean.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
            clean = clean[..metadataIndex];
        return Version.TryParse(clean, out var parsed) ? parsed : new Version(0, 0, 0);
    }
}
