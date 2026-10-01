using Avalonia.Animation;
using Avalonia.Animation.Easings;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The motion vocabulary of the Colitu apps (fade and lift in, staggered
/// cards, soft pulses), on Avalonia animations. Transform animations go
/// through a TranslateTransform or ScaleTransform on the element, the form
/// Avalonia's animator understands.
/// </summary>
internal static class ColituMotion
{
    public static readonly Easing EaseOut = new QuinticEaseOut();

    /// <summary>Fades an element in while lifting it <paramref name="offset"/> pixels.</summary>
    public static void FadeIn(Visual element, double offset, double delayMs = 0, double durationMs = 560)
    {
        element.RenderTransform = new TranslateTransform(0, offset);
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = EaseOut,
            FillMode = FillMode.Both,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, offset) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) }
                }
            }
        };
        Run(animation, element);
    }

    /// <summary>Fades and lifts the children of a panel in one after another.</summary>
    public static void StaggerIn(Panel panel, double offset = 18)
    {
        var index = 0;
        foreach (var child in panel.Children)
        {
            FadeIn(child, offset, 80 * index++, 640);
        }
    }

    /// <summary>A short spring: up to <paramref name="peak"/> and back.</summary>
    public static void Pop(Visual element, double peak = 1.06, int repeat = 1)
    {
        element.RenderTransformOrigin = RelativePoint.Center;
        element.RenderTransform = new ScaleTransform(1, 1);
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(repeat > 1 ? 440 : 700),
            IterationCount = new IterationCount((ulong)Math.Max(1, repeat)),
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } },
                new KeyFrame { Cue = new Cue(0.32), Setters = { new Setter(ScaleTransform.ScaleXProperty, peak), new Setter(ScaleTransform.ScaleYProperty, peak) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } }
            }
        };
        Run(animation, element);
    }

    /// <summary>Endless back-and-forth opacity breathing; cancel the token to stop.</summary>
    public static void Breathe(Visual element, double from, double to, double periodMs, CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(periodMs),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, to) } }
            }
        };
        Run(animation, element, token);
    }

    /// <summary>Endless ripple: grows and fades out (the status dot halo).</summary>
    public static void Ripple(Visual element, CancellationToken token)
    {
        element.RenderTransformOrigin = RelativePoint.Center;
        element.RenderTransform = new ScaleTransform(1, 1);
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(1.8),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.OpacityProperty, 0.6), new Setter(ScaleTransform.ScaleXProperty, 0.8), new Setter(ScaleTransform.ScaleYProperty, 0.8) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(ScaleTransform.ScaleXProperty, 2.4), new Setter(ScaleTransform.ScaleYProperty, 2.4) }
                }
            }
        };
        Run(animation, element, token);
    }

    /// <summary>Motion is decoration: an animation that cannot run must never break the screen.</summary>
    private static async void Run(Animation animation, Visual element, CancellationToken token = default)
    {
        try
        {
            await animation.RunAsync(element, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.SaveLog("ColituMotion", ex);
            element.Opacity = 1;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
