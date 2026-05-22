using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RawScoreBatchAnalyzer.Analysis;

namespace RawScoreBatchAnalyzer.UI;

public sealed partial class MainWindow : Window
{
    private readonly RawReplayAnalyzer analyzer = new();
    private CancellationTokenSource? cancellation;

    public MainWindow()
    {
        InitializeComponent();
        InitializeDefaults();
        WireEvents();
    }

    private void InitializeDefaults()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultLazer = Path.Combine(localAppData, "osulazer");
        if (Directory.Exists(defaultLazer))
        {
            RealmPathBox.Text = Path.Combine(defaultLazer, "client.realm");
            FilesRootBox.Text = Path.Combine(defaultLazer, "files");
        }

        OutputCsvBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"raw-score-batch-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        ThreadCountBox.Value = Math.Max(1, Environment.ProcessorCount - 1);
        SourceLabel.Text = FindRepositoryRoot();
    }

    private void WireEvents()
    {
        BrowseRealmButton.Click += async (_, _) =>
        {
            var file = await PickFile("Select client.realm", "realm");
            if (file != null)
            {
                RealmPathBox.Text = file;
                var root = Path.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(root))
                    FilesRootBox.Text = Path.Combine(root, "files");
            }
        };

        BrowseFilesButton.Click += async (_, _) => FilesRootBox.Text = await PickFolder("Select osu!lazer files folder") ?? FilesRootBox.Text;
        BrowseReplayButton.Click += async (_, _) => ReplayFolderBox.Text = await PickFolder("Select OSR folder") ?? ReplayFolderBox.Text;

        BrowseOutputButton.Click += async (_, _) =>
        {
            var file = await SaveFile("Select output CSV");
            if (file != null)
                OutputCsvBox.Text = file;
        };

        AnalyzeButton.Click += AnalyzeClicked;
        CancelButton.Click += (_, _) => cancellation?.Cancel();
    }

    private async void AnalyzeClicked(object? sender, RoutedEventArgs e)
    {
        if (cancellation != null)
            return;

        cancellation = new CancellationTokenSource();
        AnalyzeButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        LogBox.Text = string.Empty;
        AppendLog("Starting analysis...");

        try
        {
            var options = new AnalysisOptions(
                RealmPath: RealmPathBox.Text?.Trim() ?? string.Empty,
                FilesRoot: FilesRootBox.Text?.Trim() ?? string.Empty,
                ReplayFolder: ReplayFolderBox.Text?.Trim() ?? string.Empty,
                OutputCsvPath: OutputCsvBox.Text?.Trim() ?? string.Empty,
                MaxDegreeOfParallelism: Math.Max(1, (int)(ThreadCountBox.Value ?? 1)),
                Recursive: RecursiveBox.IsChecked == true);

            var progress = new Progress<AnalysisProgress>(UpdateProgress);
            var summary = await analyzer.AnalyzeAsync(options, progress, cancellation.Token);
            AppendLog($"Done. Success={summary.Success:N0}, Skipped={summary.Skipped:N0}, Failed={summary.Failed:N0}, Elapsed={summary.Elapsed:g}");
            AppendLog($"CSV: {summary.OutputCsvPath}");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            AnalyzeButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void UpdateProgress(AnalysisProgress progress)
    {
        TotalLabel.Text = $"Total: {progress.Total:N0}";
        ProcessedLabel.Text = $"Processed: {progress.Processed:N0}";
        SuccessLabel.Text = $"Success: {progress.Success:N0}";
        SkippedLabel.Text = $"Skipped: {progress.Skipped:N0}";
        FailedLabel.Text = $"Failed: {progress.Failed:N0}";
        SpeedLabel.Text = $"Speed: {progress.FilesPerSecond:N1} files/sec";
        CurrentFileLabel.Text = string.IsNullOrWhiteSpace(progress.CurrentFile) ? progress.Message : progress.CurrentFile;
        ProgressBar.Value = progress.Total > 0 ? progress.Processed * 100.0 / progress.Total : 0;
    }

    private void AppendLog(string text)
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private async Task<string?> PickFile(string title, string extension)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title)
                {
                    Patterns = new[] { $"*.{extension}" },
                },
            },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> SaveFile(string title)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = $"raw-score-batch-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV")
                {
                    Patterns = new[] { "*.csv" },
                },
            },
        });

        return file?.TryGetLocalPath();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(AppContext.BaseDirectory);
    }
}
