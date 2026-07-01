using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.HtmlRenderer;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DotPad.Contracts;
using DotPad.Models;
using DotPad.Services;
using DotPad.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace DotPad.Views;

public partial class MainWindow : Window, IDisposable
{
    private readonly IThemeService     _themeService;
    private readonly IEditorTabFactory _tabFactory;
    private readonly IServiceProvider  _serviceProvider;

    private readonly Dictionary<EditorTabViewModel, (TabItem Tab, EditorTabControl Control)> _tabs = new();
    private EditorTabViewModel? _prevActiveVm;

    // Shared output / status controls
    private HtmlPanel? _htmlPanel;
    private TextBlock? _statusText;
    private TextBlock? _elapsedText;
    private TextBlock? _executionStatusText;

    public MainWindow(
        IThemeService      themeService,
        IEditorTabFactory  tabFactory,
        IServiceProvider   serviceProvider,
        MainWindowViewModel viewModel)
    {
        _themeService    = themeService;
        _tabFactory      = tabFactory;
        _serviceProvider = serviceProvider;
        DataContext      = viewModel;

        InitializeComponent();

        _themeService.ThemeChanged += OnThemeChanged;
        InitializeSharedControls(viewModel);
    }

    private void InitializeSharedControls(MainWindowViewModel vm)
    {
        _htmlPanel           = this.FindControl<HtmlPanel>("HtmlPanel");
        _statusText          = this.Find<TextBlock>("StatusText");
        _elapsedText         = this.Find<TextBlock>("ElapsedText");
        _executionStatusText = this.Find<TextBlock>("ExecutionStatusText");

        _htmlPanel?.Container.AddFontFamily(
            new FontFamily(new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));

        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;
        fileTabControl.SelectionChanged += OnFileTabSelectionChanged;

        vm.Tabs.CollectionChanged += OnTabsCollectionChanged;
        vm.WhenAnyValue(x => x.ActiveTab).Subscribe(OnActiveTabChanged);

        var fileTree = this.FindControl<TreeView>("FileTree");
        if (fileTree is not null)
            fileTree.SelectionChanged += FileTree_SelectionChanged;

        vm.OpenTab(filePath: null);
    }

    // ── Tab lifecycle ─────────────────────────────────────────────────────────

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;

        if (e.NewItems is not null)
        {
            foreach (EditorTabViewModel vm in e.NewItems)
            {
                var ctrl = _tabFactory.Create(vm);
                ctrl.StateChanged += OnTabStateChanged;

                var tab = new TabItem { Header = BuildTabHeader(vm), Content = ctrl };
                _tabs[vm] = (tab, ctrl);
                fileTabControl.Items.Add(tab);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (EditorTabViewModel vm in e.OldItems)
            {
                if (!_tabs.TryGetValue(vm, out var entry)) continue;
                entry.Control.StateChanged -= OnTabStateChanged;
                entry.Control.SaveIfNeeded();
                entry.Control.Dispose();
                fileTabControl.Items.Remove(entry.Tab);
                _tabs.Remove(vm);
            }
        }
    }

