using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace DevStrider.Desktop.Views;

public partial class StatsView : UserControl
{
    private StatsViewModel? _vm;

    public StatsView()
    {
        InitializeComponent();
        if (App.Services != null)
        {
            _vm = App.Services.GetService(typeof(StatsViewModel)) as StatsViewModel;
            DataContext = _vm;
            if (_vm != null)
            {
                _vm.Slots.CollectionChanged += (_, __) => Render();
                _vm.OwnerFilter.CollectionChanged += OwnerFilterChanged;
                AttachOwnerListeners();
            }
            Loaded += (_, __) => Render();
        }
    }

    private void OwnerFilterChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        AttachOwnerListeners();

    private void AttachOwnerListeners()
    {
        if (_vm == null) return;
        foreach (var item in _vm.OwnerFilter)
        {
            item.PropertyChanged -= OnOwnerToggle;
            item.PropertyChanged += OnOwnerToggle;
        }
    }

    private async void OnOwnerToggle(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(OwnerFilterItem.IsSelected))
            await _vm.ReloadAsync();
    }

    /// <summary>One smooth line per owner; deterministic colour by hashing owner name.</summary>
    private void Render()
    {
        if (_vm == null) return;
        var slots = _vm.Slots;
        if (slots.Count == 0)
        {
            Chart.Series = Array.Empty<ISeries>();
            Chart.XAxes = new[] { new Axis { Labels = Array.Empty<string>() } };
            return;
        }

        var owners = slots.SelectMany(s => s.CountsByOwner.Keys).Distinct().ToList();
        var series = new List<ISeries>(owners.Count);
        foreach (var owner in owners)
        {
            var values = slots.Select(s => (double)s.CountsByOwner.GetValueOrDefault(owner)).ToArray();
            series.Add(new LineSeries<double>
            {
                Name = owner,
                Values = values,
                GeometrySize = 0,
                LineSmoothness = 0.6,
                Stroke = new SolidColorPaint(ColorFor(owner)) { StrokeThickness = 2 },
                Fill = null
            });
        }
        Chart.Series = series;

        // 24 labels (every 6 buckets = every hour) for readability; 144 ticks would be a wall.
        var labels = slots
            .Select((s, idx) => (idx % 6 == 0) ? s.Label : "")
            .ToArray();
        Chart.XAxes = new[] { new Axis { Labels = labels, LabelsRotation = 0 } };
        Chart.YAxes = new[] { new Axis { MinLimit = 0 } };
    }

    private static SKColor ColorFor(string owner)
    {
        int h = 0;
        foreach (var c in owner) h = unchecked(h * 31 + c);
        var hue = ((h % 360) + 360) % 360;
        return SKColor.FromHsl(hue, 70, 50);
    }
}
