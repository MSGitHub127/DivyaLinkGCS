// Services/OverlayService.cs
// Singleton that owns overlay panel visibility state.
//
// WHY THIS SERVICE EXISTS:
//   Setup.razor contains the "Tuning" and "RTK" trigger buttons.
//   The overlay drawers must render INSIDE the map container (right side of layout),
//   not inside Setup.razor's module grid (which would cover the setup panel).
//   Since these are different components, shared state is required.
//
// FLOW:
//   User clicks Tuning button in Setup.razor
//     → Setup.razor calls OverlayService.OpenTuning()
//       → OverlayService fires OnChanged
//         → MapOverlayHost.razor (inside map container) re-renders
//           → OverlayDrawer slides in over the MAP ONLY

namespace BlazorApp3.Services;

public sealed class OverlayService
{
    private bool _showTuning = false;
    private bool _showRtk    = false;

    public bool ShowTuning => _showTuning;
    public bool ShowRtk    => _showRtk;
    public bool AnyOpen    => _showTuning || _showRtk;

    /// <summary>
    /// Fired on every state change.
    /// Subscribers must InvokeAsync before StateHasChanged (background thread safety).
    /// </summary>
    public event Action? OnChanged;

    public void OpenTuning()
    {
        _showTuning = true;
        _showRtk    = false;   // mutual exclusion
        OnChanged?.Invoke();
    }

    public void OpenRtk()
    {
        _showRtk    = true;
        _showTuning = false;   // mutual exclusion
        OnChanged?.Invoke();
    }

    public void CloseAll()
    {
        _showTuning = false;
        _showRtk    = false;
        OnChanged?.Invoke();
    }

    public void CloseTuning()
    {
        if (!_showTuning) return;
        _showTuning = false;
        OnChanged?.Invoke();
    }

    public void CloseRtk()
    {
        if (!_showRtk) return;
        _showRtk = false;
        OnChanged?.Invoke();
    }
}