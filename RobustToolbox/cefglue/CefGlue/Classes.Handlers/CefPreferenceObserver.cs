using Xilium.CefGlue.Interop;

namespace Xilium.CefGlue;

public abstract unsafe partial class CefPreferenceObserver
{
    internal void on_preference_changed(cef_preference_observer_t* self, cef_string_t* name)
    {
        CheckSelf(self);

        OnPreferenceChanged(cef_string_t.ToString(name));
    }

    /// <summary>
    /// Called when a preference has changed. The new value can be retrieved using
    /// CefPreferenceManager::GetPreference.
    /// </summary>
    protected virtual void OnPreferenceChanged(string name)
    {
    }
}
