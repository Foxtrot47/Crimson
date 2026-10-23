using System.Numerics;
using Crimson.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Crimson.Utils;

public sealed partial class FadingArtworkBrush : XamlCompositionBrushBase
{
    public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
        nameof(ImageUrl), typeof(string), typeof(FadingArtworkBrush),
        new PropertyMetadata(null, (sender, _) => ((FadingArtworkBrush)sender).LoadArtwork()));

    private bool _connected;
    private LoadedImageSurface? _image;
    private CompositionSurfaceBrush? _surface;
    private CompositionLinearGradientBrush? _fade;

    public string? ImageUrl
    {
        get => GetValue(ImageUrlProperty) as string;
        set => SetValue(ImageUrlProperty, value);
    }

    protected override void OnConnected()
    {
        _connected = true;
        LoadArtwork();
    }

    protected override void OnDisconnected()
    {
        _connected = false;
        ReleaseArtwork();
    }

    private void LoadArtwork()
    {
        ReleaseArtwork();
        if (!_connected || !EpicEndpointPolicy.TryGetContentUri(ImageUrl, out var uri)) return;

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        _image = LoadedImageSurface.StartLoadFromUri(uri, new Size(1280, 720));
        _surface = compositor.CreateSurfaceBrush(_image);
        _surface.Stretch = CompositionStretch.UniformToFill;
        _fade = compositor.CreateLinearGradientBrush();
        _fade.StartPoint = Vector2.Zero;
        _fade.EndPoint = new Vector2(1, 0);
        _fade.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.White));
        _fade.ColorStops.Add(compositor.CreateColorGradientStop(0.35f, Colors.White));
        _fade.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));

        // Fade the artwork's alpha into the real backdrop without fading the graph.
        var mask = compositor.CreateMaskBrush();
        mask.Source = _surface;
        mask.Mask = _fade;
        CompositionBrush = mask;
    }

    private void ReleaseArtwork()
    {
        var brush = CompositionBrush;
        CompositionBrush = null;
        brush?.Dispose();
        _surface?.Dispose();
        _surface = null;
        _fade?.Dispose();
        _fade = null;
        _image?.Dispose();
        _image = null;
    }
}
