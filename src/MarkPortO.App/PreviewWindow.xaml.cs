// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPortO.
//
// MarkPortO is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPortO.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MarkPortO.App.Services;
using MarkPortO.App.ViewModels;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;

namespace MarkPortO.App;

/// <summary>
/// The preview of a converted file (FR-068): the pages on the left, the image of the page above and its Markdown below.
/// While the file is being converted it follows the work: the list is read again when the file changes, and the page that is
/// being read is selected as long as "follow" is checked.
/// </summary>
public partial class PreviewWindow : Window
{
    private readonly FileItemViewModel _item;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<PreviewItem> _items = [];
    private bool _selectingByProgram;
    private int _version;

    public PreviewWindow(FileItemViewModel item)
    {
        InitializeComponent();
        _item = item;
        Title = UiText.Get("Preview_Title", item.FileName);
        PageList.ItemsSource = _items;
        RightDragScroll.Attach(ImageScroll);

        // The pages are read again, at most every half second, when the file changes while it is converted.
        _refresh.Tick += async (_, _) =>
        {
            _refresh.Stop();
            await ReloadAsync();
        };
        _item.PropertyChanged += OnItemChanged;
        Closed += (_, _) =>
        {
            _item.PropertyChanged -= OnItemChanged;
            _refresh.Stop();
            TextPane.Release();
            VlmPane.Release();
        };

        TextPane.PageStepRequested += Step;
        VlmPane.PageStepRequested += Step;
        TextPane.Header = UiText.Get("Preview_PaneText");
        VlmPane.Header = UiText.Get("Preview_PaneVlm");
        ShowVlmBox.IsChecked = _item.Source is { Step3: not StepStatus.NotApplicable };
        FollowBox.IsChecked = _item.Source is { Step3: StepStatus.Running or StepStatus.Pending };
        Loaded += async (_, _) =>
        {
            await Task.WhenAll(TextPane.InitialiseAsync(), VlmPane.InitialiseAsync());
            if (TextPane.WebFailed)
            {
                TextRadio.IsChecked = true;
                RenderedRadio.IsEnabled = false;
            }

            Arrange();
            await ReloadAsync();
        };
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_refresh.IsEnabled)
        {
            _refresh.Start();
        }
    }

    private async Task ReloadAsync()
    {
        if (_item.Source is not { } document)
        {
            return;
        }

        var version = ++_version;
        var pages = await Task.Run(() => PagePreview.Load(document));
        if (version != _version)
        {
            return;
        }

        var selected = (PageList.SelectedItem as PreviewItem)?.Page.Index;
        var vlmOn = document.Step3 != StepStatus.NotApplicable;
        for (var i = 0; i < pages.Count; i++)
        {
            if (i < _items.Count)
            {
                _items[i].Update(pages[i], vlmOn);
            }
            else
            {
                _items.Add(new PreviewItem(pages[i], vlmOn));
            }
        }

        while (_items.Count > pages.Count)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        PageList.Items.Refresh();

        // Follow the work: the latest page that is finished (read, or failed) when the VLM is used; the last page otherwise.
        // Without a finished page yet, the choice stays as it is.
        var target = selected;
        if (FollowBox.IsChecked == true && pages.Count > 0)
        {
            var finished = vlmOn ? pages.LastOrDefault(p => !p.IsDeleted && p.Step3 is PageStep3Status.Done or PageStep3Status.Failed) : pages[^1];
            target = finished?.Index ?? target;
        }

        target ??= pages.FirstOrDefault()?.Index;
        var item = _items.FirstOrDefault(p => p.Page.Index == target);
        if (item is not null && !ReferenceEquals(item, PageList.SelectedItem))
        {
            _selectingByProgram = true;
            PageList.SelectedItem = item;
            PageList.ScrollIntoView(item);
            _selectingByProgram = false;
        }

        // The selected page may have new content (its VLM result, its image).
        await ShowAsync();
    }

    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        // A page that the user chooses stops the following; the program's own choice does not.
        if (!_selectingByProgram && IsLoaded)
        {
            FollowBox.IsChecked = false;
        }

        _ = ShowAsync();
    }

    private async Task ShowAsync()
    {
        if (PageList.SelectedItem is not PreviewItem item)
        {
            PageImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
            TextPane.Show(string.Empty, string.Empty, false);
            VlmPane.Show(string.Empty, string.Empty, false);
            return;
        }

        var page = item.Page;
        ShowImage(page.ImagePath);

        var folder = _item.Source?.OutputFolder ?? string.Empty;
        var rendered = RenderedRadio.IsChecked == true;
        var text = PagePreview.WithoutVlm(page.Markdown);
        TextPane.Show(text.Length > 0 ? text : UiText.Get("Preview_NoMarkdown"), folder, rendered);
        var vlm = page.Vlm ?? (page.Step3 == PageStep3Status.Failed ? UiText.Get("Preview_VlmFailed") : UiText.Get("Preview_NoVlm"));
        VlmPane.Show(vlm, folder, rendered);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Places the chosen panes in the grid, side by side or stacked. The panes stay in the grid (a WebView2 does not like to be moved);
    /// the chosen ones take slots 0, 2, 4 and the splitters between them slots 1 and 3; the other slots have no size.
    /// </summary>
    private void Arrange()
    {
        var sideBySide = SideBySideRadio.IsChecked == true;
        var chosen = new List<FrameworkElement>();
        if (ShowImageBox.IsChecked == true) { chosen.Add(ImagePane); }
        if (ShowTextBox.IsChecked == true) { chosen.Add(TextPane); }
        if (ShowVlmBox.IsChecked == true) { chosen.Add(VlmPane); }

        PaneGrid.ColumnDefinitions.Clear();
        PaneGrid.RowDefinitions.Clear();
        for (var slot = 0; slot < 5; slot++)
        {
            var isPane = slot % 2 == 0;
            var used = isPane ? slot / 2 < chosen.Count : slot / 2 < chosen.Count - 1;
            var length = !used ? new GridLength(0) : isPane ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            if (sideBySide)
            {
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = length, MinWidth = used && isPane ? 80 : 0 });
            }
            else
            {
                PaneGrid.RowDefinitions.Add(new RowDefinition { Height = length, MinHeight = used && isPane ? 60 : 0 });
            }
        }

        FrameworkElement[] all = [ImagePane, TextPane, VlmPane];
        foreach (var pane in all)
        {
            pane.Visibility = chosen.Contains(pane) ? Visibility.Visible : Visibility.Collapsed;
        }

        for (var k = 0; k < chosen.Count; k++)
        {
            Place(chosen[k], k * 2, sideBySide);
        }

        GridSplitter[] splitters = [Split0, Split1];
        for (var k = 0; k < splitters.Length; k++)
        {
            var splitter = splitters[k];
            splitter.Visibility = k < chosen.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
            Place(splitter, k * 2 + 1, sideBySide);
            splitter.ResizeDirection = sideBySide ? GridResizeDirection.Columns : GridResizeDirection.Rows;
            splitter.Width = sideBySide ? 6 : double.NaN;
            splitter.Height = sideBySide ? double.NaN : 6;
            splitter.HorizontalAlignment = sideBySide ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            splitter.VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        }
    }

    private static void Place(UIElement element, int slot, bool sideBySide)
    {
        Grid.SetColumn(element, sideBySide ? slot : 0);
        Grid.SetRow(element, sideBySide ? 0 : slot);
    }

    private void OnLayoutChanged(object sender, RoutedEventArgs e)
    {
        // The events also come while the window is being made from the XAML, before every control exists.
        if (IsLoaded)
        {
            Arrange();
        }
    }

    private void ShowImage(string? path)
    {
        if (path is null)
        {
            PageImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            // Read the file at once, so that it is not locked (the conversion may write it again).
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            PageImage.Source = image;
            NoImageText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            PageImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
        }
    }

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _ = ShowAsync();
        }
    }

    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        // The event also comes while the window is being made from the XAML, before the image and its scroll viewer exist.
        if (ImageScroll is null || PageImage is null)
        {
            return;
        }

        var fit = FitBox.IsChecked == true;
        PageImage.Stretch = fit ? System.Windows.Media.Stretch.Uniform : System.Windows.Media.Stretch.None;
        var bars = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ImageScroll.HorizontalScrollBarVisibility = bars;
        ImageScroll.VerticalScrollBarVisibility = bars;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    // The left and right arrow keys, and the wheel at the end of what a pane can scroll, go to the page before or after.
    private DateTime _lastScroll = DateTime.MinValue;
    private DateTime _lastStep = DateTime.MinValue;

    private void Step(int delta)
    {
        var index = Math.Clamp(PageList.SelectedIndex + delta, 0, Math.Max(0, _items.Count - 1));
        if (_items.Count == 0 || index == PageList.SelectedIndex)
        {
            return;
        }

        PageList.SelectedIndex = index;
        PageList.ScrollIntoView(PageList.SelectedItem);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.Left or Key.Right))
        {
            return;
        }

        Step(e.Key == Key.Right ? 1 : -1);
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Delta == 0)
        {
            return;
        }

        var down = e.Delta < 0;
        var scroller = ScrollerOf(e.OriginalSource as DependencyObject);
        var now = DateTime.UtcNow;
        var canScroll = scroller is not null && (down ? scroller.VerticalOffset < scroller.ScrollableHeight - 0.5 : scroller.VerticalOffset > 0.5);
        if (canScroll)
        {
            _lastScroll = now; // the pane scrolls as it does
            return;
        }

        // At the end (or where nothing scrolls) the wheel changes the page, after a short pause so that a scroll that reached the end does not go on to the next page.
        e.Handled = true;
        if (now - _lastScroll < TimeSpan.FromMilliseconds(400) || now - _lastStep < TimeSpan.FromMilliseconds(200))
        {
            return;
        }

        _lastStep = now;
        Step(down ? 1 : -1);
    }

    private static ScrollViewer? ScrollerOf(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ScrollViewer viewer)
            {
                return viewer;
            }

            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    /// <summary>One line of the list of pages.</summary>
    private sealed class PreviewItem(PreviewPage page, bool vlmOn)
    {
        public PreviewPage Page { get; private set; } = page;

        public string Title { get; private set; } = TitleOf(page, vlmOn);

        public void Update(PreviewPage next, bool vlmOnNow)
        {
            Page = next;
            Title = TitleOf(next, vlmOnNow);
        }

        private static string TitleOf(PreviewPage page, bool vlmOn)
        {
            var mark = page.IsDeleted ? "  ×"
                : !vlmOn ? string.Empty
                : page.Step3 switch
                {
                    PageStep3Status.Done => "  ✓",
                    PageStep3Status.Failed => "  !",
                    PageStep3Status.Running => "  ▶",
                    _ => "  …",
                };
            return $"{page.Index}  {MarkPortO.Core.Localization.PageLabel.Text(page.Label, System.Globalization.CultureInfo.CurrentUICulture)}{mark}";
        }
    }
}
