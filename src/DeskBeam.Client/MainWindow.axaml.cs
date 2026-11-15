using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeskBeam.Client.Input;
using DeskBeam.Client.ViewModels;
using DeskBeam.Core.Protocol;

namespace DeskBeam.Client;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly HashSet<int> _pressedKeys = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        VideoImage.PointerPressed += OnVideoPointerPressed;
        VideoImage.PointerReleased += OnVideoPointerReleased;
        VideoImage.PointerMoved += OnVideoPointerMoved;
        VideoImage.PointerWheelChanged += OnVideoPointerWheel;
        VideoImage.KeyDown += OnVideoKeyDown;
        VideoImage.KeyUp += OnVideoKeyUp;
        VideoImage.LostFocus += OnVideoLostFocus;

        Closing += (_, _) => _viewModel.SaveSettings();
        Closed += (_, _) => _ = _viewModel.DisposeAsync();
    }

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        VideoImage.Focus();
        if (!_viewModel.IsConnected || !_viewModel.ForwardMouse)
            return;

        var point = e.GetCurrentPoint(VideoImage);
        if (!TryMapPoint(point.Position, out var x, out var y))
            return;

        e.Pointer.Capture(VideoImage);
        var button = ButtonFor(point.Properties.PointerUpdateKind, out _);
        _viewModel.SendPointer(PointerAction.Move, x, y);
        _viewModel.SendPointer(PointerAction.Down, x, y, button);
        e.Handled = true;
    }

    private void OnVideoPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_viewModel.IsConnected || !_viewModel.ForwardMouse)
            return;

        var point = e.GetCurrentPoint(VideoImage);
        if (!TryMapPoint(point.Position, out var x, out var y))
            return;

        e.Pointer.Capture(null);
        var button = ButtonFor(point.Properties.PointerUpdateKind, out _);
        _viewModel.SendPointer(PointerAction.Move, x, y);
        _viewModel.SendPointer(PointerAction.Up, x, y, button);
        e.Handled = true;
    }

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_viewModel.IsConnected || !_viewModel.ForwardMouse)
            return;

        if (!TryMapPoint(e.GetPosition(VideoImage), out var x, out var y))
            return;

        _viewModel.SendPointer(PointerAction.Move, x, y);
        e.Handled = true;
    }

    private void OnVideoPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_viewModel.IsConnected || !_viewModel.ForwardMouse)
            return;

        if (!TryMapPoint(e.GetPosition(VideoImage), out var x, out var y))
            return;

        var delta = (int)Math.Round(e.Delta.Y * 120);
        if (delta == 0)
            return;

        _viewModel.SendPointer(PointerAction.Move, x, y);
        _viewModel.SendPointer(PointerAction.Wheel, x, y, 0, delta);
        e.Handled = true;
    }

    private void OnVideoKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_viewModel.IsConnected || !_viewModel.ForwardKeyboard)
            return;

        var vk = KeyMap.ToVirtualKey(e.Key);
        if (vk == 0)
            return;

        if (_pressedKeys.Add(vk))
            _viewModel.SendKey(vk, true);
        e.Handled = true;
    }

    private void OnVideoKeyUp(object? sender, KeyEventArgs e)
    {
        var vk = KeyMap.ToVirtualKey(e.Key);
        if (vk == 0)
            return;

        if (_pressedKeys.Remove(vk))
            _viewModel.SendKey(vk, false);
        e.Handled = true;
    }

    private void OnVideoLostFocus(object? sender, RoutedEventArgs e)
    {
        // Make sure the host does not keep keys stuck down when focus leaves the video.
        foreach (var vk in _pressedKeys)
            _viewModel.SendKey(vk, false);
        _pressedKeys.Clear();
    }

    /// <summary>
    /// Maps a point inside the Image control to normalized (0..1) coordinates of the captured
    /// monitor, accounting for the letterboxing that Stretch=Uniform introduces.
    /// </summary>
    private bool TryMapPoint(Point point, out double normalizedX, out double normalizedY)
    {
        normalizedX = 0;
        normalizedY = 0;

        var frameWidth = _viewModel.FrameWidth;
        var frameHeight = _viewModel.FrameHeight;
        if (frameWidth <= 0 || frameHeight <= 0)
            return false;

        var bounds = VideoImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var scale = Math.Min(bounds.Width / frameWidth, bounds.Height / frameHeight);
        var contentWidth = frameWidth * scale;
        var contentHeight = frameHeight * scale;
        var offsetX = (bounds.Width - contentWidth) / 2;
        var offsetY = (bounds.Height - contentHeight) / 2;

        if (point.X < offsetX || point.Y < offsetY ||
            point.X > offsetX + contentWidth || point.Y > offsetY + contentHeight)
            return false;

        normalizedX = Math.Clamp((point.X - offsetX) / contentWidth, 0, 1);
        normalizedY = Math.Clamp((point.Y - offsetY) / contentHeight, 0, 1);
        return true;
    }

    private static int ButtonFor(PointerUpdateKind kind, out bool isDown)
    {
        isDown = true;
        switch (kind)
        {
            case PointerUpdateKind.LeftButtonPressed:
            case PointerUpdateKind.LeftButtonReleased:
                isDown = kind == PointerUpdateKind.LeftButtonPressed;
                return 1;
            case PointerUpdateKind.RightButtonPressed:
            case PointerUpdateKind.RightButtonReleased:
                isDown = kind == PointerUpdateKind.RightButtonPressed;
                return 2;
            case PointerUpdateKind.MiddleButtonPressed:
            case PointerUpdateKind.MiddleButtonReleased:
                isDown = kind == PointerUpdateKind.MiddleButtonPressed;
                return 3;
            case PointerUpdateKind.XButton1Pressed:
            case PointerUpdateKind.XButton1Released:
                isDown = kind == PointerUpdateKind.XButton1Pressed;
                return 4;
            case PointerUpdateKind.XButton2Pressed:
            case PointerUpdateKind.XButton2Released:
                isDown = kind == PointerUpdateKind.XButton2Pressed;
                return 5;
            default:
                isDown = false;
                return 1;
        }
    }
}
