using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm is not null && !_vm.ConfirmClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateColumnVisibility();
        UpdateSortArrows();
        ApplyDisplayMode();
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

        ProtocolColumn.Width = new DataGridLength(compact ? 1 : 3, DataGridLengthUnitType.Star);
        ProtocolColumn.MinWidth = compact ? 258 : 220;
        ServerColumn.MinWidth = ClientColumn.MinWidth = compact ? 250 : 230;

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
