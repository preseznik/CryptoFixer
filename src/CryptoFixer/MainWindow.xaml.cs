using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Schannel;
using CryptoFixer.Core.Settings;
using CryptoFixer.ViewModels;

namespace CryptoFixer;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // Show all protocol rows when the screen allows it, but never exceed the work area.
        var workArea = SystemParameters.WorkArea;
        Height = Math.Max(MinHeight, Math.Min(1040, workArea.Height - 32));
        Width = Math.Max(MinWidth, Math.Min(1180, workArea.Width - 32));

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm is not null && !_vm.ConfirmClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm?.Shutdown();
        base.OnClosed(e);
    }

    /// <summary>Right-click opens the item's menu without selecting it, because selecting a machine connects to it.</summary>
    private void OnMachineListPreviewRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnMachinesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MachinesViewModel.IsPanelOpen))
        {
            UpdateMachinesPanel(animate: true);
        }
    }

    private const double MachinesPanelWidth = 272;

    /// <summary>Slides the machines panel in from the left, or out and then collapses it so it leaves the tab order.</summary>
    private void UpdateMachinesPanel(bool animate)
    {
        var open = _vm?.Machines.IsPanelOpen == true;
        var target = open ? MachinesPanelWidth : 0;

        if (!animate)
        {
            MachinesPanel.BeginAnimation(WidthProperty, null);
            MachinesPanel.Width = target;
            MachinesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (open)
        {
            MachinesPanel.Visibility = Visibility.Visible;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromSeconds(open ? 0.18 : 0.15))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            // A later toggle may have replaced this animation; only finish if the state still matches.
            if (_vm?.Machines.IsPanelOpen != open)
            {
                return;
            }

            MachinesPanel.BeginAnimation(WidthProperty, null);
            MachinesPanel.Width = target;
            if (open)
            {
                Dispatcher.BeginInvoke(() => MachineFilterBox.Focus(), DispatcherPriority.Input);
            }
            else
            {
                MachinesPanel.Visibility = Visibility.Collapsed;
            }
        };
        MachinesPanel.BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Machines.PropertyChanged -= OnMachinesPropertyChanged;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Machines.PropertyChanged += OnMachinesPropertyChanged;
        }

        UpdateColumnVisibility();
        UpdateSortArrows();
        ApplyDisplayMode();
        UpdateMachinesPanel(animate: false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.DisplayMode):
                ApplyDisplayMode();
                break;

            case nameof(MainViewModel.ShowServerColumn):
            case nameof(MainViewModel.ShowClientColumn):
                UpdateColumnVisibility();
                break;

            case nameof(MainViewModel.SortKey):
            case nameof(MainViewModel.SortDescending):
                // The DataGrid resets header arrows when the view's sort changes; set them once that has happened.
                Dispatcher.BeginInvoke(UpdateSortArrows, DispatcherPriority.Loaded);
                break;
        }
    }

    private void UpdateColumnVisibility()
    {
        ServerColumn.Visibility = _vm?.ShowServerColumn == false ? Visibility.Collapsed : Visibility.Visible;
        ClientColumn.Visibility = _vm?.ShowClientColumn == false ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Standard and Compact share the grid with different cell templates; List uses its own items control (bound in XAML).</summary>
    private void ApplyDisplayMode()
    {
        var compact = _vm?.DisplayMode == TableDisplayMode.Compact;

        ProtocolColumn.CellTemplate = ResourceTemplate(compact ? "CompactProtocolCellTemplate" : "ProtocolCellTemplate");
        ServerColumn.CellTemplate = ResourceTemplate(compact ? "CompactServerCellTemplate" : "ServerCellTemplate");
        ClientColumn.CellTemplate = ResourceTemplate(compact ? "CompactClientCellTemplate" : "ClientCellTemplate");
        ResetColumn.CellTemplate = ResourceTemplate(compact ? "CompactResetCellTemplate" : "ResetCellTemplate");

        ProtocolColumn.Width = new DataGridLength(compact ? 1 : 3, DataGridLengthUnitType.Star);
        ProtocolColumn.MinWidth = compact ? 258 : 220;
        ServerColumn.MinWidth = ClientColumn.MinWidth = compact ? 250 : 230;
        // Without a minimum the grid squeezes this Auto column (clipping the link) instead of scrolling horizontally.
        ResetColumn.MinWidth = compact ? 56 : 140;

        ProtocolGrid.ColumnHeaderStyle = (Style)FindResource(compact ? "CompactHeaderStyle" : "HeaderStyle");
        if (compact)
        {
            ProtocolGrid.FontSize = 12;
        }
        else
        {
            ProtocolGrid.ClearValue(FontSizeProperty);
        }
    }

    private DataTemplate ResourceTemplate(string key) => (DataTemplate)FindResource(key);

    private void UpdateSortArrows()
    {
        foreach (var column in ProtocolGrid.Columns)
        {
            column.SortDirection = null;
        }

        if (_vm is null)
        {
            return;
        }

        DataGridColumn? sorted = _vm.SortKey switch
        {
            ProtocolSortKey.Name => ProtocolColumn,
            ProtocolSortKey.ServerState or ProtocolSortKey.ServerSource => ServerColumn,
            ProtocolSortKey.ClientState or ProtocolSortKey.ClientSource => ClientColumn,
            _ => null,
        };

        if (sorted is not null)
        {
            sorted.SortDirection = _vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        }
    }

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        // Sorting is done by the view model's comparer, not by DataGrid's SortDescriptions.
        e.Handled = true;

        ProtocolSortKey? key =
            e.Column == ProtocolColumn ? ProtocolSortKey.Name :
            e.Column == ServerColumn ? ProtocolSortKey.ServerState :
            e.Column == ClientColumn ? ProtocolSortKey.ClientState :
            null;

        if (key is { } k)
        {
            _vm?.SortByColumn(k);
        }
    }

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = PresetsButton,
            Placement = PlacementMode.Top,
        };

        menu.Items.Add(SectionHeader("Built-in presets"));
        foreach (var preset in _vm.BuiltInPresetList)
        {
            menu.Items.Add(PresetItem(preset, _vm.StagePresetCommand));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(SectionHeader($"Saved presets  ({PresetFile.FileName})"));
        if (_vm.PresetsError is not null)
        {
            menu.Items.Add(Note("The presets file can't be read. Edit or reload it, or save a preset to start a new file."));
        }
        else if (_vm.UserPresets.Count == 0)
        {
            menu.Items.Add(Note("No saved presets yet."));
        }
        else
        {
            foreach (var preset in _vm.UserPresets)
            {
                menu.Items.Add(PresetItem(preset, _vm.StagePresetCommand));
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Save current settings as preset…", Command = _vm.SavePresetCommand });

        var delete = new MenuItem { Header = "Delete preset", IsEnabled = _vm.UserPresets.Count > 0 };
        foreach (var preset in _vm.UserPresets)
        {
            delete.Items.Add(Named(new MenuItem { Header = Literal(preset.Name), Command = _vm.DeletePresetCommand, CommandParameter = preset }, preset.Name));
        }

        menu.Items.Add(delete);
        menu.Items.Add(new MenuItem
        {
            Header = _vm.PresetsFileExists ? "Edit presets file in Notepad" : "Create presets file and open in Notepad",
            Command = _vm.EditPresetsFileCommand,
            ToolTip = _vm.PresetsPath,
        });
        menu.Items.Add(new MenuItem { Header = "Reload presets file", Command = _vm.ReloadPresetsCommand, ToolTip = _vm.PresetsPath });

        menu.IsOpen = true;
    }

    private void OnUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var updates = _vm.Updates;
        var menu = new ContextMenu
        {
            PlacementTarget = UpdatesButton,
            Placement = PlacementMode.Bottom,
        };

        menu.Items.Add(SectionHeader($"CryptoFixer {updates.CurrentVersion}"));
        if (updates.AvailableRelease is { } release)
        {
            menu.Items.Add(new MenuItem { Header = $"Install CryptoFixer {release.Version}…", Command = updates.InstallCommand, FontWeight = FontWeights.SemiBold });
        }

        menu.Items.Add(new MenuItem { Header = "Check for updates now", Command = updates.CheckNowCommand });
        var atStartup = new MenuItem
        {
            Header = "Check for updates at startup",
            IsCheckable = true,
            IsChecked = _vm.CheckForUpdatesAtStartup,
            ToolTip = $"Stored as \"checkForUpdates\" in {_vm.SettingsPath}",
        };
        atStartup.Click += (_, _) => _vm.CheckForUpdatesAtStartup = atStartup.IsChecked;
        menu.Items.Add(atStartup);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Open releases page", Command = updates.OpenReleasesPageCommand, ToolTip = updates.ReleasesPage.AbsoluteUri });

        menu.IsOpen = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not { CheckForUpdatesAtStartup: true } vm)
        {
            return;
        }

        // Let the window finish drawing first; the check runs in the background and only updates the header.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (vm.CheckForUpdatesAtStartup)
        {
            await vm.Updates.CheckAsync(userInitiated: false);
        }
    }

    private static MenuItem PresetItem(ProtocolPreset preset, ICommand stage)
    {
        var item = new MenuItem
        {
            Header = Literal(PresetText.MenuLabel(preset)),
            Command = stage,
            CommandParameter = preset,
            ToolTip = new TextBlock { Text = PresetText.Describe(preset), MaxWidth = 460, TextWrapping = TextWrapping.Wrap },
        };
        AutomationProperties.SetName(item, PresetText.MenuLabel(preset));
        ToolTipService.SetShowDuration(item, 60_000);
        return item;
    }

    private static MenuItem SectionHeader(string text) =>
        Named(new() { Header = Literal(text), IsEnabled = false, FontWeight = FontWeights.SemiBold }, text);

    private static MenuItem Note(string text) =>
        Named(new() { Header = new TextBlock { Text = text, FontStyle = FontStyles.Italic, MaxWidth = 360, TextWrapping = TextWrapping.Wrap }, IsEnabled = false }, text);

    /// <summary>A TextBlock header keeps underscores in user-chosen names from becoming access keys.</summary>
    private static TextBlock Literal(string text) => new() { Text = text };

    /// <summary>Non-string headers aren't exposed to screen readers, so name the item explicitly.</summary>
    private static MenuItem Named(MenuItem item, string name)
    {
        AutomationProperties.SetName(item, name);
        return item;
    }
}
