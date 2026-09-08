using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace S880Tray;

internal static class ThemeService
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("SurfaceBrush", "#F4F6F7", "#181D1F"), ("PanelBrush", "#FFFFFF", "#232B2F"),
        ("PanelBorderBrush", "#CFD8DC", "#3C484E"), ("DividerBrush", "#DFE5E8", "#3A464B"),
        ("TextBrush", "#202F35", "#E0E8EA"), ("MutedBrush", "#697780", "#ACBDC4"),
        ("MicroBrush", "#73858D", "#9EB1B8"), ("AccentBrush", "#1F6E82", "#8CD9D4"),
        ("AccentHoverBrush", "#124E62", "#BDEBE8"), ("SelectedBrush", "#DCECF0", "#2C3C40"),
        ("SelectionBorderBrush", "#28788E", "#8CD9D4"), ("ControlBrush", "#F8FAFB", "#293237"),
        ("ControlBorderBrush", "#D1DCE0", "#46575F"), ("ControlTextBrush", "#243A43", "#DFEAEE"),
        ("DetailsBrush", "#ECF1F3", "#28373E"), ("DetailsTextBrush", "#4C626B", "#CBDCE3"),
        ("RailBrush", "#39889E", "#8CD9D4"), ("RailEmptyBrush", "#D9E3E7", "#45585F"),
        ("StatusIdleBrush", "#527F8C", "#89C8D5"), ("StatusBusyBrush", "#B28A3D", "#E0AF5F"),
        ("StatusErrorBrush", "#B45445", "#F2A090"), ("PresetAccentBrush", "#1F6E82", "#E0A45B")
    ];

    internal static void Apply(ResourceDictionary resources, bool dark)
    {
        foreach (var item in Palette) resources[item.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? item.Dark : item.Light));
    }

    internal static Color GetColor(ResourceDictionary resources, string key) => ((SolidColorBrush)resources[key]).Color;

    internal static void ApplyCaption(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = dark ? 1 : 0;
        try { _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
