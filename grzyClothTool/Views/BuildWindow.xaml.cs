using grzyClothTool.Controls;
using grzyClothTool.Helpers;
using grzyClothTool.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using static grzyClothTool.Controls.CustomMessageBox;
using static grzyClothTool.Enums;

namespace grzyClothTool.Views
{
    /// <summary>
    /// Interaction logic for BuildWindow.xaml
    /// </summary>
    public partial class BuildWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string _projectName = MainWindow.AddonManager.ProjectName;
        public string ProjectName
        {
            get => _projectName;
            set
            {
                if (_projectName != value)
                {
                    _projectName = value;
                    OnPropertyChanged(nameof(ProjectName));
                    RefreshBuildState();
                }
            }
        }

        private bool _isBuilding;
        public bool IsBuilding
        {
            get => _isBuilding;
            set
            {
                if (_isBuilding != value)
                {
                    _isBuilding = value;
                    OnPropertyChanged(nameof(IsBuilding));
                }
            }
        }
        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set
            {
                if (_progressValue != value)
                {
                    _progressValue = value;
                    OnPropertyChanged(nameof(ProgressValue));
                }
            }
        }

        private bool _splitAddons;
        public bool SplitAddons
        {
            get => _splitAddons;
            set
            {
                if (_splitAddons != value)
                {
                    _splitAddons = value;
                    OnPropertyChanged(nameof(SplitAddons));
                }
            }
        }

        // Console keeps the newest lines only; "Copy log" returns the complete log.
        private const int MaxConsoleLines = 3000;
        private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromMilliseconds(200);

        public ObservableCollection<BuildLogEntry> ConsoleLines { get; } = [];

        private BuildReporter _reporter;
        private readonly DispatcherTimer _uiTimer;

        private bool _autoScroll = true;
        public bool AutoScroll
        {
            get => _autoScroll;
            set
            {
                if (_autoScroll != value)
                {
                    _autoScroll = value;
                    OnPropertyChanged(nameof(AutoScroll));
                }
            }
        }

        private string _statusPhase = "Ready to build";
        public string StatusPhase
        {
            get => _statusPhase;
            set { _statusPhase = value; OnPropertyChanged(nameof(StatusPhase)); }
        }

        private string _statusTime = string.Empty;
        public string StatusTime
        {
            get => _statusTime;
            set { _statusTime = value; OnPropertyChanged(nameof(StatusTime)); }
        }

        private string _statusPercent = string.Empty;
        public string StatusPercent
        {
            get => _statusPercent;
            set { _statusPercent = value; OnPropertyChanged(nameof(StatusPercent)); }
        }

        private string _statusCounts = string.Empty;
        public string StatusCounts
        {
            get => _statusCounts;
            set { _statusCounts = value; OnPropertyChanged(nameof(StatusCounts)); }
        }

        private bool _autoOptimizeTextures;
        public bool AutoOptimizeTextures
        {
            get => _autoOptimizeTextures;
            set
            {
                if (_autoOptimizeTextures != value)
                {
                    _autoOptimizeTextures = value;
                    OnPropertyChanged(nameof(AutoOptimizeTextures));
                }
            }
        }

        private bool _isWarningVisible;
        public bool IsWarningVisible
        {
            get => _isWarningVisible;
            set
            {
                if (_isWarningVisible != value)
                {
                    _isWarningVisible = value;
                    OnPropertyChanged(nameof(IsWarningVisible));
                }
            }
        }

        private string _warningMessage;
        public string WarningMessage
        {
            get => _warningMessage;
            set
            {
                if (_warningMessage != value)
                {
                    _warningMessage = value;
                    OnPropertyChanged(nameof(WarningMessage));
                }
            }
        }

        private bool _canBuild = true;
        public bool CanBuild
        {
            get => _canBuild;
            set
            {
                if (_canBuild != value)
                {
                    _canBuild = value;
                    OnPropertyChanged(nameof(CanBuild));
                }
            }
        }

        public string BuildPath { get; set; } = GetDefaultBuildPath();

        private static string GetDefaultBuildPath()
        {
            var projectName = MainWindow.AddonManager.ProjectName;
            var mainFolder = PersistentSettingsHelper.Instance.MainProjectsFolder;

            if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(mainFolder))
            {
                return string.Empty;
            }

            return Path.Combine(mainFolder, projectName, "build_output");
        }

        private BuildResourceType _resourceType;

        public BuildWindow()
        {
            InitializeComponent();
            DataContext = this;

            _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UiRefreshInterval };
            _uiTimer.Tick += (_, _) => RefreshBuildProgress();

            this.Loaded += Window_Loaded;
        }

        private void RefreshBuildProgress()
        {
            if (_reporter == null)
            {
                return;
            }

            var newLines = _reporter.DrainPending();
            if (newLines.Count > 0)
            {
                // Only the tail matters when a burst is bigger than the console itself.
                foreach (var line in newLines.Skip(Math.Max(0, newLines.Count - MaxConsoleLines)))
                {
                    ConsoleLines.Add(line);
                }

                while (ConsoleLines.Count > MaxConsoleLines)
                {
                    ConsoleLines.RemoveAt(0);
                }

                if (AutoScroll && ConsoleLines.Count > 0)
                {
                    ConsoleList.ScrollIntoView(ConsoleLines[^1]);
                }
            }

            var snapshot = _reporter.GetSnapshot();
            ProgressValue = snapshot.Percent;
            StatusPhase = snapshot.Phase;
            StatusPercent = $"{snapshot.Percent:0.0}%";
            StatusCounts = $"{snapshot.DoneItems} file(s) · {snapshot.Warnings} warning(s) · {snapshot.Errors} error(s)";

            string remaining;
            if (snapshot.Remaining == null)
            {
                remaining = "estimating...";
            }
            else if (snapshot.Remaining == TimeSpan.Zero)
            {
                remaining = "done";
            }
            else
            {
                remaining = $"~{FormatDuration(snapshot.Remaining.Value)}";
            }

            StatusTime = $"Elapsed {FormatDuration(snapshot.Elapsed)} · Remaining {remaining}";
        }

        private static string FormatDuration(TimeSpan time) =>
            time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            var log = _reporter?.GetFullLog();
            if (!string.IsNullOrEmpty(log))
            {
                Clipboard.SetText(log);
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(BuildPath))
            {
                Process.Start("explorer.exe", BuildPath);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            split_addons.IsEnabled = MainWindow.AddonManager.Addons.Count > 1;

            RefreshBuildState();
        }

        private void RefreshBuildState()
        {
            var projectNameError = ValidateProjectName();
            if (projectNameError != null)
            {
                IsWarningVisible = true;
                WarningMessage = projectNameError;
                CanBuild = false;
                return;
            }

            if (string.IsNullOrEmpty(BuildPath))
            {
                IsWarningVisible = true;
                WarningMessage = "Build path could not be determined. Please check your project settings.";
                CanBuild = false;
                return;
            }

            var allDrawablesCount = MainWindow.AddonManager.Addons.Sum(a => a.Drawables.Count);
            if (allDrawablesCount == 0)
            {
                IsWarningVisible = true;
                WarningMessage = "No drawables found. Add drawables to be able to build resource.";
                CanBuild = false;
                return;
            }

            IsWarningVisible = false;
            WarningMessage = null;
            CanBuild = true;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            FocusManager.SetFocusedElement(this, this);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = IsBuilding;
        }

        private async Task BuildResource(BuildResourceHelper buildHelper)
        {
            switch (_resourceType)
            {
                case BuildResourceType.FiveM:
                    await buildHelper.BuildFiveMResource();
                    break;
                case BuildResourceType.AltV:
                    await buildHelper.BuildAltVResource();
                    break;
                case BuildResourceType.Singleplayer:
                    await buildHelper.BuildSingleplayerResource();
                    break;
                default:
                    throw new NotImplementedException($"Unsupported resource type: {_resourceType}");
            }
        }

        private async void build_MyBtnClickEvent(object sender, RoutedEventArgs e)
        {
            RefreshBuildState();
            if (!CanBuild)
            {
                return;
            }

            if (string.IsNullOrEmpty(ProjectName) || string.IsNullOrEmpty(BuildPath))
            {
                CustomMessageBox.Show("Please fill in all fields. Make sure a project is loaded.", "Error", CustomMessageBoxButtons.OKOnly);
                return;
            }

            var buildButton = sender as CustomButton;
            if (buildButton != null)
            {
                buildButton.IsEnabled = false; // blocking interactions - spamming button led to building multiple times/exception
            }

            ProgressValue = 0;
            ConsoleLines.Clear();
            _reporter = new BuildReporter();
            IsBuilding = true;
            _uiTimer.Start();

            if (AutoOptimizeTextures)
            {
                var scan = AutoOptimizer.Scan(MainWindow.AddonManager.Addons);
                var applied = AutoOptimizer.Apply(scan.Candidates);
                if (applied > 0)
                {
                    SaveHelper.SetUnsavedChanges(true);
                }
                _reporter.Info($"Auto-optimize: {applied} texture(s) marked for optimization.");
            }

            _reporter.Info("Saving project...");
            await SaveHelper.SaveAsync();

            var succeeded = false;
            try
            {
                var timer = Stopwatch.StartNew();

                var buildHelper = new BuildResourceHelper(ProjectName, BuildPath, _resourceType, SplitAddons, _reporter);

                await Task.Run(() => BuildResource(buildHelper)); // moved out of ui thread, so users don't think tool stopped responding

                timer.Stop();
                _reporter.Finish();
                _reporter.Info($"Build done in {timer.Elapsed}. Output: {BuildPath}");
                LogHelper.Log($"Build done, elapsed time: {timer.Elapsed}");
                succeeded = true;
            }
            catch (Exception ex)
            {
                _reporter.Finish();
                _reporter.Error($"Build failed: {ex.Message}");
                LogHelper.Log($"Build failed: {ex}", LogType.Error);
            }
            finally
            {
                _uiTimer.Stop();
                RefreshBuildProgress(); // flush the last lines and the final state

                if (buildButton != null)
                {
                    buildButton.IsEnabled = true;
                }

                IsBuilding = false;
            }

            // The window stays open so the console can be reviewed after the build.
            if (succeeded)
            {
                StatusPhase = "Build done";
                CustomMessageBox.Show($"Build done, elapsed time: {_reporter.GetSnapshot().Elapsed}", "Build done", CustomMessageBoxButtons.OpenFolder, BuildPath);
            }
            else
            {
                StatusPhase = "Build failed - see the console for details";
                CustomMessageBox.Show("Build failed. Check the console in the build window for details.", "Error", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Error);
            }
        }

        private void RadioButton_ChangedEvent(object sender, RoutedEventArgs e)
        {
            if (sender is ModernLabelRadioButton radioButton && radioButton.IsChecked == true)
            {
                _resourceType = radioButton.Label switch
                {
                    "FiveM" => BuildResourceType.FiveM,
                    "AltV" => BuildResourceType.AltV,
                    "Singleplayer" => BuildResourceType.Singleplayer,
                    _ => throw new NotImplementedException()
                };


                // Singleplayer doesn't support splitting addons
                if (_resourceType == BuildResourceType.Singleplayer)
                {
                    SplitAddons = false;
                    split_addons.IsEnabled = false;
                }
                else if (DataContext != null) // check if DataContext exist, to prevent error (happens on initialization)
                {
                    split_addons.IsEnabled = MainWindow.AddonManager.Addons.Count > 1;
                }
            }
        }

        private string ValidateProjectName()
        {
            string result = null;

            if (string.IsNullOrEmpty(ProjectName))
            {
                result = "Project name cannot be empty";
            }
            else if (ProjectName.Length < 3)
            {
                result = "Project name must be at least 3 characters long";
            }
            else if (ProjectName.Length > 50)
            {
                result = "Project name cannot be longer than 50 characters";
            }
            else if (!Regex.IsMatch(ProjectName, @"^[a-z0-9_]+$"))
            {
                result = "Project name can only contain lowercase letters, numbers, and underscores";
            }

            return result;
        }
    }
}
