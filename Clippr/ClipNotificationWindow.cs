using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Clippr;

internal enum ClipNotificationKind { Ready, Saved, Error }

internal sealed class ClipNotificationWindow : Window
{
    private const int CardWidth = 402;
    private const int CardHeight = 112;
    private const int VisibleMilliseconds = 4900;
    private readonly DispatcherQueueTimer _timer;
    private readonly Border _remainingBar;
    private readonly Border _iconBorder;
    private readonly Border _leftGlow;
    private readonly Border _accentStrip;
    private readonly FontIcon _icon;
    private readonly TextBlock _titleText;
    private readonly TextBlock _messageText;
    private readonly LinearGradientBrush _glowBrush;
    private readonly int _startX;
    private readonly int _targetX;
    private readonly int _top;
    private DateTime _phaseStarted;
    private bool _exiting;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    public ClipNotificationWindow(Window owner, ClipNotificationKind kind, string title, string message,
        Action<ClipNotificationWindow> onClosed)
    {
        Title = $"ClipDat · {title}";
        var color = GetAccentColor(kind);
        var accent = new SolidColorBrush(color);
        _icon = new FontIcon
        {
            Glyph = GetGlyph(kind),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 21,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 241, 247, 252)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 },
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Dismiss notification");
        close.Click += (_, _) => Dismiss();

        var body = new Grid { ColumnSpacing = 14, Padding = new Thickness(18, 13, 15, 17) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        _iconBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = accent,
            Child = _icon,
            VerticalAlignment = VerticalAlignment.Center
        };
        body.Children.Add(_iconBorder);
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        _titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 245, 247, 249))
        };
        text.Children.Add(_titleText);
        _messageText = new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 182, 191, 200))
        };
        text.Children.Add(_messageText);
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        Grid.SetColumn(close, 2);
        body.Children.Add(close);
        _glowBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        _glowBrush.GradientStops.Add(new GradientStop
        {
            Color = ColorHelper.FromArgb(88, color.R, color.G, color.B), Offset = 0
        });
        _glowBrush.GradientStops.Add(new GradientStop
        {
            Color = ColorHelper.FromArgb(0, color.R, color.G, color.B), Offset = 1
        });
        var surface = new Grid();
        _leftGlow = new Border
        {
            Width = 145,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(12, 0, 0, 12),
            Background = _glowBrush,
            IsHitTestVisible = false
        };
        surface.Children.Add(_leftGlow);
        _accentStrip = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent,
            IsHitTestVisible = false
        };
        surface.Children.Add(_accentStrip);
        surface.Children.Add(body);
        surface.Children.Add(new Border
        {
            Height = 3,
            Margin = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 53, 62)),
            IsHitTestVisible = false
        });
        _remainingBar = new Border
        {
            Width = CardWidth - 32,
            Height = 3,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = accent,
            IsHitTestVisible = false
        };
        surface.Children.Add(_remainingBar);
        var card = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 24, 29, 37)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 58, 69)),
            BorderThickness = new Thickness(1),
            Child = surface
        };
        Content = card;

        var work = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _startX = work.X + work.Width + 4;
        _targetX = work.X + work.Width - CardWidth - 18;
        _top = work.Y + 20;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
        }
        AppWindow.MoveAndResize(new RectInt32(_startX, _top, CardWidth, CardHeight));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnAnimationTick;
        Closed += (_, _) =>
        {
            _timer.Stop();
            onClosed(this);
        };
        Activate();
        // Windows 11 can draw a bright DWM focus border even for a borderless popup.
        // COLOR_NONE removes that frame while retaining the app-drawn card outline.
        uint noBorderColor = 0xFFFFFFFE;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 34,
            ref noBorderColor, sizeof(uint));
        owner.Activate();
        _phaseStarted = DateTime.UtcNow;
        _timer.Start();
    }

    public void Dismiss()
    {
        if (_exiting) return;
        _exiting = true;
        _phaseStarted = DateTime.UtcNow;
    }

    public void Update(ClipNotificationKind kind, string title, string message)
    {
        var color = GetAccentColor(kind);
        Title = $"ClipDat · {title}";
        _titleText.Text = title;
        _messageText.Text = message;
        _icon.Glyph = GetGlyph(kind);
        _iconBorder.Background = new SolidColorBrush(color);
        _accentStrip.Background = new SolidColorBrush(color);
        foreach (var stop in _glowBrush.GradientStops)
            stop.Color = ColorHelper.FromArgb(stop.Offset == 0 ? (byte)88 : (byte)0, color.R, color.G, color.B);
        _remainingBar.Background = new SolidColorBrush(color);
        if (_exiting)
        {
            _exiting = false;
            _phaseStarted = DateTime.UtcNow;
            _remainingBar.Width = CardWidth - 32;
        }
    }

    private static string GetGlyph(ClipNotificationKind kind) => kind switch
    {
        ClipNotificationKind.Saved => "\uE73E",
        ClipNotificationKind.Error => "\uE783",
        _ => "\uE768"
    };

    private static Windows.UI.Color GetAccentColor(ClipNotificationKind kind) => kind switch
    {
        ClipNotificationKind.Saved => ColorHelper.FromArgb(255, 46, 152, 240),
        ClipNotificationKind.Error => ColorHelper.FromArgb(255, 232, 71, 78),
        _ => ColorHelper.FromArgb(255, 67, 190, 132)
    };

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = (DateTime.UtcNow - _phaseStarted).TotalMilliseconds;
        if (!_exiting)
            _remainingBar.Width = (CardWidth - 32) * Math.Clamp(1 - elapsed / VisibleMilliseconds, 0, 1);
        if (!_exiting && elapsed >= VisibleMilliseconds)
        {
            Dismiss();
            elapsed = 0;
        }
        if (_exiting)
        {
            _remainingBar.Width = 0;
            var progress = Math.Clamp(elapsed / 260, 0, 1);
            AppWindow.Move(new PointInt32((int)Math.Round(_targetX + (_startX - _targetX) * progress), _top));
            if (progress >= 1) Close();
        }
        else
        {
            var progress = Math.Clamp(elapsed / 320, 0, 1);
            var easeOut = 1 - Math.Pow(1 - progress, 3);
            AppWindow.Move(new PointInt32((int)Math.Round(_startX + (_targetX - _startX) * easeOut), _top));
        }
    }
}