    private StackPanel BuildTabHeader(EditorTabViewModel vm)
    {
        var label = new TextBlock
        {
            Text = vm.FileName,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        var closeBtn = new Button
        {
            Content = "×",
            Classes = { "TabClose" },
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.Click += (_, _) => vm.CloseCommand.Execute(Unit.Default).Subscribe();

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 4,
            Children    = { label, closeBtn }
        };
    }

    // ── Selection sync ────────────────────────────────────────────────────────

    private void OnFileTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc) return;
        var selectedVm = _tabs.FirstOrDefault(kv => kv.Value.Tab == tc.SelectedItem as TabItem).Key;
        GetViewModel().ActiveTab = selectedVm;
    }

    private void OnActiveTabChanged(EditorTabViewModel? vm)
    {
        // Unsubscribe caret from previous
        if (_prevActiveVm is not null && _tabs.TryGetValue(_prevActiveVm, out var prev))
            prev.Control.State.Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;

        _prevActiveVm = vm;

        if (vm is null) return;

        // Select the tab in the control
        var fileTabControl = this.FindControl<TabControl>("FileTabControl")!;
        if (_tabs.TryGetValue(vm, out var entry) && fileTabControl.SelectedItem != entry.Tab)
            fileTabControl.SelectedItem = entry.Tab;

        // Subscribe caret and sync UI
        if (_tabs.TryGetValue(vm, out entry))
        {
            entry.Control.State.Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
            SyncSharedUi(vm, entry.Control);
        }
    }

    private void OnTabStateChanged(EditorTabControl ctrl)
    {
        var activeVm = GetViewModel().ActiveTab;
        if (activeVm is not null &&
            _tabs.TryGetValue(activeVm, out var entry) &&
            entry.Control == ctrl)
            SyncSharedUi(activeVm, ctrl);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_statusText is null) return;
        var activeVm = GetViewModel().ActiveTab;
        if (activeVm is null || !_tabs.TryGetValue(activeVm, out var entry)) return;
        var ed = entry.Control.State.Editor;
        _statusText.Text = $"Line {ed.TextArea.Caret.Line} Column {ed.TextArea.Caret.Column}";
    }

    // ── Shared UI sync ────────────────────────────────────────────────────────

    private void SyncSharedUi(EditorTabViewModel tabVm, EditorTabControl ctrl)
    {
        var vm = GetViewModel();
        vm.IsPaused    = tabVm.IsPaused;
        vm.IsDebugging = tabVm.IsDebugging;
        vm.DebugInfo   = tabVm.DebugInfo;

        if (_elapsedText is not null)
            _elapsedText.Text = ctrl.State.ElapsedText;

        if (_executionStatusText is not null)
        {
            _executionStatusText.Text       = ctrl.State.ExecStatusText;
            _executionStatusText.Foreground = string.IsNullOrEmpty(ctrl.State.ExecStatusColor)
                ? Brushes.Transparent
                : GetThemeBrush(ctrl.State.ExecStatusColor) ?? Brushes.Transparent;
        }

        if (_htmlPanel is not null)
            _htmlPanel.Text = $"""
                <html>
                <head><style>{ctrl.State.OutputCss}</style></head>
                <body>{ctrl.State.OutputContent}</body>
                </html>
                """;
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void OnThemeChanged(ActiveTheme theme)
    {
        var editorBg = GetThemeBrush("DotPad.Editor.BackgroundBrush");
        foreach (var (_, ctrl) in _tabs.Values)
            ctrl.ApplyTheme(theme, editorBg);

        var activeVm = GetViewModel().ActiveTab;
        if (activeVm is not null && _tabs.TryGetValue(activeVm, out var entry))
            SyncSharedUi(activeVm, entry.Control);
    }

    // ── Debug panel handlers (shared output panel) ────────────────────────────

    private void DbgContinue_Click(object? sender, RoutedEventArgs e) => GetActiveControl()?.ResumeDebug(DebugAction.Continue);
    private void DbgStepOver_Click(object? sender, RoutedEventArgs e) => GetActiveControl()?.ResumeDebug(DebugAction.StepOver);
    private void DbgStepIn_Click(object? sender, RoutedEventArgs e)   => GetActiveControl()?.ResumeDebug(DebugAction.StepIn);
    private void DbgStepOut_Click(object? sender, RoutedEventArgs e)  => GetActiveControl()?.ResumeDebug(DebugAction.StepOut);

    private async void DbgPause_Click(object? sender, RoutedEventArgs e)
    {
        if (GetActiveControl() is { } ctrl) await ctrl.PauseAsync();
    }

    private void NewFile_Click(object? sender, RoutedEventArgs e)
        => GetViewModel().NewFile();

    private void Preferences_Click(object sender, RoutedEventArgs e)
        // Defer to next tick so the button's pending pointer events (PointerReleased,
        // PointerCaptureLost) fully process before the dialog opens — prevents
        // "PlatformImpl is null" from OS-queued events hitting the closing popup.
        => Dispatcher.UIThread.Post(() =>
            _serviceProvider.GetRequiredService<PreferencesWindow>().ShowDialog(this));

    // ── Tree view ─────────────────────────────────────────────────────────────

    private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not Node { SubNodes: null } node) return;
        GetViewModel().OpenTab(node.FilePath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private EditorTabControl? GetActiveControl()
    {
        var vm = GetViewModel().ActiveTab;
        return vm is not null && _tabs.TryGetValue(vm, out var e) ? e.Control : null;
    }

    private IBrush? GetThemeBrush(string key)
    {
        object? val = null;
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out val);
        return val as IBrush;
    }

    private MainWindowViewModel GetViewModel()
        => DataContext as MainWindowViewModel
            ?? throw new InvalidOperationException(
                $"Expected {nameof(MainWindowViewModel)}, got {DataContext?.GetType().Name ?? "null"}");

    public void Dispose()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        foreach (var (_, ctrl) in _tabs.Values)
        {
            ctrl.StateChanged -= OnTabStateChanged;
            ctrl.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
