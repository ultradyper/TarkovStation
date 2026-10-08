using Xilium.CefGlue.Interop;

namespace Xilium.CefGlue;

public abstract unsafe partial class CefSettingObserver
{
    internal void on_setting_changed(cef_setting_observer_t* self, cef_string_t* requestingUrl, cef_string_t* topLevelUrl, CefContentSettingTypes contentType)
    {
        CheckSelf(self);

        var mRequestingUrl = cef_string_t.ToString(requestingUrl);
        var mTopLevelUrl = cef_string_t.ToString(topLevelUrl);

        OnSettingChanged(mRequestingUrl, mTopLevelUrl, contentType);
    }

    /// <summary>
    /// Called when a content or website setting has changed. The new value can be
    /// retrieved using CefRequestContext::GetContentSetting or
    /// CefRequestContext::GetWebsiteSetting.
    /// </summary>
    protected virtual void OnSettingChanged(
        string requestingUrl,
        string topLevelUrl,
        CefContentSettingTypes contentType)
    {
    }
}
