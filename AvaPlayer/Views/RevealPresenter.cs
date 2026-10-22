using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AvaPlayer.Views;

/// <summary>Keeps closing content visible until its native style transitions finish.</summary>
public sealed class RevealPresenter : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<RevealPresenter, bool>(nameof(IsOpen));
    public static readonly StyledProperty<TimeSpan> TransitionDurationProperty =
        AvaloniaProperty.Register<RevealPresenter, TimeSpan>(nameof(TransitionDuration), TimeSpan.FromMilliseconds(260));

    private CancellationTokenSource? _visibilityChange;
    private bool _attached;

    public RevealPresenter()
    {
        IsVisible = false;
        UpdateInteraction();
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public TimeSpan TransitionDuration { get => GetValue(TransitionDurationProperty); set => SetValue(TransitionDurationProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Classes.Set("open", IsOpen);
        IsVisible = IsOpen;
        UpdateInteraction();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _visibilityChange?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsOpenProperty) return;
        UpdateInteraction();
        if (_attached) _ = UpdateVisibilityAsync();
    }

    private void UpdateInteraction()
    {
        IsHitTestVisible = IsOpen;
        KeyboardNavigation.SetTabNavigation(this, IsOpen ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private async Task UpdateVisibilityAsync()
    {
        _visibilityChange?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _visibilityChange = cancellation;
        try
        {
            if (IsOpen)
            {
                if (!IsVisible)
                {
                    IsVisible = true;
                    // Finish the initial layout and render before starting the native transitions.
                    UpdateLayout();
                    if (TopLevel.GetTopLevel(this) is { } topLevel)
                    {
                        var frame = new TaskCompletionSource();
                        topLevel.RequestAnimationFrame(_ => frame.TrySetResult());
                        await frame.Task.WaitAsync(cancellation.Token);
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                Classes.Add("open");
            }
            else
            {
                Classes.Remove("open");
                await Task.Delay(TransitionDuration, cancellation.Token);
                IsVisible = false;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_visibilityChange, cancellation)) _visibilityChange = null;
        }
    }
}
